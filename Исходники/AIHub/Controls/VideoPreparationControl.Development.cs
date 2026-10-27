using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private static string DevelopmentKey(string key) => key switch
    {
        "TranslationHint" => "DevelopmentTranslationHint",
        "TranslationSource" => "DevelopmentTranslationSource",
        "EnglishLabel" => "DevelopmentEnglishLabel",
        "EnglishExample" => "DevelopmentEnglishExample",
        "TranslationStale" => "DevelopmentTranslationStale",
        "TranslationConfirmSource" => "DevelopmentTranslationConfirmSource",
        "AiWorking" => "DevelopmentWorking",
        "AiReady" => "DevelopmentReady",
        "AiError" => "DevelopmentError",
        "TranslationWorking" => "DevelopmentTranslationWorking",
        "AiCanceled" => "DevelopmentCanceled",
        "AiTimeout" => "DevelopmentTimeout",
        _ => key
    };
    private bool IsDevelopmentStep => VideoDevelopment.IsStep(Session.Project.CurrentStep);
    private VideoPreparationText ActiveText => IsWorldStep ? Session.Project.CurrentStep == VideoWorld.TranslationPurpose
        ? VideoWorld.Ensure(Session.Project).Working : SelectedWorldCard.Text
        : IsDevelopmentStep ? VideoDevelopment.Ensure(Session.Project) : Session.Project;

    private void BuildDevelopment(StackPanel page)
    {
        var source = new StackPanel();
        var original = Text("IdeaLabel"); original.Text = Session.Project.IdeaText; source.Children.Add(original);
        var working = Text("EnglishLabel"); working.Text = Session.Project.EnglishText; source.Children.Add(working);
        var context = new Expander { Header = T("DevelopmentSource"), Content = source, Margin = new Thickness(0, 4, 0, 8) };
        context.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); page.Children.Add(context);
        BuildDevelopmentSourceState(page);
        BuildIdea(page);
    }

    private void BuildDevelopmentSourceState(StackPanel page)
    {
        if (VideoDevelopment.SourceCurrent(Session.Project)) return;
        page.Children.Add(Text("DevelopmentStale"));
        page.Children.Add(Text("ReviewNextHint"));
    }

    private bool DevelopmentSourceAvailable()
    {
        if (VideoTranslationHistory.IsCurrent(Session.Project) && !string.IsNullOrWhiteSpace(Session.Project.EnglishText)) return true;
        _statusKey = "DevelopmentNeedsConcept"; RefreshStatus(); return false;
    }

    private bool DevelopmentReviewed()
    {
        if (!IsDevelopmentStep || VideoDevelopment.SourceCurrent(Session.Project)) return true;
        _statusKey = "DevelopmentStale"; RefreshStatus(); return false;
    }
}
