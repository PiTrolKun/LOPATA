using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AIHub.Services.CharacterKnowledge;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls.CharacterKnowledge;

internal sealed partial class CharacterKnowledgeControl : UserControl
{
    private readonly Window _owner;
    private readonly KnowledgeStore _store;
    private readonly Func<string> _languageCode;
    private readonly DispatcherTimer _draftTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(550)
    };
    private Func<string, string> _localize;
    private IReadOnlyList<KnowledgeBase> _bases = [];
    private KnowledgeProject? _project;
    private KnowledgeDraft? _draft;
    private bool _draftChanged;
    private bool _rules;
    private bool _rendering;
    private string? _statusKey;
    private object[] _statusArguments = [];

    internal CharacterKnowledgeControl(Window owner, Func<string, string> localize,
        Func<string> languageCode, KnowledgeStore? store = null)
    {
        _owner = owner;
        _localize = localize;
        _languageCode = languageCode;
        _store = store ?? new();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/CharacterKnowledge/CharacterKnowledgeResources.xaml", UriKind.Relative)
        });
        _draftTimer.Tick += (_, _) => Checkpoint();
        BuildInterface();
        RefreshWorkspace();
    }

    internal void Enter()
    {
        if (_project is not null) return;
        Perform("OpenFailed", () =>
        {
            _bases = _store.List();
            RefreshBases();
            var recent = _store.LastOpened();
            var id = (_bases.FirstOrDefault(item => item.BaseId == recent) ?? _bases.FirstOrDefault())?.BaseId ?? recent;
            if (id is { } selected) Activate(_store.Load(selected));
        });
    }

    internal void Configure(Func<string, string> localize)
    {
        var selected = SelectedItem?.Id;
        LiterarySpellChecking.ReleaseTree(Content as DependencyObject);
        _localize = localize;
        BuildInterface();
        RefreshBases();
        RefreshWorkspace(selected);
        RefreshStatus();
    }

    internal bool Checkpoint()
    {
        _draftTimer.Stop();
        if (!_draftChanged || _project is null) return true;
        return Perform("SaveFailed", () =>
        {
            _project = _store.Save(_project with { Draft = _draft });
            _draftChanged = false;
        });
    }

    private void CreateBase()
    {
        var name = AskBaseName();
        if (name is null || !Checkpoint()) return;
        Perform("SaveFailed", () =>
        {
            Activate(_store.Create(name));
            _bases = _store.List();
            RefreshBases();
            _store.Remember(_project!.Base.BaseId);
            Say("BaseCreated");
        });
    }

    private void OpenBase()
    {
        if (_rendering || _baseSelector.SelectedValue is not Guid id || id == _project?.Base.BaseId) return;
        if (!Checkpoint() || !Perform("OpenFailed", () => Activate(_store.Load(id))))
        {
            RefreshBases();
            return;
        }
        Perform("RememberFailed", () => _store.Remember(id));
    }

    private void ImportBase()
    {
        var dialog = new OpenFileDialog { Filter = T("ZipFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(_owner) != true || !Checkpoint()) return;
        Perform("ImportFailed", () =>
        {
            Activate(_store.Import(dialog.FileName));
            _bases = _store.List();
            RefreshBases();
            _store.Remember(_project!.Base.BaseId);
            Say("Imported");
        });
    }

    private void ExportBase()
    {
        if (_project is not { } project) return;
        var invalid = Path.GetInvalidFileNameChars();
        var name = string.Concat(project.Base.Name.Select(character =>
            invalid.Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        var dialog = new SaveFileDialog
        {
            Filter = T("ZipFilter"), DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true,
            FileName = string.IsNullOrWhiteSpace(name) ? "knowledge" : name
        };
        if (dialog.ShowDialog(_owner) != true) return;
        if (!dialog.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            Say("ZipExtension");
            return;
        }
        Perform("ExportFailed", () =>
        {
            KnowledgeArchive.Export(dialog.FileName, project with { Draft = _draft });
            Say("Exported");
        });
    }

    private void Activate(KnowledgeProject project)
    {
        _project = project;
        _draft = project.Draft;
        _draftChanged = false;
        _rules = _draft is KnowledgeRuleDraft;
        Say(null);
        RefreshWorkspace(_draft?.TargetId);
        RefreshBases();
    }

    private void StartNew() => BeginDraft(EmptyDraft());

    private void StartFromSelection(bool variant)
    {
        if (_project is not { } project || SelectedItem is not { } selected) return;
        var draft = _rules
            ? KnowledgeEditing.FromRule(project.Knowledge.Rules.Single(rule => rule.Id == selected.Id), variant)
            : KnowledgeEditing.FromEntry(project.Knowledge.Entries.Single(entry => entry.Id == selected.Id), variant);
        BeginDraft(draft);
    }

    private KnowledgeDraft EmptyDraft() => _rules
        ? new KnowledgeRuleDraft(KnowledgeRule.Empty())
        : new KnowledgeEntryDraft(KnowledgeEntry.Empty());

    private void BeginDraft(KnowledgeDraft draft)
    {
        if (_project is not { } project || (HasDraftContent() && !Confirm("ReplaceDraft"))) return;
        Perform("SaveFailed", () =>
        {
            ApplySaved(_store.Save(project with { Draft = draft }), SelectedItem?.Id);
            Say(null);
            _draftStart?.Focus();
        });
    }

    private void SaveDraft(bool addAnother)
    {
        if (_project is not { } project || _draft is not { } draft || !IsCurrentDraft) return;
        Perform("SaveFailed", () =>
        {
            var contents = KnowledgeEditing.Commit(project.Knowledge, draft);
            var next = addAnother ? EmptyDraft() : null;
            ApplySaved(_store.Save(project with { Knowledge = contents, Draft = next }), draft.ItemId);
            Say("Saved");
            if (addAnother) _draftStart?.Focus();
        });
    }

    private void CloseDraft()
    {
        if (_project is not { } project || !IsCurrentDraft || (HasDraftContent() && !Confirm("DiscardDraft"))) return;
        Perform("SaveFailed", () =>
        {
            try
            {
                ApplySaved(_store.Save(project with { Draft = null }), SelectedItem?.Id);
                Say(null);
            }
            catch (KnowledgeProblem error) when (error.Key == "ChangedElsewhere")
            {
                Activate(_store.Load(project.Base.BaseId));
            }
        });
    }

    private void RemoveSelected()
    {
        if (_project is not { } project || SelectedItem is not { } item || !Confirm("DeleteQuestion")) return;
        Perform("SaveFailed", () =>
        {
            var contents = _rules
                ? project.Knowledge with { Rules = project.Knowledge.Rules.Where(rule => rule.Id != item.Id).ToArray() }
                : project.Knowledge with { Entries = project.Knowledge.Entries.Where(entry => entry.Id != item.Id).ToArray() };
            var draft = _draft?.TargetId == item.Id ? null : _draft;
            ApplySaved(_store.Save(project with { Knowledge = contents, Draft = draft }), null);
            Say("Deleted");
        });
    }

    private void ApplySaved(KnowledgeProject project, Guid? selected)
    {
        _draftTimer.Stop();
        _project = project;
        _draft = project.Draft;
        _draftChanged = false;
        RefreshWorkspace(selected);
    }

    private void ChangeEntry(Func<KnowledgeEntry, KnowledgeEntry> update)
    {
        if (_rendering || _draft is not KnowledgeEntryDraft draft) return;
        _draft = draft with { Content = update(draft.Content) };
        DraftChanged();
    }

    private void DraftChanged()
    {
        _draftChanged = true;
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private bool IsCurrentDraft => _rules ? _draft is KnowledgeRuleDraft : _draft is KnowledgeEntryDraft;

    private bool HasDraftContent() => _draft switch
    {
        KnowledgeEntryDraft entry => entry.Content.Type.Length + entry.Content.Input.Length + entry.Content.Output.Length > 0,
        KnowledgeRuleDraft rule => rule.Content.Condition.Length + rule.Content.Then.Count + rule.Content.Else.Count > 0,
        _ => false
    };

    private bool Confirm(string key) =>
        MessageBox.Show(_owner, T(key), T("Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            == MessageBoxResult.Yes;

    private bool Perform(string failure, Action action)
    {
        try { action(); return true; }
        catch (KnowledgeProblem error)
        {
            if (error.Item is { } item)
                Say(error.Key, item.Length > 120 ? item[..120] + "…" : item);
            else Say(error.Key);
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Say(failure);
            return false;
        }
    }

    private string T(string key) => _localize("CharacterKnowledge." + key);

    private void Say(string? key, params object[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        RefreshStatus();
    }
}
