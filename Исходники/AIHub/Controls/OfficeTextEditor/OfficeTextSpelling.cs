using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;

namespace AIHub.Controls.OfficeTextEditor;

/// <summary>Windows ISpellChecker reports UTF-16 ranges as used by the browser DOM.
/// All COM objects in a check belong to the same invoking apartment.</summary>
internal static partial class OfficeTextSpelling
{
    internal sealed record Issue(int offset, int length, string[] suggestions);
    internal sealed record Result(List<Issue> Issues, HashSet<string> Missing);
    private static readonly Guid FactoryClass = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    internal static Result Check(string text, string language, CancellationToken token)
    {
        var factory = (IFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(FactoryClass, true)!)!;
        var checkers = new Dictionary<string, IChecker>();
        var missing = new HashSet<string>();
        var issues = new List<Issue>();
        try
        {
            foreach (Match word in Words().Matches(text))
            {
                token.ThrowIfCancellationRequested();
                var tag = word.Value.Any(c => c is >= '\u0400' and <= '\u052f') ? "ru-RU" :
                    word.Value.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') ? "en-US" : language;
                if (missing.Contains(tag)) continue;
                if (!checkers.TryGetValue(tag,out var checker))
                {
                    if (!factory.IsSupported(tag)) { missing.Add(tag); continue; }
                    checker = factory.Create(tag);
                    checkers.Add(tag,checker);
                }
                var errors = checker.Check(word.Value);
                try
                {
                    while (errors.Next(out var item) == 0 && item != null)
                    {
                        try
                        {
                            var start = checked((int)item.Start()); var length = checked((int)item.Length());
                            if (length <= 0 || start < 0 || start > word.Length - length) continue;
                            var suggestions = checker.Suggest(word.Value.Substring(start,length));
                            try
                            {
                                var list = new List<string>(); var next = new string[1];
                                while (list.Count < 8 && suggestions.Next(1,next,IntPtr.Zero)==0) list.Add(next[0]);
                                issues.Add(new(word.Index + start,length,[..list]));
                            }
                            finally { Release(suggestions); }
                        }
                        finally { Release(item); }
                    }
                }
                finally { Release(errors); }
            }
            return new(issues,missing);
        }
        finally
        {
            foreach (var checker in checkers.Values) Release(checker);
            Release(factory);
        }
    }
    private static void Release(object item) => Marshal.ReleaseComObject(item);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
    private interface IFactory
    {
        IEnumString SupportedLanguages();
        [return:MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string tag);
        IChecker Create([MarshalAs(UnmanagedType.LPWStr)] string tag);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
    private interface IChecker
    {
        [return:MarshalAs(UnmanagedType.LPWStr)] string LanguageTag();
        IErrors Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
    private interface IErrors
    {
        [PreserveSig] int Next(out IError? error);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
    private interface IError
    {
        uint Start();
        uint Length();
    }
}
