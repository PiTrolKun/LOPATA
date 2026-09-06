# REESTR — зависимости, модели, backends и инструменты

## 2026-09-06 — данные кастомных промптов, 0.1.45-dev

Новых внешних зависимостей нет. Общая локальная библиотека PromptPairStore:
AppDataPaths.BaseDirectory/Prompts/pairs.json, schemaVersion=1, упорядоченный
список items (ID, ContractId, Name, AnalysisPrompt, ComposePrompt).
Первый адаптер — image-description/omni-two-pass/v1; хранение и редактор общие.
Запись через временный файл с атомарной заменой и проверкой конкурирующей записи.
Работы сохраняют необязательные Settings.PromptMode и Settings.CustomPrompts
(снимок пары). Отсутствующие поля означают стандартный режим; массовой миграции нет.
Скрытая беседа и существующий формат результатов сохранены. Старые сборки не
понимают кастомный режим: не использовать их для перезаписи таких работ.
Файлы пользовательских вариантов в репозиторий и установщик не включаются.

## Следующая работа — лицензии, 2026-09-05

Подготовлено [ТЗ единого механизма](../ТЗ/Архив/2026-09-05_единый_механизм_лицензий_LOPATA.md). Механизм 0.1.44-dev принят пользователем; установщик и сохранение согласий проверены. Полнота лицензионных материалов нативной поставки остаётся открытой.
[Предварительная проверка и снимки состава](Лицензии/2026-09-05_подготовка.md) содержат подтверждённые сведения и конкретные остатки. Для лицензионной работы учитывать найденные там расхождения со старыми строками реестра.


Этот файл фиксирует всё, что может повлиять на лицензии, поставку, безопасность и воспроизводимость проекта.

Реальные зависимости, модели и backends перечислены ниже.

### Уточнение Heavy runtime, 0.1.26-dev (2026-09-04)

Новых внешних зависимостей и загрузок нет. В существующем изолированном
runtime добавлен собственный adapter `Исходники/AIHub/Tools/omni_attention.py`
(лицензия кода проекта), выбираемый только Heavy через Transformers
AttentionInterface/AttentionMaskInterface. Системный torch и site-packages
не редактируются. На проверенном Windows-стеке grouped SDPA MATH заменяется
эквивалентным repeat-KV с EFFICIENT_ATTENTION; имеется очистка unused cache.
Qwen2.5-Omni-3B BF16, CUDA-only, Thinker-only и ограничения лицензии модели
сохраняются. ИИ+ не возвращается в сценарий; CPU-Kokoro не изменялась.
Новые `OmniResponses` — локальные диагностические артефакты сессии,
содержащие пользовательский текст/паспорт файла; это не runtime-зависимость
и не материалы для автоматической публикации. Перед публикацией сырых
пользовательских логов требуется проверить их состав.

## Runtime-зависимости

