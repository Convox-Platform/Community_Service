using Dapper;
using Npgsql;

namespace Community_Service.Migration
{
    public class CreateDb
    {
        public static void CreateDatabase(string connectionString, string sqlSecretPassword)
        {
            string masterConnectionString = $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={sqlSecretPassword};";

            using (var connection = new NpgsqlConnection(masterConnectionString))
            {
                string checkDbSql = "SELECT COUNT(*) FROM pg_database WHERE datname = @DbName";
                var DbName = "Community_Service";
                bool dbExists = connection.ExecuteScalar<int>(checkDbSql, new { DbName }) > 0;

                if(!dbExists)
                {
                    string createDbSql = $"CREATE DATABASE \"{DbName}\"";
                    connection.Execute(createDbSql);
                }
            }
            using (var connection = new NpgsqlConnection(connectionString))
            {
                string checkTableSql = @"
        SELECT COUNT(*)
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name = LOWER(@TableName)";


                // Servers
                bool serversTableExists =
                    connection.ExecuteScalar<long>(
                        checkTableSql,
                        new { TableName = "Servers" }) > 0;

                if (!serversTableExists)
                {
                    string createServersSql = @"
            CREATE TABLE Servers
            (
                Id BIGINT PRIMARY KEY,
                Name VARCHAR(255) NULL,
                Description TEXT NULL,
                Image TEXT NULL,
                UsersId BIGINT[] NOT NULL DEFAULT ARRAY[]::BIGINT[]
            )";

                    connection.Execute(createServersSql);
                }


                // TextChannels
                bool textChannelsTableExists =
                    connection.ExecuteScalar<long>(
                        checkTableSql,
                        new { TableName = "TextChannels" }) > 0;

                if (!textChannelsTableExists)
                {
                    string createTextChannelsSql = @"
            CREATE TABLE TextChannels
            (
                Id BIGINT PRIMARY KEY,
                Name VARCHAR(255) NULL,
                Type VARCHAR(20) NOT NULL DEFAULT 'Text',
                ServerId BIGINT NOT NULL,

                CONSTRAINT FK_TextChannels_Servers
                    FOREIGN KEY (ServerId)
                    REFERENCES Servers(Id)
                    ON DELETE CASCADE,

                CONSTRAINT CK_TextChannels_Type
                    CHECK (Type = 'Text')
            )";

                    connection.Execute(createTextChannelsSql);
                }


                // VoiceChannels
                bool voiceChannelsTableExists =
                    connection.ExecuteScalar<long>(
                        checkTableSql,
                        new { TableName = "VoiceChannels" }) > 0;

                if (!voiceChannelsTableExists)
                {
                    string createVoiceChannelsSql = @"
            CREATE TABLE VoiceChannels
            (
                Id BIGINT PRIMARY KEY,
                Name VARCHAR(255) NULL,
                Type VARCHAR(20) NOT NULL DEFAULT 'Voice',
                ServerId BIGINT NOT NULL,

                CONSTRAINT FK_VoiceChannels_Servers
                    FOREIGN KEY (ServerId)
                    REFERENCES Servers(Id)
                    ON DELETE CASCADE,

                CONSTRAINT CK_VoiceChannels_Type
                    CHECK (Type = 'Voice')
            )";

                    connection.Execute(createVoiceChannelsSql);
                }
            }
        }
    }
}
