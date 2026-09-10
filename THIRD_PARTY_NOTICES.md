# THIRD PARTY NOTICES — AI_HUB

## Qdrant 1.19.1 — optional downloaded runtime (2026-09-10)

Qdrant contributors; Apache License 2.0. Official source and Windows binary:
https://github.com/qdrant/qdrant/releases/tag/v1.19.1.
The original upstream LICENSE is retained in `Исходники/AIHub/Licenses/texts/qdrant-LICENSE.txt`
and copied beside the downloaded executable. This runtime is not bundled with the installer.
Retain applicable licenses/notices when redistributing; the separate audit of all native
binary dependencies has not been completed. Pinned artifact hashes and delivery details:
`Тесты/ProcessInfrastructure/README.md`.

Этот файл предназначен для уведомлений о сторонних библиотеках, инструментах, моделях, backends и материалах.

## Runtime-зависимости

### eSpeak NG

- Назначение: локальный синтетический голос ядра AI HUB и отметки слов для синхронного раскрытия текста.
- Версия: `1.52.0`.
- Официальный источник: `https://github.com/espeak-ng/espeak-ng/releases/tag/1.52.0`.
- Лицензия: GPL-3.0-or-later.
- Поставка: `libespeak-ng.dll` и каталог `espeak-ng-data` могут включаться в build/publish AI HUB в папке `VoiceRuntime/eSpeakNG`.
- MSI SHA-256: `7F673C709EA5DD579D3B5EBB98688CC575328A6AB7438D2BC405B88CEDAEAFB9`.
- DLL SHA-256: `E737572DF0A35A32B7BD444537C661C1C916B13B0B91351030C7F1D531307BEB`.
- Исходный код соответствующей версии доступен в официальном репозитории по тегу `1.52.0`.
- eSpeak NG используется только для голоса ядра. Будущие реалистичные голоса моделей-инструментов являются отдельной системой.

### System.Speech

- Назначение: доступ AI HUB к установленным Windows SAPI-голосам и событиям произнесённых слов.
- Версия: `10.0.9`.
- Источник: NuGet `System.Speech`, Microsoft.
- Лицензия: MIT.
- Поставка: библиотека включается в build/publish AI HUB как программная зависимость.

### DocumentFormat.OpenXml

- Назначение: создание итоговых документов executor-сессии в формате DOCX.
- Версия: `3.5.1`.
- Источник: NuGet `DocumentFormat.OpenXml`, Microsoft.
- Лицензия: MIT.
- Поставка: библиотека и её программная зависимость `DocumentFormat.OpenXml.Framework` включаются в build/publish AI HUB.
- Ограничение: AI HUB создаёт файл только по явной команде пользователя и не запускает внешнее приложение автоматически.

### Встроенный набор работы с файлами

Следующие NuGet-библиотеки включаются в build/publish AI HUB:

| Пакет | Версия | Назначение | Лицензия |
|---|---:|---|---|
| ClosedXML | 0.105.0 | XLSX/XLSM | MIT |
| PdfPig | 0.1.15 | PDF | Apache-2.0 |
| SharpCompress | 1.0.0 | Архивы | MIT |
| CsvHelper | 33.1.0 | CSV/TSV | Apache-2.0 или MS-PL |
| AngleSharp | 1.5.2 | HTML/SVG DOM | MIT |
| Markdig | 1.3.2 | Markdown | BSD-2-Clause |
| YamlDotNet | 18.1.0 | YAML | MIT |
| MimeKit | 4.17.0 | EML/MIME | MIT |
| Microsoft.Data.Sqlite | 10.0.10 | SQLite | MIT |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | Нативный SQLite runtime | Apache-2.0; SQLite public domain |

Источником пакетов служит официальный NuGet. AI HUB использует их только для
локальной обработки и read-only просмотра; макросы, внешняя навигация и
исполнение вложений не включаются.

### Необязательные загружаемые компоненты

Каталог AI HUB может после явного подтверждения пользователя скачать отдельные
runtimes обработки (Temurin JRE, Apache Tika, ImageMagick, Tesseract, FFmpeg,
LibreOffice, whisper.cpp и Whisper model) и программные просмотрщики (WebView2,
PDF.js, EPUB.js, LibVLC, OpenSeadragon, Babylon.js, AvalonEdit). Они не входят в
обычный publish. Их источники, версии, размеры и лицензии закреплены в
`ТЗ/2026-07-24_каталог_компонентов_и_загрузка_возможностей.md` и
`Документы_проекта/REESTR.md`.

