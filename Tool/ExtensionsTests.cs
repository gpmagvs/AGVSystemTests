using EquipmentManagment.Tool;

namespace EquipmentManagment.Tool.Tests
{
    [TestClass]
    public class ExtensionsTests
    {
        [TestMethod]
        public void DoubleToLinear11V2_RoundTrip_WithinTolerance()
        {
            double errorThreshold = 0.1001;
            double maxError = 0;

            for (decimal fvS = 25.0m; fvS <= 26.0m; fvS += 0.1m)
            {
                double value = (double)fvS;
                byte[] dataBytes = value.DoubleToLinear11V2(-2);
                double converted = dataBytes.Linear11ToDouble(-2);
                double error = Math.Abs(value - converted);
                maxError = Math.Max(maxError, error);
                Assert.IsTrue(error <= errorThreshold, $"Value {value} error {error}");
            }
            Assert.IsTrue(maxError <= errorThreshold);
        }

        [TestMethod]
        public void DoubleToLinear11V2_OtherNValues()
        {
            byte[] bytes = 10.0.DoubleToLinear11V2(-1);
            Assert.AreEqual(2, bytes.Length);
            double back = bytes.Linear11ToDouble(-1);
            Assert.IsTrue(Math.Abs(10.0 - back) < 0.6);
        }

        [TestMethod]
        public void DoubleToLinear11V2_OutOfRangeN_Throws()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => 1.0.DoubleToLinear11V2(-17));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => 1.0.DoubleToLinear11V2(16));
        }

        [TestMethod]
        public void DoubleToLinear16_ReturnsTwoBytes_Decodable()
        {
            byte[] dataBytes = 25.0.DoubleToLinear16(-9);
            Assert.AreEqual(2, dataBytes.Length);
            float decoded = dataBytes.Linear16ToDouble(-9);
            Assert.IsFalse(float.IsNaN(decoded));
            Assert.IsFalse(float.IsInfinity(decoded));
        }

        [TestMethod]
        public void Linear11ToDouble_Empty_ReturnsZero()
        {
            Assert.AreEqual(0, Array.Empty<byte>().Linear11ToDouble(-2));
        }

        [TestMethod]
        public void GetBoolArray_BitPattern_SwapsHighLowBytes()
        {
            bool[] bits = ((ushort)0b0000_0000_0000_0101).GetBoolArray();
            Assert.AreEqual(16, bits.Length);
            Assert.IsTrue(bits[8]);
            Assert.IsFalse(bits[9]);
            Assert.IsTrue(bits[10]);
        }

        [TestMethod]
        public void GetBoolArray_ThenGetUshort_RoundTrip()
        {
            ushort original = 0b1010_0000_0000_0101;
            bool[] bits = original.GetBoolArray();
            ushort back = bits.GetUshort();
            Assert.AreEqual(original, back);
        }

        [TestMethod]
        public void ToBitArray_Byte_HasEightElements()
        {
            int[] bits = ((byte)0b10110001).ToBitArray();
            Assert.AreEqual(8, bits.Length);
            Assert.IsTrue(bits.All(b => b == 0 || b == 1));
            // 0b10110001 有 4 個 bit 為 1
            Assert.AreEqual(4, bits.Sum());
        }

        [TestMethod]
        public void GetHighLowBytes_SplitsLowThenHigh()
        {
            byte[] bytes = 0x1234.GetHighLowBytes();
            Assert.AreEqual(2, bytes.Length);
            Assert.AreEqual(0x34, bytes[0]);
            Assert.AreEqual(0x12, bytes[1]);
        }

        [TestMethod]
        public void GetHighLowBytes_ThenGetInt_RoundTrip()
        {
            const int original = 0x1234;
            short back = original.GetHighLowBytes().GetInt();
            Assert.AreEqual((short)original, back);
        }

        [TestMethod]
        public void LinearToDouble_And_DoubleToLinear_RoundTrip()
        {
            byte[] encoded = 25.0.DoubleToLinear();
            Assert.AreEqual(25.0, encoded.LinearToDouble());
        }
    }
}
