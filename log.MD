[2026-09-27 02:01:03.873] [DEBUG] [WTelegram] 2>Sending   Channels_GetMessages                     #CDD0
[2026-09-27 02:01:03.988] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-26 23:01:03Z
[2026-09-27 02:01:03.988] [DEBUG] [WTelegram]              → Messages_ChannelMessages              #CDD0
[2026-09-27 02:01:03.988] [INFO ] [TelegramService] [MTProto] Запрос чанка для 'Addison Rae - Obsessed.m4a' (ID 752): смещение 0, размер 262 144 байт (0 / 5 496 932 байт)...
[2026-09-27 02:01:03.988] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-26 23:01:03Z (svc)
[2026-09-27 02:01:03.988] [DEBUG] [WTelegram]             → Upload_GetFile                         #C8B0
[2026-09-27 02:01:03.988] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-27 02:01:04.062] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-26 23:01:03Z
[2026-09-27 02:01:04.062] [DEBUG] [WTelegram]              → Upload_File                           #C8B0
[2026-09-27 02:01:04.062] [INFO ] [TelegramService] [MTProto] Получен чанк для 'Addison Rae - Obsessed.m4a': смещение 0, размер 262 144 байт, сохранен в RAM кэш.
[2026-09-27 02:01:04.063] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Запрос метаданных] для Addison Rae - Obsessed.m4a (32768/5496932)
[2026-09-27 02:01:04.063] [INFO ] [TelegramService] [MTProto] Запрос чанка для 'Addison Rae - Obsessed.m4a' (ID 752): смещение 5 242 880, размер 1 048 576 байт (5 361 664 / 5 496 932 байт)...
[2026-09-27 02:01:04.063] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-26 23:01:03Z (svc)
[2026-09-27 02:01:04.063] [INFO ] [MtprotoWorkerPool] [RAM Streaming] Старт скачивания 4 448 356 байт (диапазон 1 048 576..5 496 932, чанки #1..#5) через 3 параллельных воркера MTProto...
[2026-09-27 02:01:04.063] [DEBUG] [WTelegram]             → Upload_GetFile                         #AD00
[2026-09-27 02:01:04.063] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-27 02:01:04.063] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос Глобального Чанка #1 (смещение 1 048 576, размер 1024 КБ)...
[2026-09-27 02:01:04.063] [DEBUG] [WTelegram] 2>Sending   Upload_GetFile                           #F338
[2026-09-27 02:01:04.120] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-26 23:01:03Z
[2026-09-27 02:01:04.120] [DEBUG] [WTelegram]              → Upload_File                           #AD00
[2026-09-27 02:01:04.120] [INFO ] [TelegramService] [MTProto] Получен чанк для 'Addison Rae - Obsessed.m4a': смещение 5 242 880, размер 254 052 байт, сохранен в RAM кэш.
[2026-09-27 02:01:04.121] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для 'Addison Rae - Obsessed.m4a' (ID 752): Глобальный Чанк #5 (смещение 5 394 432, 32 768 байт).
[2026-09-27 02:01:04.121] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для 'Addison Rae - Obsessed.m4a' (ID 752): Глобальный Чанк #5 (смещение 5 427 200, 65 536 байт).
[2026-09-27 02:01:04.121] [INFO ] [TelegramService] [Cache RAM] Чтение из ОЗУ для 'Addison Rae - Obsessed.m4a' (ID 752): Глобальный Чанк #5 (смещение 5 492 736, 4 196 байт).
[2026-09-27 02:01:04.121] [INFO ] [TelegramService] [NetworkAudit] Завершено для 'Addison Rae - Obsessed.m4a': Размер: 5 496 932 б (5,24 МБ) | Из сети TG: 516 196 б (0,49 МБ) | Из ОЗУ: 102 500 б (0,10 МБ) | Эффективность сети: 1.00x (Идеально — 0% дублирования)
[2026-09-27 02:01:04.121] [INFO ] [TrayContext] Событие OnDownloadCompleted для 'Addison Rae - Obsessed.m4a' получено из TelegramService.
[2026-09-27 02:01:04.121] [INFO ] [TrayProgressOverlay] CompleteTransfer [Download] вызван для 'Addison Rae - Obsessed.m4a'. Запуск _completionTimer...
[2026-09-27 02:01:04.195] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-26 23:01:03Z
[2026-09-27 02:01:04.195] [DEBUG] [WTelegram]              → Upload_File                           #F338
[2026-09-27 02:01:04.195] [INFO ] [MtprotoWorkerPool] [Воркер #3] Успешно получен Глобальный Чанк #1 (1024 КБ за 131 мс).
[2026-09-27 02:01:04.271] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос Глобального Чанка #2 (смещение 2 097 152, размер 1024 КБ)...
[2026-09-27 02:01:04.271] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-26 23:01:03Z (svc)
[2026-09-27 02:01:04.271] [DEBUG] [WTelegram]             → Upload_GetFile                         #7A50
[2026-09-27 02:01:04.271] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-27 02:01:04.359] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-26 23:01:03Z
[2026-09-27 02:01:04.359] [DEBUG] [WTelegram]              → Upload_File                           #7A50
[2026-09-27 02:01:04.359] [INFO ] [MtprotoWorkerPool] [Воркер #1] Успешно получен Глобальный Чанк #2 (1024 КБ за 87 мс).
[2026-09-27 02:01:04.359] [INFO ] [MtprotoWorkerPool] [Воркер #1] Чанк #5 (смещение 5 242 880) уже имеется в RAM, сетевой запрос пропущен.
[2026-09-27 02:01:04.475] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос Глобального Чанка #3 (смещение 3 145 728, размер 1024 КБ)...
[2026-09-27 02:01:04.476] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-26 23:01:03Z (svc)
[2026-09-27 02:01:04.476] [DEBUG] [WTelegram]             → Upload_GetFile                         #3200
[2026-09-27 02:01:04.476] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-27 02:01:04.561] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-26 23:01:03Z
[2026-09-27 02:01:04.561] [DEBUG] [WTelegram]              → Upload_File                           #3200
[2026-09-27 02:01:04.562] [INFO ] [MtprotoWorkerPool] [Воркер #2] Успешно получен Глобальный Чанк #3 (1024 КБ за 86 мс).
[2026-09-27 02:01:04.680] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос Глобального Чанка #4 (смещение 4 194 304, размер 1024 КБ)...
[2026-09-27 02:01:04.680] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-26 23:01:03Z (svc)
[2026-09-27 02:01:04.680] [DEBUG] [WTelegram]             → Upload_GetFile                         #0F34
[2026-09-27 02:01:04.680] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-27 02:01:04.763] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-26 23:01:03Z
[2026-09-27 02:01:04.763] [DEBUG] [WTelegram]              → Upload_File                           #0F34
[2026-09-27 02:01:04.763] [INFO ] [MtprotoWorkerPool] [Воркер #3] Успешно получен Глобальный Чанк #4 (1024 КБ за 83 мс).
[2026-09-27 02:01:06.307] [INFO ] [TrayProgressOverlay] Таймаут неактивности передачи (1,5 сек). Запуск скрытия оверлея...
[2026-09-27 02:01:06.307] [INFO ] [TrayProgressOverlay] CompleteTransfer [Download] вызван для 'Addison Rae - Obsessed.m4a'. Запуск _completionTimer...
[2026-09-27 02:01:07.720] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-09-27 02:01:07.723] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.