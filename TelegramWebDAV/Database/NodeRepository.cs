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
            bool insideTrash = false;

            foreach (var part in parts)
            {
                if (currentNode == null) return null;

                if (!insideTrash && part.Equals(".Trash", StringComparison.OrdinalIgnoreCase) && currentNode.ParentId == null)
                {
                    using (var connection = _dbManager.GetConnection())
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = 0 LIMIT 1;";
                        command.Parameters.AddWithValue("@parentId", currentNode.Id);
                        command.Parameters.AddWithValue("@name", part);
                        currentNode = ReadNode(command);
                    }
                    insideTrash = true;
                    continue;
                }

                int expectedDeleted = insideTrash ? 1 : 0;
                using (var connection = _dbManager.GetConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = @isDeleted LIMIT 1;";
                    command.Parameters.AddWithValue("@parentId", currentNode.Id);
                    command.Parameters.AddWithValue("@name", part);
                    command.Parameters.AddWithValue("@isDeleted", expectedDeleted);
                    currentNode = ReadNode(command);
                }
            }

            return currentNode;
        }

        /// <summary>
        /// Возвращает узел по ID.
        /// </summary>
        public Node? GetNodeById(int nodeId)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM nodes WHERE id = @nodeId LIMIT 1;";
                command.Parameters.AddWithValue("@nodeId", nodeId);
                return ReadNode(command);
            }
        }

        /// <summary>
        /// Проверяет, находится ли узел (или его родительская папка) в корзине (.Trash)
        /// </summary>
        public bool IsNodeInTrash(int nodeId)
        {
            var trash = EnsureTrashFolder();
            if (nodeId == trash.Id) return true;

            using (var connection = _dbManager.GetConnection())
            {
                int currentId = nodeId;
                while (currentId > 0)
                {
                    if (currentId == trash.Id) return true;
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT parent_id, is_deleted FROM nodes WHERE id = @id LIMIT 1;";
                        cmd.Parameters.AddWithValue("@id", currentId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read()) return false;
                            bool isDel = Convert.ToInt32(reader["is_deleted"]) == 1;
                            if (isDel) return true;
                            if (reader.IsDBNull(0)) return false;
                            currentId = Convert.ToInt32(reader["parent_id"]);
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Воспроизводит (зеркалирует) иерархию родительских папок внутри корзины (.Trash)
        /// и возвращает ID целевой папки в корзине.
        /// </summary>
        public int EnsureTrashHierarchyForParent(int originalParentId)
        {
            var root = GetRootNode();
            if (root == null || originalParentId == root.Id)
            {
                return EnsureTrashFolder().Id;
            }

            if (IsNodeInTrash(originalParentId))
            {
                return originalParentId;
            }

            var chain = new List<string>();
            using (var connection = _dbManager.GetConnection())
            {
                int currentId = originalParentId;
                while (currentId != root.Id)
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id, parent_id, name, is_dir FROM nodes WHERE id = @id LIMIT 1;";
                        cmd.Parameters.AddWithValue("@id", currentId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read()) break;
                            string name = reader["name"].ToString() ?? "";
                            if (!string.IsNullOrEmpty(name))
                            {
                                chain.Add(name);
                            }
                            if (reader.IsDBNull(1)) break;
                            currentId = Convert.ToInt32(reader["parent_id"]);
                        }
                    }
                }
            }

            chain.Reverse();

            var trash = EnsureTrashFolder();
            int currentTrashParentId = trash.Id;

            using (var connection = _dbManager.GetConnection())
            {
                foreach (var folderName in chain)
                {
                    int? foundId = null;
                    using (var searchCmd = connection.CreateCommand())
                    {
                        searchCmd.CommandText = "SELECT id FROM nodes WHERE parent_id = @parentId AND name = @name AND is_dir = 1 AND is_deleted = 1 LIMIT 1;";
                        searchCmd.Parameters.AddWithValue("@parentId", currentTrashParentId);
                        searchCmd.Parameters.AddWithValue("@name", folderName);
                        var scalar = searchCmd.ExecuteScalar();
                        if (scalar != null && scalar != DBNull.Value)
                        {
                            foundId = Convert.ToInt32(scalar);
                        }
                    }

                    if (foundId.HasValue)
                    {
                        currentTrashParentId = foundId.Value;
                    }
                    else
                    {
                        using (var insertCmd = connection.CreateCommand())
                        {
                            insertCmd.CommandText = "INSERT INTO nodes (parent_id, name, is_dir, is_deleted) VALUES (@parentId, @name, 1, 1); SELECT last_insert_rowid();";
                            insertCmd.Parameters.AddWithValue("@parentId", currentTrashParentId);
                            insertCmd.Parameters.AddWithValue("@name", folderName);
                            currentTrashParentId = Convert.ToInt32(insertCmd.ExecuteScalar());
                        }
                    }
                }
            }

            return currentTrashParentId;
        }

        /// <summary>
        /// Получает всех потомков (папки и файлы) для указанной директории
        /// </summary>
        public List<Node> GetChildren(int parentId)
        {
            var children = new List<Node>();
            bool isTrash = IsNodeInTrash(parentId);

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
        /// Мягкое удаление (перемещение в корзину с сохранением структуры каталогов)
        /// </summary>
        public void SoftDeleteNode(int nodeId)
        {
            var node = GetNodeById(nodeId);
            if (node == null) return;

            var root = GetRootNode();
            int originalParentId = node.ParentId ?? root?.Id ?? 1;

            int targetTrashParentId = EnsureTrashHierarchyForParent(originalParentId);

            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    if (node.IsDir)
                    {
                        // Проверяем, существует ли уже такая папка в целевой директории корзины
                        int? existingTrashFolderId = null;
                        using (var checkCmd = connection.CreateCommand())
                        {
                            checkCmd.Transaction = transaction;
                            checkCmd.CommandText = "SELECT id FROM nodes WHERE parent_id = @parentId AND name = @name AND is_dir = 1 AND is_deleted = 1 LIMIT 1;";
                            checkCmd.Parameters.AddWithValue("@parentId", targetTrashParentId);
                            checkCmd.Parameters.AddWithValue("@name", node.Name);
                            var scalar = checkCmd.ExecuteScalar();
                            if (scalar != null && scalar != DBNull.Value)
                            {
                                existingTrashFolderId = Convert.ToInt32(scalar);
                            }
                        }

                        if (existingTrashFolderId.HasValue)
                        {
                            // Если папка уже создана в корзине, переносим дочерние узлы в неё
                            using (var moveChildrenCmd = connection.CreateCommand())
                            {
                                moveChildrenCmd.Transaction = transaction;
                                moveChildrenCmd.CommandText = "UPDATE nodes SET parent_id = @existingId, is_deleted = 1, updated_at = CURRENT_TIMESTAMP WHERE parent_id = @nodeId;";
                                moveChildrenCmd.Parameters.AddWithValue("@existingId", existingTrashFolderId.Value);
                                moveChildrenCmd.Parameters.AddWithValue("@nodeId", nodeId);
                                moveChildrenCmd.ExecuteNonQuery();
                            }

                            UpdateChildrenDeletedStateRecursive(connection, transaction, existingTrashFolderId.Value, 1);

                            using (var deleteFolderCmd = connection.CreateCommand())
                            {
                                deleteFolderCmd.Transaction = transaction;
                                deleteFolderCmd.CommandText = "DELETE FROM nodes WHERE id = @nodeId;";
                                deleteFolderCmd.Parameters.AddWithValue("@nodeId", nodeId);
                                deleteFolderCmd.ExecuteNonQuery();
                            }
                        }
                        else
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.Transaction = transaction;
                                command.CommandText = "UPDATE nodes SET is_deleted = 1, parent_id = @trashParentId, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
                                command.Parameters.AddWithValue("@trashParentId", targetTrashParentId);
                                command.Parameters.AddWithValue("@nodeId", nodeId);
                                command.ExecuteNonQuery();
                            }

                            UpdateChildrenDeletedStateRecursive(connection, transaction, nodeId, 1);
                        }
                    }
                    else
                    {
                        string finalName = node.Name;
                        int duplicateIndex = 1;
                        string baseName = System.IO.Path.GetFileNameWithoutExtension(node.Name);
                        string ext = System.IO.Path.GetExtension(node.Name);

                        while (true)
                        {
                            using (var checkCmd = connection.CreateCommand())
                            {
                                checkCmd.Transaction = transaction;
                                checkCmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE parent_id = @parentId AND name = @name AND is_deleted = 1 AND id != @nodeId;";
                                checkCmd.Parameters.AddWithValue("@parentId", targetTrashParentId);
                                checkCmd.Parameters.AddWithValue("@name", finalName);
                                checkCmd.Parameters.AddWithValue("@nodeId", nodeId);
                                long count = Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0);
                                if (count == 0) break;

                                duplicateIndex++;
                                finalName = $"{baseName} ({duplicateIndex}){ext}";
                            }
                        }

                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "UPDATE nodes SET is_deleted = 1, parent_id = @trashParentId, name = @name, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
                            command.Parameters.AddWithValue("@trashParentId", targetTrashParentId);
                            command.Parameters.AddWithValue("@name", finalName);
                            command.Parameters.AddWithValue("@nodeId", nodeId);
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
        /// Проверяет, является ли папка корзиной (.Trash)
        /// </summary>
        public bool IsTrashFolder(int nodeId)
        {
            var trash = EnsureTrashFolder();
            return nodeId == trash.Id;
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
            bool isTrash = IsNodeInTrash(newParentId);
            int isDeletedVal = isTrash ? 1 : 0;

            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "UPDATE nodes SET parent_id = @newParentId, name = @newName, is_deleted = @isDeleted, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
                        command.Parameters.AddWithValue("@newParentId", newParentId);
                        command.Parameters.AddWithValue("@newName", newName);
                        command.Parameters.AddWithValue("@isDeleted", isDeletedVal);
                        command.Parameters.AddWithValue("@nodeId", nodeId);
                        command.ExecuteNonQuery();
                    }

                    // Если перемещаемый узел - папка, рекурсивно обновляем флаг is_deleted для всех её потомков
                    UpdateChildrenDeletedStateRecursive(connection, transaction, nodeId, isDeletedVal);

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        private void UpdateChildrenDeletedStateRecursive(SqliteConnection connection, SqliteTransaction transaction, int parentId, int isDeletedVal)
        {
            var childIds = new List<(int Id, bool IsDir)>();
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = "SELECT id, is_dir FROM nodes WHERE parent_id = @parentId;";
                selectCmd.Parameters.AddWithValue("@parentId", parentId);
                using (var reader = selectCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        childIds.Add((reader.GetInt32(0), reader.GetInt32(1) == 1));
                    }
                }
            }

            if (childIds.Count == 0) return;

            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = "UPDATE nodes SET is_deleted = @isDeleted, updated_at = CURRENT_TIMESTAMP WHERE parent_id = @parentId;";
                updateCmd.Parameters.AddWithValue("@isDeleted", isDeletedVal);
                updateCmd.Parameters.AddWithValue("@parentId", parentId);
                updateCmd.ExecuteNonQuery();
            }

            foreach (var child in childIds)
            {
                if (child.IsDir)
                {
                    UpdateChildrenDeletedStateRecursive(connection, transaction, child.Id, isDeletedVal);
                }
            }
        }

        /// <summary>
        /// Обновляет временные метки узла (updated_at и created_at)
        /// </summary>
        public bool UpdateNodeTimestamps(int nodeId, DateTime? modifiedTime, DateTime? creationTime = null)
        {
            if (!modifiedTime.HasValue && !creationTime.HasValue) return false;

            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                var setClauses = new List<string>();
                if (modifiedTime.HasValue)
                {
                    setClauses.Add("updated_at = @updatedAt");
                    command.Parameters.AddWithValue("@updatedAt", modifiedTime.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                }
                if (creationTime.HasValue)
                {
                    setClauses.Add("created_at = @createdAt");
                    command.Parameters.AddWithValue("@createdAt", creationTime.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                }

                command.CommandText = $"UPDATE nodes SET {string.Join(", ", setClauses)} WHERE id = @nodeId;";
                command.Parameters.AddWithValue("@nodeId", nodeId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// Создание или перезапись файла с поддержкой версионирования, метаданных и сохранения оригинальных дат
        /// </summary>
        public void CreateOrUpdateFile(int parentId, string name, long size, int? tgMessageId, AudioMetadataResult? metadata = null, byte[]? inlineBytes = null, DateTime? lastModified = null, DateTime? creationDate = null)
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
                    // Проверяем: если это пустой файл-заглушка от Проводника (size == 0) или probe от Total Commander (size <= 1) без tg_message_id,
                    // то это не предыдущая версия для корзины, а наполнение только что созданного узла!
                    if (existingNode.Size <= 1 && existingNode.TgMessageId == null)
                    {
                        using (var updateCmd = connection.CreateCommand())
                        {
                            string updateSql = @"
                                UPDATE nodes SET
                                    size = @size,
                                    tg_message_id = @tgMessageId,
                                    artist = @artist,
                                    title = @title,
                                    album = @album,
                                    year = @year,
                                    genre = @genre,
                                    track_number = @trackNumber,
                                    duration_seconds = @duration,
                                    bitrate = @bitrate,
                                    header_cache_bytes = @headerCache,
                                    album_cover_bytes = @albumCover,
                                    updated_at = " + (lastModified.HasValue ? "@updatedAt" : "CURRENT_TIMESTAMP") + @"
                                WHERE id = @nodeId;";

                            updateCmd.CommandText = updateSql;
                            updateCmd.Parameters.AddWithValue("@size", size);
                            updateCmd.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                            updateCmd.Parameters.AddWithValue("@nodeId", existingNode.Id);
                            if (lastModified.HasValue)
                            {
                                updateCmd.Parameters.AddWithValue("@updatedAt", lastModified.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                            }
                            AddMetadataParameters(updateCmd, metadata, inlineBytes);
                            updateCmd.ExecuteNonQuery();
                        }
                        return;
                    }

                    // Версионирование: Отправляем старый в корзину, создаем новый с версией + 1
                    int targetTrashParentId = EnsureTrashHierarchyForParent(parentId);
                    
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
                            updateCmd.Parameters.AddWithValue("@trashId", targetTrashParentId);
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
                                    header_cache_bytes, album_cover_bytes, created_at, updated_at
                                ) VALUES (
                                    @parentId, @name, 0, @size, @version, @originalId, @tgMessageId,
                                    @artist, @title, @album, @year, @genre, @trackNumber, @duration, @bitrate,
                                    @headerCache, @albumCover, @createdAt, @updatedAt
                                );";
                            insertCmd.Parameters.AddWithValue("@parentId", parentId);
                            insertCmd.Parameters.AddWithValue("@name", name);
                            insertCmd.Parameters.AddWithValue("@size", size);
                            insertCmd.Parameters.AddWithValue("@version", existingNode.Version + 1);
                            insertCmd.Parameters.AddWithValue("@originalId", existingNode.Id);
                            insertCmd.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@createdAt", creationDate.HasValue 
                                ? creationDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                                : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                            insertCmd.Parameters.AddWithValue("@updatedAt", lastModified.HasValue 
                                ? lastModified.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                                : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                            
                            AddMetadataParameters(insertCmd, metadata, inlineBytes);
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
                                header_cache_bytes, album_cover_bytes, created_at, updated_at
                            ) VALUES (
                                @parentId, @name, 0, @size, 1, @tgMessageId,
                                @artist, @title, @album, @year, @genre, @trackNumber, @duration, @bitrate,
                                @headerCache, @albumCover, @createdAt, @updatedAt
                            );";
                        command.Parameters.AddWithValue("@parentId", parentId);
                        command.Parameters.AddWithValue("@name", name);
                        command.Parameters.AddWithValue("@size", size);
                        command.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@createdAt", creationDate.HasValue 
                            ? creationDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                            : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                        command.Parameters.AddWithValue("@updatedAt", lastModified.HasValue 
                            ? lastModified.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                            : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                        
                        AddMetadataParameters(command, metadata, inlineBytes);
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private void AddMetadataParameters(SqliteCommand command, AudioMetadataResult? metadata, byte[]? inlineBytes = null)
        {
            command.Parameters.AddWithValue("@artist", (object?)metadata?.Artist ?? DBNull.Value);
            command.Parameters.AddWithValue("@title", (object?)metadata?.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("@album", (object?)metadata?.Album ?? DBNull.Value);
            command.Parameters.AddWithValue("@year", (object?)metadata?.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("@genre", (object?)metadata?.Genre ?? DBNull.Value);
            command.Parameters.AddWithValue("@trackNumber", (object?)metadata?.TrackNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@duration", (object?)metadata?.DurationSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("@bitrate", (object?)metadata?.Bitrate ?? DBNull.Value);

            // Не сохраняем тяжелые BLOB-байты в базу данных, чтобы база на 60 000 треков оставалась легкой (~20 МБ)
            command.Parameters.AddWithValue("@headerCache", DBNull.Value);
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
