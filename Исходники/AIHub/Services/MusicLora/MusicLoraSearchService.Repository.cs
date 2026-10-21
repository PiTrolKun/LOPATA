using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services.MusicLora;

internal sealed partial class MusicLoraSearchService
{
    private static async Task<int> InspectRepositoryAsync(string repo, string variation, string? exact, MusicLoraHubClient hub,
        Action<MusicLoraSearchItem> publish, Action<string> notice, CancellationToken token, MusicLoraSearchExclusions? excluded)
    {
        int rejected = 0; var now = DateTimeOffset.UtcNow;
        try {
                var detail = await hub.JsonAsync("/" + repo + "?blobs=true", token); var revision = S(detail, "sha");
                if (!Regex.IsMatch(revision, @"\A[a-f0-9]{40,64}\z")) throw new InvalidDataException("Music.Lora.Search.RevisionMissing");
                detail = await hub.JsonAsync("/" + repo + "/revision/" + revision + "?blobs=true", token);
                if (S(detail, "sha") != revision || detail.TryGetProperty("gated", out var gated) && gated.ValueKind is not JsonValueKind.False and not JsonValueKind.Null)
                    throw new InvalidDataException("Music.Lora.Search.PublicOnly");
                if (!detail.TryGetProperty("cardData", out var card)) throw new InvalidDataException("Music.Lora.Search.LicenseMissing");
                var license = S(card, "license"); if (!Licenses.Contains(license)) throw new InvalidDataException("Music.Lora.Search.LicenseMissing");
                // Reject other model families before reading their README and weight headers.
                if (!RepositoryCandidate(detail, variation, repo)) throw new InvalidDataException("Music.Lora.Search.BaseUnconfirmed");
                if (variation == MusicAceCatalog.Variation && (!card.TryGetProperty("base_model", out var exactBase)
                    || !Strings(exactBase).Any(b => b is "ACE-Step/acestep-v15-xl-turbo" or "ACE-Step/acestep-v15-xl-turbo-diffusers")))
                    throw new InvalidDataException("Music.Lora.Search.BaseUnconfirmed");
                var readme = await hub.TextAsync(repo, revision, "README.md", token);
                var siblings = detail.GetProperty("siblings").EnumerateArray().ToArray();
                var conditions = "Published license: " + license + "\nRepository: https://huggingface.co/" + repo + "/tree/" + revision + "\n\n" + readme;
                foreach (var f in siblings.Where(f => S(f, "rfilename") is "LICENSE" or "LICENSE.txt" or "LICENSE.md" or "COPYING").Take(2))
                    conditions += "\n\n" + S(f, "rfilename") + "\n" + await hub.TextAsync(repo, revision, S(f, "rfilename"), token);
                if (conditions.Length > 295000) throw new InvalidDataException("Music.Lora.Search.ResponseTooLarge");
                await Parallel.ForEachAsync(siblings.Where(f => S(f, "rfilename").EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => FilePriority(S(f, "rfilename"))).ThenBy(f => S(f, "rfilename"), StringComparer.Ordinal),
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token }, async (f, fileToken) => {
                    var path = S(f, "rfilename");
                    try {
                    var size = N(f, "size") ?? 0;
                    if (size is <= 8 or > 2000000000 || !f.TryGetProperty("lfs", out var lfs)) { Interlocked.Increment(ref rejected); return; }
                    var sha = S(lfs, "sha256"); if (!Regex.IsMatch(sha, @"\A[a-f0-9]{64}\z")) { Interlocked.Increment(ref rejected); return; }
                    if (excluded?.InspectWeight(sha) == false) return;
                    var url = MusicLoraHubClient.Resolve(repo, revision, path);
                    // Same immutable weights already have a reviewed recipe; show it once.
                    var known = MusicLoraCatalog.Bundled.FirstOrDefault(d => d.Variation == variation && d.Repository == repo
                        && d.Files.Any(a => a.Sha256 == sha));
                    if (known is not null) {
                        publish(new(known, known.DescriptionRu + "\n" + known.DescriptionEn, "", repo.Split('/')[0],
                            N(detail, "likes"), N(detail, "downloads"), now, known.Files.Select(a => a.SourceUrl).ToArray(), true));
                        return;
                    }
                        var h = await hub.HeaderAsync(url, size, fileToken);
                        var files = new List<ManagedModelArtifactFile>(); string format = "peft"; Dictionary<string, double> slots;
                        var trigger = MusicLoraTensorRules.Meta(h, "trigger"); var evidence = "";
                        if (variation == MusicAceCatalog.Variation) {
                            var bases = card.TryGetProperty("base_model", out var b) ? Strings(b) : [];
                            if (!bases.Any(b => b is "ACE-Step/acestep-v15-xl-turbo" or "ACE-Step/acestep-v15-xl-turbo-diffusers"))
                                throw new InvalidDataException("Music.Lora.Search.BaseUnconfirmed");
                            var slider = MusicLoraTensorRules.Meta(h, "slider");
                            if (slider.Length > 0) {
                                using var meta = JsonDocument.Parse(slider); var m = meta.RootElement;
                                if (S(m, "backbone") != "ace-turbo" || S(m, "targets") != "all" || !m.GetProperty("alpha").TryGetDouble(out var alpha)
                                    || !double.IsFinite(alpha) || alpha <= 0) throw new InvalidDataException("Music.Lora.Search.TensorMismatch");
                                MusicLoraTensorRules.Ace(h, true, m.GetProperty("rank").GetInt32()); format = "audio-sliders-peft-v1";
                                evidence = "Positive: " + S(m, "positive") + "\nNegative: " + S(m, "negative"); trigger = "";
                            } else {
                                var folder = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
                                var configPath = folder + "adapter_config.json";
                                if (Path.GetFileName(path) != "adapter_model.safetensors" || !siblings.Any(s => S(s, "rfilename") == configPath))
                                    throw new InvalidDataException("Music.Lora.Search.ConfigMissing");
                                var bytes = await hub.FileAsync(MusicLoraHubClient.Resolve(repo, revision, configPath), fileToken);
                                using var config = JsonDocument.Parse(bytes); var c = config.RootElement;
                                var baseName = S(c, "base_model_name_or_path");
                                if (S(c, "peft_type") != "LORA" || baseName is not ("ACE-Step/acestep-v15-xl-turbo" or "ACE-Step/acestep-v15-xl-turbo-diffusers")
                                    || S(c, "bias") != "none" || Bool(c, "use_dora") || Bool(c, "use_rslora") || Bool(c, "fan_in_fan_out") || Has(c, "modules_to_save")
                                    || Has(c, "rank_pattern") || Has(c, "alpha_pattern") || Has(c, "layer_replication") || Has(c, "layers_to_transform")
                                    || Has(c, "exclude_modules") || Has(c, "target_parameters")
                                    || !c.GetProperty("lora_alpha").TryGetDouble(out var alpha) || !double.IsFinite(alpha) || alpha <= 0)
                                    throw new InvalidDataException("Music.Lora.Search.BaseUnconfirmed");
                                MusicLoraTensorRules.Ace(h, false, c.GetProperty("r").GetInt32());
                                MusicLoraTensorRules.AceConfiguration(h, c);
                                files.Add(new() { RelativePath = "adapter_config.json", SourceUrl = MusicLoraHubClient.Resolve(repo, revision, configPath),
                                    SizeBytes = bytes.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
                            }
                            slots = new() { ["dit"] = 1 };
                        } else slots = MusicLoraTensorRules.Studio(h, card.TryGetProperty("base_model", out var studioBase)
                            && Strings(studioBase).Contains("m-a-p/YuE2-3B", StringComparer.Ordinal));
                        files.Add(new() { RelativePath = Path.GetFileName(path), SourceUrl = url, SizeBytes = size, Sha256 = sha });
                        var id = "found-" + MusicLoraSearchStore.Digest(repo + "\n" + revision + "\n" + path + "\n" + sha + "\n" + format)[..32];
                        var name = Path.GetFileNameWithoutExtension(path); var description = Limit(evidence.Length > 0 ? evidence : readme, 2500);
                        var tags = Regex.Matches(name + " " + evidence, @"[\p{L}\p{N}]{3,32}").Select(m => m.Value.ToLowerInvariant()).Distinct().Take(40).ToArray();
                        var d = new MusicLoraDefinition(id, name, name, description, description, variation, repo, revision, license,
                            Limit(trigger, 200), 0, 1, 1, tags, slots, files.ToArray(), format);
                        MusicLoraCatalog.ValidateDefinition(d);
                        var item = new MusicLoraSearchItem(d, Limit(evidence + "\n" + readme, 14000), conditions, repo.Split('/')[0],
                            N(detail, "likes"), N(detail, "downloads"), now, [url]);
                        publish(item);
                    } catch (Exception e) when (!fileToken.IsCancellationRequested && Skippable(e) && !RateLimited(e)) {
                        Interlocked.Increment(ref rejected); if (exact is not null) notice(path + " · " + e.Message);
                    }
                });
                if (rejected > 0) notice("Music.Lora.Search.Rejected|" + rejected);
                return 1;

        } catch (Exception e) when (!token.IsCancellationRequested && Skippable(e) && !RateLimited(e)) {
            // InvalidDataException derives from SystemException, not IOException.
            // A rejected repository is an expected search outcome, not a failed search.
            notice(repo + " · " + e.Message); return 0;
        }
    }
}
