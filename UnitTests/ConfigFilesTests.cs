using System.Text.Json;

namespace AGVSystemTests.UnitTests;

[TestClass]
public sealed class ConfigFilesTests
{
    [DataTestMethod]
    [DataRow("SystemConfigs.json")]
    [DataRow("SystemConfigs-SomeThingChange.json")]
    public void ConfigFiles_AreValidJson_AndContainCoreKeys(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.IsTrue(File.Exists(path), $"Expected config file copied to output: {path}");

        string json = File.ReadAllText(path);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);

        Assert.IsTrue(doc.RootElement.TryGetProperty("FieldName", out JsonElement fieldName));
        Assert.IsFalse(string.IsNullOrWhiteSpace(fieldName.GetString()));

        Assert.IsTrue(doc.RootElement.TryGetProperty("DBConnection", out JsonElement dbConnection));
        Assert.IsFalse(string.IsNullOrWhiteSpace(dbConnection.GetString()));

        Assert.IsTrue(doc.RootElement.TryGetProperty("MapConfigs", out JsonElement mapConfigs));
        Assert.AreEqual(JsonValueKind.Object, mapConfigs.ValueKind);

        Assert.IsTrue(mapConfigs.TryGetProperty("MapFolder", out JsonElement mapFolder));
        Assert.IsFalse(string.IsNullOrWhiteSpace(mapFolder.GetString()));
    }
}

