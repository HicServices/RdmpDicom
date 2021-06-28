using NUnit.Framework;
using Rdmp.Core.CommandLine.Interactive;
using Rdmp.Dicom.CommandExecution;
using ReusableLibraryCode.Checks;
using Tests.Common;

namespace Rdmp.Dicom.Tests.Integration
{
    class ExecuteCommandPacsFetchTest : DatabaseTests
    {
        [Test]
        public void TestLocal()
        {
            
            var cmd = new ExecuteCommandPacsFetch(new ConsoleInputManager(RepositoryLocator, new ThrowImmediatelyCheckNotifier()){DisallowInput= true},"2013-01-01","2014-01-01","www.dicomserver.co.uk",11112,"you","localhost",11112,"me",TestContext.CurrentContext.WorkDirectory,0);
            cmd.Execute();
        }
    }
}
