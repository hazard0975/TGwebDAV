using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TelegramWebDAV.Models;
using TelegramWebDAV.Services;

namespace TelegramWebDAV.Database
{
    public class NodeRepository
    {
        private readonly DatabaseManager _dbManager;

        public NodeRepository(DatabaseManager dbManager)
        {
            _dbManager = dbManager;
        }

        /// <summary>
        /// Возвращает корневую директорию (диск).
        /// </summary>
        public Node? GetRootNode()
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM nodes WHERE parent_id IS NULL AND name = 'Root' LIMIT 1;";
                return ReadNode(command);
            }
        }

        /// <summary>
        /// Выполняет поиск узла по пути, например "/Music/Track.mp3"
        /// </summary>
        public Node? GetNodeByPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return GetRootNode();

            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Node? currentNode = GetRootNode();

            foreach (var part in parts)
            {
                if (currentNode == null) return null;
                
                bool isTrash = currentNode.Name == ".Trash";

                using (var connection = _dbManager.GetConnection())
                using (var command = connection.CreateCommand())
                {
                    // Ищем дочерний элемент по имени. Если мы в корзине, ищем с is_deleted = 1, иначе with is_deleted = 0
                    if (isTrash)
                    {
                        command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = 1 LIMIT 1;";
                    }
                    else
                    {
                        command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = 0 LIMIT 1;";
                    }
                    command.Parameters.AddWithValue("@parentId", currentNode.Id);
                    command.Parameters.AddWithValue("@name", part);
                    
                    currentNode = ReadNode(command);
                }
            }

            return currentNode;
        }

        /// <summary>
        /// Получает всех потомков (папки и файлы) для указанной директории
        /// </summary>
        public List<Node> GetChildren(int parentId)
        {
            var children = new List<Node>();
            bool isTrash = IsTrashFolder(parentId);

            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                if (isTrash)
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND is_deleted = 1;";
                }
                else
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND is_deleted = 0;";
                }
                command.Parameters.AddWithValue("@parentId", parentId);
                
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        children.Add(MapReaderToNode(reader));
                    }
                }
            }
            return children;
        }

        /// <summary>
        /// Создает новую папку.
        /// </summary>
        public bool CreateFolder(int parentId, string name)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                // Проверяем, нет ли уже такого узла
                command.CommandText = "SELECT COUNT(*) FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = 0;";
                command.Parameters.AddWithValue("@parentId", parentId);
                command.Parameters.AddWithValue("@name", name);
                long count = Convert.ToInt64(command.ExecuteScalar() ?? 0);
                if (count > 0) return false; // Уже существует

                command.CommandText = "INSERT INTO nodes (parent_id, name, is_dir) VALUES (@parentId, @name, 1);";
                command.ExecuteNonQuery();
                return true;
            }
        }

        /// <summary>
        /// Мягкое удаление (перемещение в корзину)
        /// </summary>
        public void SoftDeleteNode(int nodeId)
        {
            var trashFolder = EnsureTrashFolder();
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE nodes SET is_deleted = 1, parent_id = @trashId, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
                command.Parameters.AddWithValue("@trashId", trashFolder.Id);
                command.Parameters.AddWithValue("@nodeId", nodeId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Проверяет, является ли папка корзиной (.Trash)
        /// </summary>
        public bool IsTrashFolder(int nodeId)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM nodes WHERE id = @nodeId LIMIT 1;";
                command.Parameters.AddWithValue("@nodeId", nodeId);
                string? name = command.ExecuteScalar() as string;
                return name == ".Trash";
            }
        }

        /// <summary>
        /// Одиночное перманентное удаление из базы данных
        /// </summary>
        public void PermanentDeleteNode(int nodeId)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM nodes WHERE id = @nodeId;";
                command.Parameters.AddWithValue("@nodeId", nodeId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Пакетное транзакционное удаление из базы данных для высокой производительности
        /// </summary>
        public void PermanentDeleteNodes(List<int> nodeIds)
        {
            if (nodeIds == null || nodeIds.Count == 0) return;

            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM nodes WHERE id = @id;";
                        var idParam = command.Parameters.Add("@id", SqliteType.Integer);

                        foreach (var id in nodeIds)
                        {
                            idParam.Value = id;
                            command.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        /// <summary>
        /// Перемещение / Переименование
        /// </summary>
        public void MoveNode(int nodeId, int newParentId, string newName)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE nodes SET parent_id = @newParentId, name = @newName, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
                command.Parameters.AddWithValue("@newParentId", newParentId);
                command.Parameters.AddWithValue("@newName", newName);
                command.Parameters.AddWithValue("@nodeId", nodeId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Создание или перезапись файла с поддержкой версионирования и метаданных
        /// </summary>
        public void CreateOrUpdateFile(int parentId, string name, long size, int? tgMessageId, AudioMetadataResult? metadata = null)
        {
            using (var connection = _dbManager.GetConnection())
            {
                Node? existingNode = null;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = 0 LIMIT 1;";
                    command.Parameters.AddWithValue("@parentId", parentId);
                    command.Parameters.AddWithValue("@name", name);
                    existingNode = ReadNode(command);
                }

                if (existingNode != null)
                {
                    // Версионирование: Отправляем старый в корзину, создаем новый с версией + 1
                    var trashFolder = EnsureTrashFolder();
                    
                    using (var transaction = connection.BeginTransaction())
                    {
                        using (var updateCmd = connection.CreateCommand())
                        {
                            updateCmd.Transaction = transaction;
                            // Имя в корзине делаем с пометкой версии, чтобы избежать коллизий
                            string trashName;
                            if (existingNode.IsDir)
                            {
                                trashName = $"{existingNode.Name}_v{existingNode.Version}";
                            }
                            else
                            {
                                string ext = System.IO.Path.GetExtension(existingNode.Name);
                                string nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(existingNode.Name);
                                trashName = $"{nameWithoutExt}_v{existingNode.Version}{ext}";
                            }
                            updateCmd.CommandText = "UPDATE nodes SET is_deleted = 1, parent_id = @trashId, name = @trashName WHERE id = @nodeId;";
                            updateCmd.Parameters.AddWithValue("@trashId", trashFolder.Id);
                            updateCmd.Parameters.AddWithValue("@trashName", trashName);
                            updateCmd.Parameters.AddWithValue("@nodeId", existingNode.Id);
                            updateCmd.ExecuteNonQuery();
                        }

                        using (var insertCmd = connection.CreateCommand())
                        {
                            insertCmd.Transaction = transaction;
                            insertCmd.CommandText = @"
                                INSERT INTO nodes (
                                    parent_id, name, is_dir, size, version, original_node_id, tg_message_id,
                                    artist, title, album, year, genre, track_number, duration_seconds, bitrate,
                                    header_cache_bytes, album_cover_bytes
                                ) VALUES (
                                    @parentId, @name, 0, @size, @version, @originalId, @tgMessageId,
                                    @artist, @title, @album, @year, @genre, @trackNumber, @duration, @bitrate,
                                    @headerCache, @albumCover
                                );";
                            insertCmd.Parameters.AddWithValue("@parentId", parentId);
                            insertCmd.Parameters.AddWithValue("@name", name);
                            insertCmd.Parameters.AddWithValue("@size", size);
                            insertCmd.Parameters.AddWithValue("@version", existingNode.Version + 1);
                            insertCmd.Parameters.AddWithValue("@originalId", existingNode.Id);
                            insertCmd.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                            
                            AddMetadataParameters(insertCmd, metadata);
                            insertCmd.ExecuteNonQuery();
                        }

                        // Удаляем старый прогресс докачки для перезаписываемого файла
                        using (var clearCmd = connection.CreateCommand())
                        {
                            clearCmd.Transaction = transaction;
                            clearCmd.CommandText = "DELETE FROM upload_progress WHERE node_id = @nodeId;";
                            clearCmd.Parameters.AddWithValue("@nodeId", existingNode.Id);
                            clearCmd.ExecuteNonQuery();
                        }
                        
                        transaction.Commit();
                    }
                }
                else
                {
                    // Просто создаем новый файл
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            INSERT INTO nodes (
                                parent_id, name, is_dir, size, version, tg_message_id,
                                artist, title, album, year, genre, track_number, duration_seconds, bitrate,
                                header_cache_bytes, album_cover_bytes
                            ) VALUES (
                                @parentId, @name, 0, @size, 1, @tgMessageId,
                                @artist, @title, @album, @year, @genre, @trackNumber, @duration, @bitrate,
                                @headerCache, @albumCover
                            );";
                        command.Parameters.AddWithValue("@parentId", parentId);
                        command.Parameters.AddWithValue("@name", name);
                        command.Parameters.AddWithValue("@size", size);
                        command.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                        
                        AddMetadataParameters(command, metadata);
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private void AddMetadataParameters(SqliteCommand command, AudioMetadataResult? metadata)
        {
            command.Parameters.AddWithValue("@artist", (object?)metadata?.Artist ?? DBNull.Value);
            command.Parameters.AddWithValue("@title", (object?)metadata?.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("@album", (object?)metadata?.Album ?? DBNull.Value);
            command.Parameters.AddWithValue("@year", (object?)metadata?.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("@genre", (object?)metadata?.Genre ?? DBNull.Value);
            command.Parameters.AddWithValue("@trackNumber", (object?)metadata?.TrackNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@duration", (object?)metadata?.DurationSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("@bitrate", (object?)metadata?.Bitrate ?? DBNull.Value);
            command.Parameters.AddWithValue("@headerCache", (object?)metadata?.HeaderCache ?? DBNull.Value);
            command.Parameters.AddWithValue("@albumCover", (object?)metadata?.AlbumCover ?? DBNull.Value);
        }

        /// <summary>
        /// Обновляет аудио-метаданные для существующего узла
        /// </summary>
        public void UpdateAudioMetadata(int nodeId, AudioMetadataResult metadata)
        {
            if (metadata == null) return;
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    UPDATE nodes SET 
                        artist = @artist, title = @title, album = @album, year = @year,
                        genre = @genre, track_number = @trackNumber, duration_seconds = @duration,
                        bitrate = @bitrate, header_cache_bytes = COALESCE(@headerCache, header_cache_bytes),
                        album_cover_bytes = COALESCE(@albumCover, album_cover_bytes),
                        updated_at = CURRENT_TIMESTAMP
                    WHERE id = @nodeId;";
                command.Parameters.AddWithValue("@nodeId", nodeId);
                AddMetadataParameters(command, metadata);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Обновляет прогресс частичной загрузки (для умного софта с Content-Range в PUT).
        /// Если загрузка завершена (передан tgMessageId или достигнут totalSize), очищает временный прогресс.
        /// </summary>
        public void UpdateUploadProgress(int parentId, string name, long chunkPosition, long totalSize, int? tgMessageId)
        {
            using (var connection = _dbManager.GetConnection())
            {
                Node? node = null;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = 0 LIMIT 1;";
                    command.Parameters.AddWithValue("@parentId", parentId);
                    command.Parameters.AddWithValue("@name", name);
                    node = ReadNode(command);
                }

                if (node == null)
                {
                    CreateOrUpdateFile(parentId, name, totalSize, tgMessageId);
                    return;
                }

                bool isCompleted = tgMessageId.HasValue || (chunkPosition >= totalSize);

                using (var command = connection.CreateCommand())
                {
                    if (isCompleted)
                    {
                        // При успешном завершении удаляем временный журнал докачки и обновляем финальный узел
                        command.CommandText = @"
                            DELETE FROM upload_progress WHERE node_id = @nodeId;
                            UPDATE nodes SET size = @totalSize, tg_message_id = COALESCE(@tgMessageId, tg_message_id) 
                            WHERE id = @nodeId;";
                    }
                    else
                    {
                        command.CommandText = @"
                            INSERT INTO upload_progress (node_id, chunk_position) 
                            VALUES (@nodeId, @chunkPosition);
                            UPDATE nodes SET size = @totalSize, tg_message_id = COALESCE(@tgMessageId, tg_message_id) 
                            WHERE id = @nodeId;";
                    }
                    command.Parameters.AddWithValue("@nodeId", node.Id);
                    command.Parameters.AddWithValue("@chunkPosition", chunkPosition);
                    command.Parameters.AddWithValue("@totalSize", totalSize);
                    command.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Очищает временный журнал загрузки для указанного узла
        /// </summary>
        public void ClearUploadProgress(int nodeId)
        {
            using (var connection = _dbManager.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM upload_progress WHERE node_id = @nodeId;";
                    command.Parameters.AddWithValue("@nodeId", nodeId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private Node EnsureTrashFolder()
        {
            var root = GetRootNode() ?? throw new InvalidOperationException("Root node not found in database.");
            using (var connection = _dbManager.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @rootId AND name = '.Trash' LIMIT 1;";
                    command.Parameters.AddWithValue("@rootId", root.Id);
                    var trashNode = ReadNode(command);
                    if (trashNode != null) return trashNode;
                }

                using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.CommandText = "INSERT INTO nodes (parent_id, name, is_dir) VALUES (@rootId, '.Trash', 1);";
                    insertCmd.Parameters.AddWithValue("@rootId", root.Id);
                    insertCmd.ExecuteNonQuery();
                }

                using (var getCmd = connection.CreateCommand())
                {
                    getCmd.CommandText = "SELECT * FROM nodes WHERE parent_id = @rootId AND name = '.Trash' LIMIT 1;";
                    getCmd.Parameters.AddWithValue("@rootId", root.Id);
                    return ReadNode(getCmd) ?? throw new InvalidOperationException("Failed to retrieve created .Trash folder.");
                }
            }
        }

        private Node? ReadNode(SqliteCommand command)
        {
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    return MapReaderToNode(reader);
                }
            }
            return null;
        }

        private Node MapReaderToNode(SqliteDataReader reader)
        {
            var node = new Node
            {
                Id = Convert.ToInt32(reader["id"]),
                ParentId = reader["parent_id"] != DBNull.Value ? Convert.ToInt32(reader["parent_id"]) : (int?)null,
                Name = Convert.ToString(reader["name"]) ?? string.Empty,
                IsDir = Convert.ToInt32(reader["is_dir"]) == 1,
                Size = Convert.ToInt64(reader["size"]),
                CreatedAt = Convert.ToDateTime(reader["created_at"]),
                UpdatedAt = Convert.ToDateTime(reader["updated_at"]),
                TgMessageId = reader["tg_message_id"] != DBNull.Value ? Convert.ToInt32(reader["tg_message_id"]) : (int?)null,
                Version = Convert.ToInt32(reader["version"]),
                IsDeleted = Convert.ToInt32(reader["is_deleted"]) == 1
            };

            // Чтение аудио-метаданных, если столбцы присутствуют
            try
            {
                if (reader["artist"] != DBNull.Value) node.Artist = Convert.ToString(reader["artist"]);
                if (reader["title"] != DBNull.Value) node.Title = Convert.ToString(reader["title"]);
                if (reader["album"] != DBNull.Value) node.Album = Convert.ToString(reader["album"]);
                if (reader["year"] != DBNull.Value) node.Year = Convert.ToInt32(reader["year"]);
                if (reader["genre"] != DBNull.Value) node.Genre = Convert.ToString(reader["genre"]);
                if (reader["track_number"] != DBNull.Value) node.TrackNumber = Convert.ToInt32(reader["track_number"]);
                if (reader["duration_seconds"] != DBNull.Value) node.DurationSeconds = Convert.ToInt32(reader["duration_seconds"]);
                if (reader["bitrate"] != DBNull.Value) node.Bitrate = Convert.ToInt32(reader["bitrate"]);
                if (reader["header_cache_bytes"] != DBNull.Value) node.HeaderCacheBytes = (byte[])reader["header_cache_bytes"];
                if (reader["album_cover_bytes"] != DBNull.Value) node.AlbumCoverBytes = (byte[])reader["album_cover_bytes"];
            }
            catch
            {
                // Игнорируем отсутствие колонок при частичных SELECT
            }

            return node;
        }
    }
}
