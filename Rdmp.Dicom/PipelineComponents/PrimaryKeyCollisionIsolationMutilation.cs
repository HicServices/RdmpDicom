﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using FAnsi.Discovery;
using FAnsi.Discovery.QuerySyntax;
using FAnsi.Discovery.TypeTranslation;
using MapsDirectlyToDatabaseTable;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Curation.Data.DataLoad;
using Rdmp.Core.Curation.Data.EntityNaming;
using Rdmp.Core.DataLoad;
using Rdmp.Core.DataLoad.Engine.DatabaseManagement.EntityNaming;
using Rdmp.Core.DataLoad.Engine.DatabaseManagement.Operations;
using Rdmp.Core.DataLoad.Engine.Job;
using Rdmp.Core.DataLoad.Engine.Mutilators;
using Rdmp.Core.DataLoad.Triggers;
using Rdmp.Core.QueryBuilding;
using ReusableLibraryCode.Checks;
using ReusableLibraryCode.DataAccess;
using ReusableLibraryCode.Progress;

namespace Rdmp.Dicom.PipelineComponents
{
    public class PrimaryKeyCollisionIsolationMutilation:IPluginMutilateDataTables
    {
        [DemandsInitialization("All tables which participate in record isolation e.g. Study,Series, Image.  These tables must have valid JoinInfos configured and one must be marked TableInfo.IsPrimaryExtractionTable",Mandatory=true)]
        public TableInfo[] TablesToIsolate { get; set; }
        
        [DemandsInitialization("Database in which to put _Isolation tables.",Mandatory=true)]
        public ExternalDatabaseServer IsolationDatabase { get; set; }
        
        private List<JoinInfo> _joins;
        private DiscoveredDatabase _raw;
        private IQuerySyntaxHelper _syntaxHelper;
        private int _dataLoadInfoId;
        private QueryBuilder _qb;

        private TableInfo _primaryTable;
        private ColumnInfo _primaryTablePk;
        private string _fromSql;
        private INameDatabasesAndTablesDuringLoads _namer;
        private IDataLoadJob _job;

        public void Check(ICheckNotifier notifier)
        {
            //if there is only one or no tables that's fine (mandatory will check for null itself)
            if (TablesToIsolate == null)
                throw new Exception("No tables have been selected");
             
            //make sure there is only one primary key per table and that it's a string
            foreach (TableInfo t in TablesToIsolate)
            {
                if (t.ColumnInfos.Count(c => c.IsPrimaryKey) != 1)
                    throw new Exception("Table '" + t + "' did not have exactly 1 IsPrimaryKey column");

                var pk = t.ColumnInfos.Single(c => c.IsPrimaryKey);

                var type = t.GetQuerySyntaxHelper().TypeTranslater.GetCSharpTypeForSQLDBType(pk.Data_type);
                
                if (type != typeof(string))
                    notifier.OnCheckPerformed(new CheckEventArgs("Expected primary key column " + pk + " to be a string data type but it was '" + pk.Data_type +"'" ,CheckResult.Fail));
            }

            //if there are multiple tables then we must know how to join them
            if (TablesToIsolate.Length >1 && TablesToIsolate.Count(t => t.IsPrimaryExtractionTable) != 1)
            {
                notifier.OnCheckPerformed(
                    new CheckEventArgs(
                        "There are " + TablesToIsolate.Length +
                        " tables to operate on but none are marked IsPrimaryExtractionTable.  This should be set on the top level table e.g. Study",
                        CheckResult.Fail));
            }

            try
            {
                //if there are multiple tables we need to know how to join them on a 1 column to 1 basis
                BuildJoinOrder(true);
            }
            catch (Exception e)
            {
                notifier.OnCheckPerformed(new CheckEventArgs("Failed to build join order", CheckResult.Fail, e));
                return;
            }

            //This is where we put the duplicate records
            var db = IsolationDatabase.Discover(DataAccessContext.DataLoad);

            if(!db.Exists())
                throw new Exception("IsolationDatabase did not exist");

            //Make sure the isolation tables exist and the schema matches RAW
            foreach (var tableInfo in TablesToIsolate)
            {
                var table = db.ExpectTable(GetIsolationTableName(tableInfo));

                if (!table.Exists())
                {
                    bool fix = notifier.OnCheckPerformed(
                        new CheckEventArgs("Isolation table '" + table.GetFullyQualifiedName() + "' did not exist",
                            CheckResult.Fail, null, "Create isolation table?"));

                    if (fix)
                        CreateIsolationTable(table, tableInfo);
                    else
                        throw new Exception("User rejected change");
                }
                else
                    ValidateIsolationTableSchema(table,tableInfo,notifier);
            }
        }