### RHVoice и голосовые профили

- Назначение: необязательная альтернативная читалка ядра `Просто ИИ голос` и временная озвучка исполнителя «Режима неопределённости».
- RHVoice используется как отдельно установленный Windows SAPI-компонент; движок, установщики и голосовые данные не включаются в обычный publish AI HUB.
- Текущие профили ядра: русский `Aleksandr` 4.2.2 и английский `Slt` 4.1.2. Профили исполнителя текущего сценария: русский `Elena` 4.3 и английский `Bdl` 4.1.
- Установка выполняется отдельным скриптом `Инструменты/setup-rhvoice.ps1` из официальных GitHub releases; SHA-256 каждого setup-файла закреплён в скрипте.
- RHVoice C API объявлен как LGPL-2.1-or-later; репозиторий RHVoice также помечен лицензией GPL-2.0. Профили `Slt` и `BDL` основаны на CMU voices.
- До отдельной юридической проверки запрещено включать установщики или voice data RHVoice в поставку AI HUB; они остаются внешней системной установкой.

## AI-модели

### Qwen3 8B GGUF

- Назначение: основное ИИ-ядро AI HUB.
- Источник: Hugging Face `Qwen/Qwen3-8B-GGUF`.
- Файл: `Qwen3-8B-Q4_K_M.gguf`.
- Формат: GGUF, квантизация Q4_K_M.
- Лицензия: Apache-2.0.
- Размер: `5027783488` байт, около 5.03 ГБ.
- Поставка: не включается в установщик AI HUB; скачивается отдельно пользователем через программу в выбранную папку моделей.
- Проверка целостности: SHA-256 `d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785`.

### Qwen3 0.6B GGUF

- Назначение: тестовый artifact для проверки web-download инструмента и сценария `core_tool_test`.
- Источник: Hugging Face `jc-builds/Qwen3-0.6B-Q4_K_M-GGUF`.
- Файл: `Qwen3-0.6B-Q4_K_M.gguf`.
- Формат: GGUF, квантизация Q4_K_M.
- Лицензия: Apache-2.0.
- Размер: `396705472` байт, около 397 МБ.
- Проверка целостности: SHA-256 `ac2d97712095a558e31573f62f466a3f9d93990898b0ec79d7c974c1780d524a`.
- Поставка: не включается в установщик и не публикуется в GitHub; скачана в пользовательскую папку результатов `AI_HUB/Tools/Web/Downloads` для теста инструментов.

### SmolVLM2 2.2B Instruct GGUF

- Назначение: необязательный внутренний модуль смыслового описания изображений для Песочницы и будущих профильных сценариев.
- Источник: Hugging Face `ggml-org/SmolVLM2-2.2B-Instruct-GGUF`, закреплённая ревизия `1bc3c9f74ceafd4c8d4411cc9cf188bba3798f91`.
- Модель: `SmolVLM2-2.2B-Instruct-Q4_K_M.gguf`, размер `1112602656` байт, SHA-256 `0cf76814555b8665149075b74ab6b5c1d428ea1d3d01c1918c12012e8d7c9f58`.
- Проектор: `mmproj-SmolVLM2-2.2B-Instruct-Q8_0.gguf`, размер `592523200` байт, SHA-256 `ae07ea1facd07dd3230c4483b63e8cda96c6944ad2481f33d531f79e892dd024`.
- Формат: GGUF, мультимодальный запуск через `llama-server` с отдельным `--mmproj`.
- Лицензия: Apache-2.0 согласно карточке модели.
- Поставка: не включается в установщик и репозиторий; оба файла скачиваются внутри AI HUB только после подтверждения пользователя и проходят проверку SHA-256.
- Ограничение: модель возвращает наблюдаемое описание изображения, но не заменяет OCR, редактор изображений и идентификацию личности.

### Kimi-VL-A3B-Thinking-2506 GGUF

