using System.IO;
using System.Text;

namespace AIHub.Services.OfficeEditor;

internal static class WorkbookClipboard
{
    internal static string Quote(string value) => value.IndexOfAny(['\t','\r','\n','"']) < 0
        ? value : "\"" + value.Replace("\"","\"\"") + "\"";

    internal static List<List<string>> Parse(string text)
    {
        var result = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, closed = false, rowEnded = false;
        for (int i=0; i<text.Length; i++)
        {
            char c = text[i];
            rowEnded = false;
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i+1 < text.Length && text[i+1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closed = true; }
            }
            else if (c is '\t' or '\r' or '\n')
            {
                row.Add(field.ToString()); field.Clear(); closed = false;
                if (c == '\t') continue;
                if (c == '\r' && i+1 < text.Length && text[i+1] == '\n') i++;
                result.Add(row); row = []; rowEnded = true;
            }
            else if (closed) throw new InvalidDataException();
            else if (c == '"' && field.Length == 0) quoted = true;
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException();
        if (!rowEnded) { row.Add(field.ToString()); result.Add(row); }
        return result;
    }
}
