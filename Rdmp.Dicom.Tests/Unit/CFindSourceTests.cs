using NUnit.Framework;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Dicom.Cache.Pipeline;
using Rdmp.Dicom.CommandExecution;
using ReusableLibraryCode.Checks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tests.Common;

namespace Rdmp.Dicom.Tests.Unit
{
    class CFindSourceTests : UnitTests
    {
        [Test]
        public void TestRunFindOn_PublicServer()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);

            var cmd = new ExecuteCommandCFind(
                new ConsoleInputManager(RepositoryLocator, new ThrowImmediatelyCheckNotifier()) { DisallowInput = true },
                "2013-01-01",
                "2014-01-01",
                "www.dicomserver.co.uk",
                104,
                "you",
                "me",
                dir.FullName);
            cmd.Execute();

            // file name is miday on 2010 1st January
            var f = Path.Combine(dir.FullName, @"out/Data/Cache/ALL/20130101120000.csv");
            FileAssert.Exists(f);
            
            var result = File.ReadAllLines(f);

            // should be at least 1 image in the public test server
            Assert.GreaterOrEqual(result.Length,2);
        }
    }
}
