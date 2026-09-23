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
        /// Инициализация БД при запуске (разворачивание схемы при отсутствии таблиц или миграция полей)
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

                // Гарантируем наличие всех необходимых столбцов (миграция старых БД)
                EnsureColumnsExist(connection);

                // Очистка ошибочных 0-байтовых фантомов в корзине (созданных Проводником Windows до исправления)
                CleanupCorruptedTrashPlaceholders(connection);
            }
        }

        private void CleanupCorruptedTrashPlaceholders(SqliteConnection connection)
        {
            try
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM nodes WHERE is_deleted = 1 AND size = 0 AND tg_message_id IS NULL AND is_dir = 0;";
                    int cleaned = cmd.ExecuteNonQuery();
                    if (cleaned > 0)
                    {
                        Console.WriteLine($"[DatabaseManager] Очищено {cleaned} 0-байтовых фантомных файлов из корзины.");
                    }
                }
            }
            catch { }
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
    parent_id INTEGER,
    name TEXT NOT NULL,
    is_dir INTEGER NOT NULL DEFAULT 0,
    size INTEGER NOT NULL DEFAULT 0,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    
    tg_message_id INTEGER,
    
    version INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    original_node_id INTEGER,
    
    artist TEXT,
    title TEXT,
    album TEXT,
    year INTEGER,
    genre TEXT,
    track_number INTEGER,
    duration_seconds INTEGER,
    bitrate INTEGER,
    
    header_cache_bytes BLOB,
    album_cover_bytes BLOB,
    
    FOREIGN KEY (parent_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (original_node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS upload_progress (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    node_id INTEGER NOT NULL,
    chunk_position INTEGER NOT NULL DEFAULT 0,
    file_hash TEXT,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_nodes_parent_id ON nodes(parent_id);
CREATE INDEX IF NOT EXISTS idx_nodes_name ON nodes(name);
CREATE INDEX IF NOT EXISTS idx_nodes_is_deleted ON nodes(is_deleted);
CREATE INDEX IF NOT EXISTS idx_upload_progress_node_id ON upload_progress(node_id);
";

        private void EnsureColumnsExist(SqliteConnection connection)
        {
            var existingColumns = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(nodes);";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader["name"] != DBNull.Value)
                        {
                            existingColumns.Add(reader["name"].ToString()!);
                        }
                    }
                }
            }

            void AddColumnIfMissing(string columnName, string columnDefinition)
            {
                if (!existingColumns.Contains(columnName))
                {
                    using (var alterCmd = connection.CreateCommand())
                    {
                        alterCmd.CommandText = $"ALTER TABLE nodes ADD COLUMN {columnName} {columnDefinition};";
                        alterCmd.ExecuteNonQuery();
                    }
                }
            }

            AddColumnIfMissing("version", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing("is_deleted", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("original_node_id", "INTEGER");
            AddColumnIfMissing("artist", "TEXT");
            AddColumnIfMissing("title", "TEXT");
            AddColumnIfMissing("album", "TEXT");
            AddColumnIfMissing("year", "INTEGER");
            AddColumnIfMissing("genre", "TEXT");
            AddColumnIfMissing("track_number", "INTEGER");
            AddColumnIfMissing("duration_seconds", "INTEGER");
            AddColumnIfMissing("bitrate", "INTEGER");
            AddColumnIfMissing("header_cache_bytes", "BLOB");
            AddColumnIfMissing("album_cover_bytes", "BLOB");
        }

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
