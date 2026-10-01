using System.Diagnostics;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public delegate Task<string> FinancialGenerate(DebugModelInfo model, string system, string prompt, int maxTokens, CancellationToken token);
public sealed class FinancialAnalysisPipeline(FinancialGenerate generate)
{
    public async Task RunAsync(FinancialRunStore store, Action<string, int>? progress, CancellationToken token)
    {
        var run = store.Load(); var calculation = FinancialCalculator.Calculate(run.Input);
        var results = new List<FinancialStageResult>();
        foreach (var stage in FinancialAnalysisPlan.Stages)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(stage.Id, results.Count);
            var result = store.ReadCurrentStage(stage.Id);
            if (result is null)
            {
                var clock = Stopwatch.StartNew();
                var text = await generate(run.Model, SystemPrompt(stage, run.Input.Language), BuildPrompt(stage, run.Input, calculation, results), stage.MaxTokens, token);
                token.ThrowIfCancellationRequested();
                text = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Contains("<think>", StringComparison.Ordinal))
                    throw new InvalidDataException("No completed analytical answer.");
                var issues = FinancialReplyQuality.Issues(text, run.Input.Language, run.Input.Unit);
                if (issues.Count > 0)
                {
                    text = await generate(run.Model, SystemPrompt(stage, run.Input.Language).Replace("/think", "/no_think", StringComparison.Ordinal),
                        FinancialAnalysisPrompts.Correct(stage, run.Input, text, issues), stage.MaxTokens, token);
                    token.ThrowIfCancellationRequested();
                    text = ImageAnalysisKimiRequestBuilder.ExtractFinalAnswer(text);
                    if (FinancialReplyQuality.Issues(text, run.Input.Language, run.Input.Unit).Count > 0) throw new BackgroundOperationWaitingException("Finance.InvalidModelReply");
                }
                result = new(stage.Id, run.Revision, run.Model.Path, run.Model.Name, text, clock.Elapsed.TotalSeconds, DateTimeOffset.UtcNow, FinancialAnalysisPlan.AnalysisVersion);
                store.SaveStage(result);
            }
            results.Add(result);
            if (stage.Report) FinancialReportExporter.Export(store, stage.Id, calculation);
        }
        progress?.Invoke("done", results.Count);
    }
    public static string SystemPrompt(string language) => FinancialAnalysisPrompts.System(language);
    public static string SystemPrompt(FinancialAnalysisStage stage, string language) => FinancialAnalysisPrompts.System(stage, language);
    public static string BuildPrompt(FinancialAnalysisStage stage, FinancialInput input, FinancialCalculation calculation, IReadOnlyList<FinancialStageResult> results) =>
        FinancialAnalysisPrompts.Build(stage, input, calculation, results);
}
