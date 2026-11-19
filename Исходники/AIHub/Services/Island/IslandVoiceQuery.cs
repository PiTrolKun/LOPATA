using System;
using System.Text.RegularExpressions;

namespace Lopata.Island;

internal static partial class IslandVoiceCommands
{
    private static Entry[] AdditionalEntries() =>
    [
        new(IslandVoiceAction.FindTrack, "Включи Du Hast", "Play Du Hast", "Find a title or an artist in the local library."),
        new(IslandVoiceAction.FindArtist, "Включи исполнителя Rammstein", "Play artist Rammstein", "Play tracks by an artist from the local library."),
        new(IslandVoiceAction.FindAlbum, "Поставь альбом Mutter", "Play album Mutter", "Play an album from the local library."),
        new(IslandVoiceAction.FindDescription, "Включи песню, где мужик в лес пошёл", "Play a song about walking in the forest", "Search local lyrics and track descriptions."),
        new(IslandVoiceAction.FindHistory, "Включи то, что играло вчера перед Rammstein", "Play what played yesterday before Rammstein", "Search the local listening history when enabled."),
        new(IslandVoiceAction.FindSimilar, "Включи похожее по звучанию", "Play something similar", "Find tracks with similar local genres and tags."),
        new(IslandVoiceAction.ChooseResult, "Первую", "First", "Choose a numbered result after the island offers a choice."),
        new(IslandVoiceAction.CancelChoice, "Отмени выбор", "Cancel the choice", "Dismiss the offered choices.",
            ["отмена выбора"], ["cancel choice"]),
        new(IslandVoiceAction.StartTimer, "Поставь таймер на семь минут", "Set a timer for seven minutes", "Start the island timer for the stated duration."),
        new(IslandVoiceAction.TimerRemaining, "Сколько осталось?", "How much time is left?", "Read the time remaining on the island timer.",
            ["сколько осталось на таймере"], ["time left on the timer"]),
        new(IslandVoiceAction.StartNote, "Запиши в заметку", "Add a note",
            "Wait for the island to ask what to write, then dictate the note or say Cancel."),
        new(IslandVoiceAction.ShowNotes, "Покажи заметки", "Show notes", "Open the island notes.",
            ["открой заметки"], ["open notes"]),
        new(IslandVoiceAction.ShowWeather, "Покажи погоду", "Show weather", "Open the island weather widget.",
            ["открой погоду"], ["open weather"]),
        new(IslandVoiceAction.RainForecast, "Будет ли дождь?", "Will it rain?", "Read the available forecast and when it was updated.",
            ["будет дождь", "будет ли сегодня дождь"], ["is it going to rain"]),
        new(IslandVoiceAction.ShowStatus, "Покажи состояние ПК", "Show PC status", "Open the island system widget.",
            ["покажи состояние компьютера"], ["open system status"]),
        new(IslandVoiceAction.MemoryUsed, "Сколько памяти занято?", "How much memory is used?", "Read current RAM usage.",
            ["сколько оперативной памяти занято"], ["read memory usage"]),
        new(IslandVoiceAction.ShowTracks, "Открой список треков", "Open the track list", "Open the current local track list.",
            ["покажи список треков", "открой список песен"], ["show tracks", "show the track list"]),
        new(IslandVoiceAction.OpenLopata, "Открой ЛОПАТУ", "Open LOPATA", "Open or activate the main LOPATA window.",
            ["открой лапату", "запусти лопату"], ["open lapata", "launch lopata"]),
        new(IslandVoiceAction.ShowTasks, "Открой список задач", "Open the task list", "Open the list of tasks in the island.",
            ["покажи задачи лопаты"], ["show lopata tasks"]),
        new(IslandVoiceAction.TaskStatus, "Как идёт генерация?", "How is generation going?", "Read the current task state confirmed by LOPATA.",
            ["как идет обработка", "что с генерацией"], ["what is the task status"]),
        new(IslandVoiceAction.PauseTask, "Поставь апскейл на паузу", "Pause the upscale task", "Pause a matching LOPATA task when it supports pausing."),
        new(IslandVoiceAction.ResumeTask, "Продолжи обработку", "Resume the processing task", "Resume a matching paused LOPATA task."),
        new(IslandVoiceAction.WindowsVolume, "Громкость системы 30 процентов", "System volume 30 percent", "Set the Windows output volume."),
        new(IslandVoiceAction.ExternalPause, "Пауза в YouTube", "Pause YouTube", "Pause the current external player when Windows identifies it as the named source."),
        new(IslandVoiceAction.ExternalNext, "Следующее видео", "Next video", "Advance the external source shown in the player when it offers Next.",
            ["следующий ролик"], ["skip to the next video"]),
        new(IslandVoiceAction.ScheduleStop, "Выключи музыку через полчаса", "Stop music in half an hour",
            "Schedule a stop for the current local playback. For example, “Play favorites and stop music in half an hour.”"),
        new(IslandVoiceAction.CancelStop, "Отмени остановку музыки", "Cancel the music stop timer", "Cancel the island's scheduled music stop.",
            ["отмени таймер музыки", "отмени отключение музыки"], ["cancel the scheduled stop"])
    ];