        private string GetIsolationTableName(TableInfo tableInfo)
        {
            return tableInfo.GetRuntimeName(LoadBubble.Live) + "_Isolation";
        }

        private void ValidateIsolationTableSchema(DiscoveredTable toValidate, TableInfo tableInfo, ICheckNotifier notifier)
        {
            var expected = tableInfo.GetColumnsAtStage(LoadStage.AdjustRaw).Select(c=>c.GetRuntimeName(LoadStage.AdjustRaw)).Union(new []{SpecialFieldNames.DataLoadRunID}).ToArray();
            var found = toValidate.DiscoverColumns().Select(c=>c.GetRuntimeName()).ToArray();

            foreach (var missingFromIsolation in expected.Except(found, StringComparer.CurrentCultureIgnoreCase))
                notifier.OnCheckPerformed(
                    new CheckEventArgs(
                        "Isolation table '" + toValidate + "' did not contain expected column'" + missingFromIsolation +
                        "'", CheckResult.Fail));

            foreach (var unexpectedInIsolation in found.Except(expected, StringComparer.CurrentCultureIgnoreCase))
                notifier.OnCheckPerformed(
                    new CheckEventArgs(
                        "Isolation table '" + toValidate + "' contained an unexpected column'" + unexpectedInIsolation +
                        "'", CheckResult.Fail));
        }

        private void CreateIsolationTable(DiscoveredTable toCreate, TableInfo tableInfo)
        {
            var from = tableInfo.Discover(DataAccessContext.DataLoad);

            //create a RAW table schema called TableName_Isolation
            var cloner = new TableInfoCloneOperation(null,null,LoadBubble.Live);
            cloner.CloneTable(from.Database, toCreate.Database, from, toCreate.GetRuntimeName(), true, true, true, tableInfo.PreLoadDiscardedColumns);
            
            if(!toCreate.Exists())
                throw new Exception(string.Format("Table '{0}' did not exist after issuing create command",toCreate));

            //Add the data load run id
            toCreate.AddColumn(SpecialFieldNames.DataLoadRunID,new DatabaseTypeRequest(typeof(int)),false,10);
        }

        private void BuildJoinOrder(bool isChecks)
        {
            _qb = new QueryBuilder(null, null);

            var memory = new MemoryRepository();

            foreach (TableInfo t in TablesToIsolate)
                _qb.AddColumn(new ColumnInfoToIColumn(memory,t.ColumnInfos.First()));

            _primaryTable = TablesToIsolate.Length == 1 ? TablesToIsolate[0] : TablesToIsolate.Single(t => t.IsPrimaryExtractionTable);
            _primaryTablePk = _primaryTable.ColumnInfos.Single(c => c.IsPrimaryKey);

            _qb.PrimaryExtractionTable = _primaryTable;

            _qb.RegenerateSQL();
            
            _joins = _qb.JoinsUsedInQuery ?? new List<JoinInfo>();

            _fromSql = SqlQueryBuilderHelper.GetFROMSQL(_qb);

            if(!isChecks)
                foreach (TableInfo tableInfo in TablesToIsolate)
                    _fromSql = _fromSql.Replace(tableInfo.GetFullyQualifiedName(), GetRAWTableNameFullyQualified(tableInfo));

            if (_joins.Any(j=>j.GetSupplementalJoins().Any()))
                throw new Exception("Supplemental (2 column) joins are not supported when resolving multi table primary key collisions");

            //order the tables in order of dependency
            List<TableInfo> tables = new List<TableInfo>();

            TableInfo next = _primaryTable;

            int overflow = 10;
            while (next != null)
            {
                tables.Add(next);
                var jnext = _joins.SingleOrDefault(j => j.PrimaryKey.TableInfo.Equals(next));
                if (jnext == null)
                    break;

                next = jnext.ForeignKey.TableInfo;
                
                if(overflow-- ==0)
                    throw new Exception("Joins resulted in a loop overflow");
            }

            TablesToIsolate = tables.ToArray();
        }

