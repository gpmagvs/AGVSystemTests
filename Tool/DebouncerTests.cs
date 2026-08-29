using EquipmentManagment.Tool;

namespace EquipmentManagment.Tool.Tests
{
    [TestClass]
    public class DebouncerTests
    {
        [TestMethod]
        public async Task Debounce_AfterDelay_ActionInvoked()
        {
            var debouncer = new Debouncer();
            int count = 0;
            debouncer.Debounce(() => Interlocked.Increment(ref count), delay: 50);
            await Task.Delay(120);
            Assert.AreEqual(1, count);
        }

        [TestMethod]
        public async Task Debounce_RapidCalls_OnlyLastRuns()
        {
            var debouncer = new Debouncer();
            int count = 0;
            int lastValue = 0;

            debouncer.Debounce(() => { Interlocked.Increment(ref count); lastValue = 1; }, 80);
            await Task.Delay(20);
            debouncer.Debounce(() => { Interlocked.Increment(ref count); lastValue = 2; }, 80);
            await Task.Delay(20);
            debouncer.Debounce(() => { Interlocked.Increment(ref count); lastValue = 3; }, 80);

            await Task.Delay(150);
            Assert.AreEqual(1, count);
            Assert.AreEqual(3, lastValue);
        }
    }
}
