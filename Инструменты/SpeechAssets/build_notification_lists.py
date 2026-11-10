"""Build review-first RU/EN speech lists from localization, Roslyn and Python AST evidence.

No product settings, voices or notification subscriptions are changed. Only runtime notifications
and processing states are retained; UI documentation is not speech content.
"""
from __future__ import annotations

import argparse
import ast
from collections import defaultdict, Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import shutil
import string
import xml.etree.ElementTree as ET
from notification_filter import localized_reason, has_words, shorten

EVENT_KEY = re.compile(
    r"status|stage|progress|notif|attention|error|fail|warn|success|ready|start|finish|complet|cancel|stop|paus|resum|running|loading|unload|queue|waiting|warm|audit|prepar|download|install|verif|infer|generat|execut|saved|exported|imported|found|busy|missing|invalid|expired|dialog|confirm|validation|unavailable|unsupported|incompatib|requir|corrupt|mismatch|rejected|accepted|removed|deleted|repair|checkpoint|memory|timeout|connect|disconn|offline|online|blocked|attention|overflow|limit|notfound|nofile|notext|model|runtime|worker|engine|step|pass|sampl|denois|encod|decod|compil|token|layer|offload|quant|calibr|optim|splitt|analys|merge|index|segment|frame|chunk|received|selected|changed|detected|recovered|restarted|retir|releas|freed|device|backend|resident|available|cache",
    re.I,
)
MODEL_PREFIX = re.compile(r"^(Models\.|CoreModel\.|ToolModel\.|ModelTuning\.|ModelRuntime\.|Runtime\.|HardwareRuntime\.|ComponentAudit\.|EngineSets\.|ChatSpeech\.|VideoEditor\.Voices\.)")
TEXT_EVENT = re.compile(r"не удалось|невозможно|заверш[её]н|готов[аоы]?\b|ошибк|предупрежден|проверя|подготав|загружа|скачива|выгружа|приостанов|продолжаем|обрабатыва|генериру|сохран[её]н|отмен[её]н|ожидан|распаков|поврежд|недостаточно|подключ[её]н|отключ[её]н|выбран|измен[её]н|освобожд|активирован|передан|получен|модель.*(?:занята|работает)|could not|cannot |failed|completed|paused|resuming|preparing|downloading|unloading|loading|generating|saved|cancelled|canceled|waiting|not found|out of memory", re.I)
FORMAT = re.compile(r"\{[^{}\n]+\}")
NON_SPEECH_KEY = re.compile(r"SystemPrompt|PromptTemplate|PromptBody|InstructionTemplate|(^|\.)(TestRu|TestEn|SampleRu|SampleEn)$", re.I)
CODE_FRAGMENT = re.compile(r"^\s*(SELECT |INSERT |UPDATE |DELETE |CREATE |ALTER |DROP |PRAGMA|https?://|<[^ ]|\{|\[\s*[\{\"]|--[a-z])", re.I)


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def clean(text: str) -> str:
    text = re.sub(r"https?://\S+", "", text)
    text = re.sub(r"[\u200b\ufeff]", "", text)
    text = re.sub(r"\s*[·•]\s*", ". ", text)
    text = re.sub(r"\s+", " ", text).strip(" .…;:|—\t")
    text = re.sub(r"\.(?:\s*\.)+", ".", text)
    return text + ("" if text.endswith((".", "!", "?")) else ".")


def markdown_literal(text: str) -> str:
    # Escape markup, not ordinary sentence punctuation. A leading ordinal must not become a nested list.
    text = re.sub(r"([\\`*_\[\]<>~^])", r"\\\1", text)
    text = re.sub(r"^(\d{1,9})([.)])(?=\s)", r"\1\\\2", text)
    return re.sub(r"^([+>#-])(?=\s)", r"\\\1", text)


def subsystem(code: str) -> tuple[str, str]:
    rules = [
        (r"SpeechAssets|speech.assets", "при создании речевых ассетов", "while creating speech assets"),
        (r"OfficeReader|Reader\.", "при чтении документа", "while reading a document"),
        (r"OfficeEditor|OfficeText|OfficeWorkbook|OfficeSlide", "в редакторе документов", "in the document editor"),
        (r"BookConverter|Books|LiteraryImport|Literary.Import", "при обработке книги", "while processing a book"),
        (r"Rag|Lexicon", "при работе с базой знаний", "while working with the knowledge base"),
        (r"SongClip|VideoPrep|VideoPreparation|H3|Vace", "при подготовке видеоролика", "while preparing a video"),
        (r"Video|Lyric", "при обработке видео", "while processing video"),
        (r"Music|Poetry|Rhyme", "при работе с музыкой и текстами", "while working with music and lyrics"),
        (r"Image|Upscale|Lora", "при обработке изображения", "while processing an image"),
        (r"Vector|Svg", "при работе с векторной графикой", "while working with vector graphics"),
        (r"Dictation", "при голосовом вводе", "during voice input"),
        (r"Speech|Narrator|Voice|Tts", "при озвучивании текста", "while synthesizing speech"),
        (r"LetItOut|Chat", "при работе чата", "while working in the chat"),
        (r"Literary", "при работе над произведением", "while working on a literary project"),
        (r"Financial|Finance", "при финансовом анализе", "during financial analysis"),
        (r"Publisher|Publishing", "при публикации", "during publication"),
        (r"Update|Bootstrap", "при обновлении ЛОПАТЫ", "while updating LOPATA"),
        (r"Collection|Shell", "при работе коллекции ЛОПАТЫ", "while working with the LOPATA collection"),
        (r"Capture", "при захвате экрана", "during screen capture"),
        (r"Model|Runtime|Worker|Engine|Memory|Core|Component", "при работе с моделью", "while working with a model"),
        (r"Background", "при выполнении фоновой задачи", "while running a background task"),
    ]
    for pattern, ru, en in rules:
        if re.search(pattern, code, re.I):
            return ru, en
    return "при выполнении операции", "while performing the operation"


