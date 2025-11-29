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
            double fvStart = 25.0;
            double fvEnd = 26.0;
            double step = 0.1;
            double errorThreshold = 0.1001;
            int count = 1;
            double maxError = 0;
            double minError = 0;

            for (double fvS = fvStart; fvS <= fvEnd; fvS += step)
            {
                byte[] dataBytes = (fvS).DoubleToLinear11(-2);
                double doubleValConverted = dataBytes.Linear11ToDouble(-2);
                double error = Math.Abs(fvS - doubleValConverted);
                maxError = error > maxError ? error : maxError;
                minError = (count == 1 || error < minError) ? error : minError;
                Console.WriteLine($"[{count}] original = {fvS}, converted back = {doubleValConverted}. Error = {error}");
                if (error > errorThreshold)
                {
                    Console.WriteLine($"[{count}] Value: {fvS} conversion error too high: {error}");
                    Assert.Fail($"Value: {fvS} conversion error too high: {error}");
                }
                count++;
            }
            Console.WriteLine($"Max error: {maxError}");
            Console.WriteLine($"Min error: {minError}");
            Console.WriteLine($"All value error less than {errorThreshold}");
            Assert.IsTrue(true);


            //byte[] dataBytes = fvStart.DoubleToLinear11V2(-2);
            //double doubleValConverted = dataBytes.Linear11ToDouble(-2);
            //double error = Math.Abs(fvStart - doubleValConverted);
            //Console.WriteLine($"original : {fvStart}, converted roll back : {doubleValConverted}");
            //Console.WriteLine($"error : {error}");

            //Assert.IsTrue(error <= 0.11);
        }

        [TestMethod()]
        public void DoubleToLinear16Test()
        {
            double fvStart = 25.0;
            double fvEnd = 26.0;
            double step = 0.1;
            double errorThreshold = 0.1001;
            int count = 1;
            double maxError = 0;
            double minError = 0;

            for (double fvS = fvStart; fvS <= fvEnd; fvS += step)
            {
                byte[] dataBytes = (fvS).DoubleToLinear16(-9);
                double doubleValConverted = dataBytes.Linear16ToDouble(-9);
                double error = Math.Abs(fvS - doubleValConverted);
                maxError = error > maxError ? error : maxError;
                minError = (count == 1 || error < minError) ? error : minError;
                Console.WriteLine($"[{count}] original = {fvS}, converted back = {doubleValConverted}. Error = {error}");
                if (error > errorThreshold)
                {
                    Console.WriteLine($"[{count}] Value: {fvS} conversion error too high: {error}");
                    Assert.Fail($"Value: {fvS} conversion error too high: {error}");
                }
                count++;
            }
            Console.WriteLine($"Max error: {maxError}");
            Console.WriteLine($"Min error: {minError}");
            Console.WriteLine($"All value error less than {errorThreshold}");
            Assert.IsTrue(true);


            //byte[] dataBytes = fvStart.DoubleToLinear11V2(-2);
            //double doubleValConverted = dataBytes.Linear11ToDouble(-2);
            //double error = Math.Abs(fvStart - doubleValConverted);
            //Console.WriteLine($"original : {fvStart}, converted roll back : {doubleValConverted}");
            //Console.WriteLine($"error : {error}");

            //Assert.IsTrue(error <= 0.11);
        }
    }
}