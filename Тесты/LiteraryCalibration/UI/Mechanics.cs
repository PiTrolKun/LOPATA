using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using System.Text.Json;

static class Mechanics {
 static IEnumerable<T> All<T>(DependencyObject o){if(o is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++)foreach(var a in All<T>(VisualTreeHelper.GetChild(o,i)))yield return a;}
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
 public static void Run(Func<string,string> l){
  TaskCompletionSource<CalibrationResult>? pending=null;CalibrationRequest? captured=null;CancellationToken active=default;
  var initial=JsonSerializer.Serialize(new{sections=new[]{new{Topic=1,Question="q1",Text="кот живёт дома"},new{Topic=2,Question="q2",Text="другой блок"}}});string saved="";
  var w=new LiteraryCalibrationWindow(l,initial,"ru",s=>saved=s,(r,ct)=>{captured=r;active=ct;pending=new();return pending.Task;}){Left=-10000,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};w.Show();Pump();
  var inputs=All<TextBox>(w).Where(b=>!Equals(b.Tag,"CalibrationAnalysisRequest")).ToArray();var panels=All<LiteraryCalibrationAnalysisPanel>(w).ToArray();
  void Click(int p=0)=>All<Button>(panels[p]).First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  void Finish(CalibrationResult result){pending!.SetResult(result);Pump();w.UpdateLayout();}
  bool Status(string key)=>All<TextBlock>(w).Any(t=>t.Text==l(key));
  int Spans()=>inputs.Sum(i=>AdornerLayer.GetAdornerLayer(i)?.GetAdorners(i)?.OfType<CalibrationTextAdorner>().Sum(a=>a.Spans.Count)??0);
  var input=inputs[0];input.Select(0,3);input.SelectedText="пёс";var before=input.Text;
  Click();Check(captured!.Fields.Count==1&&captured.Fields[0].Text==before,"scope / unsaved text");Check(panels.All(p=>!All<Button>(p).First().IsEnabled),"busy buttons");
  Finish(new([new(new("f0",0,3),"Пояснение",[],["кот"])],false));Check(Spans()==1&&input.Text==before,"non-mutating marks");
  var badge=All<Button>(w).First(b=>Equals(b.Tag,"CalibrationFindingBadge"));Check(badge.Visibility==Visibility.Visible,"visible finding badge");
  badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
  var popupRoot=PresentationSource.CurrentSources.OfType<PresentationSource>().Select(p=>p.RootVisual).OfType<DependencyObject>().FirstOrDefault(p=>All<TextBlock>(p).Any(t=>t.Text=="Пояснение"));
  Check(popupRoot is not null,"left click opens explanation bubble");Check(input.Text==before,"opening does not edit");
  All<Button>(popupRoot!).Single(b=>Equals(b.Content,"×")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
  input.Undo();Check(input.Text=="кот живёт дома"&&Spans()==0,"undo must edit text and invalidate marks");
  Check(badge.Visibility==Visibility.Hidden,"editing removes badge");
  Click();input.AppendText(".");Finish(new([new(new("f0",0,3),"Устарело",[],[])],false));Check(Status("Literary.Analysis.Stale")&&Spans()==0,"stale rejected");
  Click();Finish(new([new(new("f0",0,3),"Пояснение",[],[])],false));Check(Spans()==1,"marks returned");
  Click();pending!.SetException(new InvalidOperationException("malformed reply"));Pump();Check(Status("Literary.Analysis.Failed")&&Spans()==1,"failure preserves previous marks");
  Click();All<Button>(panels[0]).Single(b=>Equals(b.Content,l("Literary.Analysis.Stop"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(active.IsCancellationRequested,"stop token");pending!.SetCanceled(active);Pump();Check(Status("Literary.Analysis.Cancelled")&&All<Button>(panels[0]).First().IsEnabled,"cancel releases controls");
  Click(2);Check(captured!.WholeProject&&captured.Fields.Count==2,"overall scope");Finish(new([new(new("f0",0,3),"Связь",[new("f1",0,6)],[])],false));Check(Spans()==2,"related marks");inputs[1].AppendText(".");Check(Spans()==0,"linked invalidation");
  All<Button>(w).Single(b=>Equals(b.Content,l("Literary.Anchor.Save"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(saved.Contains("sections"),"save");
  Click();w.Close();Check(w.IsVisible&&active.IsCancellationRequested,"close waits cancellation");pending!.SetCanceled(active);Pump();Check(!w.IsVisible,"close after idle");
  Console.WriteLine("PASS UI scope, stale, busy, undo, linked marks, failure, cancel, close");
  var field=new TextBox{Text="mispellled words",FontSize=22};LiterarySpellChecking.Enable(field,"en-US");
  var marks=new CalibrationMarkedText(field,"word",l);var menuWindow=new Window{Content=field,Width=400,Height=150,Left=-10000,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};menuWindow.Show();Pump();
  marks.Set([new(new("word",0,10),"Подсказка модели",[],["misspelled"])]);field.CaretIndex=0;
  var ctor=typeof(ContextMenuEventArgs).GetConstructors(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).First();
  var values=ctor.GetParameters().Select(p=>p.ParameterType==typeof(bool)?(object)true:p.ParameterType==typeof(double)?-1d:field).ToArray();
  var e=(ContextMenuEventArgs)ctor.Invoke(values);
  typeof(CalibrationMarkedText).GetMethod("OpenMenu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(marks,[field,e]);
  var menu=field.ContextMenu!.Items.OfType<MenuItem>().ToArray();
  Check(menu.Any(i=>i.Header is TextBlock t&&t.Text=="Подсказка модели"),"explanation in menu");
  Check(menu.Any(i=>Equals(i.ToolTip,l("Literary.Analysis.SpellingSuggestion"))),"native spelling suggestions coexist");
  menu.First(i=>Equals(i.ToolTip,l("Literary.Analysis.ModelSuggestion"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Check(field.Text=="misspelled words","manual word replacement");field.Undo();Check(field.Text=="mispellled words","word replacement undo");
  menuWindow.Close();Console.WriteLine("PASS model / spelling menus, explanation, word replacement and undo");
  var bubbleInput=new TextBox{Text="кот",FontSize=18};var bubble=new CalibrationFindingBubble(bubbleInput,"test",l);
  var bw=new Window{Resources=Application.Current.MainWindow.Resources,Content=bubble.Host,Width=550,Height=600,Left=-10000,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};bw.Show();Pump();
  bubble.Set([new(new("test",0,3),"Два обозначения одного героя. Проверьте, какое нужно сохранить.",[],["пёс"])]);
  bubble.Badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
  var popup=(System.Windows.Controls.Primitives.Popup)typeof(CalibrationFindingBubble).GetField("_popup",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(bubble)!;
  Check(popup.IsOpen,"bubble open");
  var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(220)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
  var visual=(FrameworkElement)popup.Child;var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(visual);
  var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using(var file=System.IO.File.Create("Тесты/LiteraryCalibration/runs/ui/bubble.png"))encoder.Save(file);
  All<Button>(popup.Child).Single(b=>Equals(b.Tag,"CalibrationBubbleReplacement")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(!popup.IsOpen&&bubbleInput.Text=="пёс","bubble word replacement");bubbleInput.Undo();Check(bubbleInput.Text=="кот","bubble replacement undo");
  bubble.Badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();bubbleInput.AppendText("!");Check(!popup.IsOpen,"edit closes bubble");bw.Close();
  Console.WriteLine("PASS finding bubble / left click / manual replacement / edit invalidation");
 }
}
