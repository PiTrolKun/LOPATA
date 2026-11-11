namespace Lopata.Collection;

public static class CollectionLabels
{
    public static string Kind(string path, string language)
    {
        var english = language == "en";
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".svg" or ".gif" or ".bmp" or ".tif" or ".tiff"
                or ".avif" or ".jxl" or ".tga" or ".qoi" or ".jp2" or ".j2k" or ".jng" or ".dds"
                or ".exr" or ".hdr" or ".dpx" or ".cin" or ".ico" or ".pcx" or ".psd" or ".psb"
                or ".mng" or ".pbm" or ".pgm" or ".ppm" or ".pam" or ".pfm" or ".phm" or ".pgx"
                or ".sgi" or ".ras" or ".xbm" or ".xpm" or ".wbmp" or ".fits" or ".farbfeld"
                or ".art" or ".avs" or ".dcx" or ".mtv" or ".otb" or ".palm" or ".pdb" or ".vicar"
                or ".viff" or ".vips" => english ? "Images" : "Изображения",
            ".mp3" or ".opus" or ".flac" or ".wav" or ".ogg" or ".m4a" or ".mid" or ".midi" => english ? "Audio" : "Аудио",
            ".mp4" or ".webm" or ".mov" or ".avi" or ".mkv" => english ? "Video" : "Видео",
            ".docx" or ".odt" or ".rtf" or ".txt" or ".md" or ".html" or ".htm" or ".pdf" => english ? "Documents" : "Документы",
            ".xlsx" or ".ods" or ".csv" or ".tsv" => english ? "Spreadsheets" : "Таблицы",
            ".pptx" or ".odp" => english ? "Presentations" : "Презентации",
            ".epub" or ".fb2" or ".cbz" => english ? "Books" : "Книги",
            ".zip" => english ? "Archives" : "Архивы",
            ".lopatadoxai" or ".lopatavideo" or ".lopataaivid" or ".lopatasvg" => english ? "Projects" : "Проекты",
            _ => english ? "Other" : "Другие"
        };
    }

    public static string Scenario(string scenario, string language)
    {
        var english = language == "en";
        var root = scenario.Split('/', 2)[0].ToLowerInvariant();
        return root switch
        {
            "screen-capture" => english ? "Screen capture" : "Захват экрана",
            "image-generation" or "image" => english ? "Image generation" : "Генерация изображений",
            "image-utility" => english ? "Image processing" : "Обработка изображений",
            "vector" or "vector-editor" or "vector-generation" => english ? "Vector graphics" : "Векторная графика",
            "office" => english ? "AI Office" : "AI Office",
            "books" or "book-converter" => english ? "Books" : "Книги",
            "music" or "music-generation" or "music-stems" or "audio-conversion" or "speech-assets" => english ? "Music and audio" : "Музыка и аудио",
            "video" or "video-editor" or "video-preparation" or "song-clip" or "lyric-video" or "video-upscale" => english ? "Video" : "Видео",
            "literary" or "literature" => english ? "Literature" : "Литература",
            "reader" => english ? "Reader" : "Читалка",
            "image-description" => english ? "Image description" : "Описание изображений",
            "let-it-out" or "letitout" or "let_it_out" => english ? "Conversation" : "Разговор",
            "" => english ? "Scenario not recorded" : "Сценарий не указан",
            _ => english ? "Other scenarios" : "Другие сценарии"
        };
    }
}
