using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TelegramWebDAV.Database
{
    public class DatabaseManager
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public DatabaseManager(string dbPath = "base.db")
        {
            _dbPath = dbPath;
            _connectionString = $"Data Source={_dbPath};";
        }

        /// <summary>
        /// Инициализация БД при запуске (разворачивание схемы при отсутствии таблиц)
        /// </summary>
        public void InitializeDatabase()
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();

                // Включаем WAL режим для конкурентного доступа
                EnableWalMode(connection);

                if (!HasNodesTable(connection))
                {
                    Console.WriteLine("Создание структуры базы данных SQLite (base.db)...");
                    ApplySchema(connection);
                    SeedRootFolder(connection);
                    Console.WriteLine("База данных успешно инициализирована.");
                }
            }
        }

        private bool HasNodesTable(SqliteConnection connection)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='nodes';";
                    var result = command.ExecuteScalar();
                    return result != null && Convert.ToInt32(result) > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void EnableWalMode(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL;";
                command.ExecuteNonQuery();
            }
        }

        private const string EmbeddedFallbackSchema = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS nodes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER NULL,
    name TEXT NOT NULL,
    is_dir INTEGER NOT NULL,
    tg_message_id INTEGER NULL,
    size INTEGER DEFAULT 0,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
    title TEXT NULL,
    artist TEXT NULL,
    album TEXT NULL,
    duration INTEGER DEFAULT 0,
    cover_mime TEXT NULL,
    header_cache_bytes BLOB NULL,
    version_count INTEGER DEFAULT 1,
    FOREIGN KEY (parent_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS versions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    node_id INTEGER NOT NULL,
    version_num INTEGER NOT NULL,
    tg_message_id INTEGER NOT NULL,
    size INTEGER DEFAULT 0,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    header_cache_bytes BLOB NULL,
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS upload_progress (
    node_id INTEGER PRIMARY KEY,
    bytes_uploaded INTEGER DEFAULT 0,
    total_size INTEGER DEFAULT 0,
    temp_tg_location TEXT NULL,
    updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_nodes_parent_id ON nodes(parent_id);
CREATE INDEX IF NOT EXISTS idx_nodes_name ON nodes(name);
CREATE INDEX IF NOT EXISTS idx_versions_node_id ON versions(node_id);
";

        private void ApplySchema(SqliteConnection connection)
        {
            string schemaSql;
            string schemaPath = GetSchemaFilePath();
            
            if (File.Exists(schemaPath))
            {
                schemaSql = File.ReadAllText(schemaPath);
            }
            else
            {
                schemaSql = EmbeddedFallbackSchema;
            }

            var statements = schemaSql.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawStatement in statements)
            {
                string statement = rawStatement.Trim();
                if (string.IsNullOrWhiteSpace(statement)) continue;

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }
            }
        }

        private string GetSchemaFilePath()
        {
            string path1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Database", "Schema.sql");
            if (File.Exists(path1)) return path1;

            string path2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Schema.sql");
            if (File.Exists(path2)) return path2;

            string path3 = Path.Combine(Directory.GetCurrentDirectory(), "Database", "Schema.sql");
            if (File.Exists(path3)) return path3;

            string path4 = Path.Combine(Directory.GetCurrentDirectory(), "TelegramWebDAV", "Database", "Schema.sql");
            if (File.Exists(path4)) return path4;

            return path1;
        }

        private void SeedRootFolder(SqliteConnection connection)
        {
            // Создаем корневую директорию, если её нет
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    INSERT INTO nodes (parent_id, name, is_dir)
                    SELECT NULL, 'Root', 1
                    WHERE NOT EXISTS (SELECT 1 FROM nodes WHERE parent_id IS NULL AND name = 'Root');
                ";
                command.ExecuteNonQuery();
            }
        }

        public SqliteConnection GetConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }
    }
}
