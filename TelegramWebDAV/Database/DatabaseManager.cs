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
        /// Инициализация чистой БД при первом запуске (разворачивание схемы)
        /// </summary>
        public void InitializeDatabase()
        {
            bool isNewDatabase = !File.Exists(_dbPath);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();

                // Включаем WAL режим для конкурентного доступа
                EnableWalMode(connection);

                if (isNewDatabase)
                {
                    Console.WriteLine("Создание чистой базы данных SQLite (base.db)...");
                    ApplySchema(connection);
                    SeedRootFolder(connection);
                    Console.WriteLine("База данных успешно инициализирована.");
                }
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
            string schemaSql = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Database", "Schema.sql"));
            
            using (var command = connection.CreateCommand())
            {
                command.CommandText = schemaSql;
                command.ExecuteNonQuery();
            }
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
