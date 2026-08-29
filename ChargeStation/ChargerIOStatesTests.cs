using EquipmentManagment.ChargeStation;

namespace EquipmentManagment.ChargeStation.Tests
{
    [TestClass]
    public class ChargerIOStatesTests
    {
        [TestMethod]
        public void EMO_FalseToTrue_FiresOnEMOOnce()
        {
            var states = new ChargerIOStates();
            int alarm = 0;
            int recovery = 0;
            states.OnEMO += (_, __) => alarm++;
            states.OnEMORecovery += (_, __) => recovery++;

            // 預設 _EMO = true，先降為 false 再升為 true 才觸發 alarm
            states.EMO = false;
            Assert.AreEqual(1, recovery);
            states.EMO = true;
            Assert.AreEqual(1, alarm);

            states.EMO = true; // 同值不重複觸發
            Assert.AreEqual(1, alarm);
        }

        [TestMethod]
        public void SMOKE_TrueToFalse_FiresRecovery()
        {
            var states = new ChargerIOStates();
            int detected = 0;
            int recovery = 0;
            states.OnSmokeDetected += (_, __) => detected++;
            states.OnSmokeDetectRecovery += (_, __) => recovery++;

            states.SMOKE_DECTECTED = true;
            Assert.AreEqual(1, detected);
            states.SMOKE_DECTECTED = false;
            Assert.AreEqual(1, recovery);
        }

        [TestMethod]
        public void AIR_ERROR_EdgeTriggersAlarmAndRecovery()
        {
            var states = new ChargerIOStates();
            int alarm = 0;
            int recovery = 0;
            states.OnAirError += (_, __) => alarm++;
            states.OnAirErrorRecovery += (_, __) => recovery++;

            states.AIR_ERROR = true;
            states.AIR_ERROR = false;
            Assert.AreEqual(1, alarm);
            Assert.AreEqual(1, recovery);
        }

        [TestMethod]
        public void TEMPERATURE_TrueToFalse_NoRecoveryEvent()
        {
            var states = new ChargerIOStates();
            int error = 0;
            states.OnTemperatureError += (_, __) => error++;

            states.TEMPERATURE_MODULE_ABN = true;
            Assert.AreEqual(1, error);
            states.TEMPERATURE_MODULE_ABN = false;
            Assert.AreEqual(1, error); // 無 recovery 事件
            Assert.IsFalse(states.TEMPERATURE_MODULE_ABN);
        }

        [TestMethod]
        public void UpdateIOAcutalInputState_WritesPublicFlags()
        {
            var states = new ChargerIOStates();
            states.UpdateIOAcutalInputState(true, true, true, true, true, false);
            Assert.IsTrue(states.IO_St_EMO);
            Assert.IsTrue(states.IO_St_SMOKE_DECTECTED);
            Assert.IsTrue(states.IO_St_AIR_ERROR);
            Assert.IsTrue(states.IO_St_TEMPERATURE_MODULE_ABN);
            Assert.IsTrue(states.IO_St_CYLINDER_FORWARD);
            Assert.IsFalse(states.IO_St_CYLINDER_BACKWARD);
        }

        [TestMethod]
        public void Reset_ClearsInternalAlarmFields()
        {
            var states = new ChargerIOStates();
            states.EMO = true;
            states.SMOKE_DECTECTED = true;
            states.AIR_ERROR = true;
            states.TEMPERATURE_MODULE_ABN = true;

            states.Reset();
            Assert.IsFalse(states.EMO);
            Assert.IsFalse(states.SMOKE_DECTECTED);
            Assert.IsFalse(states.AIR_ERROR);
            Assert.IsFalse(states.TEMPERATURE_MODULE_ABN);
        }
    }
}