def technical_phrase(text: str, code: str) -> tuple[str, str]:
    ru, en = subsystem(code)
    if re.search(r"out of memory|not enough memory|insufficient.*memory|memory.*limit|не хватает памяти", text, re.I):
        return f"Недостаточно памяти {ru}.", f"There is not enough memory {en}."
    if re.search(r"permission|access denied|consent|acknowledg|license", text, re.I):
        return f"Нужно проверить разрешения и согласия {ru}.", f"Permissions and consent must be checked {en}."
    if re.search(r"hash|checksum|signature|tamper|integrity|changed after", text, re.I):
        return f"Не пройдена проверка целостности {ru}.", f"An integrity check failed {en}."
    if re.search(r"not found|missing|unavailable|does not exist|не найден", text, re.I):
        return f"Не найдены необходимые данные или компоненты {ru}.", f"Required data or components were not found {en}."
    if re.search(r"unsupported|not supported|incompatible|cannot support", text, re.I):
        return f"Обнаружена неподдерживаемая возможность {ru}.", f"An unsupported feature was encountered {en}."
    if re.search(r"timeout|timed out|deadline", text, re.I):
        return f"Превышено время ожидания {ru}.", f"The operation timed out {en}."
    if re.search(r"network|connect|http|server|offline|download", text, re.I):
        return f"Не удалось получить необходимые данные {ru}. Проверьте соединение и настройки.", f"Required data could not be retrieved {en}. Check the connection and settings."
    if re.search(r"busy|another.*active|not ready|still running", text, re.I):
        return f"Исполнитель ещё занят {ru}. Дождитесь завершения текущей задачи.", f"The executor is still busy {en}. Wait for the current task to finish."
    if re.search(r"invalid|malformed|corrupt|empty|mismatch|too large|exceed|unexpected|not valid", text, re.I):
        return f"Обнаружены некорректные или неподходящие данные {ru}.", f"Invalid or unsuitable data was encountered {en}."
    if re.search(r"cancel|abort", text, re.I):
        return f"Операция отменена {ru}.", f"The operation was cancelled {en}."
    return f"Не удалось завершить действие {ru}. Подробности показаны в окне программы.", f"An action could not be completed {en}. Details are shown in the application window."


# Exact bilingual forms for first-party visible strings that are outside Localization/*.json.
DIRECT = {
    "Выберите хотя бы один диалог.": ("Выберите хотя бы один диалог.", "Select at least one dialog."),
    "Select at least one dialog.": ("Выберите хотя бы один диалог.", "Select at least one dialog."),
    "Не удалось открыть ответы:": ("Не удалось открыть ответы.", "Could not open the answers."),
    "Could not open answers:": ("Не удалось открыть ответы.", "Could not open the answers."),
    "Подготовка анализа…": ("Подготовка анализа.", "Preparing the analysis."),
    "Первый проход завершён. Выберите произведение.": ("Первый проход завершён. Выберите произведение.", "The first pass is complete. Select a work."),
    "Анализ остановлен. Ответы сохранены.": ("Анализ остановлен. Ответы сохранены.", "Analysis stopped. The answers have been saved."),
    "Сначала укажите, что первоисточник есть.": ("Сначала укажите, что первоисточник есть.", "First indicate that an original source exists."),
    "First indicate that an original source exists.": ("Сначала укажите, что первоисточник есть.", "First indicate that an original source exists."),
    "Отмечаем литературный текст и лишние фрагменты": ("Отмечаем литературный текст и лишние фрагменты.", "Identifying literary text and unnecessary fragments."),
    "Собираем главы и проверяем сомнительное": ("Собираем главы и проверяем сомнительные фрагменты.", "Assembling chapters and checking uncertain fragments."),
    "Создаём книгу DOCX и историю анализа": ("Создаём книгу и сохраняем историю анализа.", "Creating the book and saving the analysis history."),
    "Another model operation is active.": ("Другая операция с моделью уже выполняется.", "Another model operation is already running."),
    "В библиотеке игры нет рисунка «{value}».": ("В библиотеке игры нет необходимого рисунка.", "A required image is missing from the game library."),
    "Этот элемент библиотеки не является готовым игровым персонажем.": ("Этот элемент библиотеки не является готовым игровым персонажем.", "This library item is not a ready-to-use game character."),
    "У выбранного исходника библиотеки нет подготовленных игровых кадров.": ("У выбранного исходника библиотеки нет подготовленных игровых кадров.", "The selected library source has no prepared game frames."),
    "Слишком много кадров в составных рисунках этого уровня.": ("Слишком много кадров в составных рисунках этого уровня.", "The composite images for this level contain too many frames."),
    "Рисунок не был подготовлен перед запуском игры.": ("Рисунок не был подготовлен перед запуском игры.", "An image was not prepared before the game started."),
    "Размер рисунка библиотеки превышает разрешённый размер.": ("Размер рисунка библиотеки превышает разрешённый размер.", "A library image exceeds the permitted size."),
}


