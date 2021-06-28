using System;
using System.Collections.Generic;
using Dicom;
using Dicom.Network;
using MapsDirectlyToDatabaseTable;
using ReusableLibraryCode.Checks;
using ReusableLibraryCode.DataAccess;
using ReusableLibraryCode.Progress;
using Rdmp.Dicom.Cache.Pipeline.Dicom;
using Rdmp.Dicom.PACS;
using Rdmp.Core.Curation.Data;
using Rdmp.Core.Caching.Pipeline.Sources;
using Rdmp.Core.QueryBuilding;

namespace Rdmp.Dicom.Cache.Pipeline
{
    /// <summary>
    /// Abstract cache source for querying PACS servers
    /// </summary>
    public abstract class SMICacheSource : CacheSource<SMIDataChunk>
    {
        [DemandsInitialization("Remote Application Entity of the PACS server", mandatory: true)]
        public string RemoteAETitle { get; set; }

        [DemandsInitialization("Where the remote PACS server is", mandatory: true)]
        public Uri RemoteAEUri { get; set; }

        [DemandsInitialization("The port to send the request to on the remote PACS server", mandatory: true)]
        public int RemoteAEPort { get; set; }

        [DemandsInitialization("Local Application Entity title of your computer/software", mandatory: true)]
        public string LocalAETitle { get; set; }

        [DemandsInitialization("Local AE Uri of your computer", mandatory: true)]
        public Uri LocalAEUri { get; set; }

        [DemandsInitialization("The type of imaging to be cached, using the relevant acronym from the DICOM standard. e.g. CT,MR", mandatory: true)]
        public string Modality { get; set; }

        [DemandsInitialization("The port to listen on for responses", defaultValue: 2104, mandatory: true)]
        public int LocalAEPort { get; set; }

        [DemandsInitialization("Cooldown (in seconds) after a successful request", defaultValue: 60, mandatory: true)]
        public int RequestCooldownInSeconds { get; set; }

        [DemandsInitialization("Cooldown (in seconds) after a successful transfer", defaultValue: 120, mandatory: true)]
        public int TransferCooldownInSeconds { get; set; }

        [DemandsInitialization("Polling (in seconds) for successful transfer", defaultValue: 1, mandatory: true)]
        public int TransferPollingInSeconds { get; set; }

        [DemandsInitialization("Timeout period (in seconds) for unsuccessful transfer", defaultValue: 120, mandatory: true)]
        public int TransferTimeOutInSeconds { get; set; }

        [DemandsInitialization("A column containing a whitelist of patient identifiers which are permitted to be downloaded")]
        public ColumnInfo PatientIdWhitelistColumnInfo { get; set; }

        [DemandsInitialization("Ignore whitelist of patient identifiers", defaultValue: false, mandatory: true)]
        public bool IgnoreWhiteList { get; set; }


        protected HashSet<string> Whitelist;


        protected DicomCMoveRequest CreateCMoveByStudyUid(string destination, string studyUid, IDataLoadEventListener listener)
        {
            var request = new DicomCMoveRequest(destination, studyUid);
            listener.OnNotify(this, new NotifyEventArgs(ProgressEventType.Information, "DicomRetriever.CreateCMoveByStudyUid created request for: " + studyUid));
            // no more dicomtags have to be set
            return request;
        }
        public override void Dispose(IDataLoadEventListener listener, Exception pipelineFailureExceptionIfAny)
        {
        }

        public override void Abort(IDataLoadEventListener listener)
        {
        }


        public override SMIDataChunk TryGetPreview()
        {
            return null;
        }

        #region Check
        public override void Check(ICheckNotifier notifier)
        {
            // ping configured remote PACS with a C-ECHO request
            //use a new requestSender
            var echoRequestSender = new DicomRequestSender(GetConfiguration(), new FromCheckNotifierToDataLoadEventListener(notifier));
            echoRequestSender.OnRequestException += ex =>
            {
                notifier.OnCheckPerformed(new CheckEventArgs("Error sending ECHO", CheckResult.Fail, ex));
            };
            echoRequestSender.OnRequestTimeout += () =>
            {
                notifier.OnCheckPerformed(new CheckEventArgs("Failed to get response from server after timeout", CheckResult.Fail));
            };
            echoRequestSender.OnRequestSucess += () =>
            {
                notifier.OnCheckPerformed(new CheckEventArgs("Successfully received C-ECHO response from remote PACS", CheckResult.Success));
            };

            try
            {
                echoRequestSender.Check();
            }
            catch (Exception e)
            {
                notifier.OnCheckPerformed(new CheckEventArgs("Error when sending C-ECHO to remote PACS", CheckResult.Fail, e));
            }
        }
        #endregion

        #region CreateStudyRequestByDateRangeForModality
        protected DicomCFindRequest CreateStudyRequestByDateRangeForModality(DateTime dateFrom, DateTime dateTo, string modality, DicomPriority priority = DicomPriority.Low)
        {

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study, priority);

            // always add the encoding - with agnostic encoding
            request.Dataset.AddOrUpdate(new DicomTag(0x8, 0x5), "ISO_IR 100");

            // add the dicom tags with empty values that should be included in the result of the QR Server
            request.Dataset.AddOrUpdate(DicomTag.PatientID, "");
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, "");
            request.Dataset.AddOrUpdate(DicomTag.StudyDescription, "");

