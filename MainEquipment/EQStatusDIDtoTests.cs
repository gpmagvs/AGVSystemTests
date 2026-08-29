using EquipmentManagment.Device.Options;
using EquipmentManagment.MainEquipment;
using static EquipmentManagment.MainEquipment.EQStatusDIDto;

namespace EquipmentManagment.MainEquipment.Tests
{
    [TestClass]
    public class EQStatusDIDtoTests
    {
        [TestMethod]
        public void TransferStatus_Disconnected_OverridesLoad()
        {
            var dto = new EQStatusDIDto(EQ_TYPE.EQ)
            {
                IsConnected = false,
                Load_Request = true,
                Unload_Request = true
            };
            Assert.AreEqual(EQ_TRANSFER_STATUS.DISCONNECT, dto.TransferStatus);
        }

        [TestMethod]
        public void TransferStatus_LoadWinsOverUnload()
        {
            var dto = new EQStatusDIDto(EQ_TYPE.EQ)
            {
                IsConnected = true,
                Load_Request = true,
                Unload_Request = true
            };
            Assert.AreEqual(EQ_TRANSFER_STATUS.LOADABLE, dto.TransferStatus);
        }

        [TestMethod]
        public void TransferStatus_UnloadOnly_Unloadable()
        {
            var dto = new EQStatusDIDto(EQ_TYPE.EQ)
            {
                IsConnected = true,
                Load_Request = false,
                Unload_Request = true
            };
            Assert.AreEqual(EQ_TRANSFER_STATUS.UNLOADABLE, dto.TransferStatus);
        }

        [TestMethod]
        public void TransferStatus_Neither_Unknown()
        {
            var dto = new EQStatusDIDto(EQ_TYPE.STK)
            {
                IsConnected = true,
                Load_Request = false,
                Unload_Request = false
            };
            Assert.AreEqual(EQ_TRANSFER_STATUS.Unknown, dto.TransferStatus);
        }
    }
}
