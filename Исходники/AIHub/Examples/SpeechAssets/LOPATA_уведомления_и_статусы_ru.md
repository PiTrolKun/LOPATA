# ЛОПАТА — уведомления и статусы моделей

09.11.2026 · 0.7.67-dev

<!-- LOPATA.Metadata: {"program": "LOPATA", "version": "0.7.67-dev", "operation": "speech-assets/notification-catalog", "created_utc": "2026-11-09T15:24:50.477764+00:00"} -->

Очищенный набор уведомлений и статусов выполнения для ручной озвучки. Описания интерфейса, инструкции и пустые фрагменты исключены.
Каждый пункт списка — один будущий звуковой файл. Заголовки и идентификаторы не произносятся. Совпадающие фразы объединены; исходные события перечислены в общем реестре.

Реплик: 1529. Событий и кандидатов: 5247.

Включён резерв фраз жизненного цикла моделей. Их наличие в списке не означает, что такие голосовые уведомления уже подключены.

### 0001 · voice.00ea4a3ee4914c14

<!-- LOPATA.Events: ["normalized:model.busy"] -->

- Модель занята текущей задачей.

### 0002 · voice.68925598258d19d9

<!-- LOPATA.Events: ["normalized:model.cache_cleared"] -->

- Кэш модели очищен.

### 0003 · voice.a8b2b6f6ce155d72

<!-- LOPATA.Events: ["normalized:model.cache_reused"] -->

- Используем подготовленный кэш модели.

### 0004 · voice.cd200c6276e99272

<!-- LOPATA.Events: ["normalized:model.cancelled"] -->

- Задача модели отменена.

### 0005 · voice.1aa016cc2b86b58c

<!-- LOPATA.Events: ["normalized:model.checkpoint"] -->

- Промежуточный результат модели сохранён.

### 0006 · voice.d407601e93b86f6c

<!-- LOPATA.Events: ["normalized:model.compatibility"] -->

- Проверяем совместимость модели с доступным оборудованием.

### 0007 · voice.b4c43926473d3bd7

<!-- LOPATA.Events: ["normalized:model.compatible"] -->

- Модель совместима с выбранным исполнителем.

### 0008 · voice.49144907ebac4ff0

<!-- LOPATA.Events: ["normalized:model.complete"] -->

- Модель завершила обработку запроса.

### 0009 · voice.d94757c465749e61

<!-- LOPATA.Events: ["normalized:model.context_compress"] -->

- Сжимаем историю для продолжения работы модели.

### 0010 · voice.ae3082f62df5cae0

<!-- LOPATA.Events: ["normalized:model.context_compressed"] -->

- История сжата. Можно продолжать работу.

### 0011 · voice.7d1ba0c5e08d4ae6

<!-- LOPATA.Events: ["normalized:model.context_full"] -->

- Достигнут предел контекста модели.

### 0012 · voice.c9dd7c8d132e0ef8

<!-- LOPATA.Events: ["normalized:model.context_near_full"] -->

- Контекст модели почти заполнен.

### 0013 · voice.c9bae7d9141b72a1

<!-- LOPATA.Events: ["normalized:model.cpu"] -->

- Выбран исполнитель на центральном процессоре.

### 0014 · voice.81b98d77d6ee76b2

<!-- LOPATA.Events: ["normalized:model.discovery"] -->

- Ищем установленные модели.

### 0015 · voice.ef3d2afa837709eb

<!-- LOPATA.Events: ["normalized:model.download_cancel"] -->

- Скачивание модели отменено.

### 0016 · voice.90fdc9c734f51f64

<!-- LOPATA.Events: ["normalized:model.download_complete"] -->

- Модель скачана.

### 0017 · voice.00fe8a6aa11990ee

<!-- LOPATA.Events: ["normalized:model.download_error"] -->

- Не удалось скачать модель. Проверьте соединение и повторите попытку.

### 0018 · voice.8b128d01fe1df984

<!-- LOPATA.Events: ["normalized:model.download_pause"] -->

- Скачивание модели приостановлено.

### 0019 · voice.f0cd149827238451

<!-- LOPATA.Events: ["normalized:model.download_progress"] -->

- Модель скачивается.

### 0020 · voice.4c076d248483bace

<!-- LOPATA.Events: ["normalized:model.download_resume"] -->

- Продолжаем скачивание модели.

### 0021 · voice.f7937c32628310ef

<!-- LOPATA.Events: ["normalized:model.download_start"] -->

- Начинаем скачивание модели.

### 0022 · voice.0b648f3ddb70ef84

<!-- LOPATA.Events: ["normalized:model.failed"] -->

- Задача модели завершилась ошибкой.

### 0023 · voice.169bd9e70f2b4933

<!-- LOPATA.Events: ["normalized:model.fallback"] -->

- Используем другой совместимый исполнитель.

### 0024 · voice.82954cb23d86722c

<!-- LOPATA.Events: ["normalized:model.first_token"] -->

- Ожидаем начало ответа модели.

### 0025 · voice.8781ddacdef891f6

<!-- LOPATA.Events: ["normalized:model.found"] -->

- Установленная модель найдена.

### 0026 · voice.f2c91759ac467951

<!-- LOPATA.Events: ["normalized:model.generation"] -->

- Выполняется генерация.

### 0027 · voice.7177780670aef112

<!-- LOPATA.Events: ["normalized:model.gpu"] -->

- Выбран исполнитель на графическом процессоре.

### 0028 · voice.fdef65f0a5902617

<!-- LOPATA.Events: ["normalized:model.incompatible"] -->

- Выбранная модель несовместима с доступным исполнителем.

### 0029 · voice.3dbb695bd614e4a5

<!-- LOPATA.Events: ["normalized:model.installed"] -->

- Установка модели завершена.

### 0030 · voice.0cad75d2e42fa919

<!-- LOPATA.Events: ["normalized:model.license"] -->

- Ознакомьтесь с условиями использования выбранной модели.

### 0031 · voice.7fd76be7d82524db

<!-- LOPATA.Events: ["normalized:model.load"] -->

- Загружаем модель в память.

### 0032 · voice.3147422beef465a7

<!-- LOPATA.Events: ["normalized:model.load_cpu"] -->

- Загружаем модель в оперативную память.

### 0033 · voice.7d30dbf2551fe30e

<!-- LOPATA.Events: ["normalized:model.load_error"] -->

- Не удалось загрузить модель в память.

### 0034 · voice.1b507a5d34aa4a01

<!-- LOPATA.Events: ["normalized:model.load_gpu"] -->

- Загружаем модель в видеопамять.

### 0035 · voice.5a1c741ced7d7448

<!-- LOPATA.Events: ["normalized:model.loaded"] -->

- Модель загружена в память.

### 0036 · voice.1c16c3f5591566ec

<!-- LOPATA.Events: ["normalized:model.memory_low"] -->

- Для модели недостаточно свободной памяти.

### 0037 · voice.a9ebd36ab6fb9a54

<!-- LOPATA.Events: ["normalized:model.not_installed"] -->

- Нужная модель ещё не установлена.

### 0038 · voice.9cf360b359c4881a

<!-- LOPATA.Events: ["normalized:model.not_selected"] -->

- Сначала выберите модель.

### 0039 · voice.d998f8c268d17726

<!-- LOPATA.Events: ["normalized:model.offload"] -->

- Часть модели размещается в оперативной памяти.

### 0040 · voice.0c08c0fad0cae24d

<!-- LOPATA.Events: ["normalized:model.paused"] -->

- Работа модели приостановлена.

### 0041 · voice.17edea3503cfb0c1

<!-- LOPATA.Events: ["normalized:model.permission"] -->

- Для продолжения нужно ваше решение.

### 0042 · voice.39fffc6d016751c7

<!-- LOPATA.Events: ["normalized:model.prefill"] -->

- Модель читает запрос и контекст.

### 0043 · voice.c1f09a6fd86c4997

<!-- LOPATA.Events: ["normalized:model.preparing"] -->

- Подготавливаем модель и необходимые компоненты.

### 0044 · voice.5d432c32e5c36466

<!-- LOPATA.Events: ["normalized:model.ready"] -->

- Модель готова к работе.

### 0045 · voice.24472856652b759f

<!-- LOPATA.Events: ["normalized:model.reinstalled"] -->

- Переустановка модели завершена.

### 0046 · voice.ce0a6e237448478a

<!-- LOPATA.Events: ["normalized:model.removed"] -->

- Модель удалена из хранилища.

### 0047 · voice.154b3fde481c41bd

<!-- LOPATA.Events: ["normalized:model.request"] -->

- Запрос передан модели.

### 0048 · voice.684d37113396dc42

<!-- LOPATA.Events: ["normalized:model.resume_checkpoint"] -->

- Продолжаем задачу модели с сохранённого этапа.

### 0049 · voice.7bafed74999abaf8

<!-- LOPATA.Events: ["normalized:model.resuming"] -->

- Работа модели продолжается.

### 0050 · voice.e9df427935801172

<!-- LOPATA.Events: ["normalized:model.retire"] -->

- Освобождаем память, занятую моделью.

### 0051 · voice.7fad9a770f6f6004

<!-- LOPATA.Events: ["normalized:model.retire_failed"] -->

- Не удалось подтвердить освобождение памяти модели.

### 0052 · voice.72aeb4e67600e524

<!-- LOPATA.Events: ["normalized:model.retired"] -->

- Модель выгружена из памяти.

### 0053 · voice.60befefcb5b1831b

<!-- LOPATA.Events: ["normalized:model.selected"] -->

- Модель выбрана.

### 0054 · voice.4029b8f8d0c3d1b2

<!-- LOPATA.Events: ["normalized:model.streaming"] -->

- Модель формирует ответ.

### 0055 · voice.aec18ef3de9ee978

<!-- LOPATA.Events: ["normalized:model.thinking"] -->

- Модель обрабатывает запрос.

### 0056 · voice.8b939256079391b5

<!-- LOPATA.Events: ["normalized:model.timeout"] -->

- Превышено время ожидания ответа модели.

### 0057 · voice.4409dc52167c9f8c

<!-- LOPATA.Events: ["normalized:model.token_limit"] -->

- Достигнут предел длины ответа модели.

### 0058 · voice.fb4a6870b7024b7c

<!-- LOPATA.Events: ["normalized:model.tool"] -->

- Модель запросила выполнение инструмента.

### 0059 · voice.83c02ecfbe036c00

<!-- LOPATA.Events: ["normalized:model.tool_failed"] -->

- Не удалось выполнить запрос модели к инструменту.

### 0060 · voice.bc7e6b5f67782e58

<!-- LOPATA.Events: ["normalized:model.tool_ready"] -->

- Инструмент завершил работу. Продолжаем обработку.

### 0061 · voice.78d23cfdc8b107e6

<!-- LOPATA.Events: ["normalized:model.unpack"] -->

- Распаковываем файлы модели.

### 0062 · voice.607bed693312e354

<!-- LOPATA.Events: ["normalized:model.verified"] -->

- Файлы модели прошли проверку.

### 0063 · voice.fc5294d7a07ffa7a

<!-- LOPATA.Events: ["normalized:model.verify"] -->

- Проверяем целостность файлов модели.

### 0064 · voice.db127df9228565ff

<!-- LOPATA.Events: ["normalized:model.verify_error"] -->

- Проверка файлов модели не пройдена.

### 0065 · voice.2d5821b5c1d5708d

<!-- LOPATA.Events: ["normalized:model.warmup"] -->

- Выполняем пробный запуск модели.

### 0066 · voice.a888e7ad834ce254

<!-- LOPATA.Events: ["normalized:model.warmup_complete"] -->

- Пробный запуск модели завершён.

### 0067 · voice.226f14a0ec4661c1

<!-- LOPATA.Events: ["normalized:model.warmup_failed"] -->

- Пробный запуск модели завершился ошибкой.

### 0068 · voice.823652aee32ce382

<!-- LOPATA.Events: ["normalized:model.worker_crash"] -->

- Исполнитель модели неожиданно завершился.

### 0069 · voice.04117f4b366cf3f8

<!-- LOPATA.Events: ["normalized:model.worker_ready"] -->

- Исполнитель модели готов.

### 0070 · voice.b55e4096de71c15b

<!-- LOPATA.Events: ["normalized:model.worker_recovered"] -->

- Работа исполнителя модели восстановлена.

### 0071 · voice.fcc90f20891cc2e9

<!-- LOPATA.Events: ["normalized:model.worker_restart"] -->

- Перезапускаем исполнитель модели.

### 0072 · voice.3c8d0f9922c71ccd

<!-- LOPATA.Events: ["normalized:model.worker_start"] -->

- Запускаем исполнитель модели.

### 0073 · voice.c8a121152de63f80

<!-- LOPATA.Events: ["normalized:model.worker_stop"] -->

- Исполнитель модели остановлен.

### 0074 · voice.94eff08c541d0e89

<!-- LOPATA.Events: ["source:8186762878f685d0", "source:f2811e3f1772c840", "source:1418035eb2d06b22", "source:a0912c46454292b1", "source:70469e68d6b9ea2b", "source:89008a902ecd7c05", "source:941941c533cee58d", "source:630818bb08076b50", "source:008134fd620592a4", "dynamic:f9d6d4b1114b55f7", "dynamic:6f024f6e582e2b78", "dynamic:8551edeb54353931", "dynamic:8b76d2ced98be156", "dynamic:4c3ed3bae55df4de", "dynamic:864091f0ce17e01b", "dynamic:a97e37e4b4fd538b", "dynamic:7558e9e60d8a2e01", "dynamic:68e142003acbb660", "dynamic:4b584edc8f8320d5", "dynamic:97d31a3f5777b457", "dynamic:66b7916c6114c4d8", "dynamic:9d16790ae736be5d", "dynamic:1c318da36f7a1ce9", "dynamic:06a9c08ddc376d69", "dynamic:f6d3b891a4bcd796", "dynamic:dd47b9314df7b897", "dynamic:221abd5cfb84576b", "dynamic:0b15f83f502aa070", "dynamic:c5825db3962c5de9", "dynamic:93116d8a13fe916b", "dynamic:5de809733a58ef11", "dynamic:6dc8ac5ca84c1331", "dynamic:3e5566978cb073af", "dynamic:68e9e650a9a6df67", "dynamic:734d68478a37ae87", "dynamic:7233df6fd375537f", "dynamic:85b0da243297d066", "dynamic:586d5eeb64c18a19", "dynamic:641022cc2c8b8b11", "dynamic:1e6040ca1cbb9e8a", "dynamic:924721b3522386fd", "dynamic:ca1d6150a97e941f", "dynamic:f7c63e52293ccd74", "dynamic:8ab8bca735257121", "dynamic:a48b32d8f8f7ac77", "dynamic:e70e3ac873ae5b71", "dynamic:ff8eec672bc8dfd0", "dynamic:8d287dbca77adf85", "dynamic:ad75df1cbf459731", "dynamic:34d301071fcf7335", "dynamic:58560f79832ae124", "dynamic:f188b54dbc92518f", "dynamic:1af780de2d89a15a", "dynamic:d0bafc7d8300d83e", "dynamic:d85e8703567dfea8", "dynamic:c9318465cb2218e8", "dynamic:efe2977a71ad3027", "dynamic:27b4ceef675a0105", "dynamic:96c28255f4121582", "dynamic:b76a01ae60df06f6", "dynamic:4c121b9584e6ac1e", "dynamic:588a3a6b403b4e2d", "dynamic:9e29c0f9c0636a4f", "dynamic:13c49ac4616f54f6", "dynamic:9c127ce622f7a3b2", "dynamic:95584b8875938de4", "dynamic:5e74a7f4d3c65f79", "dynamic:104c72a65464da7a", "dynamic:98ab89f59003825a", "dynamic:7398d461b1ac3b4e", "dynamic:3fa6d87a6df4d9ea", "dynamic:d608f4340d5f949a", "dynamic:3e5bfd3b5c749452", "dynamic:590735076d84b8e2", "dynamic:eba1c9c5ffc80274", "dynamic:8a00344e030aea88", "dynamic:577706715594476d", "dynamic:81b41f4c2c625e1c", "dynamic:0ff6d4c9f629dcfc", "dynamic:f8b0b8436ca57142", "dynamic:3f4141dc6dd31bd8", "dynamic:fa642abb16609ac0", "dynamic:8b274b7765e289cc", "dynamic:2729f841711d318e", "dynamic:77130523a59700d4", "dynamic:caf4202ccab8fb30", "dynamic:7ee7f336ba52e0fc", "dynamic:1fa2ac984a4968ca", "dynamic:388f1df1d59818f3", "dynamic:63549bb83ed1f6d5", "dynamic:e19d388949082ead", "dynamic:547da8438e37917f", "dynamic:bfb7c08c13c9547d", "dynamic:c4ed7893786ed03f", "dynamic:1655baf404330814", "dynamic:ad47553a696cdeba", "dynamic:cb99bc91594b4208", "dynamic:6158c19e1fd79b23", "dynamic:44013eb7f488b065", "dynamic:4eee3d523843f74b", "dynamic:e0c6d415da5a844c", "dynamic:c01116c906fbc977", "dynamic:bc6e3cc1c5bf0c3b", "dynamic:9f2e3e8e81cec7a5", "dynamic:9a08bdf20c90dfcc", "dynamic:bc4d1e36f1164a04", "dynamic:2edfbc1b8e6c8c03", "dynamic:01c8ef22c41c3dfd", "dynamic:215973350ad0cb09", "dynamic:36c84672f655f2b8", "dynamic:51b9d462bbe9f309", "dynamic:f7ba3fdf1c94cdca", "dynamic:a7b5732a8ff67a8b", "dynamic:3f64f4e62dfef6ca", "dynamic:601b2e86c3302d8b", "dynamic:d04773d49902c10d", "dynamic:04e173dff31054c3", "dynamic:0ebd63672cc4939f", "dynamic:5b21a9b1d1ec89ec", "dynamic:65b567f3131c10fe", "dynamic:81ffeb5b607d2d9c", "dynamic:71640f9578106013", "dynamic:a4923e97c31dee84", "dynamic:7d8df415cceb6a49", "dynamic:7ee65e2bb089caa7", "dynamic:83b11f32846e3bae", "dynamic:8cac78aef4fe8a69", "dynamic:a080703a49e0ec58", "dynamic:4eee5375bae5984c", "dynamic:935ce3f7380e1768", "dynamic:95cade29be55bb9a", "dynamic:5fdf9dd70bd4eb68", "dynamic:b95958084edb5718", "dynamic:9c5ec34907ef0658", "dynamic:73e39730cc2849c4", "dynamic:ea6fadac2a08b0f4", "dynamic:f5daea5874a1055e", "dynamic:0b6de353d64b046a", "dynamic:fd90ad1ff7bf8611", "dynamic:9bac5a7c85298d8e", "dynamic:3e8fc9f4467df636", "dynamic:9e94d6ecb3195b3e", "dynamic:8346f8389534d4ae", "dynamic:6509cc49817b78ff", "dynamic:c76209d20f4cc655", "dynamic:fb1b3c19b5c863d3", "dynamic:115b932d3c7e575a", "dynamic:630388c047caefdd", "dynamic:368958300a9d806e", "dynamic:bcad874ce78fa4c5", "dynamic:d0f04220503722dd", "dynamic:c006247c0131ff59", "dynamic:f65ce7b4a16d28b1", "dynamic:730d8c6c2e5e5697", "dynamic:2b50e8faee3ff6ca", "dynamic:aa08e32d36fb0c9c", "dynamic:bb1a6c6ed3c8e0b9", "dynamic:4f990741c6618a42", "dynamic:85f5fc09ca1c7311", "dynamic:c823624a89c2a928", "dynamic:f36ba880ebcff14f", "dynamic:71307d1c0dc9184f", "dynamic:6ecf34dcfea603fc", "dynamic:6dc014c759acc4e7", "dynamic:e398dfb880e6e3fd", "dynamic:f80074a0d0ce6978", "dynamic:f2162c9e98c39820", "dynamic:30e2f844f9b21aef", "dynamic:a20ae6b552810f00", "dynamic:59fd436d1426202f", "dynamic:53a19aaeeda58086", "dynamic:6744c9879ccc8bef", "dynamic:8030fc1cdc12b02e", "dynamic:f38291e0c4a014e6", "dynamic:7a775c8ddd487a97", "dynamic:4133d2de8b97c4f9", "dynamic:7c15fd7a49f2ef5f", "dynamic:2eecb824d565dc49", "dynamic:720df8d93e39a846", "dynamic:e5a404ebb6f00bcc", "dynamic:a0804e7b1931348d", "dynamic:f36f86c55b33e4c9", "dynamic:943e7986704e73db", "dynamic:2cfce571e166c533", "dynamic:669ef3fe4b7b6b53", "dynamic:dc7850e58a533a31", "dynamic:01ea12af149a2d9c", "dynamic:7e4d74d254500ea2", "dynamic:43ef3627982a45ca", "dynamic:bd7596db0cdc840f", "dynamic:cd6258d28c30c2db", "dynamic:1378d2b4c4ebbe9f", "dynamic:31f65289d16689f6", "dynamic:7f8d17eb877edc43", "dynamic:b9d727e6173b65e6", "dynamic:c1ad26a84b5f844f", "dynamic:bd1dfe47a0c30fb4", "dynamic:2323ceaef0152301", "dynamic:c2a29d27ab656ac8", "dynamic:68fe7c67161a6c39", "dynamic:2e380f4af38d3201", "dynamic:fdcb7bb75e636ea1", "dynamic:287eb68a44784a49", "dynamic:17acb003c509bd4c", "dynamic:725607c0783493db", "dynamic:8c185fc9a832aeda", "dynamic:3e8269e99fd639ac", "dynamic:426e9c5ce7c2ba93", "dynamic:22e67b2e2dd8af09", "dynamic:3ff9220e395d29ef", "dynamic:41602659382199ca", "dynamic:a704745080d0a3a8", "dynamic:776989d325fa1038", "dynamic:26e5ab9c0616c959", "dynamic:8d1184cb15239bcb", "dynamic:0ebbd4bd9540b015", "dynamic:f7210922e03f8c50", "dynamic:31cc3909fba0f4f3", "dynamic:6540c2c655f17881", "dynamic:1a911d9bf28a38e4", "dynamic:1d1a4a57e41d94df", "dynamic:f2320f614b44dc04", "dynamic:be930128adab1a1f", "dynamic:3fcb785236d83ae2", "dynamic:531228db3d30fd65", "dynamic:028e2415401dda3e", "dynamic:1bf89ecf50afc1da", "dynamic:2e382b758fd8396f", "dynamic:a73139e30426ec46", "dynamic:9c70f7afefdf40d9", "dynamic:b9b303bcd5f520af", "dynamic:1542f98e3a6aa383", "dynamic:dd256fc8029c6703", "dynamic:15aa89253e88a638", "dynamic:4a4ca164f1c359ff", "dynamic:43d13684d6fd4ab1", "dynamic:8f39d6f5b48c9522", "dynamic:b9df88a935d25a4b", "dynamic:09ba0dcd4b354232", "dynamic:73dbc6c14f98d451", "dynamic:451daca993e9f3a1", "dynamic:ff213fd34aa0af10", "dynamic:ca08d44c857c1594", "dynamic:cd0de335bf394526", "dynamic:61443376b2332732", "dynamic:7ef230f0d2317d34", "dynamic:d8647205640195bf", "dynamic:db0bc1c20ed0fc2f", "dynamic:05871bb7272e8b64", "dynamic:6e8b532cfa8f445f", "dynamic:8d8042248aca2974", "dynamic:c577b91962fbdb8c", "dynamic:980f42c6a80347be", "dynamic:89698a7d973dca0f", "dynamic:cbc8389b45e1ca32", "dynamic:7f48d9043b786c62", "dynamic:b934846feea8dba2", "dynamic:4eecafcfedd347a9", "dynamic:1cd87561119fe689", "dynamic:2d7e67560e33f176", "dynamic:8a8a0525fa862fb4", "dynamic:d205441484b0e03c", "dynamic:8bc21afa31e3abae", "dynamic:e1046627fc21c9a7", "dynamic:7d02f20dd55ebb81", "dynamic:96fe758a7c574c05", "dynamic:243bbf79762a0d5c", "dynamic:08547366259e17cc", "dynamic:7851836bf14fc59f", "dynamic:b1ced4c08f8547a9", "dynamic:80867bcdbd0ee570", "dynamic:2b4bfe540de24a20", "dynamic:dd249a4929c86add", "dynamic:d9eec68e24dc758b", "dynamic:64cd43aedd3e21b7", "dynamic:f81a7b08b9b83d56", "dynamic:6e349118d92e33b7", "dynamic:a1f428477bbea5c5", "dynamic:f87587ce7e5fd203", "dynamic:c5c7917df23c9f86", "dynamic:74ce2f68de2c783b", "dynamic:3440ccc5336ba2a3", "dynamic:361339fad9c6d8e5", "dynamic:b4c8e393d0042c1c", "dynamic:852687c0bdf32ea6", "dynamic:d0bfd5740a9e82e2", "dynamic:b49e5b2b4b1b94a1", "dynamic:5deca379c541810e", "dynamic:e7ce946d13a50752", "dynamic:9117fcf0fe9f2eab", "dynamic:b4576255d2da06ed", "dynamic:4ef68e18ad520675", "dynamic:2bab04bf1bed55fe", "dynamic:d61f30905e0deb72", "dynamic:fe5ee13c8b07d9bb", "dynamic:d5a4f5a5628620ad", "dynamic:87ae5bb525c51350", "dynamic:6d3e07e840b90288", "dynamic:ea3d7ec9f098152b", "dynamic:9b8f41cb9f8ed120", "dynamic:ca6791c2fe77785d", "dynamic:bbc046c1637f8864", "dynamic:a88ca5238f5672b3", "dynamic:7a8a452aceb48f5d", "dynamic:3d3e8f88b4b32d07", "dynamic:9c8176aaf98be0e8", "dynamic:74dd5fd37a407592", "dynamic:dd23cd77c6d3280e", "dynamic:5671742d9918b4c8", "dynamic:e623a2e1b9f798fb", "dynamic:dfa07bb943932cdf", "dynamic:4ba4b6689862af95", "dynamic:44fdffa7822c8648", "dynamic:90064993610837c5", "dynamic:1572f0531f5e051d", "dynamic:8eb104ace0ecb4de", "dynamic:09472cca1a8c926b", "dynamic:dcce3ab5cf601435", "dynamic:673a36aaa7333e1b", "dynamic:ad36f491cb13a70d", "dynamic:002da6a58a82e1d8", "dynamic:600b0d9f5e52f40f", "dynamic:79287a1e2259900b", "dynamic:f82ccb7ce7cc343c", "dynamic:396e11798777c9cf", "dynamic:6fcd507b97b78a80", "dynamic:7adc4509a91cd4e5", "dynamic:2b6092a8fd130a21", "dynamic:bba7b93372e59dcb", "dynamic:d8d494c5b05c7d89", "dynamic:882688377b43682e", "dynamic:448ff6bdbb08412f", "dynamic:d66aa4c214ac55ec", "dynamic:066a431a900bf16c", "dynamic:366b19d73591ad28", "dynamic:79f67727176c0839", "dynamic:558084e8e8045f25", "dynamic:464c5daa00a0fa32", "dynamic:5e79204f824d7e40", "dynamic:f778e0668249ea49", "dynamic:6b74544df4642800", "dynamic:a89aac8b9714dafd", "dynamic:ca4f132db9e43dfd", "dynamic:b14c4717b0274af0", "dynamic:a4f15b252a42b8be", "dynamic:f573133e8dd43605", "dynamic:584e2fc74ae4201f", "dynamic:14b2edb9501077b0", "dynamic:ab8504875b518f30", "dynamic:1f7278e63ce1be8d", "dynamic:8d5e215cfbb1b496", "dynamic:1726f4e40df478c6", "dynamic:f809e4b538f44932", "dynamic:2d43cc95d878eeed", "dynamic:5373626f16409869", "dynamic:79f76caa15641901", "dynamic:3d999b4364dced49", "dynamic:35b0fd266b4512cc", "dynamic:6ab9c4f8234451c2", "dynamic:455dd1785862555d", "dynamic:0f7b65f44a99fc52", "dynamic:bfe2b4e7cb526922", "dynamic:e17bbeabcb571972", "dynamic:61879b2fabc5b62c", "dynamic:6c6dabfe6b4f3569", "dynamic:aef0947fcc4f95c3", "dynamic:ee079dfe610f899d", "dynamic:44a9b46b53460299", "dynamic:33b414eb3adb40e3", "dynamic:c3708384478de0a1", "dynamic:f55c233cfccc8a60", "dynamic:6c3b99b5908fb67b", "dynamic:8f5a8d1c371fb5ed", "dynamic:a6b69ffb044b05e1", "dynamic:3aef64e6b73ec46f", "dynamic:8fcd02ada20a15cf", "dynamic:f6f0f40f1db62a0e", "dynamic:e6ac0a1bf99d128b", "dynamic:653d142d51141c75", "dynamic:42bfb7ea28fad823", "dynamic:f9f69ecf6aa9f685", "dynamic:73776eb6f1324a94", "dynamic:90fff8d49f28ce13", "dynamic:cab9334fbb890a5e", "dynamic:a807b546c58a9edf", "dynamic:31b3d5652b0bf2d7", "dynamic:5b72bbd7e0e27ccd", "dynamic:b0808a11fb241cf0", "dynamic:16850ed35c72c7d9", "dynamic:2520f2f65dfe3c37", "dynamic:b8ab4f8f64eb2a21", "dynamic:a0e01d52f695422e", "dynamic:81f75a41b9c1d9eb", "dynamic:7c317f2eb933f049", "dynamic:1a794bd1bee14c2a", "dynamic:8a8636cb8b9714f0", "dynamic:5ebb08d860304172", "dynamic:bdb7da4e9968f483", "dynamic:d276078dfae1778d", "dynamic:54da5b77eb9717a2", "dynamic:ac027868df1ebb39", "dynamic:b12d556050735af4", "dynamic:820f5ed90380043c", "dynamic:eb00bb94f99a6498", "dynamic:5a311badd2261a35", "dynamic:becc8d44689d7005", "dynamic:f1f2e3ea55db391f", "dynamic:72efde7cba596fc8", "dynamic:b21488b1e465f085", "dynamic:3c65e7222496b94b", "dynamic:a890ba60f0f3b2da", "dynamic:140698744b3c8c78", "dynamic:f05978116145fa22", "dynamic:14feeaf982dc1ae6", "dynamic:16ef30efcf4ffa9a", "dynamic:d592a91c407a2479", "dynamic:339fbf7ef58d5075", "dynamic:48097cf8e7c9407f", "dynamic:bcdfd63d9621a5c2", "dynamic:bcb93af8bed8ec06", "dynamic:5310fc44f434eac8", "dynamic:3743828443ccf168", "dynamic:d993b7f9f3f3f9cb", "dynamic:48d77e3131215094", "dynamic:64f2f534290e0227", "dynamic:0975c61f3cda56cc", "dynamic:e36226d6caaf2960", "dynamic:987ea57cbc95ae94", "dynamic:869fbff348fac9fb", "dynamic:accaba28f10cd384", "dynamic:59977dd3ac7e1221", "dynamic:dfeb0f8a02c040b2", "dynamic:2a69283d47b60e8d", "dynamic:8b1fc4d77e08b976", "dynamic:f77816e1c1111eeb", "dynamic:fd6f684a5ce7dfde", "dynamic:c3ef6c4316dd9aa5", "dynamic:14d05b31a655610a", "dynamic:7d37908b64f64878", "dynamic:7abd3f451a4c7d96", "dynamic:ba3c17093f6e571d", "dynamic:99e5690bf1f05d13", "dynamic:5b3ac1b56fe93220", "dynamic:76c19d5b95f0c123", "dynamic:627c0d1bf8f8693b", "dynamic:ff92f1e8f3c7faf0", "dynamic:42f0b52797926759", "dynamic:a46d03a3168ac0c0", "dynamic:98417f03d597e4c9", "dynamic:8aae7bbcd70316e4", "dynamic:95c2b01998ddfce9", "dynamic:4b0855a8c04a35f7", "dynamic:26cfa0afcfc1ff96", "dynamic:14d517b119dd6051", "dynamic:8fc5b6efd002ab9e", "dynamic:72031e2b5936cf5e", "dynamic:74582a7edc45cfe4", "dynamic:a775275915cc9399", "dynamic:6227ddc9c58f1503", "dynamic:ae677491f27d3ea8", "dynamic:92a927691b402995", "dynamic:688e26a1de5bfe03", "dynamic:d3cf8b2c299704be", "dynamic:40c1f0f010f69827", "dynamic:caa403ce06ae3730", "dynamic:6644a7d9fce787e8", "dynamic:6f3d3004fd48bcd2", "dynamic:1beb1699df7ae61f", "dynamic:37a09dd87c0be50c", "dynamic:043d9a2b945c8185", "dynamic:8a222e33021429da", "dynamic:b77c5dd870c0cee7", "dynamic:dcb8fff0b6885bb1", "dynamic:f64704c656dd7c52", "dynamic:dbcef207598420c5", "dynamic:a57a4b24cf5ec77d", "dynamic:4878f19dd39fda14", "dynamic:15fa42bdd892e8ff", "dynamic:0531d71284a8e2bc", "dynamic:34b5eacb4f7c4d01", "dynamic:9ff059d91eb66a43", "dynamic:9af842dd72ca5100", "dynamic:482f85a0db5e7045", "dynamic:554bf2ec88beba5a", "dynamic:fd908c9d96947b74", "dynamic:0b61a43cafe6f170", "dynamic:efc127385a7edc32", "dynamic:e351169904be35e1", "dynamic:1dcc87afe0a078d5", "dynamic:4d3b6dbad7c11179", "dynamic:e96ccedc002aacd0", "dynamic:3c704744e573c5c1", "dynamic:155d94d83c0dc09e", "dynamic:9d11517ebf2c5bf5", "dynamic:4ca52141f74537f0", "dynamic:584ffa6b6488856d", "dynamic:b450f4444bb4e498", "dynamic:9a4125491e7ab68b", "dynamic:f46bc660746f5da5", "dynamic:0697009adb2caa5a", "dynamic:76b9cec6960d3ae1", "dynamic:a190a31804f84208", "dynamic:079540bc6258bd7d", "dynamic:6e18691847897300", "dynamic:58bea625de9f6e51", "dynamic:41369ddf66fcdc4f", "dynamic:cc0e83abfb99aee4", "dynamic:2a50d46253f7d3bd", "dynamic:2b1c3eeed18eeaad", "dynamic:f83bc8a83b1c06c3", "dynamic:25755233d12a3ae1", "dynamic:b703d743476c376b", "dynamic:dcde6680570057c8", "dynamic:6bd9f8976b413563", "dynamic:d0fa735229246afe", "dynamic:7c854b5b635b5975", "dynamic:c4f5d6ac750e653e", "dynamic:f3f960a807deb5ac", "dynamic:15bc2517bdf08e45", "dynamic:a5147adcf290fa93", "dynamic:09f60cd1609d64a9", "dynamic:b30c206177eadd2d", "dynamic:89183774b9c2217d", "dynamic:51f71cdcbbd95b25", "dynamic:ec168c0f130cd6f9", "dynamic:f54133105b073463", "dynamic:54ac485e57788495", "dynamic:da055e6c86aaa826", "dynamic:267f5f24e6af7876", "dynamic:13e5cdcb5a9007fb", "dynamic:1ed12b28f206b5b4", "dynamic:56d1de828442e9b3", "dynamic:1f8c7626e51e16c0", "dynamic:951e6e0c3e507ac0", "dynamic:1d81ee4820a50693", "dynamic:29ac9d9062c1f1ac", "dynamic:80a1ad6cbbd6c2e2", "dynamic:2797585fb186eca4", "dynamic:a25996c9ae312145", "dynamic:50f2435a1c79d357", "dynamic:63d7d9b3857bc4a7", "dynamic:4a62a956ecfe9095", "dynamic:19a54037a260ee13", "dynamic:2c836850e5a12099", "dynamic:b3b6acfeb108a281", "dynamic:a8f6e3318c2692d6", "dynamic:8cc3263282a31162", "dynamic:f7ba5f3b23b695c7", "dynamic:693b5f1cabaf31c7", "dynamic:7438f828bb73279c", "dynamic:8fea48f733371340", "dynamic:0b335de59ddf5887", "dynamic:802d8344ce8bc501", "dynamic:eaf79a6c66b801f7", "dynamic:796ad527f570f5c3", "dynamic:90862d5b7e3a17d5", "dynamic:60ba01ad9d4c1b6c", "dynamic:35a5e617a9c1a3e3", "dynamic:14afa887b7382929", "dynamic:3716a261aa1d6e40", "dynamic:a93f4c589f536516", "dynamic:aa54a5ee3e9c5937", "dynamic:335bcb0cb4db2ebb", "dynamic:0bbe268d23ff6fac", "dynamic:59a5f2371f57326c", "dynamic:84d6ac472fb1d95f", "dynamic:cc38ac10b07b30ae", "dynamic:35fb080edab34689", "dynamic:32b9a2a9f1f96492", "dynamic:a821482f4de13525", "dynamic:f8cd8212ebe930eb", "dynamic:683c5ba43ca68d2c", "dynamic:3335ad9878e360a8", "dynamic:a047521a46cf5f45", "dynamic:8a8df9b14045ee43", "dynamic:8cf4a45bfd55b9ed", "dynamic:93ddea4adea75252", "dynamic:e9a66a68bdc7cf2d", "dynamic:cfef840e78f2a319", "dynamic:738824509c335743", "dynamic:30885e7f4b289834", "dynamic:e88a59f6d5c7b5be", "dynamic:7d13464cf59e7e79", "dynamic:e17f879e72fbe8e1", "dynamic:0a942ebe44183f1e", "dynamic:3fc41876d019c1df", "dynamic:fa48ff0f09891e85", "dynamic:75c59a020c1d8bb4", "dynamic:ca94dacb82fbde01", "dynamic:50986052784a8514", "dynamic:b35d23f12b5f5fbf", "dynamic:da9df0e9002a0ddb", "dynamic:3b03eb7516baaa62", "dynamic:6dd9c21e7b8acc78", "dynamic:2e0a16ba087230bf", "dynamic:7d789959c9e434c8", "dynamic:4cd2ebd2b6554c50", "dynamic:e3e126eb34a53504", "dynamic:3813a9b930f6c965", "dynamic:8468a8cd3fadcabd", "dynamic:d1eb2977bdbdd28e", "dynamic:40b85cfeed9bf144", "dynamic:b23dd969a4d5ab0f", "dynamic:59524d77fe7a6d56", "dynamic:fe9f7de61bfcd2c9", "dynamic:0a83dc462955ff71", "dynamic:0a67ec873a9ca428", "dynamic:002a98a9519cf41b", "dynamic:53eec7c807260cf4", "dynamic:885c568921dc183f", "dynamic:13e008fc0ae5748e", "dynamic:c1fb211756ae814d", "dynamic:f34882a7bc93afe0", "dynamic:72106138f250d8ba", "dynamic:b6f042b504d81ebe", "dynamic:7502fa91052e8f47", "dynamic:ba779a418b2965dd", "dynamic:ec31c64c5fa09a6a", "dynamic:27f439d7617115ad", "dynamic:3ccb2bd12f40d86f", "dynamic:9aa4ac7a4d8680c0", "dynamic:cb79518ca1f485bb", "dynamic:eb87ee287466373e", "dynamic:ba770d10bf90ea1c", "dynamic:656ff90bbc309b65", "dynamic:00a42929c509cd2e", "dynamic:f998cc860f821437", "dynamic:d14f15f969fd30bd", "dynamic:2dc9c8ec307cde07", "dynamic:fa08cd1ac9badc13", "dynamic:a6f0ff855055701b", "dynamic:0404b1b4f49801d6", "dynamic:86c1cf9c2780738f", "dynamic:f4702e826598810c", "dynamic:5ce51c75c74fc2bd", "dynamic:d470efbc3948b84b", "dynamic:c9141567065c8aa3", "dynamic:461345cf2cc67fd1", "dynamic:e498eec91a8e3ac1", "dynamic:7336417277c289e3", "dynamic:2ec95f98cf39ba7d", "dynamic:582753352e048c25", "dynamic:27134d58da81fc10", "dynamic:425abb894ada2cc6", "dynamic:f57bfbc651af0fa0", "dynamic:d8299c545e9200b8", "dynamic:d22e083b4fd7486b", "dynamic:964c13088b56c772", "dynamic:b4a57c5baff22fd7", "dynamic:b86a8a71fbb06a9e", "dynamic:78a2b8d698c0e7d8", "dynamic:fee798c40486ee1c", "dynamic:e93e5b3769a4adbd", "dynamic:e9f8d393718fb4b8", "dynamic:9294f3c0f5bb7745", "dynamic:31faaa0fec4d3600", "dynamic:0b8cde2798a5d217", "dynamic:fd41a19972269547", "dynamic:aed2b472930c5873", "dynamic:80f670bb7a8e5bb5", "dynamic:6643035cbea7d85c", "dynamic:4f7b09e24d8a3023", "dynamic:26bb43d4e6a3fab1", "dynamic:40495b863add87c1", "dynamic:910186215b26a422", "dynamic:deedd56d33604b0f", "dynamic:67d46b1af48381b9", "dynamic:e3f30784bc62f3a9", "dynamic:32f8169414c2a493", "dynamic:46d5dbddb6be0bf3", "dynamic:3e13a1a7c8ba4a0a", "dynamic:c839fe9dca41730a", "dynamic:1db9eed4b9e1e525", "dynamic:0ffdaa1687e1b743", "dynamic:c709f06d7a4c38ca", "dynamic:fdd70745c31a658c", "dynamic:a561ac0c44f66e37", "dynamic:f2100c898d5bcc97", "dynamic:9327c528595e1c56", "dynamic:6518460cb8fca068", "dynamic:6f34667a7717c8c0", "dynamic:4c5652d223c0e31b", "dynamic:bd28a35e4855f143", "dynamic:b0e12746c71b993a", "dynamic:1c67611d021cf26d", "dynamic:54f72c58b33965bc", "dynamic:54736ca6976dadcb", "dynamic:aea625077fcd63a9", "dynamic:f3fff4ae60196c11", "dynamic:d14274efa17c5102", "dynamic:b19f641abfca4adf", "dynamic:2124d244b0974d93", "dynamic:4fc5f450ce43382d", "dynamic:5aaf7c32e574f478", "dynamic:544fd695cf23f690", "dynamic:c4fbc366d4223f62", "dynamic:720c2277fa6284e5", "dynamic:d6abc7e978790772", "dynamic:017202a2687066cf", "dynamic:d155f348f06b12e1", "dynamic:fead12f0abb00473", "dynamic:8c0473ea0127842f", "dynamic:c5504e5b9f326f63", "dynamic:4abcf1552eaf025f", "dynamic:88d038868e241161", "dynamic:78b6b53039376146", "dynamic:990d1fd89fe8b83f", "dynamic:4b072b93bf242880", "dynamic:1f1822a1ca6a8ba0", "dynamic:ffafb7ee0439ac8d", "dynamic:25962cc1b928e321", "dynamic:58855305ba3bd8c7", "dynamic:3f116ce2794c8eab", "dynamic:e26a69081b90e9d0", "dynamic:ba8e5b5965503295", "dynamic:804899eb692a1af4", "dynamic:3f2914c0c9cd667b", "dynamic:abd1967671e876d4", "dynamic:2dbcc77b0fdf1df6", "dynamic:c5bd2e85788b9a3a", "dynamic:cd51bf76baf9180f", "dynamic:7365a2703638b74b", "dynamic:9ade74be8893a414", "dynamic:080d7c1ce0015bee", "dynamic:adf3aa49b1483808", "dynamic:1a848c48f5c66960", "dynamic:59dd03000e37eb14", "dynamic:313a9fa1b080184b", "dynamic:470a7ece8ba4042d", "dynamic:5cb4c2bc0688392d", "dynamic:28cce74df974514a", "dynamic:8aed97b2dfa25bec", "dynamic:a5ec47f19efaa9a1", "dynamic:9943bfbebf4585a3", "dynamic:30a73c2eece0ed69", "dynamic:d4a6da7dd3ba832e", "dynamic:086a4d83186ea571", "dynamic:2ebedbd4624950fa", "dynamic:2cd14f468085c138", "dynamic:5d1270f95efb3530", "dynamic:643a2ed041764684", "dynamic:b1e343d9c681657c", "dynamic:d35d98d30849228e", "dynamic:b64d2919dcced192", "dynamic:72bf972053bb4630", "dynamic:62a7134aef37bbe4", "dynamic:5bebf5c58abd8b22", "dynamic:012bfcec5c1edf8c", "dynamic:2ff3efaf4a7321c5", "dynamic:d7cbb78120a75a3f", "dynamic:7c495869052b5925", "dynamic:4f2dddf768af2bb7", "dynamic:17e55dcaaef9bb8b", "dynamic:33a57912808d355d", "dynamic:01c512e1215b40a7", "dynamic:e1903d2c583ce325", "dynamic:22c091b1adbe8f92", "dynamic:2df29d5ec0f94b9c", "dynamic:e1af2b0fcff8bb31", "dynamic:055a21d678d24d16", "dynamic:d927446509db5f68", "dynamic:2f52ef165b99d7fe", "dynamic:ba51d21d9b0b08c5", "dynamic:2ba16351015ecfdd", "dynamic:5f3a44b400ab1c28", "dynamic:27d97683f1bd75d8", "dynamic:94b4b142a3c7ee0b", "dynamic:3438c333f9128279", "dynamic:853f71759460c1d6", "dynamic:40098bc130dc9c7a", "dynamic:08a1162cf0972884", "dynamic:85a52504e85d8bcb", "dynamic:b9bab5ad2e068be7", "dynamic:acaeda4ae03d639f", "dynamic:3fbc90f4e9dcc2c7", "dynamic:7dba0d4b55a9aa9f", "dynamic:6beb8cbffba404b8", "dynamic:b02ca06515e9a250", "dynamic:a48d9a3ce7f2e84e", "dynamic:847cda41e37f4bf1", "dynamic:df7d1a0eb512835a", "dynamic:0040f43cde03a2f4", "dynamic:120fe0671b44a611", "dynamic:db98da36dfd1365e", "dynamic:9641cd7934ab2e03", "dynamic:63d16ba4913911ce", "dynamic:fce56fa46463a6a1", "dynamic:7b0801fdaec10efa", "dynamic:3bac3ab91e761469", "dynamic:67dceff5cab21dca", "dynamic:ee9a9ca4dd398618", "dynamic:28e0254f3d3382aa", "dynamic:bb418f6e2fef2e9a", "dynamic:f844b23b28b13038", "dynamic:beba1e1439fe167e", "dynamic:ca9bf049adb8980f", "dynamic:8ba1e6dc15a53e0c", "dynamic:c18efa4eb09b885d", "dynamic:a575658af8bf53f2", "dynamic:d1cbc4c1d3345530", "dynamic:5fcf25569da7d7e5", "dynamic:1bf3df0fcc1ba194", "dynamic:051f565cf2833618", "dynamic:9b1bce11b2cdfc0f", "dynamic:d87b18b2d1371a65", "dynamic:5b4d8ed7ca43a00d", "dynamic:b61f1ce5d923309a", "dynamic:f61b612c8df32897", "dynamic:12aeb7c6a89dd144", "dynamic:3edc023bbeac0d76", "dynamic:4c3b8168058b0eae", "dynamic:afa81f4a8f64d549", "dynamic:0fdcaa1eef8cbccd", "dynamic:90d43448f22d75b8", "dynamic:7cbe138a2316fc1f", "dynamic:a9a96f5f040af36b", "dynamic:ed6461bf0575bcf5", "dynamic:c73d69c845bf4a76", "dynamic:ce218eaec8658a6b", "dynamic:ed383740775bd2eb", "dynamic:1e9b7c0c4784a5a9", "dynamic:ecdcd34bd56edbd2", "dynamic:ea0d8c2b0f062d42", "dynamic:7e577e03a8d1ba2b", "dynamic:912ec3b998fb5b6c", "dynamic:cce9d8a08e004ed1", "dynamic:6c88bc91c8cdc20e", "dynamic:3949527971dd754c", "dynamic:54c5f2e8c2e3f016", "dynamic:a4780a480288853d", "dynamic:35f59ad3d04fc9ec", "dynamic:e73f5222d0073e40", "dynamic:5eac4d394a52dbd4", "dynamic:860ba5855602a077", "dynamic:0f6e78adfff6022b", "dynamic:dfa97827728544bc", "dynamic:dc891ee3d9744538", "dynamic:c2b7a60e169fcd4a", "dynamic:52aefb2da6701557", "dynamic:29c3df295bc4dd52", "dynamic:c3d15a00bf58670b", "dynamic:03701ab1c8b652f4", "dynamic:50f779a25cc92a1d", "dynamic:325c8582ca74e1d3", "dynamic:708504f85480d576", "dynamic:163225c23a3c5bed", "dynamic:1045d9a9ab9b7e5e", "dynamic:3a24d01daa5b491d", "dynamic:41d5a2854dcd3c54", "dynamic:8feee5607bfe5cfd", "dynamic:f3a470e0c99caaff", "dynamic:8a953807b434aa60", "dynamic:2bc72203dd40c778", "dynamic:e668109bcdc10bee", "dynamic:834963be7bb52213", "dynamic:c80ed725648fda28", "dynamic:d17389f0ce6c3efc", "dynamic:16682af4f0db50e9", "dynamic:2357c781b3c8b002", "dynamic:378fca06c9abf285", "dynamic:e6f1d6f6c8296126", "dynamic:1fde744e9185b0d1", "dynamic:2fd4bfa9cc1eaa68", "dynamic:531fcd1d5e939f23", "dynamic:801a63283c03e32d", "dynamic:6eae5e38438c8c83", "dynamic:e60a72cee5dd49b7", "dynamic:e4c22a4b385f61f2", "dynamic:dd4abc74b7c09ebf", "dynamic:c90d374b37bf36e5", "dynamic:e3b4ad4f02a79a98", "dynamic:8ee17964656139b9", "dynamic:3f0cb1e61a316d18", "dynamic:942d5a1247061ca6", "dynamic:623cb8344609d597", "dynamic:f6a37b64736cb04e", "dynamic:7de9cc152972cc1a", "dynamic:5e65204556db8ad8", "dynamic:cef1b30f7d984eb3", "dynamic:3e6f926475470d04", "dynamic:63684ce71f2f4726", "dynamic:84dfeec22b942aaa", "dynamic:74f17166de2eb18b", "dynamic:b06f21178b27fcb8", "dynamic:148a52de42ea7f36", "dynamic:e7bb01318e1b302d", "dynamic:7ad11ab622aea149", "dynamic:320c700d1771be5b", "dynamic:ffb332d10d58c003", "dynamic:e10cdbef7e5f1609", "dynamic:23b43c47769605f3", "dynamic:9bf3b654c3b7a533", "dynamic:d360698dfb6653b4", "dynamic:818fa4647ac87215", "dynamic:ae7d21e31a258ab5", "dynamic:3e5a20f8b0d16d0b", "dynamic:503685b07f1b306f", "dynamic:95e9bc97350767e1", "dynamic:f34c677255d751ac", "dynamic:bce1f1754ce597bd", "dynamic:4b4ef53b989ebaf9", "dynamic:f48bd06ddad8d18b", "dynamic:172a1b40028ab504", "dynamic:08ca89f208fb7a72", "dynamic:fbc8e64698a7e99c", "dynamic:1197412583bc707a", "dynamic:5c2cb85e5158283e", "dynamic:4538ca79585fc8d6", "dynamic:9e88f0da3e34f10d", "dynamic:86c329bd042e5100", "dynamic:163aaefcb25ab663", "dynamic:123b042936084060", "dynamic:82bb4aedfab4380f", "dynamic:1d097d0a5ef5016c", "dynamic:bb9c820f4cb4dcc0", "dynamic:ac2c3c9433a31992", "dynamic:85b5ded4cb90c3be", "dynamic:738c176191e3bb32", "dynamic:95ee519425e8f9e9", "dynamic:d0d05aa05d08f032", "dynamic:9e266e99de78fe7f", "dynamic:0388c246dd4dcd65", "dynamic:968f0a0c9cd16c58", "dynamic:ee04230279909483", "dynamic:95dabb089306b992", "dynamic:dba432946477da6c", "dynamic:ef5c9b19f433adbc", "dynamic:916ba00d1bd41dd7", "dynamic:ad96052c0d0eee6d", "dynamic:5436f950e0e73119", "dynamic:ec68bc56d3df176d", "dynamic:d99c1c2a1a6aef7a", "dynamic:3f8a4e56b31aee27", "dynamic:7dfe6f52e1049bfb", "dynamic:67737df8591f255e", "dynamic:919fcc09c57b746a", "dynamic:a7d4bd65ffae0a0f", "dynamic:a92004d407b7cbac", "dynamic:8619caf8444ab334", "dynamic:4406bdcd922e5d47", "dynamic:f7ce14a6fa441373", "dynamic:38c9cdc7b863dcbc", "dynamic:bf3d49990f4c2660", "dynamic:1c016f35bbcee1b4", "dynamic:a8fb92e184aa080a", "dynamic:9a1453cb7d2e604b", "dynamic:41bc66c02926814f", "dynamic:4e0026f3b33ab1ef", "dynamic:f353b7918c90512b", "dynamic:12bdb796b85cf6f7", "dynamic:6ae6e62065c3f11c", "dynamic:baac5918bcfe2bb8", "dynamic:5793800515b120cd", "dynamic:97de3036a7655500", "dynamic:cb88d41e071e9d92", "dynamic:475642b3b8a0c5d2", "dynamic:f096bb85d8f7fb47", "dynamic:6a96fc913ad608f8", "dynamic:4c8ddb644b8f66f9", "dynamic:975746258d11fadf", "dynamic:9b8e17202a899a32", "dynamic:2fc1a01b1c1b43c0", "dynamic:aa923a12c6dca963", "dynamic:1cbc37569baeec78", "dynamic:edd0c856e6486aec", "dynamic:1dc7ac6a13fe956e", "dynamic:ba512e2a8307f60f", "dynamic:f1bcdee95e159786", "dynamic:475dd6404393dab5", "dynamic:0b84beabc2fac016", "dynamic:09c9d043d7ce3f29", "dynamic:d45481a8477b0137", "dynamic:3d6b7e1c32b09980", "dynamic:9152eb4b54373a51", "dynamic:de8ffaa5c3d9fd84", "dynamic:f6cc830137276f0a", "dynamic:3ac0251137b4d1d3", "dynamic:d06e11b50558abc4", "dynamic:802fddf719daf423", "dynamic:ff63ef9cbff52126", "dynamic:8f1bb7c429499bf0", "dynamic:340ca0fc31ddc2bb", "dynamic:6e254ff43a2b6ea5", "dynamic:501dd78d133ba55a", "dynamic:f3a5550d1970911e", "dynamic:14168f20c7f1cf88", "dynamic:baf4080a50ec14e8", "dynamic:fd273bcde948940f", "dynamic:8e69e79aad84ae0e", "dynamic:ad5528f267924b8f", "dynamic:12a5cca2ba681574", "dynamic:4859be0564db6b9c", "dynamic:c7f88114ad1fa129", "dynamic:0c5d8c283970b74d", "dynamic:659eec06f7c7ad00", "dynamic:372fd6e67a98dd55", "dynamic:2ea1469b9297de77", "dynamic:8e32438291a6a3a5", "dynamic:f46c8ab0170439d6", "dynamic:74dee222aef37ca7", "dynamic:4a0736f8d8027aff", "dynamic:46ccd7bcc87805fe", "dynamic:ff8fcd749552502f", "dynamic:4462cf1968e7a37b", "dynamic:f5caff7d8980df44", "dynamic:092829a57f9322c2", "dynamic:8ee4243824c21efc", "dynamic:51d62d805a6867e4", "dynamic:e436bdbb9973c9a9", "dynamic:9958e40c5fce59fd", "dynamic:1d2c497f3a62975d", "dynamic:61c3d1da42b6479f", "dynamic:14a8987c469ff3df", "dynamic:8945acc6023b2126", "dynamic:78110580b7c8141f", "dynamic:53e3b82e79113873", "dynamic:e28c0827c156b392", "dynamic:1c3f8ddf27e9400a", "dynamic:3fcd2b854fd0b854", "dynamic:2ce6afd157340017", "dynamic:c661e0bcc652dffc", "dynamic:899848069379a6dc", "dynamic:a036209981d21400", "dynamic:fa0c873a81d92154", "dynamic:00e0400acc380ac2", "dynamic:e54857d7fb1dc553", "dynamic:06e634665ecc74e0", "dynamic:c9f3e76e057dccb9", "dynamic:31feede2447dcf56", "dynamic:4058f871cc65687d", "dynamic:d4744c94f98afd05", "dynamic:b5e86021e29b63ac", "dynamic:27277fa75bd5229f", "dynamic:9246b570f04bdd2f", "dynamic:239ec680918e06df", "dynamic:fbfac0fdd210e8fe", "dynamic:58fb142a2e7269d8", "dynamic:a30830c87e472289", "dynamic:31c79a30b10bfe51", "dynamic:29d2560668971f39", "dynamic:bba480be3f7afcd2", "dynamic:a16b701ab22c712b", "dynamic:6e9b37ddb58c0586", "dynamic:f9677e1acd1b689c", "dynamic:169c59486b47bece", "dynamic:16c8ee97506220a6", "dynamic:942716d1a84caff0", "dynamic:5b8df709db3dfef1", "dynamic:661a7f654109ec9d", "dynamic:546e50c7614d28fa", "dynamic:9d6f13c041698a1d", "dynamic:a37508e2a5f8fe54", "dynamic:437ebd31ce3cc6de", "dynamic:75e5f203dbd2a043", "dynamic:85717cc0a27e62be", "dynamic:d7c91d64fcd9ee99", "dynamic:1de5c314da61c3a5", "dynamic:4cbb349ab5569801", "dynamic:195a828d13bdc609", "dynamic:244d637ca5cff31a", "dynamic:cb21cc3daac9ead5", "dynamic:6c97e8f4048f604e", "dynamic:b7b38d58c01d6606", "dynamic:d85c50f673d96621", "dynamic:4690e59c490fd183", "dynamic:99d668b2d5143bce", "dynamic:dfd3309ea56823b2", "dynamic:0be8fc3a7d7afa53", "dynamic:8fb8a657136fd80f", "dynamic:388fd26e2d2aef0d", "dynamic:b9ce92ae9110b024", "dynamic:55c7e7d09ffd14a6", "dynamic:b6115a1c4ff9b9a7", "dynamic:01ebcb731bfe56bd", "dynamic:7a9c94c65952c04d", "dynamic:d4cf45f41f4a9950", "dynamic:e64e4a9c3850d2cd", "dynamic:90a0835b740530f6", "dynamic:63ce7b55ecc150db", "dynamic:50f25b75087aca78", "dynamic:54861e821320d8bd", "dynamic:f03afcb2cb545b41", "dynamic:a0c51e781afb3690", "dynamic:a7591921d3003c1d", "dynamic:e2ebff7d5a32ca29", "dynamic:01ba23fc209ff27e", "dynamic:ff1101f6b7fc764b", "dynamic:60e5dfde74d6b411", "dynamic:29aca38705bde53a", "dynamic:43fa2ada85fc1e65", "dynamic:726853690b4e4d39", "dynamic:5e8dacdc863d9f4a", "dynamic:f504912729553124", "dynamic:25de26666955a3f6", "dynamic:b4511400fd014c3c", "dynamic:02895eb9833229b4", "dynamic:38bba111f9f8c5b1", "dynamic:1d4902bb6b91ff1c", "dynamic:78fe7d669c6d7d63", "dynamic:9231f3c1e7061057", "dynamic:898d1f6d5666c76d", "dynamic:c443ff210a7ebcd2", "dynamic:4fb95e4f89af380d", "dynamic:21fa2c053b77b80c", "dynamic:a2feba8aefe12109", "dynamic:a5b5dfe847841963", "dynamic:66630d1dc4c90beb", "dynamic:d23e62e60e0d1445", "dynamic:76d1b3ba6e975cad", "dynamic:5a4fd3cd158481bd", "dynamic:f6fd0485b5739780", "dynamic:46f05b5ec2d54101", "dynamic:85748dc1f09debb7", "dynamic:780fa81dbd735780", "dynamic:ad5583631fdf5903", "dynamic:e600f7153a33be3f", "dynamic:bf69110e7d9811d7", "dynamic:67f760775125100a", "dynamic:cdd3e262c01aaa8c", "xaml:Исходники/AIHub/MainWindow.xaml#251a9f95be43", "xaml:Исходники/AIHub/MainWindow.xaml#62f1de1889c1", "xaml:Исходники/AIHub/MainWindow.xaml#520577d7f7e0", "xaml:Исходники/AIHub/MainWindow.xaml#d0af7805f9b6", "xaml:Исходники/AIHub/Controls/BookConversionResources.xaml#d0af7805f9b6", "xaml:Исходники/AIHub/Controls/ImageAnalysisBundleConfirmationControl.xaml#251a9f95be43", "xaml:Исходники/AIHub/Controls/ImageAnalysisBundleSelectorControl.xaml#d0af7805f9b6"] -->

- Изменилось состояние операции. Подробности показаны в окне программы.

### 0075 · voice.61a6ec44270cde8e

<!-- LOPATA.Events: ["source:eface2888cc863a3", "source:d54168cceeab2105", "source:ab5e8e7973873d44", "source:8b1982aba696250f", "source:a537254695ae6f3b", "source:85b3c3060d7de359", "source:97c1a789918fff1f", "source:153c4721f6e79ee2", "source:1c8b77e0e4d568a2", "source:a7a67593bdaf8885", "source:9531c332318752c0", "source:58ca18dee4ece19c", "source:d920964eb14c8348", "source:7d6fc5e28223c1ee", "source:bf0039e6d97458c9", "source:83c2b6f89ae49567", "source:ddfb16871d62a975", "source:e08768e9a02c7688", "source:a27875ec08096602", "source:1ce683fd44d0ec74", "source:2c010636770f6e20", "source:19473e9ca95e5a5d", "source:015c78210b4c485f", "dynamic:a0358d4eb958736a", "dynamic:802fb323b83e45b2", "dynamic:e5ea268275783dc3", "dynamic:a7557384ca8a12a5", "dynamic:a30c959620b90559", "dynamic:9bd8f48c693cf5b6", "dynamic:4646eb2889f0321f", "dynamic:4a1708daca3e4de5", "dynamic:0b18a178cce38037", "dynamic:06a7eec161eadc99", "dynamic:c88196b9ead6163f", "dynamic:0113f3b0ded839e0", "dynamic:5f8009deaa4875c1", "dynamic:d599764bf46d15ba", "dynamic:14a1392ee127ebe6", "dynamic:d2fd1d612547f2f6", "dynamic:7d9d42c1d0c08b6e", "dynamic:9c1d52ad0c942bd2", "dynamic:3544d560eda70fbc", "dynamic:d9d798c746a122e0", "dynamic:a30a137ff84e27d3", "dynamic:d746dc1ff37ed31c", "dynamic:1301790af0b41a21", "dynamic:2c72df99d57a717f", "dynamic:42fa7587d2ef4d67", "dynamic:bca8a6e2769f4ac9", "dynamic:15419265f19404b8", "dynamic:e93d3daa37a2cc9e", "dynamic:3bd3bc706602f74e", "dynamic:01f0d1844fb1e970", "dynamic:a493be5566cea059", "dynamic:8bfe8a7d60f53284"] -->

- Не удалось завершить действие при подготовке видеоролика. Подробности показаны в окне программы.

### 0076 · voice.04232841af06e033

<!-- LOPATA.Events: ["source:378bbe7e3c44a9f2", "source:852105af05f5429d", "source:50a290ad918798ee", "source:47fb64f2ef34e9db", "source:7bd57dc80d0c4b4f", "source:ef5e0f1490ad3853", "source:71ef7cd89ec6c141", "source:10048c86406c70d2", "source:bf6cf9fd87a33911", "source:c3b5c5c3a0f3a7bd", "source:5d88548b2fd8bd07", "source:e55a7e7856cda9ee", "source:80a26bc24e801d0b", "source:81dae88a01f6577c", "source:d2aeb67e91aa3bb0", "source:a1cdcd563fb2177b", "source:423c7c1325b1fdf9", "source:826be4c554fc7bdd", "source:ded5faaeeca661be", "source:36f9e706d79e6b96", "source:bb4a1be3d908f202", "source:ee3326de7d998257", "source:c2046efb3ba51bae", "source:c988b2fffda4a589", "source:34c0a8f33fc8b213", "source:f5fa5ed1a5660d95", "source:e5bc4e4ed84404a8", "source:c0db301a57385f62", "source:ec0a32784d7fa746", "source:ffe27738d1ff032f", "source:6d3fb2dc7b4bf06f", "source:b85a59a9e30a9683", "source:2dc539955f4730c9", "source:b49c821e9a83f626", "source:81e0d74667c4fa92", "source:0d6a99608e74430f", "source:08294ab981641404", "source:f3f6afe8d528c64a", "source:e3b67ef39636e4bd", "source:df89f7b38d4eadc1", "source:69edc337d35a6020", "source:9315f1ad6954a691", "source:da9443d9fe6efa75", "source:7c4b0c2f702441b7", "source:e691867e9936badc", "source:549e73f8e17aa191", "source:284f0a90fcf0f210", "source:9d9d21850d95f172", "source:dd854a25a96acebb", "source:08ad7a97e26bdcd3", "source:7aebc008c3a72098", "source:8076ea33bbd241d0", "source:349e13624314bcb8", "source:22a872619387d30e", "source:578d7f59d772f23c", "source:71d36d980650b126", "source:6a4fc3e4ff489fb5", "source:65275c9283cdbeca", "source:572bb84722095604", "source:715f2dd3cbdc6751", "source:54a7dab61eac0327", "source:bd8fe6821fac1ebd", "source:8527712b8a671128", "source:3a31371aeba83d50", "source:766cb80ab267ac3c", "source:2f96c2248946abd8", "source:1017dd85bfe62057", "source:50c421a227f6911e", "source:77748d032366b2c7", "source:af54d328bdc2d179", "source:133c6649f77f6d9d", "source:28d34c3247fba31a", "source:37c4245ed36177b7", "source:a76360b45e1cb70e", "source:f30752c8587bd5cd", "source:f3d174174830db2a", "source:c1dd229cb89024a5", "source:033f3c2690057b82", "source:cf77215a88560de8", "source:3b38a6ed74d433f2", "source:17ea21a023a14389", "source:d0e11f7b7f89d895", "source:35ee1cab789acbe5", "dynamic:2ff766cdf0ee9a0e", "dynamic:65231a797101a667", "dynamic:ecbf09c2dfbabb0d", "dynamic:017f2f1dc3de5a97", "dynamic:25a64f460b06e5e8", "dynamic:f2856cffd3e60d43", "dynamic:b62fbe5ba01acc8f", "dynamic:e8fdf68ae9cfd507", "dynamic:4ef8b06f767e59e9", "dynamic:4300856bffa50616", "dynamic:9ba278852aba1eee", "dynamic:ce3b35d790465d51", "dynamic:593ff7d866a5ec63", "dynamic:d49f8441299b44e1", "dynamic:4cf4b7dc33734190", "dynamic:1c7083d263b84a8c", "dynamic:dcda6cfb8ec92291", "dynamic:31b4ef9a3bb1f933", "dynamic:e8f2907863000057", "dynamic:dbb616db50e75c6e", "dynamic:da31656d97a02221", "dynamic:55ab06a89a722f0c", "dynamic:d8fb2c6dd79b5acc", "dynamic:a19c1a086fd93862", "dynamic:87d6e7e61aa0115c", "dynamic:ce1e70f9c1928f9a", "dynamic:684837b5c1ba35e2", "dynamic:0c0b58087698cad7", "dynamic:adf36268f2986e54", "dynamic:4aeb302e42210c48", "dynamic:73242c162fa7ce3b", "dynamic:d30460a8b94cb88e", "dynamic:2af5f52810e8c2d9", "dynamic:030e303efa58b925", "dynamic:c6b14313c63e3102", "dynamic:ec211edb81196807", "dynamic:8ab2481d4a83dd59", "dynamic:5f20a8213a891a93"] -->

- Не удалось завершить действие при обработке видео. Подробности показаны в окне программы.

### 0077 · voice.130acc73865828cf

<!-- LOPATA.Events: ["localization:Music.Library.ArtistMissing", "localization:Music.Poetry.ModelMissing", "source:284602880a0f747e", "source:deaf6c7762c85b49", "source:b26f5cdbbbd70ea7", "source:eb13655cd82cc0b8", "source:5741d3f11b71d8d3", "source:0b5ef83c328dd72f", "source:ef2f3b0a46de7ed9", "source:43d71891904ecb68", "source:07bd09f14428ab4b", "source:bd5e96b75c05ef35", "source:5f0105282e1e1ffc", "source:8d368f45ec9cbff0", "source:3849c83a402d453a", "source:a6f172b1382622eb", "source:d907b30fb4363520", "source:2f46c2a86aa0690e", "source:1ed271286525645b", "source:188699005dc1f6cb", "source:406e7d69eaef17b5", "source:e8f5503cef924196", "source:5b4aa5f7fe2e6e71", "source:eec4b332760d035c", "source:70e9852467805697", "source:edeaaf34f7b6e031", "source:6b4baa374a301f3f", "source:2352eddfc77e0e09", "source:550e0e868546c81d", "source:dbf6b09f9f28e88e", "source:087a06e9a8c916ff", "source:a85c917f609366cd", "source:3dadfd2984973a72", "source:dee834cd9d723ba0", "source:200d4c75079585e3", "source:ca226bc6b278feb5", "source:be354af24dbbd68d", "source:0d79c1238a6a034f", "source:b2e725fbdc90055f", "source:d9ba93d7d28aa184", "source:5bd403911635e202", "source:4ca11256388d1a8d", "source:9cc72b8979a68fbf", "source:4cca760ad896275f", "source:9df7c76c904df027", "source:b79d07da7a822ca3", "source:763ebe04a4128d64", "source:971ee0118b30df1f", "source:212f68743d067be8", "source:eaf7e5be9b580ab0", "source:e594300cf422b6c4", "source:4bb8df60584cd40c", "source:334bf3fc010d267d", "source:266035c148714b27", "source:f15dd9c547df2248", "source:f88caf1cf3185a32", "source:38a7b130590cc9ea", "source:094d20e63f1a853a", "source:622c3f987c5fa8a6", "source:8c8b0982dd8a3c27", "source:0ce736e123fda6af", "source:90482ff043eba2d5", "source:5458f578157c252d", "source:068d19af400e724a", "source:5bef9f3decf366cd", "source:c17039ea5bfc6978", "source:e061b07066810bda", "source:18c82df1afb618d4", "source:f03dc204980cacf4", "source:88361df3df9be353", "source:8e20308de29a3774", "source:c93834138a250643", "source:d76c3c6637144834", "source:5c85d3cc69b6e36a", "source:7e5316e584b06093", "source:6e2094932119d85d", "source:e23aaec92a4b4e2d", "source:507800c97d74273b", "source:b6c018ed20c1ca71", "source:fe7bfbfe200b28ae", "source:5509dd945907c820", "source:ab7ef8d119417221", "source:ec13d40325b9ea48", "source:e24b4df2c2370ef1", "source:525b50e1ea349c04", "source:4283b4ddcaab7d78", "source:87520893eed098c7", "source:c5bc80da01e9c6bb", "source:17fa4adf229e1faa", "source:95f7f8c9cc266a49", "source:7ffb30a0f383b90b", "source:71a5a9c3e50c334b", "source:1dc55132b6d0cf15", "source:4d86162c7134a3a6", "source:4b96c88a4e87a36b", "source:abe916ad7f02dbc2", "source:c8622e1f4b8a821c", "source:e0f4bc0474f46c2a", "source:5470a7d70ae77662", "source:96905249bbb9c625", "source:b43b6bcc2a89866e", "source:e33936b92879139b", "source:f24dd4d04d7b1672", "source:4db276ae9c683379", "source:a642343c1b45794d", "source:2442e1d7c03ad67d", "source:9069ea57202e6f99", "source:49e291f857a50731", "source:4146f2b857b5d1df", "source:ecba66a3a0322180", "source:0f9ef1cb54aa4420", "source:9fc3a6be03b792ec", "source:5d920145efb15800", "source:df7b1d917ef9fbda", "source:21de0be1d68303b8", "source:91190653438848fa", "source:3a47a48d954f2b7d", "source:b95453e2fd169c36", "source:5ed1126a303f099c", "source:9227f5cebf64546c", "source:f470b610f9e74623", "source:aaca6abf49f01bf9", "source:29533fadba805daf", "source:a3c38424f881b3e7", "source:b70676fe19ad3703", "source:1ed3639bce6a953c", "source:b5c89af94225ae7b", "source:8f86b36713576d88", "source:0e2fa4694409453e", "source:d7cdbee6abe18f17", "source:fa9f0b9cf52dcac9", "source:4e75c99710f36786", "source:4cb6645eb74c5f7f", "model-log:poetry_model_remove_failed", "model-log:music_log_callback_failed", "model-log:worker_failed", "dynamic:5007cf75062b5a5a", "dynamic:e570462f97d79246", "dynamic:c87312c290ffceba", "dynamic:0ae479049959666d", "dynamic:f009f0b9ff5f12c8", "dynamic:a79d3fd8d017c9b2", "dynamic:6445c7a2ff58c7c2", "dynamic:1474657bd3776c77", "dynamic:b83ebdcd163b2c72", "dynamic:a6c503693e27208d", "dynamic:a79aa9a0d9c6cef6", "dynamic:6267ebfd104f30d1", "dynamic:c11374a0c3c734da", "dynamic:c0c303862cd9f63b", "dynamic:c2009e15e0d36c11", "dynamic:396afc82f6579810", "dynamic:eb719da4a2189619", "dynamic:a0ee1017efcd6a88", "dynamic:8f7a13f7a4ace352", "dynamic:7b2a7696a873f7ef", "dynamic:45c33812dd06bc02", "dynamic:974189d6cc91de89", "dynamic:7652e342744cade0", "dynamic:93e19dd143601005", "dynamic:7112e9c6b17c9a05", "dynamic:395854ae62b76139", "dynamic:273295483291062e", "dynamic:2c69f7b8b1659e49", "dynamic:b8b35303ba1d1a11", "dynamic:9f8b82a48252eea2", "dynamic:f84d1e81f8f96877", "dynamic:e231f0eb460b8762", "dynamic:5d4625db7fe5ac25", "dynamic:ec3b4fb5e9360421", "dynamic:abc003692abbc65a", "dynamic:c4584a7896bcc07c", "dynamic:f8b723bfdcaeb365", "dynamic:a45e9264f4928e93", "dynamic:a85b832557771e16", "dynamic:ebc4a4bf6b7db0f3", "dynamic:585e0b107b94b3bb", "dynamic:f2b5a30032bf331f", "dynamic:320f73743f61ac04", "dynamic:2a06deb5c47ba83a", "dynamic:5057d2cc60338aec", "dynamic:fe7d0b6745f8aec6", "dynamic:1d1722e11125ac36", "dynamic:9104d2c79d3d4590", "dynamic:158ca7e159e3f876", "dynamic:823fb383405d5b32", "dynamic:d0f7bcda6ec7622e", "dynamic:2c700e8dbe9ea2fd", "dynamic:04520dc23d39a26b", "dynamic:1cbee3a99c78106d", "dynamic:e4d0bf17881d05ad", "dynamic:f5116718244a946a", "dynamic:a841e32debe2cd77", "dynamic:a2e2ed692fef8ced", "dynamic:59de476e660736f3", "dynamic:55a906655e89a123"] -->

- Не удалось завершить действие при работе с музыкой и текстами. Подробности показаны в окне программы.

### 0078 · voice.aa4ab8b0981c5b9a

<!-- LOPATA.Events: ["source:dc8755e206b0b700", "source:001e726545e92529", "source:6e3398a20884f9cf", "source:3bfb1e059c193ba3", "source:41bc33041a8e82b6", "source:8a618d2539c9b8bc", "source:30293717ca24f159", "source:a2b8d414b4ed6c95", "source:19b1ea981e1ce09c", "source:15de869b5079a75c", "dynamic:048576c610d15875", "dynamic:608134ddb1d64e18"] -->

- Не удалось завершить действие при озвучивании текста. Подробности показаны в окне программы.

### 0079 · voice.fee6134961882b98

<!-- LOPATA.Events: ["source:03bf35372e822fe2", "source:b8911e76189875e0", "source:a831ef8cf633f666", "source:2e63e757862686af", "source:3357d45581c4c3a9", "source:b67591f0ea872ec6", "source:40777c37b7acf546", "source:2dbe4ad25c8948ad", "source:8e39a46a29aeba02", "source:ce7061a6467a4ed8", "source:55fc5a3f61a5e798", "source:2528545e09e82c66", "source:3c85c0e5908645a5", "source:79b4a9dfc4108de0", "source:c5dceb8af96d52b4", "source:74776822b43593a7", "source:4d5141b7ab2c2308", "source:153f77daae3ac02d", "source:81f650ba4985302d", "source:bdf039c5f8c42e1b", "source:58069db2a3e8ae40", "source:991d8249d4239368", "source:d77bbfc569d379da", "source:70e6bd788d7e1d0f", "source:355a866b06141916", "source:3ce0e511fc360677", "source:4699f9023ed3f182", "dynamic:9ecbc2d7f3f19834", "dynamic:ceb4684fd62352c9", "dynamic:cbd92e1bfacd973e", "dynamic:4613abef5507d275", "dynamic:99aa50ee33209ddc", "dynamic:2af7216460ae689d", "dynamic:b1c354b48d7304e5", "dynamic:268f8b717f6c13ff", "dynamic:98b1df36d736516b", "dynamic:8a4ac45e635be1c5", "dynamic:5aeefe4ba8989cc1", "dynamic:69b719ba6fafe725", "dynamic:2d92878e92e6f296", "dynamic:08766667260f39d7", "dynamic:6114db7d6d418baf", "dynamic:0e4978b56624c251"] -->

- Не удалось завершить действие при чтении документа. Подробности показаны в окне программы.

### 0080 · voice.15cdef8c575a125b

<!-- LOPATA.Events: ["source:f774869d9c429455", "source:e04303d575a7bdaa", "source:2d33590db7612e21", "source:8ae1f1a4c0e055be", "source:f3a943cfd1d82faa", "source:8dcd9cfa31216b68", "source:4287a66d22c1d2e3", "source:28538bfff1b5fb4b", "source:5c27ae05837ec75e", "source:128785db405de064", "source:7e607d864497eeb8", "source:712c66417fe6f9a2", "source:3915f9e48188a07e", "source:7cf31c3221db4b79", "source:1790d5c2349896a8", "source:61f3d8de4d3cf443", "source:7db432aab308c92b", "source:65e2dc4fc1adfb20", "source:2752aa1f95df3508", "source:8a8e763d994f419c", "source:c49dd83ca1551adf", "source:7f57e86b9bd6855e", "source:fe97721dda242a81", "source:86ac8694a514fefa", "source:921719e569e67b55", "source:ad02eb1783cf50ba", "source:bbf7e5b802b656ec", "source:9ad814a6e4d43ea5", "source:b83e995c6a2073aa", "source:a02e6d596e967dca", "source:b10a129bfb5d3dcb", "source:9185a672898814ea", "source:16db1532e2a5e0ab", "source:983eea698b3d47db", "source:a2e084f0a883982c", "source:3534b7dee984ba52", "source:fa7d95d4a1afdc9a", "source:c352e1855e8cfb81", "source:1bc993a8a5430333", "source:052389e4dc25aa01", "source:74005628f35a827d", "source:579521401ccc81ea", "source:a51a0b889d8c91af", "source:b527f9edde26593e", "source:a30ea5d343cb9146", "source:0d45e874fcd5113f", "source:947f721015edb867", "source:b21b4016383545b3", "source:a758ba573b373a03", "source:51b86e85dad6d118", "source:0684c0088be44369", "source:9cf0e32996c1dd0e", "source:296ce3748dfaed65", "source:4cb3b60e71a7f48c", "source:6c21f165e7ecbdbe", "source:130cd295b3e9a51b", "source:65d32784561ef51c", "source:065ce53c56f667e6", "source:3181601e6524d242", "source:3fe853f14c749f51", "source:7b9768e3b4e0ca81", "source:5cc384b41548949b", "source:0cae2d5efb70c4bd", "source:0e40f7170dbd5b0d", "source:b11bdb9959da3f83", "source:7b24d54048a60c8a", "source:2fd54e756f9032db", "source:dc2dd631525ace86", "source:e512c676da15c83c", "source:ebf4e31c2f1aa357", "source:c50f70a942a86c97", "source:fb380c42589381bc", "source:d9367f30d15a778c", "source:2eaa082bdfef850e", "source:b44a2f5a12674b56", "source:080dcad05ec57dfd", "source:9f1e72d5ec9703b1", "dynamic:3ea284ff432830bc", "dynamic:5e205f85701237d7", "dynamic:6457be541608300c", "dynamic:820811b84b884814", "dynamic:557c7b079cf7d952", "dynamic:0a56f883be5aa125", "dynamic:b82649ef8b9e1952", "dynamic:f6e08f3852da76ad", "dynamic:1c7f334f5c418818", "dynamic:ce4b3d0fe645c609", "dynamic:cdc37a655b19619b", "dynamic:3ef498bff366d015", "dynamic:d867ed4692490f60", "dynamic:3b211bfe587014bf", "dynamic:5a3d426856e48cd3", "dynamic:c8c30d60696015a7", "dynamic:b29312233032bc20", "dynamic:5d84644e9f00fe52", "dynamic:177c6d653fbf5642", "dynamic:3b9443bcde0cfadd", "dynamic:69ae6a4e9b4409ca", "dynamic:4b34629e188a917e", "dynamic:945aa35a453fd5fc", "dynamic:269439edf04388eb", "dynamic:41988ed2b0c67bbf", "dynamic:c9d78add544e7cde", "dynamic:e57434c26b4dbafe", "dynamic:385b6fba80eece5e", "dynamic:4c75645ab3fa7c31", "dynamic:f3c6d5f0721d250b", "dynamic:f6571c83b972ffd2", "dynamic:2ec82139d25e107a", "dynamic:222f4d32d654b98e", "dynamic:eb3626fa3349f215", "dynamic:6cdcbe68d5f2474d", "dynamic:35a50cd34730ec55", "dynamic:e23bc07dcd5fecc0", "dynamic:51507e84cf5ae4cc", "dynamic:edf07cd14ef3e0fe"] -->

- Не удалось завершить действие при обработке книги. Подробности показаны в окне программы.

### 0081 · voice.f8b9b51caa183530

<!-- LOPATA.Events: ["localization:Status.ExternalComponentSearchFailed", "source:d9499fcd5ba1aa2a", "source:dbac6d2fbc75dd76", "source:cd74a7003069086a", "source:5b4f9f484595d7af", "source:facd2c326034f1a9", "source:626898e9cb04f743", "source:6c002edb77159e8e", "source:5b2cf916e257537c", "source:719f20ea8ff3ccd5", "source:2991fe42a20cb936", "source:2a8787f81a488819", "source:8bab3aafb732af60", "source:0ed01670222b44a5", "source:b66da33e79bbd5c5", "source:15f8300f6f7b3846", "source:6c2cf829887b5163", "source:f9d66e7cbbf83a77", "source:2fd763e15ed27816", "source:05b95df4b60922a4", "source:6412b7634aa3827e", "source:0b5d0f9c8d3a116a", "source:c29f392638efa57b", "source:66a32c796feb3c00", "source:2f337dca697e688e", "source:206d774307589651", "source:afa55343e914a7d1", "source:f97109d123dae995", "source:fa59ccd5a0a55618", "source:5b249f5ad6bbc590", "source:e896fdebcdd57387", "source:470136f4b7430261", "source:256c4a63ae4f7a5b", "source:7fa91af9e3b2d60e", "source:8681974b01f29405", "source:2249ce0541a16437", "source:62b0e765ce82c7ca", "source:be60f855c2b43319", "source:1700b3b8c7e32428", "source:450602d623b3902a", "source:5b56ea0d8f525ab5", "source:54face8f9594a42b", "source:845901101ee05d0b", "source:d6e10c6d47a1b890", "source:82bbcacf95f9e6f1", "source:a25991912908d9f5", "source:651996c4bbe5c6ea", "source:39c965e5738cf8a2", "source:23ffc2cd5e3c05d4", "source:b3ed91e78a586946", "source:db6f0032506b7d22", "source:746d55c5fbc26e57", "source:b575c8ff80d2aa36", "source:7f49187591b8fe42", "source:0b5b37b5e9372495", "source:e781851228393ca5", "source:9db98eff415c303e", "source:c8234ca04683ffc7", "source:2318f3b70ac14ae5", "source:10785cb71c796130", "source:2f804cc9cd124fca", "source:613b9c93963ae0a6", "source:7a518e9fb4e2592e", "source:1705fbe032a07694", "source:c13e1c2d94bf49b7", "source:094b76401c8afda1", "source:076e13465d2fd25a", "source:dd0ddf24dd05f371", "source:a289397f7e5a1e93", "source:9bf90b654ff752af", "source:3c45a35f24fb211b", "source:951eb8f91344ebc3", "source:f96bc6f71c244a01", "source:be0452615b7c4945", "source:4d9a901f07358bdb", "source:11d18799cf85ef92", "source:411643063ea019f1", "source:3613ea29744fe895", "source:067c52412d229d78", "source:83789958469d1607", "source:ed11b0ce67e2984c", "source:e07e1a34b60383fb", "source:27363e1e82bd74b5", "source:53f9af9988ab2650", "source:e0989887f2e313a0", "source:200e487d34d55027", "source:28a3b8a168c3912b", "source:5a8bc9f5521ce4a2", "source:af1496736bda1a92", "source:fedff1a7679554fe", "source:9f53790e142b786c", "source:ccf2c065d6efecee", "source:3210d70c1fc46203", "source:3127b7506b9525d7", "source:a15a993fa71cb28b", "source:bc12d08a5a6de52b", "source:611afed1b47e7035", "source:feeb59f3a64e2a41", "source:5da40948d2eae6eb", "source:78e686d6d76b40d5", "source:3228f6f22921d2c3", "source:6e01ee52b7a32a02", "source:32ca7a22c4cf741c", "source:7531682932a5f23b", "source:f783b2099a0ab1d9", "source:d898c59feb2f0063", "source:47992cf5721dfec1", "source:5c7095105322d2a8", "source:3a180901551d1fda", "source:ab0fa18d6d989b40", "source:6fe61a1c8c93ff0a", "source:a3925f8e978c6ded", "source:e34b55d43146545e", "source:7b2e240f796ce232", "source:97c53069d555befb", "source:fbfb421b513fcc58", "source:3620a8b1cb2233e3", "source:17af390f08f82b06", "source:9b6a0704848bddbf", "source:61afb21b438c4af4", "source:cf7b005d0931a49b", "source:7186589e9551014f", "source:7af7732db26628f9", "source:ea71214b829ad28a", "source:277b091efb322bb8", "source:97b862b42a7d66be", "source:ec971bf56d01818a", "source:e49c7f0b05729985", "source:670c8c8b23c0b29c", "source:6d4cef733b58fd75", "source:ceaac1825cdae2c6", "source:e96921b8c13e352f", "source:b25422e444c3b24f", "source:a1e66e108b2f80ff", "source:119c7e485b3dac2f", "source:f7ff895c0936eb70", "source:7e3814e50f03fb2b", "source:06f5bcaccf431ece", "source:f1fc017cf47ba928", "source:ae4e8f493ffe4195", "source:88f21bf68751c9de", "source:4bc64c30ea789955", "source:726d14d8279f2994", "source:82cc864fe29ddf49", "source:049a5d423c58574d", "source:59abc4ac42ba4c23", "source:cac4eed8aee58d89", "source:7c66e1c4dde261c8", "source:aa70bfd27593493f", "source:b7fa35c28b298c30", "source:c2079cc78457fb6f", "source:e411dc25ca57d9a0", "source:f9e364c64aa23abb", "source:a21cde06d0e860b9", "source:4af6bac3c75561cb", "source:ce35f305f0ba6787", "source:58a56c75e42f3931", "source:f9b79d1b38ed713f", "source:81851c2f7759bd2f", "source:bbd1ff22613f8207", "source:e80d188b44a07b44", "source:40c522b3a4413bfc", "source:8d2b18ae1718070c", "source:c5489bd7fec44338", "source:62a35d1d105aac84", "source:9652342cec57710b", "source:53f3ab78c4cbc041", "source:ebd4ed02455d7530", "source:3a0a9dc73bc55325", "model-log:engine_set_failed", "model-log:startup_check_failed", "model-log:engine_set_ui_failed", "model-log:graceful_stop_failed", "dynamic:8e49e811416c78ba", "dynamic:0b90a7f623a2a647", "dynamic:d9b39b19cf3ccd5e", "dynamic:3bc5a20e6138ab42", "dynamic:c713e85e8ba2029f", "dynamic:dfef9d33f5fbc0fa", "dynamic:bc20217c6f9f8341", "dynamic:7e7ff68be077779e", "dynamic:74200037c30b1bde", "dynamic:da35383b447d115c"] -->

- Не удалось завершить действие при работе с моделью. Подробности показаны в окне программы.

### 0082 · voice.90a51ca9c25ff46b

<!-- LOPATA.Events: ["source:88c7f8ec1dd2c43b", "source:f129a020046a0705", "source:5d9b9fded834844c", "source:6e5ccaf146f8a72f", "source:8f63724cc8565791", "source:05947deecc7b8265", "source:87903ec80266c268", "source:c4f45b108001bea4", "source:477725109d47df91", "source:b00edf95b368fcc7", "source:2dff3fdb55c1aaa6", "source:67d49321dd3bb5ed", "source:e28bc79d1a3e0eb9", "source:cf41680ad5d5749e", "source:e70c1041396cb026", "source:947cc5a2c010c7f3", "source:bf3216982d09ef50", "dynamic:8ee9a2764370ef52", "dynamic:63608656e074cec0", "dynamic:5c017e139e8a0f91", "dynamic:341ba5b4aa47931a", "dynamic:b095e84e2991cc16", "dynamic:5105c9eb7b3b1560", "dynamic:0ce285e2915c3c31", "dynamic:60b2378f9b04050e", "dynamic:fed5003354b06ce7", "dynamic:60658f7b203ed98d", "dynamic:105f3b471c4ce452", "dynamic:c7755786e5e609f4", "dynamic:16e3ac4c19ae3194", "dynamic:89d333a109c08df2", "dynamic:4aaf4a21139c254f", "dynamic:7c7f7110ea483726", "dynamic:b877ce2656f6ab30", "dynamic:43bb725d19191c13"] -->

- Не удалось завершить действие при работе с базой знаний. Подробности показаны в окне программы.

### 0083 · voice.25f1c71e95fb2f25

<!-- LOPATA.Events: ["localization:Status.ExecutorExportFailed", "localization:Status.ExecutorResolveFailed", "localization:Status.ExecutorSnapshotFailed", "localization:Status.ExecutorUnresolvedCapabilities", "localization:Status.PreviousWorkDeleteFailed", "localization:Status.PreviousWorkRenameFailed", "localization:Status.PreviousWorkRestoreFailed", "localization:Status.SessionArchiveCreateFailed", "localization:Status.SessionCheckpointFailed", "source:eaa9b81379d3c26f", "source:d8611006400bdc54", "source:69014722a80c0538", "source:76a98b23dc608e06", "source:e0173d7b8153af05", "source:1a37a27e40e40aa8", "source:1c13c25157d3f8ea", "source:4a7925f1d996c080", "source:3b52dea52f735eec", "source:9315123adfc67373", "source:2c8b15617a23d0f5", "source:a971aae9c74d45da", "source:9c8d993eae1dc679", "source:97e4e2f7a75f4c6b", "source:814348996989a058", "source:135aff730254c7f2", "source:20f9e9a063b6d055", "source:fbdb1e16cd7d0cf1", "source:0128097ac7067425", "source:022e72eb80060fb4", "source:fc00b7fa94c3c658", "source:bd3091d127131a2c", "source:4f4436cef615c7c5", "source:f55586b0f3ff605e", "source:cea720bdbf60a126", "source:97213a152ef99b12", "source:40d15da592d194a6", "source:9fe4f7fe835279a6", "source:f429222c57cc6c5a", "source:355c523770814a37", "source:fd7a24075ac18187", "source:afb3be4ca0ac572f", "source:3723171e0d80ca7d", "source:72afdb7d0ec96d5e", "source:920a9512d34c2a9e", "source:7a696b33fd0501a7", "source:ded618ba2fff4426", "source:a740df7558283400", "source:e82fed8ff6a375a6", "source:ddd05c737d65c90e", "source:6d03ae62ee1812c0", "source:8b1d61b001beaf68", "source:ccb634665ac7959e", "source:a701eb3a70d66fc4", "source:c5d1ba56917ebcf3", "source:4334fe9291468084", "source:a59f222a90a352d7", "source:1d566e20ef21aab9", "source:74ef0a8d43128465", "source:433daf785111403c", "source:ead5431143eb629b", "source:f512dcc3a143cd94", "source:344a21af81b6c3e4", "source:14406680ccf834e0", "source:ff50f4cf8a785c20", "source:fcf12d08ae025f8b", "source:24e97fe010ccc59c", "source:87019e01383559ed", "source:33d783101f4cd284", "source:893dc04800b8aa2e", "source:b198f06d71296f60", "source:1b9bf71f54bab393", "source:5e40b0806a267dff", "source:0a4d7472b3fca6db", "source:476f983bf4261a82", "source:7b472d45addbf65e", "source:a15c3e94ee060d4d", "source:79794c8e766715ca", "source:9758c536071ecfcb", "source:201bb96d70b8391e", "source:ff0b472aad3d3acb", "source:5381ffb1f2561b25", "source:7ae8b6f8ca7fad23", "source:040b5d1a2bd8c398", "source:d3355e237ee9062e", "source:609b3a0ad5f4487d", "source:96a8cbdab853372e", "source:585de844d9e17c7e", "source:a31a99d44dde2079", "source:ebc86b468a603015", "source:bb63f7ab11d0e399", "source:76c6b033a82820e0", "source:e853a823f6598b72", "source:fcb6a4df217eaaac", "source:97bd2a7ac59c3210", "source:dc37dc5fb839e8a5", "source:71ee8f15bc07f343", "source:4823bb6f2829767b", "source:a8503a77cc5852b8", "source:ec431476d81e88ed", "source:64eb31232b716102", "source:97bc51e7f48a09e5", "source:9d40650ec51feffa", "source:afc71207684cdb47", "source:647bb3e8a57fd775", "source:3028887a6b43268b", "source:224da4155f5400b5", "source:48fc93abb0a0e6bf", "source:eac098e61d03b321", "source:0ab104d842e686d8", "source:41d31da6dd5a4519", "source:04427c3bf697491b", "source:815b48ed8d5c868a", "source:5ac1dcb8d97cdb3a", "source:b1a230605ce4db48", "source:d636bc50b80ed9b4", "source:276d27605680322b", "source:98572cd9146438d9", "source:8b76c73e659b211d", "source:950754118b8993f0", "source:1d473a892f92f06e", "source:fb890630e4fcf008", "source:5aa1c7153377b782", "source:bad8bf39d9ede3f8", "source:7cef49b232cf04a4", "source:f0eb7c70d8d09314", "source:88d7a56e3cd04ced", "source:6510e8f12a0282e4", "source:9f7ae6dbea5f06b1", "source:4bbde0f18deed97b", "source:4d241be4bcb5b3fa", "source:fc1f90a4ef216382", "source:dd140f258373ca26", "source:0b3747cadf569dc5", "source:43a2269fcba03492", "source:6776a1a0bd0c63b2", "source:e95c79cabd374e92", "source:cf65f8d316ebcef4", "source:338e6f8bc386d5e5", "source:22e91b9a4b063cd9", "source:e064662eaa70502d", "source:d42f0fc2ac517061", "source:64471cb46ac527ef", "source:b7cb5b12e9074a10", "source:08dc3ecb73d65600", "dynamic:4b3d57906cbc7031", "dynamic:e9f12aaf7f54c8b3", "dynamic:774567d3f1229d0a", "dynamic:0e05f12de9975059", "dynamic:fa65efc93ded17b9", "dynamic:d688235aeb9abbf3", "dynamic:6730ce778c70c6ad", "dynamic:447c1dee2c176349", "dynamic:f8a461909cf87b80", "dynamic:d8fbd6a7aa1ae4d1", "dynamic:77c1b57ec5d913a7", "dynamic:d6b3db3338847d20", "dynamic:4675de36f9d0f2ca", "dynamic:eee74c304b093933", "dynamic:d70c7aaf35be10e9", "dynamic:2a07015b5492f452", "dynamic:738eed1dd09a860d", "dynamic:e0fb2e55425e1d52", "dynamic:1eec5aa3933da1f3", "dynamic:d72c42c7593730bf", "dynamic:9d2a66eded8fa6d3", "dynamic:ddc7c0e041544eed", "dynamic:609651f63b933639", "dynamic:54eeefd784f10774", "dynamic:622edaf3e0438985", "dynamic:f355668df4e91f89", "dynamic:a9243286999a4eb8", "dynamic:573150918113f81d", "dynamic:44efcc4c2b7d511b", "dynamic:f785eccd3fd0dd6d", "dynamic:c66c4d7a5507e4cd", "dynamic:9ba574f0e54d110f", "dynamic:98788984a20cd6fe", "dynamic:9500012ba590b5b6", "dynamic:c05a9bc5d3588a8d", "dynamic:84838a39752eb9d6", "dynamic:d37276ddf4d60bf2", "dynamic:b6be6df15420f3db", "dynamic:a22b128f17fd21ae", "dynamic:cd703ea473ec7b3a", "dynamic:5bc89bb029decc2d", "dynamic:33529ac78326e508", "dynamic:4f70c303db57ab5d", "dynamic:f710d8e89144f273", "dynamic:895017db91a39382"] -->

- Не удалось завершить действие при выполнении операции. Подробности показаны в окне программы.

### 0084 · voice.61f4ec662bab9610

<!-- LOPATA.Events: ["localization:SpeechAssets.LineError", "localization:SpeechAssets.QueueFinishedWithErrors", "dynamic:fb99e92f1ae35c78", "dynamic:ee9278c404d50d2c", "dynamic:e8b52f7cb3f92923", "dynamic:25cd72aa2f40c997", "dynamic:10935187f43bd23b", "dynamic:a637a95bc7145d8b", "dynamic:5c253bc92b41a882", "dynamic:d531b482e1c05cec", "dynamic:8d369b15bc97ab2e", "dynamic:c7a81d7c16c1a091", "dynamic:969a601e52d1f762"] -->

- Не удалось завершить действие при создании речевых ассетов. Подробности показаны в окне программы.

### 0085 · voice.6142c90979588592

<!-- LOPATA.Events: ["source:b1fa65c47f999ef8", "source:cf7930b8b33fd0b9", "dynamic:109a66eda05f2954", "dynamic:a2f403b50be2abad", "dynamic:d0ca81da15df5654", "dynamic:15ae8601b2d6be57", "dynamic:caa2e3dc86955ce6", "dynamic:99240754b4055a49", "dynamic:c1def89747c7155c", "dynamic:58618f442e0ad999", "dynamic:8741282cc7dbfe49", "dynamic:9af81d82d965790b", "dynamic:ae40bfffabc71a90", "dynamic:89bd2f4c04826607", "dynamic:d7dfd07a97f83e11", "dynamic:7b17ed9564ef336f", "dynamic:f7c8ca929e5cc988"] -->

- Не удалось завершить действие в редакторе документов. Подробности показаны в окне программы.

### 0086 · voice.c890157a527742fc

<!-- LOPATA.Events: ["localization:DebugChat.LogError", "source:b82eb61e61a749c7", "source:e6c67cd680356825", "source:a96e39f3893ad7a2", "source:7db96b35555a6267", "source:8147675cc999cf06", "source:c2a95f7ded173c0b", "source:af0c8fea9f8cdefc", "source:802b696f518aa28d", "source:77b5559be110fdc3", "source:ea7a7b566046cd72", "source:b7983daa3e99abc9", "source:a6f89eeaa4689baf", "source:7ca8e31cb389fad0", "source:38f63241e33c4eb7", "source:b9cd326a5b74fdda", "source:995047d6878193af", "source:ee409a0addbf2fde", "source:e6db11dc03424ae4", "source:3a45928ef55f923d", "source:8249110b4a26f5c2", "source:b393d1d526887215", "source:4e7060afce77c655", "source:0c7e582aa2c85739", "source:a12221985d559730", "source:ad389204f07547d2", "source:59f4bc950cb78e8d", "source:95b7d606a66a8e75", "source:b3b09228d23c1c3b", "source:f4dab56babb2cf2e", "source:359c02bec0293d64", "source:163c4163e466bbe5", "source:2426ac9e8dd2f550", "source:0ff29e5dd6d1cfd7", "source:930e88fbd6ad0e57", "source:dceeb9eb6108a1fa", "source:7c4f70b09ca48cd0", "source:3f2830b21e30d56f", "model-log:let_it_out_memory_yield_failed", "model-log:let_it_out_memory_close_failed", "dynamic:8fba57f80cad0407", "dynamic:76cecfb54b7ac6b4", "dynamic:13e9936e58f2f426", "dynamic:ead76d7fe2654f56", "dynamic:f27b5a23423ddaeb", "dynamic:a89747e31de1d762", "dynamic:464f2a4734dcfdb3", "dynamic:d13252153131902d", "dynamic:414c63c046669840", "dynamic:ada55ead8bced6be", "dynamic:9b6e004310a1f931", "dynamic:8672d6377659981a", "dynamic:4ddd75a4ec3f423d", "dynamic:16d4c14dbd906a6d"] -->

- Не удалось завершить действие при работе чата. Подробности показаны в окне программы.

### 0087 · voice.49428661f4759979

<!-- LOPATA.Events: ["source:e31bc32522283b3a", "source:dda871422bd44799", "source:1296cc57b0a76e3b", "source:2866ef740102b2e7", "source:ff24e8fb3cd86c37", "source:c0a73fe8a305534e", "source:7b27ce4dbbccb9e5", "source:7b6f404ced1aaa2e", "source:829ec6701bb8d542", "source:fc44e6eda358b074", "source:5b8103e2f9198014", "source:c5930026698c90b0", "source:f2bda397564bc95e", "source:1a871442a20d297a", "source:dc3112b6ced37685", "source:31d07b8c0b440872", "source:688b774addfb9640", "source:f4dd87f4eadb93ed", "source:2a9946d40c745422", "source:ccba967550fc2144", "source:99123d67cfb736c9", "source:14948aecc9005491", "source:27dad91418e92614", "source:d0d72f7e82544e65", "source:7c2702e1b26dcc35", "source:229dc769a31cf1d3", "source:099d1fe1f7553668", "source:d726f93695f9f221", "source:48822662ee4a84a6", "source:419c60ab56b7958f", "source:5406cd06708111a8", "source:4cf82e0c4b43bafe", "source:434bd4ed08e36b33", "source:58de91df96c09f3a", "source:62d54cd5e4eb854e", "source:a270c5df1c8a5431", "source:b6dd513daabe5b9a", "source:61642fb4cef74446", "source:b893cbe48190f7ee", "source:d443ee9b090a1c70", "source:7b6eeb8db9982c71", "source:dc45077e79132405", "source:8c02cefbca021a42", "source:a89653e7a2a0d911", "source:df8c52b648ba260b", "source:a687f69010fae96c", "source:9259470f10e8f75f", "source:b00c66727f585ed3", "source:8726eca6753fe16a", "source:af017a89f86b0742", "source:d5d7ccce31c2066b", "source:32b6e7e83497042a", "source:f2b1796348386f89", "source:68ac049851a7e50c", "source:4e0da35e80ab7d4b", "source:052fbbbfe0242415", "source:fd149dd594f4bd47", "source:63ed0469c2598db5", "source:3cc680d5a09ea61c", "source:14f7fbffee04cac1", "source:0b8b4934ead5e05d", "source:23b0581179f053ab", "source:ba57c028f9d6ba3e", "source:499f02f12ddb5202", "source:4a1451ea812f151b", "source:8fab2e1dd89cb149", "source:f78ceb857e1ed937", "source:f4914b321a573aa7", "source:7de08f9cb0991944", "source:286c968371a24cbe", "source:14618c69a4f9b4b7", "source:306bb5d88dbe280d", "source:f4ff507f4c74a60b", "source:0540c557514483da", "source:799d6a19dc0b0c22", "source:869753121bf2ef5d", "source:f3becd834fbb2fc6", "source:8c25e89da1c5639a", "source:604a923754feb7dc", "source:697dda785dedbc82", "source:d6fad84bad0eefb0", "source:3afa69631a362fbb", "source:3c55579573f9cc74", "source:7210ee120ff7bd17", "source:901f2d688f3913e4", "source:62adf9f791aa724d", "source:0213eca30fe5c478", "source:d562710e08778546", "source:36b523d4f244c049", "source:a927674cb32be811", "source:f13db665dcec0893", "source:fa61f76debd55f4b", "source:76ed8141d429b0c7", "source:3dc5f963faa368f1", "source:ac71708a341e9bba", "source:4e074134b76fc465", "source:ed3a65a60b2a67e3", "source:f2c23a02ac30ee4e", "source:528b250aaa6b2b11", "source:874fd9dcf811ed49", "source:e2e8f1291e2d5952", "source:50e3e228229c08dc", "dynamic:bd44f26c4793cb57", "dynamic:f6dbb77bf752fea6", "dynamic:cdc667f7ef55ce87", "dynamic:896c5be74652c8ad", "dynamic:473f24df4b22d4cf", "dynamic:be3229faf514b3ca", "dynamic:4512a7bf403fefca", "dynamic:4be951970f3c8bcb", "dynamic:8be8ab28f3638533", "dynamic:9f2d830e470e7847", "dynamic:72074f00d042539d", "dynamic:c0bdcd5b61dac618", "dynamic:7fddc368fdeb4084", "dynamic:9f9dabf3c99ceaaf", "dynamic:6fdb9b55006c67cc", "dynamic:141b72af4c3994be", "dynamic:e18d4e18a2454804", "dynamic:b17d73d60bff29f4", "dynamic:b8dcc13839637f11", "dynamic:c39714e4d5c0ad84", "dynamic:fbe59245ed5515e9", "dynamic:57476039cf817244", "dynamic:d0499a4da25287fa", "dynamic:e037bd943a03cf53"] -->

- Не удалось завершить действие при работе над произведением. Подробности показаны в окне программы.

### 0088 · voice.66a9bf80362363f2

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Activity.Error", "localization:ImageAnalysis.Workspace.FileError", "localization:ImageAnalysis.Workspace.Status.Error", "localization:ImageUtility.Error.Directory", "localization:ImageUtility.Error.Processing", "localization:Status.ImageAnalysisExportFailed", "localization:Status.ImageAnalysisFailed", "localization:Status.ImageAnalysisHeavyPreparationFailed", "source:978da1e7e038ad17", "source:155b3814ff6ee29a", "source:d648563e6c5dfe9d", "source:db2ea8aa8738dec2", "source:ab9bbb7f1f248e84", "source:b2a86cd136946b72", "source:b4599d553f17d5a2", "source:5b1bc86179b4800e", "source:0e389cb7e11dbab2", "source:aaa7cc12fce129e3", "source:958fd5505c07c4d5", "source:f4147c98b1610682", "source:a053d19161e147a9", "source:8468545ea4f52f22", "source:1ab1ff32f7260194", "source:669f858897498822", "source:c9eaeb2468a435f1", "source:65301e19f4164d85", "source:c56608a6164e4691", "source:820b1f7fe77cc454", "source:8e363860ad820433", "source:23f6acc36da0a241", "source:4a2803509204e73a", "source:d0d6359e2eee5c7a", "source:5e8babe1ffa5811e", "source:e1e08d70e28e7192", "source:8fd9940d0ea89b34", "source:4e97f20c4e9e1464", "source:60199eefc353f3ce", "source:ad6e52692d01e734", "source:25e1d78b660a2ce6", "source:20c9be3a9faa4253", "source:13a8f5b01ca434a7", "source:a651093c5207301d", "source:d2a660c2e120c4e8", "source:5c5ce683368be958", "source:899f1c77ef3e8176", "source:b7b08970a71e222f", "source:aec143b74a5818d8", "source:1d01ec3436203bb3", "source:1cd9e3179fd56927", "source:b7866551f92971eb", "source:32e4e2399e560c78", "source:8f8375f256c2ed79", "source:e930ea06441ebec8", "source:b6b5806105925722", "source:4355bd3c2f10452b", "source:493eed3f80d183ca", "source:2a71da3e68b7fe60", "source:183ba532a52e2dc1", "source:c38a7f79eb4a4885", "source:0e050030254190b2", "source:3dc607187a1b020a", "source:6cae48a1a466cf53", "source:dc75f7e7b9f882f5", "source:0a398317ce5ca4c3", "source:a1b5e64fcaba9f82", "source:328e11f48f666fac", "source:1e29fbbe136dc800", "source:95e704681c7ebf7a", "source:9cc990177a8e66f1", "source:0f72158bdde6d18d", "source:73a6498bcf500148", "source:f120ddf590b33479", "source:55d3157e2213930f", "source:249333033c1b17ce", "source:a83d9d5e5bda3374", "source:b3bb7a2b5d221fad", "source:d47a8f451d21eb85", "source:a430470d046dcc26", "source:ea340d7af7a54034", "source:c5fce170103bc868", "source:5330df961cbb98e0", "dynamic:abd2dfa0dab89079", "dynamic:621d4cd9ccf2eeb8", "dynamic:326143fea3f5efb2", "dynamic:57baf13f614d4829", "dynamic:943d1e9f4b887b99", "dynamic:81c5a46c5e57512f", "dynamic:dcc46e45cab4ea31", "dynamic:7201939f598f565c", "dynamic:3941430db3af6883", "dynamic:74523c8ae70366b2", "dynamic:c4a8ba13e9f6249b", "dynamic:e9b27578167822e8", "dynamic:4288ccd23ec94fa1", "dynamic:c450de4c6dce5b79", "dynamic:e997cfd80ea510c2", "dynamic:f13ca4d94d05e123", "dynamic:eec74145bcceab9e", "dynamic:f04bc5670a62d201", "dynamic:17b0c02fb5067706", "dynamic:9d85534b9ba567c8", "dynamic:ca0246c13ab63690", "dynamic:f7b46e6ccf5033ff", "dynamic:9f8c1778dda5c371", "dynamic:7933bb27a3361425", "dynamic:26828aa4b30bc617"] -->

- Не удалось завершить действие при обработке изображения. Подробности показаны в окне программы.

### 0089 · voice.7964f6a361de320a

<!-- LOPATA.Events: ["source:f4b4bb551f7f7a5c", "dynamic:1995d9ec3cc5879b", "dynamic:758765e0169ec0f5", "dynamic:b89702fba8e8f178", "dynamic:e7d6e3dbfbe20123"] -->

- Не удалось завершить действие при публикации. Подробности показаны в окне программы.

### 0090 · voice.99b7a7a268f9c41a

<!-- LOPATA.Events: ["source:894528474a7d47d9", "source:d4f650497c1eca3f", "source:1646930e227b4b4e", "source:c988f9c64d7115a3", "source:1644ba339cd273c3", "source:b08f2a4ead92e819", "source:0c4e4926e8b02c89", "source:3ce714cf5338daf7", "source:ca62f62134bf3203", "source:e6dc93d2d5f488ec", "source:c24f5f6dbaced914", "source:7f7532329a2d8ca6", "source:400efe4769c5a417", "source:5e7355631052f1ba", "source:7da26a88af247c77", "source:9b6235fae2d2314c", "source:ab8bc7fe0f5f704b", "source:398e97f902ac04e1", "source:f5a12db4ced945c8", "source:cb82c2db92e06930", "source:4ca4392b6748f567", "source:67c100907eea50f2", "source:810b00db02bd653a", "source:c719d91562b17def", "source:1e1c744da6832bde", "source:2b25fb6f274bf2b6", "source:9753d77a3d3d09ae", "source:4cd26958042dc007", "source:df856f83026dc929", "source:12c5216feec9e652", "source:78763bdf8c95bc83", "source:77363a70f870b8f5", "source:14625770ceb6868e", "source:56026847ee67264c", "source:7b2d2f85601cebf6", "source:416fafc48625e3bc", "source:4aff36adacf164bf", "source:32bb3f5b465e03e8", "source:0d40ee8e95a47bc0", "source:935ddc75929a3811", "source:6308540d42456860", "source:a099bc63479a7fa7", "source:c2e6bf9e8c04d524", "source:87e4f2f074f6a31c", "source:3db5f867177dc2c2", "source:a1c6e161e3b4daaa", "source:4d29be0bd3160b69", "source:4a7f9ee47e79e250", "source:682370260836b694", "source:7c6c3a64d426d35e", "source:d832546e0ae54af7", "source:83c1aa5488ea0029", "source:d0a2d06efdca503b", "source:907a5dd4e52af133", "source:e0f4bf080241c85f", "source:d561ac36277dba1b", "source:729952449b137872", "source:2eb3e3a9ba807414", "source:5197831b96986fd0", "source:8ac549dd293bb3b5", "source:33a15400a915dfc1", "source:d7d6b144510f1ed3", "source:a55c46d332d733d3", "source:5eba7af5e6b7ee4b", "source:617763b59f9ea8cb", "source:bd84098246de759d", "source:1141d2c1ee96880a", "source:da95410ad1b20ff1", "source:b7ebb8dd27c63759", "source:d84b611b3b937069", "source:05832d8bf0bc5379", "source:01351b0e08d8095d", "source:dd23bebefd92b373", "source:662cb3adaf8ddc5e", "source:270accd92e4db23a", "source:77c261eaaeeab094", "source:c47dd00cbdbb803b", "source:d899b1a36e432405", "source:76cc9522f73e4ae5", "source:6332e2dc69e68d39", "source:291b4bbafec84be4", "source:01ee357f9908756a", "source:1e54c7c6b78d793f", "source:8e9c6d105fc6c069", "source:fc097d291252f255", "source:4f51ee5ffcf009e7", "source:e618402df4aae038", "source:cc9d219c2f123e96", "source:e96d9d189f342554", "source:6490e69d00ab0e97", "source:e7ae54d7724c5479", "source:1fc4b462c7f067a0", "source:55fe00cb5d482cf7", "source:1b8e0d9adba2ffa3", "source:fa27da13537c338c", "source:d9ba806dea21ee1a", "source:caaa3f3d69ceb56e", "source:58b75bbae98b2ba1", "source:5e8b324030bb3d63", "source:6d4091502603b872", "source:f38198b478910633", "source:f2bf7462b5866ade", "source:d7448232c82d586b", "source:950b45210d80a593", "source:11030f836ee72afa", "source:71b9a020b8c80de3", "dynamic:2cf201be0bed6e04", "dynamic:4949cf2e4de87019", "dynamic:4eb3a2a56120500a", "dynamic:69cd6dd6776c406d", "dynamic:20ef6125486968a2", "dynamic:7d845ad5c5ab7c0f", "dynamic:c4db6b86649b061d", "dynamic:f358e55363c6b709"] -->

- Не удалось завершить действие при обновлении ЛОПАТЫ. Подробности показаны в окне программы.

### 0091 · voice.fa2864fecced0189

<!-- LOPATA.Events: ["source:283255ae9439a3aa", "source:e153657ef8f78607", "dynamic:4b01a5134cc83b04", "dynamic:b05ad3cfbf0565cf", "dynamic:9ab4c497662428c5", "dynamic:3ff89d67d665d607", "dynamic:2f45f0d6f1462266", "dynamic:2256a6807da01923", "dynamic:79fa0570979f6b1e", "dynamic:efe8d390698549d1", "dynamic:3380fd6df66abf97"] -->

- Не удалось завершить действие при работе с векторной графикой. Подробности показаны в окне программы.

### 0092 · voice.c23472e1548acbd8

<!-- LOPATA.Events: ["source:b318962b7416dad4", "source:60c917c45018e2f2", "source:21fb2f06de588db3", "source:ca91c353a3a7d36b", "source:d1cb59c3b3d581a1", "source:8cc6ec1db24778fc", "source:7ee9c830a6dc8ce5", "source:d11c7701d592ca2a", "source:96b227f69aeea107", "source:447f5ab9938c5cfc", "source:f238b2c22c23a728", "source:d7c8c500dae1e563", "source:d92433c8750ed695", "source:39a4d4010156d8f8", "source:b0426c3570ca1b9d", "source:9b45e3288f7049bf", "source:5f82518c57413419", "source:6d4fd09c4ebc8356", "source:3b2bec35a03dfb63", "source:5ee49b79cad0f68a", "source:2396dc8e8ddd6c57", "source:740d8ec6462b9f0a", "source:80052514c2b45419", "source:3db7c75068a3d205", "source:fdb35908d890fc52", "source:980b2546f27c69f7", "dynamic:e0ef6c54351865c2", "dynamic:4b2545aba5fcded6", "dynamic:35778b7aa5f07022"] -->

- Не удалось завершить действие при работе коллекции ЛОПАТЫ. Подробности показаны в окне программы.

### 0093 · voice.39d22f87e61c38dc

<!-- LOPATA.Events: ["source:7158f7d7587b3a8f", "source:5f6e7bbe4dd940f5", "source:09ab556405243e6b", "source:cfcd8f49740ba18e", "source:7dcffbdee68fc8e2", "source:de0c6ba39fbb79ac", "source:01d0a7fe47473b7f", "source:64fd0661fda13b03", "source:696324a33a666950", "source:4eda191852d75925", "source:294d1c351d836906", "source:ff391006d6d9980c", "dynamic:b859dbf82a6ac215", "dynamic:93a0dbb9986b3f90", "dynamic:380327137b3e9ca0", "dynamic:51669d62394a777a", "dynamic:784265edb8ce9d1a", "dynamic:7f362e55733ecb0b", "dynamic:e228e8f97f13009b", "dynamic:a92a4ebb558dd8d3", "dynamic:ba37c62143398206", "dynamic:bac2eace03294a1d", "dynamic:887b28c6cc6fe110", "dynamic:c75cd62825e2ad1e", "dynamic:9595c49c3dc5cb92", "dynamic:527bbdbb0e29d7a5"] -->

- Не удалось завершить действие при финансовом анализе. Подробности показаны в окне программы.

### 0094 · voice.478f0c0872aa7e90

<!-- LOPATA.Events: ["source:db1d34afa25df157", "source:3645feabe1988415", "dynamic:9bd107f8ecee2ea4"] -->

- Не удалось завершить действие при голосовом вводе. Подробности показаны в окне программы.

### 0095 · voice.2ce16c0c24c36d82

<!-- LOPATA.Events: ["source:16c0a5091d50e6e7", "source:b1d3f28e2cd4fd4f", "source:6fe9addae0352fd9", "source:7465bc7c2f935dff", "source:0bd5b9d4e3003b60", "source:ba53003cbb5a9551", "source:93369c98c19d988a", "source:63b9bce726621a1f", "source:f762aaebb5278e7d", "source:44d138aababd2acf", "source:52d22d0916328717", "model-log:background_restore_failed", "model-log:background_command_failed", "model-log:background_failure_retirement_failed", "model-log:background_checkpoint_failed", "dynamic:f8696ec3c98d05a4", "dynamic:b85aaeb86c0a0ed3", "dynamic:a17874b8204009a8", "dynamic:c338bc153575fd5d", "dynamic:ee5ddd053fadc561"] -->

- Не удалось завершить действие при выполнении фоновой задачи. Подробности показаны в окне программы.

### 0096 · voice.02d99d01083988b7

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Canceled", "enum:ImageUtilityItemStatus.Cancelled", "enum:MusicGenerationStage.Cancelled"] -->

- Задача отменена.

### 0097 · voice.3f4e956f696da70f

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Completed", "enum:ImageUtilityItemStatus.Completed", "enum:MusicGenerationStage.Completed", "worker:Complete", "worker:complete"] -->

- Задача завершена.

### 0098 · voice.79c316de04d3021e

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Countdown"] -->

- Готовимся продолжить задачу.

### 0099 · voice.6d34ed4b86b3071a

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Failed", "enum:ImageUtilityItemStatus.Failed"] -->

- Задача завершилась ошибкой.

### 0100 · voice.ff74c3d3a0aabb44

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Paused", "enum:MusicGenerationStage.Paused"] -->

- Задача приостановлена.

### 0101 · voice.535fc36e38ae395a

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Pausing"] -->

- Приостанавливаем задачу.

### 0102 · voice.864869fa787e5a7c

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Running", "enum:ImageUtilityItemStatus.Running"] -->

- Задача выполняется.

### 0103 · voice.4dad43886df37234

<!-- LOPATA.Events: ["enum:BackgroundOperationPhase.Waiting"] -->

- Ожидаем продолжения задачи или вашего решения.

### 0104 · voice.86b913e10c855147

<!-- LOPATA.Events: ["enum:ImageUtilityItemStatus.Duplicate"] -->

- Обнаружен повторяющийся элемент.

### 0105 · voice.fb40dada2e6d7089

<!-- LOPATA.Events: ["enum:ImageUtilityItemStatus.Pending"] -->

- Задача ожидает выполнения.

### 0106 · voice.ab2d1ffdf1e528a2

<!-- LOPATA.Events: ["enum:ImageUtilityItemStatus.Skipped"] -->

- Элемент пропущен.

### 0107 · voice.7554b1278a10ee32

<!-- LOPATA.Events: ["enum:InterviewStage.Adaptive"] -->

- Начинаем этап уточняющих вопросов.

### 0108 · voice.b82d072ede44d25c

<!-- LOPATA.Events: ["enum:InterviewStage.Correction"] -->

- Выполняем исправления.

### 0109 · voice.3502aab095d36102

<!-- LOPATA.Events: ["enum:InterviewStage.Mandatory"] -->

- Начинаем этап обязательных вопросов.

### 0110 · voice.333523490a2bd222

<!-- LOPATA.Events: ["enum:InterviewStage.Review"] -->

- Результат готов к просмотру и проверке.

### 0111 · voice.bd24118c24ec6e26

<!-- LOPATA.Events: ["enum:InterviewStage.Understanding"] -->

- Проверяем понимание материала.

### 0112 · voice.19b5b4675358858f

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Encoding"] -->

- Кодируем результат.

### 0113 · voice.e99fddc11f9d7bc4

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Error", "worker:error"] -->

- Исполнитель сообщил об ошибке.

### 0114 · voice.8689b32fbf09a808

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Idle"] -->

- Ожидание запуска задачи.

### 0115 · voice.651fdab8c53093fb

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Loading", "worker:Loading"] -->

- Загружаем модель и необходимые данные.

### 0116 · voice.eb1421b59c8859e7

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Planning"] -->

- Подготавливаем план выполнения.

### 0117 · voice.f9b7a065f6c3b545

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Separating"] -->

- Разделяем звуковые дорожки.

### 0118 · voice.392556dde311ea87

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Sequence"] -->

- Создаём музыкальную последовательность.

### 0119 · voice.ebbc7e968162d5cb

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Sound"] -->

- Создаём звуковой результат.

### 0120 · voice.6b0807d67b2ac940

<!-- LOPATA.Events: ["enum:MusicGenerationStage.Synchronizing"] -->

- Синхронизируем результат.

### 0121 · voice.2e1c8211d7bdfe35

<!-- LOPATA.Events: ["enum:ParagraphStage.Prepared"] -->

- Подготовка завершена.

### 0122 · voice.05f8404cec777fe0

<!-- LOPATA.Events: ["enum:ParagraphStage.Request"] -->

- Запрос подготовлен.

### 0123 · voice.6c2da76f010cc500

<!-- LOPATA.Events: ["enum:ParagraphStage.Result"] -->

- Получен результат обработки.

### 0124 · voice.5fca08b71c0525e0

<!-- LOPATA.Events: ["enum:UpdatePhase.Applying"] -->

- Устанавливаем обновление.

### 0125 · voice.902d2119ce8f3018

<!-- LOPATA.Events: ["enum:UpdatePhase.AwaitingHealth"] -->

- Проверяем запуск обновлённой программы.

### 0126 · voice.2c4fdeff43bdefc4

<!-- LOPATA.Events: ["enum:UpdatePhase.Healthy"] -->

- Обновлённая программа прошла проверку запуска.

### 0127 · voice.7e8ddae5c360e2ef

<!-- LOPATA.Events: ["enum:UpdatePhase.Prepared"] -->

- Обновление подготовлено к установке.

### 0128 · voice.0a877c7e30ff3822

<!-- LOPATA.Events: ["enum:UpdatePhase.RolledBack"] -->

- Предыдущая рабочая версия программы восстановлена.

### 0129 · voice.ac0327582f2ca87d

<!-- LOPATA.Events: ["enum:UpdatePhase.RollingBack"] -->

- Возвращаем предыдущую рабочую версию программы.

### 0130 · voice.b9d1dc52137f8e11

<!-- LOPATA.Events: ["localization:About.LinkFailed"] -->

- Не удалось открыть браузер.

### 0131 · voice.9e5434e58d4029af

<!-- LOPATA.Events: ["localization:Arcade.AbilityReady", "localization:Dictation.Ready", "localization:ExecutionRoute.Status.ready", "localization:Generation.Ready", "localization:Literary.Import.RagStages.Ready", "localization:Music.Rhyme.Stage.Ready", "localization:Vector.Ready"] -->

- Готово.

### 0132 · voice.cfcc90f73b52eef3

<!-- LOPATA.Events: ["localization:Arcade.Ai.FirstEvents"] -->

- Готовим случайные мини-события первого уровня.

### 0133 · voice.a12ca221e1e532cf

<!-- LOPATA.Events: ["localization:Arcade.Ai.IncompleteOutput"] -->

- Модель не закончила ответ. Подготовка остановлена.

### 0134 · voice.b9c14127a074dfd6

<!-- LOPATA.Events: ["localization:Arcade.Ai.InvalidBossArt"] -->

- Рисунок босса содержит неподдерживаемые данные. Сохранённый прогресс не изменён.

### 0135 · voice.cb1af170cf1664ee

<!-- LOPATA.Events: ["localization:Arcade.Ai.InvalidBossReward"] -->

- Модель вернула некорректную награду босса. Победа сохранена; подготовку награды можно повторить.

### 0136 · voice.77b1e80b4646a560

<!-- LOPATA.Events: ["localization:Arcade.Ai.Loading"] -->

- Загружаем модель.

### 0137 · voice.f16efe0204777358

<!-- LOPATA.Events: ["localization:Arcade.Ai.Preparing"] -->

- Готовим следующий уровень.

### 0138 · voice.39ee603c29099391

<!-- LOPATA.Events: ["localization:Arcade.Ai.Programming"] -->

- Собираем поведение противников и волны.

### 0139 · voice.09a267d68b8d22e7

<!-- LOPATA.Events: ["localization:Arcade.Ai.Ready"] -->

- Подготовка завершена.

### 0140 · voice.84465060975cfa2b

<!-- LOPATA.Events: ["localization:Arcade.Ai.Rejected"] -->

- Уровень не прошёл проверку. Запусти подготовку снова; готовые этапы сохранены.

### 0141 · voice.68db6bc72f2d2c62

<!-- LOPATA.Events: ["localization:Arcade.Ai.Reviewing"] -->

- Проверяем замысел и сложность.

### 0142 · voice.b13321c9141e47a9

<!-- LOPATA.Events: ["localization:Arcade.Ai.Testing"] -->

- Проверяем уровень пробными прохождениями.

### 0143 · voice.8cda6f99a6f796f5

<!-- LOPATA.Events: ["localization:Arcade.Ai.TimedOut"] -->

- Модель не ответила вовремя. Подготовка остановлена.

### 0144 · voice.9f1d7fdbd39f735c

<!-- LOPATA.Events: ["localization:Arcade.AudioUnavailable"] -->

- Звук недоступен. Игра продолжается без него.

### 0145 · voice.2af5a82955ae595a

<!-- LOPATA.Events: ["localization:Arcade.ExperienceSaveFailed"] -->

- Не удалось сохранить последний опыт героя. Игра продолжается; при следующем сохранении повторим попытку.

### 0146 · voice.9179dc1f915013e0

<!-- LOPATA.Events: ["localization:Arcade.Failed"] -->

- Не удалось завершить операцию. Сохранённый прогресс остаётся; подробности — в журнале.

### 0147 · voice.88ecc4d59d53d4ca

<!-- LOPATA.Events: ["localization:Arcade.GamePaused"] -->

- Пауза. Пробел или кнопка «Продолжить» — вернуться в игру.

### 0148 · voice.1951653c574a5f67

<!-- LOPATA.Events: ["localization:Arcade.Playing"] -->

- Игра идёт.

### 0149 · voice.5f7faec4ae338063

<!-- LOPATA.Events: ["localization:Arcade.Preparation.Cancelled", "localization:BookConverter.SmartPreparation.Cancelled"] -->

- Подготовка отменена.

### 0150 · voice.fd94d5daff2f8ced

<!-- LOPATA.Events: ["localization:Arcade.Preparation.Checking", "localization:Music.Preparation.Checking"] -->

- Проверяем компоненты.

### 0151 · voice.07c65d87f64835e5

<!-- LOPATA.Events: ["localization:Arcade.Preparation.Downloading"] -->

- Скачиваем модель.

### 0152 · voice.d6552b12983bed83

<!-- LOPATA.Events: ["localization:Arcade.Preparation.Failed"] -->

- Подготовка не завершена. Подробности — в журнале.

### 0153 · voice.2db1aff5d6835458

<!-- LOPATA.Events: ["localization:Arcade.Preparation.Ready"] -->

- Компоненты готовы.

### 0154 · voice.ce6196ebf59d11e0

<!-- LOPATA.Events: ["localization:Arcade.WaitNext"] -->

- Следующий уровень ещё готовится. Готовый прогресс сохранён.

### 0155 · voice.662ffe07d50d438d

<!-- LOPATA.Events: ["localization:Arcade.WaitReward"] -->

- Готовим усиление за победу над боссом. Прогресс сохранён; следующий уровень начнётся с наградой.

### 0156 · voice.ae8996fd5cbe7555

<!-- LOPATA.Events: ["localization:Arcade.WaitWarmup"] -->

- Ждём прогрева моделей.

### 0157 · voice.f8e65810ad1c78c1

<!-- LOPATA.Events: ["localization:AudioShell.Cancelled"] -->

- Обработка остановлена. Готовые результаты сохранены.

### 0158 · voice.be832808c12aa7d0

<!-- LOPATA.Events: ["localization:AudioShell.Done", "localization:BookConverter.Done", "localization:Finance.Stage.done", "localization:Processes.Done", "localization:VideoUpscale.Done"] -->

- Готово.

### 0159 · voice.ea44389593d6dbf4

<!-- LOPATA.Events: ["localization:AudioShell.Failed"] -->

- Не удалось обработать файл. Проверьте его доступность и свободное место.

### 0160 · voice.1541f0263db8182e

<!-- LOPATA.Events: ["localization:AudioShell.InvalidContainer"] -->

- Структура файла или метаданные повреждены. Оригинал сохранён.

### 0161 · voice.e94b65b67daca62e

<!-- LOPATA.Events: ["localization:AudioShell.MetadataTooLarge"] -->

- Метаданные слишком велики или повреждены. Обработка остановлена; оригинал сохранён.

### 0162 · voice.10eb440fa6309b9e

<!-- LOPATA.Events: ["localization:AudioShell.Preparing"] -->

- Проверяем аудиокомпоненты.

### 0163 · voice.fba6287b265442aa

<!-- LOPATA.Events: ["localization:AudioShell.Processing", "localization:Capture.GifAssembling", "localization:Capture.VideoAssembling", "localization:ImageUtility.ProcessingCount", "localization:Literary.Import.WorkingParts.Preparing", "localization:Music.Poetry.Search.Preparing", "localization:Music.Score.Progress", "localization:VideoPrep.VaceWorkspace.ProgressRetry"] -->

- Обработка продолжается. Текущий прогресс показан в окне программы.

### 0164 · voice.a5ba887e92538851

<!-- LOPATA.Events: ["localization:AudioShell.SidecarChanged"] -->

- Сопроводительные метаданные повреждены или относятся к другой версии песни. Обработка остановлена.

### 0165 · voice.79f40a5a85c09adf

<!-- LOPATA.Events: ["localization:AudioShell.Summary", "localization:Batch.Interrupted", "localization:ImageUtility.Event.Recovered", "localization:Literary.Interview.RecentStatus", "localization:Music.Poetry.Search.Waiting", "localization:VideoPrep.RefDrawingSummary"] -->

- Обновлены сведения о текущей операции. Подробности показаны в окне программы.

### 0166 · voice.898822771cfd394a

<!-- LOPATA.Events: ["localization:AudioShell.UnsupportedChannels"] -->

- MP3 поддерживает не более двух каналов. Преобразование остановлено без сведения каналов.

### 0167 · voice.f61eb02ee2a2b438

<!-- LOPATA.Events: ["localization:AudioShell.UnsupportedCodec"] -->

- Кодек этого файла не поддерживается текущим аудиокомплектом. WAV поддерживается в PCM 16 бит; другие WAV не изменяем.

### 0168 · voice.5d449f45c367a633

<!-- LOPATA.Events: ["localization:AudioShell.UnsupportedPrecision"] -->

- В текущем комплекте WAV записывается в 16 бит. Преобразование остановлено, чтобы не снизить исходную разрядность.

### 0169 · voice.d55071d877607afe

<!-- LOPATA.Events: ["localization:AudioShell.UnsupportedRate"] -->

- MP3 не поддерживает исходную частоту дискретизации. Преобразование остановлено без изменения частоты; выберите FLAC.

### 0170 · voice.09633a1928257682

<!-- LOPATA.Events: ["localization:AudioShell.UnsupportedStreams"] -->

- В файле несколько аудиодорожек или неподдерживаемые вложения. Обработка остановлена, чтобы не потерять данные.

### 0171 · voice.af0b8d6e58c77311

<!-- LOPATA.Events: ["localization:AudioShell.ValidationFailed"] -->

- Проверка результата не прошла. Готовый файл не опубликован; оригинал сохранён.

### 0172 · voice.089fb2e940bec971

<!-- LOPATA.Events: ["localization:Background.Stop.InProgress"] -->

- Сброс фоновых задач ещё не завершён. Дождитесь освобождения ресурсов или повторите STOP.

### 0173 · voice.e398cc6c1dfbdaaa

<!-- LOPATA.Events: ["localization:Batch.DoneDocument", "localization:Batch.DoneFolder", "localization:ImageUtility.CompletedCount", "localization:ImageUtility.Event.Completed", "localization:Literary.Import.Jelly.Ready", "localization:Literary.Import.WorkingParts.Complete", "localization:Literary.Import.WorkingParts.Ready", "localization:Music.Models.Remove.Done", "localization:VideoEditor.EventsReady"] -->

- Текущий этап завершён. Результат готов к проверке.

### 0174 · voice.232dd32bc7fa9b05

<!-- LOPATA.Events: ["localization:Batch.Failed"] -->

- Не удалось подготовить результат. Сохранённые материалы доступны для продолжения.

### 0175 · voice.7f914c16077b581e

<!-- LOPATA.Events: ["localization:Batch.Imported"] -->

- Импорт завершён. Проверьте состав загруженного набора.

### 0176 · voice.952c93de645309da

<!-- LOPATA.Events: ["localization:Batch.Item.error"] -->

- Ошибка — подробности при наведении.

### 0177 · voice.4f8f1d517fe5f0fb

<!-- LOPATA.Events: ["localization:Batch.Item.ready"] -->

- Описание готово.

### 0178 · voice.958abc0d91c0024e

<!-- LOPATA.Events: ["localization:Batch.Stage.completed"] -->

- Результат сохранён.

### 0179 · voice.41430cc2470fd6fd

<!-- LOPATA.Events: ["localization:Batch.Stage.failed"] -->

- Результат не готов.

### 0180 · voice.9950314973e17200

<!-- LOPATA.Events: ["localization:Batch.Stage.paused"] -->

- Работа остановлена. Можно продолжить.

### 0181 · voice.93b1967a85f34f10

<!-- LOPATA.Events: ["localization:Batch.State.completed"] -->

- Готово.

### 0182 · voice.3b917d7d2970442b

<!-- LOPATA.Events: ["localization:Batch.State.failed"] -->

- Не завершено.

### 0183 · voice.b2d09ae211ac08fd

<!-- LOPATA.Events: ["localization:Batch.State.paused", "localization:ImageUtility.Paused", "localization:Music.Projects.Paused"] -->

- Приостановлено.

### 0184 · voice.7bb68b335393e73f

<!-- LOPATA.Events: ["localization:BookConverter.CancelPending"] -->

- Останавливаем. Уже готовые результаты сохранятся.

### 0185 · voice.6a9e648ec9cccd18

<!-- LOPATA.Events: ["localization:BookConverter.Cancelled"] -->

- Операция остановлена. Готовые файлы сохранены.

### 0186 · voice.d0c4f85fac7beeee

<!-- LOPATA.Events: ["localization:BookConverter.CancelledShort", "localization:ImageUtility.Status.Cancelled", "localization:ImageUtility.Stopped"] -->

- Остановлено.

### 0187 · voice.b86aee6bffed7a2d

<!-- LOPATA.Events: ["localization:BookConverter.CancelledSummary", "localization:VideoPrep.RefImportCanceled"] -->

- Обработка остановлена. Готовые результаты сохранены.

### 0188 · voice.4c809f7c67c07021

<!-- LOPATA.Events: ["localization:BookConverter.Editor.NoText"] -->

- В книге нет редактируемых текстовых блоков.

### 0189 · voice.6a692227670ba995

<!-- LOPATA.Events: ["localization:BookConverter.Editor.NotFound"] -->

- Текст не найден.

### 0190 · voice.855a1410d971d31e

<!-- LOPATA.Events: ["localization:BookConverter.Error"] -->

- Не удалось выполнить действие. Подробности в отчёте.

### 0191 · voice.0f7e0d230123e66a

<!-- LOPATA.Events: ["localization:BookConverter.Error.ApplicationMissing"] -->

- Не найден файл запуска ЛОПАТЫ.

### 0192 · voice.9f6f0293c0958945

<!-- LOPATA.Events: ["localization:BookConverter.Error.CalibreRequired"] -->

- Встроенный calibre не найден. Восстановите установку ЛОПАТЫ.

### 0193 · voice.e6930219d06f743b

<!-- LOPATA.Events: ["localization:BookConverter.Error.ComponentUnprepared"] -->

- Необходимый компонент недоступен или его условия ещё не подтверждены. Откройте ЛОПАТУ, проверьте лицензии и целостность установки.

### 0194 · voice.067f67e12f5e99b3

<!-- LOPATA.Events: ["localization:BookConverter.Error.EncryptedBook"] -->

- Зашифрованный EPUB или обфускация шрифтов не поддерживаются. Защита не снимается.

### 0195 · voice.d410dc06c76cddb2

<!-- LOPATA.Events: ["localization:BookConverter.Error.ImageUnavailable"] -->

- Изображение или графическая страница отсутствует либо не читается.

### 0196 · voice.20a488648c7a7d13

<!-- LOPATA.Events: ["localization:BookConverter.Error.InputFormat"] -->

- Формат исходника не поддерживается.

### 0197 · voice.fad2b1d8ba1a5cce

<!-- LOPATA.Events: ["localization:BookConverter.Error.InvalidEmbeddedImage"] -->

- Неподдерживаемое встроенное изображение.

### 0198 · voice.994fc01caed6b0dd

<!-- LOPATA.Events: ["localization:BookConverter.Error.Metadata"] -->

- Не удалось записать или проверить обязательные метаданные. Результат не опубликован.

### 0199 · voice.644a409f4551a7f3

<!-- LOPATA.Events: ["localization:BookConverter.Error.MetadataFormat"] -->

- Для этого формата запись обязательных метаданных не поддерживается.

### 0200 · voice.b21fe18b2f6aa631

<!-- LOPATA.Events: ["localization:BookConverter.Error.MissingImage"] -->

- Изображение отсутствует или находится за пределами папки документа.

### 0201 · voice.b9d3bb81dc74a5b7

<!-- LOPATA.Events: ["localization:BookConverter.Error.NoPages"] -->

- Нет страниц для CBZ.

### 0202 · voice.b0b8dc4d5ca1f855

<!-- LOPATA.Events: ["localization:BookConverter.Error.NoTextLayer"] -->

- В исходнике нет текстового слоя. Страницы можно сохранить в графическом формате; распознавание относится к умному режиму.

### 0203 · voice.d943a73a61f78587

<!-- LOPATA.Events: ["localization:BookConverter.Error.OfficeBundleInvalid"] -->

- Файлы встроенного LibreOffice повреждены или отсутствуют. Восстановите установку ЛОПАТЫ.

### 0204 · voice.82b66eaec6deeeff

<!-- LOPATA.Events: ["localization:BookConverter.Error.OfficeRequired"] -->

- Встроенный LibreOffice не найден. Восстановите установку ЛОПАТЫ.

### 0205 · voice.f290dbfa5930b3d0

<!-- LOPATA.Events: ["localization:BookConverter.Error.OutputFormat"] -->

- Неподдерживаемый формат результата.

### 0206 · voice.8d8377843e685286

<!-- LOPATA.Events: ["localization:BookConverter.Error.PdfRenderer"] -->

- Компонент отрисовки PDF отсутствует.

### 0207 · voice.41a6f6a03f7c463a

<!-- LOPATA.Events: ["localization:BookConverter.Error.PdfUnavailable"] -->

- Исполнитель PDF недоступен.

### 0208 · voice.b6a1e417864585f4

<!-- LOPATA.Events: ["localization:BookConverter.Error.SourceUnsupported"] -->

- Этот исходный формат не поддерживается выбранным направлением.

### 0209 · voice.432f5545d388e845

<!-- LOPATA.Events: ["localization:BookConverter.Error.UnavailableOutput"] -->

- Этот выходной формат недоступен для исходника.

### 0210 · voice.db023aa0df544d17

<!-- LOPATA.Events: ["localization:BookConverter.Error.UnavailablePair"] -->

- Этот формат результата недоступен для выбранных исходников. Изображения не превращаются в текст без распознавания.

### 0211 · voice.f541e8b035f0ca4d

<!-- LOPATA.Events: ["localization:BookConverter.Failed", "localization:Components.Status.Failed", "localization:OfficeEditor.Sheet.Status.failed"] -->

- Ошибка.

### 0212 · voice.0d769f2e0d77f43e

<!-- LOPATA.Events: ["localization:BookConverter.LogResumed"] -->

- Конвертация продолжена из сохраненного состояния.

### 0213 · voice.99e5b158195253c9

<!-- LOPATA.Events: ["localization:BookConverter.NoSupportedFiles"] -->

- В выбранных путях нет поддерживаемых файлов.

### 0214 · voice.12e38617eaa946fd

<!-- LOPATA.Events: ["localization:BookConverter.NoWarnings"] -->

- Преобразование завершено без предупреждений.

### 0215 · voice.c2fc69e49bba467d

<!-- LOPATA.Events: ["localization:BookConverter.OpenFailed"] -->

- Не удалось открыть результат. Подробности в отчёте.

### 0216 · voice.093549b514723db9

<!-- LOPATA.Events: ["localization:BookConverter.Progress.Completed", "localization:Finance.Phase.Completed", "localization:ImageUtility.Status.Completed", "localization:Music.Status.Completed", "localization:VideoEditor.Done"] -->

- Готово.

### 0217 · voice.5faf2167017d099d

<!-- LOPATA.Events: ["localization:BookConverter.Progress.Converting"] -->

- Преобразуем и сохраняем структуру книги.

### 0218 · voice.ab7947c701cb32a1

<!-- LOPATA.Events: ["localization:BookConverter.Progress.Merge"] -->

- Собираем книгу из выбранных файлов.

### 0219 · voice.8160098f5ae4fe57

<!-- LOPATA.Events: ["localization:BookConverter.Progress.Metadata"] -->

- Записываем происхождение и проверяем результат.

### 0220 · voice.c2fe2c56d4929881

<!-- LOPATA.Events: ["localization:BookConverter.Ready", "localization:ExecutionRoute.Ready", "localization:LetItOut.AiState.Ready", "localization:Literary.Readiness.Ready", "localization:Processes.State.Ready", "localization:VideoPrep.VaceFile.ready"] -->

- Готов.

### 0221 · voice.c8ad3bb2b6cc0012

<!-- LOPATA.Events: ["localization:BookConverter.Review.BlockMissing"] -->

- Этот текстовый блок больше недоступен. Обновите просмотр.

### 0222 · voice.bf86124c1d5d4227

<!-- LOPATA.Events: ["localization:BookConverter.Review.Error"] -->

- Не удалось подготовить или обновить просмотр. Подробности ниже.

### 0223 · voice.88eeb80e37d2c3e9

<!-- LOPATA.Events: ["localization:BookConverter.Review.EvidenceUnmatched"] -->

- Проверьте эти места по оригиналу; они не окрашены.

### 0224 · voice.f18d9139a1c5ebb3

<!-- LOPATA.Events: ["localization:BookConverter.Review.ImageUnavailable"] -->

- Изображение недоступно для предпросмотра.

### 0225 · voice.97aab2de80c3b55a

<!-- LOPATA.Events: ["localization:BookConverter.Review.NoEditableText"] -->

- В этом результате нет доступного для правки текста.

### 0226 · voice.3f1bf9961fae5f3c

<!-- LOPATA.Events: ["localization:BookConverter.Review.OriginalUnavailable"] -->

- Исходный файл недоступен. Проверьте, что он не перемещён и доступен для чтения.

### 0227 · voice.0c44fa47dbcc19e2

<!-- LOPATA.Events: ["localization:BookConverter.Review.PdfError"] -->

- Не удалось показать исходную страницу PDF.

### 0228 · voice.07f3cf6a535a15e8

<!-- LOPATA.Events: ["localization:BookConverter.Review.PdfLoading"] -->

- Подготавливаем исходную страницу.

### 0229 · voice.e37e3a96b255eed3

<!-- LOPATA.Events: ["localization:BookConverter.Review.Progress"] -->

- Подготавливаем результат к сохранению.

### 0230 · voice.4e672b7f06a7bd41

<!-- LOPATA.Events: ["localization:BookConverter.Review.StageChanged"] -->

- Файл просмотра изменился вне редактора. Результат не сохранён.

### 0231 · voice.72a79760a9641e4b

<!-- LOPATA.Events: ["localization:BookConverter.Review.Waiting"] -->

- Ожидаем проверки результата.

### 0232 · voice.be296e5af0ffc01d

<!-- LOPATA.Events: ["localization:BookConverter.SkippedShort", "localization:ImageUtility.Status.Skipped"] -->

- Пропущено.

### 0233 · voice.45c2273019e817f4

<!-- LOPATA.Events: ["localization:BookConverter.Smart.InvalidOcr"] -->

- Распознанная страница не прошла проверку. Исходник не изменён.

### 0234 · voice.6395e2ca8fdf1f79

<!-- LOPATA.Events: ["localization:BookConverter.Smart.InvalidStructure"] -->

- Модель вернула неполный или некорректный разбор структуры. Результат не сохранён.

### 0235 · voice.f58e6b7831bc7007

<!-- LOPATA.Events: ["localization:BookConverter.Smart.InvalidVision"] -->

- Не удалось получить проверяемый результат анализа изображения.

### 0236 · voice.7fc39cd0219e4a3b

<!-- LOPATA.Events: ["localization:BookConverter.Smart.InvalidWork"] -->

- Не удалось прочитать сохранённое задание умного режима.

### 0237 · voice.34f7768e7a427625

<!-- LOPATA.Events: ["localization:BookConverter.Smart.NotPrepared"] -->

- Выбранные модели ещё не подготовлены. Откройте подготовку умного режима.

### 0238 · voice.1bc95f41ef09ead3

<!-- LOPATA.Events: ["localization:BookConverter.Smart.OcrFailed"] -->

- Не удалось распознать страницу. Подробности сохранены в журнале задания.

### 0239 · voice.b716d2fd9c2957a1

<!-- LOPATA.Events: ["localization:BookConverter.Smart.OutputChanged"] -->

- Сохранённый результат изменён или отсутствует. Автоматическое продолжение остановлено, чтобы не создать дубликат.

### 0240 · voice.acdede3acd7e5a5f

<!-- LOPATA.Events: ["localization:BookConverter.Smart.WorkChanged"] -->

- Настройки сохранённого задания изменились. Начните новое задание.

### 0241 · voice.05d7f83bd666bbc7

<!-- LOPATA.Events: ["localization:BookConverter.SmartPreparation.CheckRequired"] -->

- Требуется проверка выбранных компонентов.

### 0242 · voice.2ea49da70325fc31

<!-- LOPATA.Events: ["localization:BookConverter.SmartPreparation.Failed"] -->

- Не удалось подготовить компоненты. Можно повторить проверку.

### 0243 · voice.23c45c622b629958

<!-- LOPATA.Events: ["localization:BookConverter.SmartPreparation.Missing"] -->

- Файлы отсутствуют или требуют восстановления.

### 0244 · voice.8e35f460c3842a17

<!-- LOPATA.Events: ["localization:BookConverter.SmartPreparation.MissingSize", "source:3e59b00928beefcb", "source:1cc01d1c7d8d5735", "source:35504d8348d8644d", "source:3989ae3a6c5e7f58", "source:264705dc087c18bb", "source:79bb4d188cc1799e", "source:013ab35dc41c2a9a", "source:b075766f379b2a6c", "source:373a9f1bd8297990", "source:21437b952c71df37", "source:02f6452e3e987e2b", "source:80d5df12ede4c329", "source:405bdc15dc0805a7"] -->

- Не найдены необходимые данные или компоненты при обработке книги.

### 0245 · voice.710f54057f024144

<!-- LOPATA.Events: ["localization:BookConverter.SmartPreparation.NotReady"] -->

- Комплект ещё не готов. Подготовьте недостающие компоненты или измените выбор.

### 0246 · voice.6408cedcefb6a339

<!-- LOPATA.Events: ["localization:BookConverter.SmartPreparation.Preparing"] -->

- Подготавливаем выбранные компоненты.

### 0247 · voice.7770d980a23b64ac

<!-- LOPATA.Events: ["localization:BookConverter.SmartPreparation.StorageMissing"] -->

- Не выбрана папка моделей.

### 0248 · voice.45c73326b254c3f5

<!-- LOPATA.Events: ["localization:BookConverter.Warning.DocxNotes"] -->

- DOCX: оформление нормализовано; примечания сохранены как связанные разделы в конце текста.

### 0249 · voice.7e7aafea2ed4899a

<!-- LOPATA.Events: ["localization:BookConverter.Warning.ExternalCss"] -->

- Внешний или недоступный CSS-ресурс исключён; сетевые ресурсы не загружаются.

### 0250 · voice.914e643a5cb14725

<!-- LOPATA.Events: ["localization:BookConverter.Warning.PdfImage"] -->

- PDF содержит изображение, которое не удалось извлечь. Для точного вида сохраните PDF в CBZ.

### 0251 · voice.89a42574aa502c9f

<!-- LOPATA.Events: ["localization:BookConverter.Warning.PdfScanned"] -->

- Страницы PDF без текста сохранены как изображения; распознавание не запускалось.

### 0252 · voice.9ec712f2c05e62b6

<!-- LOPATA.Events: ["localization:BookConverter.Warning.TextOnly"] -->

- TXT не сохраняет оформление и изображения; текст и подписи сохранены.

### 0253 · voice.e613c5fc3c8db155

<!-- LOPATA.Events: ["localization:Capture.AudioUnavailable"] -->

- Не удалось получить список звуковых устройств Windows.

### 0254 · voice.0792d2855e8f1378

<!-- LOPATA.Events: ["localization:Capture.DeviceMissing"] -->

- Ранее выбранное устройство отключено.

### 0255 · voice.c539bd7e7c213659

<!-- LOPATA.Events: ["localization:Capture.Done"] -->

- Снимок готов.

### 0256 · voice.48053fa233a1d6a3

<!-- LOPATA.Events: ["localization:Capture.Error"] -->

- Не удалось завершить захват.

### 0257 · voice.964f121194ecf009

<!-- LOPATA.Events: ["localization:Capture.GifExitWait"] -->

- Завершаем GIF перед выходом. Дождитесь сохранения записи.

### 0258 · voice.6ea1d2faf526b80d

<!-- LOPATA.Events: ["localization:Capture.GifMissed"] -->

- Временные интервалы сохранены.

### 0259 · voice.5340eb8ae069647f

<!-- LOPATA.Events: ["localization:Capture.GifPreserved"] -->

- GIF не завершён. Временные кадры сохранены; можно повторить сборку.

### 0260 · voice.2be12fce1878822c

<!-- LOPATA.Events: ["localization:Capture.GifReduced"] -->

- Чёткость временно снижалась из-за нагрузки; итоговый размер сохранён.

### 0261 · voice.203f88ba1a3d5ef0

<!-- LOPATA.Events: ["localization:Capture.HiddenUnavailable"] -->

- Окно скрыто или свёрнуто. В настройках отключено его восстановление для снимка.

### 0262 · voice.3d988450d9b93c65

<!-- LOPATA.Events: ["localization:Capture.PendingFrame"] -->

- Предыдущий снимок ещё не сохранён. Сначала сохраните его в доступную папку.

### 0263 · voice.120945393bd118a2

<!-- LOPATA.Events: ["localization:Capture.ReplaceText"] -->

- В буфере обмена находится текст. Заменить его скриншотом?

### 0264 · voice.56ed46594971d3bc

<!-- LOPATA.Events: ["localization:Capture.RetrySave"] -->

- Полученный снимок сохранён в памяти. Попробовать сохранить PNG в другую папку?

### 0265 · voice.3bafb2b4b58bef97

<!-- LOPATA.Events: ["localization:Capture.TextKept"] -->

- Текст в буфере сохранён.

### 0266 · voice.fc35cdf869428adc

<!-- LOPATA.Events: ["localization:Capture.VideoMissing"] -->

- Встроенный кодировщик видео отсутствует. Восстановите установку ЛОПАТЫ.

### 0267 · voice.1b0800f5a7a80cec

<!-- LOPATA.Events: ["localization:Capture.VideoPreserved"] -->

- Видеозапись сохранена во временной папке. Сборку можно повторить.

### 0268 · voice.a621cb5940746113

<!-- LOPATA.Events: ["localization:ChatSpeech.Empty"] -->

- В сообщении нет текста для чтения.

### 0269 · voice.ac2495b72fe911aa

<!-- LOPATA.Events: ["localization:ChatSpeech.Failed"] -->

- Не удалось озвучить сообщение. Текст сохранён.

### 0270 · voice.d2886490cd786f25

<!-- LOPATA.Events: ["localization:ChatSpeech.Invalid"] -->

- Не удалось прочитать задание озвучки.

### 0271 · voice.725a195eecd45358

<!-- LOPATA.Events: ["localization:ChoiceScenario.CoreUnavailableStop"] -->

- Ядро недоступно, поэтому продолжить сужение сейчас нельзя. Вернитесь назад или отмените сценарий.

### 0272 · voice.80fb2a57240b1723

<!-- LOPATA.Events: ["localization:ChoiceScenario.CoreWorking"] -->

- Ядро готовит следующий шаг сценария.

### 0273 · voice.ba8b7c54719039f3

<!-- LOPATA.Events: ["localization:ChoiceScenario.Files.Missing", "localization:Reader.Missing"] -->

- Файл не найден.

### 0274 · voice.3a85945ff49f01a7

<!-- LOPATA.Events: ["localization:ChoiceScenario.RepeatedStepError"] -->

- Ядро повторило уже пройденный шаг. Можно выбрать другой путь или перейти к финалу.

### 0275 · voice.3a279cd7a67b4d94

<!-- LOPATA.Events: ["localization:CloseChoice.TrayUnavailable"] -->

- Значок трея недоступен. Скрытие программы сейчас невозможно.

### 0276 · voice.f9eda0a724d0ff91

<!-- LOPATA.Events: ["localization:Collection.Failed"] -->

- Не удалось обновить интеграцию коллекции. Проверьте наличие компонентов и повторите попытку.

### 0277 · voice.20436e70556cbb77

<!-- LOPATA.Events: ["localization:ComponentAudit.SaveFailed"] -->

- Не удалось сохранить настройку аудита компонентов. Прежний режим сохранён.

### 0278 · voice.3ad9bdaafab50bbb

<!-- LOPATA.Events: ["localization:Components.DownloadCancelled"] -->

- Загрузка компонентов отменена.

### 0279 · voice.40d0ac9799d09061

<!-- LOPATA.Events: ["localization:Components.DownloadComplete"] -->

- Выбранные компоненты обработаны. Статусы обновлены.

### 0280 · voice.9330678888901256

<!-- LOPATA.Events: ["localization:Components.DownloadConfirm", "localization:ImageAnalysis.Install.DownloadConfirm", "localization:ToolModel.RerankerMissingPrompt"] -->

- Для продолжения требуется скачивание компонентов. Проверьте сведения и подтвердите загрузку.

### 0281 · voice.d99f1e9e95fa2a7b

<!-- LOPATA.Events: ["localization:Components.DownloadFailed", "source:f6d229d1b10a0804", "source:8d939f4291165c45", "source:6d4ff248f3a3fddf", "source:24c515bab398ca11", "source:15d78c6acccf1f73", "source:a8b1cf6cb1f186c2", "source:dea769edc1632e65", "source:2308dec6e4ea5b91", "source:85a40764fc880d92", "source:8ba4793eafb20085", "source:7dc3ec6c6d824779", "source:1cc66b5cd47442b9", "source:46576f29d8528a9b", "source:fbc0483933516ab4", "source:f8ce3d06cf194be8", "source:2202e238953b45de", "source:b4c34af45534945c", "source:d2b5f35654e51b12", "source:8fd9f114d69634a3", "source:9b6c6e833f3e1cb7", "source:f9c602e530045ebc", "source:5ea484199b24e972", "source:ce7685180b90aa8e", "source:fd1da163402062d2", "source:bed6184c820531e8", "source:479001451dbb11e9", "source:98e2ce2aa98937d0", "source:f7f5179a34985037", "source:f9a9908256a87096", "source:303d19c059136440", "source:8bb5dd033c1bfac2", "source:069c5d3a0a5822b0", "source:9f0153893e8bf67d", "source:52fcb97bd68c0295", "source:8389aa94cafd4f08"] -->

- Не удалось получить необходимые данные при работе с моделью. Проверьте соединение и настройки.

### 0282 · voice.89005f0ebf4b70d2

<!-- LOPATA.Events: ["localization:Components.DownloadingProgress", "localization:Components.LaunchInstallerConfirm", "localization:CoreModel.DownloadProgress", "localization:DebugChat.DownloadProgress", "localization:DebugChat.DownloadProgressUnknownTotal", "localization:ImageAnalysis.Install.ProgressValueWithSpeed", "localization:ImageAnalysis.Workspace.Voice.DownloadProgress", "localization:ImageUtility.Event.Downloading", "localization:Status.ExecutorDownloading", "localization:ToolModel.RerankerDownloadProgress"] -->

- Идёт загрузка и подготовка компонентов.

### 0283 · voice.4e2427c37c3a28f1

<!-- LOPATA.Events: ["localization:Components.ExecutorPlanDeclined"] -->

- Запуск исполнителя остановлен: обязательные компоненты не подтверждены.

### 0284 · voice.6a1d4589dc1818cb

<!-- LOPATA.Events: ["localization:Components.ExecutorRequestStatus"] -->

- Ожидается решение по доверенному плану компонентов.

### 0285 · voice.d587851344f3034f

<!-- LOPATA.Events: ["localization:Components.ExternalDiscoveryFound"] -->

- Поиск завершён. Справочные материалы найдены.

### 0286 · voice.7b696c3011fcfe22

<!-- LOPATA.Events: ["localization:Components.ExternalDiscoveryWarning"] -->

- В доверенном каталоге ЛОПАТА нет части запрошенных возможностей. Разрешить исполнителю поискать внешние варианты?

### 0287 · voice.151a6d3fde6a9e6b

<!-- LOPATA.Events: ["localization:Components.NothingSelected"] -->

- Не выбрано ни одного доступного для загрузки компонента.

### 0288 · voice.ead63151f9e55714

<!-- LOPATA.Events: ["localization:Components.Removed", "localization:Models.Result.Removed", "localization:Status.ImageAnalysisVisionRemoved"] -->

- Выбранные файлы удалены.

### 0289 · voice.12d8675a5bff79e7

<!-- LOPATA.Events: ["localization:Components.Status.Downloaded"] -->

- Загружено.

### 0290 · voice.16cf74e78f479e27

<!-- LOPATA.Events: ["localization:Components.Status.Downloading", "localization:Models.Status.downloading"] -->

- Загружается.

### 0291 · voice.ff9d50ff273e4913

<!-- LOPATA.Events: ["localization:Components.Status.NotInstalled"] -->

- Не установлено.

### 0292 · voice.c03fe164a2642aeb

<!-- LOPATA.Events: ["localization:Components.VerificationComplete"] -->

- Проверка установленных компонентов завершена.

### 0293 · voice.3ffb185b68257c5a

<!-- LOPATA.Events: ["localization:CoreModel.FolderUnavailablePrompt"] -->

- Папка моделей недоступна. Подключите диск или измените папку хранения.

### 0294 · voice.f2d42c4553936e97

<!-- LOPATA.Events: ["localization:CoreModel.MissingPrompt"] -->

- Настройки сохранены. Основное ядро Qwen3 8B Q4\_K\_M не найдено.

### 0295 · voice.b535393a08100af1

<!-- LOPATA.Events: ["localization:DebugChat.BackendMissing"] -->

- Backend llama-server.exe и fallback llama-cli.exe не найдены.

### 0296 · voice.ce82d7623f0dd604

<!-- LOPATA.Events: ["localization:DebugChat.Error"] -->

- Ошибка backend-а. Подробности в логе.

### 0297 · voice.b737939e384d0612

<!-- LOPATA.Events: ["localization:DebugChat.Generating"] -->

- Генерация ответа.

### 0298 · voice.325c1cc66eb5914e

<!-- LOPATA.Events: ["localization:DebugChat.LogBackendMissing", "localization:DebugChat.LogCliBackendMissing", "localization:DebugChat.LogServerBackendMissing", "source:b6242fa223d9bfa7", "source:d9fedd379bf96a19", "source:803d58772b80942d", "source:794b4a055c571d83", "source:11ae3b3b00eb9d5c", "source:5a0ce0684f1d8219", "source:3b22a524009d875e", "source:942dc51219aa463b", "source:05835fe713677241"] -->

- Не найдены необходимые данные или компоненты при работе чата.

### 0299 · voice.b35b9a252b25259c

<!-- LOPATA.Events: ["localization:DebugChat.LogCoreMemoryModelFallback"] -->

- Не удалось сжать память моделью. Используется резервное сжатие.

### 0300 · voice.31e7319b3eb80c45

<!-- LOPATA.Events: ["localization:DebugChat.LogCoreToolTestFinished"] -->

- Проверка инструментов через ядро завершена.

### 0301 · voice.5ab837b5650b0f8d

<!-- LOPATA.Events: ["localization:DebugChat.LogNoModelSelected"] -->

- Отправка отменена: модель не выбрана.

### 0302 · voice.112c3c96becb570a

<!-- LOPATA.Events: ["localization:DebugChat.LogStopped", "localization:DebugChat.Stopped"] -->

- Генерация остановлена.

### 0303 · voice.24ca9374ab38979a

<!-- LOPATA.Events: ["localization:DebugChat.LogToolAgentFinished"] -->

- Debug-ядро завершило ответ с учётом инструментов.

### 0304 · voice.accea3f6e9e0504b

<!-- LOPATA.Events: ["localization:DebugChat.NoModels"] -->

- Модели не найдены в выбранных папках хранения.

### 0305 · voice.1f753889c434c94e

<!-- LOPATA.Events: ["localization:Diagnostics.Literary.OpenFailed"] -->

- Не удалось открыть папку журналов.

### 0306 · voice.ed1802ccf0fb3572

<!-- LOPATA.Events: ["localization:Dictation.Error"] -->

- Ошибка ввода.

### 0307 · voice.ad837c3f1e4d1372

<!-- LOPATA.Events: ["localization:Dictation.HotkeyConflict"] -->

- Горячая клавиша занята.

### 0308 · voice.e192f3bcec8f52b4

<!-- LOPATA.Events: ["localization:Dictation.Overloaded"] -->

- Распознавание не успевает за речью. Запись остановлена; уже введённый текст сохранён.

### 0309 · voice.e97ae21341643d9b

<!-- LOPATA.Events: ["localization:Dictation.Preparing"] -->

- Подготовка.

### 0310 · voice.8fcf600f7512773e

<!-- LOPATA.Events: ["localization:Dictation.TargetLost"] -->

- Цель недоступна.

### 0311 · voice.16751acb640ee9de

<!-- LOPATA.Events: ["localization:Dictation.UnsupportedTarget"] -->

- Поле не поддерживается.

### 0312 · voice.043840f3357f6b44

<!-- LOPATA.Events: ["localization:EmergencyStop.Completed"] -->

- Фоновые задачи остановлены. Проекты и готовые файлы сохранены.

### 0313 · voice.1727ea05cea63edf

<!-- LOPATA.Events: ["localization:EmergencyStop.Failed"] -->

- Не удалось подтвердить остановку всех задач и освобождение моделей. Новый запуск остаётся заблокирован.

### 0314 · voice.781f7b49174b5755

<!-- LOPATA.Events: ["localization:EmergencyStop.Phase.Paused", "localization:Finance.Phase.Paused", "localization:LetItOut.AiState.Paused", "localization:Vector.Paused"] -->

- На паузе.

### 0315 · voice.023349f0d5300966

<!-- LOPATA.Events: ["localization:EmergencyStop.Phase.Waiting"] -->

- Ожидает продолжения.

### 0316 · voice.380ae8191c4994ec

<!-- LOPATA.Events: ["localization:EngineSets.Busy"] -->

- Сначала завершите текущую работу с моделями или загрузку. Во время работы переключать движки нельзя.

### 0317 · voice.f025254cab1d6a69

<!-- LOPATA.Events: ["localization:EngineSets.Cancelled"] -->

- Подготовка остановлена. Активный набор не изменён.

### 0318 · voice.a41c18b23df77212

<!-- LOPATA.Events: ["localization:EngineSets.Failed"] -->

- Не удалось подготовить набор. Прежний выбор сохранён.

### 0319 · voice.01e94bd700fc35dc

<!-- LOPATA.Events: ["localization:EngineSets.Working"] -->

- Проверяем и подготавливаем набор.

### 0320 · voice.f75b84b0a4dca584

<!-- LOPATA.Events: ["localization:ExecutionRoute.Blocked"] -->

- Не хватает модуля.

### 0321 · voice.294bfb6f8b1ab06b

<!-- LOPATA.Events: ["localization:ExecutionRoute.ExternalWarning"] -->

- Это только результаты поиска. Они не установлены, не проверены и пока не входят в исполнимый маршрут.

### 0322 · voice.36b2f1c1c40ee128

<!-- LOPATA.Events: ["localization:ExecutionRoute.Status.adapter_missing"] -->

- Нет безопасного адаптера.

### 0323 · voice.5263bcea97899850

<!-- LOPATA.Events: ["localization:Executor.LivePreviewWaiting"] -->

- Исполнитель готовит первый краткий пересказ доступного результата.

### 0324 · voice.613552e15688145e

<!-- LOPATA.Events: ["localization:Executor.ResultStatus.ArtifactValue.pending", "localization:Executor.ResultStatus.EvidenceValue.pending", "localization:Executor.ResultStatus.TaskValue.pending"] -->

- Проверка не завершена.

### 0325 · voice.7b603cb48350fb4e

<!-- LOPATA.Events: ["localization:Executor.ResultStatus.EvidenceValue.invalid"] -->

- Обязательные доказательства отсутствуют.

### 0326 · voice.5f96be16e4320663

<!-- LOPATA.Events: ["localization:Executor.ResultStatus.EvidenceValue.limited"] -->

- Часть доказательств отсутствует.

### 0327 · voice.67435984637b5fb7

<!-- LOPATA.Events: ["localization:Executor.StageRecommended"] -->

- Техническая постановка готова к подтверждению.

### 0328 · voice.20abea7be24f70eb

<!-- LOPATA.Events: ["localization:Executor.StatusDownload"] -->

- Потребуется скачать и проверить.

### 0329 · voice.df6fe356f0cec17c

<!-- LOPATA.Events: ["localization:Executor.StatusInstalled"] -->

- Уже установлена — можно запустить сразу.

### 0330 · voice.ede57915439726f8

<!-- LOPATA.Events: ["localization:FileViewer.ExternalMissing"] -->

- Windows не нашла приложение для этого файла. Открыть безопасное текстовое представление внутри ЛОПАТА?

### 0331 · voice.a2b748baf6d0d125

<!-- LOPATA.Events: ["localization:FileViewer.InternalFailed"] -->

- Не удалось создать внутреннее представление.

### 0332 · voice.d89a0844d153ce6f

<!-- LOPATA.Events: ["localization:FileViewer.NoTextBody"] -->

- (нет текстового тела).

### 0333 · voice.6388be863a3dcff8

<!-- LOPATA.Events: ["localization:Finance.AnalysisFailed"] -->

- Анализ не завершён. Готовые этапы сохранены.

### 0334 · voice.137be3d76a859e41

<!-- LOPATA.Events: ["localization:Finance.BackendMissing"] -->

- Нужный backend не установлен.

### 0335 · voice.ab170ddbd145ea73

<!-- LOPATA.Events: ["localization:Finance.ContextTooSmall"] -->

- Запрос не помещается в контекст выбранной модели. Готовые этапы сохранены.

### 0336 · voice.90c3c46891e86ba7

<!-- LOPATA.Events: ["localization:Finance.CopyFailed"] -->

- Не удалось скопировать текст.

### 0337 · voice.386115b42e09b879

<!-- LOPATA.Events: ["localization:Finance.Discussion.Cancelled"] -->

- Ответ остановлен. Сообщение осталось в поле ввода.

### 0338 · voice.8e48f27d041c3dcf

<!-- LOPATA.Events: ["localization:Finance.Discussion.ContextFull"] -->

- Переписка не помещается в доступный контекст. Сообщение осталось в поле ввода.

### 0339 · voice.539580d9902edeb1

<!-- LOPATA.Events: ["localization:Finance.Discussion.Failed"] -->

- Не удалось получить или сохранить ответ. Ввод остаётся в окне, готовые результаты не изменены.

### 0340 · voice.f15f1d5d45907327

<!-- LOPATA.Events: ["localization:Finance.Discussion.SaveFailed"] -->

- Не удалось сохранить обсуждение. Проверьте доступ к папке расчёта и свободное место.

### 0341 · voice.312afe2e5213302f

<!-- LOPATA.Events: ["localization:Finance.Discussion.Working"] -->

- Советник готовит ответ.

### 0342 · voice.fb39c8dd2d9052c2

<!-- LOPATA.Events: ["localization:Finance.Done"] -->

- Все этапы готовы. Результаты сохранены в папке.

### 0343 · voice.27fd400d6d2f26e8

<!-- LOPATA.Events: ["localization:Finance.FolderFailed"] -->

- Не удалось открыть папку.

### 0344 · voice.4794014d87a0f0e8

<!-- LOPATA.Events: ["localization:Finance.InvalidAmount"] -->

- Введите неотрицательную сумму. Частичное покрытие не должно превышать расход.

### 0345 · voice.16b6d130e2d6cb95

<!-- LOPATA.Events: ["localization:Finance.InvalidModelReply"] -->

- Модель не вернула подходящее итоговое пояснение. Готовые этапы сохранены; выберите другую модель и продолжите.

### 0346 · voice.a12e82435fcf9ef5

<!-- LOPATA.Events: ["localization:Finance.InvalidPersonalAmount"] -->

- Введите неотрицательную сумму. Если расхода нет, оставьте 0.

### 0347 · voice.d62e8a01147c0651

<!-- LOPATA.Events: ["localization:Finance.LoadFailed"] -->

- Не удалось прочитать набор данных. Исходные файлы сохранены.

### 0348 · voice.58ee631461a6517b

<!-- LOPATA.Events: ["localization:Finance.ModelMissing"] -->

- Файл выбранной модели отсутствует. Выберите установленную модель и продолжите.

### 0349 · voice.31ee42d515877e9a

<!-- LOPATA.Events: ["localization:Finance.ModelUnsupported"] -->

- Эта модель не поддерживается текстовым backend-ом.

### 0350 · voice.ddb47840f4d66947

<!-- LOPATA.Events: ["localization:Finance.NoModels"] -->

- Поддерживаемая чат-модель не найдена. Можно заполнить опрос и получить расчёт.

### 0351 · voice.d1d988b98bd8601a

<!-- LOPATA.Events: ["localization:Finance.NotReady"] -->

- Этот этап ещё не выполнен.

### 0352 · voice.6e604653704c56d5

<!-- LOPATA.Events: ["localization:Finance.Phase.Waiting"] -->

- Ожидает продолжения.

### 0353 · voice.77fa21c313109395

<!-- LOPATA.Events: ["localization:Finance.Ready"] -->

- Расчёт готов. Перед анализом проверьте ответы.

### 0354 · voice.98f7d43f84c085b2

<!-- LOPATA.Events: ["localization:Finance.SaveFailed"] -->

- Не удалось сохранить расчёт.

### 0355 · voice.05accf35962c11f0

<!-- LOPATA.Events: ["localization:Finance.Saved"] -->

- Расчёт сохранён в папке.

### 0356 · voice.7b94651388e48490

<!-- LOPATA.Events: ["localization:Finance.Stage.missing"] -->

- Пропуски данных.

### 0357 · voice.fc81b4743926e075

<!-- LOPATA.Events: ["localization:Generation.AutoSaveFailed"] -->

- Не удалось сохранить копию. Оригинал остался в сессии.

### 0358 · voice.97a1d0a059b13545

<!-- LOPATA.Events: ["localization:Generation.AutoSaved"] -->

- Сохранено автоматически.

### 0359 · voice.673425a2c3f57e24

<!-- LOPATA.Events: ["localization:Generation.Canceled"] -->

- Остановлено. Готовые изображения и загруженные части сохранены.

### 0360 · voice.1c0c9c428984898c

<!-- LOPATA.Events: ["localization:Generation.DownloadRequired"] -->

- Комплект не готов. Вернитесь к выбору модели и скачайте или проверьте файлы.

### 0361 · voice.e37b0882a1ecf4a2

<!-- LOPATA.Events: ["localization:Generation.Downloading", "localization:RagBooks.Downloading"] -->

- Загрузка.

### 0362 · voice.1e2f1c9134d4ed56

<!-- LOPATA.Events: ["localization:Generation.Error", "localization:Music.Midi.Failed", "localization:Vector.Editor.Error.General"] -->

- Не удалось выполнить действие.

### 0363 · voice.483f4db379a3c927

<!-- LOPATA.Events: ["localization:Generation.Examples.CopyFailed"] -->

- Не удалось скопировать. Выделите текст и попробуйте снова.

### 0364 · voice.0347a5da5a3045da

<!-- LOPATA.Events: ["localization:Generation.Generating"] -->

- Создание изображения.

### 0365 · voice.13e0ba9eb8e9bb5a

<!-- LOPATA.Events: ["localization:Generation.InvalidOutputSize"] -->

- Выбран неподдерживаемый выходной размер.

### 0366 · voice.012ad25a2303687f

<!-- LOPATA.Events: ["localization:Generation.Lora.Cancelled"] -->

- Операция остановлена. Скачанные части сохранены.

### 0367 · voice.b128d398934366f6

<!-- LOPATA.Events: ["localization:Generation.Lora.Failed"] -->

- Не удалось завершить действие.

### 0368 · voice.981427a9b1830b78

<!-- LOPATA.Events: ["localization:Generation.Lora.Invalid"] -->

- Данные LoRA повреждены или неполны.

### 0369 · voice.8b45d0665adcf55d

<!-- LOPATA.Events: ["localization:Generation.Lora.NoLicense"] -->

- У автора не найдены условия использования.

### 0370 · voice.ce547b9c2ee5d96e

<!-- LOPATA.Events: ["localization:Generation.Lora.NoResults"] -->

- Подходящих дополнений не найдено. Можно изменить тему или вставить ссылку автора.

### 0371 · voice.b0467f19ad85f8d2

<!-- LOPATA.Events: ["localization:Generation.Lora.NotApplied"] -->

- Движок не смог полностью применить LoRA. Изображение не выдано как успешный результат.

### 0372 · voice.d249aa61af021323

<!-- LOPATA.Events: ["localization:Generation.Lora.Removed", "localization:Models.Filter.Removed", "localization:Models.Status.files_removed"] -->

- Файлы удалены.

### 0373 · voice.eec17b50cb516891

<!-- LOPATA.Events: ["localization:Generation.Lora.SearchFinished"] -->

- Поиск завершён. Новые находки добавлены в каталог.

### 0374 · voice.63ae06676e7d124f

<!-- LOPATA.Events: ["localization:Generation.Lora.UsePicker"] -->

- Для подключения LoRA используйте кнопку LoRA под полем запроса. Служебные команды LoRA в тексте запроса не поддерживаются.

### 0375 · voice.d19d2b64b516aebf

<!-- LOPATA.Events: ["localization:Generation.MissingImage"] -->

- Файл изображения перемещён или удалён.

### 0376 · voice.8b2a5b788e831e89

<!-- LOPATA.Events: ["localization:Generation.ModelUnavailable"] -->

- Эта модель исключена из сценария. Сохранённые изображения доступны; для новой генерации выберите другую модель.

### 0377 · voice.5787174d564b8a04

<!-- LOPATA.Events: ["localization:Generation.OutputNotReady"] -->

- Обработка изображения ещё не завершена.

### 0378 · voice.eb1d85c4447c3255

<!-- LOPATA.Events: ["localization:Generation.Paused"] -->

- На паузе. Модель освобождена.

### 0379 · voice.58ae6f19352fc447

<!-- LOPATA.Events: ["localization:Generation.Preparation.Missing", "localization:LetItOut.Preparation.MissingComponent", "localization:Literary.Prepare.Missing", "localization:Vector.Preparation.Missing"] -->

- Требуется подготовка.

### 0380 · voice.4de382cb8969e46a

<!-- LOPATA.Events: ["localization:Generation.Preparation.Ready", "localization:Literary.Prepare.AllReady", "localization:Vector.Preparation.Ready"] -->

- Всё готово. Можно продолжить.

### 0381 · voice.d31575860a86f642

<!-- LOPATA.Events: ["localization:Generation.ProcessingFailed"] -->

- Не удалось обработать изображение. Исходник сохранён.

### 0382 · voice.87192e03f35f9d80

<!-- LOPATA.Events: ["localization:Generation.ReferenceChanged"] -->

- Сохранённый референс изменился. Добавьте изображение ещё раз.

### 0383 · voice.c2e1c13381692b88

<!-- LOPATA.Events: ["localization:Generation.ReferenceEmptyReply"] -->

- Бета не вернула готовый фрагмент референса. Черновик сохранён.

### 0384 · voice.43446d3bc78cb235

<!-- LOPATA.Events: ["localization:Generation.ReferenceFailed"] -->

- Не удалось обработать референс. Черновик сохранён.

### 0385 · voice.595631b8ca2840f9

<!-- LOPATA.Events: ["localization:Generation.ReferenceInvalidReceipt"] -->

- Не удалось прочитать сохранённый фрагмент референса. Черновик сохранён.

### 0386 · voice.d5db83afc1eae4ea

<!-- LOPATA.Events: ["localization:Generation.ReferenceNotReady"] -->

- Подготовка Беты не завершена. Черновик сохранён.

### 0387 · voice.0b95b80bdc7c3431

<!-- LOPATA.Events: ["localization:Generation.RuntimeError"] -->

- Движок завершился с ошибкой. Подробности сохранены в журнале рядом с результатами.

### 0388 · voice.4b1d166bb7d36d64

<!-- LOPATA.Events: ["localization:Generation.VerificationFailed"] -->

- Проверка комплекта не пройдена.

### 0389 · voice.f4eebe928ebf7c00

<!-- LOPATA.Events: ["localization:Generation.Verifying"] -->

- Проверка файлов.

### 0390 · voice.34f7403ec4252a89

<!-- LOPATA.Events: ["localization:HardwareRuntime.CheckFailed"] -->

- Не удалось проверить оборудование и библиотеки. Повторите попытку.

### 0391 · voice.8f2da894d3a7b47c

<!-- LOPATA.Events: ["localization:HardwareRuntime.DownloadFailed"] -->

- Библиотеки не удалось подготовить. Можно повторить загрузку.

### 0392 · voice.6f7dae119fc5d590

<!-- LOPATA.Events: ["localization:HardwareRuntime.JellyCpuMemory"] -->

- Желе: обработка на процессоре — недостаточно свободной памяти видеокарты.

### 0393 · voice.fe79037d7b6b4584

<!-- LOPATA.Events: ["localization:HardwareRuntime.Missing"] -->

- Требуется подготовка библиотек для оборудования.

### 0394 · voice.f48b7eba9674aa4c

<!-- LOPATA.Events: ["localization:HardwareRuntime.Paused"] -->

- Загрузка библиотек приостановлена. Её можно продолжить.

### 0395 · voice.921e2903a3c4aa19

<!-- LOPATA.Events: ["localization:HardwareRuntime.Preparing"] -->

- Подготовка библиотек для оборудования.

### 0396 · voice.c44ed4d9d070586a

<!-- LOPATA.Events: ["localization:ImageAnalysis.Complete.BackupQuestion"] -->

- Завершить работу и сохранить её в истории? Создать внутреннюю резервную копию исходного изображения и финального описания?

### 0397 · voice.66871c7d79310fb8

<!-- LOPATA.Events: ["localization:ImageAnalysis.Context.RetryFailed"] -->

- Не удалось завершить создание описания. Попробуйте перезапустить программу и повторить анализ.

### 0398 · voice.d79e7fbf769ac81e

<!-- LOPATA.Events: ["localization:ImageAnalysis.Heavy.InvalidResultFormat"] -->

- Модель завершила ответ, но его формат не удалось разобрать. Исходный ответ сохранён в папке сессии OmniResponses; предыдущие версии не изменены.

### 0399 · voice.dcecd5347c1e192b

<!-- LOPATA.Events: ["localization:ImageAnalysis.Heavy.MemoryPressure.Message"] -->

- Сейчас свободной памяти заметно меньше, чем после прогрева. Это может замедлить следующий запрос.

### 0400 · voice.a0d8816120641d13

<!-- LOPATA.Events: ["localization:ImageAnalysis.Install.Active.ComponentStatus"] -->

- Загрузка идёт.

### 0401 · voice.1b0c4fb1d1bdea8d

<!-- LOPATA.Events: ["localization:ImageAnalysis.Install.Runtime.Completed"] -->

- Пробный запуск этой модели успешно завершён.

### 0402 · voice.8da58bf5e68df4d3

<!-- LOPATA.Events: ["localization:ImageAnalysis.Install.Unavailable"] -->

- Действие недоступно.

### 0403 · voice.0a687d819910df96

<!-- LOPATA.Events: ["localization:ImageAnalysis.Install.Verification.DoNotClose"] -->

- Не закрывайте ЛОПАТА до появления состояния «Комплект готов» или понятного сообщения об ошибке.

### 0404 · voice.751306e158dcdad9

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Activity.Completed"] -->

- Работа завершена и сохранена в истории.

### 0405 · voice.77a1bc7f4c35310a

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Activity.Ready"] -->

- Параметры готовы. Анализ начнётся только по вашей команде.

### 0406 · voice.3adc3c40fc1046e6

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Activity.ResultReady"] -->

- Описание готово. Можно создать новую версию, посмотреть или экспортировать результат.

### 0407 · voice.e8aa69a824006947

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Activity.RevisionActive"] -->

- Ядро ЛОПАТА готовит новую версию текста.

### 0408 · voice.b21d5856e00af5bd

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Activity.WaitingFile"] -->

- Ожидается проверенное изображение.

### 0409 · voice.2c3c739c183d447e

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Events.ExportCompleted"] -->

- Документ экспортирован.

### 0410 · voice.80a15cd91252f3a2

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Events.OperationFailed"] -->

- Операция завершилась ошибкой.

### 0411 · voice.840213d320a60253

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Events.RevisionReady"] -->

- Новая версия готова.

### 0412 · voice.3a7a65eb0fa683fc

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Events.SessionCompleted"] -->

- Работа завершена и сохранена.

### 0413 · voice.81bcc7390e3b1bb1

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Events.VisionCompleted"] -->

- Внутренний визуальный отчёт готов.

### 0414 · voice.56574407abc89396

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.FileCancelled"] -->

- Проверка файла отменена.

### 0415 · voice.56b2a87b89cde70d

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.FileReady"] -->

- Файл читается и готов к локальному анализу.

### 0416 · voice.bc4690ee97575432

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.HeavyVoice.AudioReady"] -->

- Речь Omni создана.

### 0417 · voice.6f1362fb9ffd63e7

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.HeavyVoice.Error"] -->

- Не удалось озвучить сводку. Режим голоса не изменён; выберите другой режим вручную.

### 0418 · voice.e9af42bd4a884425

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.HeavyVoice.OmniReady"] -->

- Встроенный голос Omni готов.

### 0419 · voice.cf36ad0822dfb2ce

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Image.NoFile"] -->

- Изображение не выбрано.

### 0420 · voice.b0957f1bed9781b9

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Status.ChooseSubscenario"] -->

- Выберите ветку анализа или вернитесь к сохранённой работе.

### 0421 · voice.986f0c7a8610c901

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Status.Completed"] -->

- Работа завершена и остаётся доступной в истории.

### 0422 · voice.231463ee7acf0e76

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Status.ReadOnlyHistory"] -->

- Модель режима недоступна. Историю можно читать, просматривать и экспортировать, но продолжить анализ нельзя.

### 0423 · voice.61b3eb910d4a56de

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Status.ResultReady"] -->

- Описание готово и сохранено как новая версия.

### 0424 · voice.8c359c67cc7bfe9b

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.DownloadCancelled"] -->

- Загрузка голоса приостановлена.

### 0425 · voice.6b6adc670b54bed0

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.DownloadFailed"] -->

- Не удалось загрузить модель голоса.

### 0426 · voice.c1d1d77860f3b780

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.Downloading"] -->

- Загружаем голос.

### 0427 · voice.5e65d3e64dacf297

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.Failed"] -->

- Не удалось озвучить сводку.

### 0428 · voice.640e7938c1bc608c

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.KokoroReady"] -->

- Kokoro прогрета и готова.

### 0429 · voice.39a8edabd6342468

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.ModelMissing"] -->

- Kokoro для текущего языка не загружена.

### 0430 · voice.234c508f928e758a

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.Preparing"] -->

- Готовим Kokoro в RAM.

### 0431 · voice.8248afa1a7a3d36e

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.RuntimeMissing"] -->

- Среда Kokoro пока не готова; доступна программная читалка.

### 0432 · voice.359b1f9f6634bd68

<!-- LOPATA.Events: ["localization:ImageAnalysis.Workspace.Voice.Synthesizing"] -->

- Создаём речь.

### 0433 · voice.8c5ba95a00e9d315

<!-- LOPATA.Events: ["localization:ImageInput.ClipboardBusy"] -->

- Буфер обмена сейчас недоступен. Попробуйте вставить ещё раз.

### 0434 · voice.1fa2b67b3e74e856

<!-- LOPATA.Events: ["localization:ImageInput.DownloadFailed"] -->

- Не удалось скачать изображение. Проверьте ссылку и подключение.

### 0435 · voice.e6b6d2169c5c2150

<!-- LOPATA.Events: ["localization:ImageInput.Failed"] -->

- Не удалось прочитать или сохранить изображение. Предыдущая работа сохранена.

### 0436 · voice.d3096b0b0a18a51a

<!-- LOPATA.Events: ["localization:ImageInput.FolderError"] -->

- Не удалось открыть папку изображений. Проверьте доступность диска результатов.

### 0437 · voice.ac67609f22a096a4

<!-- LOPATA.Events: ["localization:ImageInput.Missing"] -->

- Исходное изображение удалено или недоступно. Сохранённый текст можно открыть и экспортировать.

### 0438 · voice.ead0aadbad280639

<!-- LOPATA.Events: ["localization:ImageInput.RetentionQuestion"] -->

- Создан временный файл изображения. Сохранить изображение после завершения работы?

### 0439 · voice.8abb639fc7202b60

<!-- LOPATA.Events: ["localization:ImageInput.Timeout"] -->

- Не удалось получить изображение за 30 секунд. Попробуйте ещё раз.

### 0440 · voice.755e7d9b87d3c8dc

<!-- LOPATA.Events: ["localization:ImageShell.Error.ResultChanged"] -->

- Готовый файл изменился или недоступен. Повторная обработка остановлена; резервный исходник сохранён, если он ещё был нужен.

### 0441 · voice.ab00a09f31e4c8c9

<!-- LOPATA.Events: ["localization:ImageUtility.Ai.InvalidParameters", "localization:VideoPrep.H3SettingsInvalid", "localization:VideoPrep.VaceWorkspace.SettingsInvalid"] -->

- Настройки задания некорректны. Проверьте выделенные поля.

### 0442 · voice.cf889c6ad3f6499c

<!-- LOPATA.Events: ["localization:ImageUtility.Ai.Loading"] -->

- Загрузка ИИ-модели.

### 0443 · voice.5a56a4576d4cf7f9

<!-- LOPATA.Events: ["localization:ImageUtility.Ai.NoCompatibleDevice"] -->

- Выбранное устройство недоступно этому обработчику. Проверьте выбор устройства и драйвер Vulkan.

### 0444 · voice.b5b164c5decc2d81

<!-- LOPATA.Events: ["localization:ImageUtility.Ai.Processing"] -->

- ИИ обрабатывает изображение.

### 0445 · voice.106789d77b0e120b

<!-- LOPATA.Events: ["localization:ImageUtility.Ai.VerificationFailed"] -->

- Не удалось подтвердить целостность компонентов ИИ.

### 0446 · voice.cfd211a6acc4983d

<!-- LOPATA.Events: ["localization:ImageUtility.Ai.WorkerFailed"] -->

- Не удалось обработать изображение с помощью ИИ. Проверьте параметры, память и драйвер видеокарты.

### 0447 · voice.b49512e1e40a7bff

<!-- LOPATA.Events: ["localization:ImageUtility.Downloading"] -->

- Подготовка загрузки.

### 0448 · voice.56a4c90579d656c8

<!-- LOPATA.Events: ["localization:ImageUtility.Error"] -->

- Не удалось выполнить действие.

### 0449 · voice.fe8dc10ffa45ab1e

<!-- LOPATA.Events: ["localization:ImageUtility.Error.AiAnimation"] -->

- Анимация пропущена: ИИ-обработка анимаций пока не поддерживается.

### 0450 · voice.25dd6adb81ca735c

<!-- LOPATA.Events: ["localization:ImageUtility.Error.AiMissing"] -->

- ИИ-инструмент не установлен.

### 0451 · voice.e85f1b47d6da9fda

<!-- LOPATA.Events: ["localization:ImageUtility.Error.AnimationFormat"] -->

- Анимация пропущена: выбранный формат не сохраняет анимацию.

### 0452 · voice.dd36cded559e840a

<!-- LOPATA.Events: ["localization:ImageUtility.Error.ApngUnsupported"] -->

- APNG пропущен: обработка этого вида анимации пока не поддерживается.

### 0453 · voice.ea7be38235e6a1e7

<!-- LOPATA.Events: ["localization:ImageUtility.Error.Dimensions"] -->

- Не удалось определить размеры изображения.

### 0454 · voice.1e70ad4da94b3250

<!-- LOPATA.Events: ["localization:ImageUtility.Error.InvalidImage", "localization:OfficeEditor.InvalidImage"] -->

- Не удалось прочитать изображение.

### 0455 · voice.104409990d838e1a

<!-- LOPATA.Events: ["localization:ImageUtility.Error.Method"] -->

- Неизвестный способ обработки.

### 0456 · voice.9587f53deb8b73b6

<!-- LOPATA.Events: ["localization:ImageUtility.Error.MultiPage"] -->

- Многостраничный файл пропущен: этот тип последовательности пока не поддерживается.

### 0457 · voice.3c44607244e1e810

<!-- LOPATA.Events: ["localization:ImageUtility.Error.NoResult"] -->

- Обработка не создала изображение.

### 0458 · voice.15226ae0e26cc310

<!-- LOPATA.Events: ["localization:ImageUtility.Error.ResultDimensions"] -->

- Проверка результата не прошла: размеры или число кадров изменились неожиданно.

### 0459 · voice.2459f8068716b4a9

<!-- LOPATA.Events: ["localization:ImageUtility.Error.RuntimeMissing"] -->

- Встроенный обработчик изображений отсутствует. Восстановите файлы приложения.

### 0460 · voice.9020c06e28df6302

<!-- LOPATA.Events: ["localization:ImageUtility.Error.SourceMissing"] -->

- Исходный файл недоступен.

### 0461 · voice.decd16e77a26f44e

<!-- LOPATA.Events: ["localization:ImageUtility.Error.Start"] -->

- Не удалось запустить обработчик изображений.

### 0462 · voice.ab23e4eb85145287

<!-- LOPATA.Events: ["localization:ImageUtility.Event.Finished"] -->

- Обработка завершена.

### 0463 · voice.489870b3b5e4b4ad

<!-- LOPATA.Events: ["localization:ImageUtility.Finished"] -->

- Обработка очереди завершена.

### 0464 · voice.dc2e76eb83f74ef0

<!-- LOPATA.Events: ["localization:ImageUtility.Installed"] -->

- Готов к работе.

### 0465 · voice.4e74dc85e10d4867

<!-- LOPATA.Events: ["localization:ImageUtility.MissingJob"] -->

- Сохранённая очередь не найдена.

### 0466 · voice.71605da953c17f59

<!-- LOPATA.Events: ["localization:ImageUtility.PythonRequired"] -->

- Дополнительно потребуется общий компонент Python / PyTorch. Уже установленные совместимые компоненты будут использованы повторно.

### 0467 · voice.0f24fdfa7712ed19

<!-- LOPATA.Events: ["localization:ImageUtility.Ready", "localization:Music.Status.Idle"] -->

- Готово к работе.

### 0468 · voice.19bd058b9bf68b8c

<!-- LOPATA.Events: ["localization:ImageUtility.Status.Failed", "localization:LetItOut.AiState.Error", "localization:Music.Poetry.Search.State.error", "localization:Music.Projects.Failed", "localization:Music.Status.Error", "localization:OfficeEditor.Sheet.Failure", "localization:Processes.Error", "localization:Vector.Error", "localization:VideoPrep.H3File.failed"] -->

- Ошибка.

### 0469 · voice.6a6603626d3289a3

<!-- LOPATA.Events: ["localization:ImageUtility.Status.Pending"] -->

- Ожидает обработки.

### 0470 · voice.9bf491d9e388c60d

<!-- LOPATA.Events: ["localization:ImageUtility.Status.Running"] -->

- Обрабатывается.

### 0471 · voice.437e4a9c427f21cc

<!-- LOPATA.Events: ["localization:LetItOut.AiState.Loading"] -->

- Загружается.

### 0472 · voice.c4883e440ba9384f

<!-- LOPATA.Events: ["localization:LetItOut.AiState.NotReady"] -->

- Не готов.

### 0473 · voice.2c21c3fde7b306e5

<!-- LOPATA.Events: ["localization:LetItOut.AiState.Stopped", "localization:Processes.State.Stopped"] -->

- Остановлен.

### 0474 · voice.6dfa633a8b175fcf

<!-- LOPATA.Events: ["localization:LetItOut.AiState.Waiting"] -->

- Ожидает.

### 0475 · voice.3717c330a4c1e09a

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.AudioChanged"] -->

- Запись или версия распознавателя изменились. Сохранённая расшифровка требует отдельной проверки.

### 0476 · voice.3ecaef9ead773dd8

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.AudioInvalid"] -->

- Не удалось прочитать звуковую дорожку записи. Исходный файл сохранён.

### 0477 · voice.70d6ccce1ea5c15c

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.AudioNotice"] -->

- Звуковой файл будет обработан распознавателем речи. В чат попадёт автоматическая расшифровка, где возможны ошибки.

### 0478 · voice.4603ab968a9ac8f4

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.AudioPending"] -->

- Для записи нужно подготовить распознаватель речи. Прикреплённый файл сохранён.

### 0479 · voice.234978a8f8435eb6

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.Encoding"] -->

- Не удалось прочитать кодировку. Сохраните текст в UTF-8 или UTF-16.

### 0480 · voice.470f6e0f1e510939

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.Failed"] -->

- Не удалось добавить или прочитать материал. Текст сообщения сохранён.

### 0481 · voice.11501277f33bc633

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.Invalid"] -->

- Не удалось прочитать вложение.

### 0482 · voice.6f685b8b9c577712

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.Missing"] -->

- Вложение недоступно.

### 0483 · voice.0aaa98bd062f56c6

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.NoText"] -->

- В документе не найден текст. Скан можно прикрепить изображением.

### 0484 · voice.4e6e061535e3eefd

<!-- LOPATA.Events: ["localization:LetItOut.Attachments.Unsupported"] -->

- Этот тип файла пока не поддерживается. Добавьте изображение или текстовый документ.

### 0485 · voice.ab79008f53519cd9

<!-- LOPATA.Events: ["localization:LetItOut.Busy"] -->

- Другая задача выполняется или ждёт решения. Завершите её или воспользуйтесь общим переключателем работы ИИ.

### 0486 · voice.c4fd0b3d7e92795a

<!-- LOPATA.Events: ["localization:LetItOut.Context.TrimFailed"] -->

- Не удалось освободить запас контекста. Переписка сохранена.

### 0487 · voice.450d1f5e346764ad

<!-- LOPATA.Events: ["localization:LetItOut.ContextFull"] -->

- Текущее сообщение или необходимый источник не помещается в доступное окно ИИ. Сократите сообщение.

### 0488 · voice.de248de245f12ce8

<!-- LOPATA.Events: ["localization:LetItOut.Export.Failed"] -->

- Не удалось сохранить архив. Исходная тема и прежний файл сохранены.

### 0489 · voice.fd9d180bb826a3e8

<!-- LOPATA.Events: ["localization:LetItOut.Export.Saved"] -->

- Архив темы сохранён.

### 0490 · voice.7126f63e2158ecca

<!-- LOPATA.Events: ["localization:LetItOut.Failed"] -->

- Ответ не завершён. Текст сохранён на экране; проверьте состояние задачи в общем управлении ИИ.

### 0491 · voice.1632b9a311caa4f5

<!-- LOPATA.Events: ["localization:LetItOut.Library.SaveFailed"] -->

- Не удалось сохранить историю. Переписка остаётся в открытом окне.

### 0492 · voice.1b37a036657bdceb

<!-- LOPATA.Events: ["localization:LetItOut.MemorySaving"] -->

- Сохраняем память разговора.

### 0493 · voice.86e6cd4e91572428

<!-- LOPATA.Events: ["localization:LetItOut.MemorySummaryFailed"] -->

- Не удалось подготовить общую сводку. Исходная переписка сохранена.

### 0494 · voice.d00ea57a28f14451

<!-- LOPATA.Events: ["localization:LetItOut.Mode.LoadingCpu"] -->

- Загрузка в ОЗУ для CPU.

### 0495 · voice.6e0f93069c406326

<!-- LOPATA.Events: ["localization:LetItOut.Mode.LoadingGpu"] -->

- Загрузка на видеокарту.

### 0496 · voice.a1fa7e59c358ef95

<!-- LOPATA.Events: ["localization:LetItOut.Mode.LoadingHybrid"] -->

- Загрузка в гибридном режиме.

### 0497 · voice.dcfbe7ec4f7b2a91

<!-- LOPATA.Events: ["localization:LetItOut.ModelChanged"] -->

- В этом незавершённом ответе использовалось прежнее ядро. Переписка сохранена на экране.

### 0498 · voice.81f09edefb9e54c6

<!-- LOPATA.Events: ["localization:LetItOut.Paused"] -->

- Ответ на паузе. Продолжить можно через общий переключатель работы ИИ.

### 0499 · voice.5ea223eb41ddeaca

<!-- LOPATA.Events: ["localization:LetItOut.Preparation.Cancelled"] -->

- Подготовка отменена.

### 0500 · voice.f9766a3b3929df78

<!-- LOPATA.Events: ["localization:LetItOut.Preparation.DownloadingCore"] -->

- Скачивание Gemma.

### 0501 · voice.76dc60f8f4c220b2

<!-- LOPATA.Events: ["localization:LetItOut.Preparation.DownloadingRuntime"] -->

- Подготовка движка.

### 0502 · voice.95367d6e04a6f9f3

<!-- LOPATA.Events: ["localization:LetItOut.Preparation.Missing"] -->

- Gemma отсутствует или требует восстановления.

### 0503 · voice.06b6b89f3cc84b44

<!-- LOPATA.Events: ["localization:LetItOut.Preparation.Ready"] -->

- Всё готово. Можно открыть чат.

### 0504 · voice.bad2b45daf60ecb5

<!-- LOPATA.Events: ["localization:LetItOut.Preparation.SpaceRequired"] -->

- Недостаточно места для Gemma. Освободите место или выберите другое хранилище.

### 0505 · voice.a2381c9628bd0e7f

<!-- LOPATA.Events: ["localization:LetItOut.PreparationFailed"] -->

- Не удалось подготовить чат. Повторите вход и проверку компонентов.

### 0506 · voice.5321a4510d1f38a6

<!-- LOPATA.Events: ["localization:LetItOut.RamRequired"] -->

- Недостаточно свободной оперативной памяти для модели и рабочего запаса. Закройте тяжёлые программы и повторите через общее управление ИИ.

### 0507 · voice.e434c401db93409c

<!-- LOPATA.Events: ["localization:LetItOut.Ready"] -->

- Можно писать.

### 0508 · voice.37d54d70fa901a37

<!-- LOPATA.Events: ["localization:LetItOut.Stopped"] -->

- Ответ остановлен. Можно написать следующее сообщение.

### 0509 · voice.cec2f072dda901c2

<!-- LOPATA.Events: ["localization:LetItOut.Tree.Failed"] -->

- Не удалось обновить карту. Переписка и прежняя карта сохранены.

### 0510 · voice.d381332e96e90d03

<!-- LOPATA.Events: ["localization:LetItOut.Tree.NotFound", "localization:OfficeEditor.NotFound"] -->

- Совпадений нет.

### 0511 · voice.1bd9e6900230aeb5

<!-- LOPATA.Events: ["localization:LetItOut.Tree.Status.completed"] -->

- Состоялось.

### 0512 · voice.dadd8717411bc97b

<!-- LOPATA.Events: ["localization:LetItOut.VramChanged"] -->

- Свободная видеопамять опустилась ниже резерва 2 ГиБ или её расход нельзя проверить. Ответ остановлен, переписка сохранена.

### 0513 · voice.da391a5021d9d3d0

<!-- LOPATA.Events: ["localization:LetItOut.WarmFailed"] -->

- Не удалось загрузить Gemma. Проверьте задачу в общем управлении ИИ.

### 0514 · voice.8d8c958a28ec3416

<!-- LOPATA.Events: ["localization:LetItOut.WarmPaused"] -->

- Загрузка Gemma приостановлена. Продолжить можно через общее управление ИИ.

### 0515 · voice.97c898c59be87ac4

<!-- LOPATA.Events: ["localization:LetItOut.WarmStopped"] -->

- Загрузка Gemma остановлена. Она начнётся снова при отправке сообщения или повторном входе в чат.

### 0516 · voice.5ff20764fb03dfd8

<!-- LOPATA.Events: ["localization:LetItOut.Warming"] -->

- Загружаем Gemma. Пока можно написать сообщение.

### 0517 · voice.e9fec5d21857998b

<!-- LOPATA.Events: ["localization:LetItOut.Working"] -->

- Gemma готовит ответ.

### 0518 · voice.9118d7a72a13c24e

<!-- LOPATA.Events: ["localization:Literary.Advisor.Error"] -->

- Не удалось получить ответ ИИ2. Подробности в журнале LiteraryShared.

### 0519 · voice.15c4a616331285a1

<!-- LOPATA.Events: ["localization:Literary.Analysis.Cancelled"] -->

- Анализ остановлен.

### 0520 · voice.77d2980bfbdf3e2b

<!-- LOPATA.Events: ["localization:Literary.Analysis.Failed"] -->

- Не удалось получить корректные замечания. Ваш текст остаётся в редакторе.

### 0521 · voice.2802dd9b7ae82832

<!-- LOPATA.Events: ["localization:Literary.Anchor.CompressDone"] -->

- Ответ готов. Проверьте смысл перед копированием; точный объём покажут счётчики в форме.

### 0522 · voice.ac69a0cb3f73661d

<!-- LOPATA.Events: ["localization:Literary.Anchor.LoadError"] -->

- Не удалось прочитать сюжетный якорь. Проверьте доступность его файла в папке проекта.

### 0523 · voice.94b798bedb50824a

<!-- LOPATA.Events: ["localization:Literary.Anchor.SaveError"] -->

- Не удалось сохранить якорь: проверьте доступ к проекту. Если файл изменён другим окном, скопируйте свои правки и откройте якорь заново.

### 0524 · voice.41832392002cf705

<!-- LOPATA.Events: ["localization:Literary.BookReview.ExportFailed"] -->

- Правки сохранены, но DOCX не обновлён. Закройте файл DOCX и повторите сохранение.

### 0525 · voice.309acffdb31acece

<!-- LOPATA.Events: ["localization:Literary.BookReview.NoMarks"] -->

- Непроверенных пометок нет. Это не заменяет прочтение всей книги.

### 0526 · voice.12d7995a139c2d5e

<!-- LOPATA.Events: ["localization:Literary.BookReview.NotFound"] -->

- Текст не найден.

### 0527 · voice.ad3a872d72f2283f

<!-- LOPATA.Events: ["localization:Literary.BookReview.PositionFailed"] -->

- Не удалось сохранить место чтения.

### 0528 · voice.898159d84e648528

<!-- LOPATA.Events: ["localization:Literary.BookReview.SaveFailed"] -->

- Не удалось сохранить книгу.

### 0529 · voice.ebe2fa9accedf94e

<!-- LOPATA.Events: ["localization:Literary.BookReview.Saved", "localization:Literary.Draft.Saved", "localization:Literary.Import.Jelly.Saved", "localization:SongClip.MotionSaved", "localization:Status.ExecutorExported", "localization:Status.ImageAnalysisExported", "localization:Status.ModelDownloadConnectionsSaved", "localization:Status.StorageSavedComplete", "localization:Status.TextSizeSaved", "localization:Status.WindowStartupSaved"] -->

- Результат сохранён. Подробности показаны в окне программы.

### 0530 · voice.17fa051352809a76

<!-- LOPATA.Events: ["localization:Literary.BookReview.Saving"] -->

- Сохраняем правки.

### 0531 · voice.a6188f4ec6a346e7

<!-- LOPATA.Events: ["localization:Literary.BookReview.SourceUnavailable"] -->

- У этой пометки нет отдельного исходного фрагмента. Проверьте изменённый текст книги.

### 0532 · voice.2df91aad1e72cd61

<!-- LOPATA.Events: ["localization:Literary.Calibration.ReadError"] -->

- Не удалось открыть результат. Проверьте доступность папки проекта.

### 0533 · voice.5195c4b162ddd49d

<!-- LOPATA.Events: ["localization:Literary.Calibration.SaveError"] -->

- Не удалось сохранить. Возможно, проект изменился или папка недоступна.

### 0534 · voice.9eda942f2bb1d958

<!-- LOPATA.Events: ["localization:Literary.Calibration.Saved", "localization:Metadata.Saved"] -->

- Изменения сохранены.

### 0535 · voice.175891642d9e59ab

<!-- LOPATA.Events: ["localization:Literary.Context.Limited"] -->

- Ответ готов. Материалы прочитаны не полностью: некоторые источники недоступны или достигнут предел чтения/контекста.

### 0536 · voice.6971dd073792edc6

<!-- LOPATA.Events: ["localization:Literary.Context.SourceError"] -->

- Не удалось получить актуальные материалы проекта. Дождитесь сохранения или откройте проект заново.

### 0537 · voice.42b1eb37ba7e35ce

<!-- LOPATA.Events: ["localization:Literary.Create.LoadError"] -->

- Не удалось прочитать список проектов.

### 0538 · voice.d922ef22cb5b15c3

<!-- LOPATA.Events: ["localization:Literary.Create.SaveError"] -->

- Не удалось сохранить. Проверьте доступ к папке и свободное место.

### 0539 · voice.0350043af575fec8

<!-- LOPATA.Events: ["localization:Literary.Create.Saving"] -->

- Сохраняем проект и материалы.

### 0540 · voice.57d89970bb46514b

<!-- LOPATA.Events: ["localization:Literary.Delete.FilesError"] -->

- Удаление не завершено. Проект остаётся в списке, но часть файлов уже могла быть удалена.

### 0541 · voice.756dba776325b50d

<!-- LOPATA.Events: ["localization:Literary.Delete.KeepError"] -->

- Не удалось убрать проект из списка. Файлы не удалялись.

### 0542 · voice.db99802d6c020b40

<!-- LOPATA.Events: ["localization:Literary.Dialog.LeaveError"] -->

- Не удалось сохранить переписку или ввод. Выйти без сохранения этих изменений?

### 0543 · voice.0a78074dd4f5e4d4

<!-- LOPATA.Events: ["localization:Literary.Dialog.SaveError"] -->

- Не удалось сохранить чат. Ввод оставлен на экране.

### 0544 · voice.3583be2ba70e77fe

<!-- LOPATA.Events: ["localization:Literary.Draft.LoadError"] -->

- Не удалось прочитать набросок. Редактирование отключено, чтобы не перезаписать файл.

### 0545 · voice.2c663760f035c964

<!-- LOPATA.Events: ["localization:Literary.Draft.Ready"] -->

- Рабочий набросок. Изменения сохраняются автоматически.

### 0546 · voice.0fb4f072317731a2

<!-- LOPATA.Events: ["localization:Literary.Draft.SaveError"] -->

- Не удалось сохранить текст. Проверьте доступность папки проекта.

### 0547 · voice.d6ad7777baab9b24

<!-- LOPATA.Events: ["localization:Literary.Draft.TokenLimit"] -->

- Набросок слишком объёмен для обработки с запасом на ответ. Немного сократите верхний текст; он сохранён целиком.

### 0548 · voice.0e0bb15d5bbe3369

<!-- LOPATA.Events: ["localization:Literary.Editor.ExportEmpty"] -->

- В проекте пока нет текста для экспорта.

### 0549 · voice.1c718758cc1fe655

<!-- LOPATA.Events: ["localization:Literary.Editor.ExportError"] -->

- Не удалось экспортировать проект. Проверьте доступность всех файлов и папки назначения.

### 0550 · voice.270adfc29d351e5d

<!-- LOPATA.Events: ["localization:Literary.Editor.Exported", "localization:OfficeEditor.Saved"] -->

- Документ сохранён.

### 0551 · voice.24b4f6197f382597

<!-- LOPATA.Events: ["localization:Literary.Editor.Saving", "localization:Metadata.Saving"] -->

- Сохраняем.

### 0552 · voice.28fa7045164de7b3

<!-- LOPATA.Events: ["localization:Literary.Import.Cancelled"] -->

- Импорт остановлен. Сохранённые шаги будут использованы при продолжении.

### 0553 · voice.ac0ac9a02e74f870

<!-- LOPATA.Events: ["localization:Literary.Import.Changed"] -->

- Текст изменён после импорта — требуется проверка всей части.

### 0554 · voice.23b115ce65e3439e

<!-- LOPATA.Events: ["localization:Literary.Import.Corrupt"] -->

- Данные сессии повреждены или изменены. Исходник не перезаписан.

### 0555 · voice.b54fe02529308ffd

<!-- LOPATA.Events: ["localization:Literary.Import.Coverage"] -->

- Ответ модели не учитывает все фрагменты. Шаг сохранён для повторной попытки.

### 0556 · voice.e7d58f2485a5f44d

<!-- LOPATA.Events: ["localization:Literary.Import.CyclicGraph"] -->

- В связях сообщений обнаружен цикл.

### 0557 · voice.6d7f8faeeb06f46f

<!-- LOPATA.Events: ["localization:Literary.Import.DiskFull"] -->

- Недостаточно свободного места. Освободите место и продолжите сессию.

### 0558 · voice.4c5992ba42b9e09d

<!-- LOPATA.Events: ["localization:Literary.Import.ExportRefreshed"] -->

- DOCX и XLSX обновлены с учётом ручной проверки.

### 0559 · voice.cba27a5289bec710

<!-- LOPATA.Events: ["localization:Literary.Import.Exported"] -->

- Книга и XLSX сохранены.

### 0560 · voice.ec876fb7c48c52d7

<!-- LOPATA.Events: ["localization:Literary.Import.Failed"] -->

- Не удалось завершить шаг. Сессия сохранена.

### 0561 · voice.cdcf5c54f6e977f1

<!-- LOPATA.Events: ["localization:Literary.Import.Jelly.Complete"] -->

- «Желе» создано. Факты сохранены и связаны с рабочими кусками.

### 0562 · voice.334a12a68eed7782

<!-- LOPATA.Events: ["localization:Literary.Import.Jelly.Failed"] -->

- Не удалось завершить создание памяти после повторных попыток. Можно поделиться отчётом в репозитории.

### 0563 · voice.fc6bbae5ceb53b4c

<!-- LOPATA.Events: ["localization:Literary.Import.Jelly.LoadFailed"] -->

- Не удалось открыть или продолжить подготовку памяти. Проверьте доступность проекта и готовность рабочих кусков.

### 0564 · voice.702b5c268dfca7b9

<!-- LOPATA.Events: ["localization:Literary.Import.Jelly.OpenFailed", "localization:Literary.Import.RagStages.OpenFailed"] -->

- Не удалось открыть папку или ссылку.

### 0565 · voice.abe58f3dd668e1d0

<!-- LOPATA.Events: ["localization:Literary.Import.Jelly.ReviewSaved"] -->

- Решения сохранены.

### 0566 · voice.dd094e5e75ba5d25

<!-- LOPATA.Events: ["localization:Literary.Import.Jelly.Starting"] -->

- Проверяем сохранённую память и готовим извлечение фактов.

### 0567 · voice.7144e5b78d3387cd

<!-- LOPATA.Events: ["localization:Literary.Import.Jelly.Stopped"] -->

- Остановлено. Готовые фрагменты сохранены; можно продолжить.

### 0568 · voice.ff9808a120ced794

<!-- LOPATA.Events: ["localization:Literary.Import.Limit"] -->

- Экспорт превышает допустимый размер или глубину обработки.

### 0569 · voice.61a822ff58c41e56

<!-- LOPATA.Events: ["localization:Literary.Import.NoBook"] -->

- Не удалось выделить текст произведения. Попробуйте другую беседу.

### 0570 · voice.2c1892cb5096f530

<!-- LOPATA.Events: ["localization:Literary.Import.NonePending"] -->

- Непроверенных частей нет.

### 0571 · voice.aa81c3748facd645

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.BookChanged"] -->

- Книга изменилась во время подготовки подсказки. Повторите запрос по обновлённому тексту.

### 0572 · voice.4950545067e0fbdc

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.Cancelled"] -->

- Подсказка отменена. Ваш ответ сохранён; можно продолжить самостоятельно.

### 0573 · voice.e5b152c1095e9a8d

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.InvalidState"] -->

- Не удалось прочитать сохранённые ответы. Исходные данные сохранены.

### 0574 · voice.c633cbe64035326a

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.OpenFailed"] -->

- Не удалось открыть оставшиеся вопросы.

### 0575 · voice.7a7804dfe11325a0

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.PreparingProposal"] -->

- Готовим предложение ответа.

### 0576 · voice.24d013e7dfb72f2e

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.RouteFailed"] -->

- Не удалось прочитать заголовки книги. Ваши строки сохранены; можно заполнить таблицу вручную или пропустить шаг.

### 0577 · voice.57a24e508c42b544

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.RouteLinkStale"] -->

- Связь с главой не удалось подтвердить: книга изменилась или недоступна. Случайный фрагмент не открыт.

### 0578 · voice.fd1a0e42624d0ac6

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.SaveFailed"] -->

- Не удалось сохранить ответ. Переход не выполнен; введённый текст остался на экране.

### 0579 · voice.602459c8bafd5f14

<!-- LOPATA.Events: ["localization:Literary.Import.PostQuestions.SuggestionFailed"] -->

- Не удалось подготовить подсказку. Ваш ответ сохранён; можно ответить самостоятельно или повторить запрос.

### 0580 · voice.f2ea17674283e583

<!-- LOPATA.Events: ["localization:Literary.Import.QuestionSuggestionEmptyBook"] -->

- В исправленной книге нет текста для подсказки.

### 0581 · voice.4f7c11cffaeadb75

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.Checking"] -->

- Проверяем сохранённый индекс.

### 0582 · voice.bf32faafef10cd07

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.Failed"] -->

- Четыре попытки не помогли. Прогресс сохранён.

### 0583 · voice.bd7afe4c9241f2ca

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.FailedToStart"] -->

- Не удалось подготовить RAG. Проверьте доступность проекта и компонентов.

### 0584 · voice.30a39b62bc277e40

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.Retry"] -->

- Повторяем после сбоя.

### 0585 · voice.caeaba17a568d667

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.Saved"] -->

- Ранее созданные индексы сохранены. При продолжении проверим их и обновим, если материалы изменились.

### 0586 · voice.eb6d2fb846e6696e

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.Skipped"] -->

- Не подключён — пропущено.

### 0587 · voice.452bad9dc90a6ce4

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.Stopped"] -->

- Подготовка остановлена. Готовые этапы сохранены; можно продолжить.

### 0588 · voice.74740a764ae38364

<!-- LOPATA.Events: ["localization:Literary.Import.RagStages.Working"] -->

- Подготавливаем индекс.

### 0589 · voice.eb762f86ac891f5d

<!-- LOPATA.Events: ["localization:Literary.Import.RecentLoadFailed"] -->

- Не удалось открыть список черновиков.

### 0590 · voice.157ac2112a74784f

<!-- LOPATA.Events: ["localization:Literary.Import.RecentRemoveFailed"] -->

- Не удалось удалить запись из списка. Попробуйте ещё раз.

### 0591 · voice.f8027aa547f92ecf

<!-- LOPATA.Events: ["localization:Literary.Import.RecentSaveFailed"] -->

- Не удалось сохранить черновик.

### 0592 · voice.d6d5b0f3f52fa454

<!-- LOPATA.Events: ["localization:Literary.Import.ReviewFirst"] -->

- Сначала проверьте сомнительный фрагмент импортированного текста.

### 0593 · voice.eda05f47c57a4c80

<!-- LOPATA.Events: ["localization:Literary.Import.RuntimeChanged"] -->

- Модель или её обработчик изменились. Для продолжения верните прежний компонент либо начните новую сессию; сохранённые результаты остаются доступны.

### 0594 · voice.635d49fe976614b4

<!-- LOPATA.Events: ["localization:Literary.Import.SessionMissing"] -->

- Папка сессии импорта недоступна. Верните её на прежнее место для обновления полного XLSX.

### 0595 · voice.2ee0a53e44fb15c9

<!-- LOPATA.Events: ["localization:Literary.Import.StepReady"] -->

- Шаг завершён.

### 0596 · voice.2680efbbd450f514

<!-- LOPATA.Events: ["localization:Literary.Import.UnsafeArchive"] -->

- В архиве обнаружены небезопасные или конфликтующие имена файлов.

### 0597 · voice.03e2e94acf7935a5

<!-- LOPATA.Events: ["localization:Literary.Import.Unsupported"] -->

- Не найден поддерживаемый экспорт бесед DeepSeek.

### 0598 · voice.ee736637afbde3ed

<!-- LOPATA.Events: ["localization:Literary.Import.WorkingParts.FailedToStart"] -->

- Не удалось завершить подготовку или сохранить отчёт. Проверьте доступность папки проекта и свободное место.

### 0599 · voice.bf45989782fd907f

<!-- LOPATA.Events: ["localization:Literary.Import.WorkingParts.LoadFailed"] -->

- Не удалось восстановить готовые куски или черновик. Проверьте доступность проекта и повторите создание.

### 0600 · voice.8d11d5fc1dfad51d

<!-- LOPATA.Events: ["localization:Literary.Import.WorkingParts.OpenFailed"] -->

- Не удалось открыть ссылку или папку. Попробуйте ещё раз.

### 0601 · voice.480de6e5b85c9cf3

<!-- LOPATA.Events: ["localization:Literary.Import.WorkingParts.SaveFailed"] -->

- Не удалось сохранить границы. Повторите действие перед выходом.

### 0602 · voice.afa1801e0baf6c45

<!-- LOPATA.Events: ["localization:Literary.Import.WorkingParts.Stopped"] -->

- Остановлено. Сохранённые границы и файлы доступны для продолжения.

### 0603 · voice.149fbde0ad06e515

<!-- LOPATA.Events: ["localization:Literary.Import.Workspace.Failed"] -->

- Не удалось завершить переход. Сохранённые результаты доступны; можно повторить попытку или поделиться отчётом.

### 0604 · voice.b72ef02f147dedd4

<!-- LOPATA.Events: ["localization:Literary.Import.Workspace.Ready"] -->

- Проект готов. Можно вернуться к работе.

### 0605 · voice.33838d2315cbb3d6

<!-- LOPATA.Events: ["localization:Literary.Import.Workspace.Saved"] -->

- Место остановки сохранено. Можно продолжить переход.

### 0606 · voice.c7b0178a2e38e69a

<!-- LOPATA.Events: ["localization:Literary.Import.Workspace.SourceChanged"] -->

- Текст изменился во время подготовки перехода. Сохранённые правки не перезаписаны.

### 0607 · voice.f6569b8d9a2576a9

<!-- LOPATA.Events: ["localization:Literary.Interview.Error"] -->

- Не удалось выполнить действие. Ввод сохранён в форме.

### 0608 · voice.bfe31171b5ca75cb

<!-- LOPATA.Events: ["localization:Literary.Interview.LimitNotice"] -->

- Лимит контекста почти исчерпан. Дальнейшее создание проекта продолжится без ИИ.

### 0609 · voice.b1987d58778d202c

<!-- LOPATA.Events: ["localization:Literary.Interview.MissingSteps"] -->

- Для создания проекта ещё нужны ответы на пропущенные обязательные вопросы. Вернитесь к ним через F1/F2.

### 0610 · voice.fdf05d6709e58821

<!-- LOPATA.Events: ["localization:Literary.Interview.RecoveredAnswer"] -->

- Этот ответ остался неподтверждённым после сбоя. Здесь восстановлен ваш исходный текст.

### 0611 · voice.ed3310b56ab85594

<!-- LOPATA.Events: ["localization:Literary.Interview.Saved", "localization:Studio.Saved"] -->

- Сохранено.

### 0612 · voice.e00158a701c3335b

<!-- LOPATA.Events: ["localization:Literary.Jelly.Empty"] -->

- Подтверждённых фактов пока нет. Завершите часть произведения и проверьте предложенные записи.

### 0613 · voice.7117023147436988

<!-- LOPATA.Events: ["localization:Literary.Jelly.LoadError"] -->

- Не удалось открыть память произведения. Проверьте доступность проекта и повторите подготовку памяти.

### 0614 · voice.35fe42dca4f63a98

<!-- LOPATA.Events: ["localization:Literary.Jelly.Loading"] -->

- Загружаем память проекта.

### 0615 · voice.74a98fa94157be96

<!-- LOPATA.Events: ["localization:Literary.Jelly.SaveError"] -->

- Не удалось сохранить. Проверьте заполнение полей, тип знания и дословность цитаты.

### 0616 · voice.0e54ccdc2064e1b3

<!-- LOPATA.Events: ["localization:Literary.Loop.Recovered"] -->

- Ответ получен со второй попытки. Использована усиленная защита от повторов — проверьте дословные цитаты.

### 0617 · voice.bd226342e148436a

<!-- LOPATA.Events: ["localization:Literary.Loop.Retrying"] -->

- Обнаружен повторяющийся фрагмент. Первая попытка остановлена; пробуем заново с усиленной защитой от повторов.

### 0618 · voice.3893725528fb5d7a

<!-- LOPATA.Events: ["localization:Literary.Loop.Stopped"] -->

- Повторяющийся фрагмент обнаружен снова. Ответ остановлен и не добавлен в память беседы.

### 0619 · voice.8a2d15f37951df90

<!-- LOPATA.Events: ["localization:Literary.MemoryRecovery.Checking"] -->

- Повторно проверяем свободную видеопамять.

### 0620 · voice.cd204b519a683107

<!-- LOPATA.Events: ["localization:Literary.MemoryRecovery.GpuUnavailable"] -->

- Модели не удалось разместиться в видеопамяти даже с выбранным резервом. Запрос сохранён; освободите видеопамять и повторите.

### 0621 · voice.1b267e891ba7942b

<!-- LOPATA.Events: ["localization:Literary.MemoryRecovery.RamUnavailable"] -->

- Оперативной памяти недостаточно для безопасного резерва. Запрос сохранён; освободите память или измените параметры и повторите.

### 0622 · voice.c9c98e91f6cf1940

<!-- LOPATA.Events: ["localization:Literary.MemoryRecovery.UsingRam"] -->

- Подготавливаем резерв в оперативной памяти для этого запроса.

### 0623 · voice.7bdb31e12032a873

<!-- LOPATA.Events: ["localization:Literary.MemorySearch.Failed"] -->

- Не удалось обработать выбранные материалы после повторных попыток. Завершённые проходы и ваш запрос сохранены.

### 0624 · voice.4148aecbbf4dbc09

<!-- LOPATA.Events: ["localization:Literary.MemorySearch.Limit"] -->

- Не удалось уместить обязательные условия или все найденные сведения в доступное окно. Готовые проходы и запрос сохранены.

### 0625 · voice.6089c6afca27f731

<!-- LOPATA.Events: ["localization:Literary.Parameters.ReadError"] -->

- Не удалось прочитать параметры произведения.

### 0626 · voice.d0cdf8a1ec5e16b1

<!-- LOPATA.Events: ["localization:Literary.Prepare.Cancelled"] -->

- Обработка остановлена. Можно повторить.

### 0627 · voice.96e75a2ebff5e2b9

<!-- LOPATA.Events: ["localization:Literary.Prepare.Error"] -->

- Не удалось завершить подготовку.

### 0628 · voice.8b40e78378401512

<!-- LOPATA.Events: ["localization:Literary.Prepare.NeedsDownload"] -->

- Есть недостающие компоненты. Веса Giga — около 1 ГБ.

### 0629 · voice.4f7c505931afc08a

<!-- LOPATA.Events: ["localization:Literary.Project.Unavailable"] -->

- Папка проекта недоступна. Работа остановлена; текст на экране сохранён в памяти.

### 0630 · voice.ee1433de85403a72

<!-- LOPATA.Events: ["localization:Literary.Rag.Loading"] -->

- Загружаем модель индексации.

### 0631 · voice.6b2e1dd70d40e0ff

<!-- LOPATA.Events: ["localization:Literary.Rag.NoSources"] -->

- Материалы не добавлены. Индекс не требуется.

### 0632 · voice.746ea35823933285

<!-- LOPATA.Events: ["localization:Literary.Rag.NotReady"] -->

- Файл сохранён. Индекс не готов; можно повторить подготовку или продолжить с прямым чтением.

### 0633 · voice.c4769e060055fa2a

<!-- LOPATA.Events: ["localization:Literary.Rag.Ready"] -->

- Индекс готов: фрагментов.

### 0634 · voice.0b9e935237613b70

<!-- LOPATA.Events: ["localization:Literary.Readiness.Unavailable"] -->

- Не удалось получить показатели используемой видеокарты. Это не означает, что свободной памяти нет.

### 0635 · voice.f26b95fb901fb1b7

<!-- LOPATA.Events: ["localization:Literary.Workspace.Status"] -->

- Каркас рабочего экрана. Модели не подключены.

### 0636 · voice.e33734d90256342d

<!-- LOPATA.Events: ["localization:Literary.Writer.Cancelled"] -->

- Ответ остановлен. Незавершённый ответ не включён в память беседы.

### 0637 · voice.8c45eefe0e95cbf7

<!-- LOPATA.Events: ["localization:Literary.Writer.Context"] -->

- Для задания и ответа не хватает места. Сократите задание или очистите беседу критика, предварительно скопировав нужное.

### 0638 · voice.2d4185f09188a363

<!-- LOPATA.Events: ["localization:Literary.Writer.Error"] -->

- Не удалось получить ответ. Можно повторить отправку; подробности в журнале LiteraryShared.

### 0639 · voice.b124f4597c722195

<!-- LOPATA.Events: ["localization:Literary.Writer.Missing"] -->

- Не найден проверенный Qwen3.8-27B-Ridge или backend. Выполните подготовку литературного сценария.

### 0640 · voice.54a392c1e80f8774

<!-- LOPATA.Events: ["localization:Literary.Writer.Timeout"] -->

- Время ожидания истекло. Можно повторить отправку.

### 0641 · voice.7266b440ee2a10a2

<!-- LOPATA.Events: ["localization:Literary.Writer.Working"] -->

- Писатель. Qwen. Подготовка текста.

### 0642 · voice.11498385b9d97956

<!-- LOPATA.Events: ["localization:LivingArcade.DeletedRun"] -->

- Прохождение удалено. Поздние результаты не восстановят его.

### 0643 · voice.88cd032ba99ea5f7

<!-- LOPATA.Events: ["localization:LivingArcade.InvalidLevel"] -->

- Уровень не прошёл проверку.

### 0644 · voice.02c22239b28e5a6e

<!-- LOPATA.Events: ["localization:LivingArcade.InvalidSave"] -->

- Не удалось прочитать сохранение.

### 0645 · voice.6a0c196f466ffcbc

<!-- LOPATA.Events: ["localization:LivingArcade.LevelNotComplete"] -->

- Уровень ещё не завершён.

### 0646 · voice.b63c2c3f7cb1bc1d

<!-- LOPATA.Events: ["localization:LivingArcade.LevelNotReady"] -->

- Уровень ещё не подготовлен.

### 0647 · voice.bc60bc8014012c30

<!-- LOPATA.Events: ["localization:LivingArcade.LevelProbeFailed"] -->

- Уровень не прошёл пробный запуск.

### 0648 · voice.ea3502a533be36cb

<!-- LOPATA.Events: ["localization:LyricVideo.Cancelled"] -->

- Экспорт отменён. Исходная песня и сохранённый анализ не изменены.

### 0649 · voice.cf625bdea1c141cb

<!-- LOPATA.Events: ["localization:LyricVideo.Exported"] -->

- Клип сохранён.

### 0650 · voice.e7a78745bfa7ac4f

<!-- LOPATA.Events: ["localization:LyricVideo.Failed"] -->

- Не удалось завершить операцию.

### 0651 · voice.47ca8096edb23521

<!-- LOPATA.Events: ["localization:LyricVideo.Interrupted"] -->

- Экспорт остановлен. Анализ сохранён; продолжение доступно через общий режим.

### 0652 · voice.020852edbccb84e5

<!-- LOPATA.Events: ["localization:LyricVideo.NoTiming"] -->

- В файле нет подходящей разметки строк. Клип создаётся как музыкальный визуализатор; текст в дожде доступен.

### 0653 · voice.cbc802b9ceac4e40

<!-- LOPATA.Events: ["localization:LyricVideo.OtherOperation"] -->

- Другая операция активна или ожидает продолжения. Завершите её либо поставьте на паузу через общий режим.

### 0654 · voice.78cf0f7439cdf537

<!-- LOPATA.Events: ["localization:LyricVideo.PlaybackFailed"] -->

- Не удалось открыть звук для предпросмотра.

### 0655 · voice.136a4cbc3c8e1f4a

<!-- LOPATA.Events: ["localization:LyricVideo.PreferencesError"] -->

- Не удалось прочитать или сохранить настройки клипа.

### 0656 · voice.e2f83266562b1fd6

<!-- LOPATA.Events: ["localization:LyricVideo.Preparing"] -->

- Подготовка песни и анализ.

### 0657 · voice.ca62a3df9c06cf3a

<!-- LOPATA.Events: ["localization:LyricVideo.Ready"] -->

- Готово к просмотру.

### 0658 · voice.e58a8d6bad7c260a

<!-- LOPATA.Events: ["localization:Metadata.Canceled"] -->

- Остановлено. Уже сохранённые файлы остаются изменёнными.

### 0659 · voice.e6de22dd168bcf60

<!-- LOPATA.Events: ["localization:Metadata.ContentChanged"] -->

- Проверка выявила изменение содержимого. Исходник не заменён.

### 0660 · voice.de90bcd77a0bc8a0

<!-- LOPATA.Events: ["localization:Metadata.CoverReadOnly"] -->

- Замена обложки недоступна: неподдерживаемый контейнер или несколько встроенных картинок. Просмотр и извлечение доступны.

### 0661 · voice.9a67d428cf414645

<!-- LOPATA.Events: ["localization:Metadata.ExportSource"] -->

- Нельзя сохранить извлечённую картинку поверх одного из открытых исходных файлов.

### 0662 · voice.f91917a0e8e6bab6

<!-- LOPATA.Events: ["localization:Metadata.InvalidPath"] -->

- Не удалось открыть этот путь.

### 0663 · voice.705e91281bfac93e

<!-- LOPATA.Events: ["localization:Metadata.Partial"] -->

- Часть файлов не сохранена. Результат указан в списке для каждого файла.

### 0664 · voice.404754169f9f8821

<!-- LOPATA.Events: ["localization:Metadata.PreviewUnavailable"] -->

- Предпросмотр недоступен. Найденные сведения остаются доступны.

### 0665 · voice.6986a241fd2b82b8

<!-- LOPATA.Events: ["localization:Metadata.ReadFailed"] -->

- Не удалось прочитать сведения. Это не означает, что метаданных нет.

### 0666 · voice.67da617aaee064a0

<!-- LOPATA.Events: ["localization:Metadata.ReadOnly"] -->

- Только чтение: безопасная запись этого поля или формата пока не поддерживается.

### 0667 · voice.f640efa164ea93f0

<!-- LOPATA.Events: ["localization:Metadata.Ready"] -->

- Готово к просмотру.

### 0668 · voice.d0f412600e0bdfa9

<!-- LOPATA.Events: ["localization:Metadata.RuntimeMissing"] -->

- Компонент чтения метаданных отсутствует. Восстановите установку ЛОПАТЫ.

### 0669 · voice.2b311d61a7c90ca1

<!-- LOPATA.Events: ["localization:Metadata.SaveFailed"] -->

- Не удалось сохранить файл. Исправный исходник сохранён; проверьте доступ и не занят ли файл другим приложением.

### 0670 · voice.6b6ee255494339bb

<!-- LOPATA.Events: ["localization:Metadata.TimedOut"] -->

- Чтение или обработка не уложились в отведённое время.

### 0671 · voice.667183de04646458

<!-- LOPATA.Events: ["localization:Metadata.TooLarge"] -->

- Превышен безопасный объём обработки. Данные не изменены.

### 0672 · voice.7c57a08724ca9508

<!-- LOPATA.Events: ["localization:Metadata.VerificationFailed"] -->

- Проверка записанных метаданных не прошла. Исходник не заменён.

### 0673 · voice.95c5e44429b474cf

<!-- LOPATA.Events: ["localization:Models.Library.RefreshFailed"] -->

- Не удалось обновить библиотеку моделей. Остальные настройки доступны.

### 0674 · voice.b8cae816f7d39044

<!-- LOPATA.Events: ["localization:Models.Result.OperationCancelled"] -->

- Операция остановлена; пригодная частичная загрузка сохранена.

### 0675 · voice.51f49132a37bfc1b

<!-- LOPATA.Events: ["localization:Models.Result.OperationComplete"] -->

- Операция с моделью завершена.

### 0676 · voice.e96139d40506e748

<!-- LOPATA.Events: ["localization:Models.Result.OperationFailed"] -->

- Операция не завершена. Карточка и диагностическое состояние сохранены.

### 0677 · voice.82cef4628509e5da

<!-- LOPATA.Events: ["localization:Models.Result.RemoveFailed"] -->

- Файлы не удалены: сработала защита пути, закрепления или активной модели.

### 0678 · voice.17942387fce70ea6

<!-- LOPATA.Events: ["localization:Models.Stage.downloading", "localization:Music.Downloading", "localization:Music.Poetry.Selection.Downloading", "localization:Processes.Downloading"] -->

- Скачивание.

### 0679 · voice.897662644cd74bc0

<!-- LOPATA.Events: ["localization:Models.Status.corrupted"] -->

- Повреждена.

### 0680 · voice.fdc398d2421543b5

<!-- LOPATA.Events: ["localization:Models.Status.not_installed", "localization:VideoEditor.Voices.Missing"] -->

- Не установлена.

### 0681 · voice.ae351874a7b0f00d

<!-- LOPATA.Events: ["localization:Models.Status.paused", "localization:RagBooks.State.Paused"] -->

- Загрузка приостановлена.

### 0682 · voice.1bac152237b3ad45

<!-- LOPATA.Events: ["localization:Models.Status.source_unavailable"] -->

- Источник недоступен.

### 0683 · voice.3b789e7cacdb41e5

<!-- LOPATA.Events: ["localization:Models.Warning.ExternalReadOnly"] -->

- Внешний файл: ЛОПАТА не имеет права удалять или перекачивать его.

### 0684 · voice.fb2c0bfb8306d685

<!-- LOPATA.Events: ["localization:Models.Warning.LegacyManager"] -->

- Эта карточка импортирована из существующего менеджера. Управление загрузкой остаётся в исходном разделе.

### 0685 · voice.a855be078f1b8e93

<!-- LOPATA.Events: ["localization:Models.Warning.NeedsAttention"] -->

- Последняя проверка выявила проблему. Внутренние подробности сохранены в карточке, но не выводятся в интерфейс.

### 0686 · voice.4e89448a3aa3edf5

<!-- LOPATA.Events: ["localization:Models.Warning.System"] -->

- Системная модель: после удаления основные AI-функции будут недоступны до повторной загрузки.

### 0687 · voice.5fd3e8bb7c9405b3

<!-- LOPATA.Events: ["localization:Music.Ace.Tuning.MetadataOnly"] -->

- Музыкальный план выключен. Круг влияет только на оставшуюся обработку описания, языка и метаданных.

### 0688 · voice.a4e866115de63540

<!-- LOPATA.Events: ["localization:Music.AceIdeas.AllFailed"] -->

- Ни один замысел не прошёл проверку. Подробности сохранены в журнале очереди.

### 0689 · voice.8228ac1fe3f254b1

<!-- LOPATA.Events: ["localization:Music.AceIdeas.Conflict"] -->

- Замысел ACE нельзя одновременно применять с кавером, аудиопримерами или MIDI. Отключите другую механику либо используйте обычную генерацию без выбранного замысла.

### 0690 · voice.54ec88db4b49ff7e

<!-- LOPATA.Events: ["localization:Music.AceIdeas.Invalid"] -->

- ACE не смогла подготовить пригодный замысел, либо его сохранённые данные повреждены. Проверьте журнал и попробуйте другой замысел.

### 0691 · voice.9692a6c8838b5f1e

<!-- LOPATA.Events: ["localization:Music.AceIdeas.NeedSelection"] -->

- Сначала выберите хотя бы один готовый замысел.

### 0692 · voice.505426881a34a31a

<!-- LOPATA.Events: ["localization:Music.AceIdeas.NoSong"] -->

- Выберите готовую ACE-песню с сохранёнными музыкальными кодами. У этого трека они отсутствуют либо использовались кавер/MIDI — данный путь дублей для него недоступен.

### 0693 · voice.0b7694b5c88ba261

<!-- LOPATA.Events: ["localization:Music.Audio.Error"] -->

- Не удалось воспроизвести аудиофайл.

### 0694 · voice.c394c2de55a59476

<!-- LOPATA.Events: ["localization:Music.Audio.FileError"] -->

- Не удалось выполнить действие с файлом.

### 0695 · voice.019a84bd2c4f4bde

<!-- LOPATA.Events: ["localization:Music.Audio.KaraokeUnavailable"] -->

- Караоке недоступно.

### 0696 · voice.797467973ab30d74

<!-- LOPATA.Events: ["localization:Music.Audio.Missing"] -->

- Аудиофайл не найден.

### 0697 · voice.a039ad8ce8bf46b5

<!-- LOPATA.Events: ["localization:Music.Audio.Saved"] -->

- Аудиофайл сохранён.

### 0698 · voice.aca976746ee441f8

<!-- LOPATA.Events: ["localization:Music.Audio.SyncPending"] -->

- Синхронизация текста песни ещё не завершена.

### 0699 · voice.780bfccd31226dc5

<!-- LOPATA.Events: ["localization:Music.Audio.SyncWaitingPlay"] -->

- Синхронизация ещё не завершена. Воспроизведение начнётся автоматически.

### 0700 · voice.51c85326f4f67d80

<!-- LOPATA.Events: ["localization:Music.Canceled"] -->

- Операция отменена.

### 0701 · voice.799231604f4cf1be

<!-- LOPATA.Events: ["localization:Music.Cover.Applied"] -->

- Обложка выбрана и сохранена. Следующие варианты этой песни используют её повторно.

### 0702 · voice.1d779357f51126d6

<!-- LOPATA.Events: ["localization:Music.Cover.ApplyPending"] -->

- Обложка выбрана, но аудиофайл занят. Закройте внешний проигрыватель и повторите «Использовать обложку», чтобы завершить запись метаданных.

### 0703 · voice.57b28774bf0bd768

<!-- LOPATA.Events: ["localization:Music.Cover.AudioChanged"] -->

- Проверка метаданных не пройдена. Исходный аудиофайл сохранён.

### 0704 · voice.07c1c6953d025524

<!-- LOPATA.Events: ["localization:Music.Cover.Busy"] -->

- Создание обложки уже выполняется.

### 0705 · voice.ddf952103b1a1c3d

<!-- LOPATA.Events: ["localization:Music.Cover.Error"] -->

- Не удалось подготовить обложку.

### 0706 · voice.1080d2c38a79d9b5

<!-- LOPATA.Events: ["localization:Music.Cover.Generating"] -->

- Идёт генерация обложки.

### 0707 · voice.75fd45c50216d79b

<!-- LOPATA.Events: ["localization:Music.Cover.Invalid"] -->

- Файл или сохранённые данные обложки повреждены. Исходная песня сохранена.

### 0708 · voice.8f5588d43fe05c9a

<!-- LOPATA.Events: ["localization:Music.Cover.Paused"] -->

- Генерация обложки на паузе.

### 0709 · voice.d801d424bf0086c3

<!-- LOPATA.Events: ["localization:Music.Cover.Ready"] -->

- Четыре варианта готовы.

### 0710 · voice.3d3fac93cc2dd9e5

<!-- LOPATA.Events: ["localization:Music.Cover.ReadyToGenerate"] -->

- Обложка ещё не выбрана. ПКМ — выключить.

### 0711 · voice.edd43572edccc6fe

<!-- LOPATA.Events: ["localization:Music.Cover.Stopped"] -->

- Создание обложки остановлено. Готовые этапы сохранены.

### 0712 · voice.1c250ed70ba9efd5

<!-- LOPATA.Events: ["localization:Music.Editor.Loading"] -->

- Подготовка счётчика токенов.

### 0713 · voice.54dd7e526d0bfb44

<!-- LOPATA.Events: ["localization:Music.Editor.Unavailable"] -->

- Не удалось подсчитать токены. Проверьте компоненты; запуск недоступен.

### 0714 · voice.68887db2df2708d5

<!-- LOPATA.Events: ["localization:Music.Error"] -->

- Не удалось подготовить компоненты.

### 0715 · voice.0ea98d162d1f8f92

<!-- LOPATA.Events: ["localization:Music.Examples.Error"] -->

- Не удалось открыть или перенести пример.

### 0716 · voice.f6363155749a42b0

<!-- LOPATA.Events: ["localization:Music.Expert.Conflict"] -->

- Имя уже есть. Да — заменить; Нет — сохранить копию; Отмена — пропустить этот файл.

### 0717 · voice.d6c4731ac786e5fa

<!-- LOPATA.Events: ["localization:Music.Expert.Immutable"] -->

- Рекомендуемый пресет изменить нельзя. Введите имя и сохраните новый.

### 0718 · voice.bdd021d469b8cea3

<!-- LOPATA.Events: ["localization:Music.Expert.TransferConfirm"] -->

- Пресет создан для другой вариации YuE2. Перенести только совместимые настройки?

### 0719 · voice.e224008d5d21601a

<!-- LOPATA.Events: ["localization:Music.Generation.Failed"] -->

- Не удалось завершить генерацию.

### 0720 · voice.4aeb0e6e1cf9e75c

<!-- LOPATA.Events: ["localization:Music.Generation.RuntimeMissing"] -->

- Исполнитель музыки отсутствует или не прошёл проверку.

### 0721 · voice.741a8e7bb5bc2be7

<!-- LOPATA.Events: ["localization:Music.Hardware.GpuFailed"] -->

- Видеокарта не смогла выполнить этап.

### 0722 · voice.4223cf6e832b9ad0

<!-- LOPATA.Events: ["localization:Music.Library.Cancelled"] -->

- Действие отменено.

### 0723 · voice.1c0d702144867826

<!-- LOPATA.Events: ["localization:Music.Library.ChooseStored"] -->

- Выберите сохранённое произведение. Правки сначала сохраните в базе.

### 0724 · voice.8e1a900c5365d57e

<!-- LOPATA.Events: ["localization:Music.Library.Deleted"] -->

- Убрано из базы. Кнопка «Вернуть удалённое» восстановит запись.

### 0725 · voice.9d13bd5c3fe5aad9

<!-- LOPATA.Events: ["localization:Music.Library.Exported"] -->

- Копия базы сохранена.

### 0726 · voice.60cd17c3d0d9e4cd

<!-- LOPATA.Events: ["localization:Music.Library.Failed"] -->

- Не удалось выполнить действие.

### 0727 · voice.f4df1a928beb24bc

<!-- LOPATA.Events: ["localization:Music.Library.Indexing"] -->

- Подготовка поиска по стихам.

### 0728 · voice.32a07ce2134a0e7a

<!-- LOPATA.Events: ["localization:Music.Library.NotReady"] -->

- Компоненты поиска ещё не готовы. Повторите подготовку.

### 0729 · voice.5c3665dd3399ca64

<!-- LOPATA.Events: ["localization:Music.Library.NothingToRestore"] -->

- В этом окне пока ничего не удаляли.

### 0730 · voice.60f01edc99111e72

<!-- LOPATA.Events: ["localization:Music.Library.PreparationFailed"] -->

- Подготовку не удалось завершить. Можно проверить снова.

### 0731 · voice.249924563cacf433

<!-- LOPATA.Events: ["localization:Music.Library.Saved"] -->

- Сохранено в личной базе.

### 0732 · voice.0ff4b81cf4a5b959

<!-- LOPATA.Events: ["localization:Music.Lora.Cancelled", "localization:Publisher.Cancelled"] -->

- Операция отменена.

### 0733 · voice.0570015a323fc2df

<!-- LOPATA.Events: ["localization:Music.Lora.Failed"] -->

- Не удалось выполнить действие.

### 0734 · voice.bd2a7333faed1be0

<!-- LOPATA.Events: ["localization:Music.Lora.FavoritesError"] -->

- Не удалось прочитать или сохранить избранные LoRA. Список избранного не заменён.

### 0735 · voice.046974bda3fd30fd

<!-- LOPATA.Events: ["localization:Music.Lora.Missing"] -->

- Выбранные LoRA отсутствуют или не прошли проверку. Скачайте их либо отмените действие и измените выбор.

### 0736 · voice.7250897819218466

<!-- LOPATA.Events: ["localization:Music.Lora.PlannerCombinationWarning"] -->

- Несколько LoRA одновременно меняют музыкальный план с суммарным влиянием выше 1. Сочетание может испортить нотную запись.

### 0737 · voice.80c0c45b2f1066c1

<!-- LOPATA.Events: ["localization:Music.Lora.Removed"] -->

- Файлы LoRA удалены. Выбор и влияние сохранены в проекте; для применения потребуется скачать их снова.

### 0738 · voice.c1e9573ecbe8876d

<!-- LOPATA.Events: ["localization:Music.Lora.RuntimeInvalid"] -->

- Обвязка LoRA отсутствует или не прошла проверку.

### 0739 · voice.19700c865e0db3bd

<!-- LOPATA.Events: ["localization:Music.Lora.ScoreCombinationFailure"] -->

- Модель создала некорректную нотную запись. Вероятная причина — конфликт выбранных LoRA при совместном изменении музыкального плана.

### 0740 · voice.6c4223455f551d4a

<!-- LOPATA.Events: ["localization:Music.Lora.Search.BaseUnconfirmed"] -->

- Точная базовая модель или рецепт применения не подтверждены.

### 0741 · voice.ee84fcfba96d1d6f

<!-- LOPATA.Events: ["localization:Music.Lora.Search.CacheEmpty"] -->

- Для такого запроса ещё нет сохранённых результатов.

### 0742 · voice.e05ed75577c5db70

<!-- LOPATA.Events: ["localization:Music.Lora.Search.ConfigMissing"] -->

- Отсутствует поддержанная полная пара весов и конфигурации.

### 0743 · voice.7933057c3440ff69

<!-- LOPATA.Events: ["localization:Music.Lora.Search.Done"] -->

- Поиск завершён. Ничего не скачано и не включено автоматически.

### 0744 · voice.d146b55bc3529699

<!-- LOPATA.Events: ["localization:Music.Lora.Search.LicenseMissing"] -->

- Нет распознанных опубликованных условий; вариант исключён.

### 0745 · voice.75cdcb46c88fb8df

<!-- LOPATA.Events: ["localization:Music.Lora.Search.NoCompatibleFiles"] -->

- В указанном репозитории нет подтверждённых совместимых файлов.

### 0746 · voice.906541620398b6f6

<!-- LOPATA.Events: ["localization:Music.Lora.Search.RangeUnsupported"] -->

- Источник не подтвердил частичное чтение весов; файл пропущен.

### 0747 · voice.09f97f9bd5bfdc23

<!-- LOPATA.Events: ["localization:Music.Lora.Search.ResponseTooLarge"] -->

- Слишком большой ответ источника; проверка остановлена.

### 0748 · voice.2409f9535c018f87

<!-- LOPATA.Events: ["localization:Music.Lora.Search.RevisionMissing"] -->

- Не удалось закрепить ревизию репозитория.

### 0749 · voice.ec707f5ec2b5618e

<!-- LOPATA.Events: ["localization:Music.Lora.Search.Stopped"] -->

- Поиск остановлен. Найденные результаты сохранены.

### 0750 · voice.c32027c4d1f04d86

<!-- LOPATA.Events: ["localization:Music.Lora.Search.StoppedEmpty"] -->

- Поиск остановлен до первых находок.

### 0751 · voice.bb953118131d2f9e

<!-- LOPATA.Events: ["localization:Music.Lora.UnavailableSelections"] -->

- В проекте есть выбор для другого набора движков или недоступного адаптера. Для применения верните совместимый набор либо уберите этот выбор.

### 0752 · voice.2348cff9d23dd0dd

<!-- LOPATA.Events: ["localization:Music.Lora.Unknown"] -->

- Точная оценка доступной видеопамяти сейчас неизвестна. Это предварительный резерв, а не измерение нагрузки ЛОПАТЫ.

### 0753 · voice.c87ad52f4929ea5f

<!-- LOPATA.Events: ["localization:Music.LyricsSync.Unavailable"] -->

- Не удалось получить достоверную разметку текста. Песня доступна для обычного прослушивания.

### 0754 · voice.6e4cab55773e10f0

<!-- LOPATA.Events: ["localization:Music.Midi.ChangedInput"] -->

- Подготовленная мелодия изменилась. Не удалось подтвердить сохранённый вход генерации.

### 0755 · voice.4f4d7bd6543f31c6

<!-- LOPATA.Events: ["localization:Music.Midi.NoMelody"] -->

- В MIDI не найдена мелодическая дорожка с нотами. Одни ударные использовать нельзя.

### 0756 · voice.fd7e2bb4748cb7d2

<!-- LOPATA.Events: ["localization:Music.Midi.PreviewFailed"] -->

- Не удалось прослушать мелодию.

### 0757 · voice.7af9f561628a328d

<!-- LOPATA.Events: ["localization:Music.Midi.Saved"] -->

- Мелодия сохранена.

### 0758 · voice.c60c643bb3bb50ed

<!-- LOPATA.Events: ["localization:Music.Midi.Unsupported"] -->

- Редактирование, прослушивание и экспорт доступны. Генерация по мелодии подключена только к Studio и ACE; для выбранной модели выключите её применение.

### 0759 · voice.329737544ea21766

<!-- LOPATA.Events: ["localization:Music.Models.Connected"] -->

- Подключено. Проверка компонентов и лицензий — после выбора.

### 0760 · voice.1df0e81d6c2370ab

<!-- LOPATA.Events: ["localization:Music.Models.Remove.PartialFailure"] -->

- Не удалось завершить удаление. Часть файлов могла быть удалена.

### 0761 · voice.bd4d4a7c70752e18

<!-- LOPATA.Events: ["localization:Music.Models.Remove.Preparing"] -->

- Подготовка списка удаления модели.

### 0762 · voice.c58a4138c0776c51

<!-- LOPATA.Events: ["localization:Music.Models.Remove.Unavailable"] -->

- Удаление недоступно: модель отсутствует, не подключена или используется.

### 0763 · voice.dc4a5958a2c459ab

<!-- LOPATA.Events: ["localization:Music.Output.Invalid"] -->

- Не удалось прочитать или сохранить выбор форматов. Выберите формат заново.

### 0764 · voice.3c9b8b9f22ba584e

<!-- LOPATA.Events: ["localization:Music.Poetry.AssemblyIncomplete"] -->

- Сборка не содержит полного текста и параметров либо нарушает размещение результата. Предыдущие данные сохранены; ответ доступен отдельно.

### 0765 · voice.9cb75df44168646a

<!-- LOPATA.Events: ["localization:Music.Poetry.Canceled"] -->

- Ответ остановлен. Сохранённый текст не изменён.

### 0766 · voice.1d85a3a4f70515d3

<!-- LOPATA.Events: ["localization:Music.Poetry.DuplicateSong"] -->

- Модель продублировала песню в чате. Предыдущие данные сохранены; ответ доступен отдельно.

### 0767 · voice.8d0c35a5fa02e85e

<!-- LOPATA.Events: ["localization:Music.Poetry.Error"] -->

- Не удалось получить ответ.

### 0768 · voice.a70dba1cef4500b9

<!-- LOPATA.Events: ["localization:Music.Poetry.ModelUnsupported"] -->

- Эта модель не входит в проверенный список песенного чата. Выберите модель через ИИ±; сохранённая сессия остаётся доступной.

### 0769 · voice.901408c9909c24a3

<!-- LOPATA.Events: ["localization:Music.Poetry.Preparing"] -->

- Подготовка модели и проверка контекста.

### 0770 · voice.4dffdd4fb410828f

<!-- LOPATA.Events: ["localization:Music.Poetry.Ready"] -->

- Готово. Перенос в музыкальный сценарий — через копирование.

### 0771 · voice.da9e9ef034436454

<!-- LOPATA.Events: ["localization:Music.Poetry.ReasoningChoiceUnavailable"] -->

- Сохранённый режим рассуждений недоступен. Выберите поддерживаемый режим; ответ не будет отправлен с другим режимом автоматически.

### 0772 · voice.8d0c0b38a784c71d

<!-- LOPATA.Events: ["localization:Music.Poetry.ReasoningUnavailable"] -->

- Управление рассуждениями для этой модели пока не подключено.

### 0773 · voice.c37e67184c87ff22

<!-- LOPATA.Events: ["localization:Music.Poetry.Repairing"] -->

- Результат не прошёл проверку. Запрашиваем исправление. попытка 2/2.

### 0774 · voice.b964c87a44f040cf

<!-- LOPATA.Events: ["localization:Music.Poetry.SaveFailed"] -->

- Не удалось сохранить. Текст остаётся в окне; отправка и закрытие заблокированы.

### 0775 · voice.3af66e2c4aef0dca

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.AnswerTooLong"] -->

- Ответ помощника превышает допустимый объём. Он не выдан за завершённую консультацию.

### 0776 · voice.38392ff62313ba2a

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.BrowserFailed"] -->

- Не удалось запустить встроенный браузер. Выключите поиск и повторите включение или перезапустите программу.

### 0777 · voice.62627847b23f5a80

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.Busy"] -->

- На странице помощника уже идёт ответ или требуется действие пользователя. Откройте встроенное окно.

### 0778 · voice.f1738cb3cb7bec3d

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.Offline"] -->

- Нет подключения к интернету.

### 0779 · voice.c7b261ae1eae576f

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.PageChanged"] -->

- Не удалось распознать страницу помощника. Откройте встроенное окно через ПКМ и проверьте состояние сайта.

### 0780 · voice.80e21494408632e5

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.ResponseInterrupted"] -->

- Ответ помощника прерван. Частичный текст сохранён; автоматического повтора нет.

### 0781 · voice.cded5d521bb373d9

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.RuntimeMissing"] -->

- Не установлен Microsoft WebView2 Runtime. Подготовьте компонент WebView2 в настройках компонентов.

### 0782 · voice.43cb008c7b30f101

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.SiteUnavailable"] -->

- Не удалось загрузить сайт помощника. Это не ошибка локальной модели.

### 0783 · voice.01498284fafa87a7

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.State.completed", "localization:Music.Projects.Completed", "localization:OfficeEditor.Sheet.Status.completed"] -->

- Завершено.

### 0784 · voice.ebe1f7907089c919

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.State.preparing"] -->

- Подготовка вопроса.

### 0785 · voice.8b830d0769c039b2

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.State.ready"] -->

- Вопрос готов.

### 0786 · voice.3618e575e2e0d071

<!-- LOPATA.Events: ["localization:Music.Poetry.Search.Timeout"] -->

- Не удалось подтвердить завершение ответа помощника. Запрос мог быть обработан; автоматического повтора нет.

### 0787 · voice.c94a5aae13080585

<!-- LOPATA.Events: ["localization:Music.Poetry.Selection.DownloadError"] -->

- Не удалось скачать модель.

### 0788 · voice.7c5dcf9166d22e86

<!-- LOPATA.Events: ["localization:Music.Poetry.Selection.DownloadPaused"] -->

- Скачивание приостановлено.

### 0789 · voice.79963c690b2f896f

<!-- LOPATA.Events: ["localization:Music.Poetry.Selection.PreferencesError"] -->

- Не удалось прочитать или сохранить избранное и комментарии.

### 0790 · voice.b06c063a38d5dcae

<!-- LOPATA.Events: ["localization:Music.Poetry.Selection.ProfileUnavailable"] -->

- Модель ещё не установлена. Скачайте её кнопкой ниже; после загрузки она станет доступна в чате.

### 0791 · voice.6b9fccf1e7c8dbda

<!-- LOPATA.Events: ["localization:Music.Poetry.Selection.RemoveBlocked"] -->

- Удаление недоступно: модель защищена, используется в текущей операции или связана с другим сценарием.

### 0792 · voice.06d4224a088fd771

<!-- LOPATA.Events: ["localization:Music.Poetry.Selection.RemoveError"] -->

- Не удалось полностью удалить модель. Проверьте её состояние перед повторной загрузкой.

### 0793 · voice.5a841335f14f284b

<!-- LOPATA.Events: ["localization:Music.Poetry.Selection.Unavailable"] -->

- Совместимый файл не найден. Подготовьте модель в её основном сценарии; это окно ничего не скачивает.

### 0794 · voice.293974f73a9cd9b7

<!-- LOPATA.Events: ["localization:Music.Poetry.StreamFailed"] -->

- Поток ответа модели прерван или имеет неподдерживаемый формат. Ответ не применён.

### 0795 · voice.2cf3c46e097da356

<!-- LOPATA.Events: ["localization:Music.Poetry.TargetReadOnly"] -->

- Музыкальный движок выбирается пользователем в интерфейсе. Ответ модели не может менять этот выбор.

### 0796 · voice.6dc8c3ce5e9a9c1a

<!-- LOPATA.Events: ["localization:Music.Preparation.Missing"] -->

- Необходима докачка или повторная загрузка компонентов.

### 0797 · voice.96f2a13773bcc500

<!-- LOPATA.Events: ["localization:Music.Projects.Busy"] -->

- Сначала завершите или отмените текущее задание, включая приостановленное.

### 0798 · voice.720f48ddd5ec7c65

<!-- LOPATA.Events: ["localization:Music.Projects.Cancelled", "localization:Music.Status.Cancelled", "localization:OfficeEditor.Sheet.Status.cancelled", "localization:Processes.Cancelled"] -->

- Отменено.

### 0799 · voice.702b884a2c7253cb

<!-- LOPATA.Events: ["localization:Music.Projects.ReadError"] -->

- Не удалось прочитать данные проекта.

### 0800 · voice.b82e1c398523e13e

<!-- LOPATA.Events: ["localization:Music.Projects.Saved"] -->

- Сохранённый проект.

### 0801 · voice.c0e6b617b06f9f44

<!-- LOPATA.Events: ["localization:Music.Projects.WriteError"] -->

- Не удалось сохранить состояние попытки.

### 0802 · voice.7cf37d4aca8ca504

<!-- LOPATA.Events: ["localization:Music.Rhyme.CorpusInvalid"] -->

- Библиотека рифмы отсутствует, повреждена или не соответствует закреплённой версии.

### 0803 · voice.f63d270171a294ec

<!-- LOPATA.Events: ["localization:Music.Rhyme.InvalidReply"] -->

- Ответ модели не прошёл проверку: изменены защищённые строки, структура или ударения. Исходник сохранён.

### 0804 · voice.e411863f355fd256

<!-- LOPATA.Events: ["localization:Music.Rhyme.Loading.Interrupted"] -->

- Этап не завершён. Можно запустить анализ и генерацию заново.

### 0805 · voice.b69d22d35db21ec2

<!-- LOPATA.Events: ["localization:Music.Rhyme.Loading.Model"] -->

- Готовим справку и вариант модели.

### 0806 · voice.160764fc1a019c5c

<!-- LOPATA.Events: ["localization:Music.Rhyme.Loading.Paused"] -->

- На паузе. Готовые результаты сохранены.

### 0807 · voice.67697691e0d535d2

<!-- LOPATA.Events: ["localization:Music.Rhyme.Loading.Program"] -->

- Анализируем текст и подбираем программные варианты.

### 0808 · voice.e8b40d38ab6e4819

<!-- LOPATA.Events: ["localization:Music.Rhyme.NoIssues"] -->

- В выбранном объёме нет строк песни для редакторского варианта. Модель не запускалась.

### 0809 · voice.41ce5fe359fb7be5

<!-- LOPATA.Events: ["localization:Music.Rhyme.Paused"] -->

- Выравниватель приостановлен. Продолжение — через меню фоновых задач.

### 0810 · voice.63e3dc0bae076ec0

<!-- LOPATA.Events: ["localization:Music.Rhyme.PreferencesError"] -->

- Не удалось прочитать настройки мастерской рифмы.

### 0811 · voice.441c1d9a54272bdb

<!-- LOPATA.Events: ["localization:Music.Rhyme.ProgramCoverageMissing", "source:0bb1dbd2274c5687", "source:9f16f36b93ce97fe", "source:c08c6830a73a19a2", "source:8b68f9522924783b"] -->

- Не найдены необходимые данные или компоненты при работе с базой знаний.

### 0812 · voice.3ecc862fcbfff01c

<!-- LOPATA.Events: ["localization:Music.Rhyme.ProgramUnchanged"] -->

- Программная версия: подходящие перестановки и словарные замены не найдены.

### 0813 · voice.b671a47bf7e3fe0b

<!-- LOPATA.Events: ["localization:Music.Rhyme.Settings.NoSteps"] -->

- Результатов пока нет. Настройки будут использованы при следующем запуске мастерской.

### 0814 · voice.13828222dab5c480

<!-- LOPATA.Events: ["localization:Music.Rhyme.Settings.SaveFailed"] -->

- Не удалось сохранить настройки. Подробности в журнале программы.

### 0815 · voice.6edeb606b74646fd

<!-- LOPATA.Events: ["localization:Music.Rhyme.Stage.Downloading"] -->

- Скачиваем библиотеку рифмы.

### 0816 · voice.249794ee7477e77f

<!-- LOPATA.Events: ["localization:Music.Rhyme.Stage.Loading"] -->

- Загружаем поисковую модель.

### 0817 · voice.3b1624ec15292f3e

<!-- LOPATA.Events: ["localization:Music.Rhyme.Stage.ReadyWorker"] -->

- Справка готова.

### 0818 · voice.02f93769b1d8b51c

<!-- LOPATA.Events: ["localization:Music.Rhyme.Stopped"] -->

- Обработка остановлена. Черновик сохранён; можно запустить заново.

### 0819 · voice.20bb179d8fb7595d

<!-- LOPATA.Events: ["localization:Music.Rhyme.Unchanged"] -->

- Модель вернула исходный текст без изменений. Нового варианта нет.

### 0820 · voice.0351bdce2323f2fc

<!-- LOPATA.Events: ["localization:Music.Score.ChangeMode"] -->

- Требуется ваше подтверждение.

### 0821 · voice.31d8cdaac47f807c

<!-- LOPATA.Events: ["localization:Music.Score.Conflict"] -->

- Выберите один музыкальный источник: сохранённый план, MIDI или кавер / аудиопримеры. Одновременное применение не поддерживается.

### 0822 · voice.d58a63dc3676a1ca

<!-- LOPATA.Events: ["localization:Music.Score.Invalid"] -->

- План не удалось проверить. Исправьте ABC; поддерживается ограниченный диалект YuE2.

### 0823 · voice.ac190240b9eb6591

<!-- LOPATA.Events: ["localization:Music.Score.NeedSelection"] -->

- Сначала выберите хотя бы один готовый план.

### 0824 · voice.0f163044a04aa4b8

<!-- LOPATA.Events: ["localization:Music.Score.NoSongPlan"] -->

- У выбранной песни нет доступного сохранённого ABC-плана.

### 0825 · voice.6eec69ff62ff275a

<!-- LOPATA.Events: ["localization:Music.Score.PlanFailed"] -->

- Не удалось подготовить один из планов.

### 0826 · voice.6d936db708c7a384

<!-- LOPATA.Events: ["localization:Music.Score.Skipped"] -->

- Подготовка оставшихся планов остановлена по вашему выбору.

### 0827 · voice.d6b95913909428a5

<!-- LOPATA.Events: ["localization:Music.Score.TimingUnavailable"] -->

- Согласование слов с планом недоступно для текущего запроса. Нужны слова, ABC и явный CFG = 1.

### 0828 · voice.358e5a679f883972

<!-- LOPATA.Events: ["localization:Music.Score.WaitPlans"] -->

- Сначала завершите или остановите очередь планов. Два тяжёлых запуска одновременно не выполняются.

### 0829 · voice.de549945ed85e8f5

<!-- LOPATA.Events: ["localization:Music.Status.Loading", "localization:RagBooks.Loading"] -->

- Загрузка модели.

### 0830 · voice.9a3a686e4cdf2474

<!-- LOPATA.Events: ["localization:Music.Status.Paused"] -->

- Генерация приостановлена.

### 0831 · voice.275f486ad2e755f3

<!-- LOPATA.Events: ["localization:Music.Stems.Failed"] -->

- Песня создана, но раздельное сохранение не завершено. Исходник сохранён для восстановления.

### 0832 · voice.8d1bc56f13cba53d

<!-- LOPATA.Events: ["localization:Music.Transform.AlignmentFailed"] -->

- Не удалось собрать дорожки на общей временной шкале. Оригинал и новый дубль сохранены.

### 0833 · voice.097890e89107f54c

<!-- LOPATA.Events: ["localization:Music.Transform.ConflictingStudioReferences"] -->

- У примеров распознались разные темп, тональность или размер. Studio не может одновременно следовать этим параметрам.

### 0834 · voice.f47d6cf6eb96fc15

<!-- LOPATA.Events: ["localization:Music.Transform.FitTooDifferent"] -->

- Длительности дорожек различаются слишком сильно для умеренной подгонки (допустимо примерно 0,6–1,65 от исходной). Оригинал и дубль сохранены; попробуйте полный кавер.

### 0835 · voice.8735968d59ab5353

<!-- LOPATA.Events: ["localization:Music.Transform.HeadroomFailed"] -->

- Новый слой требует слишком сильного приглушения. Монтаж остановлен.

### 0836 · voice.556a2f9553398493

<!-- LOPATA.Events: ["localization:Music.Transform.InputChanged"] -->

- Подготовленный исходник изменился или повреждён. Повторно выберите файл.

### 0837 · voice.630bb44f62d7f6d6

<!-- LOPATA.Events: ["localization:Music.Transform.NoClipboardText"] -->

- В буфере обмена нет текста.

### 0838 · voice.5becf6f66ab0b88c

<!-- LOPATA.Events: ["localization:Music.Transform.NoLyrics"] -->

- В файле не найдены слова песни. Можно распознать их или вставить вручную.

### 0839 · voice.c577e49fa77223e4

<!-- LOPATA.Events: ["localization:Music.Transform.RecognitionFailed"] -->

- Не удалось распознать слова песни. Можно вставить их вручную.

### 0840 · voice.ecad97b59132e4ab

<!-- LOPATA.Events: ["localization:Music.Transform.ReferenceAnalysisFailed"] -->

- Из примера не удалось определить музыкальные параметры. Попробуйте другой фрагмент.

### 0841 · voice.9ffe14424cff0186

<!-- LOPATA.Events: ["localization:Music.Transform.ScoreFailed"] -->

- Распознавание музыкального плана завершилось ошибкой.

### 0842 · voice.9e1f5efefa566f50

<!-- LOPATA.Events: ["localization:Music.Transform.ScoreTimeout"] -->

- Не удалось дождаться запуска распознавания музыкального плана.

### 0843 · voice.617bab2a76041c76

<!-- LOPATA.Events: ["localization:Music.Transform.SilentInput"] -->

- В выбранном аудио нет различимого звука.

### 0844 · voice.cce06f3de242d3aa

<!-- LOPATA.Events: ["localization:Music.Transform.SourceTextFailed"] -->

- Не удалось прочитать текст. Используйте обычный TXT (UTF-8, UTF-16 или Windows-1251) либо вставку из буфера.

### 0845 · voice.5610af1b0e9fecf7

<!-- LOPATA.Events: ["localization:Music.Transform.StudioVoiceReference"] -->

- Studio не поддерживает прямой голосовой референс в этом исполнителе. Выберите пример звучания или ACE.

### 0846 · voice.98462987572da0da

<!-- LOPATA.Events: ["localization:Music.Transform.UnsupportedStagedReferences"] -->

- Это старое задание содержит примеры, которые нельзя совместить с исходником в данном режиме. Создайте новый запуск: в Studio-кавере примеры будут отключены.

### 0847 · voice.533093f929324a73

<!-- LOPATA.Events: ["localization:Music.Tuning.Invalid"] -->

- Не удалось прочитать пресеты круга.

### 0848 · voice.c9f26fec47a397a7

<!-- LOPATA.Events: ["localization:Music.Verifying"] -->

- Проверка.

### 0849 · voice.0ae8ad53f07ffc5d

<!-- LOPATA.Events: ["localization:Navigation.SandboxStatus"] -->

- Начните новую задачу или раскройте ранее начатое в Песочнице.

### 0850 · voice.62081c2fddc343c0

<!-- LOPATA.Events: ["localization:OfficeEditor.CellClipboardBusy"] -->

- Буфер обмена занят. Повторите действие.

### 0851 · voice.c9533ca35ac7e2b5

<!-- LOPATA.Events: ["localization:OfficeEditor.CommandUnavailable"] -->

- Команда недоступна для текущего выделения.

### 0852 · voice.13dbbab63761a655

<!-- LOPATA.Events: ["localization:OfficeEditor.DraftFailed"] -->

- Не удалось сохранить черновик. Сохраните документ отдельно.

### 0853 · voice.d5544ea5be5a91bc

<!-- LOPATA.Events: ["localization:OfficeEditor.DraftSaved"] -->

- Черновик сохранён.

### 0854 · voice.284bb7a39963a867

<!-- LOPATA.Events: ["localization:OfficeEditor.EngineUnavailable"] -->

- Не удалось запустить офисный движок.

### 0855 · voice.42aed88129d6bfab

<!-- LOPATA.Events: ["localization:OfficeEditor.ExternalContent"] -->

- Файл содержит внешние объекты, которые нельзя безопасно обработать локально.

### 0856 · voice.fe6027050b4c5257

<!-- LOPATA.Events: ["localization:OfficeEditor.Failed"] -->

- Не удалось выполнить действие. Черновики сохранены.

### 0857 · voice.b74574c13da59ce6

<!-- LOPATA.Events: ["localization:OfficeEditor.InvalidPackage"] -->

- Не удалось прочитать документ LOPATA: неверная структура или слишком большой файл.

### 0858 · voice.ab40c8070dad13cf

<!-- LOPATA.Events: ["localization:OfficeEditor.NativeInvalidText"] -->

- Этот текст содержит символ или перенос строки, который нельзя записать в выбранный абзац.

### 0859 · voice.d9fb40f1327b128f

<!-- LOPATA.Events: ["localization:OfficeEditor.NativeNoText"] -->

- В этом материале нет текста, доступного для правки. Можно открыть его в читалке или извлечь исходный файл.

### 0860 · voice.143832cbb67ad497

<!-- LOPATA.Events: ["localization:OfficeEditor.NoDrafts"] -->

- Сохранённых черновиков пока нет.

### 0861 · voice.e82a764af641b8c6

<!-- LOPATA.Events: ["localization:OfficeEditor.PartialSaved"] -->

- Файл сохранён. Дополнительные материалы и история остаются в черновике.

### 0862 · voice.4511d704b5b99f60

<!-- LOPATA.Events: ["localization:OfficeEditor.ReviewProgress", "localization:Reader.Translation.Progress"] -->

- Переводим следующий фрагмент текста.

### 0863 · voice.9c561f7f8a868368

<!-- LOPATA.Events: ["localization:OfficeEditor.Sheet.Action.ProjectSaved"] -->

- Сохранён проект.

### 0864 · voice.2f652e2867894a23

<!-- LOPATA.Events: ["localization:OfficeEditor.Sheet.Action.SourceSaved"] -->

- Сохранён исходный файл.

### 0865 · voice.98da121ca316e93c

<!-- LOPATA.Events: ["localization:OfficeEditor.Sheet.Status.running"] -->

- Выполняется.

### 0866 · voice.c12eaebddcc5db4e

<!-- LOPATA.Events: ["localization:OfficeEditor.SlideInvalidPicture"] -->

- Не удалось заменить изображение. Выберите исправный PNG или JPEG до 64 МиБ и 48 мегапикселей.

### 0867 · voice.0ca14e4d73b1896f

<!-- LOPATA.Events: ["localization:OfficeEditor.SlidePicturePackageLimit"] -->

- Не удалось встроить изображение. Проверьте размер файла презентации и изображения.

### 0868 · voice.a616927f3a22ff80

<!-- LOPATA.Events: ["localization:OfficeEditor.SpellingUnavailable"] -->

- Не найдены необходимые данные или компоненты в редакторе документов.

### 0869 · voice.f6edd88011c653f0

<!-- LOPATA.Events: ["localization:OfficeEditor.StyleUnavailable"] -->

- В документе нет этого стиля.

### 0870 · voice.605910516d06f3cb

<!-- LOPATA.Events: ["localization:OfficeEditor.Unsupported"] -->

- Это действие или формат пока не поддерживается.

### 0871 · voice.98b30127f44fdcd7

<!-- LOPATA.Events: ["localization:OfficeEditor.WorkbookRecalculation"] -->

- Часть формул в книге не удалось вычислить. Выберите ячейку, чтобы посмотреть её формулу.

### 0872 · voice.bc1a9cc7661e9487

<!-- LOPATA.Events: ["localization:OfficeEditor.WorkbookStoredResults"] -->

- Для части формул показаны последние значения из файла. После правки их потребуется пересчитать в приложении, которое поддерживает эти формулы.

### 0873 · voice.54560e3e68818d4b

<!-- LOPATA.Events: ["localization:OfficeRephrase.Failed"] -->

- Не удалось переформулировать текст. Проверьте уведомление об ошибке.

### 0874 · voice.79ea0238cb524fa8

<!-- LOPATA.Events: ["localization:OfficeRephrase.Preparing", "localization:OfficeSummary.Preparing"] -->

- Подготовка модели.

### 0875 · voice.a6aad1f9a80a3acd

<!-- LOPATA.Events: ["localization:OfficeSummary.Cancelled"] -->

- Обработка отменена.

### 0876 · voice.a8afe452aa1e1c9c

<!-- LOPATA.Events: ["localization:OfficeSummary.Failed"] -->

- Не удалось составить пересказ. Проверьте уведомление об ошибке.

### 0877 · voice.bc7d4b98a7d6ce31

<!-- LOPATA.Events: ["localization:OfficeSummary.NoText"] -->

- В документе или выделении нет доступного текста. Для сканов требуется распознавание.

### 0878 · voice.ef0065fc30aa209c

<!-- LOPATA.Events: ["localization:OfficeSummary.Ready"] -->

- Пересказ готов.

### 0879 · voice.06f551076e609bfc

<!-- LOPATA.Events: ["localization:OfficeSummary.SaveFailed"] -->

- Не удалось скопировать или сохранить пересказ.

### 0880 · voice.908c0b56c1fd870c

<!-- LOPATA.Events: ["localization:OfficeSummary.Saved"] -->

- Пересказ сохранён.

### 0881 · voice.9042723022222443

<!-- LOPATA.Events: ["localization:OfficeSummary.Working"] -->

- Составление пересказа.

### 0882 · voice.1feae5631136258b

<!-- LOPATA.Events: ["localization:OfficeSymbols.CopyFailed"] -->

- Не удалось скопировать. Попробуйте ещё раз.

### 0883 · voice.34530f23986e5081

<!-- LOPATA.Events: ["localization:OfficeSymbols.DataMissing"] -->

- Не удалось загрузить библиотеку символов. Проверьте уведомление об ошибке.

### 0884 · voice.d0ba42914b5f19bb

<!-- LOPATA.Events: ["localization:OfficeSymbols.Loading"] -->

- Загрузка библиотеки Unicode.

### 0885 · voice.a2809a1a15103b00

<!-- LOPATA.Events: ["localization:Paragraph.Cancelled"] -->

- Запрос остановлен. Ваш ввод сохранён.

### 0886 · voice.415cf28ccddc54cc

<!-- LOPATA.Events: ["localization:Paragraph.Failure"] -->

- Запрос не завершён. Ввод сохранён.

### 0887 · voice.aa7ce921c6867c98

<!-- LOPATA.Events: ["localization:Paragraph.Interrupted"] -->

- Предыдущий запрос был прерван. Ввод сохранён; отправьте его снова, когда будете готовы.

### 0888 · voice.0b0617e5923aab2c

<!-- LOPATA.Events: ["localization:Paragraph.Manual"] -->

- Нужный текст перенесите в рабочий редактор сами. До завершения главы он остаётся предварительным.

### 0889 · voice.e5d5ae7c26f4c422

<!-- LOPATA.Events: ["localization:Paragraph.Multiple"] -->

- Модель нарушила формат одного абзаца. Исходный ответ сохранён: исправьте его вручную или вернитесь к заданию для повтора.

### 0890 · voice.91fe54f02e28a96c

<!-- LOPATA.Events: ["localization:Paragraph.Receipt.error"] -->

- Не удалось прочитать — ответ остановлен.

### 0891 · voice.5bb323a5b1cd0c97

<!-- LOPATA.Events: ["localization:Paragraph.RouteMissing"] -->

- Выбранный этап отсутствует — выберите ориентир снова.

### 0892 · voice.6f81bffd6ce8da54

<!-- LOPATA.Events: ["localization:Paragraph.SaveError"] -->

- Не удалось сохранить сессию. Оставьте окно открытым и проверьте папку проекта.

### 0893 · voice.2dbda781378b4b11

<!-- LOPATA.Events: ["localization:Paragraph.Stale"] -->

- Во время запроса данные изменились. Ответ сохранён отдельно и не подменил ваш ввод.

### 0894 · voice.511e77e530e15626

<!-- LOPATA.Events: ["localization:Paragraph.Working"] -->

- Читаем выбранные источники и готовим ответ.

### 0895 · voice.49ea163ec25c16e8

<!-- LOPATA.Events: ["localization:Passport.DrivesMissing"] -->

- Диски: не найдены.

### 0896 · voice.3549b5af924d5dd9

<!-- LOPATA.Events: ["localization:Passport.GpuMissing"] -->

- GPU: не найдено; VRAM: unknown.

### 0897 · voice.18d53d142aacf452

<!-- LOPATA.Events: ["localization:Processes.State.Failed"] -->

- Ошибка запуска или неожиданная остановка.

### 0898 · voice.2d0d2194eea5be31

<!-- LOPATA.Events: ["localization:PromptPairs.StorageError"] -->

- Не удалось прочитать или сохранить данные. Проверьте доступ к папке и повторите попытку.

### 0899 · voice.f89a87a4481b9793

<!-- LOPATA.Events: ["localization:Publisher.DuplicateDirection"] -->

- Такое направление уже сохранено.

### 0900 · voice.90e2c95513c62b13

<!-- LOPATA.Events: ["localization:Publisher.InvalidResponse"] -->

- Сервис вернул неподдерживаемый ответ.

### 0901 · voice.d9e35842b5e0e9dc

<!-- LOPATA.Events: ["localization:Publisher.InvalidToken"] -->

- Ключ недействителен или истёк. Проверьте его локально и при необходимости замените.

### 0902 · voice.854e00e0216d4ab8

<!-- LOPATA.Events: ["localization:Publisher.NetworkError"] -->

- Не удалось завершить сетевую операцию или сохранить её состояние. Проверьте соединение и доступ к хранилищу.

### 0903 · voice.a3e63db7de0d0952

<!-- LOPATA.Events: ["localization:Publisher.NetworkTimeout"] -->

- Сервер не завершил ответ за 35 секунд. Проверьте соединение и повторите проверку; введённые данные сохранены в этом окне.

### 0904 · voice.e9b7cd06c85bae6f

<!-- LOPATA.Events: ["localization:Publisher.NoReleases"] -->

- Опубликованных выпусков пока нет. Направление можно сохранить для будущих релизов.

### 0905 · voice.b570cc9779530a0a

<!-- LOPATA.Events: ["localization:Publisher.NothingUncertain"] -->

- У выбранного выпуска нет неопределённой отправки.

### 0906 · voice.ffbf9bfe94c46eee

<!-- LOPATA.Events: ["localization:Publisher.Paused", "localization:WorkStart.StatusPaused"] -->

- Приостановлена.

### 0907 · voice.7b46a47db378cf8b

<!-- LOPATA.Events: ["localization:Publisher.Ready"] -->

- Готово к проверкам; отправка ещё не проверена.

### 0908 · voice.f9258660fa793b34

<!-- LOPATA.Events: ["localization:Publisher.RepositoryNotFound"] -->

- Репозиторий не найден или не является публичным.

### 0909 · voice.8185072a393dfa5d

<!-- LOPATA.Events: ["localization:Publisher.SecretUnavailable"] -->

- Windows не смогла расшифровать ключ. Подключите ключ заново в этой учётной записи.

### 0910 · voice.a0e25c4c9b91a61f

<!-- LOPATA.Events: ["localization:Publisher.StoreInvalid"] -->

- История публикаций повреждена или имеет неподдерживаемый формат. Она сохранена; отправка заблокирована, чтобы избежать дублей.

### 0911 · voice.e8eb4f051a520ba6

<!-- LOPATA.Events: ["localization:Publisher.StoreUnavailable"] -->

- Не удалось открыть хранилище публикаций. Проверьте доступ к папке и отсутствие другой запущенной копии ЛОПАТЫ.

### 0912 · voice.8f2b1e1592f8d86a

<!-- LOPATA.Events: ["localization:Publisher.Uncertain"] -->

- Требуется проверка: ответ отправки потерян. Повторная отправка заблокирована до проверки стены сообщества.

### 0913 · voice.fa9ef25a81c4e4d6

<!-- LOPATA.Events: ["localization:RagBooks.CheckComplete"] -->

- Проверка файлов завершена. Статус каждой модели показан выше.

### 0914 · voice.eb853deab32eb5a3

<!-- LOPATA.Events: ["localization:RagBooks.DownloadCanceled"] -->

- Загрузка отменена. Полученные части сохранены.

### 0915 · voice.8d010e820e9d688e

<!-- LOPATA.Events: ["localization:RagBooks.DownloadError"] -->

- Не удалось подготовить файлы.

### 0916 · voice.63486831c88ef1c0

<!-- LOPATA.Events: ["localization:RagBooks.DownloadPaused"] -->

- Загрузка приостановлена. Полученные части сохранены для продолжения.

### 0917 · voice.b2c4464bc1f4ca71

<!-- LOPATA.Events: ["localization:RagBooks.DraftReadError"] -->

- Не удалось прочитать сохранённый список. Файл сохранён без изменений; автоматическая перезапись отключена.

### 0918 · voice.4fe312f86f45caf2

<!-- LOPATA.Events: ["localization:RagBooks.DraftSaveError"] -->

- Не удалось сохранить список книг и папку. Проверьте доступ к хранилищу.

### 0919 · voice.be6260c909bae1c1

<!-- LOPATA.Events: ["localization:RagBooks.FilesMissing"] -->

- Файлы отсутствуют или не прошли проверку.

### 0920 · voice.63a21574bca322ae

<!-- LOPATA.Events: ["localization:RagBooks.InsufficientSpace"] -->

- Недостаточно свободного места для исходников и рабочих файлов.

### 0921 · voice.ebc157b828533868

<!-- LOPATA.Events: ["localization:RagBooks.LexiconReady"] -->

- Словари готовы.

### 0922 · voice.62bec7d345f89d5c

<!-- LOPATA.Events: ["localization:RagBooks.NoAdmissibleText"] -->

- В источниках нет допущенных фрагментов. Новая библиотека не выпущена, предыдущая сохранена.

### 0923 · voice.2f46e956eab39c82

<!-- LOPATA.Events: ["localization:RagBooks.NoAutomaticDownload"] -->

- Скачивание начнётся только по вашей команде. Настройка числа соединений общая для всей программы.

### 0924 · voice.c87cc36ce5cc879d

<!-- LOPATA.Events: ["localization:RagBooks.ProcessingError"] -->

- Обработка остановлена. Готовые порции сохранены.

### 0925 · voice.deb1a0d24d9897ec

<!-- LOPATA.Events: ["localization:RagBooks.Quality.invalid_characters"] -->

- Повреждённые символы.

### 0926 · voice.76c0fa97abe285a8

<!-- LOPATA.Events: ["localization:RagBooks.Quality.ocr_component_unavailable"] -->

- Компонент OCR отсутствует.

### 0927 · voice.366d795d6f5acb78

<!-- LOPATA.Events: ["localization:RagBooks.Quality.ocr_failed"] -->

- Не удалось распознать страницу.

### 0928 · voice.0615e86025f84e20

<!-- LOPATA.Events: ["localization:RagBooks.Quality.ocr_language_unavailable"] -->

- Нет языка распознавания RU/EN.

### 0929 · voice.ea31bd66ee648e12

<!-- LOPATA.Events: ["localization:RagBooks.Quality.source_extraction_failed"] -->

- Не удалось разобрать источник.

### 0930 · voice.8f418dfb956833aa

<!-- LOPATA.Events: ["localization:RagBooks.Quality.unsupported_format"] -->

- Неподдерживаемый формат.

### 0931 · voice.3bb9a80eb92bc7de

<!-- LOPATA.Events: ["localization:RagBooks.Ready"] -->

- Библиотека готова.

### 0932 · voice.9b0c32598acb755f

<!-- LOPATA.Events: ["localization:RagBooks.SourceMissing"] -->

- Исходный файл отсутствует. Выберите книгу заново.

### 0933 · voice.b1299627cde15058

<!-- LOPATA.Events: ["localization:RagBooks.SourceReadError"] -->

- Не удалось прочитать исходник. Проверьте доступность файла.

### 0934 · voice.9732e5886ad91294

<!-- LOPATA.Events: ["localization:RagBooks.State.Canceled"] -->

- Загрузка отменена.

### 0935 · voice.8346e8a85cabd979

<!-- LOPATA.Events: ["localization:RagBooks.State.Completed"] -->

- Подготовка файлов завершена.

### 0936 · voice.d2750e014a0a23e2

<!-- LOPATA.Events: ["localization:RagBooks.State.Failed"] -->

- Не удалось подготовить файлы.

### 0937 · voice.741b5c3a533b64d6

<!-- LOPATA.Events: ["localization:RagBooks.State.Pausing"] -->

- Сохраняем полученные части.

### 0938 · voice.110dae5a7d158b89

<!-- LOPATA.Events: ["localization:RagBooks.State.Waiting"] -->

- Ожидает продолжения; при необходимости откройте компоненты.

### 0939 · voice.60c864b2e1dae70c

<!-- LOPATA.Events: ["localization:RagBooks.StoppedCheckpoint"] -->

- Работа остановлена. Контрольные точки сохранены.

### 0940 · voice.c1663dda631ed611

<!-- LOPATA.Events: ["localization:Reader.Ai.InvalidChoice"] -->

- Не удалось сохранить выбор модели.

### 0941 · voice.437a170db958c51b

<!-- LOPATA.Events: ["localization:Reader.Ai.NoSource"] -->

- Для этой модели нет закреплённого источника скачивания. Можно проверить уже установленные файлы.

### 0942 · voice.5ac021732d3e255f

<!-- LOPATA.Events: ["localization:Reader.Cancelled"] -->

- Открытие отменено.

### 0943 · voice.a347e5899b27cdd0

<!-- LOPATA.Events: ["localization:Reader.CopyFailed"] -->

- Не удалось скопировать текст. Буфер обмена занят; попробуйте ещё раз.

### 0944 · voice.1b5a23d61a1dcfab

<!-- LOPATA.Events: ["localization:Reader.InvalidImage"] -->

- Не удалось прочитать изображение или превышен безопасный размер страницы.

### 0945 · voice.9e80e4cf7c5c3211

<!-- LOPATA.Events: ["localization:Reader.NoText"] -->

- В файле нет текстового слоя. Для поиска и копирования нужно распознавание текста.

### 0946 · voice.d00313d806b40f08

<!-- LOPATA.Events: ["localization:Reader.NotFound"] -->

- Текст не найден. В сканах без текстового слоя поиск недоступен.

### 0947 · voice.d644275a56a50808

<!-- LOPATA.Events: ["localization:Reader.OpenFailed"] -->

- Не удалось открыть файл. Он сохранён без изменений.

### 0948 · voice.444be86ef252b1e9

<!-- LOPATA.Events: ["localization:Reader.PreparingBook"] -->

- Подготавливаем страницы книги.

### 0949 · voice.23758f8e10d6d28c

<!-- LOPATA.Events: ["localization:Reader.PreparingOffice"] -->

- Подготавливаем страницы документа.

### 0950 · voice.8a364b59a4236b9c

<!-- LOPATA.Events: ["localization:Reader.RenderFailed"] -->

- Не удалось сформировать страницу. Проверьте файл или попробуйте открыть его снова.

### 0951 · voice.4debfa23c460e54a

<!-- LOPATA.Events: ["localization:Reader.Speech.Cancelled"] -->

- Озвучивание остановлено.

### 0952 · voice.9595f03ef3e2fb28

<!-- LOPATA.Events: ["localization:Reader.Speech.Expired"] -->

- Временное озвучивание завершено: документ был закрыт.

### 0953 · voice.f415f680c3503afd

<!-- LOPATA.Events: ["localization:Reader.Speech.Failed"] -->

- Не удалось озвучить документ. Исходный файл не изменён.

### 0954 · voice.b96141617e71e0e6

<!-- LOPATA.Events: ["localization:Reader.Speech.NoText"] -->

- В выбранном фрагменте нет доступного текста. Страницы-картинки требуют распознавания; оно автоматически не запускается.

### 0955 · voice.8e7264dc26cab801

<!-- LOPATA.Events: ["localization:Reader.Speech.PlaybackFailed"] -->

- Не удалось воспроизвести запись. Её можно сохранить в файл.

### 0956 · voice.4cc4d969897a7af2

<!-- LOPATA.Events: ["localization:Reader.Speech.Ready"] -->

- Озвучка готова. Открыт проигрыватель.

### 0957 · voice.30c9147a4d08d145

<!-- LOPATA.Events: ["localization:Reader.Speech.SaveFailed"] -->

- Не удалось сохранить копию озвучки.

### 0958 · voice.4a4c62ec2e078769

<!-- LOPATA.Events: ["localization:Reader.Speech.Saved"] -->

- Копия озвучки сохранена.

### 0959 · voice.b90e2db594989277

<!-- LOPATA.Events: ["localization:Reader.StatisticsUnavailable"] -->

- Статистика недоступна.

### 0960 · voice.d7b0580548c526b6

<!-- LOPATA.Events: ["localization:Reader.Translation.Cancelled"] -->

- Перевод остановлен. Оригинал сохранён.

### 0961 · voice.d32d76328eb76aa0

<!-- LOPATA.Events: ["localization:Reader.Translation.Expired"] -->

- Документ закрыт. Временный перевод удалён; откройте файл и запустите перевод заново.

### 0962 · voice.8a345d1ca631ede8

<!-- LOPATA.Events: ["localization:Reader.Translation.Failed"] -->

- Не удалось завершить перевод. Исходный файл не изменён.

### 0963 · voice.617d3d6f26b143c7

<!-- LOPATA.Events: ["localization:Reader.Translation.FontMissing"] -->

- Для текста перевода не найден подходящий шрифт. Выберите другой язык или установите шрифт этого языка.

### 0964 · voice.5c8df1fd198a6688

<!-- LOPATA.Events: ["localization:Reader.Translation.LayoutFailed"] -->

- Не удалось подготовить расположение перевода на странице.

### 0965 · voice.0e9cbb4d794b797b

<!-- LOPATA.Events: ["localization:Reader.Translation.NoText"] -->

- В выбранной области нет текстового слоя для перевода. Распознавание скана автоматически не запускается.

### 0966 · voice.b1e8cb3b3a269e4b

<!-- LOPATA.Events: ["localization:Reader.Translation.Reading"] -->

- Подготавливаем текст и расположение на страницах.

### 0967 · voice.eda52f504d16f638

<!-- LOPATA.Events: ["localization:Reader.Translation.Ready"] -->

- Перевод готов. Кнопка ↔ возвращает оригинал.

### 0968 · voice.6fb327bc992c42b6

<!-- LOPATA.Events: ["localization:Reader.Translation.SaveFailed"] -->

- Не удалось сохранить перевод.

### 0969 · voice.06c7417287ddc9f2

<!-- LOPATA.Events: ["localization:Reader.Translation.SavePdf"] -->

- Выберите отдельный PDF: исходный файл и временный перевод перезаписывать нельзя.

### 0970 · voice.ac72a36f1f47ca06

<!-- LOPATA.Events: ["localization:Reader.Translation.Saved"] -->

- Перевод сохранён отдельно.

### 0971 · voice.baf12dfbd6ff53ba

<!-- LOPATA.Events: ["localization:Reader.Translation.TooLong"] -->

- Перевод не помещается в исходную область текста даже уменьшенным шрифтом. Файл не сохранён с обрезанным текстом.

### 0972 · voice.b9a02c6c087b45d0

<!-- LOPATA.Events: ["localization:Reader.Translation.UnsupportedModel"] -->

- Этот формат модели пока не подключён к переводу. Выберите другую текстовую модель в настройках AI.

### 0973 · voice.d3d077f1994f541f

<!-- LOPATA.Events: ["localization:Reader.Unsupported"] -->

- Этот формат пока не поддерживается читалкой.

### 0974 · voice.7c6ecdb53dfe3dda

<!-- LOPATA.Events: ["localization:Reader.Windows.SaveFailed"] -->

- Не удалось сохранить выбор. Прежняя настройка восстановлена.

### 0975 · voice.a89656f0bd70bb23

<!-- LOPATA.Events: ["localization:Reader.Windows.SettingsFailed"] -->

- Не удалось открыть настройки Windows.

### 0976 · voice.86c67552ae185260

<!-- LOPATA.Events: ["localization:Settings.Behavior.AutostartFailed"] -->

- Не удалось изменить автозапуск. Проверьте настройки запуска в Windows; изменение не подтверждено.

### 0977 · voice.84fcf12f66761761

<!-- LOPATA.Events: ["localization:Settings.CoreVoiceRhVoiceUnavailable"] -->

- RHVoice не установлен; подготовьте официальный локальный голос.

### 0978 · voice.ec9d48061b1a7f3a

<!-- LOPATA.Events: ["localization:Settings.CoreVoiceUnavailable"] -->

- Локальный голос сейчас недоступен; текстовые режимы продолжат работать.

### 0979 · voice.9b6fbd913015ac03

<!-- LOPATA.Events: ["localization:SongClip.Canceled"] -->

- Операция приостановлена или отменена. Готовые фоны сохранены.

### 0980 · voice.f0811102ba09dfa6

<!-- LOPATA.Events: ["localization:SongClip.Complete"] -->

- Готово. Клип сохранён в проекте.

### 0981 · voice.9b3281cb4586a9da

<!-- LOPATA.Events: ["localization:SongClip.Finalizing"] -->

- Сохраняем видео и метаданные.

### 0982 · voice.1ecb65a11fb9419f

<!-- LOPATA.Events: ["localization:SongClip.GpuProbeFailed"] -->

- Проверка видеокарты завершилась с ошибкой. Данные сохранены; повторите запуск.

### 0983 · voice.1c88c566b6a97ff5

<!-- LOPATA.Events: ["localization:SongClip.InfiniteResultInvalid"] -->

- Не удалось подтвердить результат InfiniteTalk. Сохранённые участки остаются в проекте; подробности записаны в журнал.

### 0984 · voice.6c8a8e5f7e9d5248

<!-- LOPATA.Events: ["localization:SongClip.OtherOperation"] -->

- Другая фоновая задача выполняется или ожидает решения. Завершите её либо поставьте на паузу через меню значка ЛОПАТЫ в трее, затем продолжите этот проект.

### 0985 · voice.a0e035362645ffb4

<!-- LOPATA.Events: ["localization:SongClip.PickerDeleteH3Warning"] -->

- Удаление основного набора H3 затронет перечисленные варианты и Видеомастерскую. Для их работы потребуется повторная загрузка.

### 0986 · voice.77a2bbaa7d7577fc

<!-- LOPATA.Events: ["localization:SongClip.PickerInventoryLoading"] -->

- Проверяем наличие файлов.

### 0987 · voice.42b77076f6a9ee2d

<!-- LOPATA.Events: ["localization:SongClip.PickerMissing"] -->

- Не удалось получить необходимые данные при подготовке видеоролика. Проверьте соединение и настройки.

### 0988 · voice.7fb9e7d36dff8efd

<!-- LOPATA.Events: ["localization:SongClip.PickerNothingRemovable"] -->

- Удалять нечего: файлы отсутствуют или нужны другим установленным вариантам.

### 0989 · voice.843b222b37282cf1

<!-- LOPATA.Events: ["localization:SongClip.PickerRemovalLoading"] -->

- Рассчитываем список удаления.

### 0990 · voice.9e1ca2c5da80a2bb

<!-- LOPATA.Events: ["localization:SongClip.PlanReady"] -->

- Сцены подготовлены. Освобождаем помощника перед генерацией.

### 0991 · voice.6254118c19e7f4b4

<!-- LOPATA.Events: ["localization:SongClip.PlanReused"] -->

- Используем сохранённый план сцен.

### 0992 · voice.92c95c3c178842e5

<!-- LOPATA.Events: ["localization:SongClip.PlannerModelMissing"] -->

- Не найдена нужная модель помощника. Для Glitchframe требуется модель ядра, для старших вариантов — Qwen3.6-27B из Видеомастерской.

### 0993 · voice.12833d5f9762b7f5

<!-- LOPATA.Events: ["localization:SongClip.Planning"] -->

- Подготавливаем сцены по смыслу песни.

### 0994 · voice.99a3f26c7ff4c608

<!-- LOPATA.Events: ["localization:SongClip.PlanningReferences"] -->

- Уточняем назначение референсов.

### 0995 · voice.91193e023d739e3a

<!-- LOPATA.Events: ["localization:SongClip.Preparing"] -->

- Проверяем и подготавливаем компоненты.

### 0996 · voice.e8a9c2869947ffb1

<!-- LOPATA.Events: ["localization:SongClip.Saved", "localization:Vector.Editor.Saved", "localization:VideoPrep.Saved"] -->

- Проект сохранён.

### 0997 · voice.bec407b554bb804c

<!-- LOPATA.Events: ["localization:SongClip.SnapshotMismatch"] -->

- Сохранённые входные данные попытки отличаются от данных продолжения. Исходная попытка и готовые части сохранены; продолжение остановлено.

### 0998 · voice.f831f1ee0d77be61

<!-- LOPATA.Events: ["localization:SongClip.VisualPlanInvalid"] -->

- Помощник не смог составить корректный план сцен. Входные данные сохранены; генерация по непроверенному плану не запускалась.

### 0999 · voice.4f30c0212c6278ec

<!-- LOPATA.Events: ["localization:SongClip.VisualPlanMismatch"] -->

- Сохранённый план не соответствует входным данным попытки или повреждён. Готовые части сохранены; для изменённых данных начните новую генерацию.

### 1000 · voice.2b622c7e6a1034e7

<!-- LOPATA.Events: ["localization:SpeechAssets.AudioChanged"] -->

- Аудиофайл изменён или недоступен. Создайте новый дубль.

### 1001 · voice.853d8e31998a8131

<!-- LOPATA.Events: ["localization:SpeechAssets.BulkConfirmed", "localization:SpeechAssets.GeneratingLine"] -->

- Создаём следующую речевую запись.

### 1002 · voice.acdbd9d357f5f758

<!-- LOPATA.Events: ["localization:SpeechAssets.Exported"] -->

- ZIP с выбранными подтверждёнными записями сохранён.

### 1003 · voice.7d5fe78233c23ec6

<!-- LOPATA.Events: ["localization:SpeechAssets.Generating"] -->

- Генерация.

### 1004 · voice.f4088c341f185d30

<!-- LOPATA.Events: ["localization:SpeechAssets.GenerationFailed"] -->

- Не удалось завершить генерацию. Готовые дубли сохранены; проверьте компоненты или повторите нужную реплику.

### 1005 · voice.d6e712b400b193b2

<!-- LOPATA.Events: ["localization:SpeechAssets.Imported"] -->

- Список загружен. Текст реплик можно править.

### 1006 · voice.8f081f6be119a7ac

<!-- LOPATA.Events: ["localization:SpeechAssets.Invalid"] -->

- Не удалось прочитать сеанс речевых ассетов.

### 1007 · voice.842c1ec76b4f1394

<!-- LOPATA.Events: ["localization:SpeechAssets.InvalidAudio"] -->

- Запись пуста, повреждена или имеет неподдерживаемый формат.

### 1008 · voice.f3aa38a0faf4372a

<!-- LOPATA.Events: ["localization:SpeechAssets.NoText"] -->

- В списке нет текста для озвучки.

### 1009 · voice.eca33d9f2c5ac4f7

<!-- LOPATA.Events: ["localization:SpeechAssets.RunCompletedWithErrors"] -->

- Завершено с ошибками.

### 1010 · voice.3e43a3f41261ba48

<!-- LOPATA.Events: ["localization:SpeechAssets.Stopped"] -->

- Остановлено. Записи и очередь сохранены; можно продолжить.

### 1011 · voice.16c6859ee60535ca

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioCoreThinking"] -->

- Ядро готовит следующий шаг.

### 1012 · voice.13c65dc2665a60d5

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioCoreUnavailable"] -->

- Ядро сценария недоступно, показан безопасный fallback-шаг.

### 1013 · voice.c55cbb372a486b51

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioFileRemoved"] -->

- Карточка файла убрана из сессии. Оригинал не изменён.

### 1014 · voice.21e3a6801737610b

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioFileSelectionCancelled"] -->

- Выбор файлов отменён.

### 1015 · voice.75449c11c59936dc

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioFileSetupCompleted"] -->

- Файловые вводные сохранены. Теперь выберите глубину сценария.

### 1016 · voice.8dcc3f0f6e6bb298

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioFilesAdded"] -->

- Содержимое файлов модели недоступно.

### 1017 · voice.82eb3fb6a01c6b20

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioRepeatedStep"] -->

- Повторяющийся шаг остановлен защитой от зацикливания.

### 1018 · voice.0d3673e4bfee4cd7

<!-- LOPATA.Events: ["localization:Status.ChoiceScenarioTaskCardReady"] -->

- Карточка подготовки собрана. Скачивание модели пока не запускается.

### 1019 · voice.b9c2b88ba7d0df92

<!-- LOPATA.Events: ["localization:Status.CoreModelCancelRequested"] -->

- Отмена скачивания запрошена. Частичный файл будет сохранён для будущего продолжения.

### 1020 · voice.996e740692cc1e47

<!-- LOPATA.Events: ["localization:Status.CoreModelDownloadFailed"] -->

- Скачивание основного ядра не завершилось. Можно повторить позже.

### 1021 · voice.d8fad07a94675899

<!-- LOPATA.Events: ["localization:Status.CoreModelDownloadPaused"] -->

- Скачивание остановлено. Позже его можно продолжить.

### 1022 · voice.4c2811a973fcbfaa

<!-- LOPATA.Events: ["localization:Status.CoreModelDownloadStarted"] -->

- Начато скачивание основного ядра Qwen3 8B Q4\_K\_M.

### 1023 · voice.5017c2e265dc6fb1

<!-- LOPATA.Events: ["localization:Status.CoreModelFolderUnavailable"] -->

- Папка моделей недоступна. Подключите диск или измените настройки.

### 1024 · voice.39bcadb782aab745

<!-- LOPATA.Events: ["localization:Status.CoreModelMissing"] -->

- Основное ядро Qwen3 8B Q4\_K\_M не найдено.

### 1025 · voice.f9dbb2fd5d291622

<!-- LOPATA.Events: ["localization:Status.CoreModelPartial"] -->

- Найдена частичная загрузка основного ядра.

### 1026 · voice.0cd9f94af0ae10e6

<!-- LOPATA.Events: ["localization:Status.CoreModelPauseRequested"] -->

- Остановка скачивания запрошена. Частичный файл будет сохранён.

### 1027 · voice.fe16d5737ab570ee

<!-- LOPATA.Events: ["localization:Status.CoreModelPostponed"] -->

- Скачивание основного ядра отложено.

### 1028 · voice.2474a53faac8b9c9

<!-- LOPATA.Events: ["localization:Status.CoreModelStorageNotConfigured"] -->

- Папка моделей не выбрана. Откройте настройки хранения.

### 1029 · voice.050ea8ce338fa423

<!-- LOPATA.Events: ["localization:Status.CoreVoiceTestCompleted"] -->

- Проверочная реплика ядра завершена.

### 1030 · voice.ec86dfed56152f54

<!-- LOPATA.Events: ["localization:Status.CoreVoiceUnavailable"] -->

- Голос ядра недоступен. Текстовый режим продолжает работать.

### 1031 · voice.09e7e45fbe5ed9a0

<!-- LOPATA.Events: ["localization:Status.DebugChatOpenFailed"] -->

- Не удалось открыть отладочное окно проверки моделей.

### 1032 · voice.1674658da087c2bd

<!-- LOPATA.Events: ["localization:Status.ExecutorCancelled"] -->

- Работа исполнителя отменена.

### 1033 · voice.027fd6ffa6413179

<!-- LOPATA.Events: ["localization:Status.ExecutorCannotContinue"] -->

- Исполнитель честно сообщил, что продолжить работу нельзя.

### 1034 · voice.09805f095614c928

<!-- LOPATA.Events: ["localization:Status.ExecutorCompleted"] -->

- Модель-исполнитель завершила работу.

### 1035 · voice.3793a94936a745bb

<!-- LOPATA.Events: ["localization:Status.ExecutorCustomEmpty"] -->

- Свой ответ не может быть пустым.

### 1036 · voice.49a456dd3a0370b8

<!-- LOPATA.Events: ["localization:Status.ExecutorDownloadCancelled"] -->

- Загрузка исполнителя отменена.

### 1037 · voice.7ccebfc7c0b31919

<!-- LOPATA.Events: ["localization:Status.ExecutorDownloadFailed", "source:b6249d2579beb83a", "source:1fe50a293b3b848c", "source:62b8bfd64f4eb2a0", "source:d02f02362026f8c5", "source:de7b9f2a4793dceb"] -->

- Не удалось получить необходимые данные при выполнении операции. Проверьте соединение и настройки.

### 1038 · voice.73a8ed7dfb594d54

<!-- LOPATA.Events: ["localization:Status.ExecutorFinalReady"] -->

- Итоговый документ открыт; сессия завершена.

### 1039 · voice.47ffcd1c0a8e0e63

<!-- LOPATA.Events: ["localization:Status.ExecutorFinishCancelled"] -->

- Завершение отменено; сессия продолжается.

### 1040 · voice.2e5164aa6583c0d3

<!-- LOPATA.Events: ["localization:Status.ExecutorFinishedWithoutResult"] -->

- Сессия завершена без формирования результата.

### 1041 · voice.c710d4c164c63ef0

<!-- LOPATA.Events: ["localization:Status.ExecutorReadyToFinish"] -->

- Исполнитель считает результат готовым; решение о завершении остаётся за вами.

### 1042 · voice.496a399589647a89

<!-- LOPATA.Events: ["localization:Status.ExecutorResultReady"] -->

- Текущая версия результата готова; сессия остаётся открытой.

### 1043 · voice.a5a0d481a59a37c3

<!-- LOPATA.Events: ["localization:Status.ExecutorRuntimeValidation"] -->

- Модель загружается в AI-backend и проходит проверку запуска.

### 1044 · voice.639126e679b31f2a

<!-- LOPATA.Events: ["localization:Status.ExecutorSessionFinished"] -->

- Сессия исполнителя завершена пользователем.

### 1045 · voice.3d306a01643f88ad

<!-- LOPATA.Events: ["localization:Status.ExecutorSnapshotPreparing"] -->

- Исполнитель готовит отдельный документ с текущим результатом.

### 1046 · voice.5bff62c204511910

<!-- LOPATA.Events: ["localization:Status.ExecutorStageReady"] -->

- Стартовые данные собраны и ожидают вашего подтверждения.

### 1047 · voice.7f277d5df793ca7f

<!-- LOPATA.Events: ["localization:Status.ExecutorVerifying"] -->

- Проверяется загруженная модель-исполнитель.

### 1048 · voice.358580855da44ece

<!-- LOPATA.Events: ["localization:Status.ExternalComponentSearchEmpty"] -->

- Внешний поиск завершён, но подходящих вариантов не найдено.

### 1049 · voice.b5d3c8bdc013cdfb

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisAlphaBundleReady"] -->

- Модель и проектор режима «Альфа» проверены и готовы к запуску.

### 1050 · voice.17d9d859ef79bec3

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisBetaBundleReady"] -->

- Модель и проектор режима «Бета» проверены и готовы к запуску.

### 1051 · voice.5090a81118ef304b

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisBundleReady"] -->

- Комплект «Бета» проверен и готов к работе.

### 1052 · voice.828dd7e12bcabb1e

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisBundleUpdated"] -->

- Состояние комплекта «Бета» обновлено.

### 1053 · voice.533d90c53a2ed383

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisCancelled"] -->

- Операция отменена, сохранённые данные сессии не потеряны.

### 1054 · voice.2846c2838da720b0

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisCompleted"] -->

- Работа завершена и сохранена в истории без внутренней копии изображения.

### 1055 · voice.c4d2b4b3aa9e77e2

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisCompletedWithBackup"] -->

- Работа завершена; внутренняя копия изображения и описания создана.

### 1056 · voice.2f640f4d6be40ddd

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisFileRejected"] -->

- Файл не прошёл безопасную проверку.

### 1057 · voice.b55019009fcdafd9

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisHeavyBundleReady"] -->

- Комплект Гаммы Empero Ridge 27B проверен и готов к запуску.

### 1058 · voice.b7526b2dd97ad412

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisHeavyMemoryPressure"] -->

- Сессия сохранена; перезапустите режим «Гамма» для нового расчёта памяти.

### 1059 · voice.b52062727db47b97

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisHistoryReadOnly"] -->

- История открыта только для чтения — модель сейчас недоступна.

### 1060 · voice.620ceaa5c7fc129b

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisModelsDeferred"] -->

- Предварительная подготовка не завершена; нужная модель запустится при начале анализа.

### 1061 · voice.0b215c719027f991

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisModelsPreparing"] -->

- Заранее подготавливаю модели выбранного сценария.

### 1062 · voice.56e3bf52168de1f2

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisModelsReady"] -->

- Предварительная подготовка моделей завершена.

### 1063 · voice.43468777c914cfba

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisOperationCancelled"] -->

- Операция остановлена; частичная загрузка сохранена.

### 1064 · voice.75e79da61f5d7edf

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisOperationFailed"] -->

- Операция не завершена; откройте состояние компонентов для следующего действия.

### 1065 · voice.ba7f7d0a21af7e13

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisResultReady"] -->

- Литературное описание готово и сохранено как версия.

### 1066 · voice.bc45b0a655964382

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisRevisionReady"] -->

- Новая версия описания готова.

### 1067 · voice.60ba9a7cc094c85c

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisSessionMissing"] -->

- Сохранённая сессия не найдена.

### 1068 · voice.ae92029319a2eeeb

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisSessionResumed"] -->

- Сохранённая работа открыта.

### 1069 · voice.d1868777b0b570f0

<!-- LOPATA.Events: ["localization:Status.ImageAnalysisVisionRemoveFailed"] -->

- Файлы визуального аналитика не удалены из-за защитного ограничения.

### 1070 · voice.a389aeb2f7ed645e

<!-- LOPATA.Events: ["localization:Status.Initial", "xaml:Исходники/AIHub/MainWindow.xaml#StatusText"] -->

- Прототип стартового окна. Скачивание моделей и изменение настроек пока не выполняются.

### 1071 · voice.a6b68c6f859ba4bb

<!-- LOPATA.Events: ["localization:Status.LanguageSaved"] -->

- Язык интерфейса сохранён.

### 1072 · voice.452d1474a46d3887

<!-- LOPATA.Events: ["localization:Status.PassportReadySetupComplete"] -->

- Настройка завершена. Можно начать работу или изменить параметры через Перенастроить.

### 1073 · voice.87cf1beb97e6ff62

<!-- LOPATA.Events: ["localization:Status.PassportReadySetupIncomplete"] -->

- Паспорт компьютера готов. Настройка пока не завершена.

### 1074 · voice.c3f3c8b4ec344cd0

<!-- LOPATA.Events: ["localization:Status.PreviousWorkActiveDeleteBlocked"] -->

- Открытую сессию нельзя удалить. Сначала вернитесь к списку или завершите её.

### 1075 · voice.534c8adcc321923e

<!-- LOPATA.Events: ["localization:Status.PreviousWorkDeleted"] -->

- Выполняется следующий этап задачи.

### 1076 · voice.5e2ceec5a202bd93

<!-- LOPATA.Events: ["localization:Status.PreviousWorkExecutorMissing"] -->

- Сессия поднята из архива, но выбранный исполнитель не установлен. Требуется скачивание.

### 1077 · voice.130e3c0467b911a1

<!-- LOPATA.Events: ["localization:Status.PreviousWorkExpanded"] -->

- Показаны сохранённые сессии.

### 1078 · voice.3bb26a05e7b31942

<!-- LOPATA.Events: ["localization:Status.PreviousWorkRenamed"] -->

- Название сессии сохранено.

### 1079 · voice.53791c3706885fd6

<!-- LOPATA.Events: ["localization:Status.PreviousWorkUnavailable"] -->

- Архив сессии недоступен или повреждён.

### 1080 · voice.0eb10440864c9374

<!-- LOPATA.Events: ["localization:Status.ProfileSavedComplete"] -->

- Профиль сохранён и заполнен полностью.

### 1081 · voice.04ca90f5fe9885b0

<!-- LOPATA.Events: ["localization:Status.ProfileSavedIncomplete"] -->

- Профиль сохранён, но часть полезных данных пока не заполнена.

### 1082 · voice.bc0606f52016d57d

<!-- LOPATA.Events: ["localization:Status.RerankerModelDownloadStarted"] -->

- Начато скачивание вспомогательной модели поиска.

### 1083 · voice.397ae4698255dac9

<!-- LOPATA.Events: ["localization:Status.RerankerModelMissing"] -->

- Вспомогательная модель поиска пока не установлена.

### 1084 · voice.7c9bde2db01f2a4b

<!-- LOPATA.Events: ["localization:Status.RerankerModelPostponed"] -->

- Скачивание вспомогательной модели поиска отложено.

### 1085 · voice.43f96596e43ea946

<!-- LOPATA.Events: ["localization:Status.SetupOpenFailed"] -->

- Не удалось открыть настройку или обновить паспорт компьютера.

### 1086 · voice.b5334fd0fdfb5d55

<!-- LOPATA.Events: ["localization:Status.SetupOpened"] -->

- Открыта страница настройки. Реальное изменение параметров пока не выполняется.

### 1087 · voice.775702fb838d2bf2

<!-- LOPATA.Events: ["localization:Status.StorageSavedIncomplete"] -->

- Настройки сохранены, но для завершения нужно добавить хотя бы один адрес для моделей и один адрес для результатов.

### 1088 · voice.b5b12423d3e3d241

<!-- LOPATA.Events: ["localization:Studio.Context.Changed"] -->

- Беседа изменилась после открытия окна. Закройте окно и откройте его снова; исходные данные сохранены.

### 1089 · voice.e4e48faa11e4641d

<!-- LOPATA.Events: ["localization:Studio.Context.Empty"] -->

- Нет выбранных данных или пересказ пуст. Исходный контекст сохранён.

### 1090 · voice.2db949519b0cef02

<!-- LOPATA.Events: ["localization:Studio.Context.GoalNotMet"] -->

- Пересказ готов, но желаемое сокращение пока не достигнуто. Можно сжать сильнее или принять после проверки.

### 1091 · voice.2603f6af690a894a

<!-- LOPATA.Events: ["localization:Studio.Context.NoOlder"] -->

- Нет старых данных для сжатия с сохранением текущего варианта. Используйте пересказ или ручную очистку.

### 1092 · voice.55436b9c28780676

<!-- LOPATA.Events: ["localization:Studio.Context.PassTooSmall"] -->

- Доступное окно слишком мало даже для одного прохода сжатия. Освободите память видеокарты или уменьшите выбранные материалы.

### 1093 · voice.7cd5004cf31056bb

<!-- LOPATA.Events: ["localization:Studio.Context.Rejected"] -->

- Запрос не поместился в контекст модели. Текст запроса сохранён.

### 1094 · voice.0810ca7b0271370d

<!-- LOPATA.Events: ["localization:Studio.ExternalConflict"] -->

- Файл главы изменён вне программы. Местный ввод сохранён в окне; сначала согласуйте обе версии.

### 1095 · voice.919a138965a7bcdd

<!-- LOPATA.Events: ["localization:Studio.PartChoice.PrepareFailed"] -->

- Не удалось сохранить или обновить материалы. Запрос оставлен в поле ввода.

### 1096 · voice.98522ea00b75261e

<!-- LOPATA.Events: ["localization:Studio.PreparingTask"] -->

- Советник готовит задание Писателю.

### 1097 · voice.cd4574d3101d68c0

<!-- LOPATA.Events: ["localization:Studio.Route.Invalid"] -->

- Не удалось прочитать маршрут. Проверьте данные проекта; исходные данные сохранены.

### 1098 · voice.8a90ab3095f62a28

<!-- LOPATA.Events: ["localization:Studio.Route.OpenError"] -->

- Не удалось открыть или обновить маршрут. Проверьте доступ к проекту.

### 1099 · voice.0b8f37dd3f347554

<!-- LOPATA.Events: ["localization:Studio.Route.SaveError"] -->

- Не удалось сохранить маршрут. Возможно, проект изменился в другом окне.

### 1100 · voice.1555665ffa750af7

<!-- LOPATA.Events: ["localization:Studio.TaskReady"] -->

- Задание готово. Выберите действие Писателя.

### 1101 · voice.6d51dc19102f1dc8

<!-- LOPATA.Events: ["localization:Tray.ExitBlocked"] -->

- Выход пока невозможен: завершите действие в открытом вспомогательном окне или сохраните изменения.

### 1102 · voice.f53afd90c44cf6ca

<!-- LOPATA.Events: ["localization:Tray.ExitFailed"] -->

- Не удалось подтвердить сохранение задачи или освобождение моделей. Программа остаётся открытой.

### 1103 · voice.620e3fa77edd9687

<!-- LOPATA.Events: ["localization:Tray.Exiting"] -->

- Сохраняем задачу и освобождаем модели перед выходом.

### 1104 · voice.9395802e14912ee5

<!-- LOPATA.Events: ["localization:Tray.OperationFailed"] -->

- Не удалось подтвердить паузу или продолжение задачи. Результат не отмечен как готовый; подробности сохранены в журнале.

### 1105 · voice.41c71d224bdbd35d

<!-- LOPATA.Events: ["localization:Tray.PausedTasks"] -->

- Продолжить задание на паузе.

### 1106 · voice.7d86a01c26a28261

<!-- LOPATA.Events: ["localization:Tray.RestoreFailed"] -->

- Не удалось восстановить фоновую задачу. Исходная контрольная точка сохранена; требуется проверка.

### 1107 · voice.5d33df4ecf9839bf

<!-- LOPATA.Events: ["localization:Tray.ResultReady"] -->

- Результат готов.

### 1108 · voice.44a98cae67a13e99

<!-- LOPATA.Events: ["localization:Tray.ToolOutcomeUnknown"] -->

- Результат прерванного действия не подтверждён. Повтор заблокирован, чтобы не изменить файлы дважды.

### 1109 · voice.1d4c05b455abf9f0

<!-- LOPATA.Events: ["localization:Updates.CheckFailed"] -->

- Не удалось проверить обновления. Проверьте подключение к интернету и попробуйте позже.

### 1110 · voice.f408cb48777b33fd

<!-- LOPATA.Events: ["localization:Updates.Checking"] -->

- Проверяем наличие обновлений.

### 1111 · voice.2b4086a0c936e85d

<!-- LOPATA.Events: ["localization:Updates.CurrentLatest"] -->

- Для выбранного направления новых обновлений нет.

### 1112 · voice.c8d48dea513d0b1d

<!-- LOPATA.Events: ["localization:Updates.DownloadFailed"] -->

- Не удалось скачать обновление. Проверьте интернет, свободное место на диске и отсутствие загрузки в другом экземпляре программы.

### 1113 · voice.370eba11af1bb74d

<!-- LOPATA.Events: ["localization:Updates.Downloading"] -->

- Скачиваем обновление.

### 1114 · voice.f463bb227eae8fce

<!-- LOPATA.Events: ["localization:Updates.HealthFailed"] -->

- Не удалось подтвердить исправность обновления. При следующем запуске программа попробует восстановить предыдущую версию; ваши проекты сохранятся.

### 1115 · voice.c2a6b47b2f64c413

<!-- LOPATA.Events: ["localization:Updates.InstallFailed"] -->

- Не удалось назначить или запустить установку. Скачанное обновление сохранено; можно повторить попытку.

### 1116 · voice.514c33d6f2cd376b

<!-- LOPATA.Events: ["localization:Updates.InvalidFile"] -->

- Файл обновления не прошёл проверку. Повторите скачивание.

### 1117 · voice.69d79700d5b16328

<!-- LOPATA.Events: ["localization:Updates.LaunchFailed"] -->

- Не удалось запустить проверку обновления. Восстановите программу полным установщиком.

### 1118 · voice.5187aaf8e4337b1c

<!-- LOPATA.Events: ["localization:Updates.Paused"] -->

- Загрузка приостановлена. Скачанная часть сохранена для продолжения.

### 1119 · voice.d1f78e738f270cf1

<!-- LOPATA.Events: ["localization:Updates.PlanFailed"] -->

- Не удалось проверить состав установленной программы. Файлы могли измениться; полный установщик поможет восстановить установку.

### 1120 · voice.fd661be25b473998

<!-- LOPATA.Events: ["localization:Updates.RetryingDownload"] -->

- Соединение прервалось или сервер не отвечает. Повторяем загрузку; скачанные части сохранены.

### 1121 · voice.ca906bfc35d3b78b

<!-- LOPATA.Events: ["localization:Updates.SettingsFailed"] -->

- Не удалось сохранить направление обновлений. Проверьте доступ к папке настроек.

### 1122 · voice.c5dc457e576cd4ce

<!-- LOPATA.Events: ["localization:Updates.StateFailed"] -->

- Не удалось прочитать состояние обновлений. Восстановите программу полным установщиком; пользовательские проекты удалять не нужно.

### 1123 · voice.e25fddaab8fe63d9

<!-- LOPATA.Events: ["localization:Updates.Verifying"] -->

- Проверяем файлы обновления.

### 1124 · voice.ba0c0f42084dc1c4

<!-- LOPATA.Events: ["localization:Vector.AutoSaved"] -->

- SVG сохранён автоматически.

### 1125 · voice.2503cdeabc76ac03

<!-- LOPATA.Events: ["localization:Vector.Canceled"] -->

- Отменено.

### 1126 · voice.23eb89aedcfafdbb

<!-- LOPATA.Events: ["localization:Vector.Editor.Error.Archive"] -->

- Повреждённый или слишком большой архив проекта.

### 1127 · voice.19ee7d7c12b1dace

<!-- LOPATA.Events: ["localization:Vector.Editor.Error.Geometry"] -->

- Геометрия этой фигуры не поддерживается для Пластики. Исходник сохранён.

### 1128 · voice.724e078239bf5c6a

<!-- LOPATA.Events: ["localization:Vector.Editor.Error.Schema"] -->

- Версия проекта пока не поддерживается. Откройте его в более новой ЛОПАТЕ.

### 1129 · voice.a0fbc6cdb84e0f1e

<!-- LOPATA.Events: ["localization:Vector.Editor.Error.Script"] -->

- Не удалось изменить холст. Сохранённый проект не повреждён; переключите лист или повторно откройте его.

### 1130 · voice.33e05e9ccf99273e

<!-- LOPATA.Events: ["localization:Vector.Editor.Error.Ungroup"] -->

- Эту группу нельзя разобрать без изменения общего эффекта. Откройте её детали в дереве.

### 1131 · voice.5917744e98217fe1

<!-- LOPATA.Events: ["localization:Vector.Editor.Exported"] -->

- Экспорт завершён.

### 1132 · voice.edc89839481f55ee

<!-- LOPATA.Events: ["localization:Vector.Editor.GenerationCanceled"] -->

- Генерация остановлена. Готовые объекты сохранены.

### 1133 · voice.f323c43d6b5b07d9

<!-- LOPATA.Events: ["localization:Vector.Editor.GenerationReady"] -->

- Объект готов и сохранён в библиотеке проекта.

### 1134 · voice.b22b90dd3b929160

<!-- LOPATA.Events: ["localization:Vector.Editor.NoRecovery"] -->

- Несохранённых черновиков нет.

### 1135 · voice.9ef5a7531832b909

<!-- LOPATA.Events: ["localization:Vector.Editor.Recovered"] -->

- Черновик сохранён для восстановления.

### 1136 · voice.6342a0ba1354f6e2

<!-- LOPATA.Events: ["localization:Vector.Editor.RecoveryRestored"] -->

- Восстановлены несохранённые изменения. Исходный файл проекта пока не изменён.

### 1137 · voice.b2d8d06285c2ce04

<!-- LOPATA.Events: ["localization:Vector.Editor.StyleCaptured"] -->

- Выделение сохранено как ориентир стиля для следующего запроса.

### 1138 · voice.1f96b92f61369272

<!-- LOPATA.Events: ["localization:Vector.Error.Busy"] -->

- Другая операция ещё выполняется.

### 1139 · voice.155653c349626c74

<!-- LOPATA.Events: ["localization:Vector.Error.Complexity"] -->

- SVG слишком сложен для безопасного предпросмотра. Ответ сохранён.

### 1140 · voice.fb52fa240830225b

<!-- LOPATA.Events: ["localization:Vector.Error.Context"] -->

- Закончился доступный контекст модели. SVG не завершён.

### 1141 · voice.b93f0218ab10eceb

<!-- LOPATA.Events: ["localization:Vector.Error.Encode"] -->

- Не удалось закодировать картинку в выбранном формате. Попробуйте PNG или другой размер.

### 1142 · voice.226e5c882cea21bf

<!-- LOPATA.Events: ["localization:Vector.Error.Export"] -->

- Не удалось сохранить SVG: файл с таким именем уже содержит другой результат.

### 1143 · voice.9468e5a1d9dd8e79

<!-- LOPATA.Events: ["localization:Vector.Error.ExportDestination"] -->

- Выберите новый файл картинки вне служебной папки заданий SVG. Исходники и внутренние файлы нельзя заменять экспортом.

### 1144 · voice.f403b65960ca7476

<!-- LOPATA.Events: ["localization:Vector.Error.ExportMemory"] -->

- Для этого разрешения сейчас недостаточно свободной памяти. Уменьшите размер или освободите память.

### 1145 · voice.4d6a177c24bc4894

<!-- LOPATA.Events: ["localization:Vector.Error.FormatDimensions"] -->

- Этот размер не поддерживается форматом: JPEG — до 65500 пикселей по стороне, WebP — до 16383. Выберите PNG или уменьшите размер.

### 1146 · voice.323ab3283c009d28

<!-- LOPATA.Events: ["localization:Vector.Error.IncompleteSvg"] -->

- Модель не закончила SVG. Частичный ответ сохранён для диагностики.

### 1147 · voice.e178816eec2e0bde

<!-- LOPATA.Events: ["localization:Vector.Error.InvalidSvg"] -->

- Модель вернула некорректный SVG. Ответ сохранён; попробуйте уточнить запрос.

### 1148 · voice.5dd22dc52541b431

<!-- LOPATA.Events: ["localization:Vector.Error.Job"] -->

- Не удалось прочитать сохранённую задачу SVG.

### 1149 · voice.6460c87282d9630c

<!-- LOPATA.Events: ["localization:Vector.Error.Model"] -->

- Модель этого задания больше не поддерживается. Сохранённые файлы оставлены; отмените старое задание и выберите Qwen.

### 1150 · voice.a0951d01ea6bdd28

<!-- LOPATA.Events: ["localization:Vector.Error.ModelMissing"] -->

- Компонент не найден. Вернитесь к экрану подготовки и проверьте файлы.

### 1151 · voice.ac3d53daabc04271

<!-- LOPATA.Events: ["localization:Vector.Error.Render"] -->

- Не удалось отрисовать SVG. Исходный ответ сохранён.

### 1152 · voice.925ef613eabe34b7

<!-- LOPATA.Events: ["localization:Vector.Error.RendererMissing"] -->

- Не найден компонент отрисовки SVG. Восстановите файлы программы.

### 1153 · voice.134f513d22c2b382

<!-- LOPATA.Events: ["localization:Vector.Error.UnsafeSvg"] -->

- SVG содержит неподдерживаемые элементы или внешние ресурсы. Ответ сохранён.

### 1154 · voice.feff0f5100f759c2

<!-- LOPATA.Events: ["localization:Vector.Error.ViewBox"] -->

- В SVG отсутствует корректная область изображения viewBox. Ответ сохранён.

### 1155 · voice.252314270807e3d9

<!-- LOPATA.Events: ["localization:Vector.Examples.CopyFailed"] -->

- Не удалось скопировать запрос. Попробуйте ещё раз.

### 1156 · voice.1d1149ebcf419e04

<!-- LOPATA.Events: ["localization:Vector.Generating"] -->

- Создаётся SVG.

### 1157 · voice.ca94990ff09c7f78

<!-- LOPATA.Events: ["localization:Vector.Live.Unavailable"] -->

- Черновик пока недоступен. Генерация продолжается.

### 1158 · voice.75721d11c754e2d5

<!-- LOPATA.Events: ["localization:Vector.Live.Waiting"] -->

- Создаётся SVG. Ожидаем первый черновик.

### 1159 · voice.eace8cc355f4109a

<!-- LOPATA.Events: ["localization:Vector.Preparing"] -->

- Подготовка компонентов.

### 1160 · voice.03135fa213a80f1d

<!-- LOPATA.Events: ["localization:Vector.ZoomError"] -->

- Не удалось обновить детализацию из SVG. Отображается временное превью; проверьте компонент отрисовки.

### 1161 · voice.576f482d9a7e6946

<!-- LOPATA.Events: ["localization:VideoEditor.AnalysisInvalid"] -->

- Qwen вернул некорректный черновик. Ответ сохранён, автоматического повтора нет.

### 1162 · voice.c8a1f66224e3bf90

<!-- LOPATA.Events: ["localization:VideoEditor.ArchiveChecksum"] -->

- Содержимое архива не прошло проверку целостности.

### 1163 · voice.3e73ef8cbb203f4f

<!-- LOPATA.Events: ["localization:VideoEditor.ArchiveDiskSpace"] -->

- Недостаточно свободного места для открытия архива проекта.

### 1164 · voice.0601de90f447f57f

<!-- LOPATA.Events: ["localization:VideoEditor.ArrangingMusic"] -->

- Подготавливается музыкальная подложка.

### 1165 · voice.780569f4fca52620

<!-- LOPATA.Events: ["localization:VideoEditor.EventsUnavailable"] -->

- Не удалось прочитать измерения смены кадров. Разбиение не применено.

### 1166 · voice.ce70ece7b5ca40e4

<!-- LOPATA.Events: ["localization:VideoEditor.ExportInvalid"] -->

- Не удалось подтвердить правильность экспортированного файла.

### 1167 · voice.8f0343ae027d84c4

<!-- LOPATA.Events: ["localization:VideoEditor.Failed"] -->

- Операция не завершена. Проект и исходники сохранены.

### 1168 · voice.642d3f134bc641c8

<!-- LOPATA.Events: ["localization:VideoEditor.FragmentReady"] -->

- Черновик фрагмента готов.

### 1169 · voice.5b65ebc65aafc6c9

<!-- LOPATA.Events: ["localization:VideoEditor.Interrupted"] -->

- Операция приостановлена или отменена. Проект сохранён.

### 1170 · voice.6e4eb52e74e72a92

<!-- LOPATA.Events: ["localization:VideoEditor.InvalidArchive"] -->

- Архив видеопроекта повреждён или имеет неподдерживаемую структуру.

### 1171 · voice.e29ed903d380546c

<!-- LOPATA.Events: ["localization:VideoEditor.InvalidProject"] -->

- Проект повреждён или содержит неподдерживаемые параметры.

### 1172 · voice.a824d2cbaa265865

<!-- LOPATA.Events: ["localization:VideoEditor.MissingSource"] -->

- Исходный файл не найден. Используйте «Найти исходник».

### 1173 · voice.59c418c62b4d6799

<!-- LOPATA.Events: ["localization:VideoEditor.MusicInvalid"] -->

- Не удалось подготовить музыкальную подложку.

### 1174 · voice.db0129652491693f

<!-- LOPATA.Events: ["localization:VideoEditor.NoVideoInFragment"] -->

- В выбранном текстовом интервале нет видеоклипа. Проверьте границы фрагмента или положение видео на ленте.

### 1175 · voice.b65384f05b9b5448

<!-- LOPATA.Events: ["localization:VideoEditor.OpenFailed"] -->

- Не удалось открыть видеопроект. Проверьте архив, доступность файлов и свободное место.

### 1176 · voice.9d5e42b495f1e783

<!-- LOPATA.Events: ["localization:VideoEditor.PreparationFailed"] -->

- Подготовка не завершена. Можно повторить проверку или скачивание.

### 1177 · voice.cd596bbed043804b

<!-- LOPATA.Events: ["localization:VideoEditor.Preparing"] -->

- Подготовка материала.

### 1178 · voice.fdd35c954930dd52

<!-- LOPATA.Events: ["localization:VideoEditor.PreviewFailed"] -->

- Не удалось открыть предпросмотр.

### 1179 · voice.59fe0a8f32b8a7d6

<!-- LOPATA.Events: ["localization:VideoEditor.ProtectedSource"] -->

- Нельзя заменить исходник или другой проект этим файлом.

### 1180 · voice.4aa873f10decfde3

<!-- LOPATA.Events: ["localization:VideoEditor.Ready"] -->

- Готово. Исходники не изменяются.

### 1181 · voice.c24f5983e0716ec7

<!-- LOPATA.Events: ["localization:VideoEditor.RecoveryFailed"] -->

- Не удалось сохранить восстановление.

### 1182 · voice.24de8824e8686b02

<!-- LOPATA.Events: ["localization:VideoEditor.RuntimeFailed"] -->

- Ошибка обработки видео.

### 1183 · voice.f0901a26715c242f

<!-- LOPATA.Events: ["localization:VideoEditor.RuntimeMissing"] -->

- Компонент видеоредактора отсутствует.

### 1184 · voice.dffe864d6578d573

<!-- LOPATA.Events: ["localization:VideoEditor.SavedArchive"] -->

- Проект сохранён вместе со всеми материалами.

### 1185 · voice.65319a7c7063b42c

<!-- LOPATA.Events: ["localization:VideoEditor.SavedReferences"] -->

- Проект сохранён. Он содержит монтаж и ссылки на исходные файлы; не перемещайте их без перепривязки.

### 1186 · voice.9a0ac1d7f283ea21

<!-- LOPATA.Events: ["localization:VideoEditor.SpacingLocked"] -->

- Не удалось раздвинуть серии: одна из перемещаемых дорожек заблокирована.

### 1187 · voice.a15d01ccf92bfeb3

<!-- LOPATA.Events: ["localization:VideoEditor.Speaking"] -->

- Создаётся озвучка.

### 1188 · voice.eb58a305c1f3652f

<!-- LOPATA.Events: ["localization:VideoEditor.SpeechFailed"] -->

- Не удалось создать озвучку.

### 1189 · voice.267f6407b7e946c7

<!-- LOPATA.Events: ["localization:VideoEditor.SpeechLongSummary"] -->

- Озвучка готова. Интервалы сохранены; сократите текст или ускорьте реплику вручную.

### 1190 · voice.fcb222855b9010ac

<!-- LOPATA.Events: ["localization:VideoEditor.UnsupportedMedia"] -->

- Этот файл или выбранный поток не поддерживается.

### 1191 · voice.bf75f628ef13683f

<!-- LOPATA.Events: ["localization:VideoEditor.VoiceUnavailable"] -->

- Выбранный диктор не установлен или его параметры недоступны. Подмена голоса не выполняется.

### 1192 · voice.cd8b60a982e5d7e5

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.Cancelled"] -->

- Операция остановлена. Проект не изменён.

### 1193 · voice.79393b6a4fea72dc

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.GpuMemory"] -->

- Для выбранного диктора недостаточно свободной видеопамяти. Освободите её или выберите CPU.

### 1194 · voice.afda7824c482905f

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.Pending"] -->

- Подключение ещё не завершено.

### 1195 · voice.620213e262f1bb29

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.Prepared"] -->

- Подготовка завершена. Реальный синтез ещё не проверялся.

### 1196 · voice.bab3c6199aa37531

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.RecordedExampleMissing"] -->

- Записанный пример отсутствует в установленной сборке.

### 1197 · voice.bb15c6c38bf686dd

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.RecordedExampleSynthesisFailure"] -->

- Эту фразу не удалось записать штатным исполнителем модели. Текст и название не подменялись.

### 1198 · voice.46757c4267cea0fc

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.RecordedLanguageUnsupported"] -->

- Модель не поддерживает этот язык. Записанного примера нет.

### 1199 · voice.dc019c824755618d

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.RecordedMixedScriptFailure"] -->

- Тестовая фраза не прочитана: штатный Vosk завершает синтез с ошибкой на латинице. Написание LOPATA не заменялось.

### 1200 · voice.f96e814d4b92d46f

<!-- LOPATA.Events: ["localization:VideoEditor.Voices.Removed"] -->

- Файлы модели удалены. Готовая озвучка сохранена.

### 1201 · voice.de24a088d98f72d2

<!-- LOPATA.Events: ["localization:VideoPrep.AiCanceled"] -->

- Улучшение отменено. Текст и прежние версии сохранены.

### 1202 · voice.92fcc9dcda8dd3c2

<!-- LOPATA.Events: ["localization:VideoPrep.AiContextLimit"] -->

- Задание остановлено: материалы этого шага вместе с ответом не помещаются в контекст выбранной модели. Все тексты сохранены.

### 1203 · voice.c6c475565c50c933

<!-- LOPATA.Events: ["localization:VideoPrep.AiError"] -->

- Не удалось улучшить замысел. Текст и история сохранены.

### 1204 · voice.f715febdcc4b9eed

<!-- LOPATA.Events: ["localization:VideoPrep.AiIncomplete"] -->

- Ответ ИИ не завершён. Частичный результат не заменяет ваш текст.

### 1205 · voice.4c2c6f94c8383465

<!-- LOPATA.Events: ["localization:VideoPrep.AiInvalidReceipt"] -->

- Не удалось проверить сохранённый результат улучшения. Текст не заменён.

### 1206 · voice.34dd046c6107b5a8

<!-- LOPATA.Events: ["localization:VideoPrep.AiInvalidRequest"] -->

- Сохранённые параметры улучшения не поддерживаются. Текст сохранён; запустите новую попытку явно.

### 1207 · voice.b3671725c3b1861b

<!-- LOPATA.Events: ["localization:VideoPrep.AiModelMissing"] -->

- Выбранная модель отсутствует или не соответствует поддерживаемому кандидату. Текст сохранён; выберите модель явно.

### 1208 · voice.5ae69587e3ca209c

<!-- LOPATA.Events: ["localization:VideoPrep.AiModelUnverified"] -->

- Проверка файлов выбранной модели не пройдена. Подготовьте или восстановите её через управление моделями.

### 1209 · voice.531ca0df4e334672

<!-- LOPATA.Events: ["localization:VideoPrep.AiPaused"] -->

- Улучшение приостановлено. Ввод сохранён; можно продолжить через эту панель или трей.

### 1210 · voice.129e18bf07014800

<!-- LOPATA.Events: ["localization:VideoPrep.AiPausing"] -->

- Сохраняем состояние и освобождаем модель.

### 1211 · voice.7ba2e8ac865d5932

<!-- LOPATA.Events: ["localization:VideoPrep.AiReadyInHistory"] -->

- Вариант ИИ сохранён в истории. Ваши новые правки оставлены в поле.

### 1212 · voice.bec48e8a420d7592

<!-- LOPATA.Events: ["localization:VideoPrep.AiTimeout"] -->

- Модель не завершила улучшение за отведённое время. Текст сохранён.

### 1213 · voice.b19ba99d70d5438a

<!-- LOPATA.Events: ["localization:VideoPrep.AiUnavailable"] -->

- Помощник ещё не готов. Текст сохранён.

### 1214 · voice.c9dd2cc439fbe291

<!-- LOPATA.Events: ["localization:VideoPrep.AiWrongProject"] -->

- Результат относится к другому проекту или этот проект не удалось открыть. Текущий текст сохранён.

### 1215 · voice.34c0334a87b2005a

<!-- LOPATA.Events: ["localization:VideoPrep.BoardAudioPlaybackFailed"] -->

- Звук не удалось воспроизвести. Можно продолжить просмотр без звука.

### 1216 · voice.f29d1e236640e80d

<!-- LOPATA.Events: ["localization:VideoPrep.BoardAudioPreparing"] -->

- Подготавливаем звук.

### 1217 · voice.1ec9b746139db3cf

<!-- LOPATA.Events: ["localization:VideoPrep.BoardMissingImage"] -->

- Изображение недоступно.

### 1218 · voice.6c9faf9ad271da24

<!-- LOPATA.Events: ["localization:VideoPrep.BoardMissingPlan"] -->

- План удалён из сцен.

### 1219 · voice.0c979e2ca12f6997

<!-- LOPATA.Events: ["localization:VideoPrep.BoardNotReady"] -->

- Сверьте раскадровку с планами и исправьте отсутствующие связи или длительности.

### 1220 · voice.475770095e6c2cc7

<!-- LOPATA.Events: ["localization:VideoPrep.CheckMissingFile"] -->

- Вложенный файл недоступен.

### 1221 · voice.8b18dea9b8b9495e

<!-- LOPATA.Events: ["localization:VideoPrep.Checksum"] -->

- Проверка материала внутри архива не прошла. Проект не открыт.

### 1222 · voice.a4d4da9f3224ee9c

<!-- LOPATA.Events: ["localization:VideoPrep.DevelopmentCanceled"] -->

- Подготовка плана отменена. Текст и история сохранены.

### 1223 · voice.7cc01f9755ba0ca5

<!-- LOPATA.Events: ["localization:VideoPrep.DevelopmentError"] -->

- Не удалось подготовить план развития. Текст и история сохранены; проверьте выбранную модель.

### 1224 · voice.c1bca5a4814585b1

<!-- LOPATA.Events: ["localization:VideoPrep.DevelopmentMissing"] -->

- В проекте ещё нет материала развития. Вернитесь к этому шагу.

### 1225 · voice.cb67b981fa131462

<!-- LOPATA.Events: ["localization:VideoPrep.DevelopmentReady"] -->

- План развития готов. Можно отредактировать его или перейти дальше.

### 1226 · voice.4d5efba51da3e31d

<!-- LOPATA.Events: ["localization:VideoPrep.DevelopmentTimeout"] -->

- Модель не завершила подготовку плана за отведённое время. Текст сохранён.

### 1227 · voice.120b3247dbc86b06

<!-- LOPATA.Events: ["localization:VideoPrep.DiskSpace"] -->

- Недостаточно свободного места для открытия проекта.

### 1228 · voice.940f0b8921bcabdc

<!-- LOPATA.Events: ["localization:VideoPrep.ExportChanged"] -->

- Сохранённый проект или готовый экспорт изменились. Продолжение остановлено; начните новый экспорт.

### 1229 · voice.8827600ca7937632

<!-- LOPATA.Events: ["localization:VideoPrep.ExportInvalidFolder"] -->

- Не удалось использовать выбранную папку экспорта.

### 1230 · voice.2c2c583a21f65385

<!-- LOPATA.Events: ["localization:VideoPrep.FinishCanceled"] -->

- Операция отменена. Проект сохранён.

### 1231 · voice.da8e9e5d2526b111

<!-- LOPATA.Events: ["localization:VideoPrep.GeneratorSaved"] -->

- Выбор видеомодели и настройки сохранены.

### 1232 · voice.5cea2a839d9e10bf

<!-- LOPATA.Events: ["localization:VideoPrep.GeneratorUnavailable"] -->

- Эта модель исключена из выбора: источник требует внешней регистрации и ручного подтверждения доступа. Подготовка и история проекта сохранены.

### 1233 · voice.90951280ea39e48f

<!-- LOPATA.Events: ["localization:VideoPrep.H3AccelerationUnavailable"] -->

- Для первого теста выберите PyTorch и отключите Spectrum, Turbo и LoRA. Эти ускорения ещё не подключены к проверяемому пути генерации.

### 1234 · voice.18d5d0b42e8003ec

<!-- LOPATA.Events: ["localization:VideoPrep.H3Cancelled"] -->

- Генерация остановлена. Уже готовые фрагменты сохранены.

### 1235 · voice.f81ef97c364f4a8b

<!-- LOPATA.Events: ["localization:VideoPrep.H3Failed"] -->

- Не удалось завершить генерацию H3. Готовые фрагменты сохранены; подробности записаны в журнал операции.

### 1236 · voice.5c95478e1c9bdfa9

<!-- LOPATA.Events: ["localization:VideoPrep.H3File.downloading", "localization:VideoPrep.VaceFile.downloading"] -->

- Скачивается.

### 1237 · voice.93688f5093679ebf

<!-- LOPATA.Events: ["localization:VideoPrep.H3File.waiting"] -->

- Ожидает скачивания.

### 1238 · voice.00c7acd46d4f6d1d

<!-- LOPATA.Events: ["localization:VideoPrep.H3FinalizationIncomplete"] -->

- Для завершения обработки не хватает готовых фрагментов. Продолжите исходную генерацию.

### 1239 · voice.55064ab475db70c5

<!-- LOPATA.Events: ["localization:VideoPrep.H3GpuBusy"] -->

- Полный GPU возможен, но свободной видеопамяти недостаточно. Закройте игры и другие тяжёлые приложения, затем повторите запуск либо выберите «Автоматически».

### 1240 · voice.efdc3509a9d4d023

<!-- LOPATA.Events: ["localization:VideoPrep.H3InvalidResult"] -->

- Не удалось подтвердить целостность результата H3. Повреждённый результат не принят.

### 1241 · voice.4065fad230ee938e

<!-- LOPATA.Events: ["localization:VideoPrep.H3KitchenUnavailable"] -->

- Comfy Kitchen — недоступен для наших INT8-весов.

### 1242 · voice.a7f5aaad0d0e9245

<!-- LOPATA.Events: ["localization:VideoPrep.H3LiteFilesReady"] -->

- Файлы облегчённого H3 готовы. Перейдите к настройкам генерации.

### 1243 · voice.c68b6bf8f7d7fdd9

<!-- LOPATA.Events: ["localization:VideoPrep.H3LiteUpscaleUnavailable"] -->

- Для этого экспериментального набора нейросетевое увеличение ещё не подключено. Можно выбрать программное увеличение готового видео.

### 1244 · voice.fe9aa9eff6f55897

<!-- LOPATA.Events: ["localization:VideoPrep.H3NoPlans"] -->

- В проекте пока нет планов. Вернитесь к подготовке сцен и раскадровке.

### 1245 · voice.0441988b82ab2dbb

<!-- LOPATA.Events: ["localization:VideoPrep.H3OperationUnavailable"] -->

- Для первого теста доступны автоматический режим, новый фрагмент и продолжение. Prepend, Bridge и Loop пока не подключены.

### 1246 · voice.b9f9d2aae97a48ed

<!-- LOPATA.Events: ["localization:VideoPrep.H3PageCancelled"] -->

- Операция остановлена. Скачанные файлы и части докачки сохранены.

### 1247 · voice.d4e9d601d1194b92

<!-- LOPATA.Events: ["localization:VideoPrep.H3PageDownloading", "localization:VideoPrep.VacePageDownloading"] -->

- Скачиваем недостающие файлы.

### 1248 · voice.c872d73369c3bafb

<!-- LOPATA.Events: ["localization:VideoPrep.H3PageFailed"] -->

- Не удалось завершить подготовку. Подробности — в подсказке к сообщению.

### 1249 · voice.2a7879ead1beb3c1

<!-- LOPATA.Events: ["localization:VideoPrep.H3PageMissing"] -->

- Некоторых файлов ещё нет или они требуют повторного скачивания.

### 1250 · voice.a4f8098bf49ea325

<!-- LOPATA.Events: ["localization:VideoPrep.H3PlaybackFailed"] -->

- Не удалось воспроизвести видео. Можно экспортировать его и открыть во внешнем проигрывателе.

### 1251 · voice.4508c2bbad3ced8a

<!-- LOPATA.Events: ["localization:VideoPrep.H3PreparingRuntime"] -->

- Проверяем и подготавливаем среду H3… Первый запуск может занять несколько минут.

### 1252 · voice.c2b4a4e5021260af

<!-- LOPATA.Events: ["localization:VideoPrep.H3PreviewUnavailable"] -->

- Не удалось открыть изображение.

### 1253 · voice.bc151ecb5a50e16c

<!-- LOPATA.Events: ["localization:VideoPrep.H3ProgressPaused"] -->

- Вычисления приостановлены. Готовые результаты сохранены.

### 1254 · voice.a8e5b8805d340ea1

<!-- LOPATA.Events: ["localization:VideoPrep.H3PromptRequired"] -->

- У одного из планов нет рабочего запроса. Вернитесь к подготовке и заполните его.

### 1255 · voice.6f0672974d61d279

<!-- LOPATA.Events: ["localization:VideoPrep.H3ScheduleTrayUnavailable"] -->

- Запуск запланирован; свернуть приложение в трей не удалось.

### 1256 · voice.6ddc66331517f7b4

<!-- LOPATA.Events: ["localization:VideoPrep.H3SettingsSaved"] -->

- Проект и настройки сохранены.

### 1257 · voice.d73e468ae293f2ff

<!-- LOPATA.Events: ["localization:VideoPrep.H3VideoExported"] -->

- Видеофайл экспортирован.

### 1258 · voice.6c591dc02e31e636

<!-- LOPATA.Events: ["localization:VideoPrep.H3WaitingStatus"] -->

- Генерация прервана и ожидает продолжения. Готовые фрагменты сохранены.

### 1259 · voice.02899e86d90d252d

<!-- LOPATA.Events: ["localization:VideoPrep.H3WorkspaceNotice"] -->

- Готовые результаты появятся внизу. Первый запуск проверяет и подготавливает среду H3.

### 1260 · voice.b432d34f41554012

<!-- LOPATA.Events: ["localization:VideoPrep.InvalidArchive"] -->

- Это повреждённый проект или неподдерживаемый формат .lopataAIVid.

### 1261 · voice.2cae5572bb7572de

<!-- LOPATA.Events: ["localization:VideoPrep.IoError"] -->

- Не удалось открыть или сохранить проект. Проверьте доступ к файлу и свободное место.

### 1262 · voice.c9a502b772b01626

<!-- LOPATA.Events: ["localization:VideoPrep.LtxFilesReady"] -->

- Файлы LTX-2.5 скачаны и проверены. Исполнитель генерации ещё не подключён.

### 1263 · voice.94ca972757d2f586

<!-- LOPATA.Events: ["localization:VideoPrep.LtxMetadataInvalid"] -->

- Источник не подтвердил целостность выбранной ревизии LTX-2.5. Скачивание не начато; повторите проверку доступа.

### 1264 · voice.1698d2a09bca75dd

<!-- LOPATA.Events: ["localization:VideoPrep.LtxMetadataUnavailable"] -->

- Не удалось получить сведения о файлах Hugging Face. Проверьте соединение и повторите проверку.

### 1265 · voice.e3947bac781bdd38

<!-- LOPATA.Events: ["localization:VideoPrep.LtxPageCancelled"] -->

- Подготовка остановлена. Уже полученные данные сохранены.

### 1266 · voice.515b91e56aa22620

<!-- LOPATA.Events: ["localization:VideoPrep.LtxPageDownloading"] -->

- Скачиваем недостающие файлы LTX-2.5.

### 1267 · voice.8393fdfd32ff7931

<!-- LOPATA.Events: ["localization:VideoPrep.LtxPageFailed"] -->

- Не удалось завершить подготовку LTX-2.5. Повторите проверку или скачивание.

### 1268 · voice.c668fff0d883cc1c

<!-- LOPATA.Events: ["localization:VideoPrep.LtxPageMissing"] -->

- Для подготовки не хватает файлов или подтверждённых сведений об их целостности.

### 1269 · voice.7a903b35e11353d8

<!-- LOPATA.Events: ["localization:VideoPrep.MissingAttachment"] -->

- Не найден один из материалов проекта. Сохранение не завершено.

### 1270 · voice.4478a216d9b0b703

<!-- LOPATA.Events: ["localization:VideoPrep.NotSaved"] -->

- Место сохранения ещё не выбрано.

### 1271 · voice.c8a03b9d87c18faf

<!-- LOPATA.Events: ["localization:VideoPrep.OpenFailed"] -->

- Не удалось открыть проект подготовки видео .lopataAIVid. Проверьте архив, доступ к файлу и свободное место.

### 1272 · voice.aa4bbb2eb8456c47

<!-- LOPATA.Events: ["localization:VideoPrep.PackageNotReady"] -->

- Комплект ещё не готов. Исправьте обязательные пункты проверки.

### 1273 · voice.fb9c8893e6e0591d

<!-- LOPATA.Events: ["localization:VideoPrep.PackageReady"] -->

- Комплект готов к передаче.

### 1274 · voice.0d83b5b04e8f3f00

<!-- LOPATA.Events: ["localization:VideoPrep.ProjectDescriptionTooLarge"] -->

- Описание проекта и история превышают допустимый размер. Прежний сохранённый файл не изменён.

### 1275 · voice.bffae454f02799f2

<!-- LOPATA.Events: ["localization:VideoPrep.RefDrawingFailed"] -->

- Не удалось создать референс. Подробности доступны в журнале фоновой операции.

### 1276 · voice.05e2d3cdc80978b1

<!-- LOPATA.Events: ["localization:VideoPrep.RefDrawingFinished"] -->

- Подготовка референсов завершена. Выбранные изображения сохранены внутри проекта.

### 1277 · voice.0a4c944ab97dd99e

<!-- LOPATA.Events: ["localization:VideoPrep.RefDrawingPreparing"] -->

- Подготавливаем компоненты.

### 1278 · voice.0ac4ee1fd0ed21aa

<!-- LOPATA.Events: ["localization:VideoPrep.RefDrawingReady"] -->

- Набор запросов сохранён. Можно переходить к изображениям.

### 1279 · voice.b7045de3e1b49b8e

<!-- LOPATA.Events: ["localization:VideoPrep.RefInvalidBinding"] -->

- Не удалось создать связь изображения. Проверьте выбор карточки и назначение.

### 1280 · voice.3987aefcb92e08d7

<!-- LOPATA.Events: ["localization:VideoPrep.RefInvalidImage"] -->

- Файл не удалось прочитать как поддерживаемое изображение. Выберите PNG, JPEG, WebP, BMP или GIF.

### 1281 · voice.3f2a9dfbf5cab619

<!-- LOPATA.Events: ["localization:VideoPrep.RefMissingFile"] -->

- Файл изображения не найден.

### 1282 · voice.4f48c34cc107674f

<!-- LOPATA.Events: ["localization:VideoPrep.RefPlanChanged"] -->

- Набор изменился. Полученный результат сохранён для восстановления задания.

### 1283 · voice.8beefb1b8fb4956f

<!-- LOPATA.Events: ["localization:VideoPrep.RefPreviewFailed"] -->

- Не удалось показать изображение. Файл и связи не изменены.

### 1284 · voice.20a4b74bd7789abe

<!-- LOPATA.Events: ["localization:VideoPrep.RefSaveFailed"] -->

- Изображения добавлены в текущую работу, но сохранить проект не удалось. Проверьте сообщение об ошибке и повторите сохранение.

### 1285 · voice.ae137ba3629f55f3

<!-- LOPATA.Events: ["localization:VideoPrep.ShotsDuplicateReply"] -->

- Модель повторила один и тот же план. Готовые части сохранены.

### 1286 · voice.ff23e32631d27b73

<!-- LOPATA.Events: ["localization:VideoPrep.ShotsEmpty"] -->

- Сборка ещё не завершена. Можно продолжить её или добавить сцены вручную.

### 1287 · voice.77f229d166457f0f

<!-- LOPATA.Events: ["localization:VideoPrep.ShotsPaused"] -->

- Подготовка сцен приостановлена. Готовые части сохранены.

### 1288 · voice.27dd0aeb50550560

<!-- LOPATA.Events: ["localization:VideoPrep.ShotsQualityFailed"] -->

- Сцена не прошла проверку согласованности. Принятый план сохранён; можно исправить сцену вручную или запросить новый вариант.

### 1289 · voice.5a0abadf01de2dd0

<!-- LOPATA.Events: ["localization:VideoPrep.ShotsReady"] -->

- Черновик сцен и планов готов. Проверьте и при необходимости исправьте его.

### 1290 · voice.e1062f10c802ac0d

<!-- LOPATA.Events: ["localization:VideoPrep.SourceChanged"] -->

- Материал изменился во время сохранения. Сохранение не завершено.

### 1291 · voice.da3f98441a7f4c48

<!-- LOPATA.Events: ["localization:VideoPrep.TranslationCanceled"] -->

- Перевод отменён. Исходник и английские версии сохранены.

### 1292 · voice.c994a6cb145995b1

<!-- LOPATA.Events: ["localization:VideoPrep.TranslationError"] -->

- Перевод не выполнен. Текст и история сохранены.

### 1293 · voice.c892d2348c8071fe

<!-- LOPATA.Events: ["localization:VideoPrep.TranslationLiteralLost"] -->

- Переводчик изменил защищённую реплику или имя. Ответ не применён; исходник и прежние версии сохранены.

### 1294 · voice.e903bd799d13de88

<!-- LOPATA.Events: ["localization:VideoPrep.TranslationPaused"] -->

- Перевод приостановлен. Ввод сохранён; можно продолжить здесь или через трей.

### 1295 · voice.7a17e5b2f407dbda

<!-- LOPATA.Events: ["localization:VideoPrep.TranslationReady"] -->

- Перевод готов. Можно исправить текст вручную или перейти дальше.

### 1296 · voice.503b15927eb1e701

<!-- LOPATA.Events: ["localization:VideoPrep.TranslationTimeout"] -->

- Модель не завершила перевод за отведённое время. Текст сохранён.

### 1297 · voice.8308031d69c3bf12

<!-- LOPATA.Events: ["localization:VideoPrep.VaceCancelled"] -->

- Генерация остановлена. Готовые участки сохранены.

### 1298 · voice.c09d3ee826490bea

<!-- LOPATA.Events: ["localization:VideoPrep.VaceChangedProject"] -->

- Проект изменён. Прежние результаты сохранены; начните новую генерацию с текущими данными.

### 1299 · voice.bf4181c4b91de9a1

<!-- LOPATA.Events: ["localization:VideoPrep.VaceFailed"] -->

- Не удалось завершить генерацию VACE. Подробности сохранены в журнале операции.

### 1300 · voice.c5fa0b78d809b9c3

<!-- LOPATA.Events: ["localization:VideoPrep.VaceFile.failed"] -->

- Сбой подготовки.

### 1301 · voice.44b4eb985309c39d

<!-- LOPATA.Events: ["localization:VideoPrep.VaceFile.waiting"] -->

- Ожидает скачивания.

### 1302 · voice.a7c2fae85c96612f

<!-- LOPATA.Events: ["localization:VideoPrep.VaceGenerating"] -->

- Создаём видео VACE.

### 1303 · voice.91d7c928becfc372

<!-- LOPATA.Events: ["localization:VideoPrep.VaceInvalidResult"] -->

- Проверка результата VACE не пройдена. Файл не принят как готовый.

### 1304 · voice.e872f0d8c1a988bb

<!-- LOPATA.Events: ["localization:VideoPrep.VaceNoPlans"] -->

- В проекте нет планов для генерации. Вернитесь к подготовке.

### 1305 · voice.ce2dd9ca964a3f19

<!-- LOPATA.Events: ["localization:VideoPrep.VacePageCancelled"] -->

- Подготовка остановлена. Полученные файлы сохранены; скачивание можно продолжить.

### 1306 · voice.e61d1d86fc182720

<!-- LOPATA.Events: ["localization:VideoPrep.VacePageFailed"] -->

- Не удалось завершить подготовку. Можно повторить проверку или продолжить скачивание.

### 1307 · voice.7f0dec9b2f09a3f5

<!-- LOPATA.Events: ["localization:VideoPrep.VacePageMissing"] -->

- Не все файлы готовы. Скачайте недостающее или повторите проверку.

### 1308 · voice.2f967f2460113c70

<!-- LOPATA.Events: ["localization:VideoPrep.VacePaused"] -->

- Вычисления приостановлены. Готовые участки сохранены.

### 1309 · voice.3a49d8465c320bc9

<!-- LOPATA.Events: ["localization:VideoPrep.VaceRetriesExhausted"] -->

- Попытки восстановления исчерпаны. Готовые участки сохранены; продолжение доступно вручную.

### 1310 · voice.7d68145fbbd43dd9

<!-- LOPATA.Events: ["localization:VideoPrep.VaceRuntimeFailed"] -->

- Исполнитель VACE завершился с ошибкой. Подробности сохранены в журнале операции.

### 1311 · voice.e555b2aee6d90ee8

<!-- LOPATA.Events: ["localization:VideoPrep.VaceRuntimeRequired"] -->

- Не все компоненты VACE готовы. Вернитесь к подготовке и проверьте файлы.

### 1312 · voice.4c67ec23fc1bde99

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWaiting"] -->

- Генерация ожидает продолжения. Готовые участки сохранены.

### 1313 · voice.03ccfaf1b177835b

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.Cancelled"] -->

- Операция отменена. Исходные файлы сохранены.

### 1314 · voice.467a1777a305b4ff

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.ExportFailed"] -->

- Не удалось экспортировать видео. Исходный файл сохранён; подробности записаны в журнал.

### 1315 · voice.0adf5785e0a10991

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.ExportWorking"] -->

- Сохраняем файл.

### 1316 · voice.c6952a55302911a2

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.Generating"] -->

- Генерация VACE.

### 1317 · voice.613a9cb80b77fb3e

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.Notice"] -->

- Готовые цельные ролики появятся внизу. Первый запуск проверяет и подготавливает среду VACE.

### 1318 · voice.11f22797b2dabd60

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.PlaybackFailed"] -->

- Не удалось воспроизвести выбранное видео. Файл проекта сохранён.

### 1319 · voice.b03d63cbd0398a15

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.PreparingRuntime"] -->

- Проверяем и подготавливаем среду VACE.

### 1320 · voice.e03b3ca498507317

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.ProgressPaused"] -->

- Вычисления приостановлены. Готовые результаты сохранены.

### 1321 · voice.bc4ba9bb8801a4ab

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.SettingsPending"] -->

- Настройки сохранены для следующей генерации.

### 1322 · voice.2f61f1e96977d82c

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.Stage.loading"] -->

- Загружаем VACE.

### 1323 · voice.272e0b8562fa1c94

<!-- LOPATA.Events: ["localization:VideoPrep.VaceWorkspace.VideoExported"] -->

- Готовое видео экспортировано точной копией.

### 1324 · voice.28d4c117af969a32

<!-- LOPATA.Events: ["localization:VideoPrep.WarmCanceled"] -->

- Прогрев отменён. Тексты проекта сохранены.

### 1325 · voice.bb295ec66e7ab753

<!-- LOPATA.Events: ["localization:VideoPrep.WarmFailed"] -->

- Не удалось прогреть помощника. Проверьте уведомление фоновой операции.

### 1326 · voice.687f923fab6fd387

<!-- LOPATA.Events: ["localization:VideoPrep.WarmPaused"] -->

- Прогрев приостановлен.

### 1327 · voice.57f2400e19ebccbf

<!-- LOPATA.Events: ["localization:VideoPrep.WorldInvalidReply"] -->

- Модель вернула неполный или неверный комплект описаний. Текущий визуальный мир сохранён.

### 1328 · voice.4b34a3f7a2448333

<!-- LOPATA.Events: ["localization:VideoPrep.WorldMissing"] -->

- Карточка визуального мира недоступна. Откройте актуальный проект.

### 1329 · voice.31a3b810628d5d5b

<!-- LOPATA.Events: ["localization:VideoPrep.WorldReady"] -->

- Готовые варианты доступны для выбора.

### 1330 · voice.63dd2e30e9dad0b7

<!-- LOPATA.Events: ["localization:VideoPrep.WorldStale"] -->

- Замысел или план развития изменились. Описания сохранены: проверьте их перед продолжением.

### 1331 · voice.63f3ab27af7b863b

<!-- LOPATA.Events: ["localization:VideoPrep.WorldStatus.canceled"] -->

- Запрос остановлен. Можно создать другой вариант.

### 1332 · voice.5b7a09fbabc5067c

<!-- LOPATA.Events: ["localization:VideoPrep.WorldStatus.failed"] -->

- Не удалось создать этот вариант. Остальные доступны; этот можно повторить отдельно.

### 1333 · voice.86721765297d8030

<!-- LOPATA.Events: ["localization:VideoPrep.WorldStatus.pending"] -->

- Ожидает очереди.

### 1334 · voice.4b3432ecfe186832

<!-- LOPATA.Events: ["localization:VideoUpscale.Cancelled"] -->

- Остановлено. Готовые части сохранены; можно продолжить.

### 1335 · voice.549f22b0831cd58c

<!-- LOPATA.Events: ["localization:VideoUpscale.CheckpointDamaged"] -->

- Сохранённая часть изменилась или повреждена. Она не будет незаметно использована повторно.

### 1336 · voice.3ac0ec45076271e1

<!-- LOPATA.Events: ["localization:VideoUpscale.Complete"] -->

- Готово. Результат сохранён.

### 1337 · voice.3ecf1cd883e82214

<!-- LOPATA.Events: ["localization:VideoUpscale.DiskSpace"] -->

- Недостаточно места для следующей части и промежуточных файлов. Готовые части сохранены.

### 1338 · voice.17959ca4866aa103

<!-- LOPATA.Events: ["localization:VideoUpscale.Importing"] -->

- Читаем видео и готовим кадры.

### 1339 · voice.fd1b6c114b2441c3

<!-- LOPATA.Events: ["localization:VideoUpscale.InvalidProject"] -->

- Не удалось прочитать проект или цепочку.

### 1340 · voice.f4240652a4e99406

<!-- LOPATA.Events: ["localization:VideoUpscale.InvalidSource"] -->

- Не удалось определить размер, длительность или частоту видео.

### 1341 · voice.30bc02b210d297f9

<!-- LOPATA.Events: ["localization:VideoUpscale.NativeFailed"] -->

- Исполнитель завершился с ошибкой. Подробности сохранены в журнале; автоматического повторения не будет.

### 1342 · voice.1c47d9eb8377f19e

<!-- LOPATA.Events: ["localization:VideoUpscale.NoReadyParts"] -->

- Пока нет готовых частей для сохранения.

### 1343 · voice.e57a0c02771e5ad5

<!-- LOPATA.Events: ["localization:VideoUpscale.OutputMismatch"] -->

- Результат не прошёл проверку размера или количества кадров. Готовые части сохранены.

### 1344 · voice.9261186af5cfabe9

<!-- LOPATA.Events: ["localization:VideoUpscale.RebuildingPart"] -->

- Сохранённая часть повреждена: восстанавливаем только этот шаг.

### 1345 · voice.44dc48d663fb166c

<!-- LOPATA.Events: ["localization:VideoUpscale.SourceMissing"] -->

- Исходное видео не найдено.

### 1346 · voice.996a4cb038c3d880

<!-- LOPATA.Events: ["localization:VideoUpscale.WindowBusy"] -->

- В этом окне выполняется другая операция. Дождитесь её завершения.

### 1347 · voice.3f6506735717d094

<!-- LOPATA.Events: ["localization:WindowsIntegration.Failed"] -->

- Не удалось изменить команды ЛОПАТЫ в меню Проводника. Настройка сохранена в прежнем состоянии.

### 1348 · voice.604323c1218b734d

<!-- LOPATA.Events: ["localization:WorkStart.DeleteConfirmation"] -->

- Подтвердите удаление выбранных данных. Подробности показаны в окне программы.

### 1349 · voice.ed074e3fc4da230b

<!-- LOPATA.Events: ["localization:WorkStart.StatusCompleted"] -->

- Завершена.

### 1350 · voice.ded3ea8801aa6ec1

<!-- LOPATA.Events: ["localization:WorkStart.StatusInterrupted"] -->

- Предыдущий запуск прерван.

### 1351 · voice.4ea63c5afbe8b0d0

<!-- LOPATA.Events: ["localization:WorkStart.StatusUnavailable"] -->

- Архив повреждён.

### 1352 · voice.66a468dc8b4d2e88

<!-- LOPATA.Events: ["localization:WorkStart.Unavailable"] -->

- Недоступно.

### 1353 · voice.f537d6be17a968e8

<!-- LOPATA.Events: ["localization:licenses.save_error"] -->

- Не удалось сохранить подтверждение. Проверьте доступ к папке настроек и повторите попытку.

### 1354 · voice.1e4e9d3322e6d1e9

<!-- LOPATA.Events: ["model-log:loaded", "model-log:ace_preparation_progress", "model-log:diffrhythm_preparation_progress", "model-log:heartmula_preparation_progress"] -->

- Подготавливаем следующий компонент модели.

### 1355 · voice.646cbbaef974b18b

<!-- LOPATA.Events: ["source:3f070b384a6f0338", "model-log:background_cancel_callback_failed"] -->

- Операция отменена при выполнении фоновой задачи.

### 1356 · voice.f916312dd13e9fab

<!-- LOPATA.Events: ["model-log:background_observer_failed"] -->

- Не удалось получить необходимые данные при выполнении фоновой задачи. Проверьте соединение и настройки.

### 1357 · voice.f5b332c32595178a

<!-- LOPATA.Events: ["model-log:chat_attachment_read", "model-log:let_it_out_memory_action", "model-log:let_it_out_context", "model-log:chat_recall_selected", "model-log:let_it_out_summary_rejected", "model-log:let_it_out_placement", "model-log:let_it_out_vram_low", "model-log:entry_skipped", "model-log:entry_corrupted", "model-log:entry_created", "model-log:entry_updated", "model-log:music_removal_state", "model-log:python_bytecode_quarantined", "model-log:stopped", "model-log:forced_stop", "model-log:probe_cleanup_pending", "model-log:narrator_native_exit", "model-log:concept_hardware_estimate_unavailable", "model-log:book_ocr_diagnostics", "worker:background"] -->

- Изменился этап работы исполнителя. Подробности показаны в окне программы.

### 1358 · voice.f19c5a3a449f1f07

<!-- LOPATA.Events: ["model-log:startup_check_started", "model-log:component_audit_settings_unreadable"] -->

- Проверяем компоненты и подготовленные данные.

### 1359 · voice.b1a483700b3ac3b5

<!-- LOPATA.Events: ["model-log:model_retired"] -->

- Освобождаем ресурсы модели.

### 1360 · voice.bc7ddf6511bed376

<!-- LOPATA.Events: ["model-log:ready", "worker:Ready", "worker:ready"] -->

- Исполнитель готов к работе.

### 1361 · voice.a7fd76bff30c7306

<!-- LOPATA.Events: ["model-log:startup_check_finished"] -->

- Очередной этап работы модели завершён.

### 1362 · voice.e59d7afc7352ae67

<!-- LOPATA.Events: ["source:5133850a58d78188", "source:cda52cfc30e8082e", "source:875ed02491e2b5ff", "source:cf967fb017bee518", "source:d4111116b315b689", "source:66f13d9586eb8b06", "source:9681f0c5f46a7235", "source:30ebb44b110b429e", "source:3db6dc98c45472c0", "source:f947eda66693de03", "source:bb6c5da2de78ac95", "source:c0635dc7b7e352f7", "source:577f9413af080cb4", "source:fca6360a838ebc83", "source:90cb81164839055c", "source:e81a522f30793764", "source:63cee6b0c0bc7ed3", "source:9999fa98acad9f4c", "source:f86852e1994775cf", "source:fe3cb38c5b848e33", "source:e197ad2ff0ac8d62", "source:2d3e90ce48fc0aad", "source:efb042b72429f059", "source:b0dc8ba46acbb394", "source:64fbe2d8228711d6", "source:45c4caedb50d858a", "source:788e1c10873e2be7", "source:72e79bae408fe79e", "source:d16a20a68d8e1664", "source:d23016a0d7dbe2a6", "source:0019eb577fe798a6", "source:ba807e5dc8d59b8d", "source:2a464941a706ae66", "source:e4951d7212e87d25", "source:6d7717d331b4a4c4", "source:42b7577a24dbb028", "source:1738dd519ccc0a6f", "source:b752079586cd15d6", "source:90396a8c68c91c8d", "source:6ea7700741d652dd", "source:bb891a16833d2526"] -->

- Не найдены необходимые данные или компоненты при работе с моделью.

### 1363 · voice.c182c1ecbc0167c7

<!-- LOPATA.Events: ["source:f9ac4ce1c9d2b01b", "source:6f5661126777559c", "source:3fbdc7a4a4382899", "source:baba80f2688d8595", "source:15d5916ea6a755d7", "source:28a62fada2b0ee09", "source:186b471de052ca09", "source:e1d8f929cd94bb27", "source:fc2b7e3193b2fb0d", "source:25eed581bf63c466", "source:311927dcc86efa02", "source:995a8d323eee6e71", "source:8b66190bd21e6be5", "source:f750860718dac588", "source:b48eb30367969293", "source:fbeb3ded128e528e", "source:512020a20588b634", "source:ec0086c3304cab5d", "source:7a74e9fceaef71c2", "source:1df1c642386dedb2", "source:09924dd8490ffa55", "source:e279aab0a3255205", "source:908401a14aec3a38", "source:fd77606c4ec7389b", "source:01254a9ff8c8caf8", "source:d4fcef3e5720d4ec", "source:ccd7a0744a9effa5", "source:39dec5832f3c4e77", "source:1abf2bdb834262af", "source:c7b2db34bcd02f62", "source:4ed5dc7b6bf1139f", "source:e8595897fb921ed5", "source:3a7c78681af8fbf6"] -->

- Обнаружены некорректные или неподходящие данные при работе с моделью.

### 1364 · voice.40172390d4df027a

<!-- LOPATA.Events: ["source:a5c757effc50d513", "source:8ba34ad43c872f88", "source:56cab967fd1c2476", "source:825f2283eff10a68", "source:8fdd0b158c17d5c8", "source:fe9a07e5b15ebff1", "source:f1ffb5c648bba37b", "source:8e8bfd2a907a8cba", "source:dc81adb36d44ab95", "source:843a60a2d9bd2615", "source:623864e5bb842c18", "source:a283be842e2d3976", "source:7443b122cf825c36", "source:5d616e9862b29211", "source:84334fc08d4fbbb6", "source:8acf1c51f95e4001", "source:45cbdc2f99eb71fd", "source:3ff4b834354a1b4b", "source:a03faaeb5c49e859", "source:b628f89bcaa2f146", "source:73aad06a5708c7eb", "source:603278eb79f2493f", "source:f2adcd9dae83d32e", "source:42f232b77eaebe2a", "source:59aa6aa98a0f5831", "source:40114cccace4c0cb", "source:5871993caf169a89", "source:88947fe9c55d28f9", "source:453a196a3ef202c7", "source:fd452eee8e6c5dfe", "source:93309c86172dbc42", "source:bbf6a168868d5069", "source:3c1e83c74da8db22", "source:8ac2f2dee919dbc2", "source:a75ec0165bbe5865", "source:87f0c3adafb35eef", "source:5ec1860c7665294f", "source:81acb4e5c11565a1", "source:dd08f0e604e6f017", "source:2e33af0c0f4f5c5d", "source:4f5ce5a02cf5268d", "source:d6fc46bb979a2370", "source:01b38fcca9e9d51d", "source:97331c247d606a24", "source:0cb154a9743f2275", "source:f3585319272409e0", "source:e94b6f9ddf441524", "source:d93b572273a2f097", "source:3efb9289881ef3ec"] -->

- Обнаружены некорректные или неподходящие данные при обработке видео.

### 1365 · voice.df7e1d8fff93811c

<!-- LOPATA.Events: ["source:53330e56f786a7b4", "source:e04ab314948dcfe5", "source:bec839c4b8ac1c86", "source:07923d0e3f0b939d", "source:1fd5b45b2e8a2608", "source:e83248a99f599886", "source:1cdd9850b481217d", "source:47848f8d4f544c64", "source:7cffd8e62aa461fa", "source:033f592954ef4108", "source:adb6750c5720c444", "source:e22f532076358120", "source:50d28bb427001bad", "source:dd397634eecc472b", "source:3b6c1e51cb2e159e", "source:c1418822909e17a6", "source:0b1b1fa1bddf4f94", "source:3cda528dd5f23ee8", "source:4ee638d3a65afee8", "source:86b921ad85bef695", "source:f941e27a90de7201", "source:a36baf864fd51c24", "source:d44989c6fbf869f0", "source:2783416885bdf012", "source:4544a3c650e3e079", "source:01db83bfc2a1e77f", "source:f44c67379d68de86", "source:de35b5614574e89a", "source:5cd9ef9fdb1e928f", "source:0d08b61c1d877647", "source:09821cfaef1ee24d", "source:48826af79766d36a", "source:3595f5e045b14892", "source:cfd60dbd39445616", "source:8a2b0254b087c115", "source:87d07d73ea7089d6", "source:12b0fb5c32836fd9", "source:ecc311c488d02253", "source:9b9f6ec550538fb1", "source:b1125eff46bcc18f", "source:caf1eee5fb870b08", "source:2b5c09efa3179d3f", "source:7cd89fe4292e74e1", "source:6d550911d06bb3e6", "source:9d3cbed5a1c49f38", "source:faeefb302f26c097", "source:63f97332db69df32", "source:2b78c263dee7002e", "source:f0a88ebddeffd62f", "source:67b337d271225c74", "source:4dba6d4d2339f2d4", "source:6b7ce78349fe6cb1", "source:f16b66135dd7e683", "source:362eb2c1c7db58d0", "source:800ea575097986f0", "source:c84dcf9bbedf1f0a", "source:7bfbb07d7c03336c", "source:4a22b0d6e5974932", "source:b4458ff6a4f8b421", "source:6ca401ba440ffb46", "source:3acb9dfa02081b9b", "source:8e7eb042e0842453", "source:d8af53f6f1f68db3", "source:e9092c4f45b161c7", "source:6d6e08f53327fcbf", "source:d31935a481839d33", "source:459b08dbb6e7ca79", "source:f53b91966287e309", "source:23cf92a18a411a12", "source:f8ebd291505b3117", "source:7662a070eea57e92", "source:1796c2558cfa6b94", "source:47074b498b279ff0", "source:779cc1936ceb99ba", "source:e42e312f68cbd275", "source:158f03e95eaac18d", "source:fe8f4d0ee5dfd42b", "source:604144aba090996b", "source:df38241a6cf8e1f1", "source:a1e0ca609496bcb2", "source:cd0d26793e4313bb", "source:3670af9bc17e266b", "source:dcdb7010d5237766", "source:9a5fff1061124306", "source:cee6d6a116fc081f", "source:0efc2b399f5d55d5", "source:04e41f1c468d9dc7"] -->

- Обнаружены некорректные или неподходящие данные при работе с музыкой и текстами.

### 1366 · voice.ca6de16e9b77d3b0

<!-- LOPATA.Events: ["source:bc18b863df5096f8", "source:629ea8a2ebc11a20", "source:d7db8f04ddf6f232", "source:84ef5db0905a4169", "source:254fed779143aab7", "source:ac97424ab35b8163", "source:62ad6d9f6ccc638d", "source:9dcd93ba92fa40de", "source:5c89bb2245e36f16", "source:04b710d3e383997f", "source:d94ea675a45852dd", "source:6878c4ebe94ce1df", "source:521eca956eff48a1", "source:412bd8a9c5ab605d", "source:0294a2a36f8e91d0", "source:afe20be89d44d00c", "source:85b6bc2c167130fe", "source:e7f5e30f56181266", "source:2eb67e1bc73e23c8", "source:fb97f787d761127a", "source:c7fbf0400ed90665", "source:c8d83cba3e3783b7", "source:4578fd04d6a01ea6", "source:8923052359cd0bce", "source:b9f9c99adb28a9b5", "source:f84314eae34b2aeb", "source:81b9b84b60a17e7f", "source:f877629e7acb8241", "source:168329151a07d309", "source:331d059bd1f3f629", "source:ae35e02193cb1730", "source:50ce01b5a671a178", "source:b86f4d83c0870d58", "source:ff1f47b7c5cef97a", "source:025a8a35518fdd9b", "source:840043790f5c2c2f", "source:a1e5266d1a7aea4a", "source:2ffe62f35813e23f", "source:73edea8af9a60f67", "source:4935ed114c5f4d62", "source:df438365566dd800", "source:01e686dfcf33abc4", "source:43c3e60af5a425a9", "source:b692871e3a73000c", "source:5195c539e5daf116", "source:b9f82a6f3a832de5", "source:a5a0d50f2c32f003", "source:b361c9faade290ed", "source:32b10abe92e39675", "source:67ad57eb9840396c", "source:d521ea505c540040", "source:3511ed3621be33cb", "source:e249d318ef8ac334", "source:b1c828f8eb9b6935", "source:3d51ba30a40f6bc9", "source:ab6fc6c3cef9ad13", "source:cacd177db5886aa9", "source:5afbf18018a8df93", "source:d9f69e1e33a9e240", "source:24c6dd72bd5f92b8", "source:5a7d88fc731a30ad", "source:81945f5115cdf0db", "source:840622210fc22a98", "source:ff04949edde2b2e3", "source:7d154845279a7e37", "source:f2a6cee1e2d582a7", "source:4da2056d79ebe16c", "source:f8cc75f1ce8fc393"] -->

- Обнаружены некорректные или неподходящие данные при работе над произведением.

### 1367 · voice.dd40522810953135

<!-- LOPATA.Events: ["source:85ed406af640cd5d", "source:eac1223431a098fb", "source:6a5e3fa30326ee7b", "source:a2d1d9bb912a2317", "source:20f95152eb4f9c03", "source:98fd94a902443806", "source:ff2a40178ede8952", "source:02b5702fcc73a541", "source:0800553063392e10", "source:0230d0bf154fd88b", "source:a2a408a2ed57ab7e", "source:49bda9fbe6aafde9", "source:155460df8823e57c", "source:a47a88af4b1e079f", "source:2a3906b92dfab19c", "source:6261b52a25bfad83", "source:088258a16cf9524c", "source:633835c702aed0ab", "source:094077f3ecefe267", "source:fd0d9c724389e657", "source:a083c235febf7477", "source:6a51d173f86917df", "source:737d4b091685cb2f"] -->

- Не найдены необходимые данные или компоненты при выполнении операции.

### 1368 · voice.659c7c3e56c5eb77

<!-- LOPATA.Events: ["source:bf3f64a83e34399f", "source:0d7963d9e142f977", "source:e7d068acf2de046b", "source:cd67d819dbefa226", "source:5167a02be80dd074", "source:1b18b2794ec6b437", "source:7bbc461c89b928ee", "source:ad0304044e09135b", "source:8bd7ea548504533c", "source:a3356d7b7afc3bd0", "source:9b36e295f228b6a2", "source:741c093b3a5cbd5f", "source:bd5af61092036695", "source:b87e2e8e8654b414", "source:cb41a4ac7d95c627", "source:7f1871a5a5ba0816", "source:a718ace691989ab0", "source:f4eedcf2ab1bb5d9", "source:216fdf0aedee5bde", "source:eab5fcb7a190c1f5", "source:02e79da5c054557b", "source:c2e308052d20fb1f", "source:fb108af0ebcad5c9", "source:108e0e90faa004eb", "source:a3737558974e05af", "source:9c6b9deda9305aaa", "source:024d9a006e32efcf", "source:446401a1d5817dec", "source:8894d3694f081628", "source:e7530dc4e67a94c2", "source:0dcfbaee520676ec", "source:14fb10236a5c415e", "source:6b8b1961823301ba", "source:b9c99711bdaf9c15", "source:12cffa19cb343a54", "source:28539e071af95449", "source:d555705bdc8c114c", "source:a23b5a1c3e37c316", "source:607c4f8bd18000f2", "source:9a92558cb5095c10", "source:db9b32761ee5033a", "source:3b3c203434f285e3", "source:43279d92d7375645", "source:f3c5a01c42520a04", "source:25d54f4c071ebdbe", "source:cfb77e9935179cdc", "source:53da8dd45e7c8cfc", "source:32dab08a0e694ed3", "source:757db7da13c8e2ec", "source:fa02e6ee2c0e4475", "source:e260daa1648ba855", "source:8ad42feeec2b503b", "source:d1dc6fa2a5d4bfc2", "source:34748ce9d6b1644e", "source:db49cc3ee5fa7261", "source:d758a906962a673c", "source:298c24fbbf259406", "source:e5f0fbdde38947a7", "source:79246694f38fa736", "source:941826ca5b0825a3", "source:e92cb39e281ca7d8", "source:dd2a669f9bbfc8f7", "source:525f9018ab994e38", "source:82144957052d2e1c"] -->

- Обнаружены некорректные или неподходящие данные при выполнении операции.

### 1369 · voice.4e12afcc3ff06848

<!-- LOPATA.Events: ["source:155e0d3b7b072943", "source:f14cac508d0f85e9", "source:d29010f06c0f43fb", "source:04bf1d1ca5b92683", "source:821629ec0d03b8ed", "source:2ea46fa68b49d2bc", "source:39c703b711c1d70c", "source:94c55069fb7c6e6e", "source:90f4c56842345c30", "source:772c99debc9ec70d", "source:dbb4d330cce90c91", "source:e236d75295532a37", "source:bdb22ee2caae5c4b", "source:035b2aa323e6f543", "source:f076a40c3a32600a", "source:684be26a5a86c852", "source:0bf74521af39c30c", "source:98f31f14459f96e0", "source:f14f6e09b9918fdd", "source:dd6dce3b9ed44394", "source:16967e7ec08b6ef7", "source:5941388a2a39a4b4", "source:f3ff418e0bae16be", "source:f8e865808f7bc919", "source:f97e7dfb47d5793d", "source:d8d3dd3f92a2914e", "source:ae800080dc3fd45e", "source:612e06751bf30536", "source:50c643490cd1df95", "source:8f65f8bfb15ad065", "source:d2f008658b40fba7", "source:16b1b26c96f09ada", "source:c0f647ff852c3bbe", "source:feeb8635b941000c", "source:1ac1a95dc19a8cd4", "source:543fb7e8a9890889", "source:d32cc80ba173fa0f", "source:4a6d2de8b021be47", "source:c915cfb5e0cb7a34", "source:2bca5e535f7a40f4", "source:2b6e841df1561006", "source:12394adb4e967bfa", "source:e0bb9645e9866f07", "source:9cbeb287a06c3f30", "source:f42d62e756b87669", "source:973df7ee8c01cbda", "source:1d1cfe665614286e", "source:ad82d8cc75543282", "source:493325e767c02f3d", "source:78c56adef85344a8"] -->

- Не найдены необходимые данные или компоненты при работе с музыкой и текстами.

### 1370 · voice.57e5a71fb59e4c67

<!-- LOPATA.Events: ["source:03e04bdcc8ae549f", "source:11462d58811c3c49", "source:f1a9d43e89fdf831", "source:cac91a2305cf9864", "source:3ce3b46e38d9e7f0", "source:9c01826cbc8412b4", "source:4c296bf80b441816", "source:72f501d3605852d4", "source:6068dca809fd2b06", "source:796408f7449cdb45", "source:78b2d51d0cd08d91", "source:befa06886f53e39d", "source:f99f26fe143974b1", "source:ca4e690ae4477306", "source:e6a391f00066403d", "source:0ac9e1f4cc909f26"] -->

- Не найдены необходимые данные или компоненты при работе над произведением.

### 1371 · voice.f5b60e9e7c0a572c

<!-- LOPATA.Events: ["source:b2128b7ce20ca106", "source:25f5e02aa791388c", "source:b10bd5cb301329b2", "source:5342a614144d20ad", "source:fbe8307ca138e3d4", "source:04f9077deffc2cdc", "source:63da009a4fbd55ce", "source:47ff2fed4830e859", "source:a6dad5344dd77d57", "source:5aab10e61425111d", "source:2a20251882ee2742", "source:3d43dc59b7be83d9", "source:f8984d1986e05a95", "source:03ff3c06ca347ea8", "source:e32131b1b2b88a6a", "source:06ffb3b9bab29036", "source:b0efd197a2c5b014", "source:94c2bdc29b70c1b9", "source:32a56176505958b9", "source:ff93c9e2415adb55", "source:e72fb1a8d1dd3f8b", "source:b1a9396fb0756085", "source:5486988c161cf34d", "source:d8dd2f9edf39e5ab", "source:ccf3af606b6108af", "source:aefdf3d91e28527f", "source:eed173a7ced9305f", "source:bb849fd16fff9858"] -->

- Обнаружены некорректные или неподходящие данные при обработке книги.

### 1372 · voice.ebb8096a49c39030

<!-- LOPATA.Events: ["source:45d96b6df253f9f8", "source:2f01f37afee6f993", "source:7ad281a7e41dfb42", "source:f2fd7d27372581ff", "source:898be905d7d751b9", "source:0547e96696602b68", "source:a4f5ffe37c7fe811", "source:1b7d6b5f5fbf589e", "source:92a1144576ee6be4"] -->

- Не удалось получить необходимые данные при обновлении ЛОПАТЫ. Проверьте соединение и настройки.

### 1373 · voice.96f4b52f34ce0c00

<!-- LOPATA.Events: ["source:f8c73a4bf4b0d263", "source:06954b0f4f1a4ecc", "source:7ad6ba256d1d8a82", "source:f32bfe2b4af6ac59"] -->

- Не удалось получить необходимые данные при работе с музыкой и текстами. Проверьте соединение и настройки.

### 1374 · voice.e77bd7203e02d41f

<!-- LOPATA.Events: ["source:06cfbf5749f3d1dd", "source:59d9a32e2432b708", "source:28ef590ca4cef04b", "source:64779b764007bd45"] -->

- Обнаружена неподдерживаемая возможность при работе с базой знаний.

### 1375 · voice.66771f257ecaeacb

<!-- LOPATA.Events: ["source:07589fbc322514eb", "source:64a3b830b52b3b2c", "source:8929a2cbe29ff842", "source:1c2021a81c926936", "source:7b17edf38a9be483", "source:c61b9b138f28e473", "source:769344ee8a8d68cf", "source:74acd55ce0aa38c2", "source:0c8d8110969b7f46", "source:83d95a0f9e494175", "source:f341e5e952ceeffe", "source:f8b3d1b8a0b3b0b5", "source:ca7e7d77f40258fc"] -->

- Не пройдена проверка целостности при работе с музыкой и текстами.

### 1376 · voice.044b93ea4e905184

<!-- LOPATA.Events: ["source:86176b6fcb2f8f98", "source:6f5ebc7ada39fb34", "source:93b82e4746d35531", "source:0825d16f33818346", "source:89bb45e2783f8f78", "source:a3b158ba0f8d7d88", "source:b0e2257a20b28dbe", "source:ffbf340740bb23af", "source:9d12122c53c016e6", "source:f6ccf159c9a7b940", "source:117a72d35690a2f1", "source:3177d10915328796", "source:d1cd566ee2bcb6e2", "source:7e0be6dc8e8257f5", "source:e6e8a0040a633fa4", "source:32a5c0177cd0f97e", "source:4b2dc466e03dbb97", "source:1daa543cb26bf111", "source:640d880daccd7b98", "source:ec6dc7887a52ae5c", "source:ecb899c177c2c1bf", "source:b586a793ff95b101", "source:0d8b1eb34e7a982a", "source:bd6c342596763fd5", "source:a3da7e0f2bbad154", "source:e33e3a1481a40b4e"] -->

- Обнаружены некорректные или неподходящие данные при работе коллекции ЛОПАТЫ.

### 1377 · voice.4c44df4e39a28bde

<!-- LOPATA.Events: ["source:936eb0d986863ed6", "source:4f3f6440e6fe0ee6", "source:beb5004b3be193c9", "source:e1f63845cb37653a", "source:16716c8715916aac", "source:ea073b61ebf2c963", "source:5923704b9c9e2b7d", "source:09061603ea7300fe", "source:4346eb397971213b", "source:2c2e65e9a95299bd"] -->

- Обнаружены некорректные или неподходящие данные при финансовом анализе.

### 1378 · voice.cb76502001226a5a

<!-- LOPATA.Events: ["source:a106aaf0999d70b4", "source:264b1ecb4cb33992", "source:93ebf63bf9e36289", "source:d93ae52d77ed1956", "source:c4d9b22a7696aa4c", "source:1120b9aa088922b3", "source:1eaa636ede35afb0", "source:9dc2518f3b182a3b", "source:89453b33270abe48", "source:77a166e1b57bb725", "source:43157e076d46dad7", "source:a1334b7e918e56b6", "source:0908db88b97ccc27", "source:38e0c64b8f9393a2", "source:b2d5250cc74be0ad", "source:4c2acf54ad3e172f", "source:979859a9b794bf07", "source:490ef3ac4515aabb", "source:4c023a5551b4f34c", "source:f34b1e2e66fb713a", "source:4d0144e86b3c8dc3", "source:369f9a19d34421a1", "source:5927513b59d6284a", "source:5cb9e95862ea4a04"] -->

- Обнаружена неподдерживаемая возможность при работе с музыкой и текстами.

### 1379 · voice.7010a48f2db731b4

<!-- LOPATA.Events: ["source:a0d7469d257721ce", "source:cdf217b96a0db0d9", "source:701f0d51e0e2fabf", "source:0913849fcc195d2e", "source:cdb42494ed8e1ae5", "source:d3bdd2e1fe2fac60", "source:8ecf8c49f938a849", "source:e09e56d92ac5a5c4"] -->

- Не пройдена проверка целостности при обработке видео.

### 1380 · voice.eafac9e38fd95a0c

<!-- LOPATA.Events: ["source:092d292bcd5afc9a", "source:ffc95aaa924b0139"] -->

- Превышено время ожидания при обработке видео.

### 1381 · voice.6165ba7caebc50ab

<!-- LOPATA.Events: ["source:5f9fe37805071832", "source:353f540125e0de2c", "source:09571fe8386baaf2"] -->

- Не пройдена проверка целостности при обработке книги.

### 1382 · voice.5aa42f2a50aca3ad

<!-- LOPATA.Events: ["source:9f0382588d8aceb0", "source:d53cf7bebface95f", "source:96120a568c58cd00", "source:3c7275589fd0ca44", "source:0962f2f3814e01b6"] -->

- Не найдены необходимые данные или компоненты при чтении документа.

### 1383 · voice.7be976f387208c08

<!-- LOPATA.Events: ["source:ba125c0547aa1016", "source:fdc658d6d36a48be", "source:ad68f8aa2145e702", "source:bcf00824f1773fb7", "source:fb066d6a981de336", "source:59649dbb9843a0fb", "source:defa81f08275dd9c", "source:7fe3cd9750b0a8d0", "source:cc030f4cbdb41795", "source:a83d993793ff1867", "source:ad6c8cff95af35ee", "source:c6b1c511fe6387be", "source:91b5acf82645f390", "source:73ed32257865d04d", "source:50770a331a00c903", "source:16e588891dde2484", "source:c8a2c1f161571647", "source:a09826be0f787b9c", "source:dcc3516924650c06", "source:b2ca9c999029d520", "source:ed4763a842504036", "source:49feaabbc8a3af8f", "source:5c18d06b6f75d36c", "source:b28fb60e2880272d", "source:6e66cd505dcf73a0", "source:0acad99024c8662d", "source:6434d9ab5e40e079", "source:6328cb4b1a37da8e", "source:e3ce6a2397915de6"] -->

- Не найдены необходимые данные или компоненты при обработке изображения.

### 1384 · voice.253a765b2207f2f9

<!-- LOPATA.Events: ["source:a41e34cb4a61e379", "source:32d4f52ab9e3b984", "source:7c0b3f28ff3aa678", "source:df67095942e9dc98", "source:55b305204357c2e5", "source:e0722f4fdbc4b9aa", "source:f30873c959d7a9ea", "source:7e8b43ace25fa869", "source:443d8aa422471d17", "source:31fe071eaf892b5e", "source:cad70b07aaa713a0", "source:d9a79500bc9fcf5d", "source:186f71bd2dbb025f", "source:5da0f84493a7cc1c", "source:17bb17f09d8aaab5", "source:457670c6412872e2", "source:6f1c7bb6096512d6", "source:b31da6ba2acd9d23", "source:b0f4c2ee9c802003", "source:a8756d7e68d4da76", "source:b6a350b04231432d", "source:b5c7c27a6c96051b", "source:43f180290cd7f0c1", "source:0b113e5f1ba353c5", "source:eb3cc273f95fb167", "source:2843511cca00422e", "source:1f3161d5836da48a", "source:58f003af1f4f3a66", "source:1435ebd8e2c1e81f", "source:1e321540c978a9fd", "source:293503d94f77198a", "source:2c6e3ab5f317926e", "source:eb4bc1d587c50ac3", "source:9f19c76a81266a1a", "source:7613e5a6bd6806cc", "source:5b7ef1d4ef1d4f71", "source:33c1a065c8104c19", "source:9a5f4fb2d49733e5", "source:3d29474334826167", "source:234bbe4a67d4db69", "source:c6e21598c9afcf0c", "source:bc35931fed8464c4", "source:b9d4d6ce8b83bce9", "source:190fc5a64dd90574", "source:2469168d2ee76897", "source:64c4496149ddee20", "source:ffc07b3f8d87fb3f", "source:4eb4cfc7384c837c", "source:4b84461d5503d51a", "source:e963e3ab93819042", "source:55d5356245b2152e", "source:a48533a03272b8b0"] -->

- Обнаружены некорректные или неподходящие данные при обработке изображения.

### 1385 · voice.48309545660f5b47

<!-- LOPATA.Events: ["source:7d2d9ba221a4d682", "source:ae521006334ac422", "source:1275d42d33dd7ad2", "source:53c9404113cf5fee", "source:1c51062cd959c4c1", "source:1cb444523a90685a", "source:3b5fea3d58a80f9e", "source:9012b28b567dda60", "source:0cf88da0f66b452b", "source:7150da47f48dbdd0"] -->

- Не найдены необходимые данные или компоненты при работе коллекции ЛОПАТЫ.

### 1386 · voice.feed1898cf8d3901

<!-- LOPATA.Events: ["source:f4a7bbb8005c8df6", "source:15227d995d10329b", "source:0d04d496a40693ec", "source:15972aa9ac972d69", "source:51331599432b8f43", "source:6350fc16b4bfbd63", "source:2067dd10833d4e62"] -->

- Не удалось получить необходимые данные при работе над произведением. Проверьте соединение и настройки.

### 1387 · voice.a24f032378afafd4

<!-- LOPATA.Events: ["source:5e653e1ac57a028c", "source:5439ff96d3eb9069", "source:ea489b373c644c7b", "source:ca75ee65a74efe51", "source:c7d35e8ff155f957", "source:aa76bf98b12374b8", "source:effe4a4a2f4872b2", "source:a1c439331e8a9a0f", "source:558941ffe86f8f62", "source:9583b1b5c27b9b22", "source:ad3acf2bdfb54b2d", "source:0e9da5307e156e68", "source:ce23717dda6e653e", "source:c9f2875c96947285", "source:f6846d043aeb2a83", "source:98bb1c1350f1725e", "source:d3c5705fd627f45b", "source:75565fcc153510d3", "source:581421e143fb315b", "source:63bb6c2d7b1a7d94", "source:d90c63dce231950c"] -->

- Обнаружены некорректные или неподходящие данные при работе чата.

### 1388 · voice.b90e8bc7aec6c15d

<!-- LOPATA.Events: ["source:b8a6252415a6ecfa", "source:90704d573add8a68", "source:f68228e4fa561ecd", "source:5dd94cb52f094c16", "source:0ea533eee72d674b", "source:860c451b0b8abbe8", "source:f06e6091d3d5fd4b", "source:984d1ff1a0b59636", "source:e14fe645e17f1ca3", "source:2a77f9e73c6eb968"] -->

- Обнаружены некорректные или неподходящие данные при чтении документа.

### 1389 · voice.3f0abc90df1c1805

<!-- LOPATA.Events: ["source:a2c8fe3ad55008bd", "source:6ac1629d50282709", "source:ab22b6d9484c3fee", "source:d94bc27593ea515b", "source:10ce786f80bebff7"] -->

- Не удалось завершить действие при захвате экрана. Подробности показаны в окне программы.

### 1390 · voice.f7d21ae0ce196fff

<!-- LOPATA.Events: ["source:b18fc809fe2e25e9", "source:8d2f579ad81be3c2", "source:bbc3f45be72a1eb4", "source:8c7ce89aa713cb70", "source:f1ef971b3a046b68", "source:14843e1cae40a743", "source:520b77a9d971ef98", "source:44d93963042d8d5b", "source:d4eed9b186417824", "source:51839440a7bb0a12", "source:177a0f6247930193", "source:a4510a3dadb11717", "source:7d0c51a794143160", "source:e289ae7e9ae1671e", "source:fb18377fbdf0b1a8", "source:4cf1a2b7374c084b", "source:803260ab83ebfc0d", "source:d42af2ced89fa027", "source:49ebc8244d0694b9", "source:381fd05ddf15d1f9", "source:b69dd31aeba89965", "source:4ededf323be9d8a2", "source:415cd7e0648d00d5", "source:a396a5082372a385", "source:9a89078b75213d7e", "source:f14535234c9d8d66", "source:687b5a22adb5b43e", "source:d2b5bf51de4da285", "source:bf80467965b85b69", "source:64ea084d59bb2a83", "source:4a8c4db8bde781a7", "source:973d212b3169d998", "source:dabf827c06dc24b7", "source:268f488e070b680a", "source:c4e5f74d06c90383", "source:cfbc8dd3e4480599", "source:6542c4b0e7d455fe", "source:1108a10e4d1cd278", "source:9a52dab22870e35f", "source:c0b5ab18786ab1af", "source:52f7f55e478aa36a"] -->

- Обнаружены некорректные или неподходящие данные при обновлении ЛОПАТЫ.

### 1391 · voice.03c149063ac1b6d6

<!-- LOPATA.Events: ["source:bc31fbee5805772f", "source:837a04d41005520a", "source:8cb6c48ee4bcae09", "source:bb857d3d8a0eaef3", "source:b61c00e28e2722d4", "source:dcfa5483fb7befb1", "source:11e20e66fcc7c479", "source:46de8fbe6f68e1c6", "source:ab99d88a37a09557", "source:1271f54b5c28a4fe", "source:82abde89e6743429", "source:5bacce9c5ab95cb9", "source:164810b0a8d342d3", "source:37c14d5523c5f365", "source:ee13ec78fa7a3c73", "source:cc3fb9e5b2438130", "source:f559bb24319283ea", "source:90195f25bc3910c8", "source:ca24d85a21d1db7e"] -->

- Не найдены необходимые данные или компоненты при подготовке видеоролика.

### 1392 · voice.c853f43fae1b5cec

<!-- LOPATA.Events: ["source:132ce3b8aa65ab6f"] -->

- Первый проход завершён. Выберите произведение.

### 1393 · voice.3b29f7dcb0b442d1

<!-- LOPATA.Events: ["source:5c1607506124a89f", "source:e2ff5ceef3e0fc00", "source:348e3b8b3a4370b1", "source:166f15b4eb933a0b"] -->

- Обнаружена неподдерживаемая возможность при работе коллекции ЛОПАТЫ.

### 1394 · voice.e06166e3c7f50a7d

<!-- LOPATA.Events: ["source:a1d8d8fc194c463d", "source:43e12b5e7cc2d21f", "source:edb829f65a4ce204", "source:6778f17865ce2750", "source:53232b30b70d5e3b", "source:bcddb35bca015dc8", "source:b890d63f871ed809", "source:17c165fccbbfa5b6", "source:56ced8ff60cb5d31", "source:fe79bf396eadc703", "source:24f38521ef503e2c", "source:55c69a79a9cef72a", "source:7f5712b2aa1a7dec", "source:97b6b19610506dd9", "source:449e77cce33743c2", "source:1a270cb3accb2127", "source:d9cc86bfee4ed733", "source:e715c8528a07ac7e", "source:668965d9613e571b", "source:2842a8ee73dfb12e"] -->

- Не найдены необходимые данные или компоненты при обработке видео.

### 1395 · voice.f884e5f0fd11ebf1

<!-- LOPATA.Events: ["source:67186a6b799b8081", "source:d67f8f48a7d35852", "source:a14b1bf30a85e818", "source:95cb96925e2e6321", "source:1de9ef52c7e872b7", "source:63f808a905f93c28", "source:abd3815e70f1daf2", "source:7fc0bea2e2a17992", "source:fd19131407b8c66e", "source:6fea98c89b55747a", "source:4ac779871e5fdaa5", "source:43dc07e6af7814dc", "source:549a3a1afc768dda", "source:5f630ffa3f38bf40", "source:5ef94c847a1d658d", "source:20049dc42c5b81bd", "source:be83409cdc5f2980", "source:180db36cde7ed689", "source:7fb0a9b9836d6cfa", "source:2b6ae194f452db3a", "source:a75aefcf6b31a80c", "source:c1ec5330f1c7710f", "source:d000da204285652b", "source:e56e88b65ba04790", "source:a3ed82dbe10fe381", "source:cf9f12a93038a3e8", "source:cd3e63c73f87c1a9"] -->

- Обнаружены некорректные или неподходящие данные при подготовке видеоролика.

### 1396 · voice.7759364c7834a2ff

<!-- LOPATA.Events: ["source:1868962f67da4b84"] -->

- Обнаружены некорректные или неподходящие данные при озвучивании текста.

### 1397 · voice.69df2b07965bc925

<!-- LOPATA.Events: ["source:18de1fbbf5439695"] -->

- Другая операция с моделью уже выполняется.

### 1398 · voice.c151c93e1978e2e4

<!-- LOPATA.Events: ["source:200efd25a6c683a8", "source:534bcea9330bb22f", "source:f485118e654fdc07", "source:8597e8c4ef98e39a", "source:485226d458603faf", "source:18fe9fc009c5a4ce"] -->

- Превышено время ожидания при работе с музыкой и текстами.

### 1399 · voice.ce55cf1857acbaa8

<!-- LOPATA.Events: ["source:195cdedb720c9afb"] -->

- В библиотеке игры нет необходимого рисунка.

### 1400 · voice.36020cd9a368dd46

<!-- LOPATA.Events: ["source:19b3d17bdc33b5b5"] -->

- Рисунок не был подготовлен перед запуском игры.

### 1401 · voice.daab52fc6df709ba

<!-- LOPATA.Events: ["source:1cce72ba2531371f"] -->

- Выполняется загрузка данных и компонентов.

### 1402 · voice.23dc2ee2907d3321

<!-- LOPATA.Events: ["source:1e5c839b3edfaa42", "source:9ae5b5e8562ae8eb"] -->

- Не удалось получить необходимые данные при обработке видео. Проверьте соединение и настройки.

### 1403 · voice.58fe577789be88e0

<!-- LOPATA.Events: ["source:48cc670b41053eef", "source:4f061b1b85185757", "source:762dcd8efa6e4a79", "source:cae43318289726a4", "source:890974447608d59a", "source:ab84086ccec17383", "source:fa2b5c078ed04ebe", "source:4629e389dc254475", "source:88002a8416304091", "source:c61d842db6362b03", "source:1f4fab1775bb043c", "source:4edbd68a15af7566", "source:78a6ad9e84ff2668", "source:c50e41baf72b78cb", "source:662d364eab22967f"] -->

- Обнаружены некорректные или неподходящие данные при работе с базой знаний.

### 1404 · voice.c84782222690fc72

<!-- LOPATA.Events: ["source:3737475e21a0ec53", "source:9ac02c1505756d34", "source:20dfc4914430f6ac", "source:243c93f7fa806ad9"] -->

- Не найдены необходимые данные или компоненты при выполнении фоновой задачи.

### 1405 · voice.84cc182d3a01c7b7

<!-- LOPATA.Events: ["source:22c12feafe180755", "source:d3c4e899415053f8", "source:65871c8726b2d334", "source:3c73675c98c964e0", "source:4e8dd37f1e59cc32", "source:7506f41280b3f224", "source:3d61970de0087981", "source:f8d8f99ee381b133", "source:dcae5c987dffd4bb", "source:a0b07c76d84ad289"] -->

- Обнаружена неподдерживаемая возможность при обработке видео.

### 1406 · voice.56b4f4a7eb0395e6

<!-- LOPATA.Events: ["source:2528025725f331fa", "source:7c85d83a40a812e2", "source:ef7ae7786c1a8f9e", "source:bb72f46f8b82b266", "source:3f113cbafc193305", "source:95ff8cf26f19433a", "source:31898e7ee13a9809"] -->

- Не пройдена проверка целостности при обновлении ЛОПАТЫ.

### 1407 · voice.555af036bf02fae9

<!-- LOPATA.Events: ["source:36e07e786352f438", "source:29a9c2e4853fadef"] -->

- Исполнитель ещё занят при работе коллекции ЛОПАТЫ. Дождитесь завершения текущей задачи.

### 1408 · voice.f0e8245a852ad6b1

<!-- LOPATA.Events: ["source:2a1b2cd8ba1e7876"] -->

- Анализ остановлен. Ответы сохранены.

### 1409 · voice.26068d48b87f52a5

<!-- LOPATA.Events: ["source:8648e20366c789de", "source:413bc1e34d104b66", "source:efebbfd83cc5b916", "source:a6905258fac2c486", "source:ac30472522b02cd3", "source:761f6348af85811d", "source:2a2feaf389f0217c", "source:4f87fadd53ac25a3", "source:fd8fb1a85d496907", "source:fe9f55cdeeb5e211", "source:32563a6d3b07684d", "source:f43a8440cb942d5c", "source:9c511e630a25d9ee", "source:ddf384547e7ee538", "source:f8d96f9f2f3325f2"] -->

- Не найдены необходимые данные или компоненты при обновлении ЛОПАТЫ.

### 1410 · voice.0d77c4cbdee08ba9

<!-- LOPATA.Events: ["source:bab6871eb8ca6bee", "source:7a7a79bda5fe0024", "source:8666cf03da81312b", "source:2a32bd402f47aa3a", "source:a479b9d180122145", "source:e152a82611402081"] -->

- Не пройдена проверка целостности при работе с моделью.

### 1411 · voice.8ff16dc220637ced

<!-- LOPATA.Events: ["source:2d4a3e3bc7f6c945"] -->

- Размер рисунка библиотеки превышает разрешённый размер.

### 1412 · voice.4cb70f82d65b4819

<!-- LOPATA.Events: ["source:cfaf7ec46e1afa24", "source:62d1ebb0dcb31385", "source:67a935ba555941c3", "source:986743d271d04a39", "source:371e0077e3145aca", "source:8307e4a3f291f207"] -->

- Обнаружена неподдерживаемая возможность при работе с моделью.

### 1413 · voice.becb028cf8c910db

<!-- LOPATA.Events: ["source:70e88eb9bd541203", "source:eba5b44140f3f8c2", "source:f040bbb0b4ad389b", "source:bec37c5b73ee802b", "source:39464a5ea39f8cce"] -->

- Нужно проверить разрешения и согласия при работе с моделью.

### 1414 · voice.d0967aac16911e0f

<!-- LOPATA.Events: ["source:d60501f46f6d5af3", "source:8920817d18310bc1", "source:f19d81fd148c0414", "source:5c19cce50547be28", "source:f737150af80dc345", "source:3b330af24a1445d9", "source:f727d10ea37f1678"] -->

- Обнаружены некорректные или неподходящие данные при голосовом вводе.

### 1415 · voice.199e108fd1a88874

<!-- LOPATA.Events: ["source:7ad8edf079cb1f2e", "source:3ba4936ac54f528a"] -->

- Не найдены необходимые данные или компоненты при захвате экрана.

### 1416 · voice.9bf2945601d466a8

<!-- LOPATA.Events: ["source:722aa9b2c2603e51", "source:3f6f597d5e6853d4"] -->

- Не удалось получить необходимые данные при финансовом анализе. Проверьте соединение и настройки.

### 1417 · voice.8775cf31ea1cfba9

<!-- LOPATA.Events: ["source:3ff160ecd2db39f8", "source:5859485806241175"] -->

- Исполнитель ещё занят при голосовом вводе. Дождитесь завершения текущей задачи.

### 1418 · voice.362b1440b1e9e422

<!-- LOPATA.Events: ["source:414a51f7c5007801"] -->

- У выбранного исходника библиотеки нет подготовленных игровых кадров.

### 1419 · voice.3376b17c52253028

<!-- LOPATA.Events: ["source:43dea20e148f909c", "source:7920642ea8dc039d"] -->

- Не найдены необходимые данные или компоненты при озвучивании текста.

### 1420 · voice.9d2016789586a2e5

<!-- LOPATA.Events: ["source:c2b8e804d3e36482", "source:91042b1c82ee78a5", "source:8ee2f938b46fc792", "source:c5efcd1fc3864313", "source:81586d5c19d96256", "source:86758f928593af56", "source:de2dd91d267afdbc", "source:44f5252f3aa35655", "source:f82116abc456b32f"] -->

- Обнаружена неподдерживаемая возможность при работе над произведением.

### 1421 · voice.64466ff03764fc32

<!-- LOPATA.Events: ["source:79e41291431b39c4", "source:731928dd9d7c1d44", "source:51206e9a721c615e"] -->

- Исполнитель ещё занят при работе над произведением. Дождитесь завершения текущей задачи.

### 1422 · voice.e72b6737d9d366b2

<!-- LOPATA.Events: ["source:797c226fe4e76978", "source:6a435dbc9bd5d29c", "source:5d71a9b4ef83cc38", "source:ff1ee1d856b9aee6", "source:8422b2eb7d43ec92", "source:54d5dfd83cf333cf"] -->

- Обнаружена неподдерживаемая возможность при обработке изображения.

### 1423 · voice.b1786d59131466d5

<!-- LOPATA.Events: ["source:573ca22116346130", "source:7a50b6f1f529582c", "source:a62cf36aef98f0bd", "source:aa70f3095671c5c3"] -->

- Обнаружена неподдерживаемая возможность при выполнении операции.

### 1424 · voice.6dd82ebc16b2977b

<!-- LOPATA.Events: ["source:b7f316d3d6f2e967", "source:596e6d5d29a156a4", "source:a61e71459e370811", "source:5f945f03c0b254db"] -->

- Обнаружена неподдерживаемая возможность при чтении документа.

### 1425 · voice.d9abbcae3ac38f80

<!-- LOPATA.Events: ["source:5b6568d6fb66b41f", "source:840b4cf3b3bfe991", "source:9e5b554eba4f834a"] -->

- Не удалось получить необходимые данные при обработке изображения. Проверьте соединение и настройки.

### 1426 · voice.a118100792be2ea9

<!-- LOPATA.Events: ["source:62b907eb198c4d11"] -->

- Превышено время ожидания при обновлении ЛОПАТЫ.

### 1427 · voice.854cfb775f41665e

<!-- LOPATA.Events: ["source:6bcd18e46ad1b355", "source:d559579c109ec2b2", "source:9f2098a0c7101e90", "source:d601328408be4efc", "source:eded8acc56be3939", "source:8059ec611a465b61"] -->

- Обнаружены некорректные или неподходящие данные при выполнении фоновой задачи.

### 1428 · voice.b017c8ea54f3a997

<!-- LOPATA.Events: ["source:6cf52f66f06b28ef"] -->

- Этот элемент библиотеки не является готовым игровым персонажем.

### 1429 · voice.3ed8d277fae3a81e

<!-- LOPATA.Events: ["source:75e9cac5e8e44fe4"] -->

- Обнаружена неподдерживаемая возможность при подготовке видеоролика.

### 1430 · voice.1e8267abc02bf42e

<!-- LOPATA.Events: ["source:768726037ca078c8"] -->

- Собираем главы и проверяем сомнительные фрагменты.

### 1431 · voice.c44cc9aedfe1cf9f

<!-- LOPATA.Events: ["source:76f8e38f6ba4d017"] -->

- Исполнитель ещё занят при выполнении операции. Дождитесь завершения текущей задачи.

### 1432 · voice.b3825ced50a8cea6

<!-- LOPATA.Events: ["source:77b1b7c2b5f54cad"] -->

- Исполнитель ещё занят при работе чата. Дождитесь завершения текущей задачи.

### 1433 · voice.626337e122d4e18e

<!-- LOPATA.Events: ["source:fd3730d93bcb24e8", "source:e7f812d003fb1e7c", "source:789239d653409e07", "source:a74704b47c005884"] -->

- Обнаружена неподдерживаемая возможность при работе чата.

### 1434 · voice.2f5703477c6856f6

<!-- LOPATA.Events: ["source:7f6f6329cc117855", "source:e218c58f086c4a24", "source:edc488eebf502538"] -->

- Превышено время ожидания при работе с моделью.

### 1435 · voice.f6722082424ebf0f

<!-- LOPATA.Events: ["source:7f7eae5537c22fa7"] -->

- Недостаточно памяти при работе с музыкой и текстами.

### 1436 · voice.7fe49bc347c29a0f

<!-- LOPATA.Events: ["source:ff4add9761d5b624", "source:85260aa08489f122", "source:ce827b347a983734"] -->

- Не найдены необходимые данные или компоненты при финансовом анализе.

### 1437 · voice.97fa369dffbb8cd8

<!-- LOPATA.Events: ["source:8f1fd524cd060d30"] -->

- Не найдены необходимые данные или компоненты при работе с векторной графикой.

### 1438 · voice.c562ce5325779a9d

<!-- LOPATA.Events: ["source:f646501281a6e1b8", "source:d0731e1e8b0071e0", "source:ac5a749d16112ab9", "source:97e0873fe37754cc"] -->

- Выполняется подготовка данных.

### 1439 · voice.85087e7b5abdaa42

<!-- LOPATA.Events: ["source:ab57ae1c3b54dcf4", "source:98ed2a641c14ca68"] -->

- Превышено время ожидания при выполнении операции.

### 1440 · voice.14fd9ba3a6666380

<!-- LOPATA.Events: ["source:99e927a4128ed8e9", "source:b542acff4e36845b", "source:e23220471c715fd1"] -->

- Обнаружены некорректные или неподходящие данные при работе с векторной графикой.

### 1441 · voice.53b45e7d6b97cd75

<!-- LOPATA.Events: ["source:ac68f32a7d467891", "source:d0dc4b5d7a39ce6e", "source:9ea223a4f0b8af43"] -->

- Не пройдена проверка целостности при выполнении операции.

### 1442 · voice.67b3fbae3e328dc1

<!-- LOPATA.Events: ["source:9f6d4e40457bf5ab"] -->

- Не найдены необходимые данные или компоненты при голосовом вводе.

### 1443 · voice.93057fdebdd20539

<!-- LOPATA.Events: ["source:a2d362d70cc6d3b8"] -->

- Слишком много кадров в составных рисунках этого уровня.

### 1444 · voice.d1b6bb7da10a9b14

<!-- LOPATA.Events: ["source:a6202ccee0b5d4c8"] -->

- Создаём книгу и сохраняем историю анализа.

### 1445 · voice.7e4343d01f268782

<!-- LOPATA.Events: ["source:a75b075d0af82961", "source:f6f17ebf109a2f69"] -->

- Не удалось открыть ответы.

### 1446 · voice.1ed7ea387d12b90f

<!-- LOPATA.Events: ["source:af294e69c1fd1f2f", "source:e0d68f9ce2a3923c", "source:ee953b96b4f98724"] -->

- Обнаружена неподдерживаемая возможность при обновлении ЛОПАТЫ.

### 1447 · voice.4e672bac3fe481c5

<!-- LOPATA.Events: ["source:bacd568fb8c98e4f"] -->

- Не пройдена проверка целостности при работе над произведением.

### 1448 · voice.2b44e26be6a4fc19

<!-- LOPATA.Events: ["source:bec329c6e23870c7"] -->

- Нужно проверить разрешения и согласия при выполнении операции.

### 1449 · voice.dc5d883b83c18916

<!-- LOPATA.Events: ["source:bfab393a5706a85a"] -->

- Превышено время ожидания при обработке книги.

### 1450 · voice.07a209c10f26c0d7

<!-- LOPATA.Events: ["source:c21bccf44cc8d492"] -->

- Исполнитель ещё занят при работе с моделью. Дождитесь завершения текущей задачи.

### 1451 · voice.db8731350755bd3e

<!-- LOPATA.Events: ["source:cfee562cb32588c8", "source:c41af3fdd399d6a3"] -->

- Обнаружена неподдерживаемая возможность при голосовом вводе.

### 1452 · voice.1c1b3aa04f0aff2d

<!-- LOPATA.Events: ["source:cffcd7bfdd343fdd"] -->

- Не пройдена проверка целостности при чтении документа.

### 1453 · voice.1b5372cfe6aa2f9a

<!-- LOPATA.Events: ["source:d94aa8a70afe589c"] -->

- Исполнитель ещё занят при работе с музыкой и текстами. Дождитесь завершения текущей задачи.

### 1454 · voice.d00fcc9b3a7eaf2e

<!-- LOPATA.Events: ["source:f001166ab02dec1e", "source:db25b171f20bc0eb"] -->

- Исполнитель ещё занят при обработке книги. Дождитесь завершения текущей задачи.

### 1455 · voice.cac9af16e0101500

<!-- LOPATA.Events: ["source:e5ed7ea021bd89db"] -->

- Превышено время ожидания при работе чата.

### 1456 · voice.c867ab31f5fb6420

<!-- LOPATA.Events: ["source:e686be9077f53e14"] -->

- Не пройдена проверка целостности при подготовке видеоролика.

### 1457 · voice.b60e9dd0b6ffa367

<!-- LOPATA.Events: ["source:e76e7e4c2053bb46"] -->

- Превышено время ожидания при обработке изображения.

### 1458 · voice.4833b91c59031b26

<!-- LOPATA.Events: ["worker:Adding the complete original song"] -->

- Добавляем исходную песню целиком.

### 1459 · voice.6a38a8e8dc4c2f54

<!-- LOPATA.Events: ["worker:Analyzing the source song's beats without loading H3"] -->

- Анализируем ритм исходной песни без загрузки видеомодели.

### 1460 · voice.7453f8ac3a5acf94

<!-- LOPATA.Events: ["worker:Assembling accepted montage shots with the complete original song"] -->

- Собираем подтверждённые сцены с полной исходной песней.

### 1461 · voice.2ae36e80132a5902

<!-- LOPATA.Events: ["worker:Assembling the complete music video"] -->

- Собираем полный музыкальный клип.

### 1462 · voice.fd99f6f6626c2488

<!-- LOPATA.Events: ["worker:Decoding original song"] -->

- Декодируем исходную песню.

### 1463 · voice.d7d98a7884eb9214

<!-- LOPATA.Events: ["worker:Decoding the original master song"] -->

- Декодируем оригинальную мастер-запись песни.

### 1464 · voice.332ba6c98176b9a7

<!-- LOPATA.Events: ["worker:Embedding"] -->

- Создаём векторное представление материала.

### 1465 · voice.2e3e5f4926254ec6

<!-- LOPATA.Events: ["worker:Encoding the music video prompt and reference"] -->

- Кодируем запрос и референс для музыкального видео.

### 1466 · voice.e789b58430ae59e0

<!-- LOPATA.Events: ["worker:Existing montage checkpoints, prompts and result were verified"] -->

- Сохранённые этапы монтажа, запросы и результат прошли проверку.

### 1467 · voice.ba22bf228e4d10a3

<!-- LOPATA.Events: ["worker:Glitchframe render complete"] -->

- Рендеринг музыкальной визуализации завершён.

### 1468 · voice.f4b3e24395f2526b

<!-- LOPATA.Events: ["worker:H3 music video with the complete original song is ready"] -->

- Видеоклип с полной исходной песней готов.

### 1469 · voice.aded592a4a917277

<!-- LOPATA.Events: ["worker:Index", "worker:Indexing"] -->

- Строим поисковый индекс.

### 1470 · voice.7a107f0361fed0a1

<!-- LOPATA.Events: ["worker:LexiconIndex"] -->

- Индексируем словарь базы знаний.

### 1471 · voice.d9812eaa1bf19513

<!-- LOPATA.Events: ["worker:LexiconMerge"] -->

- Объединяем записи словаря базы знаний.

### 1472 · voice.b7619d8f8ba6a711

<!-- LOPATA.Events: ["worker:LexiconReady"] -->

- Словарь базы знаний готов.

### 1473 · voice.b4cdb2d68a9e91b8

<!-- LOPATA.Events: ["worker:LexiconValidate"] -->

- Проверяем записи словаря базы знаний.

### 1474 · voice.0908e821707e02e4

<!-- LOPATA.Events: ["worker:Loading audited H3 Ref2VA"] -->

- Загружаем проверенную видеомодель H3.

### 1475 · voice.72eea76d2e2f27f2

<!-- LOPATA.Events: ["worker:Preparing SDXL backgrounds"] -->

- Подготавливаем фоны с помощью модели изображений.

### 1476 · voice.c19ab76b45a295d0

<!-- LOPATA.Events: ["worker:Rendering verified music-video chunks from disk"] -->

- Собираем проверенные фрагменты музыкального видео с диска.

### 1477 · voice.eb6630c3f4ea63fe

<!-- LOPATA.Events: ["worker:Resource", "worker:resources"] -->

- Проверяем доступные ресурсы.

### 1478 · voice.4e871920cd853318

<!-- LOPATA.Events: ["worker:Reused"] -->

- Используем ранее подготовленный результат.

### 1479 · voice.680494ba9fecce5a

<!-- LOPATA.Events: ["worker:Split"] -->

- Разделяем исходный материал на части.

### 1480 · voice.5a106e2babad9282

<!-- LOPATA.Events: ["worker:The complete montage music video is ready"] -->

- Полный смонтированный музыкальный клип готов.

### 1481 · voice.a909b1e89a754a82

<!-- LOPATA.Events: ["worker:Validate"] -->

- Проверяем подготовленные данные.

### 1482 · voice.141f3b60e6f9c844

<!-- LOPATA.Events: ["worker:analysis"] -->

- Выполняем анализ исходных данных.

### 1483 · voice.0fd074d922115e2f

<!-- LOPATA.Events: ["worker:cached"] -->

- Используем сохранённый результат из кэша.

### 1484 · voice.843f0cb84f86f99e

<!-- LOPATA.Events: ["worker:chunk"] -->

- Очередной фрагмент обработан.

### 1485 · voice.b47500979ae5c34d

<!-- LOPATA.Events: ["worker:chunk-start"] -->

- Начинаем обработку следующего фрагмента.

### 1486 · voice.5529c82d31ee84f7

<!-- LOPATA.Events: ["worker:close"] -->

- Исполнитель завершает работу.

### 1487 · voice.930bd7c6d2e02e09

<!-- LOPATA.Events: ["worker:compatibility"] -->

- Проверяем совместимость компонентов.

### 1488 · voice.8af7df31e23ce3af

<!-- LOPATA.Events: ["worker:conditioning"] -->

- Подготавливаем условия генерации.

### 1489 · voice.b4b85117cd0ae452

<!-- LOPATA.Events: ["worker:continuity"] -->

- Подготавливаем связность сцен.

### 1490 · voice.3fd999ca250f7919

<!-- LOPATA.Events: ["worker:done"] -->

- Обработка завершена.

### 1491 · voice.fe9bf97ca98114fe

<!-- LOPATA.Events: ["worker:first draft"] -->

- Создаём первый черновик.

### 1492 · voice.568e24a8e88225a0

<!-- LOPATA.Events: ["worker:geek-analysis"] -->

- Анализируем песню для построения видеоряда.

### 1493 · voice.4c0b87bc14f7a5b3

<!-- LOPATA.Events: ["worker:geek-assembly"] -->

- Собираем подготовленный музыкальный клип.

### 1494 · voice.24834bfcf7afd192

<!-- LOPATA.Events: ["worker:geek-cards"] -->

- Подготавливаем карточки сцен музыкального клипа.

### 1495 · voice.61c13728c790b907

<!-- LOPATA.Events: ["worker:hardware"] -->

- Проверяем доступное оборудование.

### 1496 · voice.227c5665217c4b95

<!-- LOPATA.Events: ["worker:infinitetalk-assemble"] -->

- Собираем фрагменты видео с говорящим персонажем.

### 1497 · voice.8696ade545e1865c

<!-- LOPATA.Events: ["worker:infinitetalk-audio"] -->

- Подготавливаем аудио для говорящего персонажа.

### 1498 · voice.4e499b117e381974

<!-- LOPATA.Events: ["worker:infinitetalk-text"] -->

- Подготавливаем текст для говорящего персонажа.

### 1499 · voice.aa3229aea39009d7

<!-- LOPATA.Events: ["worker:infinitetalk-window"] -->

- Обрабатываем очередной фрагмент говорящего персонажа.

### 1500 · voice.9aedfc4de7bebd14

<!-- LOPATA.Events: ["worker:ingest"] -->

- Загружаем исходные материалы для обработки.

### 1501 · voice.39eb88ea890c342c

<!-- LOPATA.Events: ["worker:initialize"] -->

- Подготавливаем исполнитель к работе.

### 1502 · voice.1bdd612368c93ace

<!-- LOPATA.Events: ["worker:load document"] -->

- Загружаем документ в редактор.

### 1503 · voice.566222d5f764e006

<!-- LOPATA.Events: ["worker:loading-diffusion"] -->

- Загружаем диффузионную часть модели.

### 1504 · voice.5918acf242e7834f

<!-- LOPATA.Events: ["worker:loading-vae"] -->

- Загружаем кодировщик изображения.

### 1505 · voice.d4629f53dc1b0839

<!-- LOPATA.Events: ["worker:memory"] -->

- Обновлены сведения об использовании памяти модели.

### 1506 · voice.0fd0564603c847da

<!-- LOPATA.Events: ["worker:montage-analysis"] -->

- Анализируем исходные материалы для монтажа.

### 1507 · voice.5bc09e9078d75f3c

<!-- LOPATA.Events: ["worker:montage-assembly"] -->

- Собираем итоговый монтаж.

### 1508 · voice.dfd561a41f246a70

<!-- LOPATA.Events: ["worker:mux"] -->

- Объединяем видео и звуковую дорожку.

### 1509 · voice.83a42027589c3893

<!-- LOPATA.Events: ["worker:native-status"] -->

- Обновлено состояние исполнителя модели.

### 1510 · voice.7e91fabc11904393

<!-- LOPATA.Events: ["worker:not_applicable"] -->

- Этот этап для текущей задачи не требуется.

### 1511 · voice.a690f2570ccd58a3

<!-- LOPATA.Events: ["worker:not_requested"] -->

- Этот этап не был запрошен.

### 1512 · voice.b93392c545598966

<!-- LOPATA.Events: ["worker:ok"] -->

- Проверка завершена успешно.

### 1513 · voice.699b1a8556e0bf11

<!-- LOPATA.Events: ["worker:pass"] -->

- Выполняем очередной проход обработки.

### 1514 · voice.afe5d2b92188c56f

<!-- LOPATA.Events: ["worker:plan"] -->

- Составляем план обработки.

### 1515 · voice.a004ed8f2554716e

<!-- LOPATA.Events: ["worker:probe"] -->

- Проверяем работу модели коротким запросом.

### 1516 · voice.b344e573dd016f6f

<!-- LOPATA.Events: ["worker:progress"] -->

- Обработка продолжается.

### 1517 · voice.567ba84fea071305

<!-- LOPATA.Events: ["worker:refinement"] -->

- Уточняем результат обработки.

### 1518 · voice.acd99f0b2bd9e9fb

<!-- LOPATA.Events: ["worker:refinement-cached"] -->

- Используем сохранённый результат уточнения.

### 1519 · voice.351d2ccf6a390ae9

<!-- LOPATA.Events: ["worker:refinement-saved"] -->

- Результат уточнения сохранён.

### 1520 · voice.bb725b41f8d3033d

<!-- LOPATA.Events: ["worker:refinement-segments"] -->

- Уточняем отдельные фрагменты результата.

### 1521 · voice.27fe2505b136bca5

<!-- LOPATA.Events: ["worker:render"] -->

- Выполняем рендеринг.

### 1522 · voice.f98d1c0ac0407b5f

<!-- LOPATA.Events: ["worker:spelling"] -->

- Проверяем орфографию текста.

### 1523 · voice.05de4ee00c9a9279

<!-- LOPATA.Events: ["worker:stage"] -->

- Начинается очередной этап обработки.

### 1524 · voice.67f7e7d30c421d60

<!-- LOPATA.Events: ["worker:take"] -->

- Создаём следующий дубль.

### 1525 · voice.f0a3ddc4d5c97b33

<!-- LOPATA.Events: ["worker:verified"] -->

- Результат прошёл проверку.

### 1526 · voice.4098c615e5150a2b

<!-- LOPATA.Events: ["worker:view settings"] -->

- Подготавливаем параметры отображения документа.

### 1527 · voice.37526038d0baded9

<!-- LOPATA.Events: ["worker:warning"] -->

- Исполнитель сообщил о предупреждении.

### 1528 · voice.634d7b41e674a7dd

<!-- LOPATA.Events: ["worker:whole"] -->

- Обрабатываем материал целиком.

### 1529 · voice.e5bc4d7f3e7e4d56

<!-- LOPATA.Events: ["xaml:Исходники/AIHub/DebugChatWindow.xaml#StatusText"] -->

- Ожидаем продолжения операции.
