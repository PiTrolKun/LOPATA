"""Select speakable runtime notifications, not UI copy that happens to mention a model."""
import re

DOCUMENTATION_KEY = re.compile(
    r"About\.Guide|(?:^|\.)(?:Help\w*|Hint\w*|Tooltip\w*|Description\w*|Desc|Guide\w*|"
    r"Instruction\w*|Prompt\w*|SystemPrompt\w*|Example\w*|Sample\w*|TestRu|TestEn|"
    r"Title|Header|Caption|Label|Name|DefaultName|Button\w*|Tags?|Keywords?|Unit\w*|"
    r"Format|Placeholder|Watermark|Shortcut|Hotkey|Choice|Option\w*|Preset\w*)(?:\.|$)", re.I)
DOCUMENTATION_SUFFIX = re.compile(r"(?:^|\.)\w*(?:Help|Hint|Tooltip|Description|Instruction|Instructions|Explanation|Legend|Label|Title|Caption|Header|Placeholder)$", re.I)
PARAMETER_KEY = re.compile(r"\.Settings\.program\.|(?:SavedOptions|SavedCandidates|saved_candidates)$", re.I)
STATIC_COPY_KEYS = {"Music.Ace.Unsupported", "Capture.RecordingStatus", "ChoiceScenario.StepStatus"}
VERIFIED_EVENTS = {"BookConverter.Review.BlockMissing", "DebugChat.LogCoreMemoryModelFallback",
    "DebugChat.LogNoModelSelected", "Finance.NoModels", "Generation.Examples.CopyFailed",
    "HardwareRuntime.JellyCpuMemory", "Music.Examples.Error", "PromptPairs.StorageError",
    "Vector.Examples.CopyFailed"}
ACTION = re.compile(r"^(?:Убрать|Переключить|Восстановить|Показать|Скрыть|Повторить|Вернуть|Разрешить|Отключить|Включить|Импортировать|Экспортировать|Сохранить|Скачать|Удалить|Открыть|Закрыть|Запустить|Выбрать|Добавить|Проверить)\b", re.I)
DANGLING = re.compile(r"^(?:не (?:выбран[ао]?|найден[ао]?)|нет проверенного модуля|осталось(?:\s+\w+){0,4})[.…]*$", re.I)
EVENT_KEY = re.compile(
    r"(?:^|\.)(?:\w*(?:Status|Progress|Error|Failed|Failure|Warning|Success|Ready|Completed|Cancelled|Canceled|"
    r"Paused|Pausing|Resumed|Stopped|Missing|Invalid|Unavailable|Unsupported|Required|NotFound|"
    r"NoFile|NoText|NotReady|Busy|Corrupt|Mismatch|Rejected|Expired|Timeout|Saved|Exported|Imported|"
    r"Removed|Deleted|Detected|Recovered|Loading|Preparing|Downloading|Installing|Generating|Waiting|"
    r"Empty|Done|Finished|Interrupted|Started|NotSelected|NeedsInput|NoSelection|NotInstalled)\w*)(?:\.|$)", re.I)
SIGNAL = re.compile(
    r"не удалось|невозможно|недостаточно|отсутству\w*|не найден\w*|не выбран\w*|не установлен\w*|"
    r"не поддержива\w*|поврежд[её]н\w*|ошибк\w*|ист[её]к\w*|требуется|необходимо|"
    r"\b(?:нет|нельзя|готово|занят[ао]?|сохран[её]н[аоы]?|заверш[её]н[аоы]?|отмен[её]н[аоы]?|"
    r"остановлен[аоы]?|приостановлен[аоы]?|загружен[аоы]?|выгружен[аоы]?|подключ[её]н[аоы]?|"
    r"отключ[её]н[аоы]?|удал[её]н[аоы]?|обновл[её]н[аоы]?|обнаружен[аоы]?|пропущен[аоы]?)\b|"
    r"\b(?:проверяем|подготавливаем|загружаем|скачиваем|распаковываем|выгружаем|"
    r"генерируем|созда[её]м|переводим|обрабатываем|продолжаем|ожидаем|сохраняем|начинаем|"
    r"собираем|анализируем|выполняется|ид[её]т|осталось|ожидание|загрузка|скачивание)\b|"
    r"\b(?:failed|failure|error|cannot|could not|not supported|unable|unavailable|unsupported|missing|corrupt|"
    r"completed|finished|saved|exported|imported|removed|deleted|cancelled|canceled|paused|"
    r"stopped|interrupted|ready|waiting|loading|downloading|preparing|generating|processing|"
    r"resuming|verifying|installing|unloading|expired|busy|not found|not selected|not installed)\b", re.I)
