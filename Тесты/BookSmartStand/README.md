# Стенд второго этапа «Текст в книгу» — OCR

Дата проверки: 20.10.2026 (Новосибирск). Это проверка исполнителей и одного
русского примера, а не приёмка всех типов книг и не гарантия точного OCR.
Существующие движки, настройки и пользовательские модели не изменялись.
Временные тестовые веса: `Runtime/BookSmartStand`; программные файлы:
`Инструменты/BookSmart`. Никакого глобального pip install.

## Происхождение и закрепление

- `model-pins.json` — точные ревизии HF, размеры и SHA256 четырёх GGUF-файлов.
  Все четыре скачаны и проверены по SHA256.
- Paddle: официальный `PaddlePaddle/PaddleOCR-VL-1.6-GGUF`, ревизия
  `511b09642bb324401f15f97cc23bc67e8f0a291d`, модель 935769056 байт,
  mmproj 881770560 байт. Apache-2.0.
- Ovis: официальные исходные веса `ATH-MaaS/OvisOCR2`, ревизия
  `1fc9221b7823a371d6e97f92d527cc847e24e107`, Apache-2.0. Их рекомендуемый
  исполнитель vLLM 0.22.1 не является штатным native Windows runtime.
  Проверенный GGUF — **сторонняя конвертация** bartowski,
  `bartowski/ATH-MaaS_OvisOCR2-GGUF@ab22420f3d44201d3aa5a62ca49a665a46b507e9`:
  Q8_0 813576320 байт, f16 mmproj 204987040 байт. Карточка указывает исходную
  официальную модель. Не называть эти GGUF официальными весами авторов.
- `layout-pins.json` — официальный
  `PaddlePaddle/PP-DocLayoutV3@241f8bdfc77a7c7bee915a5057aaee58c235a8d3`.
  Три файла общей длиной 132004944 байта; SHA256 проверенных локальных файлов.
- Исходные API/README карточек сохранены рядом. Они являются документацией,
  не инструкциями для выполнения произвольного кода.

