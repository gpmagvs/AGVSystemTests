using AGVSystem.Service;
using AGVSystemCommonNet6.Equipment;
using EquipmentManagment.ChargeStation;
using static EquipmentManagment.Device.Options.clsEQIOLocation;

namespace AGVSystem.Service.Tests
{
    [TestClass]
    public class EquipmentStatusMapperTests
    {
        #region MainEQ

        [TestMethod]
        public void ResolveMainEqRunStatus_V1_AllFlagCombinations()
        {
            // 後寫入旗標優先：Down > Idle > Run
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V1, false, false, false));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.RUN,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V1, true, false, false));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.IDLE,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V1, false, true, false));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V1, false, false, true));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.IDLE,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V1, true, true, false));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V1, true, true, true));
        }

        [TestMethod]
        public void ResolveMainEqRunStatus_V2_DownFlagMeansRun()
        {
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.RUN,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V2, false, false, true));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN,
                EquipmentStatusMapper.ResolveMainEqRunStatus(STATUS_IO_DEFINED_VERSION.V2, true, true, false));
        }

        [TestMethod]
        public void ToMainEQStatus_V1_MapsStatusFromFlags()
        {
            MainEQStatus idle = EquipmentStatusMapper.ToMainEQStatus(
                "EQ_IDLE", 1, false, true, false, false, false, true,
                STATUS_IO_DEFINED_VERSION.V1, false, true, false);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.IDLE, idle.Status);
            Assert.IsTrue(idle.LoadRequest);

            MainEQStatus down = EquipmentStatusMapper.ToMainEQStatus(
                "EQ_DOWN", 2, true, false, true, true, false, false,
                STATUS_IO_DEFINED_VERSION.V1, false, false, true);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN, down.Status);
            Assert.IsTrue(down.UnloadRequest);
            Assert.IsTrue(down.CargoExist);
            Assert.IsTrue(down.Maintaining);
            Assert.IsFalse(down.Connected);
        }

        [TestMethod]
        public void ToMainEQStatus_V2_MapsScalarFields()
        {
            MainEQStatus status = EquipmentStatusMapper.ToMainEQStatus(
                "EQ1", 10, unloadRequest: true, loadRequest: false, cargoExist: true,
                maintaining: false, partsReplacing: true, connected: true,
                STATUS_IO_DEFINED_VERSION.V2, false, false, true);

            Assert.AreEqual("EQ1", status.Name);
            Assert.AreEqual(10, status.Tag);
            Assert.IsTrue(status.UnloadRequest);
            Assert.IsFalse(status.LoadRequest);
            Assert.IsTrue(status.CargoExist);
            Assert.IsTrue(status.PartsReplacing);
            Assert.IsTrue(status.Connected);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.RUN, status.Status);
        }

        #endregion

        #region Rack

        [TestMethod]
        public void ToRackStatus_ConnectedAndDisconnected()
        {
            RackStatus online = EquipmentStatusMapper.ToRackStatus("RackA", true);
            Assert.AreEqual("RackA", online.Name);
            Assert.IsTrue(online.Connected);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.RUN, online.Status);

            RackStatus offline = EquipmentStatusMapper.ToRackStatus("RackB", false);
            Assert.IsFalse(offline.Connected);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN, offline.Status);
        }

        #endregion

        #region ChargeStation

        [TestMethod]
        public void ResolveChargeStationRunStatus_AllBranches()
        {
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN,
                EquipmentStatusMapper.ResolveChargeStationRunStatus(false, false));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN,
                EquipmentStatusMapper.ResolveChargeStationRunStatus(false, true));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.Charging,
                EquipmentStatusMapper.ResolveChargeStationRunStatus(true, true));
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.IDLE,
                EquipmentStatusMapper.ResolveChargeStationRunStatus(true, false));
        }

        [TestMethod]
        public void FormatErrorCodes_NullEmptyAndMultiple()
        {
            Assert.AreEqual("", EquipmentStatusMapper.FormatErrorCodes(null));
            Assert.AreEqual("", EquipmentStatusMapper.FormatErrorCodes(new List<clsChargeStation.ERROR_CODE>()));
            Assert.AreEqual("Fans", EquipmentStatusMapper.FormatErrorCodes(new[] { clsChargeStation.ERROR_CODE.Fans }));
            Assert.AreEqual("Fans,Temp_OT_Warning",
                EquipmentStatusMapper.FormatErrorCodes(new[]
                {
                    clsChargeStation.ERROR_CODE.Fans,
                    clsChargeStation.ERROR_CODE.Temp_OT_Warning
                }));
        }

        [TestMethod]
        public void ToChargStationStatus_MapsAllScalarFields()
        {
            var data = new clsChargerData
            {
                ModelName = "GY7601",
                UID = "uid-1",
                TagNumber = 4,
                SecondaryTagNumber = 8,
                Connected = true,
                IOModuleConnected = true,
                IsBatteryFull = false,
                UseVehicleName = "AGV_01",
                CurrentChargeMode = clsChargeStation.CHARGE_MODE.CCM,
                Vin = 220,
                Vout = 28.5,
                Iout = 10.5,
                CC = 33,
                CV = 29.5,
                FV = 27.5,
                TC = 6,
                Temperature = 45,
                StationTemperature = 30,
                IsStationTemperatureOverThresHold = false,
                UpdateTime = new DateTime(2026, 8, 29, 10, 0, 0),
                ErrorCodes = new List<clsChargeStation.ERROR_CODE> { clsChargeStation.ERROR_CODE.BUSY }
            };
            data.SetAsUsing();
            data.Enabled = true;
            data.IsSimulation = true;

            DateTime now = new DateTime(2026, 1, 1);
            ChargStationStatus status = EquipmentStatusMapper.ToChargStationStatus("Charge_1", data, now);

            Assert.AreEqual("Charge_1", status.Name);
            Assert.AreEqual("GY7601", status.Description);
            Assert.AreEqual("uid-1", status.UID);
            Assert.AreEqual(4, status.Tag);
            Assert.AreEqual(8, status.SecondaryTag);
            Assert.IsTrue(status.Connected);
            Assert.IsTrue(status.Enabled);
            Assert.IsTrue(status.IOModuleConnected);
            Assert.IsTrue(status.IsUsing);
            Assert.IsFalse(status.IsBatteryFull);
            Assert.IsTrue(status.IsSimulation);
            Assert.AreEqual("AGV_01", status.UseVehicleName);
            Assert.AreEqual((int)clsChargeStation.CHARGE_MODE.CCM, status.ChargeMode);
            Assert.AreEqual(220, status.VoltageIn);
            Assert.AreEqual(28.5, status.VoltageOut);
            Assert.AreEqual(10.5, status.Current);
            Assert.AreEqual(33, status.CC);
            Assert.AreEqual(29.5, status.CV);
            Assert.AreEqual(27.5, status.FV);
            Assert.AreEqual(6, status.TC);
            Assert.AreEqual(45, status.ChargerTemperature);
            Assert.AreEqual(30, status.StationTemperature);
            Assert.AreEqual("BUSY", status.ErrorCodes);
            Assert.AreEqual(new DateTime(2026, 8, 29, 10, 0, 0), status.UpdateTime);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.Charging, status.Status);
        }

        [TestMethod]
        [DataRow(clsChargeStation.CHARGE_MODE.CVM)]
        [DataRow(clsChargeStation.CHARGE_MODE.FVM)]
        [DataRow(clsChargeStation.CHARGE_MODE.CCM)]
        public void ToChargStationStatus_ChargeModes_MappedAsInt(clsChargeStation.CHARGE_MODE mode)
        {
            var data = new clsChargerData { Connected = true, CurrentChargeMode = mode };
            ChargStationStatus status = EquipmentStatusMapper.ToChargStationStatus("CS", data, DateTime.Now);
            Assert.AreEqual((int)mode, status.ChargeMode);
        }

        [TestMethod]
        public void ToChargStationStatus_NullStringsAndDefaultUpdateTime_UsesFallback()
        {
            var data = new clsChargerData
            {
                ModelName = null!,
                UID = null!,
                UseVehicleName = null!,
                Connected = true,
                UpdateTime = default
            };
            DateTime now = new DateTime(2026, 5, 1, 12, 0, 0);
            ChargStationStatus status = EquipmentStatusMapper.ToChargStationStatus("CS", data, now);

            Assert.AreEqual("", status.Description);
            Assert.AreEqual("", status.UID);
            Assert.AreEqual("", status.UseVehicleName);
            Assert.AreEqual("", status.ErrorCodes);
            Assert.AreEqual(now, status.UpdateTime);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.IDLE, status.Status);
        }

        #endregion

        #region Orphan

        [TestMethod]
        public void FindOrphanNames_EmptyLive_ReturnsAllDbNames()
        {
            var orphans = EquipmentStatusMapper.FindOrphanNames(new[] { "A", "B" }, Array.Empty<string>());
            CollectionAssert.AreEquivalent(new[] { "A", "B" }, orphans);
        }

        [TestMethod]
        public void FindOrphanNames_FiltersOnlyMissing()
        {
            var orphans = EquipmentStatusMapper.FindOrphanNames(new[] { "A", "B", "C" }, new[] { "A", "C" });
            CollectionAssert.AreEqual(new[] { "B" }, orphans);
        }

        [TestMethod]
        public void FindOrphanNames_NullInputs_TreatedAsEmpty()
        {
            CollectionAssert.AreEqual(new List<string>(), EquipmentStatusMapper.FindOrphanNames(null!, null!));
            CollectionAssert.AreEqual(new List<string>(), EquipmentStatusMapper.FindOrphanNames(null!, new[] { "X" }));
        }

        [TestMethod]
        public void FindOrphanNames_NoOrphans_ReturnsEmpty()
        {
            var orphans = EquipmentStatusMapper.FindOrphanNames(new[] { "A", "B" }, new[] { "A", "B", "C" });
            Assert.AreEqual(0, orphans.Count);
        }

        [TestMethod]
        public void FindOrphanNames_EmptyDb_ReturnsEmpty()
        {
            Assert.AreEqual(0, EquipmentStatusMapper.FindOrphanNames(Array.Empty<string>(), new[] { "A" }).Count);
        }

        #endregion
    }
}
