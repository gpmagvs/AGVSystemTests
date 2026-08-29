using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Equipment;
using AGVSystemCommonNet6.MAP;
using AGVSystemCommonNet6.MRMP.Options;
using AGVSystemCommonNet6.Utilis.Snap;
using EquipmentManagment.ChargeStation;
using static AGVSystemCommonNet6.MAP.MapPoint;

namespace AGVSystemCommonNet6.CommonMethods.Tests
{
    [TestClass]
    public class MapPointCapabilityTests
    {
        private static MapPoint Point(STATION_TYPE type) => new MapPoint { StationType = type };

        [TestMethod]
        public void IsChargeAble_OnlyChargeRelatedTypes()
        {
            Assert.IsTrue(Point(STATION_TYPE.Charge).IsChargeAble());
            Assert.IsTrue(Point(STATION_TYPE.Charge_Buffer).IsChargeAble());
            Assert.IsTrue(Point(STATION_TYPE.Charge_STK).IsChargeAble());
            Assert.IsFalse(Point(STATION_TYPE.EQ).IsChargeAble());
            Assert.IsFalse(Point(STATION_TYPE.Normal).IsChargeAble());
            Assert.IsFalse(Point(STATION_TYPE.Buffer).IsChargeAble());
            Assert.IsFalse(Point(STATION_TYPE.STK).IsChargeAble());
        }

        [TestMethod]
        public void IsLoadAble_ExpectedTypes()
        {
            Assert.IsTrue(Point(STATION_TYPE.EQ).IsLoadAble());
            Assert.IsTrue(Point(STATION_TYPE.EQ_LD).IsLoadAble());
            Assert.IsTrue(Point(STATION_TYPE.STK).IsLoadAble());
            Assert.IsTrue(Point(STATION_TYPE.STK_LD).IsLoadAble());
            Assert.IsTrue(Point(STATION_TYPE.Charge_STK).IsLoadAble());
            Assert.IsFalse(Point(STATION_TYPE.EQ_ULD).IsLoadAble());
            Assert.IsFalse(Point(STATION_TYPE.Charge).IsLoadAble());
            Assert.IsFalse(Point(STATION_TYPE.Normal).IsLoadAble());
        }

        [TestMethod]
        public void IsUnloadAble_ExpectedTypes()
        {
            Assert.IsTrue(Point(STATION_TYPE.EQ).IsUnloadAble());
            Assert.IsTrue(Point(STATION_TYPE.EQ_ULD).IsUnloadAble());
            Assert.IsTrue(Point(STATION_TYPE.STK).IsUnloadAble());
            Assert.IsTrue(Point(STATION_TYPE.STK_ULD).IsUnloadAble());
            Assert.IsTrue(Point(STATION_TYPE.Charge_STK).IsUnloadAble());
            Assert.IsFalse(Point(STATION_TYPE.EQ_LD).IsUnloadAble());
            Assert.IsFalse(Point(STATION_TYPE.Buffer).IsUnloadAble());
            Assert.IsFalse(Point(STATION_TYPE.Elevator).IsUnloadAble());
        }

        [TestMethod]
        public void Charge_STK_IsLoadUnloadAndChargeAble()
        {
            MapPoint point = Point(STATION_TYPE.Charge_STK);
            Assert.IsTrue(point.IsChargeAble());
            Assert.IsTrue(point.IsLoadAble());
            Assert.IsTrue(point.IsUnloadAble());
        }
    }

    [TestClass]
    public class MrmpMqttOptionsTests
    {
        [TestMethod]
        public void IsAgvEnabled_EmptyOrNullList_EnablesAll()
        {
            Assert.IsTrue(new MrmpMqttOptions { EnabledAgvNames = new List<string>() }.IsAgvEnabled("Any"));
            Assert.IsTrue(new MrmpMqttOptions { EnabledAgvNames = null! }.IsAgvEnabled("Any"));
        }

        [TestMethod]
        public void IsAgvEnabled_Whitelist_OnlyListed()
        {
            var options = new MrmpMqttOptions { EnabledAgvNames = new List<string> { "AGV1", "AGV3" } };
            Assert.IsTrue(options.IsAgvEnabled("AGV1"));
            Assert.IsTrue(options.IsAgvEnabled("AGV3"));
            Assert.IsFalse(options.IsAgvEnabled("AGV2"));
        }