| Название | Назначение | Версия | Лицензия | Источник | Поставка вместе с программой | Ограничения |
|---|---|---|---|---|---|---|
| Microsoft .NET Runtime | Запуск будущего .NET-приложения | 10.0.8 | MIT | Установлено вместе с `Microsoft.DotNet.SDK.10` через winget / Microsoft | Нет, системная предпосылка | Требуется Windows x64 |
| Microsoft Windows Desktop Runtime | Запуск будущего WPF-приложения | 10.0.8 | MIT | Установлено вместе с `Microsoft.DotNet.SDK.10` через winget / Microsoft | Нет, системная предпосылка | Требуется Windows x64; нужен для WPF |
| eSpeak NG | Локальный синтетический голос ядра и события слов для синхронного раскрытия текста | 1.52.0 | GPL-3.0-or-later | Официальный release `https://github.com/espeak-ng/espeak-ng/releases/tag/1.52.0`; MSI `https://github.com/espeak-ng/espeak-ng/releases/download/1.52.0/espeak-ng.msi` | Да, DLL и `espeak-ng-data` копируются в `VoiceRuntime/eSpeakNG` при наличии подготовленного runtime | Только голос ядра; MSI SHA-256 `7F673C709EA5DD579D3B5EBB98688CC575328A6AB7438D2BC405B88CEDAEAFB9`, DLL SHA-256 `E737572DF0A35A32B7BD444537C661C1C916B13B0B91351030C7F1D531307BEB`; runtime готовится скриптом `Инструменты/setup-espeak-ng-runtime.ps1`; требуется поставка лицензии и доступность исходника |
| RHVoice через Windows SAPI | Альтернативный голос ядра и временный ролевой голос исполнителя «Режима неопределённости» | Engine 1.18.1; Aleksandr 4.2.2; Slt 4.1.2; Elena 4.3; Bdl 4.1 | RHVoice engine: LGPL-2.1-or-later для C API, репозиторий также помечен GPL-2.0; Slt/BDL основаны на CMU voices; лицензии отдельных русских voice-пакетов требуют отдельной проверки | Официальные releases `RHVoice/aleksandr-rus`, `RHVoice/slt-eng`, `RHVoice/elena-rus`, `RHVoice/bdl-eng`; установка скриптом `Инструменты/setup-rhvoice.ps1` | Нет, используется как отдельно установленный системный SAPI-компонент | Ядро: Aleksandr/Slt; исполнитель текущего сценария: Elena/Bdl. Установщики не включать в publish до юридической проверки. SHA-256 Elena `23E130...B643`, Bdl `F9C51B...4847` закреплены полностью в setup-скрипте |
| Python Transformers runtime | Временный локальный runtime для `BAAI/bge-reranker-v2-m3` и offline smoke-check `Florence-2-large-ft` | Python 3.12 venv в `Runtime/Python/reranker/.venv`; `torch 2.12.0+cpu`, `torchvision 0.27.0+cpu`, `Pillow 12.3.0`, `transformers 4.41.2`, `tokenizers 0.19.1`, `huggingface-hub 0.36.2`, `timm 1.0.28`, `einops 0.8.2`, `safetensors 0.7.0` | Python PSF; PyTorch/Torchvision BSD-style; Pillow HPND; Transformers/Tokenizers/Safetensors Apache-2.0; timm Apache-2.0; einops MIT | Установлено локально через `pip` в runtime-папку проекта | Нет, runtime-папка не публикуется в GitHub | Временное dev-решение; Florence запускается только из закреплённой локальной папки при `HF_HUB_OFFLINE=1` и `TRANSFORMERS_OFFLINE=1`. Реальный offline smoke-test с загрузкой весов, обработкой изображения и генерацией ответа пройден 2026-08-24; перед релизом нужен управляемый runtime |
| Python Kokoro runtime | CPU-runtime нейросетевой озвучки короткой контрольной сводки сценария `Анализ изображений` | Python 3.12; `kokoro 0.9.4`, `misaki[en] 0.9.4`, `ruaccent 1.5.8.3`, `onnxruntime 1.29.0`; использует уже установленный CPU PyTorch | Kokoro/Misaki/RUAccent — Apache-2.0; ONNX Runtime — MIT; транзитивные пакеты фиксируются отдельно перед поставкой | PyPI; воспроизводимый dev-скрипт `Инструменты/setup-kokoro-runtime.ps1` | Пока нет: подготовлен только локальный dev-runtime | Запускается отдельным offline worker на CPU; не имеет доступа к Matrix-дождю. До пользовательской загрузки голосовых файлов реальный TTS smoke-test и замеры cold/warm start не выполняются; при любой ошибке доступна программная читалка |
| Python Qwen2.5-Omni Heavy runtime | Изолированный native Windows CUDA-worker для зрения, редактуры и Talker Тяжёлого режима | Python `3.12.10`; `torch 2.11.0+cu130`, `torchvision 0.26.0+cu130`, `torchaudio 2.11.0+cu130`; `transformers 5.16.1`; `accelerate 1.14.0`; `qwen-omni-utils 0.0.9`; `numpy 2.5.2`; `soundfile 0.14.0`; `audioread 3.1.0` | Python PSF; PyTorch/Torchvision/Torchaudio BSD-style; Transformers/Accelerate/qwen-omni-utils Apache-2.0; NumPy BSD-3-Clause; SoundFile BSD-3-Clause; audioread MIT | Official PyTorch cu130 index/PyPI; воспроизводимый скрипт `Инструменты/setup-qwen-omni-runtime.ps1` | Нет: runtime не входит в Git/установщик | Среда создана 2026-08-29. Inference принудительно offline. С 0.1.19 анализ использует официальный Thinker-only и PyTorch SDPA; внешний FlashAttention 2 выбирается автоматически, только если он уже совместимо установлен. Полный Omni загружается отдельно по явному запросу речи. Сквозной анализ и Talker подтверждены, производительность нового профиля ждёт пользовательской проверки |

