## 2026-10-07 — music audio runtime

The separate `music.audio` bundle uses FFmpeg 8.1 LGPL shared libraries,
Opus 1.5.2 (BSD-3-Clause and IPR notice), and LAME 3.100 (LGPL-2.0-or-later).
No GPL/nonfree codecs or network protocols are enabled. Replaceable DLLs,
original licenses, MinGW/GCC runtime notices and exact corresponding sources
accompany every bundle in `MusicAudioRuntime`. See
[the reproducible recipe](Инструменты/MusicAudioRuntime/README.md) and
[component notice](Исходники/AIHub/Licenses/texts/music-audio-NOTICE.md).
The source ZIP includes the FFmetadata escaping fix, configuration and toolchain
inventory. Reverse engineering to debug modifications to LGPL components is
permitted. This runtime does not replace the separate video capture bundle.

## 2026-10-07 — managed hardware libraries

Active GGUF and image-generation scenarios use separate CPU/Vulkan bundles:
llama.cpp b9442 (`d4c8e2c29ce2fb9a251a0a4a16d6c857b4f70f8c`, MIT),
stable-diffusion.cpp `3f8527a46c54ecf4cb4ed6003da8e8982283c73c` (MIT and preserved
third-party notices), Microsoft VC/Redist 14.44.35211.0 and Vulkan Loader 1.4.304.0.
Build/package recipes, original notices and archive/file SHA-256 manifests are
retained. The old mandatory llama CUDA/libomp payload is retired; historical
entries below do not describe its current delivery.

Managed Python 3.12.10 profiles use pinned upstream downloads: PyTorch 2.10
CPU/CUDA 12.6/CUDA 12.8/XPU, or AMD PyTorch 2.9.1 / Windows ROCm 7.2.1;
Transformers 5.3.0. Original package licenses and notices are retained in each
profile and `Исходники/AIHub/Licenses/texts/python-*-NOTICES.txt`.
Separate catalog entries cover Microsoft redistributables, Intel OpenMP/XPU,
NVIDIA CUDA/cuDNN and the AMD component terms. Acquiring a CPU profile does not
require CUDA agreement. Profiles are assembled from original sources, not
redistributed as a repackaged Python binary archive. NCNN Real-CUGAN's original
Microsoft vcomp140.dll is covered by its separate redistributable entry.

Additional Jelly dependencies retain their existing separate notices and
version reports; this does not claim a complete transitive license audit.
See [hardware matrix](Документы_проекта/АППАРАТНАЯ_ПОДДЕРЖКА.md) and the component
license catalog for sources, versions, delivery and physical test boundaries.

## 0.4.1-beta — CUDA и Vulkan для YuE2

Музыкальный GPU-комплект содержит CUDA SM86/SM89/SM120a, Vulkan и CPU через
GGML_BACKEND_DL. Отдельный CPU AVX2 комплект сохраняется. Pins yue2.cpp/GGML
и условия CUDA/MSVC прежние; оригинальные MIT уведомления не менялись.
Vulkan-Headers v1.4.363: MIT или Apache-2.0; Vulkan-Hpp v1.4.363: Apache-2.0.
Полные оригинальные тексты находятся в `MusicRuntime/win-cuda128-x64/LICENSE-Vulkan.txt`
и `Licenses/texts/music-Vulkan-LICENSE.txt`, каталог ID `runtime.music-yue2-vulkan`.
Источники: https://github.com/KhronosGroup/Vulkan-Headers/tree/v1.4.363/LICENSES
и https://github.com/KhronosGroup/Vulkan-Hpp/blob/v1.4.363/LICENSE.txt.
LunarG SDK используется только для сборки, не поставляется и не устанавливается.
Системные NVIDIA/Vulkan драйверы в комплект не входят и не изменяются.
Наш probe и рецепт сборки доступны в `Инструменты/MusicNative` и
`Инструменты/build-music-native.ps1` под GPL-3.0-or-later.

## 0.4.0-beta — поставка музыкального runtime