        private string GetRAWTableNameFullyQualified(TableInfo tableInfo)
        {
            return _syntaxHelper.EnsureFullyQualified(
                _namer.GetDatabaseName(tableInfo.GetDatabaseRuntimeName(), LoadBubble.Raw), null,
                _namer.GetName(tableInfo.GetRuntimeName(), LoadBubble.Raw));
        }

        public void LoadCompletedSoDispose(ExitCodeType exitCode, IDataLoadEventListener postLoadEventsListener)
        {
        }

        public void Initialize(DiscoveredDatabase dbInfo, LoadStage loadStage)
        {
            _raw = dbInfo;
            _syntaxHelper = _raw.Server.GetQuerySyntaxHelper();

            if(loadStage != LoadStage.AdjustRaw)
                throw new Exception("This component should only run in AdjustRaw");
        }

        public ExitCodeType Mutilate(IDataLoadJob job)
        {
            _dataLoadInfoId = job.JobID;
            _namer = job.Configuration.DatabaseNamer;
            _job = job;

            BuildJoinOrder(false);
            
            foreach (TableInfo tableInfo in TablesToIsolate)
            {
                var pkCol = tableInfo.ColumnInfos.Single(c => c.IsPrimaryKey);

                foreach (string pkValue in DetectCollisions(pkCol, tableInfo))
                {
                    _job.OnNotify(this,new NotifyEventArgs(ProgressEventType.Information, "Found duplication in column '" + pkCol +"', duplicate value was '" + pkValue +"'"));
                    MigrateRecords(pkCol, pkValue);
                }
            }

            return ExitCodeType.Success;
        }

        private void MigrateRecords(ColumnInfo deleteOn,string deleteValue)
        {
            var deleteOnColumnName = GetRAWColumnNameFullyQualified(deleteOn);

            using (var con = _raw.Server.GetConnection())
            {
                con.Open();

                //if we are deleting on a child table we need to look up the primary table primary key (e.g. StudyInstanceUID) we should then migrate that data instead (for all tables)
                if (!deleteOn.Equals(_primaryTablePk))
                {
                    var oldValue = deleteValue;

                    deleteValue = GetPrimaryKeyValueFor(deleteOn, deleteValue, con);
                    deleteOnColumnName = GetRAWColumnNameFullyQualified(_primaryTablePk);

                    if(deleteValue == null)
                        throw new Exception("Primary key value not found for " + oldValue);

                    _job.OnNotify(this, new NotifyEventArgs(ProgressEventType.Information, "Corresponding primary key is '" + deleteValue + "' ('" + deleteOnColumnName + "')"));
                }

                //pull all records that we must isolate in all joined tables
                Dictionary<TableInfo,DataTable> toPush = new Dictionary<TableInfo, DataTable>();

                foreach (TableInfo tableInfo in TablesToIsolate)
                    toPush.Add(tableInfo, PullTable(tableInfo,con, deleteOnColumnName, deleteValue));

                //push the results to isolation
                foreach (KeyValuePair<TableInfo, DataTable> kvp in toPush)
                {
                    var toDatabase = IsolationDatabase.Discover(DataAccessContext.DataLoad);
                    var toTable = toDatabase.ExpectTable(GetIsolationTableName(kvp.Key));

                    using (var bulkInsert = toTable.BeginBulkInsert())
                        bulkInsert.Upload(kvp.Value);
                }

                foreach (TableInfo t in TablesToIsolate.Reverse())
                    DeleteRows(t, deleteOnColumnName, deleteValue, con);
            }
        }

