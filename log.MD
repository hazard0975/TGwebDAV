[2026-09-30 17:29:17.779] [INFO ] [TelegramService] [MTProto] Запрос чанка #0/12 для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): смещение 0, размер 256 КБ...
[2026-09-30 17:29:17.779] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:17Z (svc)
[2026-09-30 17:29:17.779] [DEBUG] [WTelegram]             → Upload_GetFile                         #51C5
[2026-09-30 17:29:17.780] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:18.010] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:17Z
[2026-09-30 17:29:18.011] [DEBUG] [WTelegram]              → Upload_File                           #51C5
[2026-09-30 17:29:18.015] [INFO ] [TelegramService] [MTProto] Получен чанк #0/12 для 'IMG_20201018_131419 (12 mb).jpg': смещение 0, размер 256 КБ. Скачано всего: 1 из 12 чанков (8,3%).
[2026-09-30 17:29:18.016] [INFO ] [TrayProgressOverlay] CompleteMetadata вызван для 'IMG_20201018_131419 (12 mb).jpg'. Показ оверлея 'МЕТАДАННЫЕ ПОЛУЧЕНЫ'...
[2026-09-30 17:29:19.446] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-09-30 17:29:19.448] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-09-30 17:29:23.688] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:23Z (svc)
[2026-09-30 17:29:23.688] [DEBUG] [WTelegram]             → PingDelayDisconnect                    #8DB7
[2026-09-30 17:29:23.688] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:23.730] [DEBUG] [WTelegram] 2>Receiving Pong                                     2026-09-30 14:29:23Z (svc) 
[2026-09-30 17:29:23.998] [DEBUG] [WTelegram] 2>Receiving Updates                                  2026-09-30 14:29:23Z  
[2026-09-30 17:29:52.320] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #0 (смещение 0, 262 144 байт). Скачано всего: 1 из 12 чанков (8,3%).
[2026-09-30 17:29:52.324] [INFO ] [MtprotoWorkerPool] [RAM Streaming] Скачивание 1,00 МБ (чанки #1..#3 (всего 12)) через 3 воркеров MTProto...
[2026-09-30 17:29:52.327] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #1/12 (смещение 1 048 576 б, размер 1 048 576 б)...
[2026-09-30 17:29:52.328] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:52Z (svc)
[2026-09-30 17:29:52.328] [DEBUG] [WTelegram]             → Upload_GetFile                         #B868
[2026-09-30 17:29:52.328] [INFO ] [MtprotoWorkerPool] [RAM Streaming] Скачивание 5,00 МБ (чанки #1..#5 (всего 12)) через 3 воркеров MTProto...
[2026-09-30 17:29:52.328] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:52.531] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #2/12 (смещение 2 097 152 б, размер 1 048 576 б)...
[2026-09-30 17:29:52.531] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:52Z
[2026-09-30 17:29:52.531] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:52Z (svc)
[2026-09-30 17:29:52.531] [DEBUG] [WTelegram]             → Upload_GetFile                         #F064
[2026-09-30 17:29:52.531] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:52.531] [DEBUG] [WTelegram]              → Upload_File                           #B868
[2026-09-30 17:29:52.533] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #1/12 (1 048 576 б за 204 мс) [Полный].
[2026-09-30 17:29:52.533] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Download] для IMG_20201018_131419 (12 mb).jpg (2097152/12414274)
[2026-09-30 17:29:52.651] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:52Z
[2026-09-30 17:29:52.652] [DEBUG] [WTelegram]              → Upload_File                           #F064
[2026-09-30 17:29:52.652] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #2/12 (1 048 576 б за 120 мс) [Полный].
[2026-09-30 17:29:52.652] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #2 (смещение 2 097 152, 1 048 576 байт). Скачано всего: 3 из 12 чанков (25%).
[2026-09-30 17:29:52.730] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #3/12 (смещение 3 145 728 б, размер 1 048 576 б)...
[2026-09-30 17:29:52.731] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:52Z (svc)
[2026-09-30 17:29:52.731] [DEBUG] [WTelegram]             → Upload_GetFile                         #47B4
[2026-09-30 17:29:52.731] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:52.823] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:52Z
[2026-09-30 17:29:52.823] [DEBUG] [WTelegram]              → Upload_File                           #47B4
[2026-09-30 17:29:52.824] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #3 (смещение 3 145 728, 1 048 576 байт). Скачано всего: 4 из 12 чанков (33,3%).
[2026-09-30 17:29:52.825] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #3/12 (1 048 576 б за 93 мс) [Полный].
[2026-09-30 17:29:52.934] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #1/12 (смещение 1 048 576 б, размер 1 048 576 б)...
[2026-09-30 17:29:52.935] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:52Z (svc)
[2026-09-30 17:29:52.935] [DEBUG] [WTelegram]             → Upload_GetFile                         #4ADC
[2026-09-30 17:29:52.935] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:53.060] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:53Z
[2026-09-30 17:29:53.060] [DEBUG] [WTelegram]              → Upload_File                           #4ADC
[2026-09-30 17:29:53.061] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #1/12 (1 048 576 б за 125 мс) [Полный].
[2026-09-30 17:29:53.136] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #2/12 (смещение 2 097 152 б, размер 1 048 576 б)...
[2026-09-30 17:29:53.137] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:53Z (svc)
[2026-09-30 17:29:53.137] [DEBUG] [WTelegram]             → Upload_GetFile                         #71D1
[2026-09-30 17:29:53.137] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:53.269] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:53Z
[2026-09-30 17:29:53.270] [DEBUG] [WTelegram]              → Upload_File                           #71D1
[2026-09-30 17:29:53.270] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #2/12 (1 048 576 б за 133 мс) [Полный].
[2026-09-30 17:29:53.338] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #3/12 (смещение 3 145 728 б, размер 1 048 576 б)...
[2026-09-30 17:29:53.339] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:53Z (svc)
[2026-09-30 17:29:53.339] [DEBUG] [WTelegram]             → Upload_GetFile                         #2335
[2026-09-30 17:29:53.339] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:53.492] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:53Z
[2026-09-30 17:29:53.492] [DEBUG] [WTelegram]              → Upload_File                           #2335
[2026-09-30 17:29:53.492] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #3/12 (1 048 576 б за 153 мс) [Полный].
[2026-09-30 17:29:53.545] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #4/12 (смещение 4 194 304 б, размер 1 048 576 б)...
[2026-09-30 17:29:53.546] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:53Z (svc)
[2026-09-30 17:29:53.546] [DEBUG] [WTelegram]             → Upload_GetFile                         #8E95
[2026-09-30 17:29:53.546] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:53.665] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:53Z
[2026-09-30 17:29:53.666] [DEBUG] [WTelegram]              → Upload_File                           #8E95
[2026-09-30 17:29:53.666] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #4/12 (1 048 576 б за 120 мс) [Полный].
[2026-09-30 17:29:53.666] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #4 (смещение 4 194 304, 1 048 576 байт). Скачано всего: 5 из 12 чанков (41,7%).
[2026-09-30 17:29:53.751] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #5/12 (смещение 5 242 880 б, размер 1 048 576 б)...
[2026-09-30 17:29:53.752] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:53Z (svc)
[2026-09-30 17:29:53.752] [DEBUG] [WTelegram]             → Upload_GetFile                         #2D5D
[2026-09-30 17:29:53.752] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:53.855] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:53Z
[2026-09-30 17:29:53.856] [DEBUG] [WTelegram]              → Upload_File                           #2D5D
[2026-09-30 17:29:53.856] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #5/12 (1 048 576 б за 104 мс) [Полный].
[2026-09-30 17:29:53.856] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #5 (смещение 5 242 880, 1 048 576 байт). Скачано всего: 6 из 12 чанков (50%).
[2026-09-30 17:29:53.857] [INFO ] [MtprotoWorkerPool] [RAM Streaming] Скачивание 4,00 МБ (чанки #6..#9 (всего 12)) через 3 воркеров MTProto...
[2026-09-30 17:29:53.954] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #6/12 (смещение 6 291 456 б, размер 1 048 576 б)...
[2026-09-30 17:29:53.955] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:53Z (svc)
[2026-09-30 17:29:53.955] [DEBUG] [WTelegram]             → Upload_GetFile                         #4391
[2026-09-30 17:29:53.955] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:54.121] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:54Z
[2026-09-30 17:29:54.122] [DEBUG] [WTelegram]              → Upload_File                           #4391
[2026-09-30 17:29:54.123] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #6/12 (1 048 576 б за 167 мс) [Полный].
[2026-09-30 17:29:54.123] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #6 (смещение 6 291 456, 1 048 576 байт). Скачано всего: 7 из 12 чанков (58,3%).
[2026-09-30 17:29:54.156] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #7/12 (смещение 7 340 032 б, размер 1 048 576 б)...
[2026-09-30 17:29:54.156] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:54Z (svc)
[2026-09-30 17:29:54.156] [DEBUG] [WTelegram]             → Upload_GetFile                         #6D8E
[2026-09-30 17:29:54.157] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:54.249] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:54Z
[2026-09-30 17:29:54.249] [DEBUG] [WTelegram]              → Upload_File                           #6D8E
[2026-09-30 17:29:54.250] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #7/12 (1 048 576 б за 93 мс) [Полный].
[2026-09-30 17:29:54.250] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #7 (смещение 7 340 032, 1 048 576 байт). Скачано всего: 8 из 12 чанков (66,7%).
[2026-09-30 17:29:54.359] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #8/12 (смещение 8 388 608 б, размер 1 048 576 б)...
[2026-09-30 17:29:54.359] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:54Z (svc)
[2026-09-30 17:29:54.359] [DEBUG] [WTelegram]             → Upload_GetFile                         #4982
[2026-09-30 17:29:54.359] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:54.447] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:54Z
[2026-09-30 17:29:54.448] [DEBUG] [WTelegram]              → Upload_File                           #4982
[2026-09-30 17:29:54.448] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #8/12 (1 048 576 б за 89 мс) [Полный].
[2026-09-30 17:29:54.448] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #8 (смещение 8 388 608, 1 048 576 байт). Скачано всего: 9 из 12 чанков (75%).
[2026-09-30 17:29:54.562] [INFO ] [MtprotoWorkerPool] [Воркер #3] Запрос чанка #9/12 (смещение 9 437 184 б, размер 1 048 576 б)...
[2026-09-30 17:29:54.563] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:54Z (svc)
[2026-09-30 17:29:54.563] [DEBUG] [WTelegram]             → Upload_GetFile                         #1C6E
[2026-09-30 17:29:54.563] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:54.645] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:54Z
[2026-09-30 17:29:54.645] [DEBUG] [WTelegram]              → Upload_File                           #1C6E
[2026-09-30 17:29:54.645] [INFO ] [MtprotoWorkerPool] [Воркер #3] Получен чанк #9/12 (1 048 576 б за 82 мс) [Полный].
[2026-09-30 17:29:54.646] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #9 (смещение 9 437 184, 1 048 576 байт). Скачано всего: 10 из 12 чанков (83,3%).
[2026-09-30 17:29:54.646] [INFO ] [MtprotoWorkerPool] [RAM Streaming] Скачивание 1,84 МБ (чанки #10..#11 (всего 12)) через 2 воркеров MTProto...
[2026-09-30 17:29:54.766] [INFO ] [MtprotoWorkerPool] [Воркер #2] Запрос чанка #10/12 (смещение 10 485 760 б, размер 1 048 576 б)...
[2026-09-30 17:29:54.766] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:54Z (svc)
[2026-09-30 17:29:54.766] [DEBUG] [WTelegram]             → Upload_GetFile                         #07CA
[2026-09-30 17:29:54.766] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:54.894] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:54Z
[2026-09-30 17:29:54.894] [DEBUG] [WTelegram]              → Upload_File                           #07CA
[2026-09-30 17:29:54.895] [INFO ] [MtprotoWorkerPool] [Воркер #2] Получен чанк #10/12 (1 048 576 б за 128 мс) [Полный].
[2026-09-30 17:29:54.895] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #10 (смещение 10 485 760, 1 048 576 байт). Скачано всего: 11 из 12 чанков (91,7%).
[2026-09-30 17:29:54.969] [INFO ] [MtprotoWorkerPool] [Воркер #1] Запрос чанка #11/12 (смещение 11 534 336 б, размер 1 048 576 б)...
[2026-09-30 17:29:54.970] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-09-30 14:29:54Z (svc)
[2026-09-30 17:29:54.970] [DEBUG] [WTelegram]             → Upload_GetFile                         #BECE
[2026-09-30 17:29:54.970] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-09-30 17:29:55.140] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-09-30 14:29:55Z
[2026-09-30 17:29:55.140] [DEBUG] [WTelegram]              → Upload_File                           #BECE
[2026-09-30 17:29:55.141] [INFO ] [TelegramService] [Cache RAM] Точечное чтение из ОЗУ для 'IMG_20201018_131419 (12 mb).jpg' (ID 1271): Глобальный Чанк #11 (смещение 11 534 336, 879 938 байт). Скачано всего: 12 из 12 чанков (100%).
[2026-09-30 17:29:55.141] [INFO ] [TelegramService] [NetworkAudit] Завершено для 'IMG_20201018_131419 (12 mb).jpg': Размер: 12 414 274 б (11,84 МБ) | Из сети TG: 14 773 570 б (14,09 МБ) | Из ОЗУ: 10 579 266 б (10,09 МБ) | Эффективность сети: 1,19x (Допустимо — остаток хвоста чанка)
[2026-09-30 17:29:55.141] [INFO ] [TrayContext] Событие OnDownloadCompleted для 'IMG_20201018_131419 (12 mb).jpg' получено из TelegramService.
[2026-09-30 17:29:55.141] [INFO ] [MtprotoWorkerPool] [Воркер #1] Получен чанк #11/12 (879 938 б за 171 мс) [879 938 б из 1 048 576 б, Хвост EOF].
[2026-09-30 17:29:55.142] [INFO ] [TrayProgressOverlay] CompleteTransfer [Download] вызван для 'IMG_20201018_131419 (12 mb).jpg'. Запуск _completionTimer...
[2026-09-30 17:29:55.143] [INFO ] [TrayProgressOverlay] Остановлен активный _completionTimer для файла IMG_20201018_131419 (12 mb).jpg
[2026-09-30 17:29:56.796] [INFO ] [TrayProgressOverlay] Таймаут неактивности передачи (1,7 сек).