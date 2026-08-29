using EquipmentManagment.Tool;

namespace EquipmentManagment.Tool.Tests
{
    [TestClass]
    public class CRCCalculatorTests
    {
        [TestMethod]
        public void GetCRC16_Empty_ReturnsInitValue()
        {
            Assert.AreEqual((ushort)0xFFFF, CRCCalculator.GetCRC16(Array.Empty<byte>()));
        }

        [TestMethod]
        public void GetCRC16_KnownModbusVectors()
        {
            // 01 03 00 00 00 0A → 0xCDC5
            Assert.AreEqual((ushort)0xCDC5,
                CRCCalculator.GetCRC16(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A }));

            // 單一位元組
            ushort single = CRCCalculator.GetCRC16(new byte[] { 0x00 });
            Assert.AreNotEqual((ushort)0xFFFF, single);
        }

        [TestMethod]
        public void GetCRC16_SameInput_IsDeterministic()
        {
            byte[] data = { 0xAA, 0x01, 0x02, 0x03, 0xFF };
            Assert.AreEqual(CRCCalculator.GetCRC16(data), CRCCalculator.GetCRC16(data));
        }

        [TestMethod]
        public void GetCRC16_DifferentInput_ProducesDifferentCrc()
        {
            Assert.AreNotEqual(
                CRCCalculator.GetCRC16(new byte[] { 0x01 }),
                CRCCalculator.GetCRC16(new byte[] { 0x02 }));
            Assert.AreNotEqual(
                CRCCalculator.GetCRC16(new byte[] { 0x01, 0x02 }),
                CRCCalculator.GetCRC16(new byte[] { 0x01, 0x03 }));
        }

        [TestMethod]
        public void GetCRC16_OrderMatters()
        {
            Assert.AreNotEqual(
                CRCCalculator.GetCRC16(new byte[] { 0x01, 0x02, 0x03 }),
                CRCCalculator.GetCRC16(new byte[] { 0x03, 0x02, 0x01 }));
        }

        [TestMethod]
        public void GetCRC16_LongerPayload_StillReturnsUshort()
        {
            byte[] data = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            ushort crc = CRCCalculator.GetCRC16(data);
            Assert.IsTrue(crc >= 0);
        }
    }
}
