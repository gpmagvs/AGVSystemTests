using EquipmentManagment.Device.Options;
using EquipmentManagment.WIP;
using static EquipmentManagment.WIP.clsPortOfRack;

namespace EquipmentManagment.WIP.Tests
{
    [TestClass]
    public class clsPortOfRackTests
    {
        private static clsPortOfRack CreatePort(bool hasTray = true, bool hasRack = true, bool hasClaw = false)
        {
            return new clsPortOfRack
            {
                Properties = new clsRackPortProperty
                {
                    HasTraySensor = hasTray,
                    HasRackSensor = hasRack,
                    HasClawMechanism = hasClaw
                }
            };
        }

        private static void SetSensors(clsPortOfRack port,
            SENSOR_STATUS tray1 = SENSOR_STATUS.OFF,
            SENSOR_STATUS tray2 = SENSOR_STATUS.OFF,
            SENSOR_STATUS rack1 = SENSOR_STATUS.OFF,
            SENSOR_STATUS rack2 = SENSOR_STATUS.OFF,
            SENSOR_STATUS area = SENSOR_STATUS.OFF)
        {
            port.SensorStates[SENSOR_LOCATION.TRAY_1] = tray1;
            port.SensorStates[SENSOR_LOCATION.TRAY_2] = tray2;
            port.SensorStates[SENSOR_LOCATION.RACK_1] = rack1;
            port.SensorStates[SENSOR_LOCATION.RACK_2] = rack2;
            port.SensorStates[SENSOR_LOCATION.RACK_AREA] = area;
        }

        [TestMethod]
        public void CheckCargoExistSensorsState_TrayBothOn_PlacedNormal()
        {
            clsPortOfRack port = CreatePort(hasTray: true, hasRack: false);
            SetSensors(port, tray1: SENSOR_STATUS.ON, tray2: SENSOR_STATUS.ON);
            Assert.AreEqual(CARGO_PLACEMENT_STATUS.PLACED_NORMAL, port.CheckCargoExistSensorsState());
        }

        [TestMethod]
        public void CheckCargoExistSensorsState_TrayOneOff_Asymmetric()
        {
            clsPortOfRack port = CreatePort(hasTray: true, hasRack: false);
            SetSensors(port, tray1: SENSOR_STATUS.ON, tray2: SENSOR_STATUS.OFF);
            Assert.AreEqual(CARGO_PLACEMENT_STATUS.PLACED_BUT_ASYMMETRIC, port.CheckCargoExistSensorsState());
        }

        [TestMethod]
        public void CheckCargoExistSensorsState_TrayAndRack_TypeUnknown()
        {
            clsPortOfRack port = CreatePort();
            SetSensors(port,
                tray1: SENSOR_STATUS.ON, tray2: SENSOR_STATUS.ON,
                rack1: SENSOR_STATUS.ON, rack2: SENSOR_STATUS.ON);
            Assert.AreEqual(CARGO_PLACEMENT_STATUS.PLACED_BUT_TYPE_UNKNOWN, port.CheckCargoExistSensorsState());
        }

        [TestMethod]
        public void CheckCargoExistSensorsState_None_NoCargo()
        {
            clsPortOfRack port = CreatePort();
            SetSensors(port);
            Assert.AreEqual(CARGO_PLACEMENT_STATUS.NO_CARGO, port.CheckCargoExistSensorsState());
        }

        [TestMethod]
        public void CheckCargoExistSensorsState_RackBothOn_PlacedNormal()
        {
            clsPortOfRack port = CreatePort(hasTray: false, hasRack: true);
            SetSensors(port, rack1: SENSOR_STATUS.ON, rack2: SENSOR_STATUS.ON);
            Assert.AreEqual(CARGO_PLACEMENT_STATUS.PLACED_NORMAL, port.CheckCargoExistSensorsState());
        }

        [TestMethod]
        public void CargoExist_FlashCountsAsExist()
        {
            clsPortOfRack port = CreatePort(hasTray: true, hasRack: false);
            SetSensors(port, tray1: SENSOR_STATUS.FLASH, tray2: SENSOR_STATUS.OFF);
            Assert.IsTrue(port.CargoExist);
        }

        [TestMethod]
        public void CargoExist_HasClaw_UsesCarrierExist()
        {
            clsPortOfRack port = CreatePort(hasClaw: true);
            SetSensors(port, tray1: SENSOR_STATUS.ON, tray2: SENSOR_STATUS.ON);
            port.CarrierExist = false;
            Assert.IsFalse(port.CargoExist);
            port.CarrierExist = true;
            Assert.IsTrue(port.CargoExist);
        }

