using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal static class SongClipPlannerRecovery
{
    // Start from the previous working batch size, then use the measured fit
    // and validation outcomes to grow or shrink on each project.
    internal const int InitialBatch = 6;
    internal const string RepairInstruction = "SongClip.RepairInstruction";

    internal static async Task<(T Plan, int AcceptedCount)> BatchAsync<T>(int count,
        Func<int, int, string?, Task<T>> generate, Action<double, string> report, CancellationToken token,
        Func<T, int>? acceptedCount = null)
    {
        var retry = 0;
        var pass = 0;
        string? guidance = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var plan = await generate(count, ++pass, guidance);
                var accepted = acceptedCount?.Invoke(plan) ?? count;
                if (accepted < count) report(0, "SongClip.PlanSmallerBatch");
                return (plan, accepted);
            }
            catch (VideoStructuredContextException) when (count > 1)
            {
                count = Math.Max(1, count / 2);
                retry = 0;
                guidance = null;
                report(0, "SongClip.PlanSmallerBatch");
            }
            catch (Exception error) when (IsRecoverableReply(error) && (retry == 0 || count > 1))
            {
                if (retry++ == 0)
                {
                    guidance = error.Message switch
                    {
                        "VideoPrep.AiIncomplete" => "The previous JSON was incomplete. Use concise English visual descriptions and close the full JSON response.",
                        "VideoPrep.AiTimeout" => "Return the complete JSON promptly with concise English descriptions.",
                        "SongClip.PlannerTransportLost" => "Return the complete JSON for the supplied inputs.",
                        _ => "The previous answer violated the schema or scene requirements. Check exact indices and times, reference indices and roles, first shot and transitions, and copy the accepted style verbatim."
                    };
                    if (error.Data[RepairInstruction] is string instruction) guidance += " " + instruction;
                    report(0, "SongClip.PlanRepairing");
                }
                else
                {
                    count = Math.Max(1, count / 2);
                    retry = 0;
                    guidance = null;
                    report(0, "SongClip.PlanSmallerBatch");
                }
            }
        }
    }

    internal static T PreserveValidPrefix<T>(string raw, string field, int expected, Func<string, int, T> validate, bool incomplete = false)
    {
        if (incomplete)
        {
            var partial = ReadInterruptedRows(raw, field);
            if (partial?[field] is not JsonArray { Count: > 0 } rows || rows.Count > expected)
                throw new InvalidDataException("VideoPrep.AiIncomplete");
            expected = rows.Count;
            raw = partial.ToJsonString();
        }
        try { return validate(raw, expected); }
        catch (InvalidDataException invalid) when (expected > 1 && IsRecoverableReply(invalid))
        {
            JsonObject? root;
            try { root = JsonNode.Parse(raw) as JsonObject; }
            catch (JsonException) { throw invalid; }
            if (root?[field] is JsonArray rows)
            {
                while (rows.Count >= expected) rows.RemoveAt(rows.Count - 1);
                while (rows.Count > 0)
                {
                    try { return validate(root.ToJsonString(), rows.Count); }
                    catch (InvalidDataException partial) when (IsRecoverableReply(partial)) { rows.RemoveAt(rows.Count - 1); }
                }
            }
            throw;
        }
    }

    private static JsonObject? ReadInterruptedRows(string text, string field)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), isFinalBlock: false, state: default);
        string? style = null;
        var rows = new JsonArray();
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read()) break;
                if (name == "globalStyle" && style is null && reader.TokenType == JsonTokenType.String)
                    style = reader.GetString();
                else if (name == field && style is not null && reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject && JsonDocument.TryParseValue(ref reader, out var value))
                    {
                        using (value) rows.Add(JsonNode.Parse(value.RootElement.GetRawText()));
                    }
                    break;
                }
                else return null;
            }
        }
        catch (JsonException) when (rows.Count > 0) { }
        catch (JsonException) { return null; }
        return style is null ? null : new JsonObject { ["globalStyle"] = style, [field] = rows };
    }

    internal static int Reserve(string input)
    {
        using var doc = JsonDocument.Parse(input);
        var value = doc.RootElement;
        if (value.TryGetProperty("slots", out var slots) || value.TryGetProperty("scenes", out slots))
            return 384 + (480 * slots.GetArrayLength());
        if (value.TryGetProperty("references", out var references))
            return 384 + references.EnumerateArray().Sum(reference =>
                240 + Math.Min(3500, (reference.TryGetProperty("description", out var description) ? description.GetString()?.Length ?? 0 : 0) / 3));
        return 3072;
    }

    internal static bool IsSmallRequest(string input)
    {
        using var doc = JsonDocument.Parse(input);
        var value = doc.RootElement;
        if (value.TryGetProperty("slots", out var slots) || value.TryGetProperty("scenes", out slots))
            return slots.GetArrayLength() == 1;
        if (value.TryGetProperty("references", out var references)) return references.GetArrayLength() <= 1;
        return true;
    }

    internal static bool IsRecoverableReply(Exception error) =>
        error is InvalidDataException && error.Message is
            SongClipVisualProtocol.InvalidReply or "VideoPrep.AiInvalidReply" or "VideoPrep.AiIncomplete"
        || error is TimeoutException && error.Message == "VideoPrep.AiTimeout"
        || error is IOException && error.Message == "SongClip.PlannerTransportLost";
}
