-- Включение режима WAL для безопасного конкурентного доступа
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
    channel_id INTEGER NOT NULL UNIQUE,          -- Telegram Channel ID (например 4323453199)
    access_hash INTEGER NOT NULL,                 -- Telegram Channel Access Hash
    title TEXT NOT NULL,                          -- Название канала ("Telegram WebDAV Drive")
    is_primary INTEGER NOT NULL DEFAULT 1,        -- 1 = основной канал для новых загрузок
    is_active INTEGER NOT NULL DEFAULT 1,         -- 1 = активен для чтения/записи
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (account_id) REFERENCES telegram_accounts(id) ON DELETE CASCADE
);

-- Таблица узлов виртуальной файловой системы (Файлы и Папки)
CREATE TABLE IF NOT EXISTS nodes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER,
    original_node_id INTEGER, -- Ссылка на актуальный файл, если это старая версия в корзине
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
    
    FOREIGN KEY (parent_id) REFERENCES nodes(id) ON DELETE CASCADE
);

-- Таблица трекинга незавершенных загрузок (для докачки при обрывах)
CREATE TABLE IF NOT EXISTS upload_progress (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    node_id INTEGER NOT NULL,
    chunk_position INTEGER NOT NULL DEFAULT 0, -- смещение в байтах
    file_hash TEXT, -- Хеш локального файла для валидации
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

-- Индексы для оптимизации поиска
CREATE INDEX IF NOT EXISTS idx_nodes_parent_id ON nodes(parent_id);
CREATE INDEX IF NOT EXISTS idx_nodes_name ON nodes(name);
CREATE INDEX IF NOT EXISTS idx_nodes_in_trash ON nodes(in_trash);
CREATE INDEX IF NOT EXISTS idx_upload_progress_node_id ON upload_progress(node_id);

-- Таблица персистентной очереди гарантированного удаления файлов из Telegram
CREATE TABLE IF NOT EXISTS pending_deletions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tg_message_id INTEGER NOT NULL,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_pending_deletions_tg_msg ON pending_deletions(tg_message_id);

-- Таблица персистентной очереди гарантированного обновления подписей сообщений в Telegram (#latest / #trash / rename)
CREATE TABLE IF NOT EXISTS pending_caption_updates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    node_id INTEGER NOT NULL,
    tg_message_id INTEGER NOT NULL UNIQUE,
    new_caption TEXT NOT NULL,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    status INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_pending_caption_tg_msg ON pending_caption_updates(tg_message_id);