- Назначение: визуальный аналитик Среднего комплекта сценария `Анализ изображений`.
- Источник GGUF: Hugging Face `ggml-org/Kimi-VL-A3B-Thinking-2506-GGUF`, закреплённая ревизия `e7dcd093335f922a057772febc7ab27eda985b40`.
- Исходная модель: `moonshotai/Kimi-VL-A3B-Thinking-2506`, ревизия `aa1730989e7558695b44ee493623e03bd325a994`.
- Модель: `Kimi-VL-A3B-Thinking-2506-Q4_K_M.gguf`, размер `10540747680` байт, SHA-256 `72253d82d21c546587139dfd12597d491c25a13c6540d2ce18ca1581967338c5`.
- Проектор: `mmproj-Kimi-VL-A3B-Thinking-2506-Q8_0.gguf`, размер `618098624` байт, SHA-256 `5af5e5fc0ad5e2348f5227ddfa97e9241d453020c149dbe0e920ed603189ca15`.
- Лицензия: MIT у исходной модели Moonshot AI. API карточки GGUF-репозитория не сообщает отдельное поле лицензии, поэтому перед публичной поставкой нужна повторная юридическая проверка преобразованного артефакта.
- Поставка: файлы не входят в Git и установщик; AI HUB скачивает их только после явного подтверждения пользователя. Модель и projector считаются одной составной установкой и удаляются вместе.
- Проверка: статус готовности требует размера, SHA-256 и непустого ответа на встроенное тестовое изображение через `llama.cpp b9442`. Реальный тяжёлый smoke-test в ходе реализации не запускался.

### Microsoft Florence-2-large-ft

- Назначение: общая профильная модель Среднего и будущего Тяжёлого комплектов анализа изображений.
- Источник: Hugging Face `microsoft/Florence-2-large-ft`, закреплённая ревизия `4a12a2b54b7016a48a22037fbd62da90cd566f2a`.
- Формат: Transformers/Safetensors; основной файл `model.safetensors`, размер `1540980506` байт, SHA-256 `8b4e610c952eef90a836c56cda0f398a672a3a6ca7b4d96b0e09a86dee42e2c3`.
- Дополнительные закреплённые файлы (имя / байты / SHA-256):
  - `config.json` / `2445` / `fa081841369aa9c6e42faf5c52368d673b561e2c5f8fa03d1256e7408cb4130e`;
  - `configuration_florence2.py` / `15125` / `653bafddc9651eaff1583a16db4a2bb27d33ec7d541dfab7201aaa4ecaa1cfbf`;
  - `generation_config.json` / `51` / `30e9865458ecc8ee931eeeb43f44f1d169c5ab95be39e0072142a7a6b8f31990`;
  - `modeling_florence2.py` / `127415` / `5bb7aa72c6ba62e96e1bbae6bc1aaf7b4e8e28cdfc62e670de3d5b67eeab1fdf`;
  - `preprocessor_config.json` / `806` / `2f5921bbc53c7cc04251e1027b45b1cec726276be6db23d1bb40641bfbe2cf29`;
  - `processing_florence2.py` / `46372` / `4bd7158536cbf1c7891fc8efd94437d79fd09f07f539c7398fab8a885d7d8bca`;
  - `tokenizer.json` / `1355863` / `847bbeab6174d66a88898f729d52fa8d355fafe1bea101cf960dd404581df70e`;
  - `tokenizer_config.json` / `34` / `79ffcf43af8ebda99d165f61d243180da2e2639952e41e71e11611c18770489c`;
  - `vocab.json` / `1099884` / `394fdc63c71aabe0a9b97117f5d62fb5fcc4d59b2b3ea929a3929e6a53217b3c`.
- Лицензия: MIT.
- Поставка: десять обязательных файлов не входят в Git и установщик; скачиваются только после подтверждения пользователя. Дублирующий `pytorch_model.bin` не скачивается.
- Безопасность runtime: закреплённый custom code загружается только из локальной папки с `local_files_only`, `HF_HUB_OFFLINE=1` и `TRANSFORMERS_OFFLINE=1`.
- Проверка: готовность требует непустого результата локального smoke-check. 24 августа 2026 года реальный offline smoke-check успешно загрузил веса, обработал тестовое изображение и сгенерировал непустой ответ. `flash_attn` для CPU-пути необязателен и не установлен; временный Python runtime не является готовой релизной поставкой.

### BAAI bge-reranker-v2-m3

