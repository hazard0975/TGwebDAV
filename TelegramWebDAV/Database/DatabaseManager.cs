using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TelegramWebDAV.Services;

namespace TelegramWebDAV.Database
{
    public class DatabaseManager
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public DatabaseManager(string? dbPath = null)
        {
            string path = string.IsNullOrWhiteSpace(dbPath) ? "base.db" : dbPath;
            _dbPath = Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            _connectionString = $"Data Source={_dbPath};";
        }

        /// <summary>
        /// Инициализация БД при запуске (разворачивание чистой схемы при отсутствии таблиц и создание системных папок)
        /// </summary>
        public void InitializeDatabase()
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();

                // Включаем WAL режим для конкурентного доступа
                EnableWalMode(connection);

                // Всегда применяем схему (все команды содержат CREATE TABLE/INDEX IF NOT EXISTS)
                ApplySchema(connection);
                EnsureSchemaColumns(connection);
                SeedSystemFolders(connection);
            }
        }

        private void EnsureSchemaColumns(SqliteConnection connection)
        {
            try
            {
                // Проверяем наличие колонки tg_channel_id в nodes
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(nodes);";
                using var reader = cmd.ExecuteReader();
                bool hasTgChannelId = false;
                while (reader.Read())
                {
                    string colName = reader["name"]?.ToString() ?? "";
                    if (colName.Equals("tg_channel_id", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTgChannelId = true;
                        break;
                    }
                }
                reader.Close();

                if (!hasTgChannelId)
                {
                    using var alterCmd = connection.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE nodes ADD COLUMN tg_channel_id INTEGER;";
                    alterCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("Database", $"Проверка схемы колонок: {ex.Message}");
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

-- Таблица аккаунтов / профилей Telegram
CREATE TABLE IF NOT EXISTS telegram_accounts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    api_id INTEGER NOT NULL DEFAULT 0,
    api_hash TEXT NOT NULL DEFAULT '',
    phone_number TEXT,
    session_path TEXT NOT NULL DEFAULT 'user.session',
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

-- Таблица подключенных каналов-хранилищ Telegram
CREATE TABLE IF NOT EXISTS telegram_channels (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL UNIQUE,
    access_hash INTEGER NOT NULL,
    title TEXT NOT NULL,
    is_primary INTEGER NOT NULL DEFAULT 1,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (account_id) REFERENCES telegram_accounts(id) ON DELETE CASCADE
);

-- Таблица узлов виртуальной файловой системы (Файлы и Папки)
CREATE TABLE IF NOT EXISTS nodes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER,
    original_node_id INTEGER,
    name TEXT NOT NULL,
    is_dir INTEGER NOT NULL DEFAULT 0,
    version INTEGER NOT NULL DEFAULT 1,
    in_trash INTEGER NOT NULL DEFAULT 0,
    tg_message_id INTEGER,
    tg_preview_message_id INTEGER,
    tg_channel_id INTEGER,
    size INTEGER NOT NULL DEFAULT 0,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    inline_data BLOB,
    
    FOREIGN KEY (parent_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (original_node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

-- Таблица трекинга незавершенных загрузок (для докачки при обрывах)
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
CREATE INDEX IF NOT EXISTS idx_nodes_in_trash ON nodes(in_trash);
CREATE INDEX IF NOT EXISTS idx_upload_progress_node_id ON upload_progress(node_id);

CREATE TABLE IF NOT EXISTS pending_caption_updates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    node_id INTEGER NOT NULL,
    tg_message_id INTEGER NOT NULL UNIQUE,
    new_caption TEXT NOT NULL,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    status INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_pending_caption_tg_msg ON pending_caption_updates(tg_message_id);

CREATE TABLE IF NOT EXISTS pending_deletions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tg_message_id INTEGER NOT NULL,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_pending_deletions_tg_msg ON pending_deletions(tg_message_id);
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

        private void SeedSystemFolders(SqliteConnection connection)
        {
            // 1. Создаем корневую директорию 'Root' (ID = 1), если её нет
            using (var rootCmd = connection.CreateCommand())
            {
                rootCmd.CommandText = @"
                    INSERT INTO nodes (parent_id, name, is_dir)
                    SELECT NULL, 'Root', 1
                    WHERE NOT EXISTS (SELECT 1 FROM nodes WHERE parent_id IS NULL AND name = 'Root');
                ";
                rootCmd.ExecuteNonQuery();
            }

            // 2. Создаем системную корзину '.Trash' (ID = 2) в корневом каталоге, если её нет
            using (var trashCmd = connection.CreateCommand())
            {
                trashCmd.CommandText = @"
                    INSERT INTO nodes (parent_id, name, is_dir)
                    SELECT (SELECT id FROM nodes WHERE parent_id IS NULL AND name = 'Root' LIMIT 1), '.Trash', 1
                    WHERE NOT EXISTS (
                        SELECT 1 FROM nodes 
                        WHERE parent_id = (SELECT id FROM nodes WHERE parent_id IS NULL AND name = 'Root' LIMIT 1) 
                          AND name = '.Trash'
                    );
                ";
                trashCmd.ExecuteNonQuery();
            }
        }

        public string DatabasePath => _dbPath;

        private static readonly object _vacuumLock = new object();
        private static System.Threading.Timer? _debounceVacuumTimer;

        /// <summary>
        /// Выполняет дефрагментацию и оптимизацию базы данных (VACUUM), освобождая дисковое пространство.
        /// </summary>
        public void VacuumDatabase()
        {
            lock (_vacuumLock)
            {
                try
                {
                    AppLogger.Info("Database", "Запуск оптимизации базы данных SQLite (VACUUM)...");
                    using var connection = GetConnection();
                    using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM;";
                    command.ExecuteNonQuery();
                    AppLogger.Info("Database", "Оптимизация базы данных (VACUUM) успешно завершена.");
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Database", $"Ошибка при выполнении VACUUM: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Отложенный запуск VACUUM с дебаунсом (сжатие выполняется один раз после завершения пачки удалений).
        /// </summary>
        public void ScheduleVacuum(int delayMs = 3000)
        {
            lock (_vacuumLock)
            {
                _debounceVacuumTimer?.Dispose();
                _debounceVacuumTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        VacuumDatabase();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("Database", $"Фоновый отложенный VACUUM завершился с ошибкой: {ex.Message}");
                    }
                }, null, delayMs, System.Threading.Timeout.Infinite);
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