    private static IslandVoiceCommand? TryReadNote(string transcript, bool russian)
    {
        string spoken = CommandFillers.Replace(Normalize(transcript.Replace(':', ' ')), "");
        string[] heads = russian
            ? ["запиши в заметку", "запиши в заметки", "запиши заметку", "запиши заметки", "добавь в заметки",
                "добавь заметку", "создай заметку", "сделай заметку"]
            : ["add a note", "append to notes", "write a note", "take a note", "make a note"];
        var words = spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int best = 0;
        foreach (string head in heads)
        {
            int count = 1;
            foreach (char character in head)
                if (character == ' ') count++;
            if (words.Length < count) continue;
            string prefix = string.Join(' ', words, 0, count);
            best = Math.Max(best, Score(prefix, head));
        }
        return best >= 86 ? new(IslandVoiceAction.StartNote) { Similarity = best } : null;
    }

    internal static IslandVoiceCommand? ParseNoteReply(string? transcript)
    {
        if (transcript?.Trim() is not { Length: > 0 and <= 500 } note) return null;
        string normalized = CommandFillers.Replace(Normalize(note.Replace(':', ' ')), "");
        return normalized switch
        {
            "" => null,
            "отмена" or "отмени" or "отмени заметку" or "не записывай" or "cancel" or "cancel note" or "cancel the note" =>
                new(IslandVoiceAction.CancelNote),
            _ => new(IslandVoiceAction.AddNote) { Query = note }
        };
    }

    private static Match LinkedParts(string phrase, bool russian) => Regex.Match(phrase, russian
        ? @"^(.+?) и ((?:выключи|останови) музыку через .+)$"
        : @"^(.+?) and (?:then )?((?:stop|turn off) (?:the )?music in .+)$");

    private static IslandVoiceCommand? TryReadLinked(Match linked, bool russian)
    {
        var first = ParseCommand(linked.Groups[1].Value, russian, allowLinked: false);
        var stop = TryReadExtended(linked.Groups[2].Value, russian);
        return first?.Action is IslandVoiceAction.FindTrack or IslandVoiceAction.FindAlbum or IslandVoiceAction.FindArtist
            or IslandVoiceAction.FindDescription or IslandVoiceAction.FindHistory or IslandVoiceAction.FindSimilar
            or IslandVoiceAction.PlayFavorites && stop?.Action == IslandVoiceAction.ScheduleStop
            ? first with { StopAfterSeconds = stop.Value } : null;
    }

    private static IslandVoiceCommand? TryReadExtended(string phrase, bool russian)
    {
        var timer = Regex.Match(phrase, russian ? @"^(?:(?:поставь|запусти|установи) )?таймер на (.+)$"
            : @"^(?:set|start) (?:a |the )?timer (?:for|of) (.+)$");
        if (timer.Success) return PositiveDuration(timer.Groups[1].Value, russian, IslandVoiceAction.StartTimer);
        var stop = Regex.Match(phrase, russian ? @"^(?:выключи|останови) музыку через (.+)$"
            : @"^(?:stop|turn off) (?:the )?music in (.+)$");
        if (stop.Success) return PositiveDuration(stop.Groups[1].Value, russian, IslandVoiceAction.ScheduleStop);
        var volume = Regex.Match(phrase, russian
            ? @"^(?:(?:поставь|установи) )?громкость (?:системы|виндовс|windows) (?:на )?(.+?)(?: процент(?:а|ов)?)?$"
            : @"^(?:set )?(?:system|windows) volume (?:to )?(.+?)(?: percent)?$");
        if (volume.Success && Number(volume.Groups[1].Value, russian) is >= 0 and <= 100 and var level)
            return new(IslandVoiceAction.WindowsVolume, level);

        int choice = phrase switch
        {
            "1" or "первую" or "первый" or "первая" or "первый вариант" or "first" => 1,
            "2" or "вторую" or "второй" or "вторая" or "второй вариант" or "second" => 2,
            "3" or "третью" or "третий" or "третья" or "третий вариант" or "third" => 3,
            _ => 0
        };
        var option = Regex.Match(phrase, russian ? @"^(?:выбери )?вариант (.+)$" : @"^(?:choose )?(?:option|number) (.+)$");
        if (option.Success) choice = Number(option.Groups[1].Value, russian) ?? 0;
        if (choice is >= 1 and <= 3) return new(IslandVoiceAction.ChooseResult, choice);

        var pause = Regex.Match(phrase, russian ? @"^поставь (.+) на паузу$" : @"^pause (?:the )?(.+) task$");
        if (pause.Success && Normalize(pause.Groups[1].Value) is not ("музыку" or "песню" or "трек"))
            return Query(IslandVoiceAction.PauseTask, pause.Groups[1].Value);
        var resume = Regex.Match(phrase, russian ? @"^продолжи (?:задачу |обработку |генерацию )?(.+)$"
            : @"^resume (?:the )?(.+) task$");
        if (resume.Success && (!russian || phrase is "продолжи обработку" or "продолжи генерацию"
            || phrase.StartsWith("продолжи задачу ", StringComparison.Ordinal)))
            return Query(IslandVoiceAction.ResumeTask, resume.Groups[1].Value);
        var external = Regex.Match(phrase, russian ? @"^пауза (?:в|на) (.+)$" : @"^pause (?:in )?(.+)$");
        if (external.Success && external.Groups[1].Value is "youtube" or "ютуб" or "ютубе" or "spotify" or "спотифай" or "vlc" or "влс")
            return Query(IslandVoiceAction.ExternalPause, external.Groups[1].Value);

        return TryReadSearch(phrase, russian);
    }

