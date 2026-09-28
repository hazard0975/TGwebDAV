[2026-09-28 18:35:19.331] [INFO ] [WinFsp] Создан файл для записи: '/тест7/video/11mb/Eclipse - Let The Rhythm Move You [SGYH7HVU8uE].mp4' (ID 1224)
[2026-09-28 18:35:19.338] [INFO ] [WinFsp] Начало фоновой отправки файла 'Eclipse - Let The Rhythm Move You [SGYH7HVU8uE].mp4' (11066822 байт) в Telegram (подпись: '/тест7/video/11mb/Eclipse - Let The Rhythm Move You [SGYH7HVU8uE]_v1.mp4')...
[2026-09-28 18:35:19.339] [INFO ] [VideoMetadataExtractor] MP4 Box parser определил габариты: 320x240
[2026-09-28 18:35:19.418] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=-4, subtype=00000016-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D5212
[2026-09-28 18:35:19.418] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=-4, subtype=00000016-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D36B4
[2026-09-28 18:35:19.418] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=-4, subtype=3231564e-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D5212
[2026-09-28 18:35:19.418] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=-4, subtype=32595559-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D5212
[2026-09-28 18:35:19.424] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=-4, subtype=00000016-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D36B4
[2026-09-28 18:35:19.441] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=0, subtype=00000016-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D36B4
[2026-09-28 18:35:19.445] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=0, subtype=3231564e-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D36B4
[2026-09-28 18:35:19.448] [DEBUG] [VideoMetadataExtractor] SetCurrentMediaType(stream=0, subtype=32595559-0000-0010-8000-00aa00389b71) вернул hr = 0xC00D36B4
[2026-09-28 18:35:19.449] [WARN ] [VideoMetadataExtractor] Не удалось согласовать видеоформат (RGB32/NV12/YUY2) для 'Richard X vs. Liberty X - Being Nobody (XTO0cdWwKcI).mp4'
[2026-09-28 18:35:19.449] [INFO ] [VideoMetadataExtractor] Метаданные видео 'Richard X vs. Liberty X - Being Nobody (XTO0cdWwKcI).mp4': 322x240, 216 сек, превью: нет
[2026-09-28 18:35:19.458] [INFO ] [TelegramService] Прямая потоковая передача файла 'Richard X vs. Liberty X - Being Nobody (XTO0cdWwKcI).mp4' (6683355 байт) в Telegram...
[2026-09-28 18:35:19.470] [WARN ] [VideoMetadataExtractor] ExtractThumbnailViaMediaFoundation ошибка: System.InvalidCastException: Specified cast is not valid.
   at System.StubHelpers.InterfaceMarshaler.ConvertToManaged(IntPtr& ppUnk, IntPtr itfMT, IntPtr classMT, Int32 flags)
   at TelegramWebDAV.Services.VideoMetadataExtractor.IMFSourceReader.ReadSample(Int32 dwStreamIndex, Int32 dwControlFlags, Int32& pdwActualStreamIndex, Int32& pdwStreamFlags, Int64& pllTimestamp, IMFSample& ppSample)
   at TelegramWebDAV.Services.VideoMetadataExtractor.ExtractThumbnailViaMediaFoundation(String filePath, VideoMetadataResult result)
[2026-09-28 18:35:19.470] [WARN ] [VideoMetadataExtractor] ExtractThumbnailViaMediaFoundation ошибка: System.InvalidCastException: Specified cast is not valid.
   at System.StubHelpers.InterfaceMarshaler.ConvertToManaged(IntPtr& ppUnk, IntPtr itfMT, IntPtr classMT, Int32 flags)
   at TelegramWebDAV.Services.VideoMetadataExtractor.IMFSourceReader.ReadSample(Int32 dwStreamIndex, Int32 dwControlFlags, Int32& pdwActualStreamIndex, Int32& pdwStreamFlags, Int64& pllTimestamp, IMFSample& ppSample)
   at TelegramWebDAV.Services.VideoMetadataExtractor.ExtractThumbnailViaMediaFoundation(String filePath, VideoMetadataResult result)