using Microsoft.VisualStudio.TestTools.UnitTesting;
using EquipmentManagment.MainEquipment.EQGroup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AGVSystemCommonNet6.Configuration;
using EquipmentManagment.Manager;

namespace EquipmentManagment.MainEquipment.EQGroup.Tests
{
    [TestClass()]
    public class EqGroupTests
    {
        [ClassInitialize]
        public static void Init(TestContext _textContext)
        {

            AGVSConfigulator.Init();
            StaEQPManagager.InitializeAsync(new EquipmentManagment.MainEquipment.clsEQManagementConfigs()
            {
                EQConfigPath = $"{AGVSConfigulator.SysConfigs.EQManagementConfigs.EquipmentManagementConfigFolder}//EQConfigs.json",
                WIPConfigPath = $"{AGVSConfigulator.SysConfigs.EQManagementConfigs.EquipmentManagementConfigFolder}//WIPConfigs.json",
                ChargeStationConfigPath = $"{AGVSConfigulator.SysConfigs.EQManagementConfigs.EquipmentManagementConfigFolder}//ChargStationConfigs.json",
                EQGroupConfigPath = $"{AGVSConfigulator.SysConfigs.EQManagementConfigs.EquipmentManagementConfigFolder}//EQGroupConfigs.json",

            });
            Console.WriteLine("EqGroupTests class init done.");
        }

        [TestMethod()]
        public void EqGroupTest()
        {

            StaEQPManagager.MainEQList.Select(eq => eq.EndPointOptions.TagID)
                                      .OrderBy(tag => tag)
                                      .ToList().ForEach(eqTag => Console.WriteLine(eqTag));

            EqGroup eqGroup = new EqGroup(new EqGroupConfiguration
            {
                EqGroupName = "TestGroup",
                LoadPortEqTags = new List<int> { 46, 148, 150 },
                UnloadPortEqTags = new List<int> { 70, 72, 236 }
            });

            Assert.AreEqual(1, eqGroup.LoadPorts.Count);
            Assert.AreEqual(2, eqGroup.UnloadPorts.Count);
        }
    }
}