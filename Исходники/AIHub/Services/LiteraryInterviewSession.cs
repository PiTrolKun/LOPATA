using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Disk state is authoritative. Generation never commits an answer.</summary>
public sealed class LiteraryInterviewSession : IDisposable
{
    public LiteraryInterviewState State { get; private set; }
    private readonly LiteraryInterviewRecentStore? _recent;
    public LiteraryProjectReservation? Reservation { get; private set; }
    public string? Root => Reservation?.Root;
    public const string RelativeFile = "Preparation/interview.json";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public LiteraryInterviewSession(string language, string parent, LiteraryInterviewRecentStore? recent = null)
    { State = new() { Language = language, Parent = parent }; _recent = recent; }
    public void Reserve()
    {
        if (Reservation is not null) return;
        var root = Path.Combine(State.Parent, State.ProjectName);
        if (File.Exists(Path.Combine(root, RelativeFile))) throw new IOException("Literary.Interview.ResumeExists");
        Reservation = new(State.Parent, State.ProjectName);
        Save();
    }
    public static LiteraryInterviewSession Resume(string root, LiteraryInterviewRecentStore? recent = null)
    {
        root = Path.GetFullPath(root);
        LiteraryProjectLayout.CheckTreePath(root);
        var state = Read(root);
        if (state.Finished || File.Exists(Path.Combine(root, "project.json"))) throw new IOException("Literary.Interview.AlreadyCreated");
        var session = new LiteraryInterviewSession(state.Language, Path.GetDirectoryName(root)!, recent);
        session.Reservation = new(session.State.Parent, Path.GetFileName(root));
        session.State = state;
        state.Parent = Path.GetDirectoryName(root)!; state.ProjectName = Path.GetFileName(root);
        state.Inputs[1] = state.Parent; state.Inputs[2] = state.ProjectName;
        state.InFlight = false;
        try { session.Save(); }
        catch { session.Dispose(); throw; }
        return session;
    }
    public static LiteraryInterviewState Read(string root)
    {
        var path = Path.Combine(root, RelativeFile);
        LiteraryInterviewState ReadFile(string file)
        {
            var state = JsonSerializer.Deserialize<LiteraryInterviewState>(LiteraryChapterFiles.Read(file), Json);
            if (state is null || state.Version != 1 || state.Step is < 1 or > 37 || !Enum.IsDefined(state.Stage)
                || state.Inputs is null || state.Selections is null || state.Records is null || state.Asked is null
                || state.CompletedTopics is null || state.Materials is null || state.Journal is null || state.Route is null
                || state.Route.Count != 10 || !state.Route.Select(r => r.Number).SequenceEqual(Enumerable.Range(1,10)))
                throw new InvalidDataException("Invalid interview checkpoint.");
            return state;
        }
        try { return ReadFile(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FileNotFoundException)
        { if (File.Exists(path + ".bak")) return ReadFile(path + ".bak"); throw; }
    }
    public void Save()
    {
        if (Root is null) return;
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        var folder = Path.Combine(Root, "Preparation");
        LiteraryProjectLayout.CheckTreePath(folder);
        Directory.CreateDirectory(folder);
        var previous = State.SavedAt; State.SavedAt = DateTimeOffset.Now;
        try { LiteraryChapterFiles.Write(Path.Combine(Root, RelativeFile), JsonSerializer.Serialize(State, Json)); }
        catch { State.SavedAt = previous; throw; }
        _recent?.Remember(Root, State);
    }
    public void Journal(string action, string text = "") => State.Journal.Add(new(DateTimeOffset.UtcNow, action, State.Step, text));
    public void Propose(string text, bool adaptive, bool allowEmpty = false)
    {
        if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Empty interpretation.");
        State.Pending = text; State.AdaptiveAnswer = adaptive;
        if (State.PendingId.Length == 0) State.PendingId = Guid.NewGuid().ToString("N");
        State.Correction = ""; State.Stage = InterviewStage.Understanding; Journal("proposed", text);
    }
    public void Confirm(string question, string raw, string meaning = "statement")
    {
        if (State.Stage != InterviewStage.Understanding || State.PendingId.Length == 0) throw new InvalidOperationException();
        if (meaning == "statement") meaning = State.Step switch
        {
            >=4 and <=8 => "intent",
            >=9 and <=14 => "basis_and_setting",
            >=15 and <=22 => "initial_world_state",
            >=23 and <=25 => "planned_development",
            >=26 and <=30 => "style_preference",
            31 => "requirement", 32 => "avoid", 33 => "writer_permission", 34 => "user_decision", 35 => "plan",
            _ => "project_metadata"
        };
        if (!State.Records.Any(r => r.Id == State.PendingId))
        {
            if (!State.AdaptiveAnswer && State.Records.Any(r => r.Step == State.Step && !r.Adaptive))
                throw new InvalidOperationException("Confirmed step is read-only.");
            State.Records.Add(new(State.PendingId, State.Step, LiteraryInterviewCatalog.Get(State.Step).Topic,
                question, raw, State.Pending, meaning, State.AdaptiveAnswer));
        }
        Journal("confirmed", State.Pending);
        if (!State.Asked.Any(q => q.Text == question)) State.Asked.Add(new(LiteraryInterviewCatalog.Get(State.Step).Topic,question));
        var adaptive = State.AdaptiveAnswer;
        State.Pending = ""; State.PendingId = ""; State.Correction = "";
        if (State.ConfirmationReturnStep is int returnStep)
        {
            State.ConfirmationReturnStep = null;
            State.AdaptiveAnswer = false; State.AdaptiveInput = ""; State.AdaptiveQuestion = "";
            State.Step = Math.Clamp(returnStep, 1, 37); State.Stage = InterviewStage.Mandatory;
            return;
        }
        if (adaptive)
        {
            State.Stage = InterviewStage.Adaptive; State.AdaptiveQuestion = ""; State.AdaptiveInput = "";
            if (State.AiDisabled) EndTopic();
        }
        else Advance();
    }
    public bool TopicMinimumComplete() => LiteraryInterviewCatalog.Questions
        .Where(q => q.Topic == LiteraryInterviewCatalog.Get(State.Step).Topic)
        .All(q => State.Records.Any(r => r.Step == q.Number && !r.Adaptive));
    public void Advance()
    {
        if (LiteraryInterviewCatalog.AdaptiveEnds.Contains(State.Step) && !State.AiDisabled
            && !State.CompletedTopics.Contains(LiteraryInterviewCatalog.Get(State.Step).Topic) && TopicMinimumComplete())
        { State.Stage = InterviewStage.Adaptive; State.AdaptiveQuestion = ""; State.AdaptiveInput = ""; }
        else if (State.Step == 37) State.Stage = InterviewStage.Review;
        else { State.Step++; State.Stage = InterviewStage.Mandatory; }
    }
    public void EndTopic()
    {
        if (State.Stage != InterviewStage.Adaptive || !TopicMinimumComplete()) throw new InvalidOperationException();
        State.CompletedTopics.Add(LiteraryInterviewCatalog.Get(State.Step).Topic);
        Journal("topic_finished"); Advance();
    }
    public void TestMove(int delta)
    {
        if (!LiteraryInterviewCatalog.TestNavigationEnabled) return;
        Journal("test_navigation", delta.ToString());
        State.NavigationDrafts[State.Step] = new(State.Stage,State.AdaptiveAnswer,State.AdaptiveQuestion,State.AdaptiveInput,State.Pending,State.Correction,State.PendingId,State.ConfirmationReturnStep);
        State.Step = Math.Clamp(State.Step + delta, 1, 37);
        var target = State.NavigationDrafts.GetValueOrDefault(State.Step);
        State.Stage = target?.Stage ?? InterviewStage.Mandatory; State.AdaptiveAnswer=target?.AdaptiveAnswer ?? false;
        State.Pending=target?.Pending ?? ""; State.PendingId=target?.PendingId ?? ""; State.Correction=target?.Correction ?? "";
        State.AdaptiveQuestion=target?.Question ?? ""; State.AdaptiveInput=target?.Input ?? "";
        State.ConfirmationReturnStep=target?.ConfirmationReturnStep;
    }
    public bool Complete => LiteraryInterviewCatalog.Questions.All(q => State.Records.Any(r => r.Step == q.Number && !r.Adaptive))
        && State.Route.Take(2).All(r => !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Description));
    public void Dispose() { Reservation?.Dispose(); Reservation = null; }
}
