using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

internal sealed partial class VideoH3WorkspaceControl
{
    private TextBlock _rawLabel = new();
    private System.Windows.Controls.ComboBox? _resolution;
    private System.Windows.Controls.Button _exportVideo = new(), _sendEditor = new();
    private void BuildSettings()
    {
        var building = _building; _building = true; _settings.Children.Clear();
        var basic = Group(_settings, "H3Basic", true);
        S.RunMode = "automatic";
        Preset(basic, "H3TargetSeconds", S.TargetSeconds, [0, 15, 30, 60, 90, 120, 180, 300, 600, 1800, 3600], v => S.TargetSeconds = v, "H3ProjectDuration"); Hint(basic, "H3TargetHint");
        Preset(basic, "H3ClipSeconds", S.ClipSeconds, [5, 8, 10, 12, 15], v => S.ClipSeconds = v); Hint(basic, "H3DurationHint");
        Pick(basic, "H3Aspect", S.Aspect, VideoH3WorkspaceState.Aspects, v => { S.Aspect = v; SetResolution(.98); }, true);
        var draft = new VideoH3Settings { Aspect = S.Aspect }; VideoH3WorkspaceState.Resolution(draft, .4);
        var native = new VideoH3Settings { Aspect = S.Aspect }; VideoH3WorkspaceState.Resolution(native, .98);
        var resolutions = new[] { (draft.Width, draft.Height), (native.Width, native.Height), (S.Width, S.Height) }.Distinct().ToArray();
        _resolution = Pick(basic, "H3Resolution", S.Width + " × " + S.Height, resolutions.Select(r => r.Item1 + " × " + r.Item2), v =>
        {
            var size = v.Split('×'); S.Width = int.Parse(size[0]); S.Height = int.Parse(size[1]);
            _drafts.Remove("H3Width"); _drafts.Remove("H3Height"); BuildSettings();
        }, true);
        for (var i = 0; i < resolutions.Length; i++)
        {
            var r = resolutions[i]; var id = r.Item1 + " × " + r.Item2;
            var caption = id + (r == (draft.Width, draft.Height) ? " · " + T("H3DraftResolution") : r == (native.Width, native.Height) ? " · " + T("H3NativeResolution") : "");
            _resolution.Items[i] = new Choice(id, caption);
        }
        _resolution.SelectedItem = _resolution.Items.OfType<Choice>().Single(c => c.Id == S.Width + " × " + S.Height);
        Hint(basic, "H3FpsHint");
        Preset(basic, "H3Steps", S.Steps, [8, 12, 16, 20, 24, 32, 40, 50], v => S.Steps = (int)v);
        Number(basic, "H3Seed", S.Seed, -1, 9007199254740991, v => S.Seed = (long)v, true); Hint(basic, "H3SeedHint");
        Pick(basic, "H3Audio", S.AudioMode, ["native", "mute"], v => S.AudioMode = v);
        Pick(basic, "H3ReferenceSize", S.ReferenceSize, ["match", "max"], v => S.ReferenceSize = v); Hint(basic, "H3ReferenceSizeHint");
        Preset(basic, "H3RetryCount", S.RetryCount, [1, 2, 3, 4, 5], v => S.RetryCount = (int)v);
        if (S.Turbo != "off" || !string.IsNullOrEmpty(S.ContentLoraFile))
        {
            basic.Children.Add(Label(T("H3LegacySettings")));
            basic.Children.Add(ActionButton("H3ResetLegacy", "ResetLegacy", () =>
            { S.Turbo = "off"; S.TurboFile = ""; S.ContentLoraFile = ""; BuildSettings(); Changed(); }));
        }
        var continuation = Group(_settings, "H3ContinuationGroup");
        Pick(continuation, "H3Continuation", S.Continuation, VideoH3WorkspaceState.Operations, v => S.Continuation = v);
        Pick(continuation, "H3PinMode", S.PinMode, ["masked", "guide", "both"], v => S.PinMode = v);
        Pick(continuation, "H3ContextFrames", S.ContextFrames.ToString(CultureInfo.InvariantCulture), ["5", "22", "39", "56", "90", "141"], v => S.ContextFrames = int.Parse(v, CultureInfo.InvariantCulture), true);
        Preset(continuation, "H3AudioContext", S.AudioContextFrames, [0, 5, 22, 39, 56, 90, 141, 240], v => S.AudioContextFrames = (int)v, "H3AsVideo");
        Preset(continuation, "H3MaskHold", S.MaskHold, [0, .1, .25, .5, .75, 1], v => S.MaskHold = v);
        Preset(continuation, "H3MaskRamp", S.MaskRampFrames, [0, 5, 17, 34, 51, 90, 141, 256], v => S.MaskRampFrames = (int)v); Hint(continuation, "H3ContextHint");
        var engine = Group(_settings, "H3EngineGroup");
        Pick(engine, "H3Sampler", S.Sampler, ["res_multistep", "euler"], v => S.Sampler = v, true);
        Pick(engine, "H3Scheduler", S.Scheduler, ["simple", "beta"], v => S.Scheduler = v, true);
        Preset(engine, "H3VideoShift", S.VideoShift, [1, 3, 6, 8, 10, 12, 16, 20, 30, 40], v => S.VideoShift = (int)v);
        Preset(engine, "H3AudioShift", S.AudioShift, [1, 2, 3, 6, 8, 12, 20, 40], v => S.AudioShift = (int)v);
        Pick(engine, "H3Attention", S.Attention, ["pytorch", "sage"], v => S.Attention = v, true);
        Check(engine, "H3Spectrum", S.Spectrum, v => S.Spectrum = v);
        Check(engine, "H3KitchenUnavailable", false, _ => { }, false);
        Check(engine, "H3SparseUnavailable", false, _ => { }, false); Hint(engine, "H3AccelerationHint");
        var memory = Group(_settings, "H3MemoryGroup");
        Pick(memory, "H3Memory", S.MemoryMode, ["auto", "gpu", "offload"], v => S.MemoryMode = v);
        Preset(memory, "H3Reserve", S.VramReserveGb, [0, 1, 2, 3, 4, 6, 8, 12, 16], v => S.VramReserveGb = v);
        Preset(memory, "H3Window", S.WindowSeconds, [1, 2, 3, 5, 8, 10, 15], v => S.WindowSeconds = v);
        Preset(memory, "H3Overlap", S.OverlapSeconds, [0, .25, .5, 1, 1.3, 2, 3, 5], v => S.OverlapSeconds = v);
        Preset(memory, "H3DecodeWindow", S.DecodeSeconds, [1, 2, 3, 5, 8, 10, 15], v => S.DecodeSeconds = v); Hint(memory, "H3MemoryHint");
        var upscale = Group(_settings, "H3UpscaleGroup");
        Check(upscale, "H3Upscale", S.Upscale, v => S.Upscale = v);
        Preset(upscale, "H3UpscaleSize", S.UpscaleMegapixels, [.5, 1, 2, 3, 4, 6, 8], v => S.UpscaleMegapixels = v);
        Preset(upscale, "H3Refine", S.RefineAmount, [0, .1, .2, .3, .5, .75, 1], v => S.RefineAmount = v);
        Preset(upscale, "H3RefineSteps", S.RefineSteps, [4, 8, 10, 12, 16, 20, 32, 50], v => S.RefineSteps = (int)v);
        Check(upscale, "H3KeepAudio", S.PreserveAudio, v => S.PreserveAudio = v);
        Check(upscale, "H3TemporalChunking", true, _ => { }, false); Hint(upscale, "H3UpscaleHint");

        _building = building;
    }
    private void SetResolution(double megapixels)
    {
        VideoH3WorkspaceState.Resolution(S, megapixels); _drafts.Remove("H3Width"); _drafts.Remove("H3Height");
        BuildSettings(); Changed();
    }
    private void BuildFiles()
    {
        var building = _building; _building = true; _files.Children.Clear();
        var heading = Label(T("H3ProjectGroup")); heading.FontWeight = FontWeights.SemiBold; _files.Children.Add(heading);
        _files.Children.Add(Label(_project.Title)); Hint(_files, "H3ProjectFormat");
        TextField(_files, "H3ExportName", S.ExportName, v => S.ExportName = v);
        QualityChoice(_files);
        Preset(_files, "H3OutputHeight", S.OutputHeight, [0, 720, 1080, 2160], v => S.OutputHeight = (int)v, "H3AsVideo"); Hint(_files, "H3ScaleHint");
        Hint(_files, "H3FutureFile");
        _files.Children.Add(ActionButton("Save", "Save", () => Request(SaveRequested)));
        _files.Children.Add(ActionButton("H3SaveAs", "SaveAs", () => Request(SaveAsRequested)));
        _files.Children.Add(ActionButton("H3ExportPreparation", "ExportPreparation", () => Request(ExportRequested)));
        _exportVideo = ActionButton("H3ExportVideo", "H3ExportVideo", () => _ = ExportVideoAsync()); _files.Children.Add(_exportVideo);
        _sendEditor = ActionButton("H3SendEditor", "H3SendEditor", () => { if (SelectedVideo is { } path) SendEditorRequested?.Invoke(path); }); _files.Children.Add(_sendEditor);
        RefreshResultActions();
        _rawLabel = Label("");
        _files.Children.Add(_rawLabel); RefreshRequestedSummary();
        _building = building;
    }
    private void QualityChoice(StackPanel panel)
    {
        var percent = VideoH3Quality.ToPercent(S.Crf);
        var values = new[] { 20, 40, 60, 80, 90, 100, percent }.Distinct().Order().ToArray();
        var choice = Pick(panel, "H3Quality", percent.ToString(CultureInfo.InvariantCulture),
            values.Select(v => v.ToString(CultureInfo.InvariantCulture)),
            v => S.Crf = VideoH3Quality.ToCrf(int.Parse(v, CultureInfo.InvariantCulture)), true);
        for (var i = 0; i < values.Length; i++) choice.Items[i] = new Choice(values[i].ToString(CultureInfo.InvariantCulture), values[i] + "%");
        choice.SelectedItem = choice.Items.OfType<Choice>().Single(c => c.Id == percent.ToString(CultureInfo.InvariantCulture));
        choice.ToolTip = T("H3QualityHint");
    }
    private void RefreshRequestedSummary()
    {
        _rawLabel.Text = T("H3RawDuration") + " " + (VideoH3WorkspaceState.RawFrames(S.ClipSeconds) / 24d).ToString("0.###", CultureInfo.CurrentCulture);
        _resultMode.Text = T(S.RunMode == "review" ? "H3EmptyFragments" : "H3EmptyWholeVideo");
        if (_resolution is not null)
        {
            var value = S.Width + " × " + S.Height;
            var building = _building; _building = true;
            if (!_resolution.Items.OfType<Choice>().Any(c => c.Id == value)) _resolution.Items.Add(new Choice(value, value));
            _resolution.SelectedItem = _resolution.Items.OfType<Choice>().Single(c => c.Id == value); _building = building;
        }
    }
}
