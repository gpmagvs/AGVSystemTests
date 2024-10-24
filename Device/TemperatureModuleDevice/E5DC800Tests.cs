using Microsoft.VisualStudio.TestTools.UnitTesting;
using EquipmentManagment.Device.TemperatureModuleDevice;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EquipmentManagment.Device.TemperatureModuleDevice.Tests
{
    [TestClass()]
    public class E5DC800Tests
    {
        [TestMethod()]
        public void ParseTemperatureTest()
        {
            E5DC800 module = new E5DC800(new TemperatureModuleAbstract.TemperatureModuleSetupOptions());
            double _temperature = module.ParseTemperature(new byte[] { 0x00, 0x00, 0x00, 0x1F });
            Assert.AreEqual(31, _temperature);
            double _temperature2 = module.ParseTemperature(new byte[] { 0x00, 0x01, 0x01, 0x1F });
            Assert.AreEqual(287, _temperature2);
        }
    }
}