def normalized_template(key: str, ru: str, en: str) -> tuple[str, str]:
    """Fixed speech for a dynamic visual template; its full visual text stays in the registry."""
    if key == "DebugChat.LogCoreMemoryModelFallback":
        return "Не удалось сжать память моделью. Используется резервное сжатие.", "Model-based memory compression failed. Fallback compression is being used."
    if re.search(r"RuntimeReady", key):
        return "Исполнитель модели проверен и готов к работе.", "The model executor has been verified and is ready to work."
    if key == "Components.ExternalDiscoveryFound":
        return "Поиск завершён. Справочные материалы найдены.", "Search completed. Reference materials have been found."
    if key == "Music.Score.ChangeMode":
        return "Для продолжения нужно изменить режим работы. Требуется ваше подтверждение.", "The operating mode must change to continue. Your confirmation is required."
    if "Remaining" in key and not re.search(r"Download|Install", key):
        return "Обработка продолжается. Текущий прогресс показан в окне программы.", "Processing continues. Current progress is shown in the application window."
    if re.search(r"VerifiedInstalled|VerifiedTaskMatch|VerifiedFallback", key):
        return "Найдена проверенная совместимая модель.", "A verified compatible model has been found."
    if re.search(r"Confirm.*Download|DownloadConfirm|MissingPrompt|DownloadDetails", key):
        return "Для продолжения требуется скачивание компонентов. Проверьте сведения и подтвердите загрузку.", "Components must be downloaded to continue. Review the details and confirm the download."
    if re.search(r"Confirm.*Reinstall", key):
        return "Подтвердите переустановку выбранной модели.", "Confirm reinstallation of the selected model."
    if re.search(r"DeleteConfirmation|Remove.*Confirm|Confirm.*Remove|Delete.*Confirm", key):
        return "Подтвердите удаление выбранных данных. Подробности показаны в окне программы.", "Confirm deletion of the selected data. Details are shown in the application window."
    if re.search(r"Cancelled|Canceled|Stopped", key):
        return "Обработка остановлена. Готовые результаты сохранены.", "Processing stopped. Completed results have been saved."
    if re.search(r"Failed|Error|Invalid|Missing|Unavailable|Unresolved", key):
        return technical_phrase(ru + " " + en, key)
    if re.search(r"Saved|Exported|Imported|Updated|Removed|Completed|Complete|Ready|Done", key):
        if re.search(r"Removed", key): return "Выбранные файлы удалены.", "The selected files have been removed."
        if re.search(r"Imported", key): return "Импорт завершён. Проверьте состав загруженного набора.", "Import completed. Review the contents of the imported set."
        if re.search(r"Saved|Exported", key): return "Результат сохранён. Подробности показаны в окне программы.", "The result has been saved. Details are shown in the application window."
        return "Текущий этап завершён. Результат готов к проверке.", "The current stage is complete. The result is ready for review."
    if re.search(r"Paused|Pausing", key): return "Обработка приостановлена.", "Processing is paused."
    if re.search(r"Resum", key): return "Продолжаем обработку с сохранённого места.", "Resuming processing from the saved position."
    if re.search(r"Verif|Validat|Check", key): return "Проверяем подготовленные данные и компоненты.", "Checking the prepared data and components."
    if re.search(r"Download|Install|Remaining|ProgressValue", key): return "Идёт загрузка и подготовка компонентов.", "Components are being downloaded and prepared."
    if re.search(r"Warm|Runtime.*Active", key): return "Модель загружается в память и проходит пробный запуск.", "The model is being loaded into memory and is undergoing a test run."
    if re.search(r"Translat|ReviewProgress", key): return "Переводим следующий фрагмент текста.", "Translating the next text fragment."
    if re.search(r"SpeechAssets", key): return "Создаём следующую речевую запись.", "Generating the next speech recording."
    if re.search(r"Generat|MotionProgress|MontageAnalysis", key): return "Выполняется очередной этап генерации.", "The next generation stage is in progress."
    if re.search(r"Memory|Context|LoadStatus", key): return "Обновлены сведения об использовании памяти и контекста.", "Memory and context usage information has been updated."
    if re.search(r"Previou|NextStage|StageProgress|BudgetProgress", key): return "Выполняется следующий этап задачи.", "The next task stage is in progress."
    if re.search(r"Progress|Working|Prepar|Attempt|Processing|Assembling", key): return "Обработка продолжается. Текущий прогресс показан в окне программы.", "Processing continues. Current progress is shown in the application window."
    # Preserve complete static sentences, removing dynamic-only fragments rather than speaking fake values.
    static = []
    for text in (ru, en):
        sentences = re.split(r"(?<=[.!?])\s+|\n+", text)
        remaining = [s for s in sentences if not FORMAT.search(s) and len(s.strip()) > 6]
        static.append(clean(" ".join(remaining)))
    if all(has_words(t) for t in static): return static[0], static[1]
    return "Обновлены сведения о текущей операции. Подробности показаны в окне программы.", "Information about the current operation has been updated. Details are shown in the application window."


MODEL_EVENTS = [
    ("selected", "Модель выбрана.", "A model has been selected."),
    ("not_selected", "Сначала выберите модель.", "Select a model first."),
    ("discovery", "Ищем установленные модели.", "Searching for installed models."),
    ("found", "Установленная модель найдена.", "An installed model has been found."),
    ("not_installed", "Нужная модель ещё не установлена.", "The required model is not installed yet."),
    ("compatibility", "Проверяем совместимость модели с доступным оборудованием.", "Checking model compatibility with the available hardware."),
    ("compatible", "Модель совместима с выбранным исполнителем.", "The model is compatible with the selected executor."),
    ("incompatible", "Выбранная модель несовместима с доступным исполнителем.", "The selected model is incompatible with the available executor."),
    ("permission", "Для продолжения нужно ваше решение.", "Your decision is required to continue."),
    ("license", "Ознакомьтесь с условиями использования выбранной модели.", "Review the terms of use for the selected model."),
    ("preparing", "Подготавливаем модель и необходимые компоненты.", "Preparing the model and required components."),
    ("download_start", "Начинаем скачивание модели.", "Starting the model download."),
    ("download_progress", "Модель скачивается.", "The model is being downloaded."),
    ("download_pause", "Скачивание модели приостановлено.", "The model download is paused."),
    ("download_resume", "Продолжаем скачивание модели.", "Resuming the model download."),
    ("download_complete", "Модель скачана.", "The model download is complete."),
    ("download_cancel", "Скачивание модели отменено.", "The model download was cancelled."),
    ("download_error", "Не удалось скачать модель. Проверьте соединение и повторите попытку.", "The model could not be downloaded. Check the connection and try again."),
    ("verify", "Проверяем целостность файлов модели.", "Checking the integrity of the model files."),
    ("verified", "Файлы модели прошли проверку.", "The model files passed verification."),
    ("verify_error", "Проверка файлов модели не пройдена.", "Model file verification failed."),
    ("unpack", "Распаковываем файлы модели.", "Unpacking the model files."),
    ("installed", "Установка модели завершена.", "Model installation is complete."),
    ("reinstalled", "Переустановка модели завершена.", "Model reinstallation is complete."),
    ("removed", "Модель удалена из хранилища.", "The model has been removed from storage."),
    ("load", "Загружаем модель в память.", "Loading the model into memory."),
    ("load_cpu", "Загружаем модель в оперативную память.", "Loading the model into system memory."),
    ("load_gpu", "Загружаем модель в видеопамять.", "Loading the model into video memory."),
    ("offload", "Часть модели размещается в оперативной памяти.", "Part of the model is being placed in system memory."),
    ("loaded", "Модель загружена в память.", "The model has been loaded into memory."),
    ("load_error", "Не удалось загрузить модель в память.", "The model could not be loaded into memory."),
    ("warmup", "Выполняем пробный запуск модели.", "Performing a model warm-up run."),
    ("warmup_complete", "Пробный запуск модели завершён.", "The model warm-up run is complete."),
    ("warmup_failed", "Пробный запуск модели завершился ошибкой.", "The model warm-up run failed."),
    ("ready", "Модель готова к работе.", "The model is ready to work."),
    ("busy", "Модель занята текущей задачей.", "The model is busy with the current task."),
    ("cpu", "Выбран исполнитель на центральном процессоре.", "The CPU executor has been selected."),
    ("gpu", "Выбран исполнитель на графическом процессоре.", "The GPU executor has been selected."),
    ("fallback", "Используем другой совместимый исполнитель.", "Using another compatible executor."),
    ("request", "Запрос передан модели.", "The request has been sent to the model."),
    ("prefill", "Модель читает запрос и контекст.", "The model is reading the request and context."),
    ("thinking", "Модель обрабатывает запрос.", "The model is processing the request."),
    ("first_token", "Ожидаем начало ответа модели.", "Waiting for the model to begin its response."),
    ("streaming", "Модель формирует ответ.", "The model is generating a response."),
    ("generation", "Выполняется генерация.", "Generation is in progress."),
    ("complete", "Модель завершила обработку запроса.", "The model has finished processing the request."),
    ("paused", "Работа модели приостановлена.", "The model is paused."),
    ("resuming", "Работа модели продолжается.", "The model is resuming its work."),
    ("cancelled", "Задача модели отменена.", "The model task was cancelled."),
    ("failed", "Задача модели завершилась ошибкой.", "The model task failed."),
    ("timeout", "Превышено время ожидания ответа модели.", "The model response timed out."),
    ("tool", "Модель запросила выполнение инструмента.", "The model requested a tool operation."),
    ("tool_ready", "Инструмент завершил работу. Продолжаем обработку.", "The tool operation is complete. Processing continues."),
    ("tool_failed", "Не удалось выполнить запрос модели к инструменту.", "The model's tool request could not be executed."),
    ("context_near_full", "Контекст модели почти заполнен.", "The model context is nearly full."),
    ("context_full", "Достигнут предел контекста модели.", "The model context limit has been reached."),
    ("context_compress", "Сжимаем историю для продолжения работы модели.", "Compressing the history so the model can continue."),
    ("context_compressed", "История сжата. Можно продолжать работу.", "The history has been compressed. Work can continue."),
    ("token_limit", "Достигнут предел длины ответа модели.", "The model response length limit has been reached."),
    ("cache_reused", "Используем подготовленный кэш модели.", "Reusing the prepared model cache."),
    ("cache_cleared", "Кэш модели очищен.", "The model cache has been cleared."),
    ("memory_low", "Для модели недостаточно свободной памяти.", "There is not enough free memory for the model."),
    ("checkpoint", "Промежуточный результат модели сохранён.", "An intermediate model result has been saved."),
    ("resume_checkpoint", "Продолжаем задачу модели с сохранённого этапа.", "Resuming the model task from the saved stage."),
    ("retire", "Освобождаем память, занятую моделью.", "Releasing the memory used by the model."),
    ("retired", "Модель выгружена из памяти.", "The model has been unloaded from memory."),
    ("retire_failed", "Не удалось подтвердить освобождение памяти модели.", "Release of the model memory could not be confirmed."),
    ("worker_start", "Запускаем исполнитель модели.", "Starting the model worker."),
    ("worker_ready", "Исполнитель модели готов.", "The model worker is ready."),
    ("worker_crash", "Исполнитель модели неожиданно завершился.", "The model worker exited unexpectedly."),
    ("worker_restart", "Перезапускаем исполнитель модели.", "Restarting the model worker."),
    ("worker_recovered", "Работа исполнителя модели восстановлена.", "The model worker has recovered."),
    ("worker_stop", "Исполнитель модели остановлен.", "The model worker has stopped."),
]


