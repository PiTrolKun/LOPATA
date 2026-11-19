using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lopata.Island;

internal enum IslandVoiceAction
{
    Pause, Resume, Next, Previous, SeekRelative, SeekAbsolute, Volume, Louder, Quieter,
    Sequential, Shuffle, Repeat, Current, AddFavorite, RemoveFavorite, PlayFavorites,
    FindTrack, FindArtist, FindAlbum, FindDescription, FindHistory, FindSimilar, ChooseResult, CancelChoice,
    StartTimer, TimerRemaining, StartNote, AddNote, CancelNote, ShowNotes, ShowWeather, RainForecast, ShowStatus, MemoryUsed, ShowTracks,
    OpenLopata, ShowTasks, TaskStatus, PauseTask, ResumeTask, WindowsVolume, ExternalPause, ExternalNext,
    ScheduleStop, CancelStop
}

internal sealed record IslandVoiceCommand(IslandVoiceAction Action, int Value = 0)
{
    internal int Similarity { get; init; } = 100;
    internal string Query { get; init; } = "";
    internal int StopAfterSeconds { get; init; }
}

internal static partial class IslandVoiceCommands
{
    internal const string ScopeDescription =
        "Voice control in the LOPATA Island is not a general-purpose voice assistant. It just makes some everyday tasks easier.";

    internal sealed record Entry(IslandVoiceAction Action, string Russian, string English, string Description,
        string[]? RussianAliases = null, string[]? EnglishAliases = null, string[]? SharedPronunciations = null)
    {
        internal string Example(bool russian) => russian ? Russian : English;
        internal IEnumerable<string> Examples(bool russian) =>
            ((russian ? RussianAliases : EnglishAliases) ?? []).Prepend(Normalize(Example(russian)))
                .Concat(SharedPronunciations ?? []);
    }

    internal static Entry[] Entries { get; } =
    [
        new(IslandVoiceAction.Pause, "Пауза", "Pause", "Pause the source shown in the player.",
            ["поставь на паузу", "поставь музыку на паузу", "на паузу", "музыку на паузу", "приостанови музыку"],
            ["pause playback", "pause the music"],
            ["пауза", "pause", "pausa", "пауза музыки", "pause music"]),
        new(IslandVoiceAction.Resume, "Продолжи", "Resume", "Resume the source shown in the player.",
            ["продолжай", "продолжим", "продолжить", "продолжи музыку", "продолжай музыку", "продолжим музыку",
                "продолжить музыку", "продолжи воспроизведение", "продолжай воспроизведение", "продолжить воспроизведение",
                "сними с паузы", "сними музыку с паузы", "продолжи играть", "играй дальше"],
            ["resume music", "continue", "continue playing", "keep playing", "unpause"]),
        new(IslandVoiceAction.Next, "Следующая", "Next track", "Play the next item in the source shown in the player.",
            ["дальше", "следующий", "следующий трек", "следующая песня", "включи следующий трек", "пропусти песню", "пропусти этот трек"],
            ["next", "next song", "skip track", "skip this song"]),
        new(IslandVoiceAction.Previous, "Предыдущая", "Previous track", "Play the previous item in the source shown in the player.",
            ["предыдущий", "предыдущий трек", "предыдущая песня", "включи предыдущую песню", "верни предыдущий трек", "назад на трек"],
            ["previous", "previous song", "go back one track", "go to previous track"]),
        new(IslandVoiceAction.SeekRelative, "Перемотай на минуту вперёд", "Skip forward one minute", "Move forward or backward within the local track."),
        new(IslandVoiceAction.SeekAbsolute, "Перемотай на 1:30", "Seek to 1:30", "Jump to a time in the local track."),
        new(IslandVoiceAction.Volume, "Громкость музыки 30 процентов", "Music volume 30 percent", "Set the island player volume, from 0 to 100 percent."),
        new(IslandVoiceAction.Louder, "Сделай музыку громче", "Make the music louder", "Raise the island player volume by 10 percentage points.",
            ["громче", "погромче", "сделай громче", "сделай погромче", "прибавь громкость", "подними громкость"],
            ["louder", "volume up", "turn the music up"]),
        new(IslandVoiceAction.Quieter, "Сделай музыку тише", "Make the music quieter", "Lower the island player volume by 10 percentage points.",
            ["тише", "потише", "сделай тише", "сделай потише", "убавь громкость", "понизь громкость", "убавь звук"],
            ["quieter", "volume down", "turn the music down"]),
        new(IslandVoiceAction.Sequential, "Играй подряд", "Play in order", "Play tracks in queue order.",
            ["по порядку", "по очереди", "играй по порядку", "воспроизводи по порядку"], ["play sequentially", "play tracks in order"]),
        new(IslandVoiceAction.Shuffle, "Перемешай очередь", "Shuffle the queue", "Shuffle the local queue.",
            ["случайный порядок", "в случайном порядке", "перемешай треки", "перемешай песни", "включи случайный порядок"],
            ["shuffle tracks", "play in random order"], ["shuffle", "шафл", "шаффл"]),
        new(IslandVoiceAction.Repeat, "Повтори эту песню", "Repeat this track", "Repeat the current local track.",
            ["повтори этот трек", "повтори песню", "повтор трека", "зацикли трек"],
            ["repeat track", "repeat this song", "loop this song"], ["repeat", "репит", "рипит"]),
        new(IslandVoiceAction.Current, "Что сейчас играет?", "What is playing?", "Read the title, artist and state of the source shown in the player.",
            ["что играет", "что за песня", "что за трек", "какая песня играет", "как называется песня"],
            ["whats playing", "what s playing", "what song is this", "what is this song", "what track is playing", "name this song"]),
        new(IslandVoiceAction.AddFavorite, "Добавь эту песню в избранное", "Add this track to favorites", "Save the current local track to favorites.",
            ["добавь в избранное", "сохрани в избранное", "сохрани эту песню в избранное"], ["add to favorites", "save this song to favorites"]),
        new(IslandVoiceAction.RemoveFavorite, "Убери из избранного", "Remove from favorites", "Remove the current local track from favorites.",
            ["убери эту песню из избранного", "удали из избранного", "удали эту песню из избранного"], ["remove this song from favorites"]),
        new(IslandVoiceAction.PlayFavorites, "Включи избранное вперемешку", "Shuffle favorites", "Play the available local favorites in random order.",
            ["перемешай избранное", "включи избранное в случайном порядке", "проиграй избранное вперемешку"],
            ["play favorites on shuffle", "shuffle my favorites", "play favorites"]),
        .. AdditionalEntries()
    ];