## Developer tooling

| Название | Назначение | Версия | Лицензия | Источник | Обязательно для сборки | Ограничения |
|---|---|---|---|---|---|---|
| `check-cyrillic-integrity.ps1` | Внутренний scanner UTF-8 и поломанной кириллицы | 0.1 | GPL-3.0-or-later, как код проекта | `Инструменты/check-cyrillic-integrity.ps1` | Нет | Использует PowerShell и проверяет текстовые файлы проекта |
| `Запустить_AI_HUB.cmd` + `start-aihub.ps1` | Локальный запуск dev-сборки AI_HUB двойным кликом | 0.1 | GPL-3.0-or-later, как код проекта | Корень проекта | Нет | `.cmd` запускает PowerShell-скрипт, который выполняет `dotnet build` и стартует текущий Debug exe |
| Microsoft .NET SDK | Создание, restore и build будущего C# / WPF-проекта | 10.0.300 | MIT | `Microsoft.DotNet.SDK.10`, winget / Microsoft | Да | Установлен системно; WPF smoke-test на `net10.0-windows` прошёл |
| GitHub CLI | Авторизация и публикация проекта на GitHub | 2.93.0 | MIT | `GitHub.cli`, winget / GitHub | Нет | Установлен системно; текущая сессия вызывает `C:\Program Files\GitHub CLI\gh.exe` |
| MSTest.Sdk | Автоматические тесты JSON-контракта и состояния сценария | 4.2.3 | MIT | NuGet `MSTest.Sdk` | Да, только для test-проекта | Developer tooling; в runtime и установщик не включается |
| Изолированный Kimi-VL Transformers control | Диагностическое сравнение официального визуального процессора Kimi-VL с GGUF/mmproj/llama.cpp | Python venv; PyTorch `2.5.1+cu124`; torchvision `0.20.1+cu124`; Transformers `4.51.3`; bitsandbytes `0.50.1` | Python PSF; PyTorch/Torchvision BSD-style; Transformers Apache-2.0; bitsandbytes MIT; лицензии транзитивных пакетов наследуются отдельно | `Тесты/Kimi_official_runtime_20260825`; пакеты PyPI/PyTorch, веса Hugging Face `moonshotai/Kimi-VL-A3B-Thinking-2506` | Нет | Только developer diagnostic tooling; около 31 ГБ исходных весов и venv не входят в Git, runtime продукта или установщик. Контроль пройден 2026-08-25 с официальным процессором и NF4 4-bit загрузкой на RTX 4090 |
| Изолированный Kimi-VL chatllm.cpp control | Диагностическая основа штатного visual-runtime: сравнение с llama.cpp и официальным Transformers | `chatllm.cpp v24`, commit `f5f1d25365fb59447eb58994030c5acd492fcd53`; portable ImageMagick `7.1.2-30 Q16-HDRI x64` | chatllm.cpp MIT; ImageMagick License | `Тесты/Kimi_chatllm_cpp_20260825`; GitHub `foldl/chatllm.cpp`; официальный архив ImageMagick | Нет | Контрольная папка остаётся developer tooling. CPU-контроль дал правильные ответы `2/2`; продукт использует отдельную копию тех же проверенных runtime-файлов. Для v24 vision обязательно `--max_proj_length 1024`; GPU offload отложен |

## Внешние инструменты