STATE_WORDS = {
    "idle": ("Ожидание запуска задачи.", "Waiting for a task to start."),
    "running": ("Задача выполняется.", "The task is running."),
    "pausing": ("Приостанавливаем задачу.", "Pausing the task."),
    "paused": ("Задача приостановлена.", "The task is paused."),
    "waiting": ("Ожидаем продолжения задачи или вашего решения.", "Waiting for the task to continue or for your decision."),
    "countdown": ("Готовимся продолжить задачу.", "Preparing to resume the task."),
    "completed": ("Задача завершена.", "The task is complete."),
    "complete": ("Задача завершена.", "The task is complete."),
    "done": ("Обработка завершена.", "Processing is complete."),
    "failed": ("Задача завершилась ошибкой.", "The task failed."),
    "error": ("Исполнитель сообщил об ошибке.", "The worker reported an error."),
    "canceled": ("Задача отменена.", "The task was cancelled."),
    "cancelled": ("Задача отменена.", "The task was cancelled."),
    "pending": ("Задача ожидает выполнения.", "The task is pending."),
    "skipped": ("Элемент пропущен.", "The item was skipped."),
    "duplicate": ("Обнаружен повторяющийся элемент.", "A duplicate item was detected."),
    "loading": ("Загружаем модель и необходимые данные.", "Loading the model and required data."),
    "planning": ("Подготавливаем план выполнения.", "Preparing the execution plan."),
    "plan": ("Составляем план обработки.", "Preparing the processing plan."),
    "analysis": ("Выполняем анализ исходных данных.", "Analyzing the input data."),
    "ready": ("Исполнитель готов к работе.", "The worker is ready to work."),
    "encoding": ("Кодируем результат.", "Encoding the result."),
    "render": ("Выполняем рендеринг.", "Rendering is in progress."),
    "index": ("Строим поисковый индекс.", "Building the search index."),
    "indexing": ("Строим поисковый индекс.", "Building the search index."),
    "split": ("Разделяем исходный материал на части.", "Splitting the input material into parts."),
    "validate": ("Проверяем подготовленные данные.", "Validating the prepared data."),
    "reused": ("Используем ранее подготовленный результат.", "Reusing a previously prepared result."),
    "cached": ("Используем сохранённый результат из кэша.", "Using a cached result."),
    "resource": ("Проверяем доступные ресурсы.", "Checking available resources."),
    "memory": ("Обновлены сведения об использовании памяти модели.", "Model memory usage information has been updated."),
    "warning": ("Исполнитель сообщил о предупреждении.", "The worker reported a warning."),
    "chunk-start": ("Начинаем обработку следующего фрагмента.", "Starting the next fragment."),
    "chunk": ("Очередной фрагмент обработан.", "The next fragment has been processed."),
    "compatibility": ("Проверяем совместимость компонентов.", "Checking component compatibility."),
    "verified": ("Результат прошёл проверку.", "The result passed verification."),
    "initialize": ("Подготавливаем исполнитель к работе.", "Initializing the worker."),
    "close": ("Исполнитель завершает работу.", "The worker is shutting down."),
    "ok": ("Проверка завершена успешно.", "The check completed successfully."),
    "sequence": ("Создаём музыкальную последовательность.", "Generating the musical sequence."),
    "sound": ("Создаём звуковой результат.", "Generating the audio result."),
    "synchronizing": ("Синхронизируем результат.", "Synchronizing the result."),
    "separating": ("Разделяем звуковые дорожки.", "Separating the audio tracks."),
    "mandatory": ("Начинаем этап обязательных вопросов.", "Starting the required questions stage."),
    "adaptive": ("Начинаем этап уточняющих вопросов.", "Starting the follow-up questions stage."),
    "understanding": ("Проверяем понимание материала.", "Checking understanding of the material."),
    "correction": ("Выполняем исправления.", "Applying corrections."),
    "review": ("Результат готов к просмотру и проверке.", "The result is ready for review."),
    "request": ("Запрос подготовлен.", "The request is ready."),
    "prepared": ("Подготовка завершена.", "Preparation is complete."),
    "result": ("Получен результат обработки.", "A processing result has been received."),
    "embedding": ("Создаём векторное представление материала.", "Creating a vector representation of the material."),
    "decoding original song": ("Декодируем исходную песню.", "Decoding the original song."),
    "decoding the original master song": ("Декодируем оригинальную мастер-запись песни.", "Decoding the original master recording of the song."),
    "conditioning": ("Подготавливаем условия генерации.", "Preparing generation conditioning."),
    "continuity": ("Подготавливаем связность сцен.", "Preparing scene continuity."),
    "first draft": ("Создаём первый черновик.", "Creating the first draft."),
    "geek-analysis": ("Анализируем песню для построения видеоряда.", "Analyzing the song to plan the video sequence."),
    "geek-assembly": ("Собираем подготовленный музыкальный клип.", "Assembling the prepared music video."),
    "geek-cards": ("Подготавливаем карточки сцен музыкального клипа.", "Preparing the music video scene cards."),
    "hardware": ("Проверяем доступное оборудование.", "Checking available hardware."),
    "infinitetalk-assemble": ("Собираем фрагменты видео с говорящим персонажем.", "Assembling the talking-character video fragments."),
    "infinitetalk-audio": ("Подготавливаем аудио для говорящего персонажа.", "Preparing audio for the talking character."),
    "infinitetalk-text": ("Подготавливаем текст для говорящего персонажа.", "Preparing text for the talking character."),
    "infinitetalk-window": ("Обрабатываем очередной фрагмент говорящего персонажа.", "Processing the next talking-character fragment."),
    "ingest": ("Загружаем исходные материалы для обработки.", "Importing the source material for processing."),
    "montage-analysis": ("Анализируем исходные материалы для монтажа.", "Analyzing the source material for editing."),
    "montage-assembly": ("Собираем итоговый монтаж.", "Assembling the final edit."),
    "mux": ("Объединяем видео и звуковую дорожку.", "Combining the video and audio tracks."),
    "native-status": ("Обновлено состояние исполнителя модели.", "The model worker status has been updated."),
    "not_applicable": ("Этот этап для текущей задачи не требуется.", "This stage is not required for the current task."),
    "not_requested": ("Этот этап не был запрошен.", "This stage was not requested."),
    "pass": ("Выполняем очередной проход обработки.", "Performing the next processing pass."),
    "probe": ("Проверяем работу модели коротким запросом.", "Testing the model with a short request."),
    "progress": ("Обработка продолжается.", "Processing continues."),
    "refinement": ("Уточняем результат обработки.", "Refining the processing result."),
    "refinement-saved": ("Результат уточнения сохранён.", "The refinement result has been saved."),
    "refinement-segments": ("Уточняем отдельные фрагменты результата.", "Refining individual result fragments."),
    "refinement-cached": ("Используем сохранённый результат уточнения.", "Reusing a cached refinement result."),
    "resources": ("Проверяем доступные ресурсы.", "Checking available resources."),
    "spelling": ("Проверяем орфографию текста.", "Checking text spelling."),
    "stage": ("Начинается очередной этап обработки.", "The next processing stage is starting."),
    "take": ("Создаём следующий дубль.", "Creating the next take."),
    "view settings": ("Подготавливаем параметры отображения документа.", "Preparing the document display settings."),
    "whole": ("Обрабатываем материал целиком.", "Processing the complete material."),
    "analyzing the source song's beats without loading h3": ("Анализируем ритм исходной песни без загрузки видеомодели.", "Analyzing the original song's beats without loading the video model."),
    "loading audited h3 ref2va": ("Загружаем проверенную видеомодель H3.", "Loading the verified H3 video model."),
    "preparing sdxl backgrounds": ("Подготавливаем фоны с помощью модели изображений.", "Preparing backgrounds with the image model."),
    "load document": ("Загружаем документ в редактор.", "Loading the document into the editor."),
    "loading-diffusion": ("Загружаем диффузионную часть модели.", "Loading the diffusion component of the model."),
    "loading-vae": ("Загружаем кодировщик изображения.", "Loading the image codec model."),
    "existing montage checkpoints, prompts and result were verified": ("Сохранённые этапы монтажа, запросы и результат прошли проверку.", "Saved editing checkpoints, prompts and results passed verification."),
    "rendering verified music-video chunks from disk": ("Собираем проверенные фрагменты музыкального видео с диска.", "Rendering verified music video fragments from disk."),
    "adding the complete original song": ("Добавляем исходную песню целиком.", "Adding the complete original song."),
    "assembling accepted montage shots with the complete original song": ("Собираем подтверждённые сцены с полной исходной песней.", "Assembling the accepted scenes with the complete original song."),
    "assembling the complete music video": ("Собираем полный музыкальный клип.", "Assembling the complete music video."),
    "glitchframe render complete": ("Рендеринг музыкальной визуализации завершён.", "Music visualization rendering is complete."),
    "h3 music video with the complete original song is ready": ("Видеоклип с полной исходной песней готов.", "The music video with the complete original song is ready."),
    "the complete montage music video is ready": ("Полный смонтированный музыкальный клип готов.", "The complete edited music video is ready."),
    "encoding the music video prompt and reference": ("Кодируем запрос и референс для музыкального видео.", "Encoding the prompt and reference for the music video."),
    "lexiconindex": ("Индексируем словарь базы знаний.", "Indexing the knowledge base lexicon."),
    "lexiconmerge": ("Объединяем записи словаря базы знаний.", "Merging knowledge base lexicon entries."),
    "lexiconready": ("Словарь базы знаний готов.", "The knowledge base lexicon is ready."),
    "lexiconvalidate": ("Проверяем записи словаря базы знаний.", "Validating knowledge base lexicon entries."),
}


