using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AIHub.Services.OfficeReader;

internal static class ReaderBrowserPdf
{
    // CSS named pages retain mixed portrait/landscape section sizes. The stream
    // avoids copying a large PDF through one base64 JSON response.
    internal static async Task WriteAsync(CoreWebView2 browser, string target, CancellationToken token)
    {
        using var printed = JsonDocument.Parse(await browser.CallDevToolsProtocolMethodAsync("Page.printToPDF",
            "{\"printBackground\":true,\"displayHeaderFooter\":false,\"preferCSSPageSize\":true,\"marginTop\":0,\"marginBottom\":0,\"marginLeft\":0,\"marginRight\":0,\"transferMode\":\"ReturnAsStream\"}").WaitAsync(token));
        var handle = printed.RootElement.GetProperty("stream").GetString()!;
        try
        {
            await using var output = File.Open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var chunk = JsonDocument.Parse(await browser.CallDevToolsProtocolMethodAsync("IO.read",
                    JsonSerializer.Serialize(new { handle, size = 262144 })).WaitAsync(token));
                var root = chunk.RootElement;
                var data = root.GetProperty("data").GetString()!;
                if (data.Length > 0 && (!root.TryGetProperty("base64Encoded", out var encoded) || !encoded.GetBoolean()))
                    throw new InvalidDataException("Reader.RenderFailed");
                await output.WriteAsync(Convert.FromBase64String(data), token);
                if (root.TryGetProperty("eof", out var eof) && eof.GetBoolean()) break;
            }
            if (output.Length == 0) throw new InvalidDataException("Reader.RenderFailed");
        }
        finally
        {
            try { await browser.CallDevToolsProtocolMethodAsync("IO.close", JsonSerializer.Serialize(new { handle })).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (token.IsCancellationRequested) { }
        }
    }
}
