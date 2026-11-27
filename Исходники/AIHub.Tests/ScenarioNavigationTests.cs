using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Controls.ImageLayers;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using AIHub.Services.VisualConstructor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ScenarioNavigationTests
{
    [TestMethod]
    public void CatalogKeepsCanonicalEntriesAndResolvableLinks()
    {
        var nodes = ScenarioNavigationCatalog.Nodes;
        Assert.AreEqual(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());
        Assert.AreEqual(4, ScenarioNavigationCatalog.Children(null).Count());
        Assert.IsTrue(ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Utilities).IsAvailable);
        var capture = ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Capture);
        Assert.IsTrue(capture.IsAvailable);
        Assert.AreEqual(ScenarioNavigationCatalog.Utilities, ScenarioNavigationCatalog.Get(capture.ParentId!).ParentId);
        Assert.AreEqual("uncertainty", ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Sandbox).Id);
        foreach (var node in nodes)
        {
            Assert.IsNotNull(ScenarioNavigationCatalog.Get(node.EntryTargetId));
            if (node.ParentId is not null) Assert.IsNotNull(ScenarioNavigationCatalog.Get(node.ParentId));
            foreach (var related in node.RelatedIds) Assert.IsNotNull(ScenarioNavigationCatalog.Get(related));
            foreach (var language in new[] { "ru", "en" })
            {
                var localizer = new LocalizationService(); localizer.Load(language);
                Assert.AreNotEqual(node.TitleKey, localizer.T(node.TitleKey));
                if (node.DescriptionKey.Length != 0) Assert.AreNotEqual(node.DescriptionKey, localizer.T(node.DescriptionKey));
            }
        }
    }

    [TestMethod]
    public Task VisualConstructorUsesTheHostPageAndKeepsItsThemeAndDraft() => Sta(() =>
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Проверка окна");
        var coloredProject = ConstructorProject.Create(stand.FullPath, "Цвет окна");
        var previous = coloredProject.ReadWindow();
        var window = XDocument.Parse(previous);
        window.Root!.SetAttributeValue("Background", "#FF14385C");
        coloredProject.SaveWindow(previous, window);
        File.WriteAllText(Path.Combine(stand.FullPath, "visual-constructor-recent.json"),
            JsonSerializer.Serialize(new[] { project.FilePath, coloredProject.FilePath }));
        var navigation = Create("ru", 1, dark: true);
        navigation.Resources["WindowBackgroundBrush"] = Brushes.Black;
        navigation.Resources["SecondaryButtonBackgroundBrush"] = Brushes.DarkSlateGray;
        var owner = new Window { Width = 1050, Height = 650, Content = navigation };
        navigation.ScenarioRequested += id =>
        {
            if (id == ScenarioNavigationCatalog.VisualConstructor) navigation.ShowVisualConstructor(stand.FullPath);
        };
        try
        {
            owner.Show();
            navigation.SelectDirection(ScenarioNavigationCatalog.Creation);
            Find<Button>(navigation, "Navigation.visual_constructor").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = navigation.VisualConstructorView!;
            Assert.AreSame(editor, navigation.Content);
            Assert.AreSame(owner, Window.GetWindow(editor));
            Assert.AreEqual(ScenarioNavigationCatalog.VisualConstructor, navigation.RecoveryScenarioId);

            var heading = LogicalDescendants(editor).OfType<TextBlock>()
                .Single(text => text.Text == "Визуальный конструктор с Gemma");
            var width = Find<TextBox>(editor, "VisualConstructor.Width");
            var widthSlider = Find<Slider>(editor, "VisualConstructor.Width.Slider");
            var heightSlider = Find<Slider>(editor, "VisualConstructor.Height.Slider");
            var minWidthSlider = Find<Slider>(editor, "VisualConstructor.MinWidth.Slider");
            var minHeightSlider = Find<Slider>(editor, "VisualConstructor.MinHeight.Slider");
            var preview = Find<Border>(editor, "VisualConstructor.FormPreview");
            var status = Find<TextBlock>(editor, "VisualConstructor.Status");
            var run = Find<Button>(editor, "VisualConstructor.Run");
            var recent = Find<ComboBox>(editor, "VisualConstructor.Recent");
            owner.UpdateLayout();
            Assert.IsFalse(width.IsEnabled);
            Assert.IsFalse(widthSlider.IsEnabled);
            Assert.IsFalse(run.IsEnabled);
            Assert.AreEqual(Colors.White, ((SolidColorBrush)heading.Foreground).Color);
            Assert.AreEqual(Color.FromRgb(23, 32, 50), TextBoxFill(width));
            Assert.AreEqual(Colors.DarkSlateGray, ((SolidColorBrush)((Border)run.Template.FindName("Frame", run)).Background).Color);
            Assert.AreEqual(Color.FromRgb(23, 32, 50), PopupFill(recent));

            recent.SelectedValue = project.FilePath;
            Assert.IsTrue(width.IsEnabled);
            Assert.AreEqual("", status.Text);
            Assert.AreEqual(SystemColors.WindowBrush.Color, ((SolidColorBrush)preview.Background).Color);
            widthSlider.Value = 920;
            heightSlider.Value = 540;
            minWidthSlider.Value = 400;
            minHeightSlider.Value = 300;
            Assert.AreEqual("920", width.Text);
            Assert.AreEqual("540", Find<TextBox>(editor, "VisualConstructor.Height").Text);
            Assert.AreEqual("400", Find<TextBox>(editor, "VisualConstructor.MinWidth").Text);
            Assert.AreEqual("300", Find<TextBox>(editor, "VisualConstructor.MinHeight").Text);
            Assert.AreEqual(920d, preview.Width);
            Assert.AreEqual(540d, preview.Height);
            Assert.AreEqual(400d, widthSlider.Minimum);
            Assert.AreEqual(300d, heightSlider.Minimum);
            Assert.AreEqual(920d, minWidthSlider.Maximum);
            Assert.AreEqual(540d, minHeightSlider.Maximum);
            width.Text = "5000,375";
            Assert.AreEqual(5000.375, widthSlider.Value);
            Assert.AreEqual(5000.375, preview.Width);
            width.Text = "";
            Assert.AreEqual(5000.375, preview.Width);
            width.Text = "860,5";
            Assert.AreEqual(860.5, preview.Width);
            navigation.Resources["TextPrimaryBrush"] = Brushes.Black;
            navigation.Resources["WindowBackgroundBrush"] = Brushes.WhiteSmoke;
            navigation.Resources["PanelBrush"] = Brushes.White;
            navigation.Resources["SecondaryButtonBackgroundBrush"] = Brushes.Gainsboro;
            owner.UpdateLayout();
            Assert.AreEqual(Colors.Black, ((SolidColorBrush)heading.Foreground).Color);
            Assert.AreEqual(Colors.White, TextBoxFill(width));
            Assert.AreEqual(Colors.White, PopupFill(Find<ComboBox>(editor, "VisualConstructor.Resize")));
            Assert.AreEqual(SystemColors.WindowBrush.Color, ((SolidColorBrush)preview.Background).Color);

            Find<Button>(editor, "VisualConstructor.Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(ScenarioNavigationCatalog.Creation, navigation.SelectedDirectionId);
            Assert.IsFalse(navigation.IsVisualConstructor);
            Assert.AreNotSame(editor, navigation.Content);
            navigation.SelectDirection(ScenarioNavigationCatalog.Utilities);
            navigation.NavigateFromTag(ScenarioNavigationCatalog.GetTag("visual_constructor"));
            Assert.AreSame(editor, navigation.Content);
            Assert.AreEqual("860,5", width.Text);
            Assert.IsTrue(navigation.GoBack());
            Assert.AreEqual(ScenarioNavigationCatalog.Utilities, navigation.SelectedDirectionId);
            editor.CheckpointForExitAsync().GetAwaiter().GetResult();
            var saved = XDocument.Parse(project.ReadWindow()).Root!;
            Assert.AreEqual("860.5", (string?)saved.Attribute("Width"));
            Assert.AreEqual("540", (string?)saved.Attribute("Height"));
            Assert.AreEqual("400", (string?)saved.Attribute("MinWidth"));
            Assert.AreEqual("300", (string?)saved.Attribute("MinHeight"));
            navigation.OpenScenario(ScenarioNavigationCatalog.VisualConstructor);
            recent.SelectedValue = coloredProject.FilePath;
            Assert.AreEqual(Color.FromRgb(20, 56, 92), ((SolidColorBrush)preview.Background).Color);
            recent.SelectedValue = project.FilePath;
            Assert.AreEqual(SystemColors.WindowBrush.Color, ((SolidColorBrush)preview.Background).Color);
        }
        finally { owner.Close(); }

        static T Find<T>(DependencyObject parent, string id) where T : DependencyObject =>
            LogicalDescendants(parent).OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
        static Color TextBoxFill(TextBox box)
        {
            var contentHost = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
            return ((SolidColorBrush)((Border)VisualTreeHelper.GetParent(contentHost)).Background).Color;
        }
        static Color PopupFill(ComboBox combo) =>
            ((SolidColorBrush)((Border)((Popup)combo.Template.FindName("PART_Popup", combo)).Child).Background).Color;
    });

    [TestMethod]
    public Task VisualConstructorFitsBothThemeLayoutsAndUsesActualPreviewSize() => Sta(() =>
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Проверка масштаба");
        var raw = project.ReadWindow();
        var form = new ConstructorLayout(raw);
        form.Root.SetAttributeValue("Width", "1500");
        form.Root.SetAttributeValue("Height", "1000");
        form.Add(ConstructorElementKind.Label, "Длинное имя элемента, которое помещается в список с многоточием", null);
        var buttonId = ConstructorLayout.Name(form.Add(ConstructorElementKind.Button, "Рассчитать", null));
        project.SaveWindow(raw, form.Document);
        var saved = project.ReadWindow();
        File.WriteAllText(Path.Combine(stand.FullPath, "visual-constructor-recent.json"), JsonSerializer.Serialize(new[] { project.FilePath }));
        var navigation = Create("ru", 1, dark: true);
        navigation.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 39));
        navigation.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(18, 27, 45));
        var owner = new Window { Width = 1260, Height = 740, Content = navigation };
        owner.Resources.MergedDictionaries.Add(navigation.Resources);
        try
        {
            owner.Show();
            navigation.ShowVisualConstructor(stand.FullPath);
            var editor = navigation.VisualConstructorView!;
            var recent = Find<ComboBox>(editor, "Recent");
            recent.SelectedValue = project.FilePath;
            var preview = Find<Border>(editor, "FormPreview");
            var viewport = Find<ScrollViewer>(editor, "PreviewViewport");
            var properties = Find<ScrollViewer>(editor, "WindowProperties");
            var scale = Find<ComboBox>(editor, "PreviewScale");
            var elements = Find<ListBox>(editor, "Elements");
            var empty = Find<Border>(editor, "EmptyActions");
            owner.UpdateLayout();
            Assert.IsTrue(RenderedWidth() < 1500 && RenderedWidth() <= viewport.ViewportWidth + 1);
            Assert.AreEqual(Visibility.Collapsed, viewport.ComputedHorizontalScrollBarVisibility);
            Assert.AreEqual(Visibility.Collapsed, properties.ComputedVerticalScrollBarVisibility);
            Assert.AreEqual(Visibility.Collapsed, Visuals(elements).OfType<ScrollViewer>().Single().ComputedHorizontalScrollBarVisibility);
            Assert.AreEqual(Color.FromRgb(23, 32, 50), ((SolidColorBrush)empty.Background).Color);
            var actionGroup = Find<StackPanel>(editor, "ActionGroup");
            var eventGroup = Find<StackPanel>(editor, "EventGroup");
            var stepCommands = Find<WrapPanel>(editor, "StepCommands");
            var gemmaCommands = Find<WrapPanel>(editor, "GemmaCommands");
            Rect Position(FrameworkElement element) => element.TransformToAncestor(owner).TransformBounds(new Rect(element.RenderSize));
            Assert.IsTrue(Position(actionGroup).Right < Position(viewport).Left);
            Assert.IsTrue(Position(eventGroup).Left > Position(viewport).Right);
            Assert.IsTrue(Position(stepCommands).Top > Position(viewport).Bottom);
            Assert.AreEqual(Position(stepCommands).Top, Position(gemmaCommands).Top, 2d);
            Assert.IsTrue(recent.ActualWidth <= 180);
            Assert.AreEqual(project.FilePath, Find<TextBlock>(editor, "ProjectPath").ToolTip);
            var save = Find<Button>(editor, "Save");
            Assert.IsTrue(save.Content is TextBlock && save.ActualWidth <= 36);
            Assert.AreEqual("Сохранить", AutomationProperties.GetName(save));
            Capture(owner, "constructor-dark-fit");
            var previewButton = Find<Button>(preview, "PreviewElement." + buttonId);
            var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent };
            previewButton.RaiseEvent(click);
            owner.UpdateLayout();
            Assert.IsTrue(click.Handled);
            Assert.AreEqual(buttonId, elements.SelectedValue);
            Assert.AreEqual("Click", Find<ComboBox>(editor, "Event").SelectedValue);
            Assert.AreEqual("Кнопка · Рассчитать", Find<TextBlock>(editor, "ActionTargetName").Text);
            Assert.IsNotNull(System.Windows.Documents.AdornerLayer.GetAdornerLayer(previewButton)?.GetAdorners(previewButton));
            Assert.AreEqual(saved, project.ReadWindow());
            Capture(owner, "constructor-dark-selection");
            elements.SelectedIndex = -1;

            scale.SelectedValue = "actual";
            owner.UpdateLayout();
            Assert.AreEqual(1500d, RenderedWidth(), 1d);
            Assert.IsTrue(viewport.ScrollableHeight > 0 && viewport.ScrollableWidth > 0);
            foreach (var bar in Visuals(viewport).OfType<ScrollBar>().Where(bar => bar.IsVisible))
            {
                Assert.IsNotNull(bar.Template.FindName("PART_Track", bar));
                Assert.AreEqual(typeof(Border), VisualTreeHelper.GetChild(bar, 0).GetType());
            }
            viewport.ScrollToHorizontalOffset(120);
            viewport.ScrollToVerticalOffset(140);
            owner.UpdateLayout();
            Assert.AreEqual(120, viewport.HorizontalOffset, 1d);
            Assert.AreEqual(140, viewport.VerticalOffset, 1d);
            Capture(owner, "constructor-dark-actual");
            scale.SelectedValue = "fit";
            owner.UpdateLayout();
            Assert.AreEqual(0d, viewport.ScrollableHeight);
            Assert.AreEqual(0d, viewport.ScrollableWidth);
            Assert.AreEqual(saved, project.ReadWindow());

            var caption = Find<TextBox>(editor, "Caption");
            caption.Text = "Несохранённое название";
            OpenAndCancelElement("constructor-add-element-dark", Color.FromRgb(16, 24, 39));
            Assert.AreEqual("Несохранённое название", caption.Text);
            Assert.AreEqual(saved, project.ReadWindow());

            navigation.Resources["TextPrimaryBrush"] = Brushes.Black;
            navigation.Resources["WindowBackgroundBrush"] = Brushes.WhiteSmoke;
            navigation.Resources["PanelBrush"] = Brushes.White;
            navigation.Resources["SecondaryButtonBackgroundBrush"] = Brushes.Gainsboro;
            owner.Width = 860;
            owner.Height = 480;
            owner.UpdateLayout();
            Assert.AreEqual(Colors.White, ((SolidColorBrush)empty.Background).Color);
            Assert.AreEqual(SystemColors.WindowBrush.Color, ((SolidColorBrush)preview.Background).Color);
            Assert.AreEqual(Visibility.Visible, properties.ComputedVerticalScrollBarVisibility);
            Assert.IsTrue(viewport.ActualHeight >= 60, "The preview must still be visible in a short window.");
            Capture(owner, "constructor-light-small");
            properties.ScrollToEnd();
            owner.UpdateLayout();
            Assert.AreEqual(properties.ScrollableHeight, properties.VerticalOffset, 1d);
            OpenAndCancelElement("constructor-add-element-light", Colors.WhiteSmoke);
            owner.Dispatcher.InvokeAsync(() =>
            {
                var dialog = owner.OwnedWindows.OfType<Window>().Single(window => window.IsVisible);
                try
                {
                    Find<ListBox>(dialog, "TemplateType").SelectedValue = "copy";
                    var targets = Find<ComboBox>(dialog, "TemplateOutput");
                    targets.SelectedIndex = 1;
                    Capture(dialog, "constructor-add-template-light");
                    Find<Button>(dialog, "TemplateApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Find<Button>(editor, "StepTemplates").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var chain = project.ReadChain(new ConstructorEventSite(ConstructorProject.MainForm, null, "Loaded"));
            CollectionAssert.AreEqual(new[] { ConstructorStepKind.ReadValue, ConstructorStepKind.SetValue },
                chain.Steps.Select(step => step.Spec!.Kind).ToArray());

            double RenderedWidth() => preview.TransformToAncestor(viewport)
                .TransformBounds(new Rect(preview.RenderSize)).Width;

            void OpenAndCancelElement(string snapshot, Color expected)
            {
                Color? background = null;
                var dialogWidth = 0d;
                owner.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = owner.OwnedWindows.OfType<Window>().Single(window => window.IsVisible);
                    try
                    {
                        dialog.UpdateLayout();
                        background = ((SolidColorBrush)dialog.Background).Color;
                        dialogWidth = dialog.ActualWidth;
                        var palette = Find<ListBox>(dialog, "Palette");
                        palette.SelectedValue = "Choice";
                        dialog.UpdateLayout();
                        Capture(dialog, snapshot);
                    }
                    finally { dialog.Close(); }
                }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Find<Button>(editor, "AddElement").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(expected, background);
                Assert.IsTrue(dialogWidth >= 650);
            }
        }
        finally { owner.Close(); }

        static T Find<T>(DependencyObject parent, string id) where T : DependencyObject =>
            LogicalDescendants(parent).OfType<T>().Single(node => AutomationProperties.GetAutomationId(node) == "VisualConstructor." + id);

        static IEnumerable<DependencyObject> Visuals(DependencyObject root)
        {
            yield return root;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var node in Visuals(VisualTreeHelper.GetChild(root, i))) yield return node;
        }
    });

    [TestMethod]
    public Task VisualConstructorEditsMultipleFormsAndKeepsActionSource() => Sta(() =>
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Формы и переходы");
        var mainBefore = project.ReadWindow();
        File.WriteAllText(Path.Combine(stand.FullPath, "visual-constructor-recent.json"),
            JsonSerializer.Serialize(new[] { project.FilePath }));
        var navigation = Create("ru", 1, dark: true);
        navigation.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 39));
        navigation.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(18, 27, 45));
        var owner = new Window { Width = 1260, Height = 750, Content = navigation };
        owner.Resources.MergedDictionaries.Add(navigation.Resources);
        navigation.ScenarioRequested += id =>
        {
            if (id == ScenarioNavigationCatalog.VisualConstructor) navigation.ShowVisualConstructor(stand.FullPath);
        };
        try
        {
            owner.Show();
            navigation.OpenScenario(ScenarioNavigationCatalog.VisualConstructor);
            var editor = navigation.VisualConstructorView!;
            Find<ComboBox>("Recent").SelectedValue = project.FilePath;
            VisitDialog("Добавить окно…", _ => { });
            Click("AddForm");
            Assert.HasCount(1, project.ReadForms());
            VisitDialog("Добавить окно…", dialog =>
            {
                DialogControl<TextBox>(dialog, "NewFormTitle").Text = "Окно проверки";
                Tap(DialogControl<Button>(dialog, "NewFormApply"));
            });
            Click("AddForm");
            var form = project.ReadForms().Single(item => !item.IsMain);
            Assert.AreEqual("Окно проверки", project.FormTitle(form));
            Assert.AreEqual(mainBefore, project.ReadWindow());
            Click("MakeStartup");
            Assert.AreEqual(form, project.ReadStartupForm());
            Assert.IsFalse(Find<Button>("RemoveForm").IsEnabled);
            Find<TextBox>("Caption").Text = "Настройки";
            AddElement("Button");
            Find<TextBox>("ElementText").Text = "Назад";
            AddElement("Input");
            Find<TextBox>("ElementText").Text = "Начальное значение";
            Find<ComboBox>("Arrangement").SelectedValue = "wrap";
            Find<TabControl>("Forms").SelectedIndex = 0;
            Click("MakeStartup");
            Assert.AreEqual(ConstructorProject.MainForm, project.ReadStartupForm());

            var formSource = new ConstructorLayout(project.ReadForm(form));
            Assert.AreEqual("Настройки", project.FormTitle(form));
            Assert.IsTrue(formSource.Wrap);
            Assert.AreEqual(2, formSource.Elements.Count());
            Assert.IsTrue(formSource.Elements.Any(item => ConstructorLayout.Caption(item) == "Начальное значение"));

            AddElement("Button");
            Find<TextBox>("ElementText").Text = "Открыть";
            VisitDialog("Добавить действие…", dialog =>
                LogicalDescendants(dialog).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item)
                    == "VisualConstructor.ActionApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Click("AssignAction");
            var mainLayout = new ConstructorLayout(project.ReadWindow());
            var site = new ConstructorEventSite(ConstructorProject.MainForm, ConstructorLayout.Name(mainLayout.Elements.Single()), "Click");
            Assert.AreEqual(1, project.ReferencingActions(form).Count);
            Assert.ThrowsExactly<InvalidOperationException>(() => project.ArchiveForm(form));
            var action = project.ReadChain(site).Steps.Single();
            Assert.AreEqual(ConstructorStepKind.Inside, action.Spec?.Kind);
            VisitDialog("Добавить действие…", dialog =>
            {
                var type = DialogControl<ListBox>(dialog, "ActionMode");
                type.SelectedValue = "Message";
                Capture(dialog, "constructor-add-action-dark");
                LogicalDescendants(dialog).OfType<TextBox>().Single(item => AutomationProperties.GetAutomationId(item)
                    == "VisualConstructor.Argument0").Text = "Второе действие";
                LogicalDescendants(dialog).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item)
                    == "VisualConstructor.ActionApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Click("AssignAction");
            CollectionAssert.AreEqual(new[] { ConstructorStepKind.Inside, ConstructorStepKind.Message },
                project.ReadChain(site).Steps.Select(item => item.Spec!.Kind).ToArray());
            Click("MoveUp");
            CollectionAssert.AreEqual(new[] { ConstructorStepKind.Message, ConstructorStepKind.Inside },
                project.ReadChain(site).Steps.Select(item => item.Spec!.Kind).ToArray());
            Find<ListBox>("Actions").SelectedIndex = 1;
            var controlShowsSource = false;
            VisitDialog(action.FilePath, dialog => controlShowsSource = LogicalDescendants(dialog).OfType<TextBox>()
                .Any(area => area.Text.ReplaceLineEndings("\n") == action.Source.ReplaceLineEndings("\n")));
            var menu = Find<ListBox>("Actions").ContextMenu.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetAutomationId(item) == "VisualConstructor.OpenActionCode");
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsTrue(controlShowsSource);
            var manual = action.Source.Replace("FormNavigation.Inside", "FormNavigation.Separate", StringComparison.Ordinal);
            project.CommitStepCode(action, manual);
            var changed = project.ReadChain(site).Steps.Single(item => item.Id == action.Id);
            Assert.IsNull(changed.Spec);
            project.RevertStepCode(changed);
            Assert.AreEqual(action.Source, project.ReadChain(site).Steps.Single(item => item.Id == action.Id).Source);
            project.CommitStepCode(action, manual);
            var raw = project.ReadForm(form);
            formSource.Root.SetAttributeValue("WindowTitle", "Те же настройки");
            project.SaveForm(form, raw, formSource.Document);
            var copyParent = Directory.CreateDirectory(Path.Combine(stand.FullPath, "копии")).FullName;
            var copy = project.SaveAs(copyParent, "Перенос");
            Assert.AreEqual(manual, copy.ReadChain(site).Steps.Single(item => item.Id == action.Id).Source);
            Assert.AreEqual("Те же настройки", copy.FormTitle(form));
        }
        finally { owner.Close(); }

        T Find<T>(string id) where T : DependencyObject => LogicalDescendants(navigation.VisualConstructorView!)
            .OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == "VisualConstructor." + id);
        void Click(string id) => Find<Button>(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void AddElement(string kind)
        {
            VisitDialog("Добавить элемент…", dialog =>
            {
                DialogControl<ListBox>(dialog, "Palette").SelectedValue = kind;
                Tap(DialogControl<Button>(dialog, "NewElementApply"));
            });
            Click("AddElement");
        }

        static void Tap(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static T DialogControl<T>(Window dialog, string id) where T : DependencyObject =>
            LogicalDescendants(dialog).OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == "VisualConstructor." + id);
        void VisitDialog(string title, Action<Window> inspect)
        {
            owner.Dispatcher.InvokeAsync(() =>
            {
                var window = owner.OwnedWindows.OfType<Window>().FirstOrDefault(item => item.IsVisible && item.Title == title);
                try { if (window is not null) inspect(window); }
                finally
                {
                    foreach (var child in owner.OwnedWindows.OfType<Window>().Where(item => item.IsVisible).ToArray())
                        child.Close();
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    });

    [TestMethod]
    public void VisualConstructorKeepsPreviousCodeOnSourceConflictAndAcceptsStagedRetry()
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Конфликт исходников");
        var site = new ConstructorEventSite(ConstructorProject.MainForm, null, "Loaded");
        var chain = project.ReadChain(site);
        var staged = project.CreateStep(site, new(ConstructorStepKind.Variable, ["a", "1"]));
        Directory.CreateDirectory(Path.GetDirectoryName(staged.FilePath)!);
        File.WriteAllText(staged.FilePath, staged.Source);
        project.CommitChain(chain, [staged]);
        var first = project.ReadChain(site).Steps.Single();
        Assert.AreEqual(first.Id, staged.Id);
        project.CommitStepCode(first, first.Source + "\n// first manual edit");
        var edit = project.ReadChain(site).Steps.Single();
        File.WriteAllText(edit.FilePath, edit.Source + "\n// external edit");
        Assert.ThrowsExactly<ConstructorSaveConflict>(() => project.CommitStepCode(edit, edit.Source + "\n// stale edit"));
        Assert.AreEqual(edit.Source + "\n// external edit", File.ReadAllText(edit.FilePath));
        Assert.AreEqual(first.Source, File.ReadAllText(edit.FilePath + ".previous"));
    }

    [TestMethod]
    public Task ImageLayersOpenAsTheNextPageAndKeepThePaletteWhileBusy() => Sta(() =>
    {
        var navigation = Create("ru", 1, true);
        var owner = new Window { Content = navigation };
        var library = new ManagedModelLibraryStore();
        var page = new ImageLayersControl(owner, key => key, () => true, new ImageLayerAnalyzer(library),
            () => throw new InvalidOperationException("Navigation must not prepare models"));
        navigation.ScenarioRequested += id =>
        {
            if (id == ScenarioNavigationCatalog.ImageLayers) navigation.ShowImageLayers(page);
        };
        navigation.SelectDirection(ScenarioNavigationCatalog.Analysis);
        var card = LogicalDescendants(navigation).OfType<Button>().Single(button =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "Navigation.image_layers");
        card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreSame(page, navigation.Content);
        Assert.AreEqual(ScenarioNavigationCatalog.ImageLayers, navigation.RecoveryScenarioId);
        Assert.IsNull(LogicalDescendants(page).OfType<LayerCanvas>().Single().ToolTip);

        page.Measure(new Size(1200, 750)); page.Arrange(new Rect(0, 0, 1200, 750)); page.UpdateLayout();
        foreach (var list in LogicalDescendants(page).OfType<ItemsControl>().Where(control => control is TreeView or ListBox))
        {
            list.ApplyTemplate();
            var frame = (Border)VisualTreeHelper.GetChild(list, 0);
            list.IsEnabled = false;
            Assert.AreEqual(Color.FromRgb(23, 32, 50), ((SolidColorBrush)frame.Background).Color);
            list.IsEnabled = true;
            Assert.AreEqual(Color.FromRgb(23, 32, 50), ((SolidColorBrush)frame.Background).Color);
        }

        Assert.IsTrue(navigation.GoBack());
        Assert.AreEqual(ScenarioNavigationCatalog.Analysis, navigation.SelectedDirectionId);
        Assert.AreNotSame(page, navigation.Content);
        navigation.OpenScenario(ScenarioNavigationCatalog.ImageLayers);
        Assert.AreSame(page, navigation.Content);
        owner.Close();
    });

    [TestMethod]
    public Task SandboxLandingDoesNotStartTaskAndRetainsHistoryAcrossNavigation() => Sta(() =>
    {
        var control = Create("ru", 1, true);
        var title = new TextBox { Text = "Saved title" };
        var history = new Expander { IsExpanded = true, Content = title };
        control.AttachSandboxHistory(history);
        var requests = new List<string>(); control.ScenarioRequested += requests.Add;
        Assert.IsFalse(LogicalDescendants(control).Contains(history));
        control.SelectDirection(ScenarioNavigationCatalog.Experiments);
        control.OpenScenario(ScenarioNavigationCatalog.Sandbox);
        Assert.AreEqual(0, requests.Count);
        Assert.IsTrue(control.IsSandboxLanding);
        Assert.IsTrue(LogicalDescendants(control).Contains(history));
        control.Configure(key => key); // A language refresh must not replace the history or reset the page.
        Assert.IsTrue(history.IsExpanded);
        Assert.AreEqual("Saved title", title.Text);
        var start = LogicalDescendants(control).OfType<Button>().Single(b =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Navigation.SandboxNew");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        CollectionAssert.AreEqual(new[] { "uncertainty" }, requests);
        Assert.IsTrue(control.GoBack());
        Assert.IsFalse(LogicalDescendants(control).Contains(history));
        Assert.AreEqual(ScenarioNavigationCatalog.Experiments, control.SelectedDirectionId);
        Assert.IsTrue(control.GoBack()); Assert.IsTrue(control.IsHome);
        Assert.IsFalse(control.GoBack());
        control.SelectDirection(ScenarioNavigationCatalog.Utilities);
        Assert.IsFalse(control.IsHome);
        Assert.AreEqual(ScenarioNavigationCatalog.Utilities, control.SelectedDirectionId);
        control.OpenScenario(ScenarioNavigationCatalog.Capture);
        Assert.AreEqual(ScenarioNavigationCatalog.Capture, requests[^1]);
        Assert.IsTrue(control.GoBack()); Assert.IsTrue(control.IsHome);
        control.SelectDirection(ScenarioNavigationCatalog.Creation);
        control.OpenScenario(ScenarioNavigationCatalog.Literary);
        Assert.AreEqual(ScenarioNavigationCatalog.Literary, requests[^1]);
        Assert.AreEqual(ScenarioNavigationCatalog.Creation, control.SelectedDirectionId);
    });

    [TestMethod]
    public Task LetItOutCardRequiresHostPreparationBeforeOpeningChat() => Sta(() =>
    {
        var control = Create("ru", 1, true);
        var node = ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.LetItOut);
        Assert.AreEqual("experiment_chats", node.ParentId);
        var group = ScenarioNavigationCatalog.Get(node.ParentId!);
        Assert.AreEqual(ScenarioNavigationKind.Group, group.Kind);
        Assert.AreEqual(ScenarioNavigationCatalog.Experiments, group.ParentId);
        var requests = new List<string>(); control.ScenarioRequested += requests.Add;
        var created = 0; control.LetItOutChatCreated += _ => created++;
        control.SelectDirection(ScenarioNavigationCatalog.Experiments);
        var card = LogicalDescendants(control).OfType<Button>().Single(button =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "Navigation.let_it_out");
        card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        CollectionAssert.AreEqual(new[] { ScenarioNavigationCatalog.LetItOut }, requests);
        Assert.IsFalse(control.IsLetItOutChat);
        Assert.IsNull(control.LetItOutChat);
        Assert.AreEqual(0, created); // Cancelling preparation never creates a chat or starts a model.
        Assert.AreEqual(ScenarioNavigationCatalog.Experiments, control.SelectedDirectionId);

        control.ShowLetItOutChat();
        var chat = control.LetItOutChat;
        Assert.IsNotNull(chat);
        Assert.IsTrue(control.IsLetItOutChat);
        Assert.AreSame(chat, control.Content);
        control.Configure(key => key);
        Assert.AreSame(chat, control.Content);
        Assert.AreEqual(1, created);
        Assert.IsTrue(control.GoBack());
        Assert.IsFalse(control.IsLetItOutChat);
        Assert.AreEqual(ScenarioNavigationCatalog.Experiments, control.SelectedDirectionId);
        control.OpenScenario(ScenarioNavigationCatalog.LetItOut);
        Assert.IsFalse(control.IsLetItOutChat); // Re-entry still goes through preparation.
        control.ShowLetItOutChat();
        Assert.AreSame(chat, control.Content);
        Assert.AreEqual(1, created);
    });

    [TestMethod]
    public Task LetItOutCloudEntryReturnsToOriginAndKeepsChatInstance() => Sta(() =>
    {
        var control = Create("en", 1, false);
        var requests = new List<string>(); control.ScenarioRequested += requests.Add;
        var tag = ScenarioNavigationCatalog.GetTag("let_it_out");
        Assert.AreEqual(ScenarioNavigationCatalog.LetItOut, tag.TargetId);
        control.SelectDirection(ScenarioNavigationCatalog.Creation);
        control.NavigateFromTag(tag);
        CollectionAssert.AreEqual(new[] { ScenarioNavigationCatalog.LetItOut }, requests);
        Assert.IsNull(control.LetItOutChat);
        control.ShowLetItOutChat();
        var chat = control.LetItOutChat;
        Assert.IsTrue(control.GoBack());
        Assert.AreEqual(ScenarioNavigationCatalog.Creation, control.SelectedDirectionId);
        Assert.IsFalse(control.IsLetItOutChat);
        control.ShowLetItOutChat();
        control.ShowHome();
        Assert.IsTrue(control.IsHome);
        Assert.IsFalse(control.IsLetItOutChat);
        control.ShowLetItOutChat();
        Assert.AreSame(chat, control.Content);
        control.SelectDirection(ScenarioNavigationCatalog.Utilities);
        Assert.IsFalse(control.IsLetItOutChat);
        Assert.AreNotSame(chat, control.Content);
    });

    [TestMethod]
    [DataRow("ru", 1200d, 1d, true, 2)]
    [DataRow("en", 1200d, 1.5d, false, 2)]
    [DataRow("ru", 500d, 1.5d, true, 1)]
    [DataRow("en", 500d, 1.25d, false, 1)]
    public Task TilesWrapAndAdaptWithoutClipping(string language, double width, double scale, bool dark, int columns) => Sta(() =>
    {
        var control = Create(language, scale, dark);
        control.Measure(new Size(width, 750)); control.Arrange(new Rect(0, 0, width, 750)); control.UpdateLayout();
        var tiles = LogicalDescendants(control).OfType<UniformGrid>().Single();
        Assert.AreEqual(columns, tiles.Columns);
        Assert.AreEqual(4, tiles.Children.Count);
        foreach (Button tile in tiles.Children)
        {
            Assert.IsTrue(tile.ActualWidth > 0);
            var content = (StackPanel)tile.Content;
            Assert.IsTrue(content.ActualWidth <= tile.ActualWidth);
            foreach (var text in content.Children.OfType<TextBlock>())
            {
                Assert.AreEqual(TextWrapping.Wrap, text.TextWrapping);
                Assert.IsTrue(text.ActualWidth <= content.ActualWidth + 1);
            }
        }
    });

    internal static ScenarioNavigationControl Create(string language, double scale, bool dark)
    {
        var control = new ScenarioNavigationControl();
        foreach (var (key, size) in new[] { ("UiBodyFontSize", 14d), ("UiSmallFontSize", 12d),
            ("UiCardTitleFontSize", 24d), ("UiSectionFontSize", 18d), ("UiPageTitleFontSize", 32d) })
            control.Resources[key] = size * scale;
        control.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        control.Resources["TextSecondaryBrush"] = Brushes.Gray;
        control.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(23, 32, 50) : Colors.White);
        control.Resources["LineBrush"] = Brushes.Gray;
        control.Resources["AccentBrush"] = Brushes.RoyalBlue;
        var localizer = new LocalizationService(); localizer.Load(language); control.Configure(localizer.T);
        return control;
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_CONSTRUCTOR_INTERFACE_CAPTURE") is not { Length: > 0 } path) return;
        window.UpdateLayout();
        Directory.CreateDirectory(path);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(path, name + ".png"));
        encoder.Save(stream);
    }

    internal static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
    }

    internal static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
