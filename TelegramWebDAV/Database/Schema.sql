-- Включение режима WAL для безопасного конкурентного доступа
PRAGMA journal_mode=WAL;

-- Таблица узлов виртуальной файловой системы (Файлы и Папки)
CREATE TABLE IF NOT EXISTS nodes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_id INTEGER,
    name TEXT NOT NULL,
    is_dir INTEGER NOT NULL DEFAULT 0,
    size INTEGER NOT NULL DEFAULT 0,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    
    -- Telegram специфика
    tg_message_id INTEGER,
    
    -- Версионирование и Корзина
    version INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    original_node_id INTEGER, -- Ссылка на актуальный файл, если это старая версия в корзине
    
    -- Метаданные аудио
    artist TEXT,
    title TEXT,
    album TEXT,
    year INTEGER,
    genre TEXT,
    track_number INTEGER,
    duration_seconds INTEGER,
    bitrate INTEGER,
    
    -- Кэш (заголовки и обложки)
    header_cache_bytes BLOB,
    album_cover_bytes BLOB,
    
    FOREIGN KEY (parent_id) REFERENCES nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (original_node_id) REFERENCES nodes(id) ON DELETE CASCADE
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
CREATE INDEX IF NOT EXISTS idx_nodes_is_deleted ON nodes(is_deleted);
CREATE INDEX IF NOT EXISTS idx_upload_progress_node_id ON upload_progress(node_id);
