using static EquipmentManagment.Device.Options.clsEQIOLocation;
using static EquipmentManagment.MainEquipment.clsEQ;
using static EquipmentManagment.MainEquipment.EQStatusDIDto;

namespace EquipmentManagment.MainEquipment.Tests
{
    [TestClass]
    public class EqStatusResolverTests
    {
        [TestMethod]
        public void IsEqStatusDown_V1_FollowsDownFlag()
        {
            Assert.IsTrue(EqStatusResolver.IsEqStatusDown(STATUS_IO_DEFINED_VERSION.V1, true));
            Assert.IsFalse(EqStatusResolver.IsEqStatusDown(STATUS_IO_DEFINED_VERSION.V1, false));
        }

        [TestMethod]
        public void IsEqStatusDown_V2_WhenEqpStatusDownFalse_IsDown()
        {
            // V2：Eqp_Status_Down=false 代表設備 DOWN（語意倒置）
            Assert.IsTrue(EqStatusResolver.IsEqStatusDown(STATUS_IO_DEFINED_VERSION.V2, false));
            Assert.IsFalse(EqStatusResolver.IsEqStatusDown(STATUS_IO_DEFINED_VERSION.V2, true));
        }

        [TestMethod]
        public void IsEqStatusNormalIdle_V1_RequiresIdleOnly()
        {
            Assert.IsTrue(EqStatusResolver.IsEqStatusNormalIdle(STATUS_IO_DEFINED_VERSION.V1, false, true, false));
            Assert.IsFalse(EqStatusResolver.IsEqStatusNormalIdle(STATUS_IO_DEFINED_VERSION.V1, false, true, true));
            Assert.IsFalse(EqStatusResolver.IsEqStatusNormalIdle(STATUS_IO_DEFINED_VERSION.V1, true, true, false));
        }

        [TestMethod]
        public void IsEqStatusNormalIdle_V2_FollowsDownFlagTrue()
        {
            Assert.IsTrue(EqStatusResolver.IsEqStatusNormalIdle(STATUS_IO_DEFINED_VERSION.V2, true, false, false));
            Assert.IsFalse(EqStatusResolver.IsEqStatusNormalIdle(STATUS_IO_DEFINED_VERSION.V2, false, true, false));
        }

        [TestMethod]
        public void ResolveMainStatus_V1_PriorityDownThenBusyThenIdle()
        {
            Assert.AreEqual(EQ_MAIN_STATUS.Down,
                EqStatusResolver.ResolveMainStatus(STATUS_IO_DEFINED_VERSION.V1, true, true, true));
            Assert.AreEqual(EQ_MAIN_STATUS.BUSY,
                EqStatusResolver.ResolveMainStatus(STATUS_IO_DEFINED_VERSION.V1, false, true, true));
            Assert.AreEqual(EQ_MAIN_STATUS.Idle,
                EqStatusResolver.ResolveMainStatus(STATUS_IO_DEFINED_VERSION.V1, false, false, true));
            Assert.AreEqual(EQ_MAIN_STATUS.Unknown,
                EqStatusResolver.ResolveMainStatus(STATUS_IO_DEFINED_VERSION.V1, false, false, false));
        }

        [TestMethod]
        public void ResolveMainStatus_V2_EqpStatusDownTrue_ReturnsIdle()
        {
            // 注意：與 EquipmentStatusMapper（V2 Down→RUN）不同層；此處 DTO 為 Idle
            Assert.AreEqual(EQ_MAIN_STATUS.Idle,
                EqStatusResolver.ResolveMainStatus(STATUS_IO_DEFINED_VERSION.V2, true, false, false));
            Assert.AreEqual(EQ_MAIN_STATUS.Down,
                EqStatusResolver.ResolveMainStatus(STATUS_IO_DEFINED_VERSION.V2, false, true, true));
        }

        [TestMethod]
        public void IsRackContentUnknown_BothOrNeither()
        {
            Assert.IsTrue(EqStatusResolver.IsRackContentUnknown(true, true));
            Assert.IsTrue(EqStatusResolver.IsRackContentUnknown(false, false));
            Assert.IsFalse(EqStatusResolver.IsRackContentUnknown(true, false));
            Assert.IsFalse(EqStatusResolver.IsRackContentUnknown(false, true));
        }

        [TestMethod]
        public void ResolveRackContentState_CheckDisabled_DefaultsFullUnbaked()
        {
            Assert.AreEqual(RACK_CONTENT_STATE.FULL_UNBAKED,
                EqStatusResolver.ResolveRackContentState(false, false, false));
        }

