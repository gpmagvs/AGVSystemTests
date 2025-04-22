using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystemCommonNet6.Sys.ProgramUpdates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AGVSystemCommonNet6.Sys.ProgramUpdates.Tests
{
    [TestClass()]
    public class ProgramUpdateServiceTests
    {
        [TestMethod()]
        public void UnZipFileTest()
        {
            ProgramUpdateService programUpdateService = new ProgramUpdateService();
            programUpdateService.UnZipFile(null).GetAwaiter().GetResult();
            Assert.Fail();
        }

        [TestMethod()]
        public void CreateCopyFileRunBatFileTest()
        {
            string testDestine = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"TestFolder\\{DateTime.Now.Ticks}");
            Directory.CreateDirectory(testDestine);
            ProgramUpdateService programUpdateService = new ProgramUpdateService();
            string tempBatFileFullPath = programUpdateService.CreateCopyFileRunBatFile("C:/temp", destineFolderPath: testDestine);
            Assert.Fail();
        }

        [TestMethod()]
        public void HandleUpdateFileUploadedTest()
        {
            ProgramUpdateService programUpdateService = new ProgramUpdateService();
            programUpdateService.HandleUpdateFileUploaded(null).GetAwaiter().GetResult();

            Assert.Fail();
        }
    }
}