    private static readonly Regex PhrasePunctuation = new(@"[^\p{L}\p{N}:\s]", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex WakeBrand = new(@"\b(?:л[оа]пата|l[oa]pata)\b", RegexOptions.CultureInvariant);
    private static readonly Regex CommandFillers = new(@"^(?:(?:пожалуйста|давай|please) )+|(?: (?:пожалуйста|please))+$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Negation = new(@"\b(?:не\p{L}*|not\p{L}*|never|dont\p{L}*|don t\p{L}*)\b", RegexOptions.CultureInvariant);
    private static readonly Regex NegativeNumber = new(@"(?<![\p{L}\p{N}])[-−]\s*\d", RegexOptions.CultureInvariant);
    private static readonly string[] RussianArgumentHeads =
        ["громкость", "громкость музыки", "громкость плеера", "установи громкость", "установи громкость музыки",
            "установи громкость плеера", "поставь громкость", "поставь громкость музыки", "поставь громкость плеера",
            "перемотай на", "промотай на", "перемотай до", "перейди на", "перейди к"];
    private static readonly string[] EnglishArgumentHeads = ["music volume", "set music volume", "seek to", "jump to", "skip"];

    internal static string Normalize(string? value) => Spaces.Replace(PhrasePunctuation.Replace(
        (value ?? "").ToLowerInvariant().Replace('ё', 'е'), " "), " ").Trim().TrimEnd(':');

    internal static bool IsWake(string? transcript, string phrase)
    {
        string expected = NormalizeWake(phrase);
        string spoken = NormalizeWake(transcript);
        if (expected.Length == 0 || spoken.Length is 0 or > 160) return false;
        if (spoken == expected) return true;
        if (Negation.IsMatch(spoken)) return false;

        return expected is "лопата прием" or "лопата come in"
            ? Score(spoken, "лопата прием", exactBrand: true) >= MinimumSimilarity(spoken)
                || Score(spoken, "лопата come in", exactBrand: true) >= MinimumSimilarity(spoken)
            : Score(spoken, expected, exactBrand: true) >= MinimumSimilarity(spoken);
    }

    private static string NormalizeWake(string? phrase) =>
        WakeBrand.Replace(Normalize((phrase ?? "").Replace(':', ' ')), "лопата");

    internal static IslandVoiceCommand? Parse(string? transcript, bool russian)
    {
        if (transcript is null || transcript.Length > 500) return null;
        if (TryReadNote(transcript, russian) is { } note) return note;
        return ParseCommand(transcript, russian, allowLinked: true);
    }

    private static IslandVoiceCommand? ParseCommand(string transcript, bool russian, bool allowLinked)
    {
        if (NegativeNumber.IsMatch(transcript)) return null;
        string phrase = Normalize(transcript);
        if (phrase.Length == 0) return null;
        phrase = CommandFillers.Replace(phrase, "");
        if (allowLinked && LinkedParts(phrase, russian) is { Success: true } linked) return TryReadLinked(linked, russian);
        foreach (var entry in Entries)
        {
            if (HasArgument(entry.Action)) continue;
            if (entry.Examples(russian).Contains(phrase)) return new(entry.Action);
        }
        if (TryReadExtended(phrase, russian) is { } additional) return additional;
        if (phrase.Length > 160 || Negation.IsMatch(phrase)) return null;

        var exact = ParseArguments(phrase, russian);
        if (exact is null && russian && phrase.StartsWith("промотай на ", StringComparison.Ordinal)) return null;
        return exact ?? FindSimilar(phrase, russian);
    }

    private static IslandVoiceCommand? FindSimilar(string phrase, bool russian)
    {
        var candidates = new List<IslandVoiceCommand>();
        foreach (var entry in Entries.Where(entry => !HasArgument(entry.Action)))
        {
            int similarity = entry.Examples(russian).Max(example => Score(phrase, example));
            if (similarity > 0) candidates.Add(new(entry.Action) { Similarity = similarity });
        }

        var words = phrase.Split(' ');
        foreach (string head in russian ? RussianArgumentHeads : EnglishArgumentHeads)
        {
            int headWords = head.Count(character => character == ' ') + 1;
            if (words.Length <= headWords) continue;
            string spokenHead = string.Join(' ', words, 0, headWords);
            int similarity = Score(spokenHead, head);
            if (similarity > 0 && ParseArguments(head + phrase[spokenHead.Length..], russian) is { } command)
                candidates.Add(command with { Similarity = similarity });
        }

        var best = candidates.OrderByDescending(candidate => candidate.Similarity)
            .DistinctBy(candidate => (candidate.Action, candidate.Value)).Take(2).ToArray();
        return best.Length > 0 && best[0].Similarity >= MinimumSimilarity(phrase) &&
            (best.Length == 1 || best[0].Similarity - best[1].Similarity >= 10) ? best[0] : null;
    }

    private static int MinimumSimilarity(string phrase) => phrase.Length <= 6 && !phrase.Contains(' ') ? 75 : 80;

    private static bool HasArgument(IslandVoiceAction action) =>
        action is IslandVoiceAction.SeekAbsolute or IslandVoiceAction.SeekRelative or IslandVoiceAction.Volume
            or IslandVoiceAction.FindTrack or IslandVoiceAction.FindArtist or IslandVoiceAction.FindAlbum
            or IslandVoiceAction.FindDescription or IslandVoiceAction.FindHistory or IslandVoiceAction.ChooseResult
            or IslandVoiceAction.StartTimer or IslandVoiceAction.StartNote or IslandVoiceAction.PauseTask
            or IslandVoiceAction.ResumeTask or IslandVoiceAction.WindowsVolume or IslandVoiceAction.ExternalPause
            or IslandVoiceAction.ScheduleStop;

    private static int Score(string heard, string expected, bool exactBrand = false)
    {
        var words = heard.Split(' ');
        var template = expected.Split(' ');
        if (words.Length != template.Length) return 0;
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i] == template[i]) continue;
            if (exactBrand && (template[i] == "лопата" || words[i] == "лопата")) return 0;
            if ((!exactBrand && template[i].Length < 4) || Similarity(words[i], template[i]) < 60) return 0;
        }
        return Similarity(heard, expected);
    }

    private static int Similarity(string first, string second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (int column = 0; column <= second.Length; column++) previous[column] = column;
        for (int row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= second.Length; column++)
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        int length = Math.Max(first.Length, second.Length);
        return length == 0 ? 0 : 100 * (length - previous[second.Length]) / length;
    }

    private static IslandVoiceCommand? ParseArguments(string phrase, bool russian)
    {
        var volume = Regex.Match(phrase, russian
            ? @"^(?:(?:установи|поставь) )?громкость(?: музыки| плеера)? (?:на )?(.+?)(?: процент(?:а|ов)?)?$"
            : @"^(?:set )?music volume (?:to )?(.+?)(?: percent)?$");
        if (volume.Success && Number(volume.Groups[1].Value, russian) is >= 0 and <= 100 and var level)
            return new(IslandVoiceAction.Volume, level);

        var relative = Regex.Match(phrase, russian
            ? @"^(?:перемотай|промотай) на (.+) (вперед|назад)$"
            : @"^skip (forward|backward|back) (.+)$");
        if (relative.Success)
        {
            var amount = relative.Groups[russian ? 1 : 2].Value;
            var direction = relative.Groups[russian ? 2 : 1].Value;
            if (Duration(amount, russian) is { } delta)
                return new(IslandVoiceAction.SeekRelative, direction is "вперед" or "forward" ? delta : -delta);
        }

        var absolute = Regex.Match(phrase, russian
            ? @"^(?:перемотай (?:на|до)|перейди (?:на|к)) (.+)$"
            : @"^(?:seek|jump) to (.+)$");
        return absolute.Success && Duration(absolute.Groups[1].Value, russian) is { } position
            ? new(IslandVoiceAction.SeekAbsolute, position) : null;
    }

    private static int? Duration(string phrase, bool russian)
    {
        var clock = Regex.Match(phrase, @"^(\d{1,3})\s*:\s*(\d{2})$");
        if (clock.Success && Number(clock.Groups[2].Value, russian) is >= 0 and < 60 and var seconds)
            return int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + seconds;
        var units = Regex.Match(phrase, russian
            ? @"^(?:(.+?) )?(минут(?:у|а|ы)?|секунд(?:у|а|ы)?)(?: (.+?) секунд(?:у|а|ы)?)?$"
            : @"^(?:(.+?) )?(minutes?|seconds?)(?: (?:and )?(.+?) seconds?)?$");
        if (units.Success && (units.Groups[1].Success ? Number(units.Groups[1].Value, russian) : 1) is { } amount)
        {
            bool minutes = units.Groups[2].Value.StartsWith(russian ? "мин" : "min", StringComparison.Ordinal);
            if (!units.Groups[3].Success) return amount * (minutes ? 60 : 1);
            if (minutes && Number(units.Groups[3].Value, russian) is < 60 and var remainder)
                return amount * 60 + remainder;
        }
        return null;
    }

    private static int? Number(string value, bool russian)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int numeric))
            return numeric <= 999 ? numeric : null;
        string[] low = russian
            ? ["ноль","один","два","три","четыре","пять","шесть","семь","восемь","девять","десять","одиннадцать",
                "двенадцать","тринадцать","четырнадцать","пятнадцать","шестнадцать","семнадцать","восемнадцать","девятнадцать"]
            : ["zero","one","two","three","four","five","six","seven","eight","nine","ten","eleven","twelve",
                "thirteen","fourteen","fifteen","sixteen","seventeen","eighteen","nineteen"];
        if (value is "одна" or "одну" or "a") return 1;
        if (value is "две") return 2;
        if (value is "сто" or "one hundred" or "a hundred") return 100;
        int simple = Array.IndexOf(low, value);
        if (simple >= 0) return simple;
        string[] tens = russian ? ["двадцать","тридцать","сорок","пятьдесят","шестьдесят","семьдесят","восемьдесят","девяносто"]
            : ["twenty","thirty","forty","fifty","sixty","seventy","eighty","ninety"];
        var parts = value.Split(' ', 2);
        int ten = Array.IndexOf(tens, parts[0]);
        if (ten < 0) return null;
        int one = parts.Length == 1 ? 0 : Array.IndexOf(low, parts[1]);
        return one is >= 0 and < 10 ? (ten + 2) * 10 + one : null;
    }
}