def state_phrase(code: str, location: str) -> tuple[str, str]:
    name = code.lower()
    if "Update" in location:
        updates = {"prepared": ("Обновление подготовлено к установке.", "The update is ready to install."),
            "applying": ("Устанавливаем обновление.", "Applying the update."),
            "awaitinghealth": ("Проверяем запуск обновлённой программы.", "Checking startup of the updated application."),
            "healthy": ("Обновлённая программа прошла проверку запуска.", "The updated application passed its startup check."),
            "rollingback": ("Возвращаем предыдущую рабочую версию программы.", "Restoring the previous working application version."),
            "rolledback": ("Предыдущая рабочая версия программы восстановлена.", "The previous working application version has been restored.")}
        if name in updates: return updates[name]
    if name in STATE_WORDS: return STATE_WORDS[name]
    if re.search(r"fail|error|crash", name): return technical_phrase(code + " failed", location)
    if re.search(r"finish|complete|done|ready|_verified$", name): return "Очередной этап работы модели завершён.", "The next model stage is complete."
    if re.search(r"load.*text|text.*load", name): return "Загружаем текстовую часть модели.", "Loading the text component of the model."
    if re.search(r"load|initializ|prepar|warm", name): return "Подготавливаем следующий компонент модели.", "Preparing the next model component."
    if re.search(r"unload|retir|releas|offload", name): return "Освобождаем ресурсы модели.", "Releasing model resources."
    if re.search(r"lexicon", name): return "Обрабатываем словарь базы знаний.", "Processing the knowledge base lexicon."
    if re.search(r"encod|vae|decode", name): return "Кодируем или восстанавливаем данные модели.", "Encoding or decoding model data."
    if re.search(r"sampl|diffus|denois|infer", name): return "Выполняется этап генерации модели.", "A model generation stage is in progress."
    if re.search(r"cache|reus", name): return "Используем ранее подготовленные данные.", "Reusing previously prepared data."
    if re.search(r"check|verif|validat|audit", name): return "Проверяем компоненты и подготовленные данные.", "Checking components and prepared data."
    if re.search(r"start|begin", name): return "Начинаем следующий этап обработки.", "Starting the next processing stage."
    return "Изменился этап работы исполнителя. Подробности показаны в окне программы.", "The worker stage has changed. Details are shown in the application window."


