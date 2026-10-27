using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services.VideoPreparation;

/// <summary>Literal source spans are restored outside the model. Missing/duplicated markers reject the reply.</summary>
public sealed class VideoTranslationProtection
{
    private readonly List<(string Token, string Literal)> _literals = [];
    private readonly string _prefix;
    public string Text { get; }

    public VideoTranslationProtection(string source, bool stable = false)
    {
        var ranges = new List<(int Start, int Length)>();
        var quoteStart = -1; var close = '\0';
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\' && i + 1 < source.Length && source[i + 1] is '"' or '\'' or '\\') { i++; continue; }
            if (c is '\'' or '’' && i > 0 && i + 1 < source.Length && char.IsLetterOrDigit(source[i - 1]) && char.IsLetterOrDigit(source[i + 1])) continue;
            if (quoteStart >= 0)
            {
                if (c == close) { ranges.Add((quoteStart, i - quoteStart + 1)); quoteStart = -1; }
                continue;
            }
            close = c switch { '"' => '"', '\'' => '\'', '«' => '»', '‹' => '›', '“' => '”', '„' => '“', '‘' => '’', '「' => '」', '『' => '』', _ => '\0' };
            if (close != '\0') quoteStart = i;
        }
        // The application's name is intentionally tested in both scripts. Ordinary lowercase "лопата" remains translatable.
        foreach (Match match in Regex.Matches(source, @"(?<!\p{L})(?:Лопата|Лопату|Лопаты|Лопатой|Лопате|ЛОПАТА|LOPATA)(?!\p{L})", RegexOptions.CultureInvariant))
            if (!ranges.Any(r => match.Index >= r.Start && match.Index < r.Start + r.Length)) ranges.Add((match.Index, match.Length));
        var output = new StringBuilder(); var end = 0; var nonce = stable ? VideoShots.Hash(source)[..12].ToLowerInvariant() : Guid.NewGuid().ToString("N")[..12];
        _prefix = "LOPATA_KEEP_" + nonce + "_";
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            var token = _prefix + _literals.Count.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            _literals.Add((token, source.Substring(range.Start, range.Length)));
            output.Append(source, end, range.Start - end); output.Append(token); end = range.Start + range.Length;
        }
        output.Append(source, end, source.Length - end); Text = output.ToString();
    }

    public VideoConceptAnswer Restore(VideoConceptAnswer answer)
    {
        VideoConceptProtocol.Validate(answer);
        var text = answer.Text; var changes = answer.Changes; var clarifications = answer.Clarifications;
        foreach (var (token, literal) in _literals)
        {
            var index = text.IndexOf(token, StringComparison.Ordinal);
            if (index < 0 || text.IndexOf(token, index + token.Length, StringComparison.Ordinal) >= 0
                || index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_')
                || index + token.Length < text.Length && (char.IsLetterOrDigit(text[index + token.Length]) || text[index + token.Length] == '_'))
                throw new InvalidDataException("VideoPrep.TranslationLiteralLost");
            text = text.Replace(token, literal, StringComparison.Ordinal);
            changes = changes.Replace(token, literal, StringComparison.Ordinal);
            clarifications = clarifications.Replace(token, literal, StringComparison.Ordinal);
        }
        if (text.Contains(_prefix, StringComparison.Ordinal) || changes.Contains(_prefix, StringComparison.Ordinal)
            || clarifications.Contains(_prefix, StringComparison.Ordinal)) throw new InvalidDataException("VideoPrep.TranslationLiteralLost");
        var restored = answer with { Text = text, Changes = changes, Clarifications = clarifications };
        VideoConceptProtocol.Validate(restored); return restored;
    }
}
