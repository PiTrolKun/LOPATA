using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Controls.AnimatedAssets;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.AnimatedAssets;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class AnimationWorkshopUiTests
{
    [TestMethod]
    public Task HomeSmileIsNextToGameAndUsesPreparationBeforeOpeningSameHost() => ScenarioNavigationTests.Sta(() =>
    {
        var host = ScenarioNavigationTests.Create("ru", 1, true);
        var game = FindButton(host, "Arcade.Open");
        var smile = FindButton(host, "Animation.Open");
        Assert.AreSame(game.Parent, smile.Parent);
        var row = (StackPanel)smile.Parent;
        Assert.AreEqual(row.Children.IndexOf(game) + 1, row.Children.IndexOf(smile));
        var language = new LocalizationService(); language.Load("ru");
        var model = new AnimationModelService(new StorageSettings(), 1, language.T);
        var workshop = new AnimationWorkshopControl(language.T, model);
        var verifications = 0;
        var downloads = 0;
        var preparation = new AnimationPreparationControl(language.T,
            () => new("Gemma", false, "Qwen", false, true),
            _ => { verifications++; return Task.CompletedTask; },
            (_, _) => { downloads++; return Task.CompletedTask; });
        preparation.ContinueRequested += () => host.ShowAnimatedAssets(workshop);
        var requested = "";
        host.ScenarioRequested += id =>
        {
            requested = id;
            if (id == ScenarioNavigationCatalog.AnimatedAssets) host.ShowAnimatedAssetsPreparation(preparation);
        };
        Click(smile);
        Assert.AreEqual(ScenarioNavigationCatalog.AnimatedAssets, requested);
        Assert.AreSame(preparation, host.Content);
        Assert.IsTrue(host.IsAnimatedAssetsPreparation);
        Assert.IsFalse(host.IsAnimatedAssets);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(preparation).OfType<CheckBox>().Any());
        Assert.IsFalse(FindButton(preparation, "Animation.Preparation.Continue").IsEnabled);

        Click(FindButton(preparation, "Animation.Preparation.Check"));
        Assert.AreEqual(1, verifications);
        Assert.AreEqual(0, downloads);
        Assert.IsTrue(FindButton(preparation, "Animation.Preparation.Download").IsEnabled);
        var openWorkshop = FindButton(preparation, "Animation.Preparation.Continue");
        Assert.IsTrue(openWorkshop.IsEnabled, "AI models are optional for quick animation.");
        Click(openWorkshop);
        Assert.AreSame(workshop, host.Content);
        Assert.IsTrue(host.IsAnimatedAssets);
        Assert.IsFalse(host.IsAnimatedAssetsPreparation);
        Assert.IsTrue(FindButton(workshop, "Animation.Quick").IsEnabled);
        Assert.IsTrue(FindButton(workshop, "Animation.Keyframes").IsEnabled);
        var setupControls = ScenarioNavigationTests.LogicalDescendants(workshop).ToArray();
        Assert.AreEqual(4, setupControls.OfType<RadioButton>().Count());
        var secondary = setupControls.OfType<Expander>()
            .Where(section => AutomationProperties.GetAutomationId(section) is "Animation.Projects" or "Animation.Components").ToArray();
        Assert.AreEqual(2, secondary.Length);
        Assert.IsTrue(secondary.All(section => !section.IsExpanded));
        Assert.IsFalse(setupControls.OfType<Button>()
            .Any(button => AutomationProperties.GetAutomationId(button) == "Animation.Back"));

        Click(FindButton(workshop, "Animation.Quick"));
        Assert.IsTrue(FindButton(workshop, "Animation.LoadImage").IsEnabled);
        Assert.IsTrue(host.GoBack());
        Assert.AreSame(workshop, host.Content);
        Click(FindButton(workshop, "Animation.Keyframes"));
        Assert.IsTrue(FindButton(workshop, "Animation.DrawReference").IsEnabled);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(workshop).OfType<Button>()
            .Any(button => AutomationProperties.GetAutomationId(button) == "Animation.EditReference"));
        Assert.IsTrue(host.GoBack());
        Assert.IsTrue(host.GoBack());
        Assert.IsTrue(host.IsHome);
        Assert.IsFalse(host.IsAnimatedAssets);
    });

    [TestMethod]
    public Task BackFromPreparationRestoresHomeOrTheCurrentWorkshop() => ScenarioNavigationTests.Sta(() =>
    {
        var host = ScenarioNavigationTests.Create("ru", 1, true);
        var language = new LocalizationService(); language.Load("ru");
        AnimationPreparationControl CreatePreparation() => new(language.T,
            () => new("Gemma", true, "Qwen", true, true),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        host.ShowAnimatedAssetsPreparation(CreatePreparation());
        Assert.IsTrue(host.IsAnimatedAssetsPreparation);
        Assert.IsTrue(host.GoBack());
        Assert.IsTrue(host.IsHome);
        Assert.IsFalse(host.IsAnimatedAssetsPreparation);

        var model = new AnimationModelService(new StorageSettings(), 1, language.T);
        var workshop = new AnimationWorkshopControl(language.T, model);
        host.ShowAnimatedAssets(workshop);
        var preparation = CreatePreparation();
        host.ShowAnimatedAssetsPreparation(preparation, returnToWorkshop: true);
        preparation.CheckAsync().GetAwaiter().GetResult();
        Assert.IsTrue(host.IsAnimatedAssetsPreparation);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(preparation).OfType<Button>()
            .Any(button => AutomationProperties.GetAutomationId(button) == "Animation.Preparation.Download"
                && button.Visibility == Visibility.Visible));
        Assert.IsTrue(host.GoBack());
        Assert.AreSame(workshop, host.Content);
        Assert.IsTrue(host.IsAnimatedAssets);
        Assert.IsFalse(host.IsAnimatedAssetsPreparation);
    });

    [TestMethod]
    public Task PreparationButtonsFollowTheThemeWithoutReopeningThePage() => ScenarioNavigationTests.Sta(() =>
    {
        var host = ScenarioNavigationTests.Create("ru", 1, true);
        var preparation = new AnimationPreparationControl(key => key,
            () => new("Gemma", false, "Qwen", false, true),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        host.ShowAnimatedAssetsPreparation(preparation);
        preparation.CheckAsync().GetAwaiter().GetResult();
        var buttons = new[] { "Check", "Download", "Continue" }
            .Select(id => FindButton(preparation, "Animation.Preparation." + id)).ToArray();
        var size = new Size(1100, 650);

        foreach (bool dark in new[] { true, false, true })
        {
            var background = dark ? Color.FromRgb(17, 24, 39) : Color.FromRgb(248, 248, 248);
            var foreground = dark ? Color.FromRgb(248, 250, 252) : Color.FromRgb(31, 31, 31);
            var border = dark ? Color.FromRgb(45, 55, 75) : Color.FromRgb(218, 221, 227);
            host.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(background);
            host.Resources["TextPrimaryBrush"] = new SolidColorBrush(foreground);
            host.Resources["LineBrush"] = new SolidColorBrush(border);
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();

            foreach (var button in buttons)
            {
                var frame = button.Template.FindName("Frame", button) as Border;
                Assert.IsNotNull(frame);
                Assert.AreEqual(new CornerRadius(8), frame.CornerRadius);
                Assert.AreEqual(background, ((SolidColorBrush)frame.Background).Color);
                Assert.AreEqual(foreground, ((SolidColorBrush)button.Foreground).Color);
                Assert.AreEqual(border, ((SolidColorBrush)frame.BorderBrush).Color);
            }
        }
    });

    [TestMethod]
    public async Task WorkshopPreparationUsesTheSharedAuditPolicyForItsBundledRuntime()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-animation-audit-");
        try
        {
            string receipt;
            string firstAudit;
            using (ComponentAudit.UseTestStore(directory.FullName, enabled: false))
            {
                await AnimationModelService.PrepareBaseAsync(CancellationToken.None);
                receipt = Directory.GetFiles(Path.Combine(directory.FullName, "receipts"), "*.json").Single();
                firstAudit = await File.ReadAllTextAsync(receipt);
                await AnimationModelService.PrepareBaseAsync(CancellationToken.None);
                Assert.AreEqual(firstAudit, await File.ReadAllTextAsync(receipt), "Quick checks reuse the first full audit.");
            }
            using (ComponentAudit.UseTestStore(directory.FullName, enabled: true))
            {
                await AnimationModelService.PrepareBaseAsync(CancellationToken.None);
                Assert.AreNotEqual(firstAudit, await File.ReadAllTextAsync(receipt), "Full audit verifies the runtime again.");
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public void WinkAndLegacySequenceSymbolBecomeAnExplicitArtistInstruction()
    {
        var language = new LocalizationService();
        language.Load("ru");
        string Text(string key) => language.T("Animation." + key);

        var instruction = AnimationWorkshopControl.ReactionInstruction("😉", "Соблазнительно подмигивает", Text);
        StringAssert.Contains(instruction, "Закрой ровно один глаз");
        StringAssert.Contains(instruction, "Второй глаз оставь открытым");
        StringAssert.Contains(instruction, "Соблазнительно подмигивает");
        var sequence = AnimationWorkshopControl.ReactionInstruction(";)", "", Text);
        StringAssert.Contains(sequence, "Закрой ровно один глаз");
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void RedrawingUsesTheLatestDescriptionAndOnlyAnExplicitGuide(string language)
    {
        var localization = new LocalizationService();
        localization.Load(language);
        string Text(string key) => localization.T("Animation." + key);

        var project = new AnimationProject { Reference = [1], ReferenceIsEmoji = true, ReferenceDescription = "Rounded cat" };
        Assert.IsNull(AnimationWorkshopControl.ReferenceDrawingRequest(project, editCurrent: false, Text).Source);

        project.ReferenceDescription = "Photorealistic black cat with a sly expression";
        var redraw = AnimationWorkshopControl.ReferenceDrawingRequest(project, editCurrent: false, Text);
        Assert.IsNull(redraw.Source);
        StringAssert.Contains(redraw.Prompt, project.ReferenceDescription);
        Assert.IsFalse(redraw.Prompt.Contains("Rounded cat", StringComparison.Ordinal));
        Assert.IsFalse(redraw.Prompt.Contains(Text("CalmFace"), StringComparison.Ordinal));

        project.ReferenceGuide = [2];
        Assert.AreSame(project.ReferenceGuide, AnimationWorkshopControl.ReferenceDrawingRequest(project, editCurrent: false, Text).Source);

        var edit = AnimationWorkshopControl.ReferenceDrawingRequest(project, editCurrent: true, Text);
        Assert.AreSame(project.Reference, edit.Source);
        Assert.AreEqual(project.ReferenceDescription, edit.Prompt);

        project.ReferenceDescription = "";
        StringAssert.Contains(AnimationWorkshopControl.ReferenceDrawingRequest(project, editCurrent: false, Text).Prompt, Text("CalmFace"));
    }

    [TestMethod]
    public void SavedDoubtPresetReplacesTheOldGrimaceWhilePreservingManualInstructions()
    {
        var language = new LocalizationService();
        language.Load("ru");
        string Text(string key) => language.T("Animation." + key);

        var project = new AnimationProject { ReferenceIsEmoji = true, ReferenceEmoji = "😜" };
        var card = new AnimationCard { Kind = "reaction" };
        var pose = new AnimationPose
        {
            Text = "🤔",
            Prompt = string.Format(Text("ReactionEditPrompt"), "Сомнение", project.ReferenceEmoji)
        };
        var visible = AnimationWorkshopControl.ReactionPoseInstruction(project, card, pose, Text);
        StringAssert.Contains(visible, "Убери язык");
        var artist = AnimationWorkshopControl.ReactionPoseInstruction(project, card, pose, Text, forArtist: true);
        StringAssert.Contains(artist, "Retract the tongue completely");
        StringAssert.Contains(artist, "open both eyes");
        Assert.IsFalse(artist.Contains(project.ReferenceEmoji, StringComparison.Ordinal));

        pose.Prompt = "Сделай прищур и поменяй направление взгляда.";
        Assert.AreEqual(pose.Prompt,
            AnimationWorkshopControl.ReactionPoseInstruction(project, card, pose, Text, forArtist: true));
    }

    [TestMethod]
    public void PoseBackgroundDistinguishesOpaquePhotosAndTransparentArtwork()
    {
        var pixels = Enumerable.Repeat((byte)255, 400 * 4).ToArray();
        pixels[3] = 252;
        pixels[7] = 0;
        Assert.IsFalse(AnimationModelService.HasVisibleTransparency(pixels));

        for (var index = 0; index < 20; index++)
            pixels[index * 4 + 3] = 0;
        Assert.IsTrue(AnimationModelService.HasVisibleTransparency(pixels));
    }

    [TestMethod]
    public void ContinueDraftReturnsToPreviewAfterBackToSetup()
    {
        var project = new AnimationProject
        {
            Page = 0, Reference = [1], ReferenceApproved = true,
            Cards = [new AnimationCard { Poses = [new AnimationPose { Time = 3.5, Image = [2] }], Duration = 7 }]
        };
        Assert.AreEqual(6, AnimationWorkshopControl.DraftResumePage(project));
        project.Cards[0].Poses[0].Image = null;
        Assert.AreEqual(5, AnimationWorkshopControl.DraftResumePage(project));
        Assert.AreEqual(0, project.Page);
    }

    private static Button FindButton(DependencyObject root, string automationId) =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == automationId);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
