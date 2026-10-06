[2026-10-07 01:38:52.306] [INFO ] [WinFsp] Создан файл для записи: '/Новый текстовый документ.txt' (ID 193, alloc: 0)
[2026-10-07 01:38:55.687] [DEBUG] [WTelegram] 2>Receiving Updates                                  2026-10-06 22:38:54Z  
[2026-10-07 01:38:57.286] [INFO ] [WinFsp] Переименование/перемещение: '/Новый текстовый документ.txt' -> '/мсмс.txt'
[2026-10-07 01:39:00.805] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:00Z (svc)
[2026-10-07 01:39:00.805] [DEBUG] [WTelegram]             → PingDelayDisconnect                    #57D0
[2026-10-07 01:39:00.805] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:00.849] [DEBUG] [WTelegram] 2>Receiving Pong                                     2026-10-06 22:39:00Z (svc) 
[2026-10-07 01:39:05.372] [DEBUG] [WTelegram] 2>Receiving UpdateShort                              2026-10-06 22:39:04Z  
[2026-10-07 01:39:08.942] [INFO ] [WinFsp] Отправка файла 'мсмс.txt' (9 байт) из памяти в Telegram (Zero-Temp)...
[2026-10-07 01:39:08.942] [INFO ] [TelegramService] Прямая потоковая передача файла 'мсмс.txt' (9 байт) в Telegram...
[2026-10-07 01:39:08.943] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:08Z (svc)
[2026-10-07 01:39:08.943] [DEBUG] [WTelegram]             → Upload_SaveFilePart                    #9164
[2026-10-07 01:39:08.943] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:08.989] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:08Z
[2026-10-07 01:39:08.989] [DEBUG] [WTelegram]              → Boolean                               #9164
[2026-10-07 01:39:08.989] [INFO ] [TelegramService] Файл 'мсмс.txt' загружен в MTProto, финализация сообщения в канале (подпись: '🟢 /мсмс_v1.txt
#latest')...
[2026-10-07 01:39:08.990] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Upload] для мсмс.txt (9/9)
[2026-10-07 01:39:08.990] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:08Z (svc)
[2026-10-07 01:39:08.990] [DEBUG] [WTelegram]             → Messages_SendMedia                     #407C
[2026-10-07 01:39:08.990] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:09.082] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:08Z
[2026-10-07 01:39:09.082] [DEBUG] [WTelegram]              → Updates                               #407C
[2026-10-07 01:39:09.083] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:08Z (svc)
[2026-10-07 01:39:09.083] [DEBUG] [WTelegram]             → Messages_EditMessage                   #A520
[2026-10-07 01:39:09.083] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:09.149] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:08Z
[2026-10-07 01:39:09.149] [DEBUG] [WTelegram]              → Updates                               #A520
[2026-10-07 01:39:09.149] [INFO ] [TelegramService] Файл 'мсмс.txt' успешно сохранен в Telegram. Message ID: 1771
[2026-10-07 01:39:09.149] [INFO ] [TrayContext] Событие OnUploadCompleted для 'мсмс.txt' получено из TelegramService.
[2026-10-07 01:39:09.149] [INFO ] [TrayProgressOverlay] CompleteTransfer [Upload] вызван для 'мсмс.txt'. Запуск _completionTimer...
[2026-10-07 01:39:09.164] [INFO ] [WinFsp] Файл 'мсмс.txt' успешно сохранен в Telegram (Msg ID: 1771, дата изменения: 2026-10-06 22:39:08).
[2026-10-07 01:39:10.562] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-10-07 01:39:10.563] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-10-07 01:39:16.887] [INFO ] [WinFsp] Отправка файла 'мсмс.txt' (9 байт) из памяти в Telegram (Zero-Temp)...
[2026-10-07 01:39:16.888] [INFO ] [TelegramService] Прямая потоковая передача файла 'мсмс.txt' (9 байт) в Telegram...
[2026-10-07 01:39:16.888] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:16Z (svc)
[2026-10-07 01:39:16.888] [DEBUG] [WTelegram]             → Upload_SaveFilePart                    #99FC
[2026-10-07 01:39:16.888] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:16.935] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:16Z
[2026-10-07 01:39:16.935] [DEBUG] [WTelegram]              → Boolean                               #99FC
[2026-10-07 01:39:16.935] [INFO ] [TelegramService] Файл 'мсмс.txt' загружен в MTProto, финализация сообщения в канале (подпись: '🟢 /мсмс_v2.txt
#latest')...
[2026-10-07 01:39:16.935] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Upload] для мсмс.txt (9/9)
[2026-10-07 01:39:16.935] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:16Z (svc)
[2026-10-07 01:39:16.935] [DEBUG] [WTelegram]             → Messages_SendMedia                     #0D20
[2026-10-07 01:39:16.935] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:17.045] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:16Z
[2026-10-07 01:39:17.045] [DEBUG] [WTelegram]              → Updates                               #0D20
[2026-10-07 01:39:17.045] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:16Z (svc)
[2026-10-07 01:39:17.045] [DEBUG] [WTelegram]             → Messages_EditMessage                   #03D0
[2026-10-07 01:39:17.045] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:17.107] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:16Z
[2026-10-07 01:39:17.107] [DEBUG] [WTelegram]              → Updates                               #03D0
[2026-10-07 01:39:17.107] [INFO ] [TelegramService] Файл 'мсмс.txt' успешно сохранен в Telegram. Message ID: 1772
[2026-10-07 01:39:17.107] [INFO ] [TrayContext] Событие OnUploadCompleted для 'мсмс.txt' получено из TelegramService.
[2026-10-07 01:39:17.108] [INFO ] [TrayProgressOverlay] CompleteTransfer [Upload] вызван для 'мсмс.txt'. Запуск _completionTimer...
[2026-10-07 01:39:17.121] [INFO ] [WinFsp] Файл 'мсмс.txt' успешно сохранен в Telegram (Msg ID: 1772, дата изменения: 2026-10-06 22:39:16).
[2026-10-07 01:39:17.864] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:17Z (svc)
[2026-10-07 01:39:17.864] [DEBUG] [WTelegram]             → Messages_EditMessage                   #44B1
[2026-10-07 01:39:17.864] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:17.933] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:17Z
[2026-10-07 01:39:17.933] [DEBUG] [WTelegram]              → Updates                               #44B1
[2026-10-07 01:39:17.933] [INFO ] [TelegramService] Подпись сообщения #1771 в Telegram успешно обновлена на: '🗑️ /мсмс_v1.txt
ID: 1771
#trash'.
[2026-10-07 01:39:18.513] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-10-07 01:39:18.514] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-10-07 01:39:22.659] [INFO ] [WinFsp] Отправка файла 'мсмс.txt' (15 байт) из памяти в Telegram (Zero-Temp)...
[2026-10-07 01:39:22.659] [INFO ] [TelegramService] Прямая потоковая передача файла 'мсмс.txt' (15 байт) в Telegram...
[2026-10-07 01:39:22.659] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:22Z (svc)
[2026-10-07 01:39:22.659] [DEBUG] [WTelegram]             → Upload_SaveFilePart                    #A126
[2026-10-07 01:39:22.659] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:22.707] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:22Z
[2026-10-07 01:39:22.707] [DEBUG] [WTelegram]              → Boolean                               #A126
[2026-10-07 01:39:22.707] [INFO ] [TelegramService] Файл 'мсмс.txt' загружен в MTProto, финализация сообщения в канале (подпись: '🟢 /мсмс_v3.txt
#latest')...
[2026-10-07 01:39:22.707] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Upload] для мсмс.txt (15/15)
[2026-10-07 01:39:22.708] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:22Z (svc)
[2026-10-07 01:39:22.708] [DEBUG] [WTelegram]             → Messages_SendMedia                     #FD4A
[2026-10-07 01:39:22.708] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:22.789] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:22Z
[2026-10-07 01:39:22.789] [DEBUG] [WTelegram]              → Updates                               #FD4A
[2026-10-07 01:39:22.790] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:22Z (svc)
[2026-10-07 01:39:22.790] [DEBUG] [WTelegram]             → Messages_EditMessage                   #9212
[2026-10-07 01:39:22.790] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:22.853] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:22Z
[2026-10-07 01:39:22.853] [DEBUG] [WTelegram]              → Updates                               #9212
[2026-10-07 01:39:22.854] [INFO ] [TelegramService] Файл 'мсмс.txt' успешно сохранен в Telegram. Message ID: 1773
[2026-10-07 01:39:22.854] [INFO ] [TrayContext] Событие OnUploadCompleted для 'мсмс.txt' получено из TelegramService.
[2026-10-07 01:39:22.854] [INFO ] [TrayProgressOverlay] CompleteTransfer [Upload] вызван для 'мсмс.txt'. Запуск _completionTimer...
[2026-10-07 01:39:22.871] [INFO ] [WinFsp] Файл 'мсмс.txt' успешно сохранен в Telegram (Msg ID: 1773, дата изменения: 2026-10-06 22:39:22).
[2026-10-07 01:39:23.107] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:22Z (svc)
[2026-10-07 01:39:23.107] [DEBUG] [WTelegram]             → Messages_EditMessage                   #EAEA
[2026-10-07 01:39:23.107] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:23.170] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:22Z
[2026-10-07 01:39:23.170] [DEBUG] [WTelegram]              → Updates                               #EAEA
[2026-10-07 01:39:23.170] [INFO ] [TelegramService] Подпись сообщения #1772 в Telegram успешно обновлена на: '🗑️ /мсмс_v2.txt
ID: 1772
#trash'.
[2026-10-07 01:39:24.268] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-10-07 01:39:24.269] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-10-07 01:39:26.176] [INFO ] [WinFsp] Отправка файла 'мсмс.txt' (17 байт) из памяти в Telegram (Zero-Temp)...
[2026-10-07 01:39:26.176] [INFO ] [TelegramService] Прямая потоковая передача файла 'мсмс.txt' (17 байт) в Telegram...
[2026-10-07 01:39:26.176] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:25Z (svc)
[2026-10-07 01:39:26.176] [DEBUG] [WTelegram]             → Upload_SaveFilePart                    #EBF9
[2026-10-07 01:39:26.176] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:26.223] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:25Z
[2026-10-07 01:39:26.223] [DEBUG] [WTelegram]              → Boolean                               #EBF9
[2026-10-07 01:39:26.223] [INFO ] [TelegramService] Файл 'мсмс.txt' загружен в MTProto, финализация сообщения в канале (подпись: '🟢 /мсмс_v4.txt
#latest')...
[2026-10-07 01:39:26.223] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Upload] для мсмс.txt (17/17)
[2026-10-07 01:39:26.224] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:25Z (svc)
[2026-10-07 01:39:26.224] [DEBUG] [WTelegram]             → Messages_SendMedia                     #9785
[2026-10-07 01:39:26.224] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:26.308] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:25Z
[2026-10-07 01:39:26.309] [DEBUG] [WTelegram]              → Updates                               #9785
[2026-10-07 01:39:26.309] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:25Z (svc)
[2026-10-07 01:39:26.309] [DEBUG] [WTelegram]             → Messages_EditMessage                   #1511
[2026-10-07 01:39:26.309] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:26.368] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:25Z
[2026-10-07 01:39:26.368] [DEBUG] [WTelegram]              → Updates                               #1511
[2026-10-07 01:39:26.369] [INFO ] [TelegramService] Файл 'мсмс.txt' успешно сохранен в Telegram. Message ID: 1774
[2026-10-07 01:39:26.369] [INFO ] [TrayContext] Событие OnUploadCompleted для 'мсмс.txt' получено из TelegramService.
[2026-10-07 01:39:26.369] [INFO ] [TrayProgressOverlay] CompleteTransfer [Upload] вызван для 'мсмс.txt'. Запуск _completionTimer...
[2026-10-07 01:39:26.382] [INFO ] [WinFsp] Файл 'мсмс.txt' успешно сохранен в Telegram (Msg ID: 1774, дата изменения: 2026-10-06 22:39:26).
[2026-10-07 01:39:27.328] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:26Z (svc)
[2026-10-07 01:39:27.328] [DEBUG] [WTelegram]             → Messages_EditMessage                   #F6D6
[2026-10-07 01:39:27.328] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:27.391] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:26Z
[2026-10-07 01:39:27.391] [DEBUG] [WTelegram]              → Updates                               #F6D6
[2026-10-07 01:39:27.391] [INFO ] [TelegramService] Подпись сообщения #1773 в Telegram успешно обновлена на: '🗑️ /мсмс_v3.txt
ID: 1773
#trash'.
[2026-10-07 01:39:27.782] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-10-07 01:39:27.783] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-10-07 01:39:28.654] [INFO ] [WinFsp] Отправка файла 'мсмс.txt' (25 байт) из памяти в Telegram (Zero-Temp)...
[2026-10-07 01:39:28.654] [INFO ] [TelegramService] Прямая потоковая передача файла 'мсмс.txt' (25 байт) в Telegram...
[2026-10-07 01:39:28.655] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:28Z (svc)
[2026-10-07 01:39:28.655] [DEBUG] [WTelegram]             → Upload_SaveFilePart                    #62EC
[2026-10-07 01:39:28.655] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:28.702] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:28Z
[2026-10-07 01:39:28.702] [DEBUG] [WTelegram]              → Boolean                               #62EC
[2026-10-07 01:39:28.702] [INFO ] [TelegramService] Файл 'мсмс.txt' загружен в MTProto, финализация сообщения в канале (подпись: '🟢 /мсмс_v5.txt
#latest')...
[2026-10-07 01:39:28.702] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Upload] для мсмс.txt (25/25)
[2026-10-07 01:39:28.702] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:28Z (svc)
[2026-10-07 01:39:28.702] [DEBUG] [WTelegram]             → Messages_SendMedia                     #CE84
[2026-10-07 01:39:28.702] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:28.790] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:28Z
[2026-10-07 01:39:28.790] [DEBUG] [WTelegram]              → Updates                               #CE84
[2026-10-07 01:39:28.791] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:28Z (svc)
[2026-10-07 01:39:28.791] [DEBUG] [WTelegram]             → Messages_EditMessage                   #7C64
[2026-10-07 01:39:28.791] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:28.851] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:28Z
[2026-10-07 01:39:28.851] [DEBUG] [WTelegram]              → Updates                               #7C64
[2026-10-07 01:39:28.851] [INFO ] [TelegramService] Файл 'мсмс.txt' успешно сохранен в Telegram. Message ID: 1775
[2026-10-07 01:39:28.851] [INFO ] [TrayContext] Событие OnUploadCompleted для 'мсмс.txt' получено из TelegramService.
[2026-10-07 01:39:28.851] [INFO ] [TrayProgressOverlay] CompleteTransfer [Upload] вызван для 'мсмс.txt'. Запуск _completionTimer...
[2026-10-07 01:39:28.866] [INFO ] [WinFsp] Файл 'мсмс.txt' успешно сохранен в Telegram (Msg ID: 1775, дата изменения: 2026-10-06 22:39:28).
[2026-10-07 01:39:29.539] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:29Z (svc)
[2026-10-07 01:39:29.539] [DEBUG] [WTelegram]             → Messages_EditMessage                   #893D
[2026-10-07 01:39:29.539] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:29.623] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:29Z
[2026-10-07 01:39:29.624] [DEBUG] [WTelegram]              → Updates                               #893D
[2026-10-07 01:39:29.624] [INFO ] [TelegramService] Подпись сообщения #1774 в Telegram успешно обновлена на: '🗑️ /мсмс_v4.txt
ID: 1774
#trash'.
[2026-10-07 01:39:30.279] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-10-07 01:39:30.280] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-10-07 01:39:31.139] [INFO ] [WinFsp] Отправка файла 'мсмс.txt' (33 байт) из памяти в Telegram (Zero-Temp)...
[2026-10-07 01:39:31.139] [INFO ] [TelegramService] Прямая потоковая передача файла 'мсмс.txt' (33 байт) в Telegram...
[2026-10-07 01:39:31.139] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:30Z (svc)
[2026-10-07 01:39:31.139] [DEBUG] [WTelegram]             → Upload_SaveFilePart                    #505A
[2026-10-07 01:39:31.139] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:31.192] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:30Z
[2026-10-07 01:39:31.192] [DEBUG] [WTelegram]              → Boolean                               #505A
[2026-10-07 01:39:31.192] [INFO ] [TelegramService] Файл 'мсмс.txt' загружен в MTProto, финализация сообщения в канале (подпись: '🟢 /мсмс_v6.txt
#latest')...
[2026-10-07 01:39:31.192] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Upload] для мсмс.txt (33/33)
[2026-10-07 01:39:31.192] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:30Z (svc)
[2026-10-07 01:39:31.192] [DEBUG] [WTelegram]             → Messages_SendMedia                     #7FB2
[2026-10-07 01:39:31.192] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:31.279] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:30Z
[2026-10-07 01:39:31.279] [DEBUG] [WTelegram]              → Updates                               #7FB2
[2026-10-07 01:39:31.279] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:30Z (svc)
[2026-10-07 01:39:31.279] [DEBUG] [WTelegram]             → Messages_EditMessage                   #A0AA
[2026-10-07 01:39:31.279] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:31.353] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:30Z
[2026-10-07 01:39:31.353] [DEBUG] [WTelegram]              → Updates                               #A0AA
[2026-10-07 01:39:31.353] [INFO ] [TelegramService] Файл 'мсмс.txt' успешно сохранен в Telegram. Message ID: 1776
[2026-10-07 01:39:31.353] [INFO ] [TrayContext] Событие OnUploadCompleted для 'мсмс.txt' получено из TelegramService.
[2026-10-07 01:39:31.353] [INFO ] [TrayProgressOverlay] CompleteTransfer [Upload] вызван для 'мсмс.txt'. Запуск _completionTimer...
[2026-10-07 01:39:31.355] [INFO ] [WinFsp] Файл 'мсмс.txt' успешно сохранен в Telegram (Msg ID: 1776, дата изменения: 2026-10-06 22:39:31).
[2026-10-07 01:39:31.788] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:31Z (svc)
[2026-10-07 01:39:31.788] [DEBUG] [WTelegram]             → Messages_EditMessage                   #BB17
[2026-10-07 01:39:31.788] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:31.865] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:31Z
[2026-10-07 01:39:31.865] [DEBUG] [WTelegram]              → Updates                               #BB17
[2026-10-07 01:39:31.865] [INFO ] [TelegramService] Подпись сообщения #1775 в Telegram успешно обновлена на: '🗑️ /мсмс_v5.txt
ID: 1775
#trash'.
[2026-10-07 01:39:32.747] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-10-07 01:39:32.748] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-10-07 01:39:33.674] [INFO ] [WinFsp] Отправка файла 'мсмс.txt' (43 байт) из памяти в Telegram (Zero-Temp)...
[2026-10-07 01:39:33.674] [INFO ] [TelegramService] Прямая потоковая передача файла 'мсмс.txt' (43 байт) в Telegram...
[2026-10-07 01:39:33.674] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:33Z (svc)
[2026-10-07 01:39:33.674] [DEBUG] [WTelegram]             → Upload_SaveFilePart                    #BBBD
[2026-10-07 01:39:33.674] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:33.773] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:33Z
[2026-10-07 01:39:33.773] [DEBUG] [WTelegram]              → Boolean                               #BBBD
[2026-10-07 01:39:33.773] [INFO ] [TelegramService] Файл 'мсмс.txt' загружен в MTProto, финализация сообщения в канале (подпись: '🟢 /мсмс_v7.txt
#latest')...
[2026-10-07 01:39:33.774] [INFO ] [TrayProgressOverlay] Показ окна прогресса [Upload] для мсмс.txt (43/43)
[2026-10-07 01:39:33.774] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:33Z (svc)
[2026-10-07 01:39:33.774] [DEBUG] [WTelegram]             → Messages_SendMedia                     #3641
[2026-10-07 01:39:33.774] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:33.857] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:33Z
[2026-10-07 01:39:33.857] [DEBUG] [WTelegram]              → Updates                               #3641
[2026-10-07 01:39:33.857] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:33Z (svc)
[2026-10-07 01:39:33.857] [DEBUG] [WTelegram]             → Messages_EditMessage                   #B54D
[2026-10-07 01:39:33.857] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:33.918] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:33Z
[2026-10-07 01:39:33.918] [DEBUG] [WTelegram]              → Updates                               #B54D
[2026-10-07 01:39:33.918] [INFO ] [TelegramService] Файл 'мсмс.txt' успешно сохранен в Telegram. Message ID: 1777
[2026-10-07 01:39:33.918] [INFO ] [TrayContext] Событие OnUploadCompleted для 'мсмс.txt' получено из TelegramService.
[2026-10-07 01:39:33.918] [INFO ] [TrayProgressOverlay] CompleteTransfer [Upload] вызван для 'мсмс.txt'. Запуск _completionTimer...
[2026-10-07 01:39:33.920] [INFO ] [WinFsp] Файл 'мсмс.txt' успешно сохранен в Telegram (Msg ID: 1777, дата изменения: 2026-10-06 22:39:33).
[2026-10-07 01:39:34.010] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:33Z (svc)
[2026-10-07 01:39:34.010] [DEBUG] [WTelegram]             → Messages_EditMessage                   #83E5
[2026-10-07 01:39:34.010] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:34.079] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:33Z
[2026-10-07 01:39:34.079] [DEBUG] [WTelegram]              → Updates                               #83E5
[2026-10-07 01:39:34.079] [INFO ] [TelegramService] Подпись сообщения #1776 в Telegram успешно обновлена на: '🗑️ /мсмс_v6.txt
ID: 1776
#trash'.
[2026-10-07 01:39:35.313] [INFO ] [TrayProgressOverlay] _completionTimer Tick! _isTransferring=False, _isCompleted=True, Visible=True
[2026-10-07 01:39:35.314] [INFO ] [TrayProgressOverlay] Оверлей успешно скрыт по завершению таймаута.
[2026-10-07 01:39:46.364] [INFO ] [WinFsp] Перемещение элемента 'мсмс.txt' (ID 199) в корзину (.Trash)...
[2026-10-07 01:39:46.377] [INFO ] [Database] Поставлено в фоновую очередь обновление подписей Telegram для 1 элементов.
[2026-10-07 01:39:47.273] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:39:46Z (svc)
[2026-10-07 01:39:47.273] [DEBUG] [WTelegram]             → Messages_EditMessage                   #FF66
[2026-10-07 01:39:47.273] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:39:47.336] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:39:46Z
[2026-10-07 01:39:47.337] [DEBUG] [WTelegram]              → Updates                               #FF66
[2026-10-07 01:39:47.337] [INFO ] [TelegramService] Подпись сообщения #1777 в Telegram успешно обновлена на: '🗑️ /.Trash/мсмс_v7.txt
ID: 1777
#trash'.
[2026-10-07 01:40:00.701] [DEBUG] [WTelegram] 2>Receiving Updates                                  2026-10-06 22:40:00Z  
[2026-10-07 01:40:00.854] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:40:00Z (svc)
[2026-10-07 01:40:00.854] [DEBUG] [WTelegram]             → PingDelayDisconnect                    #7D14
[2026-10-07 01:40:00.854] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:40:00.898] [DEBUG] [WTelegram] 2>Receiving Pong                                     2026-10-06 22:40:00Z (svc) 
[2026-10-07 01:40:07.128] [INFO ] [WinFsp] Перманентное удаление элемента 'мсмс_v3.txt' (ID 195) из корзины...
[2026-10-07 01:40:07.142] [INFO ] [WinFsp] Удалено 1 узлов из базы; 1 сообщений отправлено в очередь удаления Telegram.
[2026-10-07 01:40:07.142] [INFO ] [TelegramService] [DeletionQueue] Фоновое перманентное удаление 1 сообщений из Telegram...
[2026-10-07 01:40:07.142] [DEBUG] [WTelegram] 2>Sending   Channels_DeleteMessages                  #8EE2
[2026-10-07 01:40:07.148] [INFO ] [WinFsp] Перманентное удаление элемента 'мсмс_v1.txt' (ID 193) из корзины...
[2026-10-07 01:40:07.149] [INFO ] [WinFsp] Удалено 1 узлов из базы; 1 сообщений отправлено в очередь удаления Telegram.
[2026-10-07 01:40:07.216] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:40:06Z
[2026-10-07 01:40:07.216] [DEBUG] [WTelegram]              → Messages_AffectedMessages             #8EE2
[2026-10-07 01:40:07.216] [INFO ] [TelegramService] Пакет из 1 сообщений успешно удален из Telegram.
[2026-10-07 01:40:07.218] [INFO ] [TelegramService] [DeletionQueue] Пакет из 1 сообщений успешно очищен из очереди.
[2026-10-07 01:40:07.218] [INFO ] [TelegramService] [DeletionQueue] Фоновое перманентное удаление 1 сообщений из Telegram...
[2026-10-07 01:40:07.219] [DEBUG] [WTelegram] 2>Sending   MsgContainer                             2026-10-06 22:40:06Z (svc)
[2026-10-07 01:40:07.219] [DEBUG] [WTelegram]             → Channels_DeleteMessages                #9DFA
[2026-10-07 01:40:07.219] [DEBUG] [WTelegram]             → MsgsAck                               
[2026-10-07 01:40:07.269] [DEBUG] [WTelegram] 2>Receiving RpcResult                                2026-10-06 22:40:06Z
[2026-10-07 01:40:07.269] [DEBUG] [WTelegram]              → Messages_AffectedMessages             #9DFA
[2026-10-07 01:40:07.269] [INFO ] [TelegramService] Пакет из 1 сообщений успешно удален из Telegram.
[2026-10-07 01:40:07.270] [INFO ] [TelegramService] [DeletionQueue] Пакет из 1 сообщений успешно очищен из очереди.
[2026-10-07 01:40:10.159] [INFO ] [Database] Запуск оптимизации базы данных SQLite (VACUUM)...
[2026-10-07 01:40:10.175] [INFO ] [Database] Оптимизация базы данных (VACUUM) успешно завершена.