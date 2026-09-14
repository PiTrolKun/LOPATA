using System.Text.Json;
using AIHub.Services;

static class Extras {
 public static async Task Run(LiteraryChatRuntime runtime,string root){
  CalibrationRequest Request(string check,string text,string manual="",string language="ru")=>new(check,manual,language,[new("f0",1,"Описание",text)],false);
  var results=new List<object>();
  foreach(var key in LiteraryCalibrationAnalysis.Checks.Keys){try{var r=await runtime.CalibrateAsync(Request(key,"Небольшой город. Инженер строит лодку."),CancellationToken.None);results.Add(new{key,result=r});Console.WriteLine(key+": valid");}catch(Exception ex){results.Add(new{key,error=ex.Message});Console.WriteLine(key+": rejected "+ex.Message);}}
  var manual=await runtime.CalibrateAsync(Request("Manual","Anna lives in a small town.","Check the wording without rewriting it.","en"),CancellationToken.None);results.Add(new{manual});
  try{await runtime.CalibrateAsync(Request("Meaning",string.Concat(Enumerable.Repeat("Длинное описание мира и главного героя. ",20000))),CancellationToken.None);throw new Exception("Budget was not enforced");}catch(ImageAnalysisContextExhaustedException){Console.WriteLine("PASS context budget");}
  if(runtime.IsBusy)throw new Exception("Busy after budget failure");
  using var cancel=new CancellationTokenSource();var pending=runtime.CalibrateAsync(Request("Clarity",string.Concat(Enumerable.Repeat("Он встретил его у его дома. ",150))),cancel.Token);
  try{await runtime.CalibrateAsync(Request("Meaning","Другой запрос"),CancellationToken.None);throw new Exception("Gate was not enforced");}catch(InvalidOperationException ex)when(ex.Message.Contains("Another literary")){Console.WriteLine("PASS shared gate");}
  await Task.Delay(250);cancel.Cancel();try{await pending;throw new Exception("Not cancelled");}catch(OperationCanceledException){Console.WriteLine("PASS cancellation");}
  if(runtime.IsBusy)throw new Exception("Busy after cancellation");
  await runtime.CalibrateAsync(Request("Retelling","История инженера в маленьком городе."),CancellationToken.None);Console.WriteLine("PASS request after cancellation");
  File.WriteAllText(Path.Combine(root,"extras.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
  Console.WriteLine(root);
 }
}
