using System.Diagnostics;
using System.IO;

namespace AIHub.Controls.SpeechAssets;

internal sealed partial class SpeechAssetsControl
{
    internal static string ExamplesDirectory => Path.Combine(AppContext.BaseDirectory, "Examples", "SpeechAssets");
    private void OpenExamples()
    {
        if (!Directory.Exists(ExamplesDirectory)) throw new FileNotFoundException(T("ExamplesMissing"));
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { ExamplesDirectory } });
    }
}