        [TestMethod]
        public void TrayPlacementState_OnOff_Asymmetric()
        {
            clsPortOfRack port = CreatePort();
            SetSensors(port, tray1: SENSOR_STATUS.ON, tray2: SENSOR_STATUS.OFF);
            Assert.AreEqual(CARGO_PLACEMENT_STATUS.PLACED_BUT_ASYMMETRIC, port.TrayPlacementState);
        }

        [TestMethod]
        public void TrayPlacementState_Flash_NoCargoButClick()
        {
            clsPortOfRack port = CreatePort();
            SetSensors(port, tray1: SENSOR_STATUS.FLASH, tray2: SENSOR_STATUS.OFF);
            Assert.AreEqual(CARGO_PLACEMENT_STATUS.NO_CARGO_BUT_CLICK, port.TrayPlacementState);
        }

        [TestMethod]
        public void IsAreaSensorOn_TypeB_OnMeansTrue()
        {
            clsPortOfRack port = CreatePort();
            port.Properties.IOLocation.Rack_Area_Sensor_Input_Type = "B";
            SetSensors(port, area: SENSOR_STATUS.ON);
            Assert.IsTrue(port.IsAreaSensorOn());
            SetSensors(port, area: SENSOR_STATUS.OFF);
            Assert.IsFalse(port.IsAreaSensorOn());
        }

        [TestMethod]
        public void IsAreaSensorOn_TypeA_OffMeansTrue()
        {
            clsPortOfRack port = CreatePort();
            port.Properties.IOLocation.Rack_Area_Sensor_Input_Type = "A";
            SetSensors(port, area: SENSOR_STATUS.OFF);
            Assert.IsTrue(port.IsAreaSensorOn());
            SetSensors(port, area: SENSOR_STATUS.ON);
            Assert.IsFalse(port.IsAreaSensorOn());
        }

        [TestMethod]
        public void IsHasCstIDButNoCargo_HasIdNoSensors_True()
        {
            clsPortOfRack port = CreatePort();
            SetSensors(port);
            port.CarrierID = "CST-001";
            Assert.IsTrue(port.IsHasCstIDButNoCargo(out bool placedNormally));
            Assert.IsTrue(placedNormally);
        }

        [TestMethod]
        public void IsHasCstIDButNoCargo_NoId_False()
        {
            clsPortOfRack port = CreatePort();
            SetSensors(port);
            port.CarrierID = "";
            Assert.IsFalse(port.IsHasCstIDButNoCargo(out _));
        }

        [TestMethod]
        public void IsHasCstIDButNoCargo_HasIdAndNormalPlacement_False()
        {
            clsPortOfRack port = CreatePort(hasTray: true, hasRack: false);
            SetSensors(port, tray1: SENSOR_STATUS.ON, tray2: SENSOR_STATUS.ON);
            port.CarrierID = "CST-002";
            Assert.IsFalse(port.IsHasCstIDButNoCargo(out bool placedNormally));
            Assert.IsTrue(placedNormally);
        }

        [TestMethod]
        public void ChangeUsableState_SameValue_ReturnsFalse()
        {
            clsPortOfRack port = CreatePort();
            Assert.IsFalse(port.ChangeUsableState(true));
        }

        [TestMethod]
        public void ChangeUsableState_Toggle_UpdatesAndReturnsTrue()
        {
            clsPortOfRack port = CreatePort();
            Assert.IsTrue(port.ChangeUsableState(false));
            Assert.AreEqual(PORT_USABLE.NOT_USABLE, port.Properties.PortUsable);
            Assert.IsTrue(port.ChangeUsableState(true));
            Assert.AreEqual(PORT_USABLE.USABLE, port.Properties.PortUsable);
        }

        [TestMethod]
        public void MaterialExistSensorStates_ExcludesAreaAndDirection()
        {
            clsPortOfRack port = CreatePort();
            var keys = port.MaterialExistSensorStates.Keys.ToList();
            Assert.IsFalse(keys.Contains(SENSOR_LOCATION.RACK_AREA));
            Assert.IsFalse(keys.Contains(SENSOR_LOCATION.TRAY_DIRECTION));
            Assert.AreEqual(4, keys.Count);
        }
    }
}
