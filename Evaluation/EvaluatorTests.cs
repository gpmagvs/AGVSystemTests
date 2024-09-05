using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystem.Evaluation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AGVSystemCommonNet6.Configuration;
using EquipmentManagment.Manager;

namespace AGVSystem.Evaluation.Tests
{
    [TestClass()]
    public class EvaluatorTests
    {
        [ClassInitialize]
        public static void Init(TestContext context)
        {
            AGVSConfigulator.Init();
            StaEQPManagager.InitializeAsync(new EquipmentManagment.MainEquipment.clsEQManagementConfigs()
            {
                EQConfigPath = $"{AGVSConfigulator.SysConfigs.EQManagementConfigs.EquipmentManagementConfigFolder}//EQConfigs.json",
                WIPConfigPath = $"{AGVSConfigulator.SysConfigs.EQManagementConfigs.EquipmentManagementConfigFolder}//WIPConfigs.json",
                ChargeStationConfigPath = $"{AGVSConfigulator.SysConfigs.EQManagementConfigs.EquipmentManagementConfigFolder}//ChargStationConfigs.json",

            });
        }

        [TestMethod()]
        public void EvaluatorTest()
        {
            Assert.Fail();
        }

        [TestMethod()]
        public void StartTest()
        {
            EvaluateFactory.StartTest().GetAwaiter().GetResult();
        }
    }
}