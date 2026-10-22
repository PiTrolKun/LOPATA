using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.Dictation;
using Button=System.Windows.Controls.Button;
using MessageBox=System.Windows.MessageBox;
using ContextMenu=System.Windows.Controls.ContextMenu;
using MenuItem=System.Windows.Controls.MenuItem;
using Control=System.Windows.Controls.Control;

namespace AIHub;

public partial class MainWindow
{
    private readonly DictationSettingsControl _dictationSettings=new();
    private readonly DictationWidget _dictationWidget=new();
    private readonly Button _dictationEntry=new(){Content="🎤",MinWidth=28,MinHeight=24,Height=24,Padding=new(2),Focusable=false};
    private readonly CancellationTokenSource _dictationLifetime=new();
    private readonly DispatcherTimer _dictationMeter=new(){Interval=TimeSpan.FromMilliseconds(80)};
    private DictationSession? _dictation;
    private DictationFloatingWindow? _dictationWindow;
    private DictationHotkey? _dictationHotkey;
    private IDictationTextTarget? _dictationTarget;
    private Task? _dictationPreparation;
    private bool _dictationExpanded,_dictationBusy,_dictationIntent,_dictationStarting,_dictationAcceptInput,_dictationDocking,_dictationDisposed,_dictationHoldRequested,_dictationPicking;
    private string? _dictationError;
    private string _dictationUndelivered="";
    private string _dictationProgress="";
    private nint _dictationForeground;
    private int _dictationLoadedThreads;

