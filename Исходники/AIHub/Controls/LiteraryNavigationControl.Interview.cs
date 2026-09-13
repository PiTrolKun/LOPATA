using System.Windows;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryNavigationControl
{
    private LiteraryInterviewControl? _interview;
    private bool _choosingCreation;
    private string _creationPage = "choice";
    private readonly LiteraryInterviewRecentStore _recent = LiteraryInterviewRecentStore.Default();
    private void CreateProject() { _choosingCreation=true; _creationPage="choice"; _notice=""; Render(); }
    private void BuildCreationModes(StackPanel body)
    {
        if (_creationPage == "choice")
        {
            body.Children.Add(Card("Literary.Interview.NewStart","Literary.Interview.NewStartHint","Literary.Start", () => { _creationPage="new"; Render(); }));
            body.Children.Add(Card("Literary.Interview.ContinueStart","Literary.Interview.ContinueStartHint","Literary.Select", () => { _creationPage="resume"; Render(); }));
            return;
        }
        if (_creationPage == "resume") { BuildRecentPreparations(body); return; }
        body.Children.Add(Card("Literary.Interview.Automatic","Literary.Interview.AutomaticHint","Literary.Start", () =>
        {
            OpenInterview(new LiteraryInterviewSession(_language,_initialFolder,_recent));
        }));
        body.Children.Add(Card("Literary.Interview.Expert","Literary.Interview.ExpertHint","Literary.Start",CreateExpertProject));
        if (_notice.Length > 0) body.Children.Add(LiteraryUi.Text(_notice));
    }
    private void BuildRecentPreparations(StackPanel body)
    {
        body.Children.Add(LiteraryUi.Text(_l("Literary.Interview.RecentHint")));
        try
        {
            var entries=_recent.Load();
            if (entries.Count==0) body.Children.Add(LiteraryUi.Text(_l("Literary.Interview.NoRecent")));
            foreach(var entry in entries)
            {
                body.Children.Add(LiteraryUi.Text(entry.Title,true));
                body.Children.Add(LiteraryUi.Text(string.Format(_l("Literary.Interview.RecentStatus"),entry.Step,entry.UpdatedAt.ToLocalTime().ToString("g"))));
                body.Children.Add(LiteraryUi.Text(entry.Root));
                body.Children.Add(LiteraryUi.Button(_l("Literary.Continue"), () => ResumePreparation(entry.Root)));
            }
        }
        catch(Exception ex) { _notice=_l("Literary.Create.LoadError")+" "+ex.Message; }
        body.Children.Add(LiteraryUi.Text(_l("Literary.Interview.ImportHint")));
        body.Children.Add(LiteraryUi.Button(_l("Literary.Interview.ImportPreparation"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory=_initialFolder, Title=_l("Literary.Interview.ImportPreparation") };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            ResumePreparation(dialog.FolderName);
        }));
        if (_notice.Length > 0) body.Children.Add(LiteraryUi.Text(_notice));
    }
    private void ResumePreparation(string root)
    {
        try { _notice=""; OpenInterview(LiteraryInterviewSession.Resume(root,_recent)); }
        catch(Exception ex) { _notice=_l("Literary.Create.LoadError")+" "+(_l(ex.Message)!=ex.Message ? _l(ex.Message) : ex.Message); Render(); }
    }
    private void OpenInterview(LiteraryInterviewSession session)
    {
        var page = new LiteraryInterviewControl(_l,session,_store); _interview=page;
        page.PauseRequested += () => { if (_interview != page) return; _interview=null; _choosingCreation=true; _creationPage="resume"; Render(); };
        page.ProjectCreated += (entry,runtime) =>
        {
            if (_interview != page) { runtime?.Dispose(); return; }
            _interview=null; _choosingCreation=false; ShowingProjects=true; LoadProjects(); OpenWorkspace(entry,runtime);
        };
        Render();
    }
}
