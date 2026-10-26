using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Services.VideoEditor;

internal static class VideoAnalysis
{
    public static Task<List<NarrationSegment>> DetectAsync(VideoAsset asset, string work, CancellationToken token) => VideoEventDetection.DetectAsync(asset, work, token);
    public static async Task WriteAsync(VideoProject p, VideoAsset asset, IReadOnlyList<NarrationSegment> segments, string brief, string work, Action<string>? status, CancellationToken token, ManagedModelLibraryStore? library = null)
    {
        var plans = segments.ToDictionary(n => n.Id, n => VideoAnalysisFrames.Plan(p, n));
        using var runtime = new OmniLlamaRuntimeService(library ?? new(), OmniLlamaProfile.Gamma);
        await runtime.PrepareAsync(s => status?.Invoke(s), null, token);
        foreach (var n in segments)
        {
            var answerPath = Path.Combine(work, n.Id + ".answer.json"); string answer;
            if (File.Exists(answerPath)) answer = await File.ReadAllTextAsync(answerPath, token);
            else
            {
                var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "Напиши короткую естественную русскую реплику диктора для этого фрагмента экранной записи. Только то, что действительно видно на кадрах; не выдумывай цель, успешное завершение, скачивание или скрытые действия. Если событие неясно, прямо укажи это в uncertainty. Дай не перечисление элементов интерфейса, а краткий рассказ о видимом действии. Длительность фрагмента " + VideoExport.N(n.End - n.Start) + " секунд. Реплика должна быть достаточно короткой для спокойной речи. Пользовательский замысел (не доказательство событий): " + brief + ". Ответ только JSON: {\"text\":\"реплика\",\"uncertainty\":\"сомнения или пусто\"}." } };
                foreach (var sample in plans[n.Id])
                {
                    var visibleAsset = p.Asset(sample.AssetId);
                    var frame = Path.Combine(work, n.Id + "-" + sample.AssetId + "-" + VideoExport.N(sample.TimelineTime) + ".png");
                    if (!File.Exists(frame)) await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-ss", VideoExport.N(sample.SourceTime), "-i", visibleAsset.Path, "-map", "0:" + visibleAsset.VideoStream, "-frames:v", "1", "-vf", "scale=1280:-1", frame], token);
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = "Кадр в " + VideoExport.N(sample.TimelineTime) + " с." });
                    content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(frame, token)) } });
                }
                var body = new JsonObject { ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } }, ["max_tokens"] = -1, ["temperature"] = .7, ["top_p"] = .8, ["top_k"] = 20, ["stream"] = true, ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } };
                using var http = new HttpClient { BaseAddress = runtime.Endpoint, Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(token); var result = await OmniLlamaProtocol.ReadAsync(stream, null, null, token, OmniLlamaProfile.Gamma);
                answer = result.Content; await File.WriteAllTextAsync(answerPath, answer, token);
            }
            var start = answer.IndexOf('{'); var end = answer.LastIndexOf('}'); if (start < 0 || end <= start) throw new InvalidDataException("VideoEditor.AnalysisInvalid");
            using var parsed = JsonDocument.Parse(answer[start..(end + 1)]);
            n.Text = parsed.RootElement.GetProperty("text").GetString() ?? "";
            if (parsed.RootElement.TryGetProperty("uncertainty", out var uncertainty) && !string.IsNullOrWhiteSpace(uncertainty.GetString())) n.Text += " [" + uncertainty.GetString() + "]";
            n.Stale = n.AudioAssetId is not null;
            n.Models = [new(runtime.ModelId, runtime.ModelId, runtime.ModelRevision, "video fragment analysis and narration draft")];
            status?.Invoke("VideoEditor.FragmentReady");
            VideoProjectStore.Save(p, Path.Combine(work, "result.lopataVideo"));
        }
    }
}
