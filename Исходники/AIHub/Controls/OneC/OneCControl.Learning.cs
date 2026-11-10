using System.Windows;
using System.Windows.Controls;
using AIHub.Models.OneC;
using AIHub.Services.OneC;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private void BuildLearning(StackPanel body)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(T("LessonsHint"), true));
        foreach (var lesson in OneCLessons.All)
        {
            var row = new WrapPanel(); panel.Children.Add(row);
            row.Children.Add(Button(lesson.Key, async () =>
            {
                if (Project is null || !await SaveDraftAsync()) return;
                var task = new OneCTask { Mode = "learn", Prompt = T(lesson.Key + "Task") };
                Project.Tasks.Add(task); Project.ActiveTaskId = task.Id; Project.ActiveProposalId = "";
                var name = lesson.Id + "-" + (Project.Files.Count + 1) + ".bsl";
                var file = Store.CreateFile(Project, name, lesson.Attempt);
                OneCProtocol.Bind(task, file); Project.OpenFileIds.Add(file.Id); Project.ActiveFileId = file.Id;
                Store.Save(Project); ForgetWidgets(); Render();
            }, "Lesson." + lesson.Id, true));
            row.Children.Add(Button("LessonHint", () =>
            {
                var w = Child(T(lesson.Key), 600, 350);
                w.Content = new TextBlock { Text = T(lesson.Key + "Hint"), TextWrapping = TextWrapping.Wrap, Margin = new(22) }; w.Show(); return Task.CompletedTask;
            }, "LessonHint." + lesson.Id));
            row.Children.Add(Button("LessonSolution", () =>
            {
                var w = Child(T("LessonSolution") + " · " + T(lesson.Key));
                var editor = new OneCCodeEditor(_l, "OneC.LessonSolution") { ReadOnly = true, Margin = new(14) };
                editor.SetText(lesson.Solution); w.Content = editor; w.Show(); return Task.CompletedTask;
            }, "LessonSolution." + lesson.Id));
        }
        body.Children.Add(new Expander { Header = T("Lessons"), Content = panel, Margin = new(3), IsExpanded = Project!.Task.Prompt.Length == 0 });
    }
}
