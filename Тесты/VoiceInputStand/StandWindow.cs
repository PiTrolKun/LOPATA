using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;

namespace VoiceInputStand;

public sealed class StandWindow : Window
{
    private sealed record Candidate(string Key,string Name);
    private readonly ComboBox _model=new(){MinWidth=360,DisplayMemberPath="Name"};
    private readonly ComboBox _device=new(){MinWidth=250,DisplayMemberPath="Name"};
    private readonly ComboBox _threads=new(){ItemsSource=new[]{1,2,4,8,12},SelectedItem=4,Width=65};
    private readonly ComboBox _interval=new(){ItemsSource=new[]{0.5,1d,2d,3d},SelectedItem=1d,Width=65};
    private readonly Button _load=new(){Content="Загрузить модель"};
    private readonly Button _record=new(){Content="● Запись",IsEnabled=false};
    private readonly Button _repeat=new(){Content="Повторить запись",IsEnabled=false};
    private readonly Button _file=new(){Content="Открыть WAV…"};
    private readonly TextBox _text=Box(20),_logBox=Box(12);
    private readonly TextBlock _status=new(){Text="Выберите модель и нажмите «Загрузить модель».",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _resources=new();
    private readonly ProgressBar _level=new(){Height=8,Maximum=0.15,Margin=new Thickness(5)};
    private readonly StandLog _log;
    private readonly RecognizerHost _host;
    private readonly string _root,_data;
    private readonly ConcurrentQueue<string> _uiLog=new();
    private readonly ConcurrentDictionary<string,AudioRequest> _requests=new();
    private readonly SortedDictionary<int,string> _segments=new();
    private readonly HashSet<int> _finals=[];
    private readonly LinkedList<AudioRequest> _queue=new();
    private readonly object _queueGate=new();
    private readonly SemaphoreSlim _signal=new(0);
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _queueTask;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(80)};
    private AudioSession? _audio;
    private string? _lastRecording;
    private int _pending;
    private bool _busy,_closing,_closeComplete;
    private (string Id,string Text)? _preview;
    private readonly object _previewGate=new();
    private double _rms;