- Назначение: будущая вспомогательная модель интернет-инструмента для выбора более подходящих результатов поиска.
- Источник: Hugging Face `BAAI/bge-reranker-v2-m3`.
- Формат: `safetensors` + tokenizer/config файлы.
- Лицензия: Apache-2.0.
- Commit: `953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e`.
- Размер: `2293242108` байт, около 2.29 ГБ.
- Поставка: не включается в установщик и не публикуется в GitHub; скачивается AI HUB после основного ядра в выбранную пользователем папку моделей.
- Текущее подключение: только автоскачивание, manifest `tool-model.json` и отображение в F12 как служебной модели без запуска.

## Developer tooling

### MSTest.Sdk

- Назначение: запуск автоматических тестов AI_HUB.
- Версия: `4.2.3`.
- Источник: NuGet `MSTest.Sdk`, Microsoft/MSTestFramework.
- Лицензия: MIT.
- Поставка: используется только при разработке и не включается в runtime или установщик.

## Backends

### Qwen2.5-Omni-3B

- Назначение: единая модель зрения, редактуры и озвучивания Тяжёлого
  режима `Анализа изображений`.
- Источник: Hugging Face `Qwen/Qwen2.5-Omni-3B`.
- Ревизия: `f75b40e3da2003cdd6e1829b1f420ca70797c34e`.
- Лицензия: Qwen Research License Agreement; разрешено только
  некоммерческое исследовательское и оценочное использование. Файл `LICENSE`
  входит в управляемый manifest модели.
- Поставка: веса не входят в Git, publish или установщик; загружаются
  отдельно только после подтверждения пользователя. Heavy принимает checkpoint
  только при полном размещении на CUDA GPU; CPU/disk offload запрещён.
- Проверка: в каталоге AI HUB закреплены 16 файлов, включая 3 shards весов;
  их общий размер `11989065629` байт, размеры и SHA-256 закреплены отдельно.
  Новая модель ещё не скачана; проверка vision и Talker остаётся на
  пользовательской приёмке.

### Python Qwen2.5-Omni Heavy runtime

- Назначение: изолированный native Windows worker через CUDA PyTorch, Transformers,
  Accelerate и `qwen-omni-utils`.
- Проверенные прямые версии: Python `3.12.10`; PyTorch/Torchvision/Torchaudio
  `2.11.0+cu130 / 0.26.0+cu130 / 2.11.0+cu130`; Transformers `5.16.1`;
  Accelerate `1.14.0`; `qwen-omni-utils 0.0.9`; NumPy `2.5.2`;
  SoundFile `0.14.0`; audioread `3.1.0`.
- Лицензии: Python PSF; PyTorch/Torchvision/Torchaudio BSD-style;
  Transformers/Accelerate/qwen-omni-utils Apache-2.0; NumPy BSD-3-Clause;
  SoundFile BSD-3-Clause; audioread MIT.
- Поставка: среда `Runtime/Python/qwen3-omni/.venv` не входит в Git и установщик.
- Подготовка: `Инструменты/setup-qwen-omni-runtime.ps1`; скрипт и CUDA-probe
  были успешно выполнены 2026-08-29 для прежнего Qwen3-контура; импорт классов
  Qwen2.5-Omni проверен 2026-08-30 без переустановки среды. Перед будущим распространением готового архива
  среды отдельно проверить и приложить notices всех транзитивных пакетов.

### llama.cpp

- Назначение: первый локальный backend для debug-проверки GGUF-моделей через `llama-cli.exe`.
- Источник: GitHub `ggml-org/llama.cpp`, release `b9442`.
- Runtime-папка: `Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64`.
- Лицензия: MIT.
- Поставка: runtime-файлы скачаны локально для разработки и не публикуются в GitHub; решение о включении в будущий установщик нужно принимать отдельным ТЗ.
- Использованные архивы:
  - `llama-b9442-bin-win-cuda-12.4-x64.zip`, SHA-256 `77d78a1d7a1d80e051c3b43db64c0433b97d11fe12f525ddfa50302f726515f1`;
  - `cudart-llama-bin-win-cuda-12.4-x64.zip`, SHA-256 `8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6`.
- Проверка: после восстановления полного набора DLL `llama-server.exe` стартует, `/health` отвечает `ok`, `/v1/chat/completions` возвращает ответ Qwen3 8B.
- Ограничение: для Qwen3 server-запуск требует `--reasoning off`, иначе OpenAI-compatible поле `message.content` может быть пустым из-за thinking-режима.

### chatllm.cpp

