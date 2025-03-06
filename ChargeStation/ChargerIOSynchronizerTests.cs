using Microsoft.VisualStudio.TestTools.UnitTesting;
using EquipmentManagment.ChargeStation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Polly;

namespace EquipmentManagment.ChargeStation.Tests
{
    [TestClass()]
    public class ChargerIOSynchronizerTests
    {
        [ClassInitialize]
        public static void TestClassInit(TestContext t)
        {

        }

        [TestMethod()]
        public void ConnectToAsyncTest()
        {
            ChargerIOSynchronizer chargerIOSynchronizer = CreateTestIOSynchronizer();
            (bool confirm, string message) results = chargerIOSynchronizer.ConnectToAsync().GetAwaiter().GetResult();
            Assert.IsTrue(results.confirm);
        }

        [TestMethod()]
        public void ContinuePollingTest()
        {

            List<Task<string>> tasks = new List<Task<string>>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Polling(i));
            }

            string[] results = Task.WhenAll(tasks).GetAwaiter().GetResult();

            int succesNum = results.Count(r => r.Contains("SUCCESS"));

            Console.WriteLine("Polling Results:\r\n" + string.Join("\r\n", results));
            Console.WriteLine($"Success: {succesNum}, Fail: {results.Length - succesNum}");
            Assert.IsTrue(results.All(r => r.Contains("SUCCESS")));

            async Task<string> Polling(int id)
            {
                ChargerIOSynchronizer chargerIOSynchronizer = CreateTestIOSynchronizer();

                // 使用 Polly 进行重试策略
                Polly.Retry.AsyncRetryPolicy<(bool confirm, string message)> retryPolicy = Policy<(bool confirm, string message)>
                    .HandleResult(result => !result.confirm)  // 若连接失败则重试
                    .WaitAndRetryAsync(
                        retryCount: 10,                          // 重试 10 次
                        retryAttempt => TimeSpan.FromMilliseconds(50), // 每次等待 50 毫秒
                        (result, timeSpan, retryCount, context) =>
                        {
                            Console.WriteLine($"第 {retryCount} 次重试: {result.Result.message}");
                        });

                (bool confirm, string message) results = await retryPolicy.ExecuteAsync(() => chargerIOSynchronizer.ConnectToAsync());

                if (!results.confirm)
                    return _log("No-Connect");
                Stopwatch stopwatch = Stopwatch.StartNew();
                bool _success_above_once = false;
                while (true)
                {
                    Polly.Retry.AsyncRetryPolicy<(bool, string)> pollingDataRetryPolicy = Policy<(bool, string)>
                    .HandleResult(result => !result.Item1)  // 若连接失败则重试
                    .WaitAndRetryAsync(
                        retryCount: 10,                          // 重试 10 次
                        retryAttempt => TimeSpan.FromMilliseconds(50), // 每次等待 50 毫秒
                        (result, timeSpan, retryCount, context) =>
                        {
                            Console.WriteLine($"第 {retryCount} 次重试: {result.Result.Item2}");
                        });

                    await pollingDataRetryPolicy.ExecuteAsync(async () =>
                    {
                        try
                        {
                            chargerIOSynchronizer.ReadCoils();
                            return (true, "");
                        }
                        catch (Exception ex)
                        {
                            return (false, ex.Message);
                        }
                    });

                    await Task.Delay(100);
                    if (stopwatch.ElapsedMilliseconds > 1000)
                    {
                        return _log("SUCCESS");
                    }
                }


                string _log(string message)
                {
                    string _msg = $"Polling {id}: {message}";
                    Console.WriteLine(_msg);
                    return _msg;
                }
            }

        }

        private ChargerIOSynchronizer CreateTestIOSynchronizer()
        {
            return new ChargerIOSynchronizer(new clsChargeStationOptions
            {
                IsEmulation = false,
                IOModubleConnOptions = new Connection.ConnectOptions
                {
                    IP = "192.168.0.101",
                    Port = 502
                }
            });
        }
    }
}