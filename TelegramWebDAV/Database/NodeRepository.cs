using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public string DatabasePath => _dbManager.DatabasePath;

        public void VacuumDatabase() => _dbManager.VacuumDatabase();

        public void ScheduleVacuum(int delayMs = 3000) => _dbManager.ScheduleVacuum(delayMs);

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
                if (currentNode == null)
                {
                    AppLogger.Warn("NodeRepository", $"[GetNodeByPath NULL] Поиск пути '{path}' прерван: родительский узел равен null на элементе '{part}'.");
                    return null;
                }

                if (!insideTrash && part.Equals(".Trash", StringComparison.OrdinalIgnoreCase) && currentNode.ParentId == null)
                {
                    int rootId = currentNode.Id;
                    using (var connection = _dbManager.GetConnection())
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = 0 LIMIT 1;";
                        command.Parameters.AddWithValue("@parentId", rootId);
                        command.Parameters.AddWithValue("@name", part);
                        currentNode = ReadNode(command);
                    }
                    if (currentNode == null)
                    {
                        AppLogger.Warn("NodeRepository", $"[GetNodeByPath NOT FOUND] Не найдена системная корзина '.Trash' в корне (Root ID={rootId}).");
                    }
                    insideTrash = true;
                    continue;
                }

                int expectedInTrash = insideTrash ? 1 : 0;
                int parentIdForSearch = currentNode.Id;
                using (var connection = _dbManager.GetConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = @inTrash LIMIT 1;";
                    command.Parameters.AddWithValue("@parentId", parentIdForSearch);
                    command.Parameters.AddWithValue("@name", part);
                    command.Parameters.AddWithValue("@inTrash", expectedInTrash);
                    currentNode = ReadNode(command);
                }

                if (currentNode == null)
                {
                    AppLogger.Warn("NodeRepository", $"[GetNodeByPath NOT FOUND] Путь: '{path}' -> Не найден элемент '{part}' (parentId={parentIdForSearch}, expectedInTrash={expectedInTrash}).");
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
        /// Формирует полный логический путь к узлу от корня (например, "/Music/Rock/Queen/song.mp3")
        /// </summary>
        public string GetNodeFullPath(int nodeId)
        {
            var root = GetRootNode();
            if (root != null && nodeId == root.Id) return "/";

            var parts = new List<string>();
            int currentId = nodeId;

            using (var connection = _dbManager.GetConnection())
            {
                while (currentId > 0 && (root == null || currentId != root.Id))
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT parent_id, name FROM nodes WHERE id = @id LIMIT 1;";
                        cmd.Parameters.AddWithValue("@id", currentId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read()) break;
                            string name = reader["name"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(name))
                            {
                                parts.Add(name);
                            }
                            if (reader.IsDBNull(0)) break;
                            currentId = Convert.ToInt32(reader["parent_id"]);
                        }
                    }
                }
            }

            if (parts.Count == 0) return "/";
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        /// <summary>
        /// Формирует полный путь к узлу с подписью версии (_vN) перед расширением для Telegram Caption.
        /// Например: "/Music/Rock/Queen/01. Bohemian Rhapsody_v1.mp3"
        /// </summary>
        public string GetNodeFullPathWithVersion(int nodeId)
        {
            var node = GetNodeById(nodeId);
            if (node == null) return "/";

            string parentPath = node.ParentId.HasValue ? GetNodeFullPath(node.ParentId.Value) : "";
            if (parentPath == "/") parentPath = "";

            int version = node.Version > 0 ? node.Version : 1;
            string nameWithVersion;
            if (node.IsDir)
            {
                nameWithVersion = $"{node.Name}_v{version}";
            }
            else
            {
                string ext = Path.GetExtension(node.Name);
                string nameWithoutExt = Path.GetFileNameWithoutExtension(node.Name);
                nameWithVersion = $"{nameWithoutExt}_v{version}{ext}";
            }

            return $"{parentPath}/{nameWithVersion}";
        }

        /// <summary>
        /// Вычисляет следующий номер версии для файла в указанной директории (1 для нового файла, N+1 при перезаписи)
        /// </summary>
        public int GetNextVersionForFile(int parentId, string fileName)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT version, size, tg_message_id FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = 0 LIMIT 1;";
                command.Parameters.AddWithValue("@parentId", parentId);
                command.Parameters.AddWithValue("@name", fileName);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        long size = Convert.ToInt64(reader["size"]);
                        bool hasTg = !reader.IsDBNull(reader.GetOrdinal("tg_message_id"));
                        int currentVersion = Convert.ToInt32(reader["version"]);

                        // Если это пустой файл-заглушка Проводника (size <= 1 без tg_message_id), это ещё версия 1
                        if (size <= 1 && !hasTg)
                        {
                            return Math.Max(1, currentVersion);
                        }

                        return currentVersion + 1;
                    }
                }
            }
            return 1;
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
                        cmd.CommandText = "SELECT parent_id, in_trash FROM nodes WHERE id = @id LIMIT 1;";
                        cmd.Parameters.AddWithValue("@id", currentId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read()) return false;
                            bool isDel = Convert.ToInt32(reader["in_trash"]) == 1;
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
                        searchCmd.CommandText = "SELECT id FROM nodes WHERE parent_id = @parentId AND name = @name AND is_dir = 1 AND in_trash = 1 LIMIT 1;";
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
                            insertCmd.CommandText = "INSERT INTO nodes (parent_id, name, is_dir, in_trash) VALUES (@parentId, @name, 1, 1); SELECT last_insert_rowid();";
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
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND in_trash = 1;";
                }
                else
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND in_trash = 0;";
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
                command.CommandText = "SELECT COUNT(*) FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = 0;";
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
        /// Гарантирует существование всей цепочки родительских каталогов по указанному пути (по аналогии с mkdir -p).
        /// Если какие-либо промежуточные папки отсутствуют, они автоматически создаются в SQLite.
        /// Возвращает узел конечной папки или null в случае ошибки / запрета.
        /// </summary>
        public Node? EnsureDirectoryPathExists(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath) || directoryPath == "/")
            {
                return GetRootNode();
            }

            // Запрещаем автоматическое создание путей внутри корзины
            if (directoryPath.Equals("/.Trash", StringComparison.OrdinalIgnoreCase) ||
                directoryPath.StartsWith("/.Trash/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var root = GetRootNode();
            if (root == null) return null;

            string[] parts = directoryPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Node currentNode = root;

            using (var connection = _dbManager.GetConnection())
            {
                foreach (var part in parts)
                {
                    // Ищем существующую активную (не удаленную) директорию с таким именем
                    Node? nextNode = null;
                    using (var searchCmd = connection.CreateCommand())
                    {
                        searchCmd.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = 0 LIMIT 1;";
                        searchCmd.Parameters.AddWithValue("@parentId", currentNode.Id);
                        searchCmd.Parameters.AddWithValue("@name", part);
                        nextNode = ReadNode(searchCmd);
                    }

                    if (nextNode != null)
                    {
                        if (!nextNode.IsDir)
                        {
                            // Если на пути встретился файл вместо каталога, создать подпапку нельзя
                            return null;
                        }
                        currentNode = nextNode;
                    }
                    else
                    {
                        // Папка отсутствует — атомарно создаем её
                        using (var insertCmd = connection.CreateCommand())
                        {
                            insertCmd.CommandText = "INSERT INTO nodes (parent_id, name, is_dir, in_trash) VALUES (@parentId, @name, 1, 0); SELECT last_insert_rowid();";
                            insertCmd.Parameters.AddWithValue("@parentId", currentNode.Id);
                            insertCmd.Parameters.AddWithValue("@name", part);
                            int newFolderId = Convert.ToInt32(insertCmd.ExecuteScalar());

                            using (var fetchCmd = connection.CreateCommand())
                            {
                                fetchCmd.CommandText = "SELECT * FROM nodes WHERE id = @id LIMIT 1;";
                                fetchCmd.Parameters.AddWithValue("@id", newFolderId);
                                var created = ReadNode(fetchCmd);
                                if (created == null) return null;
                                currentNode = created;
                            }
                        }
                    }
                }
            }

            return currentNode;
        }

        /// <summary>
        /// Мягкое удаление (перемещение в корзину с сохранением структуры каталогов)
        /// </summary>
        public void SoftDeleteNode(int nodeId)
        {
            var node = GetNodeById(nodeId);
            if (node == null) return;

            // Защита: если узел уже удален или находится в корзине, повторное мягкое удаление не требуется
            if (node.InTrash || IsNodeInTrash(node.Id))
            {
                AppLogger.Warn("NodeRepository", $"Попытка мягкого удаления узла ID {node.Id} ('{node.Name}'), который уже находится в корзине. Операция пропущена.");
                return;
            }

            var root = GetRootNode();
            int originalParentId = node.ParentId ?? root?.Id ?? 1;

            int targetTrashParentId = EnsureTrashHierarchyForParent(originalParentId);

            // Собираем точный список перемещаемых файлов ДО выполнения операции в БД,
            // чтобы обновить подписи ТОЛЬКО для них (не затрагивая файлы, уже лежащие в корзине)
            var movedFiles = new List<Node>();
            GetFilesRecursive(nodeId, movedFiles);

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
                            checkCmd.CommandText = "SELECT id FROM nodes WHERE parent_id = @parentId AND name = @name AND is_dir = 1 AND in_trash = 1 LIMIT 1;";
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
                                moveChildrenCmd.CommandText = "UPDATE nodes SET parent_id = @existingId, in_trash = 1, updated_at = CURRENT_TIMESTAMP WHERE parent_id = @nodeId;";
                                moveChildrenCmd.Parameters.AddWithValue("@existingId", existingTrashFolderId.Value);
                                moveChildrenCmd.Parameters.AddWithValue("@nodeId", nodeId);
                                moveChildrenCmd.ExecuteNonQuery();
                            }

                            UpdateChildrenTrashStateRecursive(connection, transaction, existingTrashFolderId.Value, 1);

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
                                command.CommandText = "UPDATE nodes SET in_trash = 1, parent_id = @trashParentId, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
                                command.Parameters.AddWithValue("@trashParentId", targetTrashParentId);
                                command.Parameters.AddWithValue("@nodeId", nodeId);
                                command.ExecuteNonQuery();
                            }

                            UpdateChildrenTrashStateRecursive(connection, transaction, nodeId, 1);
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
                                checkCmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = 1 AND id != @nodeId;";
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
                            command.CommandText = "UPDATE nodes SET in_trash = 1, parent_id = @trashParentId, name = @name, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
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

            // Ставим в очередь обновление подписей на #trash ТОЛЬКО для реально перемещенных в корзину файлов
            try
            {
                EnqueueCaptionUpdatesForFiles(movedFiles);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Database", $"Не удалось поставить в очередь обновление подписей корзины: {ex.Message}");
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
        /// Рекурсивно собирает узел и всех его потомков (всю физическую ветку поддерева папки)
        /// </summary>
        public List<Node> GetSubtreeNodes(int rootNodeId)
        {
            var result = new List<Node>();
            var rootNode = GetNodeById(rootNodeId);
            if (rootNode == null) return result;

            result.Add(rootNode);
            if (rootNode.IsDir)
            {
                CollectSubtreeRecursive(rootNode.Id, result);
            }

            return result;
        }

        private void CollectSubtreeRecursive(int parentId, List<Node> result)
        {
            var children = GetChildren(parentId);
            foreach (var child in children)
            {
                result.Add(child);
                if (child.IsDir)
                {
                    CollectSubtreeRecursive(child.Id, result);
                }
            }
        }

        /// <summary>
        /// Атомарно помещает ID сообщений Telegram в персистентную очередь pending_deletions
        /// и удаляет сами узлы из таблицы nodes в единой транзакции SQLite.
        /// Гарантирует мгновенное исчезновение файлов из файловой системы и надежное фоновое удаление из Telegram.
        /// </summary>
        public void EnqueuePermanentDeletion(List<int> tgMessageIds, List<int> dbNodeIds)
        {
            var uniqueMsgIds = (tgMessageIds != null) ? tgMessageIds.Where(id => id > 0).Distinct().ToList() : new List<int>();
            int nodeCount = dbNodeIds?.Count ?? 0;

            AppLogger.Info("Database", $"[EnqueuePermanentDeletion] Транзакция перманентного удаления: {nodeCount} узлов из БД, {uniqueMsgIds.Count} уникальных сообщений Telegram в очередь pending_deletions.");
            if (uniqueMsgIds.Count > 0)
            {
                AppLogger.Info("Database", $"[EnqueuePermanentDeletion] Список Telegram Message ID для очистки: [{string.Join(", ", uniqueMsgIds)}]");
            }
            if (nodeCount > 0)
            {
                AppLogger.Info("Database", $"[EnqueuePermanentDeletion] Список ID узлов БД для удаления: [{string.Join(", ", dbNodeIds!)}]");
            }

            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    if (uniqueMsgIds.Count > 0)
                    {
                        using (var insertCmd = connection.CreateCommand())
                        {
                            insertCmd.Transaction = transaction;
                            insertCmd.CommandText = "INSERT INTO pending_deletions (tg_message_id) VALUES (@msgId);";
                            var msgIdParam = insertCmd.Parameters.Add("@msgId", SqliteType.Integer);

                            foreach (var msgId in uniqueMsgIds)
                            {
                                msgIdParam.Value = msgId;
                                insertCmd.ExecuteNonQuery();
                            }
                        }
                    }

                    if (dbNodeIds != null && dbNodeIds.Count > 0)
                    {
                        using (var deleteCmd = connection.CreateCommand())
                        {
                            deleteCmd.Transaction = transaction;
                            deleteCmd.CommandText = "DELETE FROM nodes WHERE id = @id;";
                            var idParam = deleteCmd.Parameters.Add("@id", SqliteType.Integer);

                            foreach (var id in dbNodeIds)
                            {
                                idParam.Value = id;
                                deleteCmd.ExecuteNonQuery();
                            }
                        }
                    }

                    transaction.Commit();
                    AppLogger.Info("Database", $"[EnqueuePermanentDeletion] Транзакция успешно зафиксирована.");
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    AppLogger.Error("Database", $"[EnqueuePermanentDeletion] Сбой транзакции перманентного удаления: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Получает порцию сообщений Telegram, ожидающих перманентного удаления
        /// </summary>
        public List<int> GetPendingDeletions(int limit = 100)
        {
            var result = new List<int>();
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT tg_message_id FROM pending_deletions LIMIT @limit;";
                command.Parameters.AddWithValue("@limit", limit);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(reader.GetInt32(0));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Удаляет успешно очищенные ID сообщений из таблицы очереди pending_deletions
        /// </summary>
        public void RemovePendingDeletions(List<int> tgMessageIds)
        {
            if (tgMessageIds == null || tgMessageIds.Count == 0) return;
            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM pending_deletions WHERE tg_message_id = @msgId;";
                        var param = command.Parameters.Add("@msgId", SqliteType.Integer);
                        foreach (var id in tgMessageIds)
                        {
                            param.Value = id;
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
            int inTrashVal = isTrash ? 1 : 0;

            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "UPDATE nodes SET parent_id = @newParentId, name = @newName, in_trash = @inTrash, updated_at = CURRENT_TIMESTAMP WHERE id = @nodeId;";
                        command.Parameters.AddWithValue("@newParentId", newParentId);
                        command.Parameters.AddWithValue("@newName", newName);
                        command.Parameters.AddWithValue("@inTrash", inTrashVal);
                        command.Parameters.AddWithValue("@nodeId", nodeId);
                        command.ExecuteNonQuery();
                    }

                    // Если перемещаемый узел - папка, рекурсивно обновляем флаг in_trash для всех её потомков
                    UpdateChildrenTrashStateRecursive(connection, transaction, nodeId, inTrashVal);

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            // Автоматически ставим в стойкую очередь SQLite все файлы перемещаемого/переименовываемого поддерева
            EnqueueCaptionUpdatesForSubtree(nodeId);
        }

        private void UpdateChildrenTrashStateRecursive(SqliteConnection connection, SqliteTransaction transaction, int parentId, int inTrashVal)
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
                updateCmd.CommandText = "UPDATE nodes SET in_trash = @inTrash, updated_at = CURRENT_TIMESTAMP WHERE parent_id = @parentId;";
                updateCmd.Parameters.AddWithValue("@inTrash", inTrashVal);
                updateCmd.Parameters.AddWithValue("@parentId", parentId);
                updateCmd.ExecuteNonQuery();
            }

            foreach (var child in childIds)
            {
                if (child.IsDir)
                {
                    UpdateChildrenTrashStateRecursive(connection, transaction, child.Id, inTrashVal);
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
        /// Создание или перезапись файла с поддержкой версионирования, локального inline_data для микрофайлов и сохранения оригинальных дат
        /// </summary>
        public void CreateOrUpdateFile(int parentId, string name, long size, int? tgMessageId, int? tgPreviewMessageId = null, byte[]? inlineData = null, DateTime? lastModified = null, DateTime? creationDate = null, long? tgChannelId = null)
        {
            using (var connection = _dbManager.GetConnection())
            {
                Node? existingNode = null;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = 0 LIMIT 1;";
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
                                    tg_preview_message_id = @tgPreviewMessageId,
                                    tg_channel_id = @tgChannelId,
                                    inline_data = @inlineData,
                                    updated_at = " + (lastModified.HasValue ? "@updatedAt" : "CURRENT_TIMESTAMP") +
                                    (creationDate.HasValue ? ", created_at = @createdAt" : "") + @"
                                WHERE id = @nodeId;";

                            updateCmd.CommandText = updateSql;
                            updateCmd.Parameters.AddWithValue("@size", size);
                            updateCmd.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                            updateCmd.Parameters.AddWithValue("@tgPreviewMessageId", (object?)tgPreviewMessageId ?? DBNull.Value);
                            updateCmd.Parameters.AddWithValue("@tgChannelId", (object?)tgChannelId ?? DBNull.Value);
                            updateCmd.Parameters.AddWithValue("@inlineData", (object?)inlineData ?? DBNull.Value);
                            updateCmd.Parameters.AddWithValue("@nodeId", existingNode.Id);
                            if (lastModified.HasValue)
                            {
                                updateCmd.Parameters.AddWithValue("@updatedAt", lastModified.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                            }
                            if (creationDate.HasValue)
                            {
                                updateCmd.Parameters.AddWithValue("@createdAt", creationDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                            }
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
                            AppLogger.Info("Database", $"[CreateOrUpdateFile] Версионирование файла '{name}' (ID {existingNode.Id}): предыдущая версия v{existingNode.Version} отправлена в корзину под именем '{trashName}' (trashParentId={targetTrashParentId}, TgMessageId={existingNode.TgMessageId?.ToString() ?? "NULL"}).");
                            updateCmd.CommandText = "UPDATE nodes SET in_trash = 1, parent_id = @trashId, name = @trashName WHERE id = @nodeId;";
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
                                    parent_id, original_node_id, name, is_dir, version, in_trash,
                                    tg_message_id, tg_preview_message_id, tg_channel_id, size, created_at, updated_at, inline_data
                                ) VALUES (
                                    @parentId, @originalId, @name, 0, @version, 0,
                                    @tgMessageId, @tgPreviewMessageId, @tgChannelId, @size, @createdAt, @updatedAt, @inlineData
                                );";
                            insertCmd.Parameters.AddWithValue("@parentId", parentId);
                            insertCmd.Parameters.AddWithValue("@name", name);
                            insertCmd.Parameters.AddWithValue("@size", size);
                            insertCmd.Parameters.AddWithValue("@version", existingNode.Version + 1);
                            insertCmd.Parameters.AddWithValue("@originalId", existingNode.Id);
                            insertCmd.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@tgPreviewMessageId", (object?)tgPreviewMessageId ?? DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@tgChannelId", (object?)tgChannelId ?? DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@inlineData", (object?)inlineData ?? DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@createdAt", creationDate.HasValue 
                                ? creationDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                                : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                            insertCmd.Parameters.AddWithValue("@updatedAt", lastModified.HasValue 
                                ? lastModified.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                                : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                            
                            insertCmd.ExecuteNonQuery();
                        }

                        // Старая версия отправляется в корзину: обновляем её подпись в Telegram на #trash (для документа и фото-превью)
                        string oldPathWithVersion = GetNodeFullPathWithVersion(existingNode.Id);

                        if (existingNode.TgMessageId.HasValue && existingNode.TgMessageId.Value > 1)
                        {
                            string trashCaption = FormatTelegramCaption(oldPathWithVersion, existingNode.TgMessageId.Value, isLatest: false);
                            using (var captionCmd = connection.CreateCommand())
                            {
                                captionCmd.Transaction = transaction;
                                captionCmd.CommandText = @"
                                    INSERT INTO pending_caption_updates (node_id, tg_message_id, new_caption)
                                    VALUES (@nodeId, @tgMessageId, @newCaption)
                                    ON CONFLICT(tg_message_id) DO UPDATE SET
                                        new_caption = excluded.new_caption,
                                        status = 0;
                                ";
                                captionCmd.Parameters.AddWithValue("@nodeId", existingNode.Id);
                                captionCmd.Parameters.AddWithValue("@tgMessageId", existingNode.TgMessageId.Value);
                                captionCmd.Parameters.AddWithValue("@newCaption", trashCaption);
                                captionCmd.ExecuteNonQuery();
                            }
                        }

                        if (existingNode.TgPreviewMessageId.HasValue && existingNode.TgPreviewMessageId.Value > 1)
                        {
                            string trashPreviewCaption = FormatTelegramCaption(oldPathWithVersion, existingNode.TgPreviewMessageId.Value, isLatest: false);
                            using (var captionCmd = connection.CreateCommand())
                            {
                                captionCmd.Transaction = transaction;
                                captionCmd.CommandText = @"
                                    INSERT INTO pending_caption_updates (node_id, tg_message_id, new_caption)
                                    VALUES (@nodeId, @tgMessageId, @newCaption)
                                    ON CONFLICT(tg_message_id) DO UPDATE SET
                                        new_caption = excluded.new_caption,
                                        status = 0;
                                ";
                                captionCmd.Parameters.AddWithValue("@nodeId", existingNode.Id);
                                captionCmd.Parameters.AddWithValue("@tgMessageId", existingNode.TgPreviewMessageId.Value);
                                captionCmd.Parameters.AddWithValue("@newCaption", trashPreviewCaption);
                                captionCmd.ExecuteNonQuery();
                            }
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
                                parent_id, original_node_id, name, is_dir, version, in_trash,
                                tg_message_id, tg_preview_message_id, tg_channel_id, size, created_at, updated_at, inline_data
                            ) VALUES (
                                @parentId, NULL, @name, 0, 1, 0,
                                @tgMessageId, @tgPreviewMessageId, @tgChannelId, @size, @createdAt, @updatedAt, @inlineData
                            );";
                        command.Parameters.AddWithValue("@parentId", parentId);
                        command.Parameters.AddWithValue("@name", name);
                        command.Parameters.AddWithValue("@size", size);
                        command.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@tgPreviewMessageId", (object?)tgPreviewMessageId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@tgChannelId", (object?)tgChannelId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@inlineData", (object?)inlineData ?? DBNull.Value);
                        command.Parameters.AddWithValue("@createdAt", creationDate.HasValue 
                            ? creationDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                            : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                        command.Parameters.AddWithValue("@updatedAt", lastModified.HasValue 
                            ? lastModified.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") 
                            : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                        
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Обновляет прогресс частичной загрузки (для умного софта с Content-Range в PUT).
        /// Если загрузка завершена (передан tgMessageId или достигнут totalSize), очищает временный прогресс.
        /// </summary>
        public void UpdateUploadProgress(int parentId, string name, long chunkPosition, long totalSize, int? tgMessageId, int? tgPreviewMessageId = null)
        {
            using (var connection = _dbManager.GetConnection())
            {
                Node? node = null;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT * FROM nodes WHERE parent_id = @parentId AND name = @name AND in_trash = 0 LIMIT 1;";
                    command.Parameters.AddWithValue("@parentId", parentId);
                    command.Parameters.AddWithValue("@name", name);
                    node = ReadNode(command);
                }

                if (node == null)
                {
                    CreateOrUpdateFile(parentId, name, totalSize, tgMessageId, tgPreviewMessageId);
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
                            UPDATE nodes SET size = @totalSize, 
                                tg_message_id = COALESCE(@tgMessageId, tg_message_id),
                                tg_preview_message_id = COALESCE(@tgPreviewMessageId, tg_preview_message_id)
                            WHERE id = @nodeId;";
                    }
                    else
                    {
                        command.CommandText = @"
                            INSERT INTO upload_progress (node_id, chunk_position) 
                            VALUES (@nodeId, @chunkPosition);
                            UPDATE nodes SET size = @totalSize, 
                                tg_message_id = COALESCE(@tgMessageId, tg_message_id),
                                tg_preview_message_id = COALESCE(@tgPreviewMessageId, tg_preview_message_id)
                            WHERE id = @nodeId;";
                    }
                    command.Parameters.AddWithValue("@nodeId", node.Id);
                    command.Parameters.AddWithValue("@chunkPosition", chunkPosition);
                    command.Parameters.AddWithValue("@totalSize", totalSize);
                    command.Parameters.AddWithValue("@tgMessageId", (object?)tgMessageId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@tgPreviewMessageId", (object?)tgPreviewMessageId ?? DBNull.Value);
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
                OriginalNodeId = reader["original_node_id"] != DBNull.Value ? Convert.ToInt32(reader["original_node_id"]) : (int?)null,
                Name = Convert.ToString(reader["name"]) ?? string.Empty,
                IsDir = Convert.ToInt32(reader["is_dir"]) == 1,
                Version = Convert.ToInt32(reader["version"]),
                InTrash = Convert.ToInt32(reader["in_trash"]) == 1,
                TgMessageId = reader["tg_message_id"] != DBNull.Value ? Convert.ToInt32(reader["tg_message_id"]) : (int?)null,
                TgPreviewMessageId = reader["tg_preview_message_id"] != DBNull.Value ? Convert.ToInt32(reader["tg_preview_message_id"]) : (int?)null,
                TgChannelId = reader["tg_channel_id"] != DBNull.Value ? Convert.ToInt64(reader["tg_channel_id"]) : (long?)null,
                Size = Convert.ToInt64(reader["size"]),
                CreatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["created_at"]), DateTimeKind.Utc),
                UpdatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["updated_at"]), DateTimeKind.Utc),
                InlineData = reader["inline_data"] != DBNull.Value ? (byte[])reader["inline_data"] : null
            };

            return node;
        }

        private long _cachedUsedBytes = 0;
        private DateTime _usedBytesCacheExpiresAt = DateTime.MinValue;
        private bool _cachedIncludeTrash = true;
        private readonly object _usedBytesLock = new object();

        /// <summary>
        /// Возвращает суммарный объём всех сохраненных файлов в байтах.
        /// </summary>
        public long GetTotalUsedSpaceBytes(bool includeTrash = true)
        {
            lock (_usedBytesLock)
            {
                if (_usedBytesCacheExpiresAt > DateTime.UtcNow && _cachedIncludeTrash == includeTrash)
                {
                    return _cachedUsedBytes;
                }

                using var connection = _dbManager.GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT COALESCE(SUM(size), 0) 
                    FROM nodes 
                    WHERE is_dir = 0 AND (@includeTrash = 1 OR in_trash = 0);";
                command.Parameters.AddWithValue("@includeTrash", includeTrash ? 1 : 0);

                object? result = command.ExecuteScalar();
                long total = result != null && result != DBNull.Value ? Convert.ToInt64(result) : 0;

                _cachedUsedBytes = total;
                _cachedIncludeTrash = includeTrash;
                _usedBytesCacheExpiresAt = DateTime.UtcNow.AddSeconds(3); // Кэшируем на 3 секунды
                return total;
            }
        }

        /// <summary>
        /// Формирует стандартизированную подпись для сообщения Telegram со статусом, версией, ID сообщения и хэштегом.
        /// Активный файл: 🟢 /Путь/Файл_vN.ext \nID: 1487 \n#latest
        /// Мусор / Старая версия: 🗑️ /Путь/Файл_vN.ext \nID: 1487 \n#trash
        /// </summary>
        public static string FormatTelegramCaption(string fullPathWithVersion, int? messageId, bool isLatest)
        {
            string idLine = messageId.HasValue && messageId.Value > 0 ? $"\nID: {messageId.Value}" : "";
            if (isLatest)
            {
                return $"🟢 {fullPathWithVersion}{idLine}\n#latest";
            }
            else
            {
                return $"🗑️ {fullPathWithVersion}{idLine}\n#trash";
            }
        }

        /// <summary>
        /// Помещает в стойкую очередь SQLite все файлы указанного поддерева для фонового обновления подписей в Telegram.
        /// Гарантирует устойчивость к выключению ПК или перезапуску приложения.
        /// </summary>
        /// <summary>
        /// Поставить в очередь обновление подписей для конкретного списка файлов.
        /// Обновляет только те сообщения, чей текст в базе данных действительно изменился,
        /// предотвращая спам Telegram и ошибку 400 MESSAGE_NOT_MODIFIED.
        /// </summary>
        public void EnqueueCaptionUpdatesForFiles(List<Node> files)
        {
            if (files == null || files.Count == 0) return;

            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    int queuedCount = 0;
                    foreach (var file in files)
                    {
                        var freshFile = GetNodeById(file.Id) ?? file;
                        string pathWithVersion = GetNodeFullPathWithVersion(freshFile.Id);

                        if (freshFile.TgMessageId.HasValue && freshFile.TgMessageId.Value > 1)
                        {
                            string newCaption = FormatTelegramCaption(pathWithVersion, freshFile.TgMessageId.Value, !freshFile.InTrash);
                            using (var cmd = connection.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = @"
                                    INSERT INTO pending_caption_updates (node_id, tg_message_id, new_caption)
                                    VALUES (@nodeId, @tgMessageId, @newCaption)
                                    ON CONFLICT(tg_message_id) DO UPDATE SET
                                        new_caption = excluded.new_caption,
                                        status = 0
                                    WHERE pending_caption_updates.new_caption != excluded.new_caption;
                                ";
                                cmd.Parameters.AddWithValue("@nodeId", freshFile.Id);
                                cmd.Parameters.AddWithValue("@tgMessageId", freshFile.TgMessageId.Value);
                                cmd.Parameters.AddWithValue("@newCaption", newCaption);
                                int affected = cmd.ExecuteNonQuery();
                                if (affected > 0) queuedCount++;
                            }
                        }

                        if (freshFile.TgPreviewMessageId.HasValue && freshFile.TgPreviewMessageId.Value > 1)
                        {
                            string previewCaption = FormatTelegramCaption(pathWithVersion, freshFile.TgPreviewMessageId.Value, !freshFile.InTrash);
                            using (var cmd = connection.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = @"
                                    INSERT INTO pending_caption_updates (node_id, tg_message_id, new_caption)
                                    VALUES (@nodeId, @tgMessageId, @newCaption)
                                    ON CONFLICT(tg_message_id) DO UPDATE SET
                                        new_caption = excluded.new_caption,
                                        status = 0
                                    WHERE pending_caption_updates.new_caption != excluded.new_caption;
                                ";
                                cmd.Parameters.AddWithValue("@nodeId", freshFile.Id);
                                cmd.Parameters.AddWithValue("@tgMessageId", freshFile.TgPreviewMessageId.Value);
                                cmd.Parameters.AddWithValue("@newCaption", previewCaption);
                                int affected = cmd.ExecuteNonQuery();
                                if (affected > 0) queuedCount++;
                            }
                        }
                    }

                    transaction.Commit();
                    if (queuedCount > 0)
                    {
                        AppLogger.Info("Database", $"Поставлено в фоновую очередь обновление подписей Telegram для {queuedCount} элементов.");
                    }
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    AppLogger.Warn("Database", $"Ошибка при добавлении подписей в очередь: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Поставить в очередь обновление подписей для всех файлов в поддереве.
        /// </summary>
        public void EnqueueCaptionUpdatesForSubtree(int nodeId)
        {
            var filesToUpdate = new List<Node>();
            GetFilesRecursive(nodeId, filesToUpdate);
            EnqueueCaptionUpdatesForFiles(filesToUpdate);
        }

        private void GetFilesRecursive(int parentId, List<Node> result)
        {
            var node = GetNodeById(parentId);
            if (node == null) return;

            if (!node.IsDir)
            {
                result.Add(node);
                return;
            }

            var children = GetChildren(parentId);
            foreach (var child in children)
            {
                if (child.IsDir)
                {
                    GetFilesRecursive(child.Id, result);
                }
                else
                {
                    result.Add(child);
                }
            }
        }

        public PendingCaptionItem? GetNextPendingCaptionUpdate()
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, node_id, tg_message_id, new_caption FROM pending_caption_updates ORDER BY id ASC LIMIT 1;";
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new PendingCaptionItem
                        {
                            Id = reader.GetInt32(0),
                            NodeId = reader.GetInt32(1),
                            TgMessageId = reader.GetInt32(2),
                            NewCaption = reader.GetString(3)
                        };
                    }
                }
            }
            return null;
        }

        public void RemovePendingCaptionUpdate(int id)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM pending_caption_updates WHERE id = @id;";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }

        public int GetPendingCaptionCount()
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM pending_caption_updates;";
                var result = command.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        #region Telegram Accounts & Channels Repository

        /// <summary>
        /// Возвращает активный аккаунт Telegram.
        /// </summary>
        public TelegramAccount? GetActiveTelegramAccount()
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM telegram_accounts WHERE is_active = 1 ORDER BY id ASC LIMIT 1;";
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new TelegramAccount
                        {
                            Id = Convert.ToInt32(reader["id"]),
                            ApiId = Convert.ToInt32(reader["api_id"]),
                            ApiHash = Convert.ToString(reader["api_hash"]) ?? string.Empty,
                            PhoneNumber = reader["phone_number"] != DBNull.Value ? Convert.ToString(reader["phone_number"]) : null,
                            SessionPath = Convert.ToString(reader["session_path"]) ?? "user.session",
                            IsActive = Convert.ToInt32(reader["is_active"]) == 1,
                            CreatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["created_at"]), DateTimeKind.Utc),
                            UpdatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["updated_at"]), DateTimeKind.Utc)
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Создает или обновляет активный аккаунт Telegram.
        /// </summary>
        public TelegramAccount SaveTelegramAccount(int apiId, string apiHash, string? phoneNumber = null, string sessionPath = "user.session")
        {
            var active = GetActiveTelegramAccount();
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                if (active != null)
                {
                    command.CommandText = @"
                        UPDATE telegram_accounts 
                        SET api_id = @apiId, api_hash = @apiHash, phone_number = @phone, session_path = @session, updated_at = CURRENT_TIMESTAMP 
                        WHERE id = @id;";
                    command.Parameters.AddWithValue("@apiId", apiId);
                    command.Parameters.AddWithValue("@apiHash", apiHash);
                    command.Parameters.AddWithValue("@phone", (object?)phoneNumber ?? (object?)active.PhoneNumber ?? DBNull.Value);
                    command.Parameters.AddWithValue("@session", sessionPath);
                    command.Parameters.AddWithValue("@id", active.Id);
                    command.ExecuteNonQuery();
                    return GetActiveTelegramAccount()!;
                }
                else
                {
                    command.CommandText = @"
                        INSERT INTO telegram_accounts (api_id, api_hash, phone_number, session_path, is_active)
                        VALUES (@apiId, @apiHash, @phone, @session, 1);";
                    command.Parameters.AddWithValue("@apiId", apiId);
                    command.Parameters.AddWithValue("@apiHash", apiHash);
                    command.Parameters.AddWithValue("@phone", (object?)phoneNumber ?? DBNull.Value);
                    command.Parameters.AddWithValue("@session", sessionPath);
                    command.ExecuteNonQuery();
                    return GetActiveTelegramAccount()!;
                }
            }
        }

        /// <summary>
        /// Обновляет телефон для аккаунта.
        /// </summary>
        public void UpdateAccountPhone(int accountId, string? phoneNumber)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE telegram_accounts SET phone_number = @phone, updated_at = CURRENT_TIMESTAMP WHERE id = @id;";
                command.Parameters.AddWithValue("@phone", (object?)phoneNumber ?? DBNull.Value);
                command.Parameters.AddWithValue("@id", accountId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Сбрасывает авторизацию (удаляет телефон) для аккаунта.
        /// </summary>
        public void ClearAccountSession(int accountId)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE telegram_accounts SET phone_number = NULL, updated_at = CURRENT_TIMESTAMP WHERE id = @id;";
                command.Parameters.AddWithValue("@id", accountId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Возвращает основной канал-хранилище (Primary).
        /// </summary>
        public TelegramChannel? GetPrimaryTelegramChannel(int? accountId = null)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                if (accountId.HasValue)
                {
                    command.CommandText = "SELECT * FROM telegram_channels WHERE account_id = @accountId AND is_primary = 1 AND is_active = 1 LIMIT 1;";
                    command.Parameters.AddWithValue("@accountId", accountId.Value);
                }
                else
                {
                    command.CommandText = "SELECT * FROM telegram_channels WHERE is_primary = 1 AND is_active = 1 LIMIT 1;";
                }

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return MapReaderToTelegramChannel(reader);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Возвращает канал-хранилище по его Telegram Channel ID.
        /// </summary>
        public TelegramChannel? GetTelegramChannel(long channelId)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM telegram_channels WHERE channel_id = @channelId LIMIT 1;";
                command.Parameters.AddWithValue("@channelId", channelId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return MapReaderToTelegramChannel(reader);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Возвращает список всех каналов.
        /// </summary>
        public List<TelegramChannel> GetTelegramChannels(int? accountId = null)
        {
            var list = new List<TelegramChannel>();
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                if (accountId.HasValue)
                {
                    command.CommandText = "SELECT * FROM telegram_channels WHERE account_id = @accountId AND is_active = 1 ORDER BY id ASC;";
                    command.Parameters.AddWithValue("@accountId", accountId.Value);
                }
                else
                {
                    command.CommandText = "SELECT * FROM telegram_channels WHERE is_active = 1 ORDER BY id ASC;";
                }

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(MapReaderToTelegramChannel(reader));
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Сохраняет или обновляет канал-хранилище в SQLite.
        /// </summary>
        public void SaveOrUpdateTelegramChannel(int accountId, long channelId, long accessHash, string title, bool isPrimary = true)
        {
            using (var connection = _dbManager.GetConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    if (isPrimary)
                    {
                        using (var resetCmd = connection.CreateCommand())
                        {
                            resetCmd.Transaction = transaction;
                            resetCmd.CommandText = "UPDATE telegram_channels SET is_primary = 0 WHERE account_id = @accountId;";
                            resetCmd.Parameters.AddWithValue("@accountId", accountId);
                            resetCmd.ExecuteNonQuery();
                        }
                    }

                    using (var upsertCmd = connection.CreateCommand())
                    {
                        upsertCmd.Transaction = transaction;
                        upsertCmd.CommandText = @"
                            INSERT INTO telegram_channels (account_id, channel_id, access_hash, title, is_primary, is_active)
                            VALUES (@accountId, @channelId, @accessHash, @title, @isPrimary, 1)
                            ON CONFLICT(channel_id) DO UPDATE SET
                                account_id = excluded.account_id,
                                access_hash = excluded.access_hash,
                                title = excluded.title,
                                is_primary = excluded.is_primary,
                                is_active = 1,
                                updated_at = CURRENT_TIMESTAMP;
                        ";
                        upsertCmd.Parameters.AddWithValue("@accountId", accountId);
                        upsertCmd.Parameters.AddWithValue("@channelId", channelId);
                        upsertCmd.Parameters.AddWithValue("@accessHash", accessHash);
                        upsertCmd.Parameters.AddWithValue("@title", title);
                        upsertCmd.Parameters.AddWithValue("@isPrimary", isPrimary ? 1 : 0);
                        upsertCmd.ExecuteNonQuery();
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
        /// Обновляет название канала в базе.
        /// </summary>
        public void UpdateChannelTitle(long channelId, string newTitle)
        {
            using (var connection = _dbManager.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE telegram_channels SET title = @title, updated_at = CURRENT_TIMESTAMP WHERE channel_id = @channelId;";
                command.Parameters.AddWithValue("@title", newTitle);
                command.Parameters.AddWithValue("@channelId", channelId);
                command.ExecuteNonQuery();
            }
        }

        private static TelegramChannel MapReaderToTelegramChannel(SqliteDataReader reader)
        {
            return new TelegramChannel
            {
                Id = Convert.ToInt32(reader["id"]),
                AccountId = Convert.ToInt32(reader["account_id"]),
                ChannelId = Convert.ToInt64(reader["channel_id"]),
                AccessHash = Convert.ToInt64(reader["access_hash"]),
                Title = Convert.ToString(reader["title"]) ?? string.Empty,
                IsPrimary = Convert.ToInt32(reader["is_primary"]) == 1,
                IsActive = Convert.ToInt32(reader["is_active"]) == 1,
                CreatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["created_at"]), DateTimeKind.Utc),
                UpdatedAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["updated_at"]), DateTimeKind.Utc)
            };
        }

        #endregion
    }

    public class PendingCaptionItem
    {
        public int Id { get; set; }
        public int NodeId { get; set; }
        public int TgMessageId { get; set; }
        public string NewCaption { get; set; } = string.Empty;
    }
}