- Назначение: локальный visual-runtime Kimi-VL Среднего комплекта.
- Версия: release `v24`, commit
  `f5f1d25365fb59447eb58994030c5acd492fcd53`.
- Источник: GitHub `foldl/chatllm.cpp`.
- Лицензия: MIT; текст лицензии поставляется вместе с runtime-файлами.
- Runtime-папка: `Runtime/Backends/chatllm.cpp/v24/win-x64`.
- Архив Windows x64: SHA-256
  `F92F48325E4B1351FBED6BD434E07F656B67CB535B36A14E608EF88C773DAF91`.
- Ограничение: используется CPU-профиль; GPU-offload не считается проверенным
  продуктовым режимом.

### ImageMagick

- Назначение: приватное декодирование и подготовка изображений внутри
  `chatllm.cpp`; глобальная установка в Windows не выполняется.
- Версия: `7.1.2-30`, portable Q16-HDRI x64.
- Источник: официальный release ImageMagick.
- Лицензия: ImageMagick License; лицензионные файлы находятся в приватной
  runtime-папке.
- Архив: SHA-256
  `D98471F5EC9D87E222C69C8C28C98FE6665DAB76CD3EF752C5E4DE785BE553BE`.

### Python reranker runtime

- Назначение: временный dev-runtime для запуска `BAAI/bge-reranker-v2-m3` в web-search rerank слое.
- Runtime-папка: `Runtime/Python/reranker/.venv`.
- Основные пакеты:
  - Python 3.12.10 — PSF License;
  - PyTorch `2.12.0+cpu` и Torchvision `0.27.0+cpu` — BSD-style licenses;
  - Pillow `12.3.0` — HPND License;
  - Transformers `4.41.2`, Tokenizers `0.19.1` — Apache-2.0;
  - timm `1.0.28` — Apache-2.0;
  - einops `0.8.2` — MIT;
  - Safetensors `0.7.0` — Apache-2.0.
- Поставка: runtime-папка локальная, не публикуется в GitHub и не включается в установщик.
- Ограничение: это временный dev-слой; Florence использует совместимый с закреплённой ревизией runtime и только локальный проверенный custom code. Перед релизом нужно оформить управляемую поставку или заменить на более компактный backend.

## Внешние сетевые сервисы

### ipwho.is / ipwhois.io

- Назначение: примерное автоматическое определение местоположения пользователя по IP для скрытого служебного контекста AI-ядра.
- Endpoint: `https://ipwho.is/?lang=ru`.
- Источник документации: `https://ipwhois.io/documentation`.
- Поставка: не включается в программу; используется как внешний HTTPS-запрос.
- Хранение: AI HUB сохраняет только примерное место, страну/регион/город, timezone и координаты, если сервис их вернул; сам IP-адрес не сохраняется.
- Ограничения: free endpoint без API-ключа имеет fair-use limit; если сервис недоступен, AI HUB продолжает работу без местоположения. Перед публичным/коммерческим релизом нужно пересмотреть условия использования или заменить провайдера.

### DuckDuckGo Lite

- Назначение: первый dev-провайдер web-поиска для проверки Tool Gateway без API-ключа.
- Endpoint: `https://lite.duckduckgo.com/lite/?q=...`.
- Поставка: не включается в программу; используется как внешний HTTPS-запрос.
- Ограничения: текущая интеграция парсит HTML и подходит для ранней проверки, но перед публичным релизом нужно заменить или юридически/технически подтвердить выбранный search provider.

## Правило обновления

При добавлении зависимости нужно указать:

- название;
- назначение;
- версия;
- источник;
- лицензия;
- тип: runtime-зависимость, developer tooling, внешний инструмент, встроенная библиотека, AI-модель или backend;
- можно ли поставлять вместе с программой;
- особые ограничения.

Основной реестр зависимостей ведётся в `Документы_проекта/REESTR.md`.

## Лицензионный интерфейс 0.1.44-dev

Подготовленные записи и доступные тексты находятся в Licenses внутри приложения.
Дата и источник показаны для каждой записи. Это частичный комплект, не заключение
о полноте соблюдения всех условий. Список незавершённых проверок:
Документы_проекта/Лицензии/2026-09-05_подготовка.md.
Kokoro RU: точный OpenRAIL-текст пока отсутствует; код Apache-2.0, accentuator
декларирует MIT отдельно. Нативный OpenSSL 1.1.1k — OpenSSL AND SSLeay.