    public StandWindow(string root,string data)
    {
        _root=root;_data=data;_log=new(data);_host=new(_log,root,data);
        Title="ЛОПАТА — стенд голосового ввода · CPU";Width=1150;Height=790;MinWidth=900;MinHeight=650;
        Background=new SolidColorBrush(Color.FromRgb(17,24,39));Foreground=Brushes.White;
        AutomationProperties.SetAutomationId(this,"VoiceStand.Window");
        var panel=new DockPanel{Margin=new Thickness(16)};Content=panel;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);panel.Children.Add(top);
        top.Children.Add(new TextBlock{Text="Выбор распознавателя · локально, только CPU",FontSize=24,Margin=new Thickness(4,0,4,12)});
        var selectors=new WrapPanel();top.Children.Add(selectors);
        _model.ItemsSource=new[]{new Candidate("parakeet","Parakeet TDT 0.6B v3 · INT8 · RU/EN")};_model.SelectedIndex=0;
        _device.ItemsSource=Enumerable.Range(0,WaveInEvent.DeviceCount).Select(i=>new{Number=i,Name=WaveInEvent.GetCapabilities(i).ProductName}).ToArray();_device.SelectedIndex=0;
        foreach(var item in new UIElement[]{_model,_device,new TextBlock{Text="Потоки:"},_threads,new TextBlock{Text="Обновление, с:"},_interval})selectors.Children.Add(item);
        var buttons=new WrapPanel();top.Children.Add(buttons);
        var logs=new Button{Content="Открыть логи"};var unload=new Button{Content="Выгрузить"};
        foreach(var button in new[]{_load,_record,_repeat,_file,unload,logs}){button.Margin=new Thickness(4,10,4,8);button.Padding=new Thickness(12,7,12,7);buttons.Children.Add(button);}
        top.Children.Add(_level);top.Children.Add(_status);top.Children.Add(_resources);
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);panel.Children.Add(bottom);
        bottom.Children.Add(new TextBlock{Text="Подробный локальный журнал · WAV, гипотезы, токены, ошибки, CPU/RAM и задержки",Margin=new Thickness(0,10,0,4)});
        _logBox.Height=150;bottom.Children.Add(_logBox);
        bottom.Children.Add(new TextBlock{Text=_log.DirectoryPath,FontSize=11,TextWrapping=TextWrapping.Wrap});
        _text.Margin=new Thickness(0,12,0,0);panel.Children.Add(_text);
        foreach(var control in new Control[]{_model,_device,_threads,_interval}){control.Margin=new Thickness(4);control.Padding=new Thickness(5);control.Background=Brushes.White;control.Foreground=Brushes.Black;}
        AutomationProperties.SetAutomationId(_model,"VoiceStand.Model");AutomationProperties.SetAutomationId(_load,"VoiceStand.Load");
        AutomationProperties.SetAutomationId(_record,"VoiceStand.Record");AutomationProperties.SetAutomationId(_text,"VoiceStand.Text");
        _log.Line+=DisplayLog;
        _host.Preview+=(id,text)=>{lock(_previewGate)_preview=(id,text);};
        _host.Telemetry+=row=>
        {
            if(row.GetProperty("event").GetString()=="resources") _=Dispatcher.BeginInvoke(()=>_resources.Text=$"RAM: {row.GetProperty("rss").GetInt64()/1048576d:F0} МБ · CPU: {row.GetProperty("cpu_percent_machine").GetDouble():F1}% всего ПК · в очереди: {Volatile.Read(ref _pending)}");
        };
        _timer.Tick+=(_,_)=>Refresh();_timer.Start();_queueTask=Task.Run(ProcessQueue);
        _load.Click+=async(_,_)=>await Guard(Load);
        _record.Click+=async(_,_)=>await Guard(ToggleRecording);
        _repeat.Click+=async(_,_)=>await Guard(()=>Replay(_lastRecording!));
        _file.Click+=async(_,_)=>await Guard(async()=>{var dialog=new OpenFileDialog{Filter="WAV|*.wav"};if(dialog.ShowDialog(this)==true)await Replay(dialog.FileName);});
        unload.Click+=async(_,_)=>await Guard(async()=>{await StopRecording();lock(_queueGate){_pending-=_queue.Count;_queue.Clear();}await _host.DisposeAsync();_status.Text="Модель выгружена. Аудио и логи сохранены.";});
        logs.Click+=(_,_)=>Process.Start(new ProcessStartInfo("explorer.exe",_log.DirectoryPath){UseShellExecute=true});
        _model.SelectionChanged+=(_,_)=>UpdateButtons();
        Closing+=async(_,e)=>
        {
            if(_closeComplete)return;
            e.Cancel=true;if(_closing)return;
            _closing=true;IsEnabled=false;_timer.Stop();
            try{await StopRecording();}catch(Exception error){_log.Write("close_recording_error",error.ToString());}
            _stop.Cancel();_signal.Release();
            try{await _host.DisposeAsync();await _queueTask;}
            catch(Exception error){_log.Write("close_worker_error",error.ToString());}
            _log.Write("stand_closed");_log.Dispose();_closeComplete=true;
            // A synchronous completion must also leave the original Closing event first.
            _=Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    private static TextBox Box(double size)=>new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
        Background=new SolidColorBrush(Color.FromRgb(24,34,52)),Foreground=Brushes.White,FontSize=size,Padding=new Thickness(10)};
    private void DisplayLog(string line)
    {
        try
        {
            using var doc=JsonDocument.Parse(line);
            var kind=doc.RootElement.GetProperty("event").GetString();
            if(kind=="audio_packet")return;
            var data=doc.RootElement.GetProperty("data");
            if(kind=="worker_stdout")
            {
                using var inner=JsonDocument.Parse(data.GetProperty("line").GetString()!);
                kind=inner.RootElement.GetProperty("event").GetString();
                if(kind=="resources")return;
                _uiLog.Enqueue(kind+" · "+inner.RootElement.GetRawText());
            }
            else _uiLog.Enqueue(kind+" · "+data.GetRawText());
        }
        catch(JsonException){_uiLog.Enqueue(line);}
    }
    private string Selected=>((Candidate)_model.SelectedItem).Key;
    private async Task Guard(Func<Task> action)
    {
        try{await action();}
        catch(Exception error){_log.Write("ui_operation_error",error.ToString());try{await StopRecording();}catch(Exception stopError){_log.Write("stop_error",stopError.ToString());}_status.Text="Ошибка: "+error.Message+". Аудио и журнал сохранены.";}
        finally{_busy=false;UpdateButtons();}
    }
    private async Task Load()
    {
        _busy=true;UpdateButtons();_status.Text="Проверка компонентов и загрузка модели…";
        await _host.LoadAsync(Selected,(int)_threads.SelectedItem,_stop.Token);
        _status.Text="Готово. Нажмите «Запись». До нажатия микрофон выключен.";
    }
    private async Task ToggleRecording()
    {
        if(_audio is not null){await StopRecording();return;}
        ClearText();
        if(_device.SelectedIndex<0)throw new IOException("Нет доступного микрофона. Можно открыть WAV-файл.");
        _audio=new(_log,Enqueue,(double)_interval.SelectedItem);
        _audio.Level+=value=>_rms=value;
        _audio.Failed+=error=>Dispatcher.BeginInvoke(async()=>await Guard(()=>Task.FromException(error)));
        _audio.Start(_device.SelectedIndex);_lastRecording=_audio.RecordingPath;
        _record.Content="■ Остановить";_status.Text="Идёт запись. Промежуточный текст может уточняться; итог фиксируется после паузы.";UpdateButtons();
    }
    private async Task StopRecording()
    {
        if(_audio is not {} audio)return;
        _audio=null;
        try{await audio.StopAsync();}
        finally{audio.Dispose();_rms=0;_record.Content="● Запись";UpdateButtons();}
        _status.Text="Микрофон выключен. Завершается распознавание записанного.";
    }
    private void ClearText(){_segments.Clear();_finals.Clear();_requests.Clear();_text.Clear();}
    private async Task Replay(string path)
    {
        ClearText();_busy=true;UpdateButtons();
        var normalized=await Task.Run(()=>AudioSession.NormalizeFile(path,_log.DirectoryPath));
        using var reader=new WaveFileReader(normalized);
        using var splitter=new AudioSession(_log,Enqueue,1000000);
        var buffer=new byte[3200];int count;
        while((count=await reader.ReadAsync(buffer))>0)splitter.Push(buffer,count);
        splitter.FinishFile();_lastRecording=path;
        _log.Write("file_replay",new{path,mode="same audio; final chunks only"});
    }
    private void Enqueue(AudioRequest request)
    {
        lock(_queueGate)
        {
            for(var node=_queue.First;node is not null;){var next=node.Next;if(node.Value.Segment==request.Segment&&!node.Value.Final){_queue.Remove(node);_requests.TryRemove(node.Value.Id,out _);Interlocked.Decrement(ref _pending);_log.Write("preview_coalesced",new{old=node.Value.Id,latest=request.Id});}node=next;}
            _queue.AddLast(request);_requests[request.Id]=request;Interlocked.Increment(ref _pending);
        }
        _signal.Release();
    }
    private async Task ProcessQueue()
    {
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_stop.Token);AudioRequest? request;
                lock(_queueGate){request=_queue.First?.Value;if(request is not null)_queue.RemoveFirst();}
                if(request is null)continue;
                try
                {
                    var start=Stopwatch.GetTimestamp();
                    var result=await _host.DecodeAsync(request.Id,request.Path,_stop.Token);
                    var elapsed=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var latency=Stopwatch.GetElapsedTime(request.CapturedTicks).TotalMilliseconds;
                    _log.Write("recognition",new{request.Id,request.Segment,request.Final,request.Seconds,result.Text,result.Milliseconds,wall_ms=elapsed,latency_ms=latency,rtf=elapsed/(request.Seconds*1000)});
                    await Dispatcher.InvokeAsync(()=>Apply(request.Id,result.Text,request.Final));
                }
                finally{Interlocked.Decrement(ref _pending);}
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch(OperationCanceledException){_log.Write("recognition_cancelled");}
            catch(Exception error)
            {
                _log.Write("recognition_error",error.ToString());
                lock(_queueGate){_pending=0;_queue.Clear();}
                await Dispatcher.InvokeAsync(async()=>await Guard(()=>Task.FromException(error)));
            }
        }
    }
    private void Apply(string id,string text,bool final=false)
    {
        if(!_requests.TryGetValue(id,out var request)||(_finals.Contains(request.Segment)&&!final))return;
        _segments[request.Segment]=text;if(final)_finals.Add(request.Segment);
        _text.Text=string.Join("\n",_segments.Values);_text.ScrollToEnd();
    }
    private void Refresh()
    {
        (string Id,string Text)? preview;lock(_previewGate){preview=_preview;_preview=null;}
        if(preview is {} item)Apply(item.Id,item.Text);
        _level.Value=_rms;
        var lines=new List<string>();while(_uiLog.TryDequeue(out var line))lines.Add(line);
        if(lines.Count>0){_logBox.AppendText(string.Join("\n",lines.TakeLast(25))+"\n");if(_logBox.Text.Length>18000)_logBox.Text=_logBox.Text[^12000..];_logBox.ScrollToEnd();}
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var recording=_audio is not null;var idle=!_busy&&!recording&&Volatile.Read(ref _pending)==0;
        _model.IsEnabled=idle;_threads.IsEnabled=idle;_device.IsEnabled=idle;_interval.IsEnabled=idle;
        _load.IsEnabled=idle;_record.IsEnabled=!_busy&&(_host.IsReady&&_host.Engine==Selected)&&(recording||Volatile.Read(ref _pending)==0);
        _repeat.IsEnabled=idle&&_host.IsReady&&_host.Engine==Selected&&_lastRecording is not null;
        _file.IsEnabled=idle&&_host.IsReady&&_host.Engine==Selected;
    }
}
