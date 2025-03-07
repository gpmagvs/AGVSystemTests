using Microsoft.VisualStudio.TestTools.UnitTesting;
using EquipmentManagment.ChargeStation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AGVSystemCommonNet6;

namespace EquipmentManagment.ChargeStation.Tests
{
    [TestClass()]
    public class clsChargeStationTests
    {
        [TestMethod()]
        public void ReadChargerTest()
        {
            clsChargeStationGY7601Base charger = new clsChargeStationGY7601Base(new clsChargeStationOptions()
            {
                ConnOptions = new Connection.ConnectOptions
                {
                    IP = "192.168.0.181",
                    Port = 10002,
                    ConnMethod = Connection.CONN_METHODS.TCPIP,
                }
            });

            bool connected = charger.Connect().GetAwaiter().GetResult();
            if (!connected)
                Assert.Fail("Connect Fail");
            //(bool success, string message) = charger.SetFV(27.6).GetAwaiter().GetResult();
            //Console.WriteLine($"FV value write result={success},{message}");
            charger.ReadCharger().GetAwaiter().GetResult();
            charger.DisConnect();
            charger.Dispose();
            Console.WriteLine(charger.Datas.ToJson());
            Assert.IsTrue(charger.Datas.Vin > 200);

        }
    }
}