| Название | Назначение | Версия | Лицензия | Источник | Автоустановка | Ограничения |
|---|---|---|---|---|---|---|
| Inno Setup | Создание тестового `.exe` установщика AI_HUB для Windows | 6.7.3 | Собственная лицензия Inno Setup, freeware; исходники доступны у JR Software | `JRSoftware.InnoSetup`, winget / JR Software | Установлен по подтверждению пользователя | Используется только как developer tooling; в поставку AI_HUB не встраивается |

## Backends

| Название | Назначение | Версия | Лицензия | Источник | Способ установки | Ограничения |
|---|---|---|---|---|---|---|
| llama.cpp | Локальный GGUF-backend: `llama-server.exe` как основной debug-runtime, `llama-cli.exe` как fallback | release `b9442`, Windows CUDA 12.4 x64 | MIT | GitHub `ggml-org/llama.cpp`, release `b9442` | Скачан локально в `Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64`; не публикуется в GitHub | Для Qwen3 server нужно запускать с `--reasoning off`, иначе OpenAI-compatible `message.content` может быть пустым; debug-окно запускает server на свободном loopback-порту; модели не получают доступ к файлам, интернету, shell и настройкам Windows |
| chatllm.cpp + private ImageMagick | Штатный локальный visual-runtime Kimi Среднего комплекта через loopback OpenAI-compatible API | chatllm.cpp `v24`, commit `f5f1d25365fb59447eb58994030c5acd492fcd53`; ImageMagick `7.1.2-30 Q16-HDRI x64` | chatllm.cpp MIT; ImageMagick License | GitHub `foldl/chatllm.cpp` release v24; официальный release ImageMagick | `Runtime/Backends/chatllm.cpp/v24/win-x64`; включается в будущий установщик, но не публикуется в Git | Работает в проверенном CPU-профиле до 24 потоков; ImageMagick доступен только дочернему процессу через локальный PATH/MAGICK_HOME; server привязан к случайному loopback-порту и останавливается после запроса. Архив chatllm SHA-256 `F92F48325E4B1351FBED6BD434E07F656B67CB535B36A14E608EF88C773DAF91`, архив ImageMagick SHA-256 `D98471F5EC9D87E222C69C8C28C98FE6665DAB76CD3EF752C5E4DE785BE553BE` |

В версии `0.0.53-dev` тот же backend поддерживает отдельный долгоживущий runtime исполнителя и OpenAI-compatible SSE streaming. Ядро выгружается перед запуском тяжелого исполнителя; Matrix-визуализатор получает только `delta.content`, но не backend logs и системный prompt. Executor manifest получает `installed/runnable` только после фактического запуска и health-check этого backend; несовместимая архитектура фиксируется как `runtime_incompatible`. Если полный GPU-offload не запускается, runtime повторяет проверку через CPU/RAM для моделей, рассчитанных на гибридную нагрузку.

В `0.0.86-dev` этот backend также назначен закреплённому Kimi-комплекту. AI
HUB не считает наличие GGUF и projector доказательством совместимости: отдельная
проверка запускает `llama-server` на свободном loopback-порту, передаёт
встроенное тестовое изображение и требует непустой ответ. Код проверки
реализован, но реальный тяжёлый запуск Kimi в ходе разработки не выполнялся по
просьбе пользователя; совместимость текущего локального экземпляра должна быть
подтверждена пользовательским тестом.

## Внешние сетевые сервисы