YuE2 (pin 11c1ecb084329200e22fcb286e252b847442ea5c), GGML
(40e16e4a814f7fe851a0c486fb9e8c722e957830) и yyjson: оригинальные MIT LICENSE
сохранены рядом с бинарниками. CPU AVX2 и CUDA 12.8 SM89 размещаются
в MusicRuntime/win-x64 и MusicRuntime/win-cuda128-x64 каталога приложения;
это же содержимое включено в подписанное файловое обновление.
CUDA: только cudart 12.8.90 и cuBLAS 12.8.4.1 из проверенных официальных архивов,
полный NVIDIA LICENSE; nvcc/CCCL/SDK не распространяются.
Microsoft CRT 14.44.35112: неизменённые msvcp140.dll, vcruntime140.dll,
vcruntime140_1.dll из Visual Studio 2022 VC/Redist/MSVC/x64/Microsoft.VC143.CRT;
app-local поставка, без системной установки. Полный текст условий в
Licenses/texts/music-MSVC-RUNTIME.txt. Источники:
https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution и
https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/.
Все компоненты учтены в штатном каталоге лицензий. Веса скачиваются отдельно.
Проектная GPL не заменяет условия отдельных библиотек или моделей.
Записи о локальной подготовке ниже сохранены как история.

## YuE2 native runtime — 2026-10-05

Local development runtime: yue2.cpp revision
11c1ecb084329200e22fcb286e252b847442ea5c (ServeurpersoCom/yue2.cpp),
GGML revision 40e16e4a814f7fe851a0c486fb9e8c722e957830 and yyjson, under MIT.
Original copyright and permission texts are preserved in the native packs
and Исходники/AIHub/Licenses/texts/music-*.txt; catalog ID runtime.music-yue2.
The CUDA 12.8 SM89 pack additionally uses NVIDIA cudart 12.8.90 and
cuBLAS 12.8.4.1. Catalog ID runtime.music-yue2-cuda128; complete NVIDIA archive
license and third-party notices: Исходники/AIHub/Licenses/texts/music-CUDA-EULA.txt.
Official archive manifest: https://developer.download.nvidia.com/compute/cuda/redist/redistrib_12.8.1.json.
Build-only nvcc 12.8.93/CCCL 12.8.90 are kept outside the application.
These are local developer packs, tested on RTX 4090; no installer/update
containing these packs has been published. Model license entries are unchanged.

## Music genre vocabulary — 2026-10-05

Content/MusicGenres.json contains 2208 genre names and MBIDs extracted from
https://musicbrainz.org/genres on 2026-10-05. MusicBrainz identifies genre names
and MBIDs as CC0 core data: https://musicbrainz.org/doc/MusicBrainz_Database.
No user tags, artist/recording genre associations or other supplementary data
are included. Snapshot SHA-256:
A9B4518FC449F876CF4F4142EB10199227DFBD5509C5EF8AB2CA0148BE994308.
Catalog ID: builtin.music-genres. Full CC0 legal text:
Исходники/AIHub/Licenses/texts/music-genres-CC0.txt (official Creative Commons text).
Russian search hints and instrument recommendations are original project data.

# THIRD PARTY NOTICES — AI_HUB

## Music editor tokenizer — 2026-10-05

The C# NFC/pre-tokenization/byte-BPE implementation follows the YuE2 tokenizer
protocol in yue2.cpp, commit `11c1ecb084329200e22fcb286e252b847442ea5c` (MIT).
Original copyright and permission text: `Исходники/AIHub/Licenses/texts/music-tokenizer-MIT.txt`.
Catalog ID: `builtin.music-tokenizer`. This component does not include a native
inference engine or any model weights. Token tables are read from the separately
installed GGUF, subject to its existing model license entry.

## Image processing utility — 2026-10-04

ImageMagick 7.1.2-30 is also bundled for the image utility. Its existing
`native.imagemagick` catalog entry and original LICENSE/NOTICE cover this use.
The executable, configuration and color profiles are packaged together.

Optional AI components are downloaded from upstream only after user action:

- Real-ESRGAN ncnn Vulkan 20220424: BSD-3-Clause model project, MIT implementation.
- Real-CUGAN ncnn Vulkan 20220728: MIT model project and implementation.
- SwinIR v0.0 DF2K classical SR: Apache-2.0; Swin-Transformer and KAIR MIT notices.
  Network source at commit 6545850fbf8df298df73d81f3e8cba638787c8bd is bundled;
  modified inference helper imports are marked in that file. Weights are optional.
- Pillow 11.3.0: HPND, private optional wheel with its bundled notices.
- Native bundles include ncnn BSD-3-Clause, libwebp, stb and dirent notices.

