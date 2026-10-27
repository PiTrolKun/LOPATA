using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private ExampleTextInput? _ideaInput;

    private void BuildIdea(StackPanel page)
    {
        VideoConceptHistory.Capture(ActiveText);
        var form = Text("SelectedForm");
        form.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, T("SelectedForm"), T("Form." + ActiveText.Form));
        page.Children.Add(form);
        page.Children.Add(Text(IsDevelopmentStep ? "DevelopmentHint" : "IdeaHint"));
        page.Children.Add(Text((IsDevelopmentStep ? "DevelopmentHint." : "IdeaHint.") + Session.Project.Form));
        page.Children.Add(Text(IsDevelopmentStep ? "DevelopmentLabel" : "IdeaLabel"));
        var input = new ExampleTextInput(T(IsDevelopmentStep ? "DevelopmentLabel" : "IdeaLabel"), T((IsDevelopmentStep ? "DevelopmentExample." : "IdeaExample.") + Session.Project.Form), IsDevelopmentStep ? "VideoPrep.Development" : "VideoPrep.Idea", multiline: true);
        _ideaInput = input;
        var material = ActiveText;
        input.Input.Text = material.IdeaText;
        LiterarySpellChecking.Enable(input.Input, InputLanguage);
        input.Input.TextChanged += (_, _) =>
        {
            material.IdeaText = input.Input.Text;
            Changed();
            RefreshConceptHistory();
        };
        page.Children.Add(input);
        BuildConceptTools(page);
        page.Children.Add(Text("AutomaticLanguageHint"));
    }

    private bool HasIdeaForNext()
    {
        if (!string.IsNullOrWhiteSpace(ActiveText.IdeaText)) return true;
        _statusKey = IsDevelopmentStep ? "MissingDevelopment" : "MissingIdea";
        RefreshStatus();
        _ideaInput?.Input.Focus();
        _ideaInput?.BringIntoView();
        return false;
    }
}