        [TestMethod]
        public void BuildClientId_WithTimestamp_ContainsPrefixAndDigits()
        {
            var options = new MrmpMqttOptions { ClientIdSuffixMode = "WithTimestamp" };
            string clientId = options.BuildClientId("R1");
            Assert.IsTrue(clientId.StartsWith("robot_R1_"));
            Assert.IsTrue(clientId.Length > "robot_R1_".Length);
            string suffix = clientId["robot_R1_".Length..];
            Assert.IsTrue(long.TryParse(suffix, out _));
        }

        [TestMethod]
        public void BuildClientId_None_ExactPrefix()
        {
            var options = new MrmpMqttOptions { ClientIdSuffixMode = "None" };
            Assert.AreEqual("robot_R1", options.BuildClientId("R1"));
            Assert.AreEqual("robot_ABC", options.BuildClientId("ABC"));
        }

        [TestMethod]
        public void BuildClientId_CaseInsensitiveSuffixMode()
        {
            var options = new MrmpMqttOptions { ClientIdSuffixMode = "withtimestamp" };
            Assert.IsTrue(options.BuildClientId("R2").StartsWith("robot_R2_"));
        }
    }

    [TestClass]
    public class ObjectComparerBasicTests
    {
        private class Sample
        {
            public string Name { get; set; } = "";
            public int Age { get; set; }
            public List<int> Scores { get; set; } = new();
        }

        [TestMethod]
        public void CompareObjects_Identical_NoDiff()
        {
            var a = new Sample { Name = "A", Age = 1 };
            var b = new Sample { Name = "A", Age = 1 };
            Assert.AreEqual(0, ObjectComparer.CompareObjects(a, b).Count);
        }

        [TestMethod]
        public void CompareObjects_PropertyDiff_ReportsPath()
        {
            var a = new Sample { Name = "A", Age = 1 };
            var b = new Sample { Name = "B", Age = 1 };
            var diffs = ObjectComparer.CompareObjects(a, b);
            Assert.AreEqual(1, diffs.Count);
            Assert.AreEqual("Name", diffs[0].Path);
            Assert.AreEqual("A", diffs[0].OriginalValue);
            Assert.AreEqual("B", diffs[0].CurrentValue);
        }

        [TestMethod]
        public void CompareObjects_MultiplePropertyDiffs()
        {
            var a = new Sample { Name = "A", Age = 1 };
            var b = new Sample { Name = "B", Age = 2 };
            Assert.AreEqual(2, ObjectComparer.CompareObjects(a, b).Count);
        }

        [TestMethod]
        public void CompareObjects_IgnoreProperties_Skips()
        {
            var a = new Sample { Name = "A", Age = 1 };
            var b = new Sample { Name = "B", Age = 2 };
            var diffs = ObjectComparer.CompareObjects(a, b, new HashSet<string> { "Name", "Age" });
            Assert.AreEqual(0, diffs.Count);
        }

        [TestMethod]
        public void CompareObjects_NullVsValue_OneDiff()
        {
            Assert.AreEqual(1, ObjectComparer.CompareObjects(null, new Sample()).Count);
            Assert.AreEqual(1, ObjectComparer.CompareObjects(new Sample(), null).Count);
            Assert.AreEqual(0, ObjectComparer.CompareObjects(null, null).Count);
        }

        [TestMethod]
        public void CompareObjects_ListLengthMismatch_ReportsIndex()
        {
            var a = new Sample { Scores = new List<int> { 1, 2 } };
            var b = new Sample { Scores = new List<int> { 1 } };
            var diffs = ObjectComparer.CompareObjects(a, b);
            Assert.IsTrue(diffs.Any(d => d.Path.Contains("Scores[1]")));
        }

        [TestMethod]
        public void CompareObjects_DictionaryKeyAddedAndRemoved()
        {
            var a = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
            var b = new Dictionary<string, int> { ["x"] = 1, ["z"] = 3 };
            var diffs = ObjectComparer.CompareObjects(a, b);
            Assert.IsTrue(diffs.Any(d => d.Path.Contains("[y]")));
            Assert.IsTrue(diffs.Any(d => d.Path.Contains("[z]")));
        }

        [TestMethod]
        public void CompareObjects_SimpleTypeDiff()
        {
            var diffs = ObjectComparer.CompareObjects(1, 2);
            Assert.AreEqual(1, diffs.Count);
        }
    }

    [TestClass]
    public class ClsChargerDataSettingTests
    {
        [TestMethod]
        public void CC_CV_FV_TC_UpdateInternalSettingsScaledByTen()
        {
            var data = new clsChargerData();
            data.CC = 33.4;
            data.CV = 28.8;
            data.FV = 27.6;
            data.TC = 6.1;

            Assert.AreEqual(334, data.CC_Setting);
            Assert.AreEqual(288, data.CV_Setting);
            Assert.AreEqual(276, data.FV_Setting);
            Assert.AreEqual(61, data.TC_Setting);
        }