Pinned URLs, sizes and SHA-256: `Исходники/AIHub/Tools/image-utility-ai-manifest.json`.
Full original texts: `Исходники/AIHub/Licenses/texts/image-utility-*`.
Catalog entries describe the distribution method and licensing evidence, including
the absence of a separate upstream license declaration for every weight file.
Microsoft runtime DLLs remain in the original optional upstream archives; these
AI archives and model weights are not redistributed inside the application.

## Optional image generation — 2026-10-03

The application can download a separate stable-diffusion.cpp/CUDA runtime and
model weights at the user's request. No image-model weights are bundled here.
Pinned artifact revisions, sizes, SHA-256 and upstream URLs are recorded in
`Исходники/AIHub/Tools/image-generation-manifest.json`. Component terms are registered
in `Исходники/AIHub/Licenses/image-generation-entries.json` and the shared catalog.

- stable-diffusion.cpp commit 3f8527a46c54ecf4cb4ed6003da8e8982283c73c: MIT;
  vendored GGML commit 89c4413f5da6fb20cc796f16033d37f129be81fd: MIT.
  Original notices: `generation-sd-LICENSE.txt`, `generation-ggml-LICENSE.txt`.
- NVIDIA CUDA runtime 12.8.1: NVIDIA CUDA EULA, `generation-CUDA-EULA.txt`.
- Z-Image Turbo: Apache-2.0; auxiliary encoder/VAE terms are individually
  recorded in the component catalog.
- Krea 2 Turbo: Krea Community License v1 and AUP, including the enterprise revenue
  condition and output-review requirements. Export/copy/open follows manual review.


Original texts are under `Исходники/AIHub/Licenses/texts/generation-*`.
The technical/license research and remaining public-distribution checks are recorded in
`Документы_проекта/Исследования/2026-10-03_Простая_генерация_реализация_и_проверки.md`.

## Screen capture video — minimal bundle, 2026-10-03

NAudio.Core / NAudio.Wasapi 2.3.0: Copyright (c) 2020 Mark Heath, MIT.
Original license: `Исходники/AIHub/Licenses/texts/NAudio-2.3.0-LICENSE.txt`.
Source: https://github.com/naudio/NAudio/tree/v2.3.0.

FFmpeg 8.1.3-lopata-minimal-1: separate executable and replaceable DLLs,
LGPL-2.1-or-later. Minimal source-built codec set, no GPL/nonfree mode.
Original LGPL/GPL2, libvpx/Opus/OpenH264/NVIDIA-header and compiler runtime
notices accompany the binary. Exact upstream archives, complete recipe,
configuration and tool versions are supplied on the same GitHub release.
See `Документы_проекта/Лицензии/Захват_видео_FFmpeg_NOTICE.md` and
`Инструменты/CaptureRuntime/sources.json` for the pinned composition.
The broad BtbN development bundle is no longer shipped.
The bundled `capture.ffmpeg` is distinct from optional downloaded `runtime.ffmpeg`.
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
## Financial currency names (0.3.6-dev)