| Название | Назначение | Версия/API | Лицензия/условия | Источник | Поставка вместе с программой | Ограничения |
|---|---|---|---|---|---|---|
| ipwho.is / ipwhois.io | Примерное автоматическое определение местоположения пользователя по IP для скрытого контекста ядра | HTTPS JSON endpoint `https://ipwho.is/?lang=ru` | Внешний сервис; free endpoint без API-ключа, указан fair-use limit 1 запрос/сек и 60 запросов/60 сек; free endpoint предназначен для non-commercial use | Официальная документация `https://ipwhois.io/documentation` | Нет, это внешний запрос; IP не сохраняется в профиле AI HUB | Использовать редко и best-effort: при отсутствии сохранённого auto-местоположения; при недоступности сервиса программа работает без местоположения; перед публичным/коммерческим релизом пересмотреть условия или заменить провайдера |
| DuckDuckGo Lite | Первый dev-провайдер web-поиска для проверки Tool Gateway без API-ключа | HTML endpoint `https://lite.duckduckgo.com/lite/?q=...` | Внешний web-сервис; условия DuckDuckGo нужно пересмотреть перед публичным релизом | `https://lite.duckduckgo.com/lite/` | Нет, это внешний HTTPS-запрос | Используется как временный dev-провайдер; HTML-парсинг хрупкий, в будущем заменить на официальный API-провайдер или локальный SearXNG |
| Bing HTML Search | Резервный dev-провайдер web-поиска, если DuckDuckGo вернул пустую выдачу или HTML-парсер не сработал | HTML endpoint `https://www.bing.com/search?q=...` | Внешний web-сервис; условия Microsoft/Bing нужно пересмотреть перед публичным релизом | `https://www.bing.com/search` | Нет, это внешний HTTPS-запрос | Временный fallback для разработки; HTML-парсинг хрупкий, не считать финальным поисковым API |
| Hugging Face Hub API | Структурированный подбор моделей, файлов и построение проверяемого накопительного локального каталога вместо HTML-поиска | REST endpoints `https://huggingface.co/api/models` и `https://huggingface.co/api/models/{repoId}` | Внешний web-сервис Hugging Face; условия Hub/API нужно соблюдать отдельно от лицензий конкретных моделей | `https://huggingface.co/docs/hub/api` | Нет, это внешний HTTPS-запрос | Посевной справочник находится в `Каталоги/huggingface-catalog-seed.json`; синхронизатор сохраняет raw JSON/Model Card, URL источника, SHA ревизии, SHA-256 и JSONL-журнал изменений; радар автоматически допускает только публичные модели с подтверждённым размером `>8B` и отбрасывает чистые quantized-упаковки; metadata считается данными Hub, Model Card — утверждением автора; модели-кандидаты не скачиваются и не поставляются с программой |

Выбранная ядром модель-исполнитель разрешается через Hub API в самостоятельный GGUF-файл. До загрузки программа показывает repository, файл, квант, размер, лицензию и оценку ПК; загрузка начинается только после подтверждения, использует `.part`, проверку размера/SHA-256, чтение `general.architecture` и локальный `executor-model.json`. MTP/draft/mmproj/imatrix/adapter/LoRA/projector и split-файлы не считаются основными весами. Конкретные модели-исполнители не входят в поставку AI HUB и наследуют собственные лицензии их авторов.

## AI-модели