        [TestMethod]
        public void ResolveRackContentState_CheckEnabled_MapsEmptyFullUnknown()
        {
            Assert.AreEqual(RACK_CONTENT_STATE.EMPTY,
                EqStatusResolver.ResolveRackContentState(true, false, true));
            Assert.AreEqual(RACK_CONTENT_STATE.FULL_UNBAKED,
                EqStatusResolver.ResolveRackContentState(true, true, false));
            Assert.AreEqual(RACK_CONTENT_STATE.UNKNOWN,
                EqStatusResolver.ResolveRackContentState(true, false, false));
        }
    }

    [TestClass]
    public class EqTaskGateEvaluatorTests
    {
        [TestMethod]
        public void IsCreateLoad_HappyPath_NoMechanism_True()
        {
            Assert.IsTrue(EqTaskGateEvaluator.IsCreateLoadTaskAble(
                loadRequest: true,
                isEqStatusDown: false,
                portExist: false,
                cmdReserveUp: false,
                hasLdUldMechanism: false, downPose: false,
                hasCstSteeringMechanism: false, tbDownPose: false));
        }

        [TestMethod]
        public void IsCreateLoad_RequiresNoPortExist()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateLoadTaskAble(
                true, false, portExist: true, false,
                false, false, false, false));
        }

        [TestMethod]
        public void IsCreateLoad_WhenEqDown_False()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateLoadTaskAble(
                true, isEqStatusDown: true, false, false,
                false, false, false, false));
        }

        [TestMethod]
        public void IsCreateLoad_WithLdUldMechanism_RequiresDownPose()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateLoadTaskAble(
                true, false, false, false,
                hasLdUldMechanism: true, downPose: false,
                false, false));
            Assert.IsTrue(EqTaskGateEvaluator.IsCreateLoadTaskAble(
                true, false, false, false,
                hasLdUldMechanism: true, downPose: true,
                false, false));
        }

        [TestMethod]
        public void IsCreateUnload_HappyPath_RequiresPortExist()
        {
            Assert.IsTrue(EqTaskGateEvaluator.IsCreateUnloadTaskAble(
                unloadRequest: true,
                isEqStatusDown: false,
                portExist: true,
                cmdReserveUp: false,
                hasLdUldMechanism: false, upPose: false,
                hasCstSteeringMechanism: false, tbDownPose: false,
                checkRackContentStateIoSignal: false,
                rackContentState: RACK_CONTENT_STATE.UNKNOWN));
        }

        [TestMethod]
        public void IsCreateUnload_RequiresPortExist()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateUnloadTaskAble(
                true, false, portExist: false, false,
                false, false, false, false,
                false, RACK_CONTENT_STATE.EMPTY));
        }

        [TestMethod]
        public void IsCreateUnload_WhenCheckRackEnabled_UnknownBlocks()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateUnloadTaskAble(
                true, false, true, false,
                false, false, false, false,
                checkRackContentStateIoSignal: true,
                rackContentState: RACK_CONTENT_STATE.UNKNOWN));

            Assert.IsTrue(EqTaskGateEvaluator.IsCreateUnloadTaskAble(
                true, false, true, false,
                false, false, false, false,
                checkRackContentStateIoSignal: true,
                rackContentState: RACK_CONTENT_STATE.EMPTY));
        }

        [TestMethod]
        public void IsCreateUnload_WithLdUldMechanism_RequiresUpPose()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateUnloadTaskAble(
                true, false, true, false,
                hasLdUldMechanism: true, upPose: false,
                false, false, false, RACK_CONTENT_STATE.EMPTY));

            Assert.IsTrue(EqTaskGateEvaluator.IsCreateUnloadTaskAble(
                true, false, true, false,
                hasLdUldMechanism: true, upPose: true,
                false, false, false, RACK_CONTENT_STATE.EMPTY));
        }

        [TestMethod]
        public void IsCreateUnload_ReserveUp_Blocks()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateUnloadTaskAble(
                true, false, true, cmdReserveUp: true,
                false, false, false, false,
                false, RACK_CONTENT_STATE.EMPTY));
        }

        [TestMethod]
        public void IsCreateLoad_WithSteering_RequiresTbDownPose()
        {
            Assert.IsFalse(EqTaskGateEvaluator.IsCreateLoadTaskAble(
                true, false, false, false,
                false, false,
                hasCstSteeringMechanism: true, tbDownPose: false));
            Assert.IsTrue(EqTaskGateEvaluator.IsCreateLoadTaskAble(
                true, false, false, false,
                false, false,
                hasCstSteeringMechanism: true, tbDownPose: true));
        }
    }
}