The offline currency-name catalog derives Russian and English names from Unicode
CLDR JSON (https://github.com/unicode-org/cldr-json), retrieved 2026-10-01.
Copyright © 2015-2024 Unicode, Inc. Distributed under Unicode License V3;
the original notice is retained in Licenses/texts/data.cldr_UNICODE_LICENSE.txt.
Five missing accounting-unit names are translated separately from the SIX ISO
4217 List One, published 2026-09-17. Codes follow that list, excluding test code
XTS and no-currency code XXX. No exchange rates or conversion data are included.

# Literary source indexing (0.1.88-dev)

Giga-Embeddings-instruct-480M-0826 is separately downloaded from ai-sage, revision 0c94f705aa35719324fb46f7e75b0a5c275da6e4. Its card declares MIT; no separate LICENSE/copyright notice is present in that snapshot. Runeweaver GGUF is separately downloaded from limloop, whose card declares Apache-2.0; complete merge provenance has not been independently audited. Acknowledgement does not replace missing terms or grant additional rights.

The isolated indexing runtime downloads Python 3.12.10, pip 25.3, PyTorch 2.10.0 with CUDA 12.8, and Transformers 5.3.0. Original primary licenses are retained under `Исходники/AIHub/Licenses/texts`; installed packages retain their own notices. NVIDIA terms: https://docs.nvidia.com/cuda/archive/12.8.0/eula/index.html. Transitive versions are recorded in installation reports, with their respective licenses. Models and this runtime are not bundled by this change. Full source URLs and review notes are in the component license catalog.

## Screenshot utility (0.3.11-dev)

SkiaSharp and SkiaSharp.NativeAssets.Win32 3.119.4 are bundled for CPU WebP
encoding and ordinary image scaling. MIT license and original copyright
notices are retained in `Исходники/AIHub/Licenses/texts/SkiaSharp_3.119.4_LICENSE.txt`
and `SkiaSharp.NativeAssets.Win32_3.119.4_LICENSE.txt`. The complete native
third-party notice file is `SkiaSharp.NativeAssets.Win32_3.119.4_THIRD-PARTY-NOTICES.txt`
in the same directory. These include upstream Skia/libwebp and other applicable
notices; the application license catalog and installer texts contain them.
Package source: https://github.com/mono/SkiaSharp (version 3.119.4).

The capture module calls Windows-provided WGC and Direct3D11 interfaces.
Microsoft.Windows.SDK.NET and WinRT.Runtime assemblies are not bundled by this
feature. PNG/JPEG use Windows/WPF codecs. No GIF/video encoder is bundled yet.


## YuE2 Studio headless runtime (0.4.22-dev)

Pinned YuE2 Studio v3.4.0 source 9125be3cf9ba720ac439a81cd09ff8bcc2a00368
and native yue2.cpp 1141479c725b803a3649c900fd3e6fcb27f6f595.
The LOPATA patch supplies explicit managed model paths; it does not replace generation logic.
The runtime retains Studio-source.zip, Cargo.lock, the patch, CargoSources, CargoLicenses,
original LAME LGPL text, native MIT notices, NVIDIA and Microsoft terms.
Registry sources include MPL-covered files and LAME 3.100 sources for rebuilding/relinking.
See Инструменты/MusicStudio/README.md and the central music-studio-entries.json catalog.
Optional ASR models, soundfonts, React/Tauri UI and training resources are not installed.
The separately downloaded decoder companion v9 declares CC BY-NC 4.0; its pinned model
card and the full standard legal text are retained. No extra commercial rights are granted.

## ACE-Step 1.5 XL Turbo integration (2026-10-08)

Unmodified official source, MIT, commit ca1e85fe9430179831e6bc6be790c332190a3866,
is retained in Runtime/MusicAce and verified by a SHA256 manifest. LOPATA's
process adapter calls the upstream API; it does not implement model inference.
Model/companions and 88 pinned Python wheels are installed separately through
the managed downloader. Original wheel copyright/LICENSE/NOTICE extracts are
retained in Licenses/texts/ace-*.txt and covered by music-ace-entries.json.
Qwen companion components additionally use Apache-2.0. PyTorch hardware packs
keep their existing separate terms. No vLLM, flash-attn or training dependencies
are supplied. See Документы_проекта/ACE_XL_Turbo_4B_паспорт.md for revisions,
sources, integration boundaries and what has actually been verified.
# DiffRhythm 2 integration (2026-10-09)

Pinned ASLP-lab author Space source 0563fcec4bdf42ca33f6e76ebe9949429d07bf00
is retained in Runtime/MusicDiffRhythm/source.zip with per-file hashes. Author
sampling and BigVGAN are unchanged. LOPATA provides offline local loaders,
owned process control, deferred eSpeak voice initialization for Windows,
and a separately disclosed experimental Russian frontend authorized by the user.
Original EN/ZH processing is retained. Russian phoneme approximations are recorded
in execution metadata; unsupported phones fail explicitly.
DiffRhythm is Apache-2.0; BigVGAN/HiFiGAN and MuQ code use MIT.
MuQ/MuLan weights use **CC BY-NC 4.0**, restricting this supplied bundle to
noncommercial use under those terms; no additional rights are granted.
Original notices for 97 pinned Windows wheels and MuQ/Jieba source are retained
in Licenses/texts/diff2-*.txt and music-diffrhythm-entries.json. LangSegment from
the Space states py3langid/BSD provenance; its unavailable original fork was not
independently verified. eSpeak and shared Torch packs keep their separate terms.
See Документы_проекта/DiffRhythm2_паспорт.md and Инструменты/MusicDiffRhythm/README.md
for precise revisions, reproduction and the limits of runtime verification.