def visible_phrase(text: str, location: str) -> tuple[str, str]:
    if re.search(r"fail|error|could not|cannot |не удалось|ошибка|невозможно", text, re.I): return technical_phrase(text, location)
    if re.search(r"complet|finished|готов|заверш", text, re.I): return "Текущий этап обработки завершён.", "The current processing stage is complete."
    if re.search(r"load|download|загру|скачив", text, re.I): return "Выполняется загрузка данных и компонентов.", "Data and components are being loaded."
    if re.search(r"prepar|подготов", text, re.I): return "Выполняется подготовка данных.", "Data preparation is in progress."
    if re.search(r"waiting|ожидан|ожидаем", text, re.I): return "Ожидаем продолжения операции.", "Waiting for the operation to continue."
    return "Изменилось состояние операции. Подробности показаны в окне программы.", "The operation status has changed. Details are shown in the application window."


def worker_events(root: Path):
    events = defaultdict(list); errors = []; count = 0
    for path in sorted((root / "Исходники/AIHub/Tools").rglob("*.py")):
        if any(part in {"vendor", "site-packages", "__pycache__", ".venv"} for part in path.parts): continue
        count += 1
        try: tree = ast.parse(path.read_text(encoding="utf-8-sig"))
        except SyntaxError as error:
            errors.append({"file": path.relative_to(root).as_posix(), "line": error.lineno}); continue
        def add(value, line):
            if isinstance(value, ast.Constant) and isinstance(value.value, str) and 1 < len(value.value) < 160:
                events[value.value].append({"file": path.relative_to(root).as_posix(), "line": line})
        for node in ast.walk(tree):
            if isinstance(node, ast.Dict):
                for key, value in zip(node.keys, node.values):
                    if isinstance(key, ast.Constant) and key.value in {"event", "stage", "phase", "status", "state"}: add(value, node.lineno)
            if isinstance(node, ast.Call):
                name = node.func.id if isinstance(node.func, ast.Name) else node.func.attr if isinstance(node.func, ast.Attribute) else ""
                if re.search(r"emit|stage|event|status|progress", name, re.I):
                    for arg in node.args: add(arg, node.lineno)
                    for keyword in node.keywords:
                        if keyword.arg in {"stage", "phase", "state", "status", "event", "name", "message"}: add(keyword.value, node.lineno)
    return events, errors, count


