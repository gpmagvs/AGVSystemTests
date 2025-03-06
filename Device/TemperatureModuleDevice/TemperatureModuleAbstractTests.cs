using Microsoft.VisualStudio.TestTools.UnitTesting;
using EquipmentManagment.Device.TemperatureModuleDevice;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RosSharp.RosBridgeClient.MessageTypes.Sensor;

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
        [TestMethod()]
        public void FoTECHModuleTest()
        {
            TemperatureModuleAbstract module = new FOTECH_NT_22_RS(new TemperatureModuleAbstract.TemperatureModuleSetupOptions
            {
                Protocol = TemperatureModuleAbstract.TemperatureModuleSetupOptions.COMMUNICATION_PROTOCOL.TCPIP,
                IP = "192.168.0.182",
                Port = 10001
            });
            double temperature = 0;
            bool connected = module.ConnectTest().GetAwaiter().GetResult();
            if (connected)
                temperature = module.GetTemperature().GetAwaiter().GetResult();
            else
                Assert.Fail("Connect Fail");
            int thres = 40;

            module.OutputOn().GetAwaiter().GetResult();
            Thread.Sleep(1000);
            module.SettingDeviceTresholdValue(thres).GetAwaiter().GetResult();
            Thread.Sleep(1000);
            int thresReadOut = module.GetDeviceThresholdValue().GetAwaiter().GetResult();
            module.socket.Dispose();

            Assert.IsTrue(connected);
            Assert.IsTrue(temperature > 0);
            Assert.AreEqual(thres, thresReadOut);
        }
    }
}