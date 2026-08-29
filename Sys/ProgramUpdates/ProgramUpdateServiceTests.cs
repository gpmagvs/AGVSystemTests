using AGVSystemCommonNet6.Sys.ProgramUpdates;

namespace AGVSystemCommonNet6.Sys.ProgramUpdates.Tests
{
    [TestClass]
    public class ProgramUpdateServiceTests
    {
        [TestMethod]
        public async Task UnZipFile_NullFile_CreatesEmptyTempFolder()
        {
            ProgramUpdateService service = new ProgramUpdateService();
            string folder = await service.UnZipFile(null);
            Assert.IsFalse(string.IsNullOrWhiteSpace(folder));
            Assert.IsTrue(Directory.Exists(folder));
            try { Directory.Delete(folder, true); } catch { }
        }

        [TestMethod]
        public void CreateCopyFileRunBatFile_CreatesBatWithRobocopyAndDelay()
        {
            string source = Path.Combine(Path.GetTempPath(), $"upd_src_{Guid.NewGuid():N}");
            string dest = Path.Combine(Path.GetTempPath(), $"upd_dst_{Guid.NewGuid():N}");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(dest);

            ProgramUpdateService service = new ProgramUpdateService();
            string batPath = service.CreateCopyFileRunBatFile(source, delayTime: 3, destineFolderPath: dest);

            Assert.IsTrue(File.Exists(batPath));
            Assert.IsTrue(batPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
            string content = File.ReadAllText(batPath);
            Assert.IsTrue(content.Contains("robocopy", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(content.Contains(source));
            Assert.IsTrue(content.Contains(dest));
            Assert.IsTrue(content.Contains("timeout /t 3"));

            try
            {
                File.Delete(batPath);
                Directory.Delete(source, true);
                Directory.Delete(dest, true);
            }
            catch { }
        }

        [TestMethod]
        public void CreateCopyFileRunBatFile_DefaultDestine_UsesBaseDirectory()
        {
            string source = Path.Combine(Path.GetTempPath(), $"upd_src2_{Guid.NewGuid():N}");
            Directory.CreateDirectory(source);
            ProgramUpdateService service = new ProgramUpdateService();
            string batPath = service.CreateCopyFileRunBatFile(source, delayTime: 1);
            string content = File.ReadAllText(batPath);
            Assert.IsTrue(content.Contains(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('/', '\\'))
                || content.Contains(AppDomain.CurrentDomain.BaseDirectory.Replace('\\', '/')));
            try
            {
                File.Delete(batPath);
                Directory.Delete(source, true);
            }
            catch { }
        }

        [TestMethod]
        public async Task HandleUpdateFileUploaded_NullFiles_ReturnsFailure()
        {
            ProgramUpdateService service = new ProgramUpdateService();
            ProgramUpdateResult result = await service.HandleUpdateFileUploaded(null!);
            Assert.IsFalse(result.success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.message));
        }

        [TestMethod]
        public async Task HandleUpdateFileUploaded_EmptyCollection_ReturnsFailure()
        {
            ProgramUpdateService service = new ProgramUpdateService();
            // FormFileCollection 空集合
            var empty = new Microsoft.AspNetCore.Http.FormFileCollection();
            ProgramUpdateResult result = await service.HandleUpdateFileUploaded(empty);
            Assert.IsFalse(result.success);
            Assert.IsTrue(result.message.Contains("1") || result.message.Contains("數量"));
        }
    }
}