            string modalityToQuery = modality.Equals("all", StringComparison.OrdinalIgnoreCase) ? "" : modality;
            // add the dicom tags that contain the filter criteria
            request.Dataset.AddOrUpdate(DicomTag.ModalitiesInStudy, modalityToQuery);
            var studyDateRange = new DicomDateRange(dateFrom, dateTo);
            request.Dataset.AddOrUpdate(DicomTag.StudyDate, studyDateRange);

            // Problem: time and date are orthogonal in DICOM, so '9:00 1/1/20 to 10:00 31/1/20' can
            // skip data from e.g. 15:00 2/2/20 because it isn't '9:00-10:00'.
            // To avoid this, ignore time values unless we're doing less than a whole day:
            if ((dateTo - dateFrom).Days == 0)
                request.Dataset.AddOrUpdate(DicomTag.StudyTime, studyDateRange);

            return request;
        }
        #endregion

        #region CreateSeriesRequestByStudyUID
        protected DicomCFindRequest CreateSeriesRequestByStudyUid(string studyInstanceUid, DicomPriority priority = DicomPriority.Low)
        {
            //create your own request that contains exactly those DicomTags that
            // you realy need pro process your data and not to cause unneccessary traffic and IO load:
            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series, priority);

            request.Dataset.AddOrUpdate(new DicomTag(0x8, 0x5), "ISO_IR 100");

            // add the dicom tags with empty values that should be included in the result
            request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, "");
            request.Dataset.AddOrUpdate(DicomTag.SeriesDescription, "");
            request.Dataset.AddOrUpdate(DicomTag.Modality, "");
            request.Dataset.AddOrUpdate(DicomTag.NumberOfSeriesRelatedInstances, "");

            // add the dicom tags that contain the filter criteria
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyInstanceUid);

            return request;
        }
        #endregion

        #region CreateSopRequestBySeriesUID
        protected DicomCFindRequest CreateSopRequestBySeriesUid(string seriesInstanceUid, DicomPriority priority = DicomPriority.Low)
        {
            //create your own request that contains exactly those DicomTags that
            // you realy need pro process your data and not to cause unneccessary traffic and IO load:
            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image, priority);

            request.Dataset.AddOrUpdate(new DicomTag(0x8, 0x5), "ISO_IR 100");

            // add the dicom tags with empty values that should be included in the result
            request.Dataset.AddOrUpdate(DicomTag.PatientID, "");
            request.Dataset.AddOrUpdate(DicomTag.SOPInstanceUID, "");
            request.Dataset.AddOrUpdate(DicomTag.Modality, "");

            // add the dicom tags that contain the filter criteria
            request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesInstanceUid);

            return request;
        }
        #endregion


        #region GetConfiguration
        protected DicomConfiguration GetConfiguration()
        {
            return new DicomConfiguration
            {
                LocalAetTitle = LocalAETitle,
                LocalAetUri = DicomConfiguration.MakeUriUsePort(LocalAEUri, LocalAEPort),
                RemoteAetTitle = RemoteAETitle,
                RemoteAetUri = DicomConfiguration.MakeUriUsePort(RemoteAEUri, RemoteAEPort),
                RequestCooldownInMilliseconds = 1000 * RequestCooldownInSeconds,
                TransferCooldownInMilliseconds = 1000 * TransferCooldownInSeconds,
                TransferPollingInMilliseconds = 1000 * TransferPollingInSeconds,
                TransferTimeOutInMilliseconds = 1000 * TransferTimeOutInSeconds
            };
        }
        #endregion


        #region GetWhitelist

        /// <summary>
        /// Populates <see cref="Whitelist"/>
        /// </summary>
        /// <param name="listener"></param>
        protected void GetWhitelist(IDataLoadEventListener listener)
        {
            Whitelist = new HashSet<string>();

            var db = DataAccessPortal.GetInstance().ExpectDatabase(PatientIdWhitelistColumnInfo.TableInfo, DataAccessContext.DataLoad);
            var server = db.Server;

            var qb = new QueryBuilder("distinct", null);
            qb.AddColumn(new ColumnInfoToIColumn(new MemoryRepository(), PatientIdWhitelistColumnInfo));

            var sql = qb.SQL;

            listener.OnNotify(this, new NotifyEventArgs(ProgressEventType.Information, "Downloading Whitelist with SQL:" + sql));

            using (var con = server.GetConnection())
            {
                con.Open();
                var r = server.GetCommand(sql, con).ExecuteReader();

                while (r.Read())
                {
                    var o = r[PatientIdWhitelistColumnInfo.GetRuntimeName()];
                    if (o == null || o == DBNull.Value)
                        continue;

                    Whitelist.Add(o.ToString());
                }
            }

            listener.OnNotify(this, new NotifyEventArgs(Whitelist.Count == 0 ? ProgressEventType.Error : ProgressEventType.Information, "Whitelist contained " + Whitelist.Count + " identifiers"));
        }
        #endregion

        #region Filter
        protected bool Filter(HashSet<string> whitelistIfAny, DicomCFindResponse response)
        {
            var dataset = response.Dataset;

            //ignore responses where there are no dataset record being returned
            if (dataset == null)
                return false;

            //if there is a whitelist
            if (whitelistIfAny == null) return true;
            //get the response dataset patientId
            var patientId = dataset.GetSingleValue<string>(DicomTag.PatientID);

            //if the patientId is empty or not on our whitelist
            return patientId != null && whitelistIfAny.Contains(patientId.Trim());

            //No WhiteList just add
        }
        #endregion



    }
}