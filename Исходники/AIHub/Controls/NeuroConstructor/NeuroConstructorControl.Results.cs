using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.NeuroConstructor;

namespace AIHub.Controls;

public partial class NeuroConstructorControl
{
    private (string Path, DateTime Written)? _reportStamp;
    private sealed record ReviewRow(string Text, string File, int Correct, int Before, int After, string Route, int? AudioCalls)
    {
        public override string ToString() => Text + " · " + File;
    }
    private void RefreshStatus()
    {
        if (_project is not { } p) return;
        var s = NeuroService.Status(p);
        var running = s.Status is "running" or "pausing" or "waiting" || _waitingForRun.Contains(p.Id) && s.Status != "paused";
        var paused = s.Status == "paused";
        BuildPanel.IsEnabled = !_working;
        EditSettings.IsEnabled = !_working && !running && !paused;
        Mark.IsEnabled = Quadrants.IsEnabled = !_working && !running && !paused;
        Predict.IsEnabled = Check.IsEnabled = !_working && !running;
        Train.IsEnabled = EditSettings.IsEnabled && p.Channel != "feedback";
        Evaluate.IsEnabled = EditSettings.IsEnabled && _canEvaluate;
        Evaluate.ToolTip = SettingsHint.Text;
        Pause.IsEnabled = !_working && (s.Status is "running" or "waiting");
        Resume.IsEnabled = paused && !_working;
        Cancel.IsEnabled = !_working && (running || paused);
        Export.IsEnabled = !_working && !running && !paused;
        var status = T("State." + s.Status);
        if (status.StartsWith("Neuro.", StringComparison.Ordinal)) status = s.Status;
        ProgressText.Text = string.Join(" · ", new[] { status, StageLabel(s.Stage), Explain(s.Message) }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        Progress.IsIndeterminate = running && s.Total <= 0;
        Progress.Maximum = Math.Max(1,s.Total);
        Progress.Value = Math.Clamp(s.Step,0,Math.Max(1,s.Total));
        Timing.Text = s.Status == "ready" ? "" : string.Join(" · ", new[] {
            s.Total > 0 ? T("Steps", s.Step, s.Total) : null,
            T("Elapsed", Duration(s.ElapsedSeconds)),
            s.RemainingSeconds is { } left ? T("Remaining", Duration(left)) : running ? T("Estimating") : null
        }.Where(x => x is not null));
        ReadReport(p);
    }
    internal string Explain(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var line = message.Trim();
        if (line.Contains("Traceback (most recent call last)", StringComparison.Ordinal))
            line = line.Split('\n',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[^1];
        if (line.StartsWith("ValueError: ",StringComparison.Ordinal)) line = line["ValueError: ".Length..];
        return line.StartsWith("Neuro.",StringComparison.Ordinal) ? T(line["Neuro.".Length..]) : line;
    }
    internal string StageLabel(string? label)
    {
        var key = label switch
        {
            "Подготовка" => "Preparing", "Подготовка моделей" => "PreparingModels",
            "Проверка учебных и контрольных примеров" => "CheckingExamples", "Подготовка примеров" => "PreparingExamples",
            "Одновременный запуск исходных моделей и проверка ответов" => "Baseline",
            "Проверка обученной связи" => "CheckingLink", "Сохранение результата" => "SavingRun",
            "Сравнение способов передачи подсказки и выбора моделей" => "CheckingPolicies",
            "Готово" => "Done", "На паузе" => "State.paused", "Отменено" => "State.canceled",
            "Не удалось завершить операцию" => "State.failed", _ => null
        };
        if (key is not null) return T(key);
        var sampleProgress = System.Text.RegularExpressions.Regex.Match(label ?? "",
            @"^(Подготовка примеров|Проверка исходных моделей) · (\d+) из (\d+)$");
        if (sampleProgress.Success)
        {
            var stage = sampleProgress.Groups[1].Value == "Подготовка примеров" ? "PreparingExamples" : "Baseline";
            return $"{T(stage)} · {sampleProgress.Groups[2].Value}/{sampleProgress.Groups[3].Value}";
        }
        if (label?.StartsWith("Обучение · попытка ",StringComparison.Ordinal) == true)
        {
            var digits = System.Text.RegularExpressions.Regex.Matches(label,@"\d+");
            if (digits.Count == 4) return T("FitStep",digits[0].Value,digits[1].Value,digits[2].Value,digits[3].Value);
        }
        return Explain(label);
    }
    internal string Explain(Exception error) => error switch
    {
        KeyNotFoundException or JsonException => T("PackageData"),
        FileNotFoundException missing when missing.FileName is { Length: >0 } name => T("FileMissing",name),
        _ => Explain(error.Message)
    };
    internal static string Duration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString("g");
    private void ReadReport(NeuroProject p)
    {
        var path = NeuroService.ReportPath(p);
        if (!File.Exists(path)) return;
        var stamp = (path, File.GetLastWriteTimeUtc(path));
        if (_reportStamp == stamp) return;
        using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(file);
        var r = doc.RootElement;
        string accuracy = T("Accuracy", Int(r,"baselineCorrect"), Int(r,"correct"), Int(r,"total"));
        var total = Int(r,"total");
        var current = NeuroService.ReportMatches(p);
        ReportState.Text = !current ? T("PriorReport") : r.TryGetProperty("eligible", out var eligible) && eligible.ValueKind == JsonValueKind.True
            ? T("QualityReached") : T("QualityMissed");
        var details = new List<string>();
        if (r.TryGetProperty("controls", out var controls))
            details.Add(T("ControlScores",Int(controls,"disabled"), Int(controls,"shuffled"),total));
        if (r.TryGetProperty("transfers",out var transfers))
            foreach (var (key,label) in new[]{("toDevice","ToDevice"),("toCpu","ToCpu")})
                if (transfers.TryGetProperty(key,out var direction))
                    details.Add(T(label,Decimal(direction,"bytes") ?? 0,Decimal(direction,"copies") ?? 0));
        if (Decimal(r,"baselineCorrectPerSecond") is double nativeRate && Decimal(r,"correctPerSecond") is double rate)
            details.Add(T("Throughput",nativeRate,rate));
        if (r.TryGetProperty("policyComparisons",out var policies) && policies.ValueKind == JsonValueKind.Array)
            foreach (var policy in policies.EnumerateArray())
            {
                var key = String(policy,"name") switch
                {
                    "continuous-always" => "continuous", "byte-always" => "byte", "byte-changed" => "cached",
                    "routing-input" => "needed", "routing-always" => "always", "routing-learned" => "learned", _ => null
                };
                if (key is null || Decimal(policy,"inputs") is not > 0) continue;
                var inputs = Int(policy,"inputs");
                details.Add(T("PolicyResult", T("Option."+key), Int(policy,"correct"), inputs,
                    1000 * (Decimal(policy,"seconds") ?? 0) / inputs, Int(policy,"routeErrors")));
            }
        ControlScores.Text = string.Join(Environment.NewLine,details);
        var before = Decimal(r,"baselineSeconds"); var after = Decimal(r,"fusedSeconds");
        Comparison.Text = before is > 0 && after is > 0 && total > 0
            ? accuracy + Environment.NewLine + T("Latency", 1000 * before.Value / total, 1000 * after.Value / total)
            : accuracy;
        ResourcesUsed.Text = string.Join(" · ", new[] {
            Metric(r,"transferredBytes","Traffic",1), Metric(r,"peakRamBytes","Ram",1048576),
            Metric(r,"peakVramBytes","Vram",1048576)
        }.Where(x => x is not null));
        Review.ItemsSource = r.TryGetProperty("rows",out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(row => new ReviewRow(String(row,"text"),Path.GetFileName(String(row,"image")),
                Int(row,"slot"),Int(row,"baseline"),Int(row,"predicted"),String(row,"route"),
                row.TryGetProperty("calls",out var calls) && calls.TryGetProperty("audio",out var audio) ? audio.GetInt32() : null)).ToArray() : [];
        Review.SelectedIndex = Review.Items.Count > 0 ? 0 : -1;
        Results.Visibility = Visibility.Visible;
        RefreshReadiness(p);
        _reportStamp = stamp;
    }
    private static double? Decimal(JsonElement e,string key) =>
        e.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static int Int(JsonElement e,string key) => e.GetProperty(key).GetInt32();
    private static string String(JsonElement e,string key) => e.TryGetProperty(key,out var value) ? value.ToString() : "";
    private string? Metric(JsonElement e,string key,string label,int divisor) =>
        Decimal(e,key) is { } n ? T(label,n / divisor) : null;
    private void ReviewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Review.SelectedItem is not ReviewRow r) { Reviewed.Text = ""; return; }
        Reviewed.Text = T("ReviewRow", T("Slot"+r.Before), T("Slot"+r.After), T("Slot"+r.Correct));
        if (r.AudioCalls is { } calls && r.Route is "pair" or "trio")
            Reviewed.Text += " " + T("ActualRoute",T("Option."+r.Route),calls);
    }
    private void OpenReport(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (_project is not { } p) return;
        OpenFolder(Path.GetDirectoryName(NeuroService.ReportPath(p))!);
    });
    private void OpenRuntime(object sender, RoutedEventArgs e) => Try(() =>
    {
        var folder = NeuroRuntime.DirectoryPath;
        if (!File.Exists(Path.Combine(folder,"README.md"))) throw new FileNotFoundException(T("Runtime"));
        OpenFolder(folder);
        PortableResult.Text = T("ExternalHint");
    });
    internal static void OpenFolder(string folder)
    {
        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(folder);
        System.Diagnostics.Process.Start(start)?.Dispose();
    }
    private async Task Training(bool resume, bool evaluate = false)
    {
        if (_project is not { } p || _working) return;
        var first = _waitingForRun.Add(p.Id);
        if (!first && !resume) return;
        try
        {
            if (!resume) SaveSettings();
            Notice.Text = "";
            RefreshStatus();
            if (resume) await NeuroService.ResumeAsync(p);
            else if (evaluate) await NeuroService.EvaluateAsync(p);
            else await NeuroService.TrainAsync(p);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (_project?.Id == p.Id) Notice.Text = Explain(e); }
        finally { if (first) _waitingForRun.Remove(p.Id); Try(RefreshStatus); }
    }
    private async void TrainClicked(object sender, RoutedEventArgs e) => await Training(false);
    private async void EvaluateClicked(object sender, RoutedEventArgs e) => await Training(false, evaluate: true);
    private async void ResumeClicked(object sender, RoutedEventArgs e) => await Training(true);
    private async void PauseClicked(object sender, RoutedEventArgs e)
    {
        if (_project is { } p) await Perform(() => NeuroService.PauseAsync(p));
    }
    private async void CancelClicked(object sender, RoutedEventArgs e)
    {
        if (_project is { } p && System.Windows.MessageBox.Show(Window.GetWindow(this), T("ConfirmCancel"), T("Cancel"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await Perform(() => NeuroService.CancelAsync(p));
    }
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (_project is not { } p) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = T("Export"), Filter = T("ModelFilter"), DefaultExt = ".lopatanet",
            FileName = "LOPATA-" + p.Composition + ".lopatanet"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await Perform(async () =>
        {
            SaveSettings();
            await NeuroService.ExportAsync(p, dialog.FileName);
            _lastPackage = dialog.FileName;
            PortableResult.Text = T("Exported", _lastPackage);
            Notice.Text = "";
        }, "Exporting");
    }
    private async void CheckClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("ModelFilter"), Title = T("Check") };
        if (_lastPackage is { } path && File.Exists(path)) { dialog.FileName = Path.GetFileName(path); dialog.InitialDirectory = Path.GetDirectoryName(path); }
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await Perform(async () =>
        {
            var output = await NeuroService.CheckAsync(dialog.FileName, InferenceDevice,projectId:_project?.Id);
            using var doc = JsonDocument.Parse(output);
            PortableResult.Text = T("CheckResult", Path.GetFileName(dialog.FileName), Int(doc.RootElement,"matched"), Int(doc.RootElement,"total"));
            _lastPackage = dialog.FileName;
            Notice.Text = "";
        }, "Checking");
    }
}