| Название | Назначение | Версия/квант | Лицензия | Источник | Размер | Ограничения |
|---|---|---|---|---|---|---|
| Qwen2.5-Omni-3B | Последний экспериментальный checkpoint Тяжёлого режима: Thinker-only для зрения/текста и взаимоисключающий полный профиль для встроенной речи | BF16 Safetensors, official revision `f75b40e3da2003cdd6e1829b1f420ca70797c34e`; полные Thinker и Talker | Qwen Research License Agreement; только некоммерческое исследовательское и оценочное использование | Hugging Face `Qwen/Qwen2.5-Omni-3B` | 16 обязательных файлов, 3 shards; `11989065629` байт; для каждого закреплены размер и SHA-256 | Не входит в Git и установщик; AI HUB скачивает только после явного подтверждения. Для Heavy обязано полное размещение на CUDA GPU; CPU/disk offload приводит к отказу прогрева. При неудаче пользователь решил вернуться к архитектуре Среднего режима |
| Qwen3 8B GGUF | Основное ИИ-ядро AI HUB: быстрый диспетчер сценариев, будущей RAG-памяти и выбора инструментов | Qwen3 8B / GGUF / Q4_K_M / файл `Qwen3-8B-Q4_K_M.gguf` | Apache-2.0 | Hugging Face `Qwen/Qwen3-8B-GGUF`, commit `7c41481f57cb95916b40956ab2f0b139b296d974` | `5027783488` байт, около 5.03 ГБ | Не поставляется внутри установщика; скачивается отдельно пользователем через AI HUB в выбранную папку моделей; после загрузки проверяется SHA-256 `d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785` |
| Qwen3 0.6B GGUF | Тестовый web-download artifact для проверки, что ядро может попросить инструмент скачать маленькую модель | Qwen3 0.6B / GGUF / Q4_K_M / файл `Qwen3-0.6B-Q4_K_M.gguf` | Apache-2.0 | Hugging Face `jc-builds/Qwen3-0.6B-Q4_K_M-GGUF`, repo sha `c3111ad3faedb08c4abf76070a589e256258c62d` | `396705472` байт, около 397 МБ; SHA-256 `ac2d97712095a558e31573f62f466a3f9d93990898b0ec79d7c974c1780d524a` | Не является основным ядром и не включается в установщик; скачана в пользовательскую папку результатов `AI_HUB\Tools\Web\Downloads` для теста инструментов, Git ignored |
| SmolVLM2 2.2B Instruct GGUF | Внутренний необязательный модуль смыслового описания изображений | GGUF Q4_K_M + мультимодальный проектор Q8_0, ревизия `1bc3c9f74ceafd4c8d4411cc9cf188bba3798f91` | Apache-2.0 | Hugging Face `ggml-org/SmolVLM2-2.2B-Instruct-GGUF` | Модель `1112602656` байт, SHA-256 `0cf76814555b8665149075b74ab6b5c1d428ea1d3d01c1918c12012e8d7c9f58`; проектор `592523200` байт, SHA-256 `ae07ea1facd07dd3230c4483b63e8cda96c6944ad2481f33d531f79e892dd024` | Не входит в установщик и Git; скачивается внутри AI HUB только после подтверждения. Запускается скрытым `llama-server` через адаптер `adapter.image.semantic` и инструмент `session_image_describe`; не заменяет OCR и редактирование изображения |
| Kimi-VL-A3B-Thinking-2506 GGUF | Прежний visual-артефакт, сохранённый только для отката и сравнительных тестов | GGUF Q4_K_M + projector Q8_0; revision `e7dcd093335f922a057772febc7ab27eda985b40` | MIT у исходной модели `moonshotai/Kimi-VL-A3B-Thinking-2506`; GGUF-репозиторий не публикует отдельное поле лицензии | Hugging Face `ggml-org/Kimi-VL-A3B-Thinking-2506-GGUF`; upstream `moonshotai/Kimi-VL-A3B-Thinking-2506` | Модель `10540747680` байт, SHA-256 `72253d82d21c546587139dfd12597d491c25a13c6540d2ce18ca1581967338c5`; projector `618098624` байт, SHA-256 `5af5e5fc0ad5e2348f5227ddfa97e9241d453020c149dbe0e920ed603189ca15` | Больше не входит в Средний комплект. Существующие файлы и карточка не удаляются. Реальные тесты 2026-08-25 выявили тяжёлое смысловое искажение зрения через `llama.cpp b9442`; не использовать как достоверный визуальный runtime |
| Kimi-VL-A3B-Thinking-2506 GGMM Q4_1 | Штатный визуальный аналитик Среднего комплекта через `chatllm.cpp v24` | GGMM/GGML file version 1, Q4_1 | MIT у исходной модели; лицензия конкретной конвертации требует повторной юридической проверки до публичного релиза | ModelScope `judd2024/chatllm_quantized_kimi-vl`, файл `kimi-vl-thinking-2506-q4_1.bin` | `10447149104` байт; SHA-256 `33700EA2F4C8467FBCC4EFA060C763E035A8E73003424634125B5A3C64CE02C9` | Не входит в Git/установщик; скачивается AI HUB после подтверждения пользователя. CPU-контроль дал правильные ответы на портрете и сложной готической сцене. `native_resolution` не включается, `max_proj_length=1024`, искусственного лимита ответа нет |
| Microsoft Florence-2-large-ft | Общая профильная модель Среднего и будущего Тяжёлого комплектов анализа изображений | Transformers/Safetensors, 0.77B, revision `4a12a2b54b7016a48a22037fbd62da90cd566f2a` | MIT | Hugging Face `microsoft/Florence-2-large-ft` | `model.safetensors` `1540980506` байт, SHA-256 `8b4e610c952eef90a836c56cda0f398a672a3a6ca7b4d96b0e09a86dee42e2c3`; вместе с закреплёнными config/tokenizer/processor/modeling-файлами около 1.543 ГБ | Не входит в Git/установщик; AI HUB не скачивает дублирующий `pytorch_model.bin`. В манифесте закреплены десять реально используемых файлов и SHA-256 каждого. Custom code запускается только локально/offline; `flash_attn` остаётся необязательным и не устанавливается в Windows CPU-runtime. Реальный smoke-check пройден 2026-08-24. Со страницы Среднего комплекта модель не удаляется |
| hexgrad Kokoro-82M + af_heart | Английский CPU-голос для полной контрольной сводки правой панели `Анализа изображений` | 82M, PyTorch, commit `f3ff3571791e39611d31c381e3a41a3af07b4987` | Apache-2.0 | Hugging Face `hexgrad/Kokoro-82M` | Карточка AI HUB закрепляет `config.json`, `kokoro-v1_0.pth` и `voices/af_heart.pt`, около 328 МБ; для каждого файла заданы размер и SHA-256 | Не входит в Git/установщик; загружается только по явной кнопке при английском языке интерфейса. Работает offline на CPU/RAM и остаётся прогретой до выхода из сценария при достаточной памяти |
| zaakirio Kokoro-RU + Sveta + RUAccent | Русский CPU-голос для полной контрольной сводки правой панели `Анализа изображений` | Kokoro-RU 81.81M, commit `27d078fe1c0cab919613a64e906919214385f21d`; RUAccent snapshot `b78ae5ea1e62beaf138bed1865cd8c3b0b5ca855` | Веса Kokoro-RU — OpenRAIL; код/RUAccent — Apache-2.0; закреплённые данные eSpeak NG — GPL-3.0-or-later | Hugging Face `zaakirio/kokoro-ru` и `ruaccent/accentuator` | Одна русская карточка содержит голос, модель, G2P, минимальные данные eSpeak и полный offline-набор RUAccent; 54 файла с точными размерами и SHA-256 | Не входит в Git/установщик; загружается только по явной кнопке при русском языке интерфейса. Весы не распространять внутри установщика до отдельной юридической проверки OpenRAIL. Worker принудительно использует локальные RUAccent/eSpeak данные и не делает скрытых сетевых догрузок |
| BAAI bge-reranker-v2-m3 | Вспомогательная модель интернет-инструмента: reranker для выбора более подходящих web-результатов | `BAAI/bge-reranker-v2-m3`, safetensors, commit `953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e` | Apache-2.0 | Hugging Face `BAAI/bge-reranker-v2-m3` | `2293242108` байт, около 2.29 ГБ | Не поставляется внутри установщика; автоматически скачивается AI HUB после основного ядра в выбранную папку моделей `Tools/Reranker/BAAI-bge-reranker-v2-m3`; используется `web_search` через локальный Python/Transformers runtime, при недоступности включается lexical fallback |

