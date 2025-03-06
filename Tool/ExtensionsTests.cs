using Microsoft.VisualStudio.TestTools.UnitTesting;
using EquipmentManagment.Tool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EquipmentManagment.Tool.Tests
{
    [TestClass()]
    public class ExtensionsTests
    {
        [TestMethod()]
        public void DoubleToLinear11V2Test()
        {
            double fv = 27.1;

            byte[] dataBytes = fv.DoubleToLinear11V2(-2);

            double doubleValConverted = dataBytes.Linear11ToDouble(-2);

            double error = Math.Abs(fv - doubleValConverted);
            Console.WriteLine($"error : {error}");

            Assert.IsTrue(error <= 0.11);
        }
    }
}