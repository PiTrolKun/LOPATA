using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public partial class ImageAnalysisBundleSelectorControl : UserControl
{
    private IReadOnlyList<ImageAnalysisBundleDefinition> _bundles = [];
    private ImageAnalysisRecommendationResult? _result;
    private Func<string, string> _localize = key => key;
    private Func<string, object[], string> _format = (key, _) => key;
    private ImageAnalysisBundleDefinition? _noticeBundle;
    private readonly InterfaceLayoutService _interfaceLayoutService = new();
    private int _currentColumnCount = 3;

    public ImageAnalysisBundleSelectorControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsiveLayout(ActualWidth);
    }

    public event EventHandler? BackRequested;

    public event EventHandler<ImageAnalysisBundleSelectedEventArgs>? BundleSelected;

    public event EventHandler<ImageAnalysisBundleSelectedEventArgs>? UnavailableBundleRequested;

    public void UpdateResponsiveLayout(double availableWidth)
    {
        var width = ActualWidth > 0 ? ActualWidth : availableWidth;
        var columns = _interfaceLayoutService.GetBundleColumnCount(width);
        if (columns == _currentColumnCount)
        {
            return;
        }

        _currentColumnCount = columns;
        BundleItemsControl.ItemsPanel = (ItemsPanelTemplate)FindResource(columns switch
        {
            1 => "BundleItemsPanelOneColumn",
            2 => "BundleItemsPanelTwoColumns",
            _ => "BundleItemsPanelThreeColumns"
        });
    }

    public void Configure(
        IReadOnlyList<ImageAnalysisBundleDefinition> bundles,
        ImageAnalysisRecommendationResult result,
        Func<string, string> localize,
        Func<string, object[], string> format)
    {
        _bundles = bundles;
        _result = result;
        _localize = localize;
        _format = format;
        _noticeBundle = null;
        NoticePanel.Visibility = Visibility.Collapsed;
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        TitleText.Text = _localize("ImageAnalysis.Selector.Title");
        DescriptionText.Text = _localize("ImageAnalysis.Selector.Description");
        BackButton.Content = _localize("Settings.Back");
        RecommendationSummaryText.Text = BuildRecommendationSummary();
        var hasRecommendation = _result?.Recommendation is not null
            && _result.HasCompleteHardwareData;
        RecommendationSummaryPanel.SetResourceReference(
            Border.BackgroundProperty,
            hasRecommendation ? "StepBadgeBrush" : "SecondaryButtonBackgroundBrush");
        RecommendationSummaryPanel.SetResourceReference(
            Border.BorderBrushProperty,
            hasRecommendation ? "AccentBrush" : "LineBrush");

        BundleItemsControl.ItemsSource = _bundles
            .OrderBy(bundle => bundle.Level)
            .Select(CreateCard)
            .ToList();

        if (_noticeBundle is not null)
        {
            NoticeText.Text = _format(
                "ImageAnalysis.Bundle.UnavailableExplanation",
                [_localize(_noticeBundle.TitleKey)]);
        }
    }

    private ImageAnalysisBundleCardViewModel CreateCard(ImageAnalysisBundleDefinition bundle)
    {
        var assessment = _result?.Assessments.FirstOrDefault(item =>
            string.Equals(item.Bundle.Id, bundle.Id, StringComparison.Ordinal));
        var isRecommended = string.Equals(
            _result?.Recommendation?.Bundle.Id,
            bundle.Id,
            StringComparison.Ordinal);
        var actionText = bundle.IsAvailable
            ? _localize("ImageAnalysis.Bundle.Continue")
            : _localize("ImageAnalysis.Bundle.ShowExplanation");
        var isGamma = ImageAnalysisModeCapabilities.UsesOmniConversation(bundle.Id);
        var adviceKey = assessment?.HasCompleteHardwareData != true
            ? "ImageAnalysis.Bundle.Advice.Unknown"
            : assessment.IsFullyCompatible
                ? "ImageAnalysis.Bundle.Advice.Suitable"
                : "ImageAnalysis.Bundle.Advice.Limited";

        return new ImageAnalysisBundleCardViewModel
        {
            Definition = bundle,
            Title = _localize(bundle.TitleKey),
            Symbol = bundle.Id switch
            {
                ImageAnalysisBundleCatalog.LightId => "α",
                ImageAnalysisBundleCatalog.MediumId => "β",
                ImageAnalysisBundleCatalog.HeavyId => "γ",
                _ => string.Empty
            },
            Purpose = _localize(isGamma ? adviceKey : bundle.PurposeKey),
            Status = _localize(bundle.StatusKey),
            ShowStatus = !bundle.IsAvailable,
            ModelsHeader = _localize(isGamma ? "ImageAnalysis.Bundle.UsedModels" : "ImageAnalysis.Bundle.ModelsHeader"),
            RecommendationLabel = _localize(isGamma && assessment?.IsFullyCompatible != true
                ? "ImageAnalysis.Bundle.ProgramAdvice" : "ImageAnalysis.Bundle.Recommended"),
            ShowRecommendationLabel = isGamma || isRecommended,
            ActionText = actionText,
            AutomationName = $"{_localize(bundle.TitleKey)}. {actionText}",
            Components = isGamma ?
            [
                new() { Role = _localize("ImageAnalysis.Bundle.AnalysisRole"), Model = bundle.Components[0].ModelName, ShowPlacement = false },
                new() { Role = _localize("ImageAnalysis.Bundle.OptionalSpeechRole"), Model = "Kokoro", ShowPlacement = false }
            ] : bundle.Components.Select(component => new ImageAnalysisBundleComponentViewModel
            {
                Role = _localize(component.RoleKey),
                Model = component.ModelName,
                Placement = _localize(component.PlacementKey)
            }).ToList(),
            RequirementsText = isGamma
                ? bundle.Id == ImageAnalysisBundleCatalog.LightId
                    ? _localize("ImageAnalysis.Bundle.Alpha.Requirements")
                    : bundle.Id == ImageAnalysisBundleCatalog.MediumId ? _localize("ImageAnalysis.Bundle.Beta.Requirements")
                    : string.Join(Environment.NewLine + Environment.NewLine,
                    _localize("ImageAnalysis.Bundle.Gamma.Gpu"),
                    _localize("ImageAnalysis.Bundle.Gamma.Vram"),
                    _localize("ImageAnalysis.Bundle.Gamma.Ram"))
                : BuildRequirementsText(bundle.Requirements),
            HardwareText = isGamma ? string.Empty : BuildHardwareText(assessment),
            ShowHardware = !isGamma,
            IsRecommended = isRecommended,
            AnimateRecommendation = isRecommended && SystemParameters.ClientAreaAnimation
        };
    }

    private string BuildRecommendationSummary()
    {
        var recommendation = _result?.Recommendation;
        if (_result?.HasCompleteHardwareData != true || recommendation is null)
        {
            return _localize("ImageAnalysis.Recommendation.Unknown");
        }

        var title = _localize(recommendation.Bundle.TitleKey);
        if (!recommendation.IsFullyCompatible)
        {
            return _format("ImageAnalysis.Recommendation.Closest", [title]);
        }

        if (recommendation.Bundle.IsAvailable)
        {
            return _format("ImageAnalysis.Recommendation.Available", [title]);
        }

        var currentBundle = _bundles.FirstOrDefault(bundle => bundle.IsCurrentProjectBundle);
        var currentTitle = currentBundle is null
            ? _localize("ImageAnalysis.Bundle.Medium")
            : _localize(currentBundle.TitleKey);
        return _format("ImageAnalysis.Recommendation.Unavailable", [title, currentTitle]);
    }

    private string BuildRequirementsText(ImageAnalysisHardwareRequirements requirements) =>
        _format(
            "ImageAnalysis.Bundle.Requirements",
            [
                FormatNumber(requirements.RamGb),
                FormatNumber(requirements.VramGb),
                requirements.LogicalProcessorCount,
                FormatNumber(requirements.FreeDiskGb)
            ]);

    private string BuildHardwareText(ImageAnalysisBundleAssessment? assessment)
    {
        if (assessment is null)
        {
            return _localize("ImageAnalysis.Hardware.Unknown");
        }

        return string.Join(
            Environment.NewLine,
            assessment.Resources.Select(resource =>
            {
                var status = resource.IsSatisfied switch
                {
                    true => _localize("ImageAnalysis.Hardware.Enough"),
                    false => _localize("ImageAnalysis.Hardware.Short"),
                    _ => _localize("ImageAnalysis.Hardware.UnknownValue")
                };
                var actual = resource.ActualValue.HasValue
                    ? FormatNumber(resource.ActualValue.Value)
                    : _localize("Common.Unknown");
                var key = resource.ResourceId == "cpu"
                    ? "ImageAnalysis.Hardware.CpuLine"
                    : $"ImageAnalysis.Hardware.{resource.ResourceId}.Line";
                return _format(key, [actual, FormatNumber(resource.RequiredValue), status]);
            }));
    }

    private static string FormatNumber(double value) =>
        value.ToString(value % 1 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);

    private void BundleCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ImageAnalysisBundleDefinition bundle })
        {
            return;
        }

        var args = new ImageAnalysisBundleSelectedEventArgs(bundle);
        if (bundle.IsAvailable)
        {
            _noticeBundle = null;
            NoticePanel.Visibility = Visibility.Collapsed;
            BundleSelected?.Invoke(this, args);
            return;
        }

        _noticeBundle = bundle;
        NoticeText.Text = _format(
            "ImageAnalysis.Bundle.UnavailableExplanation",
            [_localize(bundle.TitleKey)]);
        NoticePanel.Visibility = Visibility.Visible;
        UnavailableBundleRequested?.Invoke(this, args);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class ImageAnalysisBundleSelectedEventArgs(
    ImageAnalysisBundleDefinition bundle) : EventArgs
{
    public ImageAnalysisBundleDefinition Bundle { get; } = bundle;
}

public sealed class ImageAnalysisBundleCardViewModel
{
    public required ImageAnalysisBundleDefinition Definition { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public bool ShowStatus { get; init; }

    public bool ShowRecommendationLabel { get; init; }

    public bool ShowHardware { get; init; }

    public string Purpose { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string ModelsHeader { get; init; } = string.Empty;

    public string RecommendationLabel { get; init; } = string.Empty;

    public string RequirementsText { get; init; } = string.Empty;

    public string HardwareText { get; init; } = string.Empty;

    public string ActionText { get; init; } = string.Empty;

    public string AutomationName { get; init; } = string.Empty;

    public IReadOnlyList<ImageAnalysisBundleComponentViewModel> Components { get; init; } = [];

    public bool IsRecommended { get; init; }

    public bool AnimateRecommendation { get; init; }
}

public sealed class ImageAnalysisBundleComponentViewModel
{
    public bool ShowPlacement { get; init; } = true;

    public string Role { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string Placement { get; init; } = string.Empty;
}