        [TestMethod]
        public void Setting_SameValueAssignedAgain_DoesNotChangeSetting()
        {
            var data = new clsChargerData();
            data.CC = 10;
            int first = data.CC_Setting;
            data.CC = 10;
            Assert.AreEqual(first, data.CC_Setting);
        }

        [TestMethod]
        public void SetAsUsing_And_SetAsNotUsing_ToggleFlags()
        {
            var data = new clsChargerData();
            data.SetAsUsing();
            Assert.IsTrue(data.IsUsing);
            Assert.IsTrue(data.Connected);

            data.Vin = 220;
            data.Vout = 28;
            data.Iout = 5;
            data.Temperature = 40;
            data.ErrorCodes.Add(clsChargeStation.ERROR_CODE.Fans);
            data.SetAsNotUsing();
            Assert.IsFalse(data.IsUsing);
            Assert.IsFalse(data.Connected);
            Assert.AreEqual(0, data.Vin);
            Assert.AreEqual(0, data.Vout);
            Assert.AreEqual(0, data.Iout);
            Assert.AreEqual(0, data.Temperature);
            Assert.AreEqual("", data.UseVehicleName);
            Assert.AreEqual(0, data.ErrorCodes.Count);
        }

        [TestMethod]
        public void ErrorCodesDescrptions_MirrorsEnumNames()
        {
            var data = new clsChargerData
            {
                ErrorCodes = new List<clsChargeStation.ERROR_CODE>
                {
                    clsChargeStation.ERROR_CODE.BUSY,
                    clsChargeStation.ERROR_CODE.Fans
                }
            };
            CollectionAssert.AreEqual(new List<string> { "BUSY", "Fans" }, data.ErrorCodesDescrptions);
        }
    }

    [TestClass]
    public class EquipmentStatusDtoDefaultsTests
    {
        [TestMethod]
        public void ChargStationStatus_Defaults()
        {
            var status = new ChargStationStatus();
            Assert.AreEqual(220.0, status.VoltageIn);
            Assert.AreEqual(0.0, status.VoltageOut);
            Assert.AreEqual(0.0, status.Current);
            Assert.AreEqual("", status.ErrorCodes);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN, status.Status);
        }

        [TestMethod]
        public void MainEQStatus_And_RackStatus_Defaults()
        {
            var eq = new MainEQStatus();
            Assert.IsFalse(eq.UnloadRequest);
            Assert.IsFalse(eq.LoadRequest);
            Assert.IsFalse(eq.CargoExist);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN, eq.Status);

            var rack = new RackStatus();
            Assert.AreEqual("", rack.Name);
            Assert.IsFalse(rack.Connected);
            Assert.AreEqual(EquipmentStatusBase.RUN_STATUS.DOWN, rack.Status);
        }
    }

    [TestClass]
    public class ClsAGVStateDtoHelperTests
    {
        [TestMethod]
        public void HasMrmpRegistrationPayload_EmptyAndFilled()
        {
            var dto = new clsAGVStateDto { AGV_Name = "A1" };
            Assert.IsFalse(dto.HasMrmpRegistrationPayload());
            dto.MrmpRegistrationPayloadJson = "{\"robot_id\":\"R1\"}";
            Assert.IsTrue(dto.HasMrmpRegistrationPayload());
            dto.SetMrmpRegistrationPayload(null!);
            Assert.IsFalse(dto.HasMrmpRegistrationPayload());
        }

        [TestMethod]
        public void HasChanged_SameState_ReturnsFalse()
        {
            var a = new clsAGVStateDto { AGV_Name = "A1", BatteryLevel_1 = 50, Theta = 10 };
            var b = new clsAGVStateDto { AGV_Name = "A1", BatteryLevel_1 = 50, Theta = 99 };
            // HasChanged 會忽略 Theta
            Assert.IsFalse(a.HasChanged(b));
        }

        [TestMethod]
        public void HasChanged_DifferentBattery_ReturnsTrue()
        {
            var a = new clsAGVStateDto { AGV_Name = "A1", BatteryLevel_1 = 50 };
            var b = new clsAGVStateDto { AGV_Name = "A1", BatteryLevel_1 = 60 };
            Assert.IsTrue(a.HasChanged(b));
        }
    }
}