CONTROL = re.compile(
    r"^(?:Сохранить|Скачать|Установить|Проверить|Открыть|Закрыть|Начать|Продолжить|Очистить|"
    r"Выбрать|Выбор|Настройки|Добавить|Удалить|Переименовать|Копировать|Запустить|"
    r"Обновить|Пауза|Стоп|Да|Нет|Отмена|Генерация|Модель|Формат|Качество|Размер|"
    r"Save|Download|Install|Check|Open|Close|Start|Continue|Clear|Select|Settings|Add|Delete|"
    r"Rename|Copy|Run|Pause|Stop|Yes|No|Cancel|Model|Format|Quality|Size)(?:\s+[\w×+−-]+){0,5}[.…!?]*$", re.I)
EXPLANATION = re.compile(
    r"(?:по умолчанию|позволяет|предназначен|используется для|можно использовать|нажмите|"
    r"эта настройка|этот режим|режим работы|параметр зада[её]т|включает|включите|показывать |показан\w* (?:только )?для контроля|"
    r"настройте|вы можете|you can |this (?:setting|option|mode)\b|by default|allows you|used to |click |enable )", re.I)


def has_words(text):
    text = re.sub(r"https?://\S+|\{[^{}]*\}", "", text)
    return bool(re.search(r"[А-Яа-яЁёA-Za-z]{3,}", text))


def localized_reason(key, ru, en, status_context):
    """Return a rejection reason, or None for an actual runtime phrase."""
    if key in VERIFIED_EVENTS and has_words(ru) and has_words(en):
        return None
    if DOCUMENTATION_KEY.search(key) or DOCUMENTATION_SUFFIX.search(key) or PARAMETER_KEY.search(key):
        return "UI heading, description, help, example or instruction"
    if key in STATIC_COPY_KEYS:
        return "static compatibility note, initial counter or input instructions"
    if not has_words(ru) or not has_words(en):
        return "no speakable words after removing placeholders and links"
    if DANGLING.fullmatch(ru.strip()):
        return "incomplete label requiring an omitted value or subject"
    if ACTION.search(ru.strip()) and not re.search(r"не удалось|невозможно|нельзя|ошибк", ru, re.I):
        return "action button or menu instruction"
    if (CONTROL.fullmatch(ru.strip()) or CONTROL.fullmatch(en.strip())) and not SIGNAL.search(ru + " " + en):
        return "control caption rather than a notification"
    if not status_context and not EVENT_KEY.search(key):
        return "no runtime notification or status evidence"
    if not status_context and re.search(r"пока нет|появятся? здесь|здесь появ|сохран[её]нных (?:сессий|прохождений).*нет", ru, re.I):
        return "empty-view placeholder rather than a runtime notification"
    if EXPLANATION.search(ru + " " + en):
        # Real failure dialogs can explain how to recover; keep their first actual event sentence.
        first = re.split(r"(?<=[.!?])\s+|\n", ru.strip(), maxsplit=1)[0]
        if EXPLANATION.search(first) or not SIGNAL.search(first):
            return "explanatory or instructional UI copy"
    if not SIGNAL.search(ru + " " + en):
        return "static UI text without an event or processing state"
    return None


def shorten(text):
    """Keep the notification and a short recovery action, not a whole dialog's documentation."""
    text = re.sub(r"^\s*(?:Статус|Status)\s*:\s*", "", text, flags=re.I)
    text = re.sub(r"https?://\S+", "", text)
    sentences = re.split(r"(?<=[.!?])\s+|\n+", text)
    result = []
    for sentence in sentences:
        if not has_words(sentence) or EXPLANATION.search(sentence) or DANGLING.fullmatch(sentence.strip()):
            continue
        if sentence.rstrip().endswith(":"):
            if not SIGNAL.search(sentence):
                continue
            sentence = re.sub(r"\s+(?:в|по адресу|to|in):\s*$", "", sentence, flags=re.I)
            sentence = sentence.rstrip(" :") + "."
        if len(" ".join(result + [sentence])) > 300:
            break
        result.append(sentence)
        if len(result) == 2:
            break
    result = " ".join(result).strip()
    if result.strip(" .").casefold() in {"состояние", "status"}:
        return ""
    return result[:1].upper() + result[1:]