    private void InitializeDictation()
    {
        _dictation=new();DictationHost.Content=_dictationEntry;
        _dictationEntry.SetResourceReference(FrameworkElement.StyleProperty,"SecondaryButtonStyle");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_dictationEntry,"Dictation.Open");
        _dictationEntry.PreviewMouseLeftButtonDown+=(_,_)=>_dictationForeground=DictationTargetPicker.ActiveWindow;
        _dictationEntry.Click+=(_,_)=>{if(_dictationWindow is not null)DockDictation();else{_dictationExpanded=!_dictationExpanded;DictationHost.Content=_dictationExpanded?_dictationWidget:_dictationEntry;}RefreshDictation();};
        _dictationWidget.DetachRequested+=ToggleDictationDock;
        _dictationWidget.CollapseRequested+=()=>{DockDictation();_dictationExpanded=false;DictationHost.Content=_dictationEntry;};
        _dictationWidget.TargetRequested+=()=>_ = GuardDictationAsync(PickDictationTargetAsync);
        _dictationWidget.ModeChanged+=hold=>{_appSettings.Dictation.HoldToTalk=hold;SaveDictationSettings();};
        _dictationWidget.ToggleRequested+=()=>_ = GuardDictationAsync(ToggleDictationAsync);
        _dictationWidget.PressRequested+=()=>{_dictationHoldRequested=true;_ = GuardDictationAsync(StartDictationAsync);};
        _dictationWidget.ReleaseRequested+=()=>{_dictationHoldRequested=false;_dictationIntent=false;_ = GuardDictationAsync(StopDictationAsync);};
        _dictationWidget.PreviewMouseLeftButtonDown+=(_,_)=>_dictationForeground=DictationTargetPicker.ActiveWindow;
        _dictation.TextReady=async(chunk,text)=>{
            if(_dictationDisposed||!_dictationAcceptInput)return;
            var target=_dictationTarget??throw new InvalidOperationException("Dictation.TargetLost");
            try {
                if(target is ExternalDictationTarget external)await external.ApplyAsync(chunk.Segment,text,chunk.Final,_dictationLifetime.Token);
                else await Dispatcher.InvokeAsync(()=>{if(_dictationAcceptInput&&!_dictationDisposed)target.Apply(chunk.Segment,text,chunk.Final);});
            }
            catch{_dictationUndelivered=text;_dictationAcceptInput=false;throw;}
        };
        _dictation.Changed+=()=>{if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(RefreshDictation);};
        _dictation.Failed+=error=>{if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(()=>_ = ReportDictationFailureAsync(error));};
        _dictationSettings.Changed+=SaveDictationSettings;
        _dictationSettings.PrepareRequested+=()=>_ = GuardDictationAsync(()=>PrepareDictationAsync(true));
        _dictationSettings.AssignHotkeyRequested+=()=>{
            _dictationHotkey?.Configure("");
            _captureHotkeys?.BeginRecording();
            try {
                var chord=DictationSettingsControl.ChooseHotkey(this,L);if(chord is null)return;
                _appSettings.Dictation.Hotkey=chord;SaveDictationSettings();
            }finally{_captureHotkeys?.EndRecording();ConfigureDictationHotkey();}
        };
        var menu=new ContextMenu();var copy=new MenuItem();
        copy.Click+=(_,_)=>{if(_dictationUndelivered.Length>0)System.Windows.Clipboard.SetText(_dictationUndelivered);};
        menu.Opened+=(_,_)=>{copy.Header=L("Dictation.CopyPending");copy.IsEnabled=_dictationUndelivered.Length>0;};menu.Items.Add(copy);_dictationEntry.ContextMenu=menu;
        _dictationMeter.Tick+=(_,_)=>RefreshDictation();_dictationMeter.Start();
        SourceInitialized+=(_,_)=>{
            _dictationHotkey=new(new WindowInteropHelper(this).Handle);
            _dictationHotkey.PressedChanged+=down=>{
                if(down){_dictationForeground=DictationTargetPicker.ActiveWindow;if(_appSettings.Dictation.HoldToTalk){_dictationHoldRequested=true;_ = GuardDictationAsync(StartDictationAsync);}else _ = GuardDictationAsync(ToggleDictationAsync);}
                else if(_dictationHoldRequested){_dictationHoldRequested=false;_dictationIntent=false;_ = GuardDictationAsync(StopDictationAsync);}
            };
            ConfigureDictationHotkey();
        };
        ApplicationBackgroundOperations.RegisterModel(this,async owner=>{if(!owner._dictationDisposed&&owner._dictation is {} session)await session.UnloadAsync();});
        RefreshDictation();
    }
    private void PrewarmDictationAfterStartup()
    {
        Dispatcher.BeginInvoke(async()=>{
            if(_appSettings.Dictation.Prewarm&&DictationComponents.IsInstalled(_componentManager)&&DictationComponents.HasAcknowledgement)
                await GuardDictationAsync(()=>PrepareDictationAsync(false));
        },DispatcherPriority.Background);
    }
    private void SaveDictationSettings()
    {
        _appSettingsStore.Save(_appSettings);ConfigureDictationHotkey();RefreshDictationSettings();RefreshDictation();
        if(_appSettings.Dictation.Prewarm&&_dictation is {Recording:false,Processing:false})_ = GuardDictationAsync(()=>PrepareDictationAsync(false));
    }
    private void ConfigureDictationHotkey(){if(_dictationHotkey?.Configure(_appSettings.Dictation.Hotkey)==false)_dictationError="Dictation.HotkeyConflict";}
    private void RefreshDictationSettings()=>_dictationSettings.Configure(_appSettings.Dictation,L);
    private void RefreshDictation()
    {
        if(_dictation is null||_dictationDisposed)return;
        var status=_dictationError??(_dictationBusy?"Dictation.Preparing":_dictation.Recording?"Dictation.Recording":_dictation.Processing?"Dictation.Processing":_dictation.Ready?"Dictation.Ready":"Dictation.Idle");
        string Text(string key)=>key=="Dictation.Preparing"&&_dictationProgress.Length>0?L(key)+" "+_dictationProgress:L(key);
        _dictationWidget.Refresh(Text,_appSettings.Dictation.HoldToTalk,_dictation.Recording,_dictation.Processing,_dictationBusy,status,_dictation.Level,_dictationStarting);
        _dictationEntry.Content=_dictationWindow is null?"🎤":"↙";
        _dictationEntry.ToolTip=L(_dictationWindow is null?"Dictation.Open":"Dictation.Dock");
        if(_dictation.Recording)_dictationEntry.Foreground=System.Windows.Media.Brushes.OrangeRed;
        else _dictationEntry.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");
        System.Windows.Automation.AutomationProperties.SetName(_dictationEntry,(string)_dictationEntry.ToolTip);
        if(_dictationWindow is {} floating)floating.Title=L("Dictation.Title");
    }
    private async Task GuardDictationAsync(Func<Task> action){try{await action();}catch(OperationCanceledException){}catch(Exception error){await ReportDictationFailureAsync(error);}}
    private async Task ReportDictationFailureAsync(Exception error)
    {
        if(_dictationDisposed)return;
        _dictationAcceptInput=false;_dictationIntent=false;_dictationError=error.Message.StartsWith("Dictation.",StringComparison.Ordinal)?error.Message:"Dictation.Error";
        OwnedProcessRegistry.Log("dictation_failed","VoiceInput",detail:error.GetType().Name);
        if(!error.Message.StartsWith("Dictation.",StringComparison.Ordinal))ComponentAuditFailures.Report(error);
        try{if(_dictation is {} session)await session.StopAsync();}catch(Exception stop){OwnedProcessRegistry.Log("dictation_stop_failed","VoiceInput",detail:stop.GetType().Name);}
        RefreshDictation();
    }
    private async Task ToggleDictationAsync(){if(_dictation?.Recording==true||_dictationStarting){_dictationIntent=false;await StopDictationAsync();}else await StartDictationAsync();}
    private async Task StartDictationAsync()
    {
        if(_dictation is null||_dictationDisposed||_dictationStarting||_dictation.Recording||_dictation.Processing)return;
        _dictationStarting=true;_dictationIntent=true;_dictationError=null;
        try {
            var foreground=DictationTargetPicker.ActiveWindow;
            if(System.Windows.Application.Current.Windows.OfType<Window>().Any(w=>DictationTargetPicker.IsWidget(w)&&new WindowInteropHelper(w).Handle==foreground))foreground=_dictationForeground;
            _dictationTarget=DictationTargetPicker.Automatic(foreground,_dictationTarget);
            if(_dictationTarget is null)await PickDictationTargetAsync();
            if(_dictationTarget is null||!_dictationIntent)return;
            await PrepareDictationAsync(true);if(!_dictationIntent||!_dictation.Ready)return;
            if(_dictationTarget is ExternalDictationTarget external)await external.ActivateAsync(_dictationLifetime.Token);
            if(!_dictationIntent)return;
            _dictationTarget.Reset();_dictationUndelivered="";_dictationAcceptInput=true;
            _dictation.Start(_appSettings.Dictation.DeviceNumber);
        }finally{_dictationStarting=false;RefreshDictation();}
    }
    private async Task StopDictationAsync(){_dictationIntent=false;if(_dictation is {} session)await session.StopAsync();RefreshDictation();}
    private async Task PickDictationTargetAsync()
    {
        if(_dictation is null||_dictation.Recording||_dictation.Processing||_dictationPicking)return;
        _dictationPicking=true;
        try {
            _dictationTarget=await DictationTargetPicker.ChooseAsync(this,L,_dictationLifetime.Token);
            _dictationError=_dictationTarget is null?"Dictation.PickRequired":null;
        }finally{_dictationPicking=false;RefreshDictation();}
    }
    private async Task PrepareDictationAsync(bool interactive)
    {
        if(_dictationPreparation is {IsCompleted:false} pending){await pending;return;}
        if(_dictation is null||_dictationDisposed)return;
        if(_dictation.Ready&&_dictationLoadedThreads==_appSettings.Dictation.EffectiveThreads)return;
        if(!interactive&&(!DictationComponents.IsInstalled(_componentManager)||!DictationComponents.HasAcknowledgement))return;
        _dictationPreparation=Prepare();await _dictationPreparation;
        async Task Prepare() {
            _dictationBusy=true;_dictationProgress="";RefreshDictation();
            try {
                if(interactive&&!IsVisible)RestoreFromTray();
                await ComponentLicenseGate.EnsureAsync(DictationComponents.TermsId,_dictationLifetime.Token);
                if(!DictationComponents.IsInstalled(_componentManager)) {
                    if(!interactive||MessageBox.Show(this,L("Dictation.DownloadQuestion"),L("Dictation.Title"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                    var progress=new Progress<AIHub.Models.ComponentDownloadProgress>(p=>{
                        _dictationProgress=p.TotalBytes>0?$"{100d*p.DownloadedBytes/p.TotalBytes:F0}%":"";RefreshDictation();
                    });
                    await _componentManager.DownloadAndInstallAsync(DictationComponents.ModelId,progress,_dictationLifetime.Token);
                }
                _dictationProgress="";
                var threads=_appSettings.Dictation.EffectiveThreads;
                await _dictation!.PrepareAsync(DictationComponents.GetModelFiles(_componentManager),threads,_dictationLifetime.Token);
                _dictationLoadedThreads=threads;_dictationError=null;
            }finally{_dictationBusy=false;RefreshDictation();}
        }
    }
    private void ToggleDictationDock()
    {
        if(_dictationWindow is not null){DockDictation();return;}
        DictationHost.Content=_dictationEntry;_dictationWidget.SetFloating(true);
        _dictationWindow=new(this,_dictationWidget,L);
        _dictationWindow.Closing+=(_,e)=>{if(_dictationDocking||_dictationDisposed)return;e.Cancel=true;Dispatcher.BeginInvoke(DockDictation);};
        _dictationWindow.Show();RefreshDictation();
    }
    private void DockDictation()
    {
        _dictationDocking=true;try {
            if(_dictationWindow is {} window){window.Content=null;window.Close();_dictationWindow=null;}
            _dictationWidget.SetFloating(false);_dictationExpanded=true;DictationHost.Content=_dictationWidget;
        }finally{_dictationDocking=false;RefreshDictation();}
    }
    private async Task DisposeDictationAsync()
    {
        if(_dictationDisposed)return;_dictationDisposed=true;_dictationIntent=false;_dictationAcceptInput=false;
        _dictationHotkey?.Dispose();_dictationMeter.Stop();_dictationLifetime.Cancel();_dictationWindow?.Close();
        if(_dictation is {} session)await session.DisposeAsync();
    }
}