def build(root: Path, output: Path, scan_path: Path):
    ru = load(root / "Исходники/AIHub/Localization/ru.json")
    en = load(root / "Исходники/AIHub/Localization/en.json")
    scan = load(scan_path)
    refs = defaultdict(list); direct_status = set(); excluded = []
    for item in scan["Items"]:
        for key in item["Keys"]:
            refs[key].append({"file": "Исходники/" + item["File"], "line": item["Line"], "status_context": item["StatusContext"]})
            if item["StatusContext"]: direct_status.add(key)
    groups = {}; event_to_clip = {}
    def add(event_id, pair, category, sources, originals=None, review=False, reason=""):
        pair = tuple(shorten(t) for t in pair)
        if not all(has_words(t) for t in pair):
            excluded.append({"event_id": event_id, "original": originals, "sources": sources,
                "reason": "no meaningful spoken notification after cleanup"})
            return
        pair = tuple(clean(t) for t in pair)
        if not all(pair): raise ValueError((event_id, "empty speech"))
        if any(FORMAT.search(t) or "{value}" in t or re.search(r"[{}]|https?://|[A-Z]:\\", t) for t in pair): raise ValueError((event_id, "unsafe speech", pair))
        clip = "voice." + hashlib.sha256((pair[0] + "\0" + pair[1]).encode()).hexdigest()[:16]
        if clip not in groups:
            groups[clip] = {"clip_id": clip, "ru": pair[0], "en": pair[1], "events": [], "categories": [], "review_required": False}
        entry = groups[clip]; entry["review_required"] |= review
        if category not in entry["categories"]: entry["categories"].append(category)
        if event_id in event_to_clip:
            if event_to_clip[event_id] != clip: raise ValueError((event_id, "ambiguous event mapping"))
            previous = next(event for event in entry["events"] if event["id"] == event_id)
            for source in sources:
                if source not in previous["sources"]: previous["sources"].append(source)
            return
        entry["events"].append({"id": event_id, "sources": sources, "original": originals, "review_required": review, "selection_reason": reason})
        event_to_clip[event_id] = clip

    for key in sorted(ru):
        if NON_SPEECH_KEY.search(key):
            excluded.append({"key": key, "reason": "model prompt or voice sample, not a notification"})
            continue
        reasons = [reason for match, reason in [(EVENT_KEY.search(key), "event-like key"), (MODEL_PREFIX.search(key), "model namespace"),
                    (key in direct_status, "status/progress/message sink"), (TEXT_EVENT.search(ru[key] + " " + en.get(key, "")), "event-like wording")] if match]
        if not reasons: continue
        rejection = localized_reason(key, ru[key], en.get(key, ""), key in direct_status)
        if rejection:
            excluded.append({"key": key, "text": ru[key], "reason": rejection})
            continue
        if (key.startswith("About.Guide.") or re.search(r"(^|\.)(Units|Seconds|PerSecond|Language|ModelSymbol|Characters)$", key)
                or not re.search(r"[А-Яа-яЁёA-Za-z]{2,}", ru[key])
                or re.search(r"^(Исправь ответ|Ты |You are |Верни (?:только |JSON)|Return only)|Верни JSON только|Верни строго JSON", ru[key] + " " + en.get(key, ""))):
            excluded.append({"key": key, "text": ru[key], "reason": "guide, unit/control fragment or model instruction rather than an event"})
            continue
        original = (ru[key], en.get(key, "")); pair = original; review = False
        if re.search(r"SettingsInvalid|InvalidSettings|InvalidParameters|InvalidOptions|ParameterRange", key):
            pair = ("Настройки задания некорректны. Проверьте выделенные поля.", "The task settings are invalid. Check the highlighted fields.")
            review = True
        if key.endswith("ExperimentShort"):
            pair = ("Экспериментальная поддержка.", "Experimental support."); review = True
        if not original[1] or any(FORMAT.search(t) or "{" in t or "}" in t for t in pair):
            pair = normalized_template(key, *original); review = True
        elif re.search(r"[А-Яа-яЁё]", pair[1].replace("ЛОПАТА", "").replace("ЛОПАТЫ", "")):
            pair = normalized_template(key, *original)
            if re.search(r"[А-Яа-яЁё]", pair[1]): pair = (original[0], "Information about the current operation has been updated. Review the details in the application window.")
            review = True
        # A localization value may contain a link; speech uses its readable wording only.
        category = "localized_event"
        add("localization:" + key, pair, category, refs.get(key, []), {"ru": original[0], "en": original[1]}, review, "; ".join(reasons))

    raw_groups = defaultdict(list)
    for item in scan["Items"]:
        if item["StatusContext"] and not item["Keys"]:
            raw_groups[(item["Text"].strip(), subsystem(item["File"]), item["Constructor"].endswith("Exception"))].append(item)
    for (text, _, exception), raw_items in sorted(raw_groups.items()):
        item = raw_items[0]
        text = item["Text"].strip()
        if not item["StatusContext"] or item["Keys"] or not 5 < len(text) < 1200: continue
        source = [{"file": "Исходники/" + candidate["File"], "line": candidate["Line"]} for candidate in raw_items]
        if (re.search(r"prompts?|debugchat", item["File"], re.I) or CODE_FRAGMENT.search(text)
                or re.search(r"Найди внутреннее|Ваша экспертная роль|^\s*\.\s*(?:PID|\{)|^\s*\{value\}.*(?:MB|GB|%)", text)):
            excluded.append({"text": text, "sources": source, "reason": "instruction, code, title or dynamic-only visual fragment"}); continue
        if not re.search(r"[А-Яа-яЁёA-Za-z]{3,}", text): continue
        if not exception and localized_reason("Source.Status", text, text, True):
            excluded.append({"text": text, "sources": source, "reason": "not a runtime notification sentence"}); continue
        event_id = "source:" + hashlib.sha256((item["File"] + "\0" + text).encode()).hexdigest()[:16]
        pair = DIRECT.get(text)
        if pair is None:
            pair = technical_phrase(text, item["File"]) if exception else visible_phrase(text, item["File"])
        add(event_id, pair, "source_candidate", source, {"literal": text, "context": item["Assignment"], "calls": item["Calls"]},
            review= text not in DIRECT, reason="unlocalized status/progress/message/exception source")

    for item in scan["Enums"]:
        add("enum:" + item["Type"] + "." + item["Member"], state_phrase(item["Member"], item["File"]), "state_machine",
            [{"file": "Исходники/" + item["File"], "line": item["Line"]}], {"state": item["Member"]}, True, "explicit enum state")
    for item in scan["Events"]:
        event_id = "model-log:" + item["Code"]
        if event_id in event_to_clip: continue
        sources = [{"file": "Исходники/" + e["File"], "line": e["Line"]} for e in scan["Events"] if e["Code"] == item["Code"]]
        add(event_id, state_phrase(item["Code"], item["File"]), "model_diagnostic_event", sources, {"code": item["Code"]}, True, "model/background diagnostic event; optional notification")
    for item in scan.get("DynamicSinks", []):
        event = "dynamic:" + hashlib.sha256((item["File"] + ":" + str(item["Line"]) + ":" + item["Sink"] + ":" + item["Expression"]).encode()).hexdigest()[:16]
        pair = ("Изменилось состояние операции. Подробности показаны в окне программы.", "The operation status has changed. Details are shown in the application window.")
        if re.search(r"error|fail|exception", item["Expression"], re.I): pair = technical_phrase("operation failed", item["File"])
        add(event, pair, "dynamic_status_sink", [{"file": "Исходники/" + item["File"], "line": item["Line"]}],
            {"sink": item["Sink"], "expression": item["Expression"]}, True, "dynamic status source retained for configuration; no invented runtime value")
    py, errors, py_count = worker_events(root)
    for code, sources in sorted(py.items()):
        if re.search(r"[/\\]|\{", code):
            excluded.append({"text": code, "sources": sources, "reason": "dynamic/code worker fragment"}); continue
        add("worker:" + code, state_phrase(code, "worker"), "worker_stage", sources, {"code": code}, True, "Python AST worker event/stage")
    xaml_count = 0
    for path in (root / "Исходники/AIHub").rglob("*.xaml"):
        if any(part in {"bin", "obj"} for part in path.parts): continue
        xaml_count += 1
        try: tree = ET.parse(path)
        except ET.ParseError: continue
        for node in tree.iter():
            name = node.get("{http://schemas.microsoft.com/winfx/2006/xaml}Name", "")
            text = node.get("Text", "")
            if not re.search(r"status|progress|notice|warning|error", name + " " + text, re.I) or not text: continue
            event = "xaml:" + path.relative_to(root).as_posix() + "#" + (name or hashlib.sha256(text.encode()).hexdigest()[:12])
            pair = DIRECT.get(text) or visible_phrase(text, path.name)
            if text in ru.values():
                key = next(k for k, v in ru.items() if v == text); pair = (text, en.get(key, "Status updated."))
            add(event, pair, "initial_status_label", [{"file": path.relative_to(root).as_posix()}], {"literal": text}, True, "initial visible XAML status; optional")
    for code, first, second in MODEL_EVENTS:
        add("normalized:model." + code, (first, second), "model_lifecycle_reserve", [], None, True,
            "reserve normalized model lifecycle phrase, not a claim of an already wired notification")

    # Keep exact aliases, but synthesize each bilingual phrase once.
    entries = sorted(groups.values(), key=lambda e: (0 if "model_lifecycle_reserve" in e["categories"] else 1, min(v["id"] for v in e["events"])))
    output.mkdir(parents=True, exist_ok=True)
    existing = [output / filename for filename in ("LOPATA_уведомления_и_статусы_ru.md", "LOPATA_уведомления_и_статусы_en.md", "LOPATA_реестр_уведомлений_и_статусов.json") if (output / filename).exists()]
    if existing:
        backup = root / "_backups" / ("NotificationLists_" + datetime.now().strftime("%Y%m%d_%H%M%S_%f"))
        backup.mkdir(parents=True)
        for path in existing:
            if path.is_symlink(): raise ValueError("Refusing to overwrite a linked catalog file")
            shutil.copy2(path, backup / path.name)
        print("Backup:", backup)
    for number, entry in enumerate(entries, 1):
        entry["number"] = number; entry["default_audio_name"] = f"{number:04}.wav"
        entry["text_sha256"] = {language: hashlib.sha256(entry[language].encode()).hexdigest() for language in ("ru", "en")}
    titles = {"ru": "ЛОПАТА — уведомления и статусы моделей", "en": "LOPATA — notifications and model status events"}
    for language in ("ru", "en"):
        version = (root / "VERSION").read_text(encoding="utf-8-sig").strip()
        metadata = {"program": "LOPATA", "version": version, "operation": "speech-assets/notification-catalog", "created_utc": datetime.now(timezone.utc).isoformat()}
        local_date = datetime.now().astimezone().strftime("%d.%m.%Y")
        lines = ["# " + titles[language], "", local_date + " · " + version, "", "<!-- LOPATA.Metadata: " + json.dumps(metadata, ensure_ascii=False) + " -->", ""]
        lines += (["Очищенный набор уведомлений и статусов выполнения для ручной озвучки. Описания интерфейса, инструкции и пустые фрагменты исключены.",
                   "Каждый пункт списка — один будущий звуковой файл. Заголовки и идентификаторы не произносятся. Совпадающие фразы объединены; исходные события перечислены в общем реестре."]
                  if language == "ru" else ["A cleaned set of runtime notifications and processing states for manual speech generation. UI descriptions, instructions and empty fragments are excluded.",
                   "Each list item becomes one audio file. Headings and identifiers are not spoken. Identical phrases share one recording; source events remain in the companion registry."])
        lines += ["", f"{'Реплик' if language == 'ru' else 'Phrases'}: {len(entries)}. {'Событий и кандидатов' if language == 'ru' else 'Events and candidates'}: {len(event_to_clip)}.", ""]
        lines += (["Включён резерв фраз жизненного цикла моделей. Их наличие в списке не означает, что такие голосовые уведомления уже подключены."]
            if language == "ru" else ["A reserve of model lifecycle phrases is included. Their presence does not mean these voice notifications are already connected."])
        lines += [""]
        for entry in entries:
            label = min(event["id"] for event in entry["events"])
            lines += [f"### {entry['number']:04} · {entry['clip_id']}", "", "<!-- LOPATA.Events: " + json.dumps([event["id"] for event in entry["events"]], ensure_ascii=False) + " -->", ""]
            text = entry[language]
            # Escape Markdown inline syntax without changing the rendered/spoken text.
            text = markdown_literal(text)
            lines += ["- " + text, ""]
        (output / f"LOPATA_уведомления_и_статусы_{language}.md").write_text("\n".join(lines), encoding="utf-8", newline="\n")
    data = {"schema": 1, "program": "LOPATA", "program_version": (root / "VERSION").read_text(encoding="utf-8-sig").strip(),
        "operation": "speech-assets/notification-catalog", "model_history": "not-applicable", "generated_utc": datetime.now(timezone.utc).isoformat(),
        "source_hashes": {"scan": hashlib.sha256(scan_path.read_bytes()).hexdigest(),
            "builder": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "filter": hashlib.sha256(Path(__file__).with_name("notification_filter.py").read_bytes()).hexdigest(),
            "localization_ru": hashlib.sha256((root / "Исходники/AIHub/Localization/ru.json").read_bytes()).hexdigest(),
            "localization_en": hashlib.sha256((root / "Исходники/AIHub/Localization/en.json").read_bytes()).hexdigest()},
        "purpose": "filtered runtime notifications and processing states; no runtime subscriptions or audio generated",
        "filter_policy": "runtime evidence and event wording; exclude UI captions, descriptions, instructions and empty fragments",
        "filter_policy_version": 2,
        "coverage": {"csharp_files": scan["Files"], "python_files": py_count, "python_parse_errors": errors, "xaml_files": xaml_count,
            "ru_keys": len(ru), "en_keys": len(en), "selected_localization_keys": sum(k.startswith("localization:") for k in event_to_clip),
            "events": len(event_to_clip), "phrases": len(entries), "categories": dict(Counter(c for e in entries for c in e["categories"]))},
        "entries": entries, "excluded_non_speech_fragments": [
            {"key": item["key"], "reason": item["reason"]} if "key" in item else
            {"event_id": item.get("event_id", "excluded:" + hashlib.sha256(json.dumps(item, ensure_ascii=False, sort_keys=True).encode()).hexdigest()[:16]),
             "sources": item.get("sources", []), "reason": item["reason"]} for item in excluded]}
    (output / "LOPATA_реестр_уведомлений_и_статусов.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(data["coverage"], ensure_ascii=False))
    return data


if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path, required=True); parser.add_argument("--scan", type=Path, required=True)
    args = parser.parse_args(); build(args.root.resolve(), args.output.resolve(), args.scan.resolve())
