using AGVSystemCommonNet6.MRMP.Topics;

namespace AGVSystemCommonNet6.MRMP.Topics.Tests
{
    [TestClass]
    public class MrmpTopicsTests
    {
        [TestMethod]
        public void Constants_MatchSpec()
        {
            Assert.AreEqual("mrmp/registration", MrmpTopics.Registration);
            Assert.AreEqual("mrmp/emergency", MrmpTopics.Emergency);
        }

        [TestMethod]
        public void TopicBuilders_InsertRobotId()
        {
            Assert.AreEqual("mrmp/R1/#", MrmpTopics.RobotWildcard("R1"));
            Assert.AreEqual("mrmp/R1/remote_command/mission", MrmpTopics.MissionCommand("R1"));
            Assert.AreEqual("mrmp/R1/remote_command/mission_abort", MrmpTopics.MissionAbort("R1"));
            Assert.AreEqual("mrmp/R1/emergency_report", MrmpTopics.EmergencyReport("R1"));
            Assert.AreEqual("mrmp/R1/alarm/set", MrmpTopics.AlarmSet("R1"));
            Assert.AreEqual("mrmp/R1/alarm/clear", MrmpTopics.AlarmClear("R1"));
            Assert.AreEqual("mrmp/R1/report/robot/status", MrmpTopics.ReportRobotStatus("R1"));
            Assert.AreEqual("mrmp/R1/report/robot/loc", MrmpTopics.ReportRobotLocation("R1"));
            Assert.AreEqual("mrmp/R1/report/robot/battery", MrmpTopics.ReportRobotBattery("R1"));
            Assert.AreEqual("mrmp/R1/report/mission/status", MrmpTopics.ReportMissionStatus("R1"));
            Assert.AreEqual("mrmp/R1/report/mission/file_upload", MrmpTopics.ReportMissionFileUpload("R1"));
            Assert.AreEqual("mrmp/R1/report/mission/detection", MrmpTopics.ReportMissionDetection("R1"));
            Assert.AreEqual("mrmp/R1/report/S1/sensor_status", MrmpTopics.ReportSensorStatus("R1", "S1"));
            Assert.AreEqual("mrmp/R1/report/streaming", MrmpTopics.ReportStreaming("R1"));
            Assert.AreEqual("mrmp/R1/get/map", MrmpTopics.GetMap("R1"));
            Assert.AreEqual("mrmp/R1/set/map", MrmpTopics.SetMap("R1"));
            Assert.AreEqual("mrmp/R1/get/robot_action_list", MrmpTopics.GetRobotActionList("R1"));
            Assert.AreEqual("mrmp/R1/get/recipe_list", MrmpTopics.GetRecipeList("R1"));
            Assert.AreEqual("mrmp/R1/get/recipe_body", MrmpTopics.GetRecipeBody("R1"));
            Assert.AreEqual("mrmp/R1/set/recipe", MrmpTopics.SetRecipe("R1"));
            Assert.AreEqual("mrmp/R1/delete/recipe", MrmpTopics.DeleteRecipe("R1"));
            Assert.AreEqual("mrmp/R1/export/all_recipes", MrmpTopics.ExportAllRecipes("R1"));
            Assert.AreEqual("mrmp/R1/import/recipes", MrmpTopics.ImportRecipes("R1"));
        }

        [TestMethod]
        public void TryParseRobotId_ValidReportTopic_ReturnsRobotId()
        {
            Assert.IsTrue(MrmpTopics.TryParseRobotId("mrmp/R1/report/robot/status", out string robotId));
            Assert.AreEqual("R1", robotId);
        }

        [TestMethod]
        public void TryParseRobotId_RegistrationAndEmergency_ReturnsFalse()
        {
            Assert.IsFalse(MrmpTopics.TryParseRobotId("mrmp/registration", out _));
            Assert.IsFalse(MrmpTopics.TryParseRobotId("mrmp/emergency", out _));
        }

        [TestMethod]
        public void TryParseRobotId_InvalidInputs_ReturnsFalse()
        {
            Assert.IsFalse(MrmpTopics.TryParseRobotId(null!, out _));
            Assert.IsFalse(MrmpTopics.TryParseRobotId("", out _));
            Assert.IsFalse(MrmpTopics.TryParseRobotId("   ", out _));
            Assert.IsFalse(MrmpTopics.TryParseRobotId("other/R1/status", out _));
            Assert.IsFalse(MrmpTopics.TryParseRobotId("mrmp", out _));
        }

        [TestMethod]
        public void TryParseRobotId_CaseInsensitivePrefix()
        {
            Assert.IsTrue(MrmpTopics.TryParseRobotId("MRMP/Agv01/get/map", out string robotId));
            Assert.AreEqual("Agv01", robotId);
        }

        [TestMethod]
        public void TryParseRobotId_DeepTopic_StillGetsSecondSegment()
        {
            Assert.IsTrue(MrmpTopics.TryParseRobotId("mrmp/RobotX/report/mission/status", out string robotId));
            Assert.AreEqual("RobotX", robotId);
        }

        [TestMethod]
        public void TopicBuilders_EmptyRobotId_StillFormats()
        {
            Assert.AreEqual("mrmp//report/robot/status", MrmpTopics.ReportRobotStatus(""));
            Assert.AreEqual("mrmp//#", MrmpTopics.RobotWildcard(""));
        }
    }
}
