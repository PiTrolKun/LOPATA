using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Controls.VisualScenario;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VisualScenarioTests
{
    [TestMethod]
    public void ProjectKeepsOriginalPromptReferencesAndIndependentDraft()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var projects = new VisualProjects(files.File("projects"));
        var inputFile = files.File("input.png");
        File.WriteAllBytes(inputFile, [1, 2, 3]);
        var project = new VisualProject { Draft = "new draft", Artist = VisualComponents.Turbo, Persistent = true };
        var a = projects.Attach(project, inputFile);
        var b = projects.Attach(project, inputFile);
        var proposed = new[] { new VisualInput(b.Id, "original", true), new VisualInput(a.Id, "coat") };
        project.Results.Add(new("previous", new("older prompt", proposed), inputFile, DateTimeOffset.Now));
        project.ActivePrompt = "previous";
        project.ManualLines.Add("new manual line");
        projects.Save(project);
        var reopened = projects.Current()!;
        Assert.AreEqual(project.Id, reopened.Id);
        Assert.AreEqual("new draft", reopened.Draft);
        Assert.AreEqual("older prompt", reopened.SelectedPrompt!.Prompt.Text);
        CollectionAssert.AreEqual(new[] { b.Id, a.Id }, reopened.SelectedPrompt.Prompt.Images.Select(image => image.ImageId).ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(projects.ImagePath(reopened, a.Id)));
        reopened.ActivePrompt = null;
        projects.Save(reopened);
        Assert.IsNull(projects.Load(project.Id).SelectedPrompt);
        Assert.HasCount(1, projects.List()[0].Results);
    }

    [TestMethod]
    [DataRow(VisualAdvisor.VramRecovery, true)]
    [DataRow("LetItOut.VramChanged", true)]
    [DataRow("LetItOutMemoryException", true)]
    [DataRow("LetItOut.RamRequired", false)]
    [DataRow(null, false)]
    public void GemmaCanResumeAfterVramPressureUsingCpu(string? detail, bool expected) =>
        Assert.AreEqual(expected, VisualAdvisor.RequiresCpuRecovery(detail));

    [TestMethod]
    public async Task ActualGemmaRecoveryProcessesTwoImagesOnCpu()
    {
        var auditRoot = Environment.GetEnvironmentVariable("AIHUB_VISUAL_ADVISOR_CPU_AUDIT");
        if (string.IsNullOrWhiteSpace(auditRoot)) Assert.Inconclusive("Opt-in installed visual Advisor test.");
        using var audit = ComponentAudit.UseTestStore(auditRoot, enabled: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var components = new VisualComponents(new StorageSettingsStore().LoadOrCreate(), 1);
        var path = await components.PrepareAdvisorAsync(VisualComponents.Gemma, false, null, timeout.Token);
        Assert.IsNotNull(path);
        var advisor = new VisualAdvisor();
        try
        {
            var prepared = await advisor.PrepareAsync(path, gemma: true, images: true, token: timeout.Token, forceCpu: true);
            var process = OwnedProcessRegistry.Shared.GetSnapshot().Single(item => item.Component == "LlamaServerRuntimeService");
            using var native = System.Diagnostics.Process.GetProcessById(process.Pid);
            Assert.AreEqual(EngineSetSelection.ResolveDirectory(NativeRuntimeReleasePolicy.UpdatedSet, "llama", "cpu"),
                Path.GetDirectoryName(native.MainModule!.FileName), ignoreCase: true);

            string Image(SkiaSharp.SKColor color)
            {
                using var bitmap = new SkiaSharp.SKBitmap(96, 96);
                bitmap.Erase(color);
                using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
            }
            var answer = await advisor.CompleteAsync(prepared,
                [VisualAdvisor.ImageMessage("Name the two colors in order in a few words.",
                    [Image(SkiaSharp.SKColors.Red), Image(SkiaSharp.SKColors.Blue)])], timeout.Token);
            Assert.IsFalse(string.IsNullOrWhiteSpace(answer));
        }
        finally { await ApplicationBackgroundOperations.RetireModelsAsync(); }
    }

    [TestMethod]
    public void AdvisorSeparatesConversationFromFragmentsAndKeepsSourceRole()
    {
        var reply = VisualAdvisor.ParseAdvice("""{"reply":"Which light?","fragments":["a warm window"]}""");
        Assert.AreEqual("Which light?", reply.Reply);
        CollectionAssert.AreEqual(new[] { "a warm window" }, reply.Fragments);
        Assert.HasCount(0, VisualAdvisor.ParseAdvice("ordinary conversation").Fragments);
        var input = new[] { new VisualInput("one", "source", true), new VisualInput("two", "reference") };
        var prompt = VisualAdvisor.ParsePrompt("""{"prompt":"Use the coat from image 2","roles":["untrusted replacement","coat reference"]}""", input);
        Assert.AreEqual("Use the coat from image 2", prompt.Text);
        CollectionAssert.AreEqual(new[] { "one", "two" }, prompt.Images.Select(image => image.ImageId).ToArray());
        CollectionAssert.AreEqual(new[] { "source", "coat reference" }, prompt.Images.Select(image => image.Role).ToArray());
    }

    [TestMethod]
    public void ComposedPromptKeepsUserRequirementsTheAdvisorOmitted()
    {
        const string addition = "Рост девушки 165 см. Фото во весь рост.";
        const string previous = "Hyper-realistic portrait with soft light.";
        var response = System.Text.Json.JsonSerializer.Serialize(new { prompt = previous });
        var prepared = VisualAdvisor.PreparePrompt(response, [], [addition, " " + addition]);

        Assert.AreEqual(addition + "\n\n" + previous, prepared.Text);
        var alreadyIncluded = System.Text.Json.JsonSerializer.Serialize(new { prompt = prepared.Text });
        Assert.AreEqual(prepared.Text, VisualAdvisor.PreparePrompt(alreadyIncluded, [], [addition]).Text);
    }

    [TestMethod]
    public void CompositionReusesAnExplicitRevisionSourceInsteadOfThePreviousPreview()
    {
        var project = new VisualProject { PromptDraft = "Old preview with an obsolete requirement", PreviewImages = [] };
        project.Results.Add(new("saved", new("Saved prompt to revise", []), "image.png", DateTimeOffset.Now));
        Assert.IsNull(project.DraftForRevision);
        project.PromptRevisionId = "saved";
        Assert.AreEqual("Saved prompt to revise", project.DraftForRevision);

        // Older projects kept the explicit revision text in PromptDraft before a preview was produced.
        project.PromptRevisionId = null;
        project.PreviewImages = null;
        project.PromptDraft = "Old project revision source";
        Assert.AreEqual("Old project revision source", project.DraftForRevision);
    }

    [TestMethod]
    [DataRow(8192)]
    [DataRow(192512)]
    public void GemmaReplyFitsTheRuntimeAndUsesTheSameReserveAsTheMeter(int capacity)
    {
        var advisor = new VisualPreparedAdvisor(new(), true, capacity);
        const int shortInput = 600;
        var reply = advisor.ReplyTokens(shortInput);
        LetItOutChatRuntime.RequireContextSpace(shortInput, reply, capacity);
        Assert.Throws<LetItOutChatContextException>(() => LetItOutChatRuntime.RequireContextSpace(shortInput, reply + 1, capacity));
        var meter = advisor.Measure(shortInput);
        Assert.AreEqual(reply - LiteraryAutomaticBudget.MinimumReply, meter.Free);
        Assert.AreEqual(LiteraryAutomaticBudget.MinimumReply, advisor.ReplyTokens(meter.Available));
        var error = Assert.Throws<InvalidOperationException>(() => advisor.ReplyTokens(meter.Available + 1));
        Assert.AreEqual("Visual.ContextFull", error.Message);

        var literaryAdvisor = advisor with { Gemma = false };
        Assert.AreEqual(LiteraryAutomaticBudget.Reply(capacity, shortInput), literaryAdvisor.ReplyTokens(shortInput));
    }

    [TestMethod]
    public void NativeCommandUsesActualImageFilesInOrderForBothArtists()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var root = files.File("models");
        var references = new[] { files.File("z.png"), files.File("a.png") };
        foreach (var path in references) File.WriteAllBytes(path, [1]);
        foreach (var id in VisualComponents.Artists)
        {
            var model = ImageGenerationCatalog.Get(id);
            var cards = ImageGenerationCatalog.CreateCards(root).Where(card => model.Components.Contains(card.ModelArtifactId)).ToList();
            if (id == VisualComponents.Base)
                cards.Add(new() { InstallDirectory = root, Files = [new() { RelativePath = "vision.gguf", Purpose = "llm_vision" }] });
            var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), id, "blue coat", 512, 512, [1],
                root, files.File("session")) { ReferenceImages = references };
            ImageGenerationCatalog.Validate(request);
            var args = ImageGenerationNativeWorker.Command(request, 0, cards, "prompt.txt", "out.png", files.File("sd-cli.exe")).ArgumentList.ToArray();
            CollectionAssert.AreEqual(references, args.Select((arg, index) => (arg, index)).Where(item => item.arg == "-r")
                .Select(item => args[item.index + 1]).ToArray());
            Assert.AreEqual(id == VisualComponents.Base, args.Contains("--llm_vision"));
        }
    }

    [TestMethod]
    public Task CardRequiresPreparationAndOpeningRevealsWorkspace() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var projects = new VisualProjects(files.File("projects"));
        var localizer = new LocalizationService();
        localizer.Load("ru");
        var current = new VisualProject { Draft = "existing draft" };
        projects.Save(current);
        var nav = new ScenarioNavigationControl { OpenVisualWorkspace = _ => null };
        nav.Configure(localizer.T);
        nav.SelectDirection(ScenarioNavigationCatalog.Creation);
        nav.OpenScenario(ScenarioNavigationCatalog.VisualScenario);
        Assert.IsFalse(nav.IsVisualScenario);
        Assert.IsNull(nav.VisualWorkspace);
        nav.OpenVisualWorkspace = _ =>
        {
            var view = new VisualScenarioControl(localizer.T, projects);
            var components = new VisualComponents(new StorageSettings(), 0);
            view.Configure(localizer.T, components, new(VisualComponents.Gemma, VisualComponents.Turbo));
            Assert.AreEqual(VisualComponents.Core, view.Preferred.Advisor);
            view.Configure(localizer.T, components, new(VisualComponents.Gemma, VisualComponents.Turbo, Confirmed: true));
            return view;
        };
        nav.OpenScenario(ScenarioNavigationCatalog.VisualScenario);
        Assert.IsTrue(nav.IsVisualScenario);
        var buttons = ScenarioNavigationTests.LogicalDescendants(nav).OfType<Button>().ToArray();
        Assert.IsTrue(buttons.Any(button => AutomationProperties.GetAutomationId(button) == "Visual.Draw"));
        Assert.IsTrue(buttons.Any(button => AutomationProperties.GetAutomationId(button) == "Visual.Attach"));
        var reopened = projects.Load(current.Id);
        Assert.AreEqual(current.Draft, reopened.Draft);
        Assert.AreEqual(VisualComponents.Gemma, reopened.Advisor);
        Assert.IsTrue(nav.GoBack());
        Assert.IsFalse(nav.IsVisualScenario);
    });

    [TestMethod]
    public Task PromptCardsShareOneSwitchAcrossRepeatedDrawingsAndRestoreLegacySelection() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var projects = new VisualProjects(files.File("projects"));
        var project = new VisualProject { ActivePrompt = "repeat", Persistent = true };
        var prompt = new VisualPrompt("Approved prompt", [new("source", "original", true), new("reference", "coat")]);
        var now = DateTimeOffset.Now;
        project.Results.Add(new("first", prompt, files.File("one.png"), now));
        project.Results.Add(new("different-inputs", prompt with { Images = prompt.Images.Reverse().ToArray() }, files.File("two.png"), now));
        project.Results.Add(new("repeat", prompt with { Images = [.. prompt.Images] }, files.File("three.png"), now));
        projects.Save(project);

        var localizer = new LocalizationService();
        localizer.Load("ru");
        var view = new VisualScenarioControl(localizer.T, projects);
        view.Configure(localizer.T, new(new StorageSettings(), 0), null);
        CheckBox[] Switches() => ScenarioNavigationTests.LogicalDescendants(view).OfType<CheckBox>()
            .Where(control => AutomationProperties.GetAutomationId(control).StartsWith("Visual.PromptCard.", StringComparison.Ordinal)).ToArray();

        Assert.HasCount(2, Switches());
        Assert.AreEqual(true, Switches()[0].IsChecked);
        Assert.AreEqual(false, Switches()[1].IsChecked);
        var elements = ScenarioNavigationTests.LogicalDescendants(view).ToArray();
        Assert.AreEqual(2, elements.OfType<Button>().Count(button =>
            AutomationProperties.GetAutomationId(button).StartsWith("Visual.PromptCard.", StringComparison.Ordinal)));
        var firstCard = elements.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "Visual.PromptCard.1.Open");
        Assert.AreEqual("№\u00a01", firstCard.Content);
        Assert.AreEqual(2, elements.OfType<TextBlock>().Count(text => text.Text == "Промпт №\u00a01"));
        Assert.AreEqual(3, elements.OfType<Button>().Count(button => AutomationProperties.GetAutomationId(button) == "Visual.EditImage"));

        Switches()[1].IsChecked = true;
        Assert.AreEqual(false, Switches()[0].IsChecked);
        Assert.AreEqual(true, Switches()[1].IsChecked);
        Assert.AreEqual("different-inputs", projects.Load(project.Id).SelectedPrompt!.Id);
        Switches()[1].IsChecked = false;
        Assert.IsTrue(Switches().All(control => control.IsChecked == false));
        Assert.IsNull(projects.Load(project.Id).SelectedPrompt);
        Assert.HasCount(3, projects.Load(project.Id).Results);
    });

    [TestMethod]
    public Task OmittedAdvisorOptionCanBecomeAFragmentWithoutChangingExistingChoices() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var projects = new VisualProjects(files.File("projects"));
        var project = new VisualProject();
        const string userText = "Suggest two backgrounds.";
        const string approvedText = "Wet city street with neon reflections.";
        const string missingText = "Sunlit rooftop with hanging gardens.";
        const string advisorText = approvedText + " Alternatively: " + missingText + " Which fits?";
        project.Messages.Add(new("request", true, userText, [], []));
        project.Messages.Add(new("reply", false, advisorText, [new("approved", approvedText, true)], []));
        project.ManualLines.Add("Landscape composition");
        projects.Save(project);
        var view = new VisualScenarioControl(key => key, projects);
        view.Configure(key => key, new(new StorageSettings(), 0), null);
        var texts = ScenarioNavigationTests.LogicalDescendants(view).OfType<TextBox>().ToArray();
        var source = texts.Single(box => box.Text == advisorText);
        var menu = source.ContextMenu;
        MenuItem OpenFor(TextBox box)
        {
            menu.PlacementTarget = box;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Visual.MakeFragment"));
        }

        Assert.AreEqual(Visibility.Collapsed, OpenFor(texts.Single(box => box.Text == userText)).Visibility);
        Assert.IsFalse(OpenFor(source).IsEnabled);
        source.Select(advisorText.IndexOf(missingText, StringComparison.Ordinal), missingText.Length);
        var add = OpenFor(source);
        Assert.IsTrue(add.IsEnabled);
        add.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        var saved = projects.Load(project.Id);
        Assert.AreEqual(advisorText, saved.Messages[1].Text);
        CollectionAssert.AreEqual(project.ManualLines, saved.ManualLines);
        Assert.HasCount(2, saved.Messages[1].Fragments);
        Assert.AreEqual(new VisualFragment("approved", approvedText, true), saved.Messages[1].Fragments[0]);
        var added = saved.Messages[1].Fragments[1];
        Assert.AreEqual(missingText, added.Text);
        Assert.IsFalse(added.Selected);

        OpenFor(source).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var switches = ScenarioNavigationTests.LogicalDescendants(view).OfType<CheckBox>().ToArray();
        Assert.HasCount(2, switches);
        Assert.AreEqual(true, switches[0].IsChecked);
        Assert.AreEqual(false, switches[1].IsChecked);
        switches[1].IsChecked = true;
        saved = projects.Load(project.Id);
        Assert.HasCount(2, saved.Messages[1].Fragments);
        Assert.AreEqual(added with { Selected = true }, saved.Messages[1].Fragments[1]);
    });

    [TestMethod]
    public Task LeftPanelShowsManualTextAndRevealsSelectedFragmentInChat() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var projects = new VisualProjects(files.File("projects"));
        var empty = new VisualProject();
        projects.Save(empty);
        var project = new VisualProject();
        const string fragmentText = "A portrait with a detailed background and soft lighting.\nKeep the subject in the foreground.";
        const string manualText = "This is a long manual instruction that must be displayed in full, even if it spans multiple lines.";
        project.ManualLines.Add(manualText);
        project.Messages.Add(new("request", true, "First message", [], []));
        project.Messages.Add(new("older", false, "Earlier reply", [new("first", fragmentText, true)], []));
        for (var index = 0; index < 24; index++)
            project.Messages.Add(new($"message-{index}", true, "Further discussion", [], []));
        project.Messages.Add(new("latest", false, "Recent reply", [new("second", "Another approved fragment", true)], []));
        projects.Save(project, current: false);
        var view = new VisualScenarioControl(key => key, projects);
        view.Configure(key => key, new(new StorageSettings(), 0), null);

        var root = (Grid)view.Content;
        var settings = ScenarioNavigationTests.LogicalDescendants(view).OfType<Expander>().Single();
        Assert.IsTrue(settings.IsExpanded);
        view.RestoreProject(project.Id);
        Assert.IsFalse(settings.IsExpanded);
        var left = (StackPanel)((ScrollViewer)root.Children[0]).Content;
        var inputs = (StackPanel)left.Children[1];
        var input = inputs.Children.OfType<TextBox>().Single();
        var referenceHeading = inputs.Children.OfType<TextBlock>().Single(label => label.Text == "Visual.References");
        Assert.IsLessThan(inputs.Children.IndexOf(input), inputs.Children.IndexOf(referenceHeading));
        Assert.AreEqual("Visual.SelectedFragments", ((TextBlock)left.Children[2]).Text);
        var manual = (TextBlock)inputs.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single().Content;
        Assert.AreEqual(manualText + "  ×", manual.Text);
        Assert.AreEqual(TextWrapping.Wrap, manual.TextWrapping);
        Assert.AreEqual(TextTrimming.None, manual.TextTrimming);

        var row = left.Children.OfType<Button>().First();
        var label = (TextBlock)row.Content;
        Assert.AreEqual(fragmentText.ReplaceLineEndings(" "), label.Text);
        Assert.AreEqual(TextWrapping.NoWrap, label.TextWrapping);
        Assert.AreEqual(TextTrimming.CharacterEllipsis, label.TextTrimming);
        Assert.AreEqual(fragmentText, ((TextBlock)((ToolTip)row.ToolTip).Content).Text);
        var chat = ((DockPanel)root.Children[1]).Children.OfType<ScrollViewer>().Single();
        var switches = ScenarioNavigationTests.LogicalDescendants(chat).OfType<CheckBox>().ToArray();
        view.Measure(new Size(1040, 560));
        view.Arrange(new Rect(0, 0, 1040, 560));
        chat.ScrollToEnd();
        view.UpdateLayout();
        var end = chat.VerticalOffset;
        Assert.IsGreaterThan(0, end);
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        view.UpdateLayout();
        var reached = chat.VerticalOffset;
        var target = switches[0].TranslatePoint(new Point(0, 0), chat);
        Assert.IsLessThan(end, reached);
        Assert.IsGreaterThanOrEqualTo(-1, target.Y);
        Assert.IsLessThanOrEqualTo(chat.ViewportHeight + 1, target.Y + switches[0].ActualHeight);
        Assert.IsTrue(projects.Load(project.Id).Messages[1].Fragments[0].Selected);

        settings.IsExpanded = true;
        switches[0].IsChecked = false;
        view.UpdateLayout();
        Assert.IsTrue(settings.IsExpanded);
        Assert.AreEqual(reached, chat.VerticalOffset, 1d);
        Assert.IsFalse(projects.Load(project.Id).Messages[1].Fragments[0].Selected);
        Assert.IsTrue(projects.Load(project.Id).Messages[^1].Fragments[0].Selected);
        Assert.HasCount(1, left.Children.OfType<Button>().ToArray());
        view.RestoreProject(empty.Id);
        Assert.IsTrue(settings.IsExpanded);
    });

    [TestMethod]
    public Task OpenWorkspaceUpdatesItsControlsAndDropdownWhenThemeChanges() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var projects = new VisualProjects(files.File("projects"));
        var project = new VisualProject { Draft = "Draft" };
        project.Messages.Add(new("request", true, "User request", [], []));
        project.Messages.Add(new("reply", false, "Advisor reply", [new("fragment", "Prompt fragment", true)], []));
        projects.Save(project);
        var nav = new ScenarioNavigationControl();
        var host = new Border { Child = nav };
        host.Resources["UiBodyFontSize"] = 14d;
        host.Resources["AccentBrush"] = Brushes.RoyalBlue;
        host.Resources["StepBadgeBrush"] = Brushes.SlateBlue;
        nav.OpenVisualWorkspace = _ =>
        {
            var view = new VisualScenarioControl(key => key, projects);
            view.Configure(key => key, new(new StorageSettings(), 0), null);
            return view;
        };
        nav.SelectDirection(ScenarioNavigationCatalog.Creation);
        nav.OpenScenario(ScenarioNavigationCatalog.VisualScenario);
        var workspace = nav.VisualWorkspace!;
        var elements = ScenarioNavigationTests.LogicalDescendants(workspace).ToArray();
        elements.OfType<Expander>().Single().IsExpanded = true;
        var chatTexts = new[] { "User request", "Advisor reply", "Prompt fragment" }
            .Select(text => elements.OfType<TextBox>().Single(box => box.Text == text)).ToArray();
        var projectsButton = elements.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "Visual.Projects");
        var composer = elements.OfType<TextBox>().Single(box => box.Text == project.Draft);
        var advisor = elements.OfType<ComboBox>().First();
        var fragment = elements.OfType<CheckBox>().Single();
        var aspect = elements.OfType<ComboBox>().Single(box => AutomationProperties.GetAutomationId(box) == "Visual.Aspect");
        var size = elements.OfType<ComboBox>().Single(box => AutomationProperties.GetAutomationId(box) == "Visual.Size");
        var format = elements.OfType<ComboBox>().Single(box => AutomationProperties.GetAutomationId(box) == "Visual.OutputFormat");
        Assert.AreEqual("PNG", ((ComboBoxItem)format.SelectedItem).Content);
        CollectionAssert.AreEqual(new[] { "PNG", "JPEG", "WebP" }, format.Items.OfType<ComboBoxItem>().Select(item => item.Content).ToArray());
        format.SelectedIndex = 2;
        Assert.IsTrue(workspace.CanLeave);
        Assert.AreEqual(ImageGenerationFileFormat.WebP, projects.Load(project.Id).OutputFormat);
        Assert.AreSame(aspect.Parent, size.Parent);
        Assert.AreEqual(Grid.GetRow(aspect), Grid.GetRow(size));
        Assert.AreNotEqual(Grid.GetColumn(aspect), Grid.GetColumn(size));
        Assert.IsFalse(elements.OfType<TextBox>().Any(box => AutomationProperties.GetName(box) == "Visual.Name"));

        foreach (var dark in new[] { true, false, true })
        {
            var foreground = dark ? Brushes.White : Brushes.Black;
            var surface = dark ? Brushes.MidnightBlue : Brushes.WhiteSmoke;
            var panel = dark ? Brushes.DarkSlateGray : Brushes.White;
            host.Resources["TextPrimaryBrush"] = foreground;
            host.Resources["TextSecondaryBrush"] = foreground;
            host.Resources["SecondaryButtonBackgroundBrush"] = surface;
            host.Resources["PanelBrush"] = panel;
            host.Resources["LineBrush"] = dark ? Brushes.SlateGray : Brushes.LightGray;
            host.Measure(new Size(1280, 820));
            host.Arrange(new Rect(0, 0, 1280, 820));
            host.UpdateLayout();
            Assert.IsLessThan(((Grid)projectsButton.Parent).ActualWidth, projectsButton.ActualWidth);
            foreach (var text in chatTexts) Assert.AreSame(foreground, text.Foreground);
            Assert.AreSame(foreground, ((TextBlock)projectsButton.Content).Foreground);
            Assert.AreSame(surface, ((Border)projectsButton.Template.FindName("Frame", projectsButton)).Background);
            Assert.AreSame(foreground, composer.Foreground);
            Assert.AreSame(foreground, composer.CaretBrush);
            Assert.AreSame(surface, composer.Background);
            Assert.AreSame(foreground, fragment.Foreground);
            Assert.IsNotNull(fragment.Template.FindName("Switch", fragment));
            Assert.AreSame(surface, advisor.Background);
            Assert.AreSame(foreground, ((ComboBoxItem)advisor.SelectedItem).Foreground);
            var popup = (Popup)advisor.Template.FindName("PART_Popup", advisor);
            Assert.AreSame(panel, ((Border)popup.Child).Background);
        }

        foreach (var text in chatTexts)
        {
            Assert.IsTrue(text.IsReadOnly);
            text.Select(1, 4);
            Assert.AreEqual(text.Text.Substring(1, 4), text.SelectedText);
            Assert.IsTrue(ApplicationCommands.Copy.CanExecute(null, text));

            var menu = text.ContextMenu;
            menu.PlacementTarget = text;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            var copy = (MenuItem)menu.Items[0];
            var selectAll = (MenuItem)menu.Items[1];
            Assert.AreSame(ApplicationCommands.Copy, copy.Command);
            Assert.AreSame(text, copy.CommandTarget);
            Assert.AreSame(text, selectAll.CommandTarget);
            ApplicationCommands.SelectAll.Execute(null, text);
            Assert.AreEqual(text.Text, text.SelectedText);
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        }
    });
}
