====================================================================================================
=== 2026-09-29 05:18:03 | Telegram WebDAV & Network Drive Service ===
====================================================================================================
[2026-09-29 05:18:03.702] [INFO ] [AppLogger] Инициализирован файловый логгер. Путь: D:\Temp\TG webDAV\logs\app.log (лимит 5 МБ, ротация до 3 файлов).
[2026-09-29 05:18:03.713] [INFO ] [Program] =================================================
[2026-09-29 05:18:03.713] [INFO ] [Program]  Telegram WebDAV & Network Drive Service v2.0
[2026-09-29 05:18:03.713] [INFO ] [Program] =================================================
[2026-09-29 05:18:03.756] [INFO ] [Program] Конфигурация загружена. Порт: 37000, Диск: Y:
[2026-09-29 05:18:03.779] [INFO ] [Program] База данных SQLite инициализирована по пути: base.db
[2026-09-29 05:18:03.782] [INFO ] [TelegramService] Проверка сессии и подключение к Telegram...
[2026-09-29 05:18:03.782] [INFO ] [TelegramService] [Streaming Config] RAM Кэш: 128 МБ (TTL: 10 мин) | Автовыкачка треков до: 10 МБ | Окно стриминга: 20 МБ | Дисковый кэш: ВЫКЛ (100% RAM)
[2026-09-29 05:18:03.808] [INFO ] [WTelegram] Loaded previous session
[2026-09-29 05:18:03.810] [DEBUG] [WTelegram] WTelegramClient 4.1.6+e5c6086e1199c947131c2ef44b526ef51501cd67 running under .NET 8.0.31
[2026-09-29 05:18:03.822] [INFO ] [WTelegram] Connecting to 149.154.167.50:443...
[2026-09-29 05:18:03.898] [DEBUG] [WTelegram] 2>Sending   InvokeWithLayer`1                        #3F6F
[2026-09-29 05:18:04.001] [DEBUG] [WTelegram] 2>Receiving MsgContainer                             2026-09-29 02:18:04Z (svc)
[2026-09-29 05:18:04.034] [DEBUG] [WTelegram]             → MsgDetailedInfo                        2026-09-29 02:18:04Z (svc) 
[2026-09-29 05:18:04.035] [DEBUG] [WTelegram]             → MsgsAck                                2026-09-29 02:18:04Z (svc) 
[2026-09-29 05:18:04.037] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:04Z
[2026-09-29 05:18:04.039] [DEBUG] [WTelegram]              → Config                                #3F6F
[2026-09-29 05:18:04.041] [INFO ] [WTelegram] Connected to DC 2... default_p2p_contacts, revoke_pm_inbox
[2026-09-29 05:18:04.042] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:04Z (svc)
[2026-09-29 05:18:04.043] [DEBUG] [WTelegram]             → Users_GetUsers                         #B528
[2026-09-29 05:18:04.043] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:04.090] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:04Z
[2026-09-29 05:18:04.091] [DEBUG] [WTelegram]              → UserBase[]                            #B528
[2026-09-29 05:18:04.094] [INFO ] [TelegramService] Успешная авторизация пользователя: Vitaly (ID: 317057068)
[2026-09-29 05:18:04.099] [INFO ] [TelegramService] Подключен канал-хранилище из настроек: 'Telegram WebDAV Drive' (ID: 4323453199).
[2026-09-29 05:18:04.108] [INFO ] [WebDAV] WebDAV Сервер запущен на http://localhost:37000/
[2026-09-29 05:18:04.108] [INFO ] [Program] Встроенный WebDAV сервер запущен: http://localhost:37000/
[2026-09-29 05:18:04.111] [INFO ] [WinFsp] Нативная библиотека winfsp-x64.dll успешно предзагружена в память процесса из C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll
[2026-09-29 05:18:04.111] [INFO ] [WinFsp] Зарегистрирован путь к нативным DLL WinFsp: C:\Program Files (x86)\WinFsp\bin
[2026-09-29 05:18:04.175] [INFO ] [VirtualDriveManager] Выбран режим диска WinFsp. Подключение Y:...
[2026-09-29 05:18:04.180] [INFO ] [WinFsp] Попытка монтирования виртуального диска Y: через WinFsp...
[2026-09-29 05:18:04.190] [INFO ] [WinFsp] Виртуальный диск Y: успешно смонтирован через WinFsp (прямой стриминг в ОЗУ).
[2026-09-29 05:18:04.191] [INFO ] [Shell] Контекстное меню корзины WebDAV успешно зарегистрировано в реестре для Y:\
[2026-09-29 05:18:24.333] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:24.334] [DEBUG] [WTelegram]             → Channels_GetMessages                   #45F8
[2026-09-29 05:18:24.334] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:24.388] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:24.391] [DEBUG] [WTelegram]              → Messages_ChannelMessages              #45F8
[2026-09-29 05:18:24.398] [INFO ] [TelegramService] [MTProto] Запрос чанка #0/11 для 'Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3' (ID 1228): смещение 0, размер 256 КБ...
[2026-09-29 05:18:24.399] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:24.399] [DEBUG] [WTelegram]             → Upload_GetFile                         #0428
[2026-09-29 05:18:24.399] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:24.653] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:24.654] [DEBUG] [WTelegram]              → Upload_File                           #0428
[2026-09-29 05:18:24.657] [INFO ] [TelegramService] [MTProto] Получен чанк #0/11 для 'Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3': смещение 0, размер 256 КБ. Чанки: 1/11.
[2026-09-29 05:18:24.659] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для 'Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:24.659] [INFO ] [TelegramService] [MTProto] Запрос чанка #10/11 для 'Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3' (ID 1228): смещение 10 747 904, размер 256 КБ...
[2026-09-29 05:18:24.660] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:24.660] [DEBUG] [WTelegram]             → Upload_GetFile                         #7878
[2026-09-29 05:18:24.660] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:24.771] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:24.771] [DEBUG] [WTelegram]              → Upload_File                           #7878
[2026-09-29 05:18:24.772] [INFO ] [TelegramService] [MTProto] Получен чанк #10/11 для 'Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3': смещение 10 747 904, размер 59 КБ. Чанки: 2/11.
[2026-09-29 05:18:24.772] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3
[2026-09-29 05:18:24.774] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для 'Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:24.777] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:24.777] [DEBUG] [WTelegram]             → Channels_GetMessages                   #8790
[2026-09-29 05:18:24.777] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:24.831] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:24.831] [DEBUG] [WTelegram]              → Messages_ChannelMessages              #8790
[2026-09-29 05:18:24.831] [INFO ] [TelegramService] [MTProto] Запрос чанка #0/11 для 'Nina - Counting Stars.mp3' (ID 1224): смещение 0, размер 256 КБ...
[2026-09-29 05:18:24.831] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:24.831] [DEBUG] [WTelegram]             → Upload_GetFile                         #0430
[2026-09-29 05:18:24.831] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:24.926] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:24.928] [DEBUG] [WTelegram]              → Upload_File                           #0430
[2026-09-29 05:18:24.928] [INFO ] [TelegramService] [MTProto] Получен чанк #0/11 для 'Nina - Counting Stars.mp3': смещение 0, размер 256 КБ. Чанки: 1/11.
[2026-09-29 05:18:24.928] [INFO ] [TelegramService] [MTProto] Запрос чанка #10/11 для 'Nina - Counting Stars.mp3' (ID 1224): смещение 11 010 048, размер 256 КБ...
[2026-09-29 05:18:24.929] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:24.929] [DEBUG] [WTelegram]             → Upload_GetFile                         #E334
[2026-09-29 05:18:24.929] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:24.930] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для 'Nina - Counting Stars.mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:24.984] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:25Z
[2026-09-29 05:18:24.984] [DEBUG] [WTelegram]              → Upload_File                           #E334
[2026-09-29 05:18:24.984] [INFO ] [TelegramService] [MTProto] Получен чанк #10/11 для 'Nina - Counting Stars.mp3': смещение 11 010 048, размер 108 КБ. Чанки: 2/11.
[2026-09-29 05:18:24.984] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла Nina - Counting Stars.mp3
[2026-09-29 05:18:24.985] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для 'Nina - Counting Stars.mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:24.991] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:24.991] [DEBUG] [WTelegram]             → Channels_GetMessages                   #380D
[2026-09-29 05:18:24.991] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.046] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.046] [DEBUG] [WTelegram]              → Messages_ChannelMessages              #380D
[2026-09-29 05:18:25.046] [INFO ] [TelegramService] [MTProto] Запрос чанка #0/13 для 'Headhunters - Dayz Like That (3base club mix).mp3' (ID 1225): смещение 0, размер 256 КБ...
[2026-09-29 05:18:25.046] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.046] [DEBUG] [WTelegram]             → Upload_GetFile                         #B75D
[2026-09-29 05:18:25.046] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.143] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.143] [DEBUG] [WTelegram]              → Upload_File                           #B75D
[2026-09-29 05:18:25.144] [INFO ] [TelegramService] [MTProto] Получен чанк #0/13 для 'Headhunters - Dayz Like That (3base club mix).mp3': смещение 0, размер 256 КБ. Чанки: 1/13.
[2026-09-29 05:18:25.144] [INFO ] [TelegramService] [MTProto] Запрос чанка #12/13 для 'Headhunters - Dayz Like That (3base club mix).mp3' (ID 1225): смещение 12 582 912, размер 256 КБ...
[2026-09-29 05:18:25.144] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.144] [DEBUG] [WTelegram]             → Upload_GetFile                         #C83D
[2026-09-29 05:18:25.144] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.145] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для 'Headhunters - Dayz Like That (3base club mix).mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:25.200] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.200] [DEBUG] [WTelegram]              → Upload_File                           #C83D
[2026-09-29 05:18:25.200] [INFO ] [TelegramService] [MTProto] Получен чанк #12/13 для 'Headhunters - Dayz Like That (3base club mix).mp3': смещение 12 582 912, размер 89 КБ. Чанки: 2/13.
[2026-09-29 05:18:25.200] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла Headhunters - Dayz Like That (3base club mix).mp3
[2026-09-29 05:18:25.201] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для 'Headhunters - Dayz Like That (3base club mix).mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:25.210] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.210] [DEBUG] [WTelegram]             → Channels_GetMessages                   #AE3D
[2026-09-29 05:18:25.210] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.304] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.304] [DEBUG] [WTelegram]              → Messages_ChannelMessages              #AE3D
[2026-09-29 05:18:25.304] [INFO ] [TelegramService] [MTProto] Запрос чанка #0/13 для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' (ID 1226): смещение 0, размер 256 КБ...
[2026-09-29 05:18:25.305] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.305] [DEBUG] [WTelegram]             → Upload_GetFile                         #111D
[2026-09-29 05:18:25.305] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.412] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.412] [DEBUG] [WTelegram]              → Upload_File                           #111D
[2026-09-29 05:18:25.412] [INFO ] [TelegramService] [MTProto] Получен чанк #0/13 для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3': смещение 0, размер 256 КБ. Чанки: 1/13.
[2026-09-29 05:18:25.413] [INFO ] [TelegramService] [MTProto] Запрос чанка #12/13 для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' (ID 1226): смещение 13 107 200, размер 256 КБ...
[2026-09-29 05:18:25.413] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.413] [DEBUG] [WTelegram]             → Upload_GetFile                         #F071
[2026-09-29 05:18:25.413] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.414] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:25.470] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.470] [DEBUG] [WTelegram]              → Upload_File                           #F071
[2026-09-29 05:18:25.470] [INFO ] [TelegramService] [MTProto] Получен чанк #12/13 для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3': смещение 13 107 200, размер 81 КБ. Чанки: 2/13.
[2026-09-29 05:18:25.470] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла 09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3
[2026-09-29 05:18:25.470] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' (ID 1226): Глобальный Чанк #0 (смещение 32 768, 32 768 байт). Чанки: 2/13.
[2026-09-29 05:18:25.471] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' (ID 1226): Глобальный Чанк #0 (смещение 65 536, 32 768 байт). Чанки: 2/13.
[2026-09-29 05:18:25.471] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:25.471] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' (ID 1226): Глобальный Чанк #0 (смещение 98 304, 65 536 байт). Чанки: 2/13.
[2026-09-29 05:18:25.472] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' (ID 1226): Глобальный Чанк #0 (смещение 163 840, 98 304 байт). Чанки: 2/13.
[2026-09-29 05:18:25.479] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.479] [DEBUG] [WTelegram]             → Channels_GetMessages                   #1FED
[2026-09-29 05:18:25.479] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.479] [INFO ] [MtprotoWorkerPool] [RAM Streaming] Скачивание 11,58 МБ (чанки #1..#12 (всего 13)) через 3 воркеров MTProto...
[2026-09-29 05:18:25.482] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #3/13 (смещение 3 145 728 б, размер 1 048 576 б)...
[2026-09-29 05:18:25.483] [DEBUG] [WTelegram] 2>Sending   Upload_GetFile                           #47E9
[2026-09-29 05:18:25.542] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.542] [DEBUG] [WTelegram]              → Messages_ChannelMessages              #1FED
[2026-09-29 05:18:25.543] [INFO ] [TelegramService] [MTProto] Запрос чанка #0/14 для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3' (ID 1229): смещение 0, размер 256 КБ...
[2026-09-29 05:18:25.543] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.543] [DEBUG] [WTelegram]             → Upload_GetFile                         #A569
[2026-09-29 05:18:25.543] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.680] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #1/13 (смещение 1 048 576 б, размер 1 048 576 б)...
[2026-09-29 05:18:25.680] [DEBUG] [WTelegram] 2>Sending   Upload_GetFile                           #FD79
[2026-09-29 05:18:25.703] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.703] [DEBUG] [WTelegram]              → Upload_File                           #47E9
[2026-09-29 05:18:25.704] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #3/13 (1 048 576 б за 220 мс) [Полный].
[2026-09-29 05:18:25.704] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла 09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3
[2026-09-29 05:18:25.709] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.709] [DEBUG] [WTelegram]              → Upload_File                           #A569
[2026-09-29 05:18:25.710] [INFO ] [TelegramService] [MTProto] Получен чанк #0/14 для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3': смещение 0, размер 256 КБ. Чанки: 1/14.
[2026-09-29 05:18:25.710] [INFO ] [TelegramService] [MTProto] Запрос чанка #13/14 для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3' (ID 1229): смещение 13 631 488, размер 256 КБ...
[2026-09-29 05:18:25.710] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:25.710] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.710] [DEBUG] [WTelegram]             → Upload_GetFile                         #2E7D
[2026-09-29 05:18:25.710] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.771] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.772] [DEBUG] [WTelegram]              → Upload_File                           #FD79
[2026-09-29 05:18:25.772] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #1/13 (1 048 576 б за 92 мс) [Полный].
[2026-09-29 05:18:25.772] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла 09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3
[2026-09-29 05:18:25.798] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:24Z
[2026-09-29 05:18:25.798] [DEBUG] [WTelegram]              → Upload_File                           #2E7D
[2026-09-29 05:18:25.798] [INFO ] [TelegramService] [MTProto] Получен чанк #13/14 для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3': смещение 13 631 488, размер 228 КБ. Чанки: 2/14.
[2026-09-29 05:18:25.798] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3' (ID 1229): Глобальный Чанк #0 (смещение 32 768, 32 768 байт). Чанки: 2/14.
[2026-09-29 05:18:25.799] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-29 05:18:25.799] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3' (ID 1229): Глобальный Чанк #0 (смещение 65 536, 32 768 байт). Чанки: 2/14.
[2026-09-29 05:18:25.800] [INFO ] [MtprotoWorkerPool] [RAM Streaming] Скачивание 12,00 МБ (чанки #1..#12 (всего 14)) через 3 воркеров MTProto...
[2026-09-29 05:18:25.880] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #2/13 (смещение 2 097 152 б, размер 1 048 576 б)...
[2026-09-29 05:18:25.881] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:24Z (svc)
[2026-09-29 05:18:25.881] [DEBUG] [WTelegram]             → Upload_GetFile                         #92FD
[2026-09-29 05:18:25.881] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:25.971] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:25Z
[2026-09-29 05:18:25.971] [DEBUG] [WTelegram]              → Upload_File                           #92FD
[2026-09-29 05:18:25.971] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #2/13 (1 048 576 б за 91 мс) [Полный].
[2026-09-29 05:18:25.972] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла 09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3
[2026-09-29 05:18:26.081] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #4/13 (смещение 4 194 304 б, размер 1 048 576 б)...
[2026-09-29 05:18:26.081] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:25Z (svc)
[2026-09-29 05:18:26.081] [DEBUG] [WTelegram]             → Upload_GetFile                         #D5EA
[2026-09-29 05:18:26.082] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:26.171] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:25Z
[2026-09-29 05:18:26.172] [DEBUG] [WTelegram]              → Upload_File                           #D5EA
[2026-09-29 05:18:26.172] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #4/13 (1 048 576 б за 90 мс) [Полный].
[2026-09-29 05:18:26.282] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #5/13 (смещение 5 242 880 б, размер 1 048 576 б)...
[2026-09-29 05:18:26.283] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:25Z (svc)
[2026-09-29 05:18:26.283] [DEBUG] [WTelegram]             → Upload_GetFile                         #EC9A
[2026-09-29 05:18:26.283] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:26.377] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:25Z
[2026-09-29 05:18:26.377] [DEBUG] [WTelegram]              → Upload_File                           #EC9A
[2026-09-29 05:18:26.378] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #5/13 (1 048 576 б за 95 мс) [Полный].
[2026-09-29 05:18:26.483] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #1/14 (смещение 1 048 576 б, размер 1 048 576 б)...
[2026-09-29 05:18:26.483] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:25Z (svc)
[2026-09-29 05:18:26.483] [DEBUG] [WTelegram]             → Upload_GetFile                         #7FC6
[2026-09-29 05:18:26.483] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:26.592] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:25Z
[2026-09-29 05:18:26.592] [DEBUG] [WTelegram]              → Upload_File                           #7FC6
[2026-09-29 05:18:26.593] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #1/14 (1 048 576 б за 109 мс) [Полный].
[2026-09-29 05:18:26.684] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #2/14 (смещение 2 097 152 б, размер 1 048 576 б)...
[2026-09-29 05:18:26.684] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:25Z (svc)
[2026-09-29 05:18:26.684] [DEBUG] [WTelegram]             → Upload_GetFile                         #A70E
[2026-09-29 05:18:26.684] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:26.778] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:25Z
[2026-09-29 05:18:26.779] [DEBUG] [WTelegram]              → Upload_File                           #A70E
[2026-09-29 05:18:26.779] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #2/14 (1 048 576 б за 95 мс) [Полный].
[2026-09-29 05:18:26.884] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #3/14 (смещение 3 145 728 б, размер 1 048 576 б)...
[2026-09-29 05:18:26.885] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:25Z (svc)
[2026-09-29 05:18:26.885] [DEBUG] [WTelegram]             → Upload_GetFile                         #3FE2
[2026-09-29 05:18:26.885] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:26.986] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:26Z
[2026-09-29 05:18:26.986] [DEBUG] [WTelegram]              → Upload_File                           #3FE2
[2026-09-29 05:18:26.987] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #3/14 (1 048 576 б за 102 мс) [Полный].
[2026-09-29 05:18:27.085] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #6/13 (смещение 6 291 456 б, размер 1 048 576 б)...
[2026-09-29 05:18:27.086] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:26Z (svc)
[2026-09-29 05:18:27.086] [DEBUG] [WTelegram]             → Upload_GetFile                         #D0CB
[2026-09-29 05:18:27.086] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:27.177] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:26Z
[2026-09-29 05:18:27.177] [DEBUG] [WTelegram]              → Upload_File                           #D0CB
[2026-09-29 05:18:27.177] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #6/13 (1 048 576 б за 92 мс) [Полный].
[2026-09-29 05:18:27.286] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #7/13 (смещение 7 340 032 б, размер 1 048 576 б)...
[2026-09-29 05:18:27.287] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:26Z (svc)
[2026-09-29 05:18:27.287] [DEBUG] [WTelegram]             → Upload_GetFile                         #4B53
[2026-09-29 05:18:27.287] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:27.378] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:26Z
[2026-09-29 05:18:27.379] [DEBUG] [WTelegram]              → Upload_File                           #4B53
[2026-09-29 05:18:27.379] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #7/13 (1 048 576 б за 92 мс) [Полный].
[2026-09-29 05:18:27.487] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #8/13 (смещение 8 388 608 б, размер 1 048 576 б)...
[2026-09-29 05:18:27.488] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:26Z (svc)
[2026-09-29 05:18:27.488] [DEBUG] [WTelegram]             → Upload_GetFile                         #5923
[2026-09-29 05:18:27.488] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:27.579] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:26Z
[2026-09-29 05:18:27.580] [DEBUG] [WTelegram]              → Upload_File                           #5923
[2026-09-29 05:18:27.580] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #8/13 (1 048 576 б за 92 мс) [Полный].
[2026-09-29 05:18:27.688] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #4/14 (смещение 4 194 304 б, размер 1 048 576 б)...
[2026-09-29 05:18:27.689] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:26Z (svc)
[2026-09-29 05:18:27.689] [DEBUG] [WTelegram]             → Upload_GetFile                         #83EB
[2026-09-29 05:18:27.689] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:27.785] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:26Z
[2026-09-29 05:18:27.786] [DEBUG] [WTelegram]              → Upload_File                           #83EB
[2026-09-29 05:18:27.787] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #4/14 (1 048 576 б за 97 мс) [Полный].
[2026-09-29 05:18:27.889] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #5/14 (смещение 5 242 880 б, размер 1 048 576 б)...
[2026-09-29 05:18:27.889] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:26Z (svc)
[2026-09-29 05:18:27.889] [DEBUG] [WTelegram]             → Upload_GetFile                         #BAB7
[2026-09-29 05:18:27.889] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:27.980] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:27Z
[2026-09-29 05:18:27.980] [DEBUG] [WTelegram]              → Upload_File                           #BAB7
[2026-09-29 05:18:27.980] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #5/14 (1 048 576 б за 90 мс) [Полный].
[2026-09-29 05:18:28.099] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #6/14 (смещение 6 291 456 б, размер 1 048 576 б)...
[2026-09-29 05:18:28.099] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:27Z (svc)
[2026-09-29 05:18:28.099] [DEBUG] [WTelegram]             → Upload_GetFile                         #3180
[2026-09-29 05:18:28.099] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:28.188] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:27Z
[2026-09-29 05:18:28.189] [DEBUG] [WTelegram]              → Upload_File                           #3180
[2026-09-29 05:18:28.189] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #6/14 (1 048 576 б за 89 мс) [Полный].
[2026-09-29 05:18:28.299] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #9/13 (смещение 9 437 184 б, размер 1 048 576 б)...
[2026-09-29 05:18:28.300] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:27Z (svc)
[2026-09-29 05:18:28.300] [DEBUG] [WTelegram]             → Upload_GetFile                         #2E3C
[2026-09-29 05:18:28.300] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:28.391] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:27Z
[2026-09-29 05:18:28.391] [DEBUG] [WTelegram]              → Upload_File                           #2E3C
[2026-09-29 05:18:28.392] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #9/13 (1 048 576 б за 91 мс) [Полный].
[2026-09-29 05:18:28.500] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #10/13 (смещение 10 485 760 б, размер 1 048 576 б)...
[2026-09-29 05:18:28.500] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:27Z (svc)
[2026-09-29 05:18:28.500] [DEBUG] [WTelegram]             → Upload_GetFile                         #11B8
[2026-09-29 05:18:28.500] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:28.594] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:27Z
[2026-09-29 05:18:28.595] [DEBUG] [WTelegram]              → Upload_File                           #11B8
[2026-09-29 05:18:28.595] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #10/13 (1 048 576 б за 94 мс) [Полный].
[2026-09-29 05:18:28.700] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #11/13 (смещение 11 534 336 б, размер 1 048 576 б)...
[2026-09-29 05:18:28.701] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:27Z (svc)
[2026-09-29 05:18:28.701] [DEBUG] [WTelegram]             → Upload_GetFile                         #D3B0
[2026-09-29 05:18:28.701] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:28.791] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:27Z
[2026-09-29 05:18:28.791] [DEBUG] [WTelegram]              → Upload_File                           #D3B0
[2026-09-29 05:18:28.792] [INFO ] [TelegramService] [NetworkAudit] Завершено для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3': Размер: 13 191 016 б (12,58 МБ) | Из сети TG: 11 880 296 б (11,33 МБ) | Из ОЗУ: 229 376 б (0,22 МБ) | Эффективность сети: 1.00x (Идеально — 0% дублирования)
[2026-09-29 05:18:28.792] [INFO ] [TrayContext] Событие OnDownloadCompleted для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' получено из TelegramService.
[2026-09-29 05:18:28.792] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #11/13 (1 048 576 б за 91 мс) [Полный].
[2026-09-29 05:18:28.793] [INFO ] [TrayProgressOverlay] CompleteTransfer [Download] вызван для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3'. Запуск _completionTimer...
[2026-09-29 05:18:28.794] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла 09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3
[2026-09-29 05:18:28.901] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #7/14 (смещение 7 340 032 б, размер 1 048 576 б)...
[2026-09-29 05:18:28.901] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:27Z (svc)
[2026-09-29 05:18:28.901] [DEBUG] [WTelegram]             → Upload_GetFile                         #2E3C
[2026-09-29 05:18:28.901] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:28.995] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:28Z
[2026-09-29 05:18:28.995] [DEBUG] [WTelegram]              → Upload_File                           #2E3C
[2026-09-29 05:18:28.996] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #7/14 (1 048 576 б за 94 мс) [Полный].
[2026-09-29 05:18:29.101] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #8/14 (смещение 8 388 608 б, размер 1 048 576 б)...
[2026-09-29 05:18:29.102] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:28Z (svc)
[2026-09-29 05:18:29.102] [DEBUG] [WTelegram]             → Upload_GetFile                         #4719
[2026-09-29 05:18:29.102] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:29.235] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:28Z
[2026-09-29 05:18:29.235] [DEBUG] [WTelegram]              → Upload_File                           #4719
[2026-09-29 05:18:29.235] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #8/14 (1 048 576 б за 133 мс) [Полный].
[2026-09-29 05:18:29.301] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #9/14 (смещение 9 437 184 б, размер 1 048 576 б)...
[2026-09-29 05:18:29.301] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:28Z (svc)
[2026-09-29 05:18:29.301] [DEBUG] [WTelegram]             → Upload_GetFile                         #EDA1
[2026-09-29 05:18:29.301] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:29.406] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:28Z
[2026-09-29 05:18:29.407] [DEBUG] [WTelegram]              → Upload_File                           #EDA1
[2026-09-29 05:18:29.407] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #9/14 (1 048 576 б за 105 мс) [Полный].
[2026-09-29 05:18:29.502] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #12/13 (смещение 12 582 912 б, размер 1 048 576 б)...
[2026-09-29 05:18:29.502] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:28Z (svc)
[2026-09-29 05:18:29.502] [DEBUG] [WTelegram]             → Upload_GetFile                         #E315
[2026-09-29 05:18:29.502] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:29.576] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:28Z
[2026-09-29 05:18:29.576] [DEBUG] [WTelegram]              → Upload_File                           #E315
[2026-09-29 05:18:29.576] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #12/13 (608 104 б за 74 мс) [608 104 б из 1 048 576 б, Хвост EOF].
[2026-09-29 05:18:29.702] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #10/14 (смещение 10 485 760 б, размер 1 048 576 б)...
[2026-09-29 05:18:29.703] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:28Z (svc)
[2026-09-29 05:18:29.703] [DEBUG] [WTelegram]             → Upload_GetFile                         #F855
[2026-09-29 05:18:29.703] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:29.794] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:28Z
[2026-09-29 05:18:29.795] [DEBUG] [WTelegram]              → Upload_File                           #F855
[2026-09-29 05:18:29.795] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #10/14 (1 048 576 б за 92 мс) [Полный].
[2026-09-29 05:18:29.902] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #11/14 (смещение 11 534 336 б, размер 1 048 576 б)...
[2026-09-29 05:18:29.903] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:28Z (svc)
[2026-09-29 05:18:29.903] [DEBUG] [WTelegram]             → Upload_GetFile                         #E601
[2026-09-29 05:18:29.903] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:29.991] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:29Z
[2026-09-29 05:18:29.991] [DEBUG] [WTelegram]              → Upload_File                           #E601
[2026-09-29 05:18:29.991] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #11/14 (1 048 576 б за 88 мс) [Полный].
[2026-09-29 05:18:30.103] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #12/14 (смещение 12 582 912 б, размер 1 048 576 б)...
[2026-09-29 05:18:30.103] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:18:29Z (svc)
[2026-09-29 05:18:30.103] [DEBUG] [WTelegram]             → Upload_GetFile                         #A5E6
[2026-09-29 05:18:30.103] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:18:30.198] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-29 02:18:29Z
[2026-09-29 05:18:30.198] [DEBUG] [WTelegram]              → Upload_File                           #A5E6
[2026-09-29 05:18:30.198] [INFO ] [TelegramService] [NetworkAudit] Завершено для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3': Размер: 13 865 469 б (13,22 МБ) | Из сети TG: 13 079 037 б (12,47 МБ) | Из ОЗУ: 65 536 б (0,06 МБ) | Эффективность сети: 1.00x (Идеально — 0% дублирования)
[2026-09-29 05:18:30.198] [INFO ] [TrayContext] Событие OnDownloadCompleted для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3' получено из TelegramService.
[2026-09-29 05:18:30.198] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #12/14 (1 048 576 б за 95 мс) [Полный].
[2026-09-29 05:18:30.198] [INFO ] [TrayProgressOverlay] CompleteTransfer [Download] вызван для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3'. Запуск _completionTimer...
[2026-09-29 05:18:30.200] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла [FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3
[2026-09-29 05:18:31.826] [INFO ] [TrayProgressOverlay] Таймаут неактивности передачи (1,6 сек).
[2026-09-29 05:18:50.451] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '[FDM] Ben Delay feat. Alexandra Prince - The Boy Is Mine (Original Extended Remix) [320 kbps] [13 mb).mp3' (ID 1229): Глобальный Чанк #13 (смещение 13 864 960, 509 байт). Чанки: 14/14.
[2026-09-29 05:18:52.914] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для '09. Исполнитель записи - Запись 9 (есть обложка, кракозябры).mp3' (ID 1226): Глобальный Чанк #12 (смещение 13 189 120, 1 896 байт). Чанки: 13/13.
[2026-09-29 05:18:55.073] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для 'Headhunters - Dayz Like That (3base club mix).mp3' (ID 1225): Глобальный Чанк #12 (смещение 12 673 024, 2 007 байт). Чанки: 2/13.
[2026-09-29 05:18:57.035] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для 'Nina - Counting Stars.mp3' (ID 1224): Глобальный Чанк #10 (смещение 11 120 640, 266 байт). Чанки: 2/11.
[2026-09-29 05:18:59.193] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для 'Tina Cousins - Wonderful Life (Kenny Hayes Suu.mp3' (ID 1228): Глобальный Чанк #10 (смещение 10 805 248, 3 848 байт). Чанки: 2/11.
[2026-09-29 05:19:03.877] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-29 02:19:02Z (svc)
[2026-09-29 05:19:03.877] [DEBUG] [WTelegram]             → PingDelayDisconnect                    #55A3
[2026-09-29 05:19:03.877] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-29 05:19:03.920] [DEBUG] [WTelegram] 2>Receiving Pong                                     2026-09-29 02:19:02Z (svc) 