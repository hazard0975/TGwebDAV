# 🗺️ Project Architecture & File Map (C# .NET 8.0 Windows)

Карта структуры файлов, компонентов и потоков данных проекта **Telegram WebDAV & Virtual Network Drive**.

---

## 📁 Структура каталогов и файлов проекта

```text
/TelegramWebDAV
├── TelegramWebDAV.csproj         # Файл проекта (.NET 8.0-windows, WinForms, x64)
├── Program.cs                    # Точка входа: инициализация БД, конфига, старт сервера и трея
├── appsettings.json              # Конфигурация по умолчанию (порты, пути к базе, параметры диска)
│
├── Config/                       # Управление конфигурацией
│   ├── AppSettings.cs            # Модель данных настроек (Telegram, Server, Database)
│   └── ConfigManager.cs          # Чтение и сохранение appsettings.json
│
├── Database/                     # Слой данных (SQLite WAL)
│   ├── Schema.sql                # SQL-скрипт инициализации таблиц с нуля (nodes, upload_progress)
│   ├── DatabaseManager.cs        # Создание файла base.db и применение PRAGMA journal_mode=WAL
│   └── NodeRepository.cs         # CRUD-операции, версионирование (v1/v2), корзина (.Trash), аудио-теги
│
├── Models/                       # Доменные модели
│   ├── Node.cs                   # Модель файла/папки (id, parent_id, size, аудио-теги, заголовочный кэш)
│   └── UploadProgress.cs         # Модель для докачки прерванных загрузок (offset, total_size, hash)
│
├── Server/                       # WebDAV / HTTP сервер
│   ├── WebDavServer.cs           # Фоновый HttpListener (порт 37000)
│   └── WebDavMiddleware.cs       # Роутинг WebDAV-методов:
│                                 #  - PROPFIND (XML-дерево файлов из SQLite)
│                                 #  - GET (HTTP 206 Partial Content / Range-запросы для перемотки)
│                                 #  - PUT (Загрузка файлов, нарезка чанков, извлечение тегов)
│                                 #  - MKCOL (Создание директорий)
│                                 #  - DELETE (Мягкое перемещение в .Trash)
│                                 #  - MOVE (Переименование и перемещение)
│
├── Services/                     # Интеграции и фоновые службы
│   ├── TelegramService.cs        # Клиент MTProto (WTelegramClient):
│   │                             #  - Управление файлом сессии (.session)
│   │                             #  - Пошаговая авторизация (Телефон -> SMS -> 2FA)
│   │                             #  - Потоковая передача данных через ArrayPool<byte>
│   │                             #  - Перехват и удержание FLOOD_WAIT без разрыва связи
│   └── AudioMetadataExtractor.cs # Интеграция с ATL.NET:
│                                 #  - Извлечение ID3v1, ID3v2, FLAC тегов
│                                 #  - Кэширование первых 128 КБ заголовка (header_cache_bytes)
│
├── UI/                           # Пользовательский интерфейс Windows
│   ├── TrayContext.cs            # Иконка в системном трее (NotifyIcon) и контекстное меню
│   └── AuthSettingsForm.cs       # Нативное диалоговое окно настроек и авторизации (WinForms)
│
└── Utils/                        # Системные утилиты Windows
    ├── NetworkDriveMounter.cs    # WinAPI (mpr.dll / WNetAddConnection2W) монтирование диска Z:
    └── WindowsRegistryFixer.cs   # Настройка реестра Windows (BasicAuthLevel=2) для работы в LAN
```

---

## 🔄 Потоки данных (Data Flow)

### 1. Чтение и Воспроизведение (AIMP, VLC, Проводник Windows)
```text
[Плеер AIMP / Проводник]
       │
       │ HTTP GET /Music/Track.mp3 (Range: bytes=0-131071)
       ▼
[WebDavMiddleware]
       │
       ├─► 1. Проверяет наличие кэша заголовка в SQLite (base.db)
       │      └─► [NodeRepository] -> Отдает первые 128 КБ мгновенно (0.4 мс)
       │
       └─► 2. При запросе основного тела файла (Range: bytes=131072-N)
              └─► [TelegramService] -> Потоковый стриминг из Telegram через ArrayPool<byte>
```

### 2. Запись и Загрузка (Проводник, Rclone, Total Commander)
```text
[Клиент / Проводник]
       │
       │ HTTP PUT /Music/NewTrack.mp3 (Stream)
       ▼
[WebDavMiddleware]
       │
       ├─► [AudioMetadataExtractor (ATL.NET)] -> Читает теги ID3/FLAC и кэширует 128 КБ заголовка
       │
       ├─► [TelegramService] -> Загрузка чанками по 512 КБ в Saved Messages
       │
       └─► [NodeRepository] -> Сохранение записи в SQLite (base.db)
```

### 3. Перезапись файла и Версионирование
1. При перезаписи существующего файла `WebDavMiddleware` находит прежнюю запись в `base.db`.
2. Прежний файл переименовывается в `имя_файла_v1.ext` и мягко переносится в виртуальную директорию `.Trash`.
3. Новый файл сохраняется с версией `v2`.

---

## ⚙️ Спецификация конфигурации (appsettings.json)

```json
{
  "Telegram": {
    "ApiId": 0,
    "ApiHash": "",
    "SessionPath": "user.session"
  },
  "Server": {
    "Port": 37000,
    "WebDavEnabled": true,
    "MountDrive": true,
    "DriveLetter": "Z:",
    "DriveName": "Telegram Drive",
    "AutoStartWithWindows": true,
    "TrashRetentionDays": 30
  },
  "Database": {
    "Path": "base.db"
  }
}
```
