using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Models;
using NAudio.Wave;
using ComboBox=System.Windows.Controls.ComboBox;
using CheckBox=System.Windows.Controls.CheckBox;
using Button=System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed class DictationSettingsControl : StackPanel
{
    private readonly TextBlock _title=Label(true),_help=Label(),_deviceLabel=Label(),_threadsLabel=Label(),_hotkeyLabel=Label();
    private readonly CheckBox _prewarm=new(){Margin=new(0,8,0,10)};
    private readonly ComboBox _mode=new(){MinWidth=180},_device=new(){MinWidth=220},_threads=new(){MinWidth=140};
    private readonly Button _assign=new(),_clear=new(),_prepare=new();
    private readonly TextBlock _hotkey=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,10,0)};
    private DictationSettings? _settings;
    private bool _refresh;
    public event Action? Changed,AssignHotkeyRequested,PrepareRequested;
    public DictationSettingsControl()
    {
        Children.Add(_title);Children.Add(_help);Children.Add(_mode);Children.Add(_prewarm);
        Children.Add(_deviceLabel);Children.Add(_device);Children.Add(_threadsLabel);Children.Add(_threads);Children.Add(_hotkeyLabel);
        var keys=new WrapPanel{Margin=new(0,5,0,8)};keys.Children.Add(_hotkey);keys.Children.Add(_assign);keys.Children.Add(_clear);Children.Add(keys);Children.Add(_prepare);
        foreach(var e in new FrameworkElement[]{_mode,_device,_threads,_assign,_clear,_prepare})e.Margin=new(0,0,8,8);
        foreach(var button in new[]{_assign,_clear,_prepare})button.SetResourceReference(StyleProperty,"SecondaryButtonStyle");
        AutomationProperties.SetAutomationId(this,"Settings.Dictation");
        AutomationProperties.SetAutomationId(_mode,"Settings.Dictation.Mode");AutomationProperties.SetAutomationId(_prewarm,"Settings.Dictation.Prewarm");
        AutomationProperties.SetAutomationId(_device,"Settings.Dictation.Device");AutomationProperties.SetAutomationId(_threads,"Settings.Dictation.Threads");
        AutomationProperties.SetAutomationId(_assign,"Settings.Dictation.AssignHotkey");AutomationProperties.SetAutomationId(_clear,"Settings.Dictation.ClearHotkey");
        AutomationProperties.SetAutomationId(_prepare,"Settings.Dictation.Prepare");
        _mode.SelectionChanged+=(_,_)=>Edit(s=>s.HoldToTalk=_mode.SelectedIndex==1);
        _prewarm.Checked+=(_,_)=>Edit(s=>s.Prewarm=true);_prewarm.Unchecked+=(_,_)=>Edit(s=>s.Prewarm=false);
        _device.SelectionChanged+=(_,_)=>Edit(s=>s.DeviceNumber=_device.SelectedIndex-1);
        _threads.SelectionChanged+=(_,_)=>Edit(s=>s.Threads=(int?)_threads.SelectedItem??0);
        _assign.Click+=(_,_)=>AssignHotkeyRequested?.Invoke();_clear.Click+=(_,_)=>Edit(s=>s.Hotkey="");_prepare.Click+=(_,_)=>PrepareRequested?.Invoke();
    }
    private static TextBlock Label(bool title=false){var text=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,6),FontWeight=title?FontWeights.SemiBold:FontWeights.Normal};text.SetResourceReference(TextBlock.ForegroundProperty,title?"TextPrimaryBrush":"TextSecondaryBrush");return text;}
    private void Edit(Action<DictationSettings> edit){if(_refresh||_settings is null)return;edit(_settings);Changed?.Invoke();}
    public void Configure(DictationSettings settings,Func<string,string> text)
    {
        _refresh=true;try {
            _settings=settings;_title.Text=text("Dictation.Title");_help.Text=text("Dictation.SettingsHelp");
            _mode.ItemsSource=new[]{text("Dictation.Toggle"),text("Dictation.Hold")};_mode.SelectedIndex=settings.HoldToTalk?1:0;
            _prewarm.Content=text("Dictation.Prewarm");_prewarm.IsChecked=settings.Prewarm;
            _deviceLabel.Text=text("Dictation.Microphone");_device.Items.Clear();_device.Items.Add(text("Dictation.DefaultDevice"));
            for(var i=0;i<WaveInEvent.DeviceCount;i++)_device.Items.Add(WaveInEvent.GetCapabilities(i).ProductName);
            _device.SelectedIndex=settings.DeviceNumber>=-1&&settings.DeviceNumber<WaveInEvent.DeviceCount?settings.DeviceNumber+1:0;
            _threadsLabel.Text=text("Dictation.Threads");_threads.ItemsSource=new[]{0,1,2,4,8,12,16};_threads.SelectedItem=settings.Threads;
            _threads.ToolTip=text("Dictation.ThreadsHelp");_hotkeyLabel.Text=text("Dictation.Hotkey");
            _hotkey.Text=string.IsNullOrWhiteSpace(settings.Hotkey)?text("Dictation.Unassigned"):settings.Hotkey;
            _assign.Content=text("Dictation.AssignHotkey");_clear.Content=text("Dictation.ClearHotkey");_prepare.Content=text("Dictation.Prepare");
        }finally{_refresh=false;}
    }
    public static string? ChooseHotkey(Window owner,Func<string,string> text)
    {
        var window=new Window{Owner=owner,Title=text("Dictation.Hotkey"),Width=410,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        window.Resources.MergedDictionaries.Add(owner.Resources);window.SetResourceReference(Window.BackgroundProperty,"WindowBackgroundBrush");window.SetResourceReference(Window.ForegroundProperty,"TextPrimaryBrush");
        var panel=new StackPanel{Margin=new(24)};panel.Children.Add(new TextBlock{Text=text("Dictation.HotkeyHint"),TextWrapping=TextWrapping.Wrap});
        var value=new TextBlock{Margin=new(0,18,0,18),FontSize=20};panel.Children.Add(value);
        var save=new Button{Content=text("Dictation.Save"),IsEnabled=false};panel.Children.Add(save);window.Content=panel;
        string? chosen=null;
        window.PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.Escape){window.DialogResult=false;return;}
            var key=e.Key==Key.System?e.SystemKey:e.Key;var modifiers=Keyboard.Modifiers;
            if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return;
            if(modifiers==ModifierKeys.None)return;
            var parts=new List<string>();if(modifiers.HasFlag(ModifierKeys.Control))parts.Add("Ctrl");if(modifiers.HasFlag(ModifierKeys.Alt))parts.Add("Alt");
            if(modifiers.HasFlag(ModifierKeys.Shift))parts.Add("Shift");if(modifiers.HasFlag(ModifierKeys.Windows))parts.Add("Win");parts.Add(key.ToString());
            chosen=string.Join("+",parts);value.Text=chosen;save.IsEnabled=true;e.Handled=true;
        };
        save.Click+=(_,_)=>window.DialogResult=true;return window.ShowDialog()==true?chosen:null;
    }
}
