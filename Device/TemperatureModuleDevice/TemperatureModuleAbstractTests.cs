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
    public class TemperatureModuleAbstractTests
    {
        [TestMethod()]
        public void ConnectTestTest()
        {
            TemperatureModuleAbstract module = new E5DC800(new TemperatureModuleAbstract.TemperatureModuleSetupOptions
            {
                Protocol = TemperatureModuleAbstract.TemperatureModuleSetupOptions.COMMUNICATION_PROTOCOL.SERIAL_PORT,
                COM = "COM30",
                BaudRate = 9600
            });
            bool connected = module.ConnectTest().GetAwaiter().GetResult();
            module.serialPort.Dispose();
            Assert.IsTrue(connected);
        }
    }
}