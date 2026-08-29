using AGVSystemCommonNet6.MRMP.Helpers;
using AGVSystemCommonNet6.MRMP.Models;
using System.Text;

namespace AGVSystemCommonNet6.MRMP.Helpers.Tests
{
    [TestClass]
    public class MrmpMessageHelperTests
    {
        private class SamplePayload
        {
            public string Name { get; set; } = "";
            public int Value { get; set; }
            public string? Optional { get; set; }
        }

        [TestMethod]
        public void ToJson_IgnoresNullProperties()
        {
            string json = MrmpMessageHelper.ToJson(new SamplePayload { Name = "n", Value = 1, Optional = null });
            Assert.IsTrue(json.Contains("\"Name\":\"n\"") || json.Contains("\"name\":\"n\"") || json.Contains("Name"));
            Assert.IsFalse(json.Contains("Optional") || json.Contains("optional"));
        }

        [TestMethod]
        public void FromJson_StringAndBytes_RoundTrip()
        {
            var original = new SamplePayload { Name = "x", Value = 7 };
            string json = MrmpMessageHelper.ToJson(original);
            SamplePayload? fromString = MrmpMessageHelper.FromJson<SamplePayload>(json);
            SamplePayload? fromBytes = MrmpMessageHelper.FromJson<SamplePayload>(Encoding.UTF8.GetBytes(json));

            Assert.IsNotNull(fromString);
            Assert.AreEqual("x", fromString!.Name);
            Assert.AreEqual(7, fromString.Value);
            Assert.IsNotNull(fromBytes);
            Assert.AreEqual("x", fromBytes!.Name);
        }

        [TestMethod]
        public void FromJson_Empty_ReturnsDefault()
        {
            Assert.IsNull(MrmpMessageHelper.FromJson<SamplePayload>(""));
            Assert.IsNull(MrmpMessageHelper.FromJson<SamplePayload>("   "));
            Assert.IsNull(MrmpMessageHelper.FromJson<SamplePayload>(Array.Empty<byte>()));
            Assert.IsNull(MrmpMessageHelper.FromJson<SamplePayload>((byte[])null!));
        }

        [TestMethod]
        public void CreateUserProperties_WithAndWithoutMissionId()
        {
            MqttUserProperties withRobot = MrmpMessageHelper.CreateUserProperties("R1");
            Assert.AreEqual("R1", withRobot.RobotId);
            Assert.AreEqual("", withRobot.MissionId);
            Assert.IsTrue(withRobot.Timestamp > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(withRobot.Uuid));

            MqttUserProperties withMission = MrmpMessageHelper.CreateUserProperties("R1", "M9");
            Assert.AreEqual("M9", withMission.MissionId);
        }

        [TestMethod]
        public void ToUserPropertyDictionary_WithoutRequire_OmitsEmptyRobotId()
        {
            var props = new MqttUserProperties
            {
                Timestamp = 1,
                Uuid = "u",
                RobotId = null,
                MissionId = null
            };
            Dictionary<string, string> dict = MrmpMessageHelper.ToUserPropertyDictionary(props, requireRobotAndMissionId: false);
            Assert.IsFalse(dict.ContainsKey("robot_id"));
            Assert.IsFalse(dict.ContainsKey("mission_id"));
        }

        [TestMethod]
        public void ToUserPropertyDictionary_RequireRobotAndMissionId_EmitsEmptyMissionId()
        {
            var props = new MqttUserProperties
            {
                Timestamp = 123,
                Uuid = "u1",
                RobotId = "R1",
                MissionId = null
            };
            Dictionary<string, string> dict = MrmpMessageHelper.ToUserPropertyDictionary(props, requireRobotAndMissionId: true);
            Assert.AreEqual("123", dict["timestamp"]);
            Assert.AreEqual("u1", dict["uuid"]);
            Assert.AreEqual("R1", dict["robot_id"]);
            Assert.AreEqual("", dict["mission_id"]);
        }

        [TestMethod]
        public void ToUserPropertyDictionary_FileUpload_IncludesFileFields()
        {
            MissionFileUploadUserProperties props = MrmpMessageHelper.CreateFileUploadUserProperties("R1", "M1", "a.bin", "log");
            Dictionary<string, string> dict = MrmpMessageHelper.ToUserPropertyDictionary(props, requireRobotAndMissionId: true);
            Assert.AreEqual("a.bin", dict["file_name"]);
            Assert.AreEqual("log", dict["file_type"]);
            Assert.AreEqual("M1", dict["mission_id"]);
            Assert.AreEqual("R1", dict["robot_id"]);
        }

        [TestMethod]
        public void GetUserProperty_MissingOrNull_ReturnsNull()
        {
            Assert.IsNull(MrmpMessageHelper.GetUserProperty(null, "k"));
            Assert.IsNull(MrmpMessageHelper.GetUserProperty(new Dictionary<string, string>(), "k"));
            Assert.AreEqual("v", MrmpMessageHelper.GetUserProperty(new Dictionary<string, string> { ["k"] = "v" }, "k"));
        }

        [TestMethod]
        public void TryParseJObject_ValidInvalidAndEmpty()
        {
            Assert.IsNotNull(MrmpMessageHelper.TryParseJObject(Encoding.UTF8.GetBytes("{\"a\":1}")));
            Assert.IsNull(MrmpMessageHelper.TryParseJObject(Encoding.UTF8.GetBytes("not-json")));
            Assert.IsNull(MrmpMessageHelper.TryParseJObject(Encoding.UTF8.GetBytes("")));
        }

        [TestMethod]
        public void NewMessageUuid_IsUnique()
        {
            var set = new HashSet<string>
            {
                MrmpMessageHelper.NewMessageUuid(),
                MrmpMessageHelper.NewMessageUuid(),
                MrmpMessageHelper.NewMessageUuid()
            };
            Assert.AreEqual(3, set.Count);
        }

        [TestMethod]
        public void UtcNowUnixMilliseconds_IsRecent()
        {
            long now = MrmpMessageHelper.UtcNowUnixMilliseconds();
            long expected = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.IsTrue(Math.Abs(now - expected) < 5000);
        }
    }
}
