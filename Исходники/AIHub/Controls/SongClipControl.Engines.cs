using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Globalization;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.SongClip;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private readonly WrapPanel _glitchOptions = new();
    private readonly WrapPanel _motionOptions = new();
    private readonly CheckBox _denoAccelerate = new();
    private TextBlock? _referenceHeading, _referenceUse, _promptHeading;
    private string _deviceSummary = "";
    private bool IsMotion => _project.Model == "h3_music_context";
    private bool IsDeno => IsMotion && _project.Settings.Motion.Mode == SongClipDeno.Mode;
    private string ReferenceHintKey => SongClipVariantCatalog.IsRetiredModel(_project.Model)?SongClipVariantCatalog.RetiredMessageKey
        : IsInfiniteTalk ? "SongClip.InfiniteReferenceHint" : !HasSceneReferences ? "SongClip.ReferenceHint" : IsGeek ? "SongClip.GeekReferenceHint"
        : SongClipModes.IsMontage(_loadedH3Mode) ? "SongClip.MontageReferenceHint" : "SongClip.MotionReferenceHint";

    private FrameworkElement BuildMotionOptions()
    {
        var title = Label("MotionParameters"); title.FontWeight = FontWeights.SemiBold;
        title.ToolTip = _l("SongClip.MotionHint"); _motionOptions.Children.Add(title);
        _motionOptions.Children.Add(BuildModeChoice());
        foreach (var (key, choice) in new[] { ("MotionSize", _motionSize), ("MotionSteps", _motionSteps) })
        {
            var option = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(4,0,12,0) };
            var label = Label(key); label.VerticalAlignment = VerticalAlignment.Center;
            option.Children.Add(label); choice.MinWidth = 100; choice.Margin = new(6,0,0,0);
            AutomationProperties.SetAutomationId(choice, "SongClip." + key);
            AutomationProperties.SetName(choice, _l("SongClip." + key));
            option.Children.Add(choice); _motionOptions.Children.Add(option);
        }
        foreach (var steps in new[] { 12,16,20,24,30,40 }) _motionSteps.Items.Add(steps);
        _motionOptions.Children.Add(BuildMontageDurationChoice());
        return _motionOptions;
    }

    private void LoadMotionSettings()
    {
        LoadModeSettings(); LoadGeekSettings(); LoadInfiniteTalkSettings();
    }

    private FrameworkElement BuildDenoOptions()
    {
        _denoAccelerate.Content = _l("SongClip.DenoAccelerate");
        _denoAccelerate.ToolTip = _l("SongClip.DenoAccelerateHint");
        _denoAccelerate.Margin = new(4, 4, 0, 8);
        _denoAccelerate.Padding = new(5, 3, 0, 3);
        _denoAccelerate.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _denoAccelerate.SetResourceReference(CheckBox.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(_denoAccelerate, "SongClip.DenoAccelerate");
        AutomationProperties.SetHelpText(_denoAccelerate, (string)_denoAccelerate.ToolTip);
        void Save(object sender, RoutedEventArgs e)
        {
            if (IsDeno && !_loading && !_busy && !OwnOperation) Checkpoint();
        }
        _denoAccelerate.Checked += Save;
        _denoAccelerate.Unchecked += Save;
        return _denoAccelerate;
    }

    private void CaptureMotionSettings()
    {
        CaptureModeSettings(); CaptureGeekSettings(); CaptureInfiniteTalkSettings();
    }

    private void RefreshEnginePresentation()
    {
        RefreshModelLabel();
        _geekOptions.Visibility = IsGeek ? Visibility.Visible : Visibility.Collapsed;
        _infiniteOptions.Visibility = IsInfiniteTalk&&!SongClipVariantCatalog.IsRetiredModel(_project.Model) ? Visibility.Visible : Visibility.Collapsed;
        _glitchOptions.Visibility = HasSceneReferences ? Visibility.Collapsed : Visibility.Visible;
        _motionOptions.Visibility = IsMotion && !IsDeno ? Visibility.Visible : Visibility.Collapsed;
        _denoAccelerate.Visibility = IsDeno ? Visibility.Visible : Visibility.Collapsed;
        RefreshModePresentation();
        var hint = _l(ReferenceHintKey);
        if (_referenceHeading is not null)
        {
            _referenceHeading.Text = _l(HasSceneReferences ? "SongClip.MotionReference" : "SongClip.Reference");
            _referenceHeading.ToolTip = hint;
        }
        if (_referenceUse is not null)
        {
            _referenceUse.Text = _l(IsInfiniteTalk ? "SongClip.InfiniteReferenceUse" : HasSceneReferences ? "SongClip.MotionReferenceUse" : "SongClip.ReferenceUse");
            _referenceUse.ToolTip = hint;
        }
        _referenceEmpty.ToolTip = hint;
        if (_promptHeading is not null) _promptHeading.Text = _l(HasSceneReferences ? "SongClip.MotionPrompt" : "SongClip.Prompt");
        if(SongClipVariantCatalog.IsRetiredModel(_project.Model))_stage=_status.Text=_l(SongClipVariantCatalog.RetiredMessageKey);
        else if(_stage==_l(SongClipVariantCatalog.RetiredMessageKey)) {
            _stage="";if(_status.Text==_l(SongClipVariantCatalog.RetiredMessageKey))_status.Text="";
        }
    }

    private static string EngineName(string model) => model switch
    {
        "glitchframe" => "Glitchframe",
        "h3_music_context" => "MiniMax H3",
        GeekatplayRunner.ModelId => "Geekatplay Music-to-Video",
        PiH3Components.ModelId => "pi-H3 DWD",
        InfiniteTalkRunner.ModelId => "InfiniteTalk · Wan2.1",
        _ => model
    };

    private string FormatEngineProgress(string message)
    {
        if (message is "Another background operation requires attention or is running." or "Music.Poetry.OtherOperation"
            or "Another model operation is active." or "Resume or discard the pending operation first.")
            return _l("SongClip.OtherOperation");
        if (message.StartsWith("SongClip.", StringComparison.Ordinal) || message.StartsWith("VideoPrep.", StringComparison.Ordinal)) return _l(message);
        if (!message.StartsWith('{')) return message;
        try
        {
            using var json = JsonDocument.Parse(message);
            var value = json.RootElement;
            if (!value.TryGetProperty("kind", out var kind)) return message;
            int Number(string key) => value.TryGetProperty(key, out var number) && number.TryGetInt32(out var result) ? result : 0;
            long? Bytes(string key) => value.TryGetProperty(key, out var number) && number.ValueKind == JsonValueKind.Number
                && number.TryGetInt64(out var result) && result >= 0 ? result : null;
            string Text(string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? "" : "";
            string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, _l("SongClip." + key), values);
            return kind.GetString() switch
            {
                "planner-download" => Format("PlannerDownloading", Text("model"),
                    ComponentCardViewModel.FormatBytes(Bytes("downloaded") ?? 0), ComponentCardViewModel.FormatBytes(Bytes("total") ?? 0)),
                "planner" => Format("PlanningBatch", Number("current"), Number("total"),
                    value.TryGetProperty("model", out var plannerModel) ? plannerModel.GetString() switch
                    {
                        "core" => "Qwen3-8B",
                        "executor" => "Qwen3.6-27B",
                        var actual => actual ?? ""
                    } : ""),
                "hardware" => _deviceSummary = value.TryGetProperty("device", out var device) && device.GetString() is { } name
                    ? name == "cpu" ? _l("SongClip.MotionDeviceCpu")
                        : string.Format(CultureInfo.CurrentCulture, _l("SongClip.MotionDeviceGpu"), name)
                    : _l("SongClip.Preparing"),
                "plan" => Format("MotionPlan", Number("total")),
                "analysis" => Format("MontageAnalysisReady", value.TryGetProperty("slots", out var slots)
                    && slots.ValueKind == JsonValueKind.Array ? slots.GetArrayLength() : 0),
                "space-estimate" when Bytes("availableBytes") is long free => Bytes("projectedRemainingShotBytes") is long remaining
                    ? Format("MontageSpaceEstimate", ComponentCardViewModel.FormatBytes(remaining), ComponentCardViewModel.FormatBytes(free))
                    : Format("MontageSpaceBeforeFirstShot", ComponentCardViewModel.FormatBytes(free)),
                "progress" => Format("MotionProgress", Number("index"), Number("total"), Number("step"), Number("steps")),
                "chunk-start" => Format("MotionPart", Number("index"), Number("total")),
                "chunk" => Format("MotionSaved", Number("index"), Number("total")),
                "cached" => value.TryGetProperty("windows", out _) ? Format("InfiniteCached", Number("windows"))
                    : Format("MotionCached", Number("index"), Number("total")),
                "complete" => _l("SongClip.MotionVerify"),
                "deno-pass" => Format("DenoPass", Number("index"), Number("width"), Number("height"), Number("steps")),
                "deno-accelerated" when Number("calls") > 0 => Format("DenoAcceleratedShot", Number("index")),
                "deno-finish-progress" => Format("DenoFinishProgress", Number("index"), Number("total")),
                "deno-finish-complete" => _l("SongClip.Finalizing"),
                "error" => value.TryGetProperty("message", out var error) ? error.GetString() ?? message : message,
                "stage" => value.TryGetProperty("stage", out var stage) ? stage.GetString() switch
                {
                    "infinitetalk-audio" => _l("SongClip.InfiniteAudio"),
                    "infinitetalk-text" => _l("SongClip.InfiniteText"),
                    "infinitetalk-window" => Format("InfiniteWindow", Number("index"), Number("total")),
                    "infinitetalk-assemble" => _l("SongClip.InfiniteAssemble"),
                    "geek-analysis" => _l("SongClip.GeekAnalysis"),
                    "geek-cards" => _l("SongClip.GeekCards"),
                    "geek-assembly" => _l("SongClip.GeekAssembly"),
                    "conditioning" => _l("SongClip.MotionConditioning"),
                    "render" => _l("SongClip.MotionRender"),
                    "mux" => _l("SongClip.MotionMux"),
                    "montage-analysis" => _l("SongClip.MontageAnalysis"),
                    "montage-assembly" => _l("SongClip.MontageAssembly"),
                    "deno-finish" => _l("SongClip.DenoFinishing"),
                    "deno-acceleration" => _l("SongClip.DenoAccelerationPreparing"),
                    "deno-attention" => value.TryGetProperty("message", out var attention) ? attention.GetString() ?? SongClipDeno.Title : SongClipDeno.Title,
                    _ => _l("SongClip.Preparing")
                } : _l("SongClip.Preparing"),
                _ => _l("SongClip.Preparing")
            };
        }
        catch (JsonException) { return message; }
    }

    private Task<SongClipResult> RunEngineAsync(SongClipAttempt attempt, Action<double,string> progress, CancellationToken token) =>
        attempt.Model switch
        {
            "glitchframe" => GlitchframeRunner.RunAsync(attempt, _store, progress, token),
            "h3_music_context" => H3MusicRunner.RunAsync(attempt, _store, progress, token),
            GeekatplayRunner.ModelId or PiH3Components.ModelId => GeekatplayRunner.RunAsync(attempt, _store, progress, token),
            InfiniteTalkRunner.ModelId => InfiniteTalkRunner.RunAsync(attempt, _store, progress, token),
            _ => throw new InvalidOperationException(_l("SongClip.Pending"))
        };
}
