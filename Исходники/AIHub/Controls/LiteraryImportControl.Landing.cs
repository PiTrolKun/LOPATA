using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private readonly TextBox _draftFolder = new();
    private readonly TextBox _draftSourceFile = new() { IsReadOnly = true };
    private readonly TextBox _draftProjectName = new();
    private readonly TextBox _draftWorkTitle = new();
    private readonly TextBlock _draftError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private FrameworkElement? _landingView;
    private bool _showingDialogSelection;
    private bool _showingQuickPreview;
    private string? _parsedSourcePath, _parsedFolderPath, _parsedSessionId;

    private void ShowLanding()
    {
        SetFirstStep(true);
        _showingQuickPreview = false;
        _body.Children.Clear();
        _landingView ??= CreateLanding();
        _body.Children.Add(_landingView);
    }

    private FrameworkElement CreateLanding()
    {
        _draftFolder.Text = _folder.Text;
        InitializeDraftSaving();
        var layout = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());

        layout.Children.Add(CreateTopics(0));

        var fields = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        fields.Children.Add(LiteraryUi.Text(L("StageTitle"), true));
        fields.Children.Add(LiteraryUi.Text(L("StageHint")));
        AddField(fields, L("DraftFolder"), _draftFolder, L("ChooseFolder"), SelectDraftFolder);
        AddField(fields, L("DraftSource"), _draftSourceFile, L("SelectSource"), SelectDraftSourceFile);
        AddField(fields, L("DraftProject"), _draftProjectName);
        AddField(fields, L("DraftWork"), _draftWorkTitle);
        LiterarySpellChecking.Enable(_draftProjectName, _language);
        LiterarySpellChecking.Enable(_draftWorkTitle, _language);
        _draftError.Foreground = System.Windows.Media.Brushes.OrangeRed;
        fields.Children.Add(_draftError);
        var next = LiteraryUi.Button(L("Next"), NextFromDraft, true);
        next.Margin = new Thickness(0, 12, 0, 0);
        fields.Children.Add(next);
        Grid.SetColumn(fields, 1);
        layout.Children.Add(fields);
        return layout;
    }

    private StackPanel CreateTopics(int active)
    {
        var topics = new StackPanel { Margin = new Thickness(0, 4, 18, 0) };
        var labels = new[] { "Stage1", "Stage2", "Stage3", "Stage4", "Stage5" };
        for (var index = 0; index < labels.Length; index++)
        {
            var label = LiteraryUi.Text($"{index + 1}. {L(labels[index])}", index == active);
            label.Margin = new Thickness(0, 0, 0, 12);
            topics.Children.Add(label);
        }
        return topics;
    }

    private void NextFromDraft()
    {
        var folder = _draftFolder.Text.Trim();
        var source = _draftSourceFile.Text.Trim();
        var project = _draftProjectName.Text.Trim();
        var work = _draftWorkTitle.Text.Trim();
        if (!Directory.Exists(folder) || !File.Exists(source)
            || Path.GetExtension(source).ToLowerInvariant() is not (".zip" or ".json")
            || !LiteraryProjectStore.IsValidProjectName(project) || work.Length is 0 or > 150)
        {
            _draftError.Text = L("DraftRequired");
            _draftError.Visibility = Visibility.Visible;
            return;
        }
        _draftError.Visibility = Visibility.Collapsed;
        SaveDraft("source");
        if (_session is not null && _input is not null && _parsedSessionId == _session.State.Id
            && string.Equals(_parsedSourcePath, source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_parsedFolderPath, folder, StringComparison.OrdinalIgnoreCase))
        {
            ShowDialogSelection();
            SaveDraft("dialogs");
            return;
        }
        _ = RunAsync(async ct =>
        {
            _session?.Dispose(); _session = null; _input = null;
            _session = await Task.Run(() => ImportSession.Create(folder, source, ct), ct);
            _input = await Task.Run(() => DeepSeekImportReader.ReadAsync(_session, ct), ct);
            _parsedSourcePath = source; _parsedFolderPath = folder; _parsedSessionId = _session.State.Id;
            ShowDialogSelection();
            SaveDraft("dialogs");
        });
    }

    private void ShowDialogSelection()
    {
        SetFirstStep(false);
        _showingQuickPreview = false;
        _showingDialogSelection = true;
        _body.Children.Clear(); _conversations.Clear();
        var layout = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(CreateTopics(1));

        var content = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        content.Children.Add(LiteraryUi.Text(L("DialogStepTitle"), true));
        content.Children.Add(LiteraryUi.Text(L("ChooseConversations")));
        foreach (var conversation in _input!.Conversations)
        {
            var suggested = ImportWorkNames.MatchesDialogTitle(_draftWorkTitle.Text, conversation.Title);
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = conversation.Title + " · " + conversation.Units });
            if (suggested)
            {
                var hint = new TextBlock { Text = " · " + L("SuggestedDialog"), FontWeight = FontWeights.SemiBold };
                hint.SetResourceReference(ForegroundProperty, "AccentBrush");
                label.Children.Add(hint);
            }
            var check = new CheckBox
            {
                Content = label,
                IsChecked = _session!.State.Conversations.Contains(conversation.Id),
                VerticalAlignment = VerticalAlignment.Center
            };
            check.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var item = new Border { Child = check, Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 2, 0, 2), CornerRadius = new CornerRadius(6),
                BorderThickness = suggested ? new Thickness(2) : new Thickness(0) };
            if (suggested) item.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            content.Children.Add(item);
            _conversations.Add((check, conversation.Id));
            check.Checked += (_, _) => DraftChanged();
            check.Unchecked += (_, _) => DraftChanged();
        }
        content.Children.Add(LiteraryUi.Text(L("Inventory") + $" {_input.Inventory.Count}; " + L("Warnings") + $" {_input.Warnings.Count}"));
        if (_input.Warnings.Count > 0) content.Children.Add(LiteraryUi.Text(L("FormatNotice")));
        content.Children.Add(LiteraryUi.Button(L("Next"), StartFullAnalysis, true));
        Grid.SetColumn(content, 1); layout.Children.Add(content);
        _body.Children.Add(layout);
    }

    private static void AddField(StackPanel fields, string label, TextBox box, string? buttonLabel = null, Action? action = null)
    {
        fields.Children.Add(LiteraryUi.Text(label, true));
        box.Margin = new Thickness(0, 4, 0, 10);
        box.Padding = new Thickness(8);
        box.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        box.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        fields.Children.Add(box);
        if (buttonLabel is null || action is null) return;
        var button = LiteraryUi.Button(buttonLabel, action);
        button.Margin = new Thickness(0, 0, 0, 18);
        fields.Children.Add(button);
    }

    private void SelectDraftFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _draftFolder.Text = dialog.FolderName;
    }

    private void SelectDraftSourceFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"ZIP/JSON (*.zip;*.json)|*.zip;*.json|{L("AllFiles")} (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _draftSourceFile.Text = dialog.FileName;
    }
}
