using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.SongClip;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>A short per-file meaning question, before copying the file into the project.</summary>
internal sealed class SongClipReferenceWindow : Window
{
    internal string ChosenKind { get; private set; } = "";
    internal string Description => _role.Input.Text.Trim();
    private readonly ExampleTextInput _role;

    internal SongClipReferenceWindow(Window? owner, Func<string, string> localize, string file)
    {
        if (owner is not null) Owner = owner;
        MusicWishUi.PrepareWindow(this, localize("SongClip.ReferenceImportTitle"), "SongClip.ReferenceImport.Window", 620);
        Height = 510; MinHeight = 430;
        string L(string key) => localize("SongClip." + key);
        var panel = new DockPanel { Margin = new(16) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var add = new Button { Content = L("ReferenceAdd"), MinWidth = 100, Margin = new(4), Padding = new(12, 7, 12, 7), IsEnabled = false };
        var cancel = new Button { Content = L("ReferenceCancel"), MinWidth = 100, Margin = new(4), Padding = new(12, 7, 12, 7), IsCancel = true };
        AutomationProperties.SetAutomationId(add, "SongClip.ReferenceImport.Add");
        AutomationProperties.SetAutomationId(cancel, "SongClip.ReferenceImport.Cancel");
        add.Click += (_, _) => { if (ChosenKind.Length > 0 && Description.Length > 0) DialogResult = true; };
        cancel.Click += (_, _) => DialogResult = false;
        footer.Children.Add(add); footer.Children.Add(cancel); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var filePanel = new DockPanel { Margin = new(0, 0, 0, 8) };
        var preview = new System.Windows.Controls.Image { Width = 96, Height = 96, Stretch = Stretch.Uniform, Margin = new(0, 0, 12, 0) };
        DockPanel.SetDock(preview, Dock.Left); filePanel.Children.Add(preview);
        try {
            using var input = File.OpenRead(file);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 256; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); preview.Source = bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileFormatException
            or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        filePanel.Children.Add(new TextBlock { Text = Path.GetFileName(file), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(filePanel, Dock.Top); panel.Children.Add(filePanel);
        var question = new TextBlock { Text = L("ReferenceKindQuestion"), FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 5) };
        DockPanel.SetDock(question, Dock.Top); panel.Children.Add(question);
        var kinds = new StackPanel { Margin = new(0, 0, 0, 10) };
        foreach (var (kind, key) in new[] { ("performer", "ReferencePerformer"), ("world", "ReferenceWorld"), ("object", "ReferenceObject") }) {
            var choice = new RadioButton { Content = L(key), Margin = new(2, 4, 2, 4), GroupName = "reference-kind" };
            choice.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(choice, "SongClip.ReferenceImport." + kind);
            choice.Checked += (_, _) => { ChosenKind = kind; add.IsEnabled = Description.Length > 0; };
            kinds.Children.Add(choice);
        }
        DockPanel.SetDock(kinds, Dock.Top); panel.Children.Add(kinds);
        var roleQuestion = new TextBlock { Text = L("ReferenceRoleQuestion"), Margin = new(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(roleQuestion, Dock.Top); panel.Children.Add(roleQuestion);
        _role = new(L("ReferenceRoleQuestion"), L("ReferenceRoleExample"), "SongClip.ReferenceImport.Description", multiline: true);
        _role.Input.Height = double.NaN; _role.Input.MaxLength = SongClipReferences.MaximumDescription;
        Loaded += (_, _) => LiterarySpellChecking.Enable(_role.Input, localize("VideoPrep.SpellingLanguage"));
        Closed += (_, _) => LiterarySpellChecking.ReleaseTree(_role);
        _role.Input.TextChanged += (_, _) => add.IsEnabled = ChosenKind.Length > 0 && Description.Length > 0;
        panel.Children.Add(_role); Content = panel;
    }
}
