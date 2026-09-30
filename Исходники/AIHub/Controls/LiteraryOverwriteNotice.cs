using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls;

internal sealed class LiteraryOverwriteNotice : Border
{
    private readonly TextBox _input;
    private readonly Func<string, string> _l;
    private readonly TextBlock _message;
    private readonly Button _disable;
    private readonly DependencyPropertyDescriptor _readOnly = DependencyPropertyDescriptor.FromProperty(TextBox.IsReadOnlyProperty, typeof(TextBox));
    private bool _attached, _insertObserved;

    public LiteraryOverwriteNotice(TextBox input, Func<string, string> localize)
    {
        _input = input; _l = localize;
        Visibility = Visibility.Collapsed; Margin = new Thickness(0, 0, 0, 8); Padding = new Thickness(10);
        BorderThickness = new Thickness(2); CornerRadius = new CornerRadius(6);
        BorderBrush = new SolidColorBrush(Color.FromRgb(217, 154, 50));
        SetResourceReference(BackgroundProperty, "SecondaryButtonBackgroundBrush");
        var content = new StackPanel(); Child = content;
        _message = LiteraryUi.Text(""); _message.FontWeight = FontWeights.SemiBold; content.Children.Add(_message);
        _disable = LiteraryUi.Button(_l("Studio.Overwrite.Disable"), () =>
        {
            LiteraryOverwriteMode.Disable(_input); Refresh();
            if (LiteraryOverwriteMode.Read(_input) == false) _input.Focus();
        });
        _disable.Margin = new Thickness(0, 6, 0, 0); content.Children.Add(_disable);
        Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _input.AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler(CommandExecuted), true);
        _input.GotKeyboardFocus += FocusChanged;
        _input.PreviewKeyDown += InputKeyDown;
        _input.PreviewTextInput += BeforeInput;
        _input.IsEnabledChanged += EnabledChanged;
        _readOnly.AddValueChanged(_input, ReadOnlyChanged);
        Refresh();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _input.RemoveHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler(CommandExecuted));
        _input.GotKeyboardFocus -= FocusChanged;
        _input.PreviewKeyDown -= InputKeyDown;
        _input.PreviewTextInput -= BeforeInput;
        _input.IsEnabledChanged -= EnabledChanged;
        _readOnly.RemoveValueChanged(_input, ReadOnlyChanged);
    }

    private void CommandExecuted(object sender, ExecutedRoutedEventArgs e)
    { if (e.Command == EditingCommands.ToggleInsert) { _insertObserved = true; Refresh(); } }
    private void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Insert) return;
        _insertObserved = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (_attached) Refresh(); }));
    }
    private void FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => Refresh();
    private void BeforeInput(object sender, TextCompositionEventArgs e) => Refresh();
    private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();
    private void ReadOnlyChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var mode = LiteraryOverwriteMode.Read(_input);
        Visibility = mode == true || mode is null && _insertObserved ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = _l(mode is null ? "Studio.Overwrite.Unknown" : "Studio.Overwrite.Warning");
        _disable.Visibility = mode is null ? Visibility.Collapsed : Visibility.Visible;
        _disable.IsEnabled = mode == true && _input.IsEnabled && !_input.IsReadOnly;
        _disable.ToolTip = !_input.IsEnabled || _input.IsReadOnly ? _l("Studio.Overwrite.Wait") : null;
        ToolTipService.SetShowOnDisabled(_disable, true);
    }
}
