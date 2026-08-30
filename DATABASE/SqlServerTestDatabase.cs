using Microsoft.Data.SqlClient;

namespace AGVSystemTests.DATABASE
{
    /// <summary>
    /// 為單元測試建立臨時 SQL Server 資料庫，Dispose 時強制刪除。
    /// </summary>
    public sealed class SqlServerTestDatabase : IDisposable
    {
        public const string DefaultServer = "127.0.0.1";
        public const string DefaultUser = "sa";
        public const string DefaultPassword = "12345678";

        /// <summary>
        /// CI 可透過環境變數覆寫測試用 SQL Server 連線設定。
        /// - AGVS_TEST_SQL_SERVER
        /// - AGVS_TEST_SQL_USER
        /// - AGVS_TEST_SQL_PASSWORD（或 MSSQL_SA_PASSWORD）
        /// </summary>
        private static string Server => Environment.GetEnvironmentVariable("AGVS_TEST_SQL_SERVER") ?? DefaultServer;
        private static string User => Environment.GetEnvironmentVariable("AGVS_TEST_SQL_USER") ?? DefaultUser;
        private static string Password =>
            Environment.GetEnvironmentVariable("AGVS_TEST_SQL_PASSWORD")
            ?? Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD")
            ?? DefaultPassword;

        public string DatabaseName { get; }
        public string MasterConnectionString { get; }
        public string ConnectionString { get; }

        private bool _disposed;

        public SqlServerTestDatabase(string? databaseName = null)
        {
            DatabaseName = databaseName ?? $"AGVS_UT_{Guid.NewGuid():N}";
            MasterConnectionString = BuildConnectionString("master");
            ConnectionString = BuildConnectionString(DatabaseName);
            CreateDatabase();
        }

        public static string BuildConnectionString(string databaseName)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = databaseName,
                UserID = User,
                Password = Password,
                Encrypt = false,
                TrustServerCertificate = true,
                MultipleActiveResultSets = true,
                ConnectTimeout = 15
            };
            return builder.ConnectionString;
        }

        public bool CanConnect()
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        public bool DatabaseExists()
        {
            using var connection = new SqlConnection(MasterConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM sys.databases WHERE name = @name";
            command.Parameters.AddWithValue("@name", DatabaseName);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        public bool TableExists(string tableName)
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NOT NULL THEN 1 ELSE 0 END";
            command.Parameters.AddWithValue("@table", tableName);
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        public bool HasPrimaryKey(string tableName)
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(1)
                FROM sys.key_constraints
                WHERE type = 'PK' AND OBJECT_NAME(parent_object_id) = @table";
            command.Parameters.AddWithValue("@table", tableName);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        public string? GetPrimaryKeyColumn(string tableName)
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT c.name
                FROM sys.key_constraints kc
                JOIN sys.index_columns ic ON kc.parent_object_id = ic.object_id AND kc.unique_index_id = ic.index_id
                JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE kc.type = 'PK' AND OBJECT_NAME(kc.parent_object_id) = @table";
            command.Parameters.AddWithValue("@table", tableName);
            return command.ExecuteScalar()?.ToString();
        }

        public bool IndexExists(string indexName)
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM sys.indexes WHERE name = @name";
            command.Parameters.AddWithValue("@name", indexName);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        public List<string> ListUserTables()
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT name FROM sys.tables
                WHERE type = 'U'
                ORDER BY name";
            var tables = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
            return tables;
        }

        private void CreateDatabase()
        {
            using var connection = new SqlConnection(MasterConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{DatabaseName}]";
            command.ExecuteNonQuery();
        }

        private void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            using var connection = new SqlConnection(MasterConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $@"
IF DB_ID(N'{DatabaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{DatabaseName}];
END";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                DropDatabase();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SqlServerTestDatabase] Drop failed: {ex.Message}");
            }
        }
    }
}
