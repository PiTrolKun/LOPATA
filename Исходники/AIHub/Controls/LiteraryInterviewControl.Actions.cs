using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryInterviewControl
{
    private string CurrentQuestion() => S.AdaptiveAnswer ? S.AdaptiveQuestion : _l(LiteraryInterviewCatalog.Get(S.Step).Key);
    private async Task SubmitAsync()
    {
        if (!ValidInput()) return;
        S.AdaptiveAnswer = false;
        var q = LiteraryInterviewCatalog.Get(S.Step);
        var text = S.Inputs.GetValueOrDefault(S.Step, "");
        var hasCustomGenre = q.Input == InterviewInput.Genres && !string.IsNullOrWhiteSpace(text);
        if (q.Input == InterviewInput.Folder) { S.Parent = text.Length == 0 ? S.Parent : text; text = S.Parent; }
        if (q.Input == InterviewInput.Name)
        {
            S.ProjectName = text;
            if (_session.Root is null) _session.Reserve();
        }
        if (_session.Root is null && S.Step > 2) throw new IOException("Literary.Rag.LocationFirst");
        if (q.Input == InterviewInput.Materials)
        {
            if (S.Materials.Count > 0 && _sourceIndex?.Ready != true) await PrepareMaterialsAsync();
            text = S.Materials.Count == 0 ? T("NoMaterials") : string.Join("\n",S.Materials.Select(Path.GetFileName));
        }
        if (q.Input == InterviewInput.Genres)
            text = string.Join(", ",S.Selections.GetValueOrDefault(6,"").Split('|',StringSplitOptions.RemoveEmptyEntries).Select(id => T("Genre." + id))) + (text.Length > 0 ? "\n" + text : "");
        if (q.Input == InterviewInput.Route) text = string.Join("\n",S.Route.Where(r => r.Title.Length > 0 || r.Description.Length > 0).Select(r => $"{r.Number}. {r.Title}\n{r.Description}"));
        if (q.Input == InterviewInput.Title && string.IsNullOrWhiteSpace(text)) text = _l("Literary.Create.Untitled");
        S.Inputs[S.Step] = text;
        if (!Save()) return;
        if (q.Creative || hasCustomGenre)
        {
            if (!S.AiDisabled) await InferAsync(false);
            else { _session.Propose(text,false); Save(); }
        }
        else await AcceptDirectAsync(text, q.Input == InterviewInput.Route ? "plan" : "statement");
    }
    private async Task AcceptDirectAsync(string text, string meaning)
    {
        if (_session.Root is null && S.Step > 2) throw new IOException("Literary.Rag.LocationFirst");
        if (S.Records.Any(r => r.Step == S.Step && !r.Adaptive)) return;
        S.Inputs[S.Step] = text; S.AdaptiveAnswer = false;
        _session.Propose(text,false,allowEmpty:true);
        _session.Confirm(_l(LiteraryInterviewCatalog.Get(S.Step).Key),text,meaning);
        if (Save()) await NextAdaptiveAsync();
    }
    private async Task NextAdaptiveAsync()
    {
        if (S.Stage == InterviewStage.Adaptive && !S.AiDisabled && S.AdaptiveQuestion.Length == 0) await InferAsync(true);
    }
    private async Task InferAsync(bool ask)
    {
        if (S.AiDisabled) return;
        if (ask && (S.Stage != InterviewStage.Adaptive || !_session.TopicMinimumComplete())) throw new InvalidOperationException();
        if (_session.Root is null) throw new IOException("Literary.Rag.LocationFirst");
        var generation = ++_generation;
        using var cancellation = new CancellationTokenSource(); _cancel = cancellation;
        _busy = true; S.InFlight = true; _progress.IsIndeterminate = true;
        _session.Journal(ask ? "question_requested" : "understanding_requested", CurrentQuestion());
        if (!Save()) { _busy=false; S.InFlight=false; _cancel=null; return; }
        Render();
        try
        {
            _runtime ??= new LiteraryChatRuntime(_session.Root,preparing:true);
            var name = "";
            if (File.Exists(AppDataPaths.UserProfilePath))
            {
                try { name = JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(AppDataPaths.UserProfilePath))?.DisplayName ?? ""; }
                catch (Exception ex) when (ex is IOException or JsonException) { _session.Journal("profile_unavailable",ex.Message); }
            }
            if (!ask && !S.Asked.Any(x => x.Text == CurrentQuestion())) S.Asked.Add(new(LiteraryInterviewCatalog.Get(S.Step).Topic,CurrentQuestion()));
            var messages = LiteraryInterviewPrompts.Build(S,ask,CurrentQuestion(),T("Topic" + LiteraryInterviewCatalog.Get(S.Step).Topic),name);
            void Budget(int count) => Dispatcher.Invoke(() =>
            { if (generation == _generation) { S.PromptTokens=count; UpdateStatus(); } });
            var result = ask
                ? await LiteraryInterviewQuestions.AskAsync(S, _session.Root, T("Topic" + LiteraryInterviewCatalog.Get(S.Step).Topic), name,
                    (request, schema) => _runtime.InterviewAsync(request, Budget, cancellation.Token, schema),
                    (action, detail) => _session.Journal(action, detail))
                : await _runtime.InterviewAsync(messages, Budget, cancellation.Token);
            if (generation != _generation || _closing) return;
            if (ask)
            {
                if (S.Asked.Any(q => q.Text.Trim() == result.Trim())) throw new InvalidDataException(T("RepeatedQuestion"));
                S.AdaptiveQuestion=result; S.AdaptiveInput="";
                S.Asked.Add(new(LiteraryInterviewCatalog.Get(S.Step).Topic,result)); _session.Journal("question",result);
            }
            else _session.Propose(result,S.AdaptiveAnswer);
        }
        catch (ImageAnalysisContextExhaustedException ex) when (!ex.OutputTruncated)
        {
            S.AiDisabled=true; S.LimitNoticePending=true; _session.Journal("context_exhausted");
            if (ask) { S.Stage=InterviewStage.Adaptive; _session.EndTopic(); }
            else _session.Propose(S.Correction.Length > 0 ? S.Correction : S.AdaptiveAnswer ? S.AdaptiveInput : S.Inputs.GetValueOrDefault(S.Step,""),S.AdaptiveAnswer);
            Save(); ShowLimitNotice();
        }
        finally
        {
            S.InFlight=false; _busy=false; _progress.IsIndeterminate=false;
            if (_cancel == cancellation) _cancel=null;
            Save();
        }
    }
    private void ShowLimitNotice()
    {
        if (!S.LimitNoticePending) return;
        System.Windows.MessageBox.Show(Window.GetWindow(this),T("LimitNotice"),T("Title"),MessageBoxButton.OK,MessageBoxImage.Information);
        S.LimitNoticePending=false; Save();
    }
    private async Task CreateAsync()
    {
        if (!_session.Complete || _session.Reservation is null || !Save()) return;
        if (S.Materials.Count > 0 && _sourceIndex?.Ready != true) await PrepareMaterialsAsync();
        var project = LiteraryInterviewPrompts.Project(S,_l("Literary.Create.Untitled"));
        _savingProject=true; _busy=true; Render();
        try
        {
            var index = _sourceIndex;
            index?.SetDestination(_session.Root!);
            var positive = S.InitialPositive ?? "";
            var negative = S.InitialNegative ?? "";
            // A large approved answer remains in the complete brief, never silently truncated.
            if (positive.Length + negative.Length > LiteraryPlotAnchorStore.MaxCharacters) throw new InvalidDataException(_l("Literary.MemorySetup.AnchorTooLong"));
            var materials = index?.Sources.ToArray() ?? [];
            var entry = await Task.Run(() => _store.CreateReserved(_session.Reservation,project,materials,
                index is null ? null : index.CopyInto, root =>
                {
                    var layout = new LiteraryProjectLayout(root);
                    foreach (var role in new[] { LiteraryChatProfile.Writer,LiteraryChatProfile.Advisor })
                    {
                        var anchors = new LiteraryPlotAnchorStore(layout,role);
                        anchors.Save(positive,negative,anchors.Load().Revision);
                    }
                    var folder = layout.EnsureFolder("Preparation");
                    LiteraryChapterFiles.Write(Path.Combine(folder,"confirmed-brief.json"),project.CreationBrief);
                    LiteraryChapterFiles.Write(Path.Combine(folder,"jelly-directions.json"),JsonSerializer.Serialize(S.Records.Where(r => r.Step is >= 15 and <= 22),LiteraryInterviewSession.Json));
                }));
            index?.Commit();
            if (index is not null) { await index.DisposeAsync(); _sourceIndex=null; }
            S.Finished=true; Save();
            _runtime?.CompletePreparation(entry.ProjectPath);
            _session.Dispose(); _transferred=true;
            ProjectCreated?.Invoke(entry,_runtime); _runtime=null;
        }
        finally { _savingProject=false; _busy=false; }
    }
}
