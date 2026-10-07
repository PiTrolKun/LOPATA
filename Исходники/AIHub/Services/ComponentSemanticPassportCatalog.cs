using AIHub.Models;

namespace AIHub.Services;

public static class ComponentSemanticPassportCatalog
{
    private static readonly IReadOnlyDictionary<string, ComponentSemanticPassport> Passports =
        new Dictionary<string, ComponentSemanticPassport>(StringComparer.OrdinalIgnoreCase)
        {
            [HardwareRuntimeCatalog.PythonCpuId] = new(
                "Проверенная среда Python/PyTorch для embeddings, «Желе» и SwinIR на процессоре без CUDA. Модели загружаются отдельно.",
                "Verified Python/PyTorch libraries for embeddings, Jelly and SwinIR on the CPU without CUDA. Model weights are downloaded separately."),
            [HardwareRuntimeCatalog.PythonCudaId] = new(
                "Закреплённая среда Python/PyTorch CUDA 12.6. Совместимость видеокарты и драйвера проверяется реальным вычислением; веса моделей загружаются отдельно.",
                "Pinned Python/PyTorch CUDA 12.6 runtime. GPU and driver compatibility is checked by an actual computation; model weights are downloaded separately."),
            [HardwareRuntimeCatalog.PythonXpuId] = new(
                "Закреплённая среда Python/PyTorch XPU для совместимых Intel GPU. Нужен подходящий драйвер; физические испытания Intel GPU ещё не выполнены. Веса загружаются отдельно.",
                "Pinned Python/PyTorch XPU runtime for compatible Intel GPUs. A suitable driver is required; physical Intel GPU tests are still pending. Weights are downloaded separately."),
            [HardwareRuntimeCatalog.PythonCuda128Id] = new(
                "Закреплённая среда Python/PyTorch CUDA 12.8, включая новые архитектуры NVIDIA. Совместимость проверяется реальным вычислением; модели загружаются отдельно.",
                "Pinned Python/PyTorch CUDA 12.8 runtime, including newer NVIDIA architectures. Compatibility is checked by an actual computation; models are downloaded separately."),
            [HardwareRuntimeCatalog.PythonRocmId] = new(
                "Закреплённая среда Python/PyTorch ROCm 7.2.1 для поддерживаемых AMD Radeon в Windows 11. Требуется драйвер AMD; совместимость проверяется вычислением. Физические испытания AMD GPU ещё не выполнены.",
                "Pinned Python/PyTorch ROCm 7.2.1 runtime for supported AMD Radeon GPUs on Windows 11. An AMD driver is required; compatibility is checked by a computation. Physical AMD GPU tests are still pending."),
            [HardwareRuntimeCatalog.SdCpuId] = new(
                "Создаёт изображения совместимыми моделями stable-diffusion.cpp на процессоре. Веса загружаются отдельно.",
                "Generates images with compatible stable-diffusion.cpp models on the CPU. Weights are downloaded separately."),
            [HardwareRuntimeCatalog.SdVulkanId] = new(
                "Создаёт изображения на совместимых Vulkan-видеокартах. Устройства проверяются исполнителем; веса загружаются отдельно.",
                "Generates images on compatible Vulkan GPUs. Devices are checked by the executor; weights are downloaded separately."),
            [HardwareRuntimeCatalog.LlamaCpuId] = new(
                "Запускает совместимые модели llama.cpp на процессоре без обязательного CUDA. Сам по себе не содержит модели.",
                "Runs compatible llama.cpp models on the CPU without requiring CUDA. It contains no model weights."),
            [HardwareRuntimeCatalog.LlamaVulkanId] = new(
                "Исполнитель llama.cpp для совместимых Vulkan-видеокарт. Доступность проверяется по драйверу и устройствам исполнителя; веса моделей загружаются отдельно.",
                "llama.cpp runtime for compatible Vulkan GPUs. Availability is checked against the driver and runtime devices; model weights are downloaded separately."),
            ["builtin.dotnet"] = new(
                "Читает TXT, JSON и XML, работает с потоками и базовыми метаданными файлов. Не понимает смысл нетекстового содержимого.",
                "Reads TXT, JSON and XML, handles streams and basic file metadata. It does not understand non-text content."),
            ["builtin.wpf-images"] = new(
                "Декодирует пиксели и размеры PNG, JPEG, GIF, BMP и TIFF. Не распознаёт объекты и смысл изображения.",
                "Decodes pixels and dimensions of PNG, JPEG, GIF, BMP and TIFF files. It does not recognize objects or image meaning."),
            ["builtin.openxml"] = new(
                "Читает и изменяет внутреннюю структуру DOCX, XLSX и PPTX. Для смысловой работы с содержимым требуется логика исполнителя.",
                "Reads and edits the internal structure of DOCX, XLSX and PPTX files. Semantic work still requires executor logic."),
            ["builtin.closedxml"] = new(
                "Читает и изменяет листы, ячейки и формулы XLSX/XLSM. Не заменяет анализ данных моделью.",
                "Reads and edits XLSX/XLSM sheets, cells and formulas. It does not replace model-driven data analysis."),
            ["builtin.pdfpig"] = new(
                "Извлекает текст, страницы и встроенные изображения из PDF. Скан без текстового слоя требует OCR.",
                "Extracts text, pages and embedded images from PDF files. Scans without a text layer require OCR."),
            ["builtin.sharpcompress"] = new(
                "Показывает состав и читает файлы внутри ZIP, 7z, RAR, TAR и GZip. Не запускает содержимое архивов.",
                "Lists and reads files inside ZIP, 7z, RAR, TAR and GZip archives. It never executes archive contents."),
            ["builtin.csvhelper"] = new(
                "Читает и записывает таблицы CSV/TSV с контролем разделителей и типов полей.",
                "Reads and writes CSV/TSV tables with controlled delimiters and field types."),
            ["builtin.anglesharp"] = new(
                "Разбирает HTML, SVG и CSS в структурированное DOM-дерево. Не исполняет произвольный код страницы.",
                "Parses HTML, SVG and CSS into a structured DOM tree. It does not execute arbitrary page code."),
            ["builtin.markdig"] = new(
                "Разбирает Markdown и безопасно отображает его локально.",
                "Parses Markdown and renders it locally in a controlled form."),
            ["builtin.yamldotnet"] = new(
                "Читает и записывает структурированные YAML-файлы.",
                "Reads and writes structured YAML files."),
            ["builtin.mimekit"] = new(
                "Разбирает EML/MIME, заголовки писем и вложения. Не подключается к почтовому ящику.",
                "Parses EML/MIME messages, mail headers and attachments. It does not connect to a mailbox."),
            ["builtin.sqlite"] = new(
                "Даёт контролируемый доступ только для чтения к схеме и данным SQLite.",
                "Provides controlled read-only access to SQLite schema and data."),
            ["runtime.java.temurin21"] = new(
                "Среда Java, необходимая Apache Tika. Сама файлы не анализирует.",
                "Java runtime required by Apache Tika. It does not analyze files by itself."),
            ["runtime.apache-tika"] = new(
                "Извлекает текст и метаданные из большого числа документов. Не выполняет визуальный анализ изображений.",
                "Extracts text and metadata from many document formats. It does not perform visual image analysis."),
            ["runtime.imagemagick"] = new(
                "Декодирует, преобразует и изменяет пиксели изображений. Не понимает смысл сцены без отдельной модели.",
                "Decodes, converts and edits image pixels. It does not understand scene meaning without a separate model."),
            ["runtime.tesseract"] = new(
                "Распознаёт печатный текст на изображениях. Качество зависит от языка и качества исходника.",
                "Recognizes printed text in images. Accuracy depends on the language pack and source quality."),
            ["language.tesseract.eng"] = new(
                "Английские языковые данные для Tesseract OCR. Без установленного Tesseract не работают.",
                "English language data for Tesseract OCR. It requires the Tesseract runtime."),
            ["language.tesseract.rus"] = new(
                "Русские языковые данные для Tesseract OCR. Без установленного Tesseract не работают.",
                "Russian language data for Tesseract OCR. It requires the Tesseract runtime."),
            ["runtime.ffmpeg"] = new(
                "Декодирует, кодирует и преобразует аудио и видео, извлекает кадры. Не понимает их содержание без отдельной модели.",
                "Decodes, encodes and converts audio/video and extracts frames. It does not understand content without a separate model."),
            ["runtime.libreoffice"] = new(
                "Преобразует старые и альтернативные офисные форматы в поддерживаемые документы. Устанавливается как внешняя программа.",
                "Converts legacy and alternative office formats into supported documents. It is installed as an external application."),
            ["runtime.whisper.cpu"] = new(
                "Локальный runtime распознавания речи whisper.cpp для CPU. Для работы нужна отдельная модель Whisper.",
                "Local whisper.cpp speech-to-text runtime for CPU. A separate Whisper model is required."),
            ["runtime.whisper.cuda124"] = new(
                "Ускоряет whisper.cpp на совместимой NVIDIA CUDA 12.4. Не добавляет новых функций распознавания.",
                "Accelerates whisper.cpp on compatible NVIDIA CUDA 12.4 hardware. It adds no new recognition capability."),
            ["model.whisper.small"] = new(
                "Многоязычная модель Whisper small переводит речь в текст. Она не анализирует музыку и другие звуки как события.",
                "Multilingual Whisper small converts speech to text. It does not analyze music or other sounds as semantic events."),
            ["model.vision.smolvlm2.projector"] = new(
                "Связывает пиксели изображения с локальной моделью SmolVLM2. Самостоятельно изображение не описывает и работает только вместе с совместимой моделью.",
                "Connects image pixels to the local SmolVLM2 model. It cannot describe images by itself and only works with a compatible model."),
            ["model.vision.smolvlm2.q4km"] = new(
                "Локально описывает видимое содержание прикреплённых изображений через проверенный llama.cpp и совместимый проектор. Не гарантирует распознавание личности, точный OCR или достоверность невидимых деталей.",
                "Describes visible content of attached images locally through verified llama.cpp and a compatible projector. It does not guarantee identity recognition, exact OCR, or unseen details."),
            ["runtime.comfyui"] = new(
                "Запланированный runtime генерации изображений. Пока отключён до отдельной безопасной интеграции.",
                "Planned image-generation runtime. It remains disabled until a separate safe integration is implemented."),
            ["viewer.webview2"] = new(
                "Открывает HTML, SVG и локальные веб-представления внутри ЛОПАТА. Используется только интерфейсом.",
                "Opens HTML, SVG and local web-based presentations inside LOPATA. It is used only by the interface."),
            ["viewer.pdfjs"] = new(
                "Показывает PDF постранично, с поиском и выделением текста. Используется только интерфейсом.",
                "Displays PDF pages with search and selectable text. It is used only by the interface."),
            ["viewer.epubjs"] = new(
                "Открывает EPUB с навигацией и оглавлением. Используется только интерфейсом.",
                "Opens EPUB books with navigation and a table of contents. It is used only by the interface."),
            ["viewer.libvlc"] = new(
                "Воспроизводит локальные аудио- и видеофайлы внутри ЛОПАТА. Используется только интерфейсом.",
                "Plays local audio and video inside LOPATA. It is used only by the interface."),
            ["viewer.openseadragon"] = new(
                "Показывает очень большие изображения с плавным масштабированием и перемещением. Используется только интерфейсом.",
                "Displays very large images with smooth zoom and pan. It is used only by the interface."),
            ["viewer.babylon"] = new(
                "Показывает локальные 3D-модели glTF/GLB. Используется только интерфейсом.",
                "Displays local glTF/GLB 3D models. It is used only by the interface."),
            ["viewer.avalonedit"] = new(
                "Открывает большие текстовые файлы и код с номерами строк и поиском. Используется только интерфейсом.",
                "Opens large text and code files with line numbers and search. It is used only by the interface.")
        };

    public static ComponentSemanticPassport Get(ComponentCatalogEntry entry) =>
        Passports.TryGetValue(entry.Id, out var passport)
            ? passport
            : new ComponentSemanticPassport(entry.Description, entry.Description);

    public static string GetDescription(ComponentCatalogEntry entry, string languageCode)
    {
        var passport = Get(entry);
        return string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            ? passport.En
            : passport.Ru;
    }

    public static bool HasPassport(string componentId) =>
        Passports.ContainsKey(componentId);
}

public sealed record ComponentSemanticPassport(string Ru, string En);