    private static IslandVoiceCommand? TryReadSearch(string phrase, bool russian)
    {
        var play = Regex.Match(phrase, russian ? @"^(?:включи|поставь|проиграй) (.+)$" : @"^play (.+)$");
        if (!play.Success) return null;
        string query = play.Groups[1].Value;
        if (query is "избранное" or "my favorites" or "favorites") return new(IslandVoiceAction.PlayFavorites);
        if (query is "похожее" or "похожее по звучанию" or "something similar" or "a similar track")
            return new(IslandVoiceAction.FindSimilar);
        var history = Regex.Match(query, russian
            ? @"^(?:то )?что (?:играло|звучало) (вчера|сегодня)(?: перед (.+))?$"
            : @"^what played (yesterday|today)(?: before (.+))?$");
        if (history.Success) return new(IslandVoiceAction.FindHistory, history.Groups[1].Value is "вчера" or "yesterday" ? 1 : 0)
            { Query = history.Groups[2].Value };
        var album = Regex.Match(query, russian ? @"^альбом (.+)$" : @"^(?:the )?album (.+)$");
        if (album.Success) return Query(IslandVoiceAction.FindAlbum, album.Groups[1].Value);
        var artist = Regex.Match(query, russian ? @"^(?:исполнителя|группу) (.+)$" : @"^(?:the )?artist (.+)$");
        if (artist.Success) return Query(IslandVoiceAction.FindArtist, artist.Groups[1].Value);
        var description = Regex.Match(query, russian
            ? @"^(?:ту )?(?:песню|трек)(?: (.+?))? (?:где|про|о том как) (.+)$|^что нибудь (.+)$"
            : @"^(?:a |the )?(?:song|track)(?: by (.+?))? (?:about|where) (.+)$|^something (.+)$");
        if (description.Success)
            return Query(IslandVoiceAction.FindDescription, description.Groups[3].Success
                ? description.Groups[3].Value : (description.Groups[1].Value + " " + description.Groups[2].Value).Trim());
        var track = Regex.Replace(query, russian ? @"^(?:(?:ту )?(?:песню|трек) )" : @"^(?:the )?(?:song|track) ", "");
        return track is "плеер" or "музыку" or "music" or "избранное вперемешку" or "favorites on shuffle"
            || track.StartsWith("громкость ", StringComparison.Ordinal) || track.StartsWith("таймер ", StringComparison.Ordinal)
            || track is "избранное в случайном порядке"
            ? null : Query(IslandVoiceAction.FindTrack, track);
    }

    private static IslandVoiceCommand? PositiveDuration(string phrase, bool russian, IslandVoiceAction action)
    {
        int? seconds = phrase is "полчаса" or "половину часа" or "half an hour" ? 1800
            : phrase is "час" or "an hour" or "one hour" ? 3600 : Duration(phrase, russian);
        return seconds is >= 6 and <= 86400 ? new(action, seconds.Value) : null;
    }

    private static IslandVoiceCommand? Query(IslandVoiceAction action, string query) =>
        query.Length is > 0 and <= 150 ? new(action) { Query = query } : null;
}
