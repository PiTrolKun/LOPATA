using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

internal static class HandoffProbe
{
    internal static void Settings(LiteraryStudioControl studio,Window owner,string imagePath,Func<string,string> l)
    {
        var role=(TextBlock)Program.Field(studio,"_role")!;
        var original=studio.State.DirectRequest; var found=false;
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(180)};
        timer.Tick+=(_,_)=>
        {
            var dialog=Application.Current.Windows.OfType<LiteraryStudioSettingsWindow>().SingleOrDefault();
            if(dialog is null) return;
            timer.Stop(); found=true;
            var toggle=Program.All<CheckBox>(dialog).Single();
            Program.Check(dialog.Owner==owner && toggle.IsChecked==original,"settings window belongs to current window and loads project preference");
            toggle.IsChecked=!original; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Program.Check(studio.State.DirectRequest==!original,"single toggle changes handoff mode");
            var root=(string)Program.Field(studio,"_directory")!;
            Program.Check(new LiteraryStudioStore(new(root)).Load().DirectRequest==!original,"settings immediately persist project preference");
            toggle.IsChecked=original; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var choices=Program.All<ComboBox>(dialog).ToArray();
            var enter=studio.State.EnterAction; var controlEnter=studio.State.ControlEnterAction;
            choices[0].SelectedIndex=(int)StudioSendKeyAction.NewLine; choices[1].SelectedIndex=(int)StudioSendKeyAction.CurrentRole;
            var saved=new LiteraryStudioStore(new(root)).Load();
            Program.Check(saved.EnterAction==StudioSendKeyAction.NewLine && saved.ControlEnterAction==StudioSendKeyAction.CurrentRole,"both key assignments persist independently");
            choices[0].SelectedIndex=(int)enter; choices[1].SelectedIndex=(int)controlEnter;
            dialog.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(dialog);
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(imagePath)) png.Save(file);
            dialog.Close();
        };
        timer.Start();
        role.ContextMenu!.IsOpen=true;
        var item=(MenuItem)role.ContextMenu.Items[0];
        Program.Check(Equals(item.Header,l("Studio.TransferSettings")),"localized handoff settings context action");
        role.ContextMenu.IsOpen=false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        timer.Stop(); Program.Check(found,"role context action opens the settings window");
    }
    internal static void Run(Window owner,string output,Func<string,string> l)
    {
        var previous=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
        try { RunCore(owner,output,l); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Window owner,string output,Func<string,string> l)
    {
        var root=Program.Create(Path.Combine(output,"handoff"));
        using var runtime=new LiteraryChatRuntime(root);
        var draft=new LiteraryDraftControl(root,l); var requests=new Requests();
        var studio=new LiteraryStudioControl(root,draft,new ContentControl {Content=draft},runtime,()=>false,l,"ru",
            new TextBlock(),new TextBlock(),requests);
        var window=new Window {Owner=owner,Resources=owner.Resources,Width=1150,Height=760,Content=studio,UseLayoutRounding=true};
        window.SetResourceReference(Window.BackgroundProperty,"WindowBackgroundBrush");
        window.Show(); Program.Pump();
        var role=(TextBlock)Program.Field(studio,"_role")!;
        Program.Check(ContextMenuService.GetShowOnDisabled(role) && role.ContextMenu!.Items.Count==1,"settings are available through the role context menu even before discussion");
        void Reset(bool direct=true)
        {
            studio.State.Clear(); studio.State.DirectRequest=direct; studio.State.Input="USER_INSTRUCTION";
            studio.State.Add("Advisor","PRIVATE_DISCUSSION"); requests.Calls.Clear();
            requests.Handler=(r,progress,ct)=>Task.FromResult(Reply(r.Action=="Transfer"?"HIDDEN_BRIEF":"WRITER_RESULT"));
            studio.ReviewTaskAsync=null; Program.Call(studio,"Render");
        }
        Task Start(bool transfer=true)=>(Task)typeof(LiteraryStudioControl).GetMethod("SendAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(studio,[transfer])!;
        void Finish(Task task)
        {
            var limit=DateTime.UtcNow.AddSeconds(6);
            while(!task.IsCompleted && DateTime.UtcNow<limit) Program.Pump();
            Program.Check(task.IsCompleted,"request completes without blocking the UI"); task.GetAwaiter().GetResult();
            Program.Check(!((LiteraryRequestIndicator)Program.Field(studio,"_activity")!).IsActive,"indicator stops at every completed or failed operation");
        }
        bool BriefVisible()=>Program.All<TextBox>((StackPanel)Program.Field(studio,"_messages")!).Any(t=>t.Text=="HIDDEN_BRIEF");
        void Stop()=>((Button)Program.Field(studio,"_stop")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var activity=(LiteraryRequestIndicator)Program.Field(studio,"_activity")!;
        var input=(TextBox)Program.Field(studio,"_input")!;
        var messages=(StackPanel)Program.Field(studio,"_messages")!;
        Reset(); studio.State.Quotes.Add(new("SOURCE","QUOTED_TEXT"));
        var responseReady=new TaskCompletionSource();
        var queueReady=new TaskCompletionSource(); requests.Queue=queueReady.Task; requests.IsBusy=true;
        requests.Handler=async (r,progress,ct)=>
        {
            Program.Check(r.Base.Task=="USER_INSTRUCTION" && !r.Conversation.Any(m=>m.Text=="USER_INSTRUCTION"),"current request is passed once, outside past conversation");
            Program.Check(r.Quotes.Single().Text=="QUOTED_TEXT","clearing composer does not remove submitted quotes");
            await responseReady.Task.WaitAsync(ct); progress.Report(new("VISIBLE_REPLY")); return Reply("VISIBLE_REPLY");
        };
        var idleHeight=studio.ActualHeight; var inputBottom=input.TranslatePoint(new Point(0,input.ActualHeight),studio).Y;
        var submitted=Start(false);
        Program.Check(input.Text.Length==0 && studio.State.Input.Length==0 && studio.State.Quotes.Count==0,"input and quote chips clear immediately after acceptance");
        Program.Check(Program.All<TextBox>(messages).Any(t=>t.Text=="USER_INSTRUCTION"),"user message is visible before model starts");
        var accepted=new LiteraryStudioStore(new(root)).Load();
        Program.Check(accepted.Input.Length==0 && accepted.Interrupted && accepted.Messages.Last().Text=="USER_INSTRUCTION"
            && accepted.Messages.Last().Quotes.Single().Text=="QUOTED_TEXT","request and quotes persist before generation, including restart recovery");
        Program.Pump();
        Program.Check(activity.IsActive && activity.StatusText==l("Studio.Activity.Queued"),"queued request has nearby busy feedback");
        Program.Check(Math.Abs(studio.ActualHeight-idleHeight)<1 && Math.Abs(input.TranslatePoint(new Point(0,input.ActualHeight),studio).Y-inputBottom)<1,"showing busy feedback does not move input or panels");
        var rotation=(RotateTransform)Program.Field(activity,"_rotation")!; var angle=rotation.Angle; Program.Pump();
        Program.Check(!SystemParameters.ClientAreaAnimation || Math.Abs(angle-rotation.Angle)>1,"indicator visibly rotates when system animations are enabled");
        Program.Check(studio.IsMeasureValid && studio.IsArrangeValid,"rotation does not invalidate layout");
        queueReady.SetResult(); Program.Pump();
        Program.Check(activity.StatusText==l("Studio.Activity.Reply"),"leaving queue updates feedback before output tokens");
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(output,"sending-ru.png"))) png.Save(file);
        responseReady.SetResult(); Finish(submitted);
        Program.Check(studio.State.Messages.Count(m=>m.Session==studio.State.Session && m.Role=="User")==1,"reply completion does not duplicate submitted message");
        Program.Check(input.Text.Length==0 && rotation.Angle==0 && activity.Visibility==Visibility.Hidden,"completion leaves empty input and stops animation");
        requests.IsBusy=false; requests.Queue=null;

        Reset(); requests.IsBusy=true; requests.Queue=new TaskCompletionSource().Task;
        submitted=Start(false); Program.Pump(); Stop(); Finish(submitted);
        Program.Check(activity.Visibility==Visibility.Hidden && new LiteraryStudioStore(new(root)).Load().Messages.Last().Text=="USER_INSTRUCTION","queue cancellation stops indicator and retains request");
        requests.IsBusy=false; requests.Queue=null;
        Reset();
        studio.ReviewTaskAsync=_=>throw new Exception("Direct mode must not open the review step");
        requests.Handler=(r,progress,ct)=>
        {
            Program.Check(studio.IsWorking && studio.State.Interrupted,"both stages share the busy operation");
            Program.Check(activity.IsActive && activity.StatusText==l(r.Action=="Transfer"?"Studio.Activity.Task":"Studio.Activity.Reply"),"direct handoff keeps indicator active with the correct stage");
            if(r.Action=="Transfer")
            {
                progress.Report(new("HIDDEN_STREAM"));
                Program.Check(!Program.All<TextBox>(studio).Any(t=>t.Text.Contains("HIDDEN_STREAM")),"task stream is hidden");
            }
            else
            {
                Program.Check(new LiteraryStudioStore(new(root)).Load().Task=="HIDDEN_BRIEF","task is saved before writer starts");
                Program.Check(r.PreviousTask=="HIDDEN_BRIEF" && !r.Conversation.Any(m=>m.Role=="Advisor"),"writer receives task with clean dialogue context");
                Program.Check(!BriefVisible(),"task never flashes before writer generation");
            }
            return Task.FromResult(Reply(r.Action=="Transfer"?"HIDDEN_BRIEF":"WRITER_RESULT"));
        };
        Finish(Start());
        Program.Check(requests.Calls.Select(r=>r.Action).SequenceEqual(new[]{"Transfer","Continue"}),"direct mode runs advisor then writer exactly once");
        Program.Check(studio.State.Result=="WRITER_RESULT" && !BriefVisible(),"only writer output is shown");
        Program.Check(studio.State.Messages.Count(m=>m.Session==studio.State.Session && m.Role=="User")==1,"automatic writer call does not invent a user message");
        var saved=new LiteraryStudioStore(new(root)).Load();
        Program.Check(saved.DirectRequest && saved.Task=="HIDDEN_BRIEF" && !saved.Interrupted,"hidden task and preference survive saving");

        var blue=(Button)Program.Field(studio,"_send")!; var red=(Button)Program.Field(studio,"_sendWriter")!;
        void Press(Button button)
        {
            Program.Check(button.IsEnabled,"requested button is enabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var limit=DateTime.UtcNow.AddSeconds(6);
            while(studio.IsWorking && DateTime.UtcNow<limit) Program.Pump();
            Program.Check(!studio.IsWorking && !activity.IsActive,"button request finishes and releases busy feedback");
        }
        Button Action(string id)=>Program.All<Button>((Grid)Program.Field(studio,"_actions")!)
            .Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)==l("Studio.Action."+id));
        Task Key(StudioSendKeyAction action)=>(Task)typeof(LiteraryStudioControl).GetMethod("HandleSendKeyAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(studio,[action])!;
        Reset(); studio.State.Clear(); Program.Call(studio,"Render"); Program.Pump();
        Program.Check(red.IsEnabled && blue.IsEnabled,"both recipients available from empty advisor session");
        Program.Check(red.TranslatePoint(new Point(),studio).X>blue.TranslatePoint(new Point(),studio).X,"writer send is to the right of advisor send");
        Program.Check(((SolidColorBrush)red.Background).Color.R>((SolidColorBrush)red.Background).Color.B,"writer send uses red accent");
        Program.Check(new FrameworkElement[]{role,blue,red}.All(e=>e.ContextMenu?.Items.Count==1 && ContextMenuService.GetShowOnDisabled(e)),"all three surfaces expose the same settings including disabled writer send");
        role.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left)
            {RoutedEvent=UIElement.MouseLeftButtonDownEvent});
        Program.Check(requests.Calls.Count==0 && studio.State.Role==LiteraryChatProfile.Advisor,"role label cannot trigger handoff");
        input.Text="LAST_UNSENT_DETAIL"; studio.AttachQuote("SOURCE","TRANSFER_QUOTE"); Press(red);
        Program.Check(requests.Calls.Select(r=>r.Action).SequenceEqual(new[]{"Transfer","Continue"})
            && requests.Calls[0].Base.Task.Contains("LAST_UNSENT_DETAIL") && requests.Calls[0].Quotes.Single().Text=="TRANSFER_QUOTE","red send transfers unsent input and quotes then writes");
        Program.Check(!red.IsEnabled && blue.IsEnabled,"writer red send requires an explanatory action");
        requests.Calls.Clear(); input.Text="ASK_ADVISOR_INSTEAD"; Press(blue);
        Program.Check(requests.Calls.Single().Base.Role==LiteraryChatProfile.Advisor && requests.Calls[0].Action=="Discuss"
            && requests.Calls[0].Conversation.Any(m=>m.Role=="Writer") && studio.State.Role==LiteraryChatProfile.Advisor,"blue returns to advisor and discusses the last writer result");

        Reset(); Press(red); requests.Calls.Clear();
        input.Text="UNSELECTED_WRITER_INPUT"; Program.Check(!red.IsEnabled,"typing alone does not enable writer send");
        Finish(Key(StudioSendKeyAction.Writer)); Program.Check(requests.Calls.Count==0 && input.Text=="UNSELECTED_WRITER_INPUT","disabled writer shortcut never falls through to advisor");
        Action("Rewrite").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.Check(red.IsEnabled && requests.Calls.Count==0,"selecting an explanatory action enables send without starting inference");
        Press(red); Program.Check(requests.Calls.Single().Base.Role==LiteraryChatProfile.Writer && requests.Calls[0].Action=="Rewrite","red sends explanation using selected writer action");
        requests.Calls.Clear(); input.Text="DISCUSS_THIS_TONE"; Press(blue);
        Program.Check(requests.Calls.Single().Action=="Discuss" && requests.Calls[0].Base.Role==LiteraryChatProfile.Advisor,"blue does not leak writer action into advisor request");

        Reset(false); Press(red); requests.Calls.Clear();
        Program.Check(!red.IsEnabled && studio.State.Role==LiteraryChatProfile.Writer,"show brief waits for a standalone writer action");
        Press(Action("Continue")); Program.Check(requests.Calls.Single().Action=="Continue","standalone continue writes without red send");
        requests.Calls.Clear();
        Program.All<Button>((Grid)Program.Field(studio,"_actions")!).Single(b=>Equals(b.Content,l("Studio.Comment"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        input.Text="COMMENT_FOR_CONTINUE"; Program.Check(red.IsEnabled && studio.State.WriterComment,"comment enables red send for the existing autonomous action");
        Finish(Key(StudioSendKeyAction.CurrentRole));
        Program.Check(requests.Calls.Single().Action=="Continue" && requests.Calls[0].Base.Task.Contains("COMMENT_FOR_CONTINUE")
            && !studio.State.WriterComment && !red.IsEnabled,"current-role shortcut delivers comment once and clears comment mode");
        requests.Calls.Clear(); Press(blue);
        Program.Check(studio.State.Role==LiteraryChatProfile.Advisor && requests.Calls.Count==0,"empty blue returns to discussion without an empty inference");
        input.Text="ABCD"; input.Select(1,2); Finish(Key(StudioSendKeyAction.NewLine));
        Program.Check(input.Text=="A"+Environment.NewLine+"D" && requests.Calls.Count==0,"newline replaces selection without sending");
        input.Text="KEY_TO_ADVISOR"; Finish(Key(StudioSendKeyAction.Advisor));
        Program.Check(requests.Calls.Single().Base.Role==LiteraryChatProfile.Advisor,"advisor shortcut has a fixed recipient");

        Reset(); studio.State.Input=""; studio.State.Quotes.Add(new("ONLY_SOURCE","ONLY_QUOTE")); Program.Call(studio,"Render"); Press(red);
        Program.Check(new LiteraryStudioStore(new(root)).Load().Messages.Any(m=>m.Role=="User" && m.Quotes.Any(q=>q.Text=="ONLY_QUOTE")),"quote-only transfer persists its submitted evidence");

        Reset(false); Finish(Start());
        Program.Check(requests.Calls.Count==1 && BriefVisible() && studio.State.Result.Length==0,"show brief pauses before writer");
        Finish(Start(false)); Program.Check(requests.Calls.Count==2 && studio.State.Result=="WRITER_RESULT","manual writer command uses prepared task");

        Reset(); requests.Handler=async (_,_,ct)=>{await Task.Delay(Timeout.Infinite,ct); return Reply("");};
        var pending=Start(); Program.Pump(); Program.Check(studio.IsWorking,"advisor cancellation starts a pending operation"); Stop(); Finish(pending);
        Program.Check(requests.Calls.Count==1 && studio.State.Role==LiteraryChatProfile.Advisor && studio.State.Input.Length==0
            && new LiteraryStudioStore(new(root)).Load().Messages.Last().Text=="USER_INSTRUCTION","cancelled preparation keeps submitted input in durable history and does not run writer");

        Reset(); requests.Handler=async (r,progress,ct)=>
        {
            if(r.Action=="Transfer") return Reply("HIDDEN_BRIEF");
            progress.Report(new("PARTIAL_WRITER")); await Task.Delay(Timeout.Infinite,ct); return Reply("");
        };
        pending=Start(); Program.Pump(); Stop(); Finish(pending);
        Program.Check(studio.State.Task=="HIDDEN_BRIEF" && studio.State.Result.Length==0
            && studio.State.Messages.Any(m=>m.Text=="PARTIAL_WRITER" && !m.Complete),"writer cancellation keeps task and marks partial output incomplete");
        Program.Check(!studio.IsWorking && !studio.State.Interrupted,"cancelled chain releases controls");

        Reset(); requests.Handler=(_,_,_)=>throw new IOException("PREPARATION_FAILED"); Finish(Start());
        Program.Check(requests.Calls.Count==1 && studio.State.Input.Length==0 && studio.State.Task.Length==0
            && new LiteraryStudioStore(new(root)).Load().Messages.Last().Text=="USER_INSTRUCTION","preparation failure cannot start writer or lose submitted input");

        Reset(); requests.Handler=(r,_,_)=>r.Action=="Transfer"?Task.FromResult(Reply("HIDDEN_BRIEF")):throw new IOException("WRITER_FAILED"); Finish(Start());
        Program.Check(studio.State.Task=="HIDDEN_BRIEF" && studio.State.Role==LiteraryChatProfile.Writer
            && ((Button)Program.Field(studio,"_send")!).IsEnabled,"writer failure leaves a reusable task for manual retry");

        Reset(); requests.Handler=(_,_,_)=>
        {
            ((TextBox)Program.Field(draft,"_editor")!).AppendText(" EDIT_DURING_TRANSFER");
            return Task.FromResult(Reply("HIDDEN_BRIEF"));
        }; Finish(Start());
        Program.Check(requests.Calls.Count==1 && studio.State.Role==LiteraryChatProfile.Advisor && !BriefVisible(),"stale editor cancels handoff without exposing hidden task");

        Reset(); requests.Handler=(_,_,_)=>
        {
            var external=new LiteraryStudioStore(new(root)); var data=external.Load(); data.Add("User","EXTERNAL_EDIT"); external.Save(data);
            return Task.FromResult(Reply("HIDDEN_BRIEF"));
        }; Finish(Start());
        Program.Check(requests.Calls.Count==1 && new LiteraryStudioStore(new(root)).Load().Messages.Last().Text=="EXTERNAL_EDIT","failed handoff save stops writer and preserves concurrent history");
        Reset(); studio.State.Quotes.Add(new("SOURCE","UNSENT_QUOTE"));
        Finish(Start(false));
        Program.Check(requests.Calls.Count==0 && studio.State.Input=="USER_INSTRUCTION" && input.Text=="USER_INSTRUCTION"
            && studio.State.Quotes.Single().Text=="UNSENT_QUOTE","initial save failure preserves unsent input and quotes without calling model");
        Program.Check(!studio.State.Messages.Any(m=>m.Session==studio.State.Session && m.Role=="User"),"rejected submission is not falsely logged as sent");
        window.Close(); Program.Pump();
    }
    private static ParagraphReply Reply(string text)=>new(text,[],new([],[]));
    private sealed class Requests : ILiteraryStudioRequests
    {
        public bool IsBusy {get;set;}
        public int ContextCapacity=>8192;
        public List<StudioRequest> Calls {get;}=[];
        public Task? Queue {get;set;}
        public Func<StudioRequest,IProgress<ModelStreamChunk>,CancellationToken,Task<ParagraphReply>> Handler {get;set;}=null!;
        public async Task<ParagraphReply> StudioAsync(StudioRequest request,Func<string,string> localize,Action<ParagraphReceipt> receipt,
            Action<int> budget,IProgress<ModelStreamChunk> progress,CancellationToken cancellation,Action? attemptStarting=null,Action? preparationStarted=null)
        {
            cancellation.ThrowIfCancellationRequested(); Calls.Add(request);
            if(Queue is not null) await Queue.WaitAsync(cancellation);
            preparationStarted?.Invoke(); budget(123); return await Handler(request,progress,cancellation);
        }
    }
}