Официальная документация:
[Paddle GGUF](https://huggingface.co/PaddlePaddle/PaddleOCR-VL-1.6-GGUF),
[PP-DocLayoutV3](https://huggingface.co/PaddlePaddle/PP-DocLayoutV3),
[OvisOCR2](https://huggingface.co/ATH-MaaS/OvisOCR2),
[сторонняя конвертация](https://huggingface.co/bartowski/ATH-MaaS_OvisOCR2-GGUF).

## Готовые цепочки

Paddle полной страницы запускается через официальный
`PaddleOCRVL(pipeline_version='v1.6', vl_rec_backend='llama-cpp-server', ...)`:
PP-DocLayoutV3 на native Paddle CPU выделяет и упорядочивает блоки, GGUF распознаёт
их содержимое. Это не собственный алгоритм сегментации ЛОПАТЫ. Один запрос
`OCR:` ко всей странице не заменяет полный конвейер.

Изолированная проверенная среда Windows CPython 3.12:
PaddlePaddle 3.2.1 CPU, PaddleOCR 3.6.0, PaddleX 3.6.1, Pillow 12.1.1.
Полный набор зависимостей — `requirements-lock.txt` и
`windows-cp312-wheel-lock.json`: 101 точный wheel с официального PyPI,
274001492 байта загрузки, без torch и transformers. В JSON есть URL/размер/SHA256/
license. Конкретный установленный venv привязан к базовому Python и сам по себе
не является переносимой поставкой! Для программы нужны отдельный embed Python,
его штатные зависимости и извлечение закреплённых wheels штатными helpers проекта.

Важное отличие от штатных defaults: `markdown_ignore_labels=[]` сохраняет
сноски, номера и колонтитулы. По умолчанию официальный pipeline исключает
footnote/header/footer/aside_text из Markdown. Исходный структурированный JSON
сохраняется дополнительно.

Ovis получает страницу целиком и точный prompt из официальной карточки;
temperature=0, enable_thinking=false. HTML таблиц, формулы и image bbox tags
не удаляются. В производственном контракте max_tokens=16384; ограничение контекста
сервера проверяется отдельно. Официальный regex cropping image tags применяется
только к координатам, выданным моделью.

## Воспроизведение

1. `Инструменты/BookSmart/prepare_ocr_stand.py` скачивает именно закреплённые
   ревизии, сверяет SHA, не меняет библиотеку пользовательских моделей.
2. `create_fixture.ps1` создаёт PNG 1200×1600 с двумя колонками, сноской и простой
   таблицей. Текст fixture — собственный проверочный материал.
3. `probe_llama_ocr.py --model paddle|ovis --server <existing llama-server.exe>
   --backend <evidence label> --layers 0|99` запускает собственный локальный
   сервер на свободном loopback-порту, сохраняет stdout/stderr/request/response/
   timing. Сервер завершает только свой процесс в finally.
4. `ocr_worker.py --input request.json --output result.json` — готовая связка
   для C# orchestration. Python передаётся извне; hardcode пути к Python нет.
   request: choice, sourcePath, serverUrl (`http://loopback:port/v1`),
   layoutModelDirectory (для Paddle), cacheDirectory (необязательно), page.
   output: schemaVersion=1, status, blocks(id,text,kind,questionable,bbox,page),
   issues, raw, markdown, assets, confidenceAvailable=false, seconds.
   При исключении status=error и ненулевой exit code; незавершённое не выдаётся
   за успешный результат. Локальный layout обязателен; скачивания из worker
   отключены. stdout предназначен для журналов, результат — отдельный JSON.

Ovis использует один блок Markdown для фактического результата страницы;
последующий безопасный импорт в BookDocument выполняет программа. Paddle
сохраняет родные виды блоков, bbox и block_order. assets содержит вырезанные
из оригинала области иллюстраций/графиков по координатам upstream. Parent должен
включить их в документ; текстовое описание не заменяет иллюстрацию.

## Фактические результаты на тестовом ПК

i9-13900KF, RTX4090 24GB, RAM128GB. Контекст 8192, CPU8 threads, один слот.
GPU и CPU запускались последовательно; пользовательские процессы не останавливались.

| Исполнитель | Модель | Загрузка, с | Inference, с | Peak working set сервера |
|---|---|---:|---:|---:|
| EngineSets002 CPU, llama11669/620d2225c | Paddle model-only | 1,03 | 19,00 | 2084007936 байт |
| EngineSets002 CPU | Ovis Q8 | 1,05 | 16,42 | 1562288128 байт |
| b9442 CUDA12.4, d4c8e2c29 | Paddle model-only | 1,81 | 1,22 | 1563836416 байт |
| b9442 CUDA12.4 | Ovis Q8 | 1,58 | 1,10 | 1645105152 байт |

Working set не является VRAM и не является минимумом требований. CUDA private
commit ~3,8GB; VRAM пик в этих запусках не измерялся. Данные по большим страницам,
контексту32768, прочим GPU/CPU и Vulkan этой таблицей не подтверждаются.
Начальные каталоги `*-b9442-cpu` использовали ngl0, но vision по умолчанию был
на CUDA: это смешанный backend, **не доказательство чистого CPU**. Последующие
`002-cpu-measured` используют CPU executable + device none + no-mmproj-offload.

Полный официальный Paddle pipeline: первый запуск 45,09с, включая ~7с загрузки
layout; worker с локальным layout — 36,70с, 24 родных блока, сохранённая сноска,
3 пустых layout-региона отмечены вопросными. Истинный результат:
`worker-paddle-response.json`; подробности `paddle-full-pipeline` и логи.
Один ранний worker получил Connection error после окончания ограниченного времени
тестового сервера; корректно вернул error, повтор с живым сервером успешен.
Ovis worker end-to-end на b9442 CUDA: 1,27с, status=ok, фактический Markdown
сохранён в `worker-ovis-response.json`. Четыре проверки контракта без моделей
PASS: local-only endpoint, подготовленный вход, сохранение исходных image crops
и вопросная отметка при truncation без удаления сносок.

## Подтверждённые ограничения качества

- На fixture с разреженными колонками PP-DocLayoutV3 смешал часть строк колонок;
  полный pipeline также потерял «15 °C». Есть пустые детектированные регионы.
- Ovis сохранил порядок колонок и сноску, но ошибся в буквах: «окна»→«окно»,
  «ъ»→«Ё», «распознанный»→«распознаваемый». Нулевой temperature не устраняет ошибки.
- Оба модельных API не дают надёжную per-character уверенность. Worker не
  выдумывает confidence и не помечает весь текст правильным. Красные отметки
  фиксируют обнаруженные проблемы (например пустой блок, U+FFFD, truncation),
  их отсутствие не доказывает правильность текста. Зелёный — лишь факт ИИ.
- Предупреждение Paddle о неподдерживаемых llama-server min_pixels/max_pixels
  сохранено; размер vision определяется mmproj/runtime. Не менять mmproj metadata
  скрыто и не сохранять изменённый файл под прежним SHA.
- Нельзя по одному примеру обещать сравнительную точность для всех русских книг,
  формул, исторической орфографии, фотографий, разворотов или таблиц.

Общие движки/установщик/основная сборка здесь не пересобирались; production
подготовка, UI, gate/пауза и экспорт проверяются родительской задачей отдельно.

## Проверка полной интеграции: отдельный план памяти Paddle

Родительский native C#-прогон обнаружил отличие model-only стенда от общего
производственного исполнения: generic `LlamaDenseMemoryPolicy` правильно отвергает
неизвестную архитектуру `paddleocr`. Ошибка устранена отдельным
`BookSmartPaddleMemoryPolicy`, без добавления Paddle в общий whitelist и без
подмены архитектуры на Qwen. Только Paddle-ветка книжного OCR передаёт специальный
планировщик в новый internal overload Omni runtime; старые потребители сохраняют
свой расчёт. Backup: `_backups/20261020_book_paddle_memory/files.json`.

Непосредственно из закреплённого GGUF: 18 слоёв, context131072, embedding1024,
16 query heads, 2 KV heads, явные K/V head dimensions128. Нельзя подставлять
embedding/head=64. [Реализация архитектуры в закреплённом llama.cpp](https://github.com/ggml-org/llama.cpp/blob/d4c8e2c29/src/models/paddleocr.cpp)
использует стандартное сохранение K/V на каждом из18 слоёв. Один sequence, F16 K/V:
`context_padded256 × 18 × 2 × (128+128) × 2` байт. Контекст32768 даёт576MiB,
131072 —2304MiB. Модель935769056 + projector881770560 + KV +4GiB раздельных
графовых резервов языка/vision дают6716486688 байт планируемой GPU-памяти.
Это консервативный допуск, а не измеренный VRAM-пик. CPU дополнительно сохраняет
штатные host/OS reserve через `LiteraryRamReservePolicy`. Неизвестные размеры,
переставленные модели, arrays, hybrid/SWA-параметры отвергаются.

`BookSmartPaddleMemoryTests` отдельно проверяет формулу, padding, CPU reserve,
отказ для других профилей и сохранность прежнего generic policy. Их сборку и
прогон, а затем повтор native-конвейера выполняет родительская задача.
