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

        private void ApplySchema(SqliteConnection connection)
        {
            string schemaPath = GetSchemaFilePath();
            if (!File.Exists(schemaPath))
            {
                throw new FileNotFoundException($"Файл схемы базы данных Schema.sql не найден по пути: {schemaPath}");
            }

            string schemaSql = File.ReadAllText(schemaPath);
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