## Встроенные библиотеки

| Название | Назначение | Версия | Лицензия | Источник | Ограничения |
|---|---|---|---|---|---|
| System.Speech | Доступ к установленным Windows SAPI-голосам RHVoice и событиям произнесённых слов | 10.0.9 | MIT | NuGet `System.Speech`, Microsoft | Windows-only; библиотека включается в publish, сами RHVoice-голоса остаются внешней установкой |
| DocumentFormat.OpenXml | Формирование итогового результата executor-сессии в формате DOCX | 3.5.1 | MIT | NuGet `DocumentFormat.OpenXml`, Microsoft | Runtime-зависимость включается в publish; создаёт файл только по явной команде и выбранному пользователем пути |
| ClosedXML | Чтение и редактирование XLSX/XLSM, внутренний табличный просмотр | 0.105.0 | MIT | NuGet `ClosedXML` | Включается в publish |
| PdfPig | Извлечение текста и структуры PDF для обработки и read-only просмотра | 0.1.15 | Apache-2.0 | NuGet `PdfPig` | Включается в publish; не исполняет PDF-скрипты |
| SharpCompress | Чтение и безопасная распаковка ZIP/7z/RAR/TAR/GZip | 1.0.0 | MIT | NuGet `SharpCompress` | Включается в publish; распаковка ограничена числом записей, размером и целевым каталогом |
| CsvHelper | Чтение CSV/TSV и внутренний табличный просмотр | 33.1.0 | Apache-2.0 или MS-PL | NuGet `CsvHelper` | Включается в publish |
| AngleSharp | Локальный разбор HTML/SVG без браузерной навигации | 1.5.2 | MIT | NuGet `AngleSharp` | Включается в publish; внешний JavaScript не запускается |
| Markdig | Разбор Markdown и безопасное текстовое представление | 1.3.2 | BSD-2-Clause | NuGet `Markdig` | Включается в publish |
| YamlDotNet | Чтение и нормализация YAML | 18.1.0 | MIT | NuGet `YamlDotNet` | Включается в publish |
| MimeKit | Чтение EML/MIME и списка вложений | 4.17.0 | MIT | NuGet `MimeKit` | Включается в publish; вложения не запускаются |
| Microsoft.Data.Sqlite | Read-only просмотр структуры локальных SQLite-файлов | 10.0.10 | MIT | NuGet `Microsoft.Data.Sqlite` | Включается в publish; соединение открывается в режиме ReadOnly |
| SQLitePCLRaw.bundle_e_sqlite3 | Нативный SQLite runtime для Microsoft.Data.Sqlite | 2.1.12 | Apache-2.0, SQLite public domain для нативного движка | NuGet `SQLitePCLRaw.bundle_e_sqlite3` | Явно закреплён на 2.1.12 вместо транзитивной 2.1.11 с опубликованным advisory |