        /// <summary>
        /// Returns the fully qualified RAW name of the column factoring in namer e.g. [ab213_ImagingRAW]..[StudyTable].[MyCol]
        /// </summary>
        /// <param name="col"></param>
        /// <returns></returns>
        private string GetRAWColumnNameFullyQualified(ColumnInfo col)
        {
            return _syntaxHelper.EnsureFullyQualified(_raw.GetRuntimeName(), null, col.TableInfo.GetRuntimeName(LoadBubble.Raw, _namer), col.GetRuntimeName(LoadStage.AdjustRaw));
        }

        private string GetPrimaryKeyValueFor(ColumnInfo deleteOn, string deleteValue, DbConnection con)
        {
            var deleteOnColumnName = GetRAWColumnNameFullyQualified(deleteOn);
            var pkColumnName = GetRAWColumnNameFullyQualified(_primaryTablePk);

            //fetch all the data
            string sqlSelect = string.Format("Select distinct {0} {1} WHERE {2} = @val", pkColumnName, _fromSql, deleteOnColumnName);
            var cmdSelect = _raw.Server.GetCommand(sqlSelect, con);
            var p = cmdSelect.CreateParameter();
            p.ParameterName = "@val";
            p.Value = deleteValue;
            cmdSelect.Parameters.Add(p);

            return  cmdSelect.ExecuteScalar() as string;
        }

        private DataTable PullTable(TableInfo tableInfo, DbConnection con, string deleteOnColumnName, string deleteValue)
        {
            DataTable dt = new DataTable();
            var pk = tableInfo.ColumnInfos.Single(c => c.IsPrimaryKey);
            var pkColumnName = GetRAWColumnNameFullyQualified(pk);

            string deleteFromTableName = GetRAWTableNameFullyQualified(tableInfo);
            
            //fetch all the data (LEFT/RIGHT joins can introduce null records so add not null to WHERE for the table being migrated to avoid full null rows)
            string sqlSelect = string.Format("Select distinct {0}.* {1} WHERE {2} = @val AND {3} is not null", deleteFromTableName, _fromSql, deleteOnColumnName, pkColumnName);
            var cmdSelect = _raw.Server.GetCommand(sqlSelect, con);
            var p = cmdSelect.CreateParameter();
            p.ParameterName = "@val";
            p.Value = deleteValue;
            cmdSelect.Parameters.Add(p);

            var da = _raw.Server.GetDataAdapter(cmdSelect);
            da.Fill(dt);

            dt.Columns.Add(SpecialFieldNames.DataLoadRunID, typeof(int));
            
            foreach (DataRow row in dt.Rows)
                row[SpecialFieldNames.DataLoadRunID] = _dataLoadInfoId;

            return dt;
        }

        private void DeleteRows(TableInfo toDelete, string deleteOnColumnName, string deleteValue, DbConnection con)
        {
            //now delete all records
            string sqlDelete = string.Format("DELETE {0} {1} WHERE {2} = @val", toDelete.GetRuntimeName(LoadBubble.Raw,_namer), _fromSql, deleteOnColumnName);

            var cmdDelete = _raw.Server.GetCommand(sqlDelete, con);
            var p2 = cmdDelete.CreateParameter();
            p2.ParameterName = "@val";
            p2.Value = deleteValue;
            cmdDelete.Parameters.Add(p2);

            //then delete it
            cmdDelete.ExecuteNonQuery();
        }


        private IEnumerable<string> DetectCollisions(ColumnInfo pkCol,TableInfo tableInfo)
        {
            var pkColName = pkCol.GetRuntimeName(LoadStage.AdjustRaw);

            var tableNameFullyQualified = GetRAWTableNameFullyQualified(tableInfo);

            string primaryKeysColliding = string.Format(
                "SELECT {0} FROM {1} GROUP BY {0} HAVING count(*)>1",
                _syntaxHelper.EnsureWrapped(pkColName),
                tableNameFullyQualified
                );

            using (var con = _raw.Server.GetConnection())
            {
                con.Open();
                var cmd = _raw.Server.GetCommand(primaryKeysColliding, con);
                var r = cmd.ExecuteReader();

                while (r.Read())
                    yield return (string) r[pkColName];
            }
        }
    }
}