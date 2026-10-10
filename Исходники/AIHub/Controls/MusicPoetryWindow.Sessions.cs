using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private void NewSession()
    {
        if (IsWorking) return;
        Capture(); CommitZone(); if (!Persist()) return;
        _session = MusicPoetryProtocol.Create(_seed(), _l); _model = _coreModel; ModelCaption(); LoadSession(); Persist();
    }
    private void OpenSessions()
    {
        if (IsWorking) return;
        if (_sessionManager is not null) { _sessionManager.Activate(); return; }
        Capture(); CommitZone(); if (!Persist()) return;
        var root = new DockPanel { Margin = new(12) };
        var window = new Window { Title = L("Sessions"), Owner = this, Width = 760, Height = 500, MinWidth = 450, MinHeight = 250,
            Content = root, Resources = Resources, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _sessionManager = window; window.Closed += (_, _) => _sessionManager = null;
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        var controls = new WrapPanel(); DockPanel.SetDock(controls, Dock.Bottom); root.Children.Add(controls);
        var list = new ListBox { DisplayMemberPath = "Caption" }; list.SetResourceReference(BackgroundProperty, "PanelBrush");
        list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); list.SetResourceReference(BorderBrushProperty, "LineBrush");
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.DynamicResourceExtension("TextPrimaryBrush")));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("PanelBrush")));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("LineBrush"))); itemStyle.Triggers.Add(selected);
        list.ItemContainerStyle = itemStyle; root.Children.Add(list);
        Action(controls, "OpenSession", () => _ = OpenAsync());
        Action(controls, "Delete", () => {
            if (list.SelectedItem is not SessionRow row || IsWorking) return;
            if (MessageBox.Show(window, L("DeleteConfirm") + "\n" + row.Caption, L("Delete"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { _store.Delete(row.Id); if (_session.Id == row.Id) { _session = MusicPoetryProtocol.Create(new(), _l); ModelCaption(); LoadSession(); } Refresh(); }
            catch (Exception ex) { _status.Text = L("Error") + " " + ex.Message; }
        });
        Action(controls, "Close", window.Close); list.MouseDoubleClick += async (_, _) => await OpenAsync();
        void Refresh() { list.ItemsSource = _store.List().Select(e => new SessionRow(e.Id,
            $"{e.UpdatedAt.LocalDateTime:g} · {e.Title}" + (e.Damaged ? " · " + L("Damaged") : ""))).ToArray(); }
        async Task OpenAsync()
        {
            if (list.SelectedItem is not SessionRow row || IsWorking) return;
            Capture(); CommitZone(); if (!Persist()) return;
            var previous = _session;
            try {
                var session = _store.Load(row.Id);
                var models = await Task.Run(() => MusicPoetryRuntime.Discover(_storage));
                if (IsWorking || !IsLoaded || !window.IsLoaded || !ReferenceEquals(previous, _session)) return;
                Capture(); CommitZone(); if (!Persist()) return;
                _session = session; _model = models.FirstOrDefault(m => m.Path.Equals(session.ModelPath, StringComparison.OrdinalIgnoreCase));
                if (_model is null && string.IsNullOrEmpty(session.ModelPath)) _model = models.FirstOrDefault(m => m.IsCoreModel);
                ModelCaption(); LoadSession(); _status.Text = _model is null ? L("ModelMissing") : L("Ready"); window.Close();
            } catch (Exception ex) { _status.Text = L("Error") + " " + ex.Message; }
        }
        try { Refresh(); window.Show(); } catch (Exception ex) { _status.Text = L("Error") + " " + ex.Message; }
    }
    private sealed record SessionRow(string Id, string Caption);
}