## Загружаемые компоненты обработки и просмотра

Каталог версии `0.0.66-dev` содержит закреплённые источники для Eclipse Temurin
JRE 21, Apache Tika 3.3.2, ImageMagick 7.1.2-27, Tesseract 5.4.0 и языковых
данных, FFmpeg 8.1 LGPL, LibreOffice 26.2.4, whisper.cpp 1.9.1 и Whisper small.
Они не входят в обычный publish и скачиваются только после явного подтверждения
пользователя. Системные установщики требуют отдельного подтверждения запуска.

Отдельный программный каталог просмотрщиков содержит WebView2 Runtime, PDF.js,
EPUB.js, LibVLC Windows, OpenSeadragon, Babylon.js и AvalonEdit. Просмотрщики
никогда не попадают в prompt, capability inventory или план выбора LLM.
Лицензии: WebView2 — Microsoft Software License Terms; PDF.js и Babylon.js —
Apache-2.0; EPUB.js — BSD-2-Clause; LibVLC — LGPL-2.1; OpenSeadragon —
BSD-3-Clause; AvalonEdit — MIT. Эти пакеты также не входят в установщик без
отдельного решения о поставке.

## Правило

### Atlas Scout — внешний developer tooling (2026-09-04)

- Runtime `1.0.0-preview.29`, Free, ZaguanLabs; источник <https://atlasscout.dev/docs>.
- Проприетарная лицензия runtime и комплектный EULA; не включён в AI HUB,
  его установщик или Git. Поставка с продуктом не разрешалась и не планируется.
- Назначение: локальная структурная навигация Codex, шесть бесплатных MCP tools.
  Python — полная поддержка, C# — частичная. Pro/trial не активированы.
- Проверки и откат: `Диагностика/2026-09-04_Atlas_Scout_Free_настройка.md`.

Перед добавлением любой зависимости нужно обновить этот файл и, если требуется, `THIRD_PARTY_NOTICES.md`.
