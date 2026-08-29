using EquipmentManagment.Device.TemperatureModuleDevice;

namespace EquipmentManagment.Device.TemperatureModuleDevice.Tests
{
    [TestClass]
    public class E5DC800Tests
    {
        private static E5DC800 CreateModule() =>
            new E5DC800(new TemperatureModuleAbstract.TemperatureModuleSetupOptions());

        [TestMethod]
        public void ParseTemperature_KnownVectors()
        {
            E5DC800 module = CreateModule();
            Assert.AreEqual(31, module.ParseTemperature(new byte[] { 0x00, 0x00, 0x00, 0x1F }));
            Assert.AreEqual(287, module.ParseTemperature(new byte[] { 0x00, 0x01, 0x01, 0x1F }));
            Assert.AreEqual(0, module.ParseTemperature(new byte[] { 0x00, 0x00, 0x00, 0x00 }));
        }

        [TestMethod]
        public void ParseTemperature_DifferentInputs_ProduceDifferentResults()
        {
            E5DC800 module = CreateModule();
            double a = module.ParseTemperature(new byte[] { 0x00, 0x00, 0x00, 0x10 });
            double b = module.ParseTemperature(new byte[] { 0x00, 0x00, 0x00, 0x20 });
            Assert.AreNotEqual(a, b);
        }
    }
}
