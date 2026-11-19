using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using AIHub.Controls.MailHub;
using AIHub.Services.MailHub;
using Lopata.MailHub;
using Color = System.Windows.Media.Color;
using Border = System.Windows.Controls.Border;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DockPanel = System.Windows.Controls.DockPanel;
using ListBox = System.Windows.Controls.ListBox;
using PasswordBox = System.Windows.Controls.PasswordBox;
using Popup = System.Windows.Controls.Primitives.Popup;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using StackPanel = System.Windows.Controls.StackPanel;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using Wpf = System.Windows;

namespace AIHub.Tests;

[TestClass]
public sealed class MailHubThemeTests
{
    [STATestMethod]
    public void ConnectionInstructionsAndLinksFollowProviderSelection()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-mail-gmail-guide-");
        var owner = new Wpf.Window();
        _ = new WindowInteropHelper(owner).EnsureHandle();
        MailHubAccountWindow? account = null;
        try
        {
            var files = new MailHubFiles(directory.FullName);
            account = new(owner, Localize, new MailHubConfiguration(files.Root));
            var form = (StackPanel)((ScrollViewer)account.Content).Content;
            var provider = form.Children.OfType<ComboBox>().Single();
            var passwordMode = form.Children.OfType<CheckBox>().Single();
            var help = form.Children.OfType<TextBlock>().First();
            Assert.IsTrue(passwordMode.IsChecked == true);
            AssertGuide("GooglePasswordSteps", "https://myaccount.google.com/apppasswords");
            passwordMode.IsChecked = false;
            passwordMode.RaiseEvent(new(ToggleButton.ClickEvent));
            AssertGuide("GooglePasswordSteps", "https://myaccount.google.com/apppasswords");
            provider.SelectedIndex = 1;
            AssertGuide("MailRuPasswordSteps", "https://account.mail.ru/user/2-step-auth/passwords/");
            Assert.AreEqual(Wpf.Visibility.Collapsed, passwordMode.Visibility);
            provider.SelectedIndex = 2;
            AssertGuide("YandexPasswordSteps", "https://mail.yandex.ru/?dpda=yes", "https://passport.yandex.ru/security/app-passwords");
            provider.SelectedIndex = 3;
            AssertGuide("OutlookSteps", "https://outlook.live.com/mail/0/options/mail/forwarding?nlp=1",
                "https://learn.microsoft.com/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth");
            Assert.AreEqual(Wpf.Visibility.Collapsed, form.Children.OfType<PasswordBox>().Single().Visibility);
            provider.SelectedIndex = 4;
            AssertGuide("ICloudPasswordSteps", "https://account.apple.com/");
            Assert.AreEqual(Wpf.Visibility.Visible, form.Children.OfType<PasswordBox>().Single().Visibility);
            provider.SelectedIndex = 5;
            Assert.HasCount(0, help.Inlines.OfType<System.Windows.Documents.Hyperlink>());
            provider.SelectedIndex = 0;
            AssertGuide("GooglePasswordSteps", "https://myaccount.google.com/apppasswords");
            Assert.IsFalse(passwordMode.IsChecked == true);

            account.Close();
            account = new(owner, Localize, new MailHubConfiguration(files.Root),
                MailHubAccount.Create(MailHubProvider.Gmail, "test@gmail.com", "", "", 993, false, MailHubAuthentication.OAuth));
            var editForm = (StackPanel)((ScrollViewer)account.Content).Content;
            Assert.IsFalse(editForm.Children.OfType<CheckBox>().Single().IsChecked == true);

            void AssertGuide(string stepKey, params string[] urls)
            {
                CollectionAssert.AreEqual(urls, help.Inlines.OfType<System.Windows.Documents.Hyperlink>()
                    .Select(link => link.NavigateUri.AbsoluteUri).ToArray());
                Assert.IsTrue(help.Inlines.OfType<System.Windows.Documents.Run>()
                    .Any(run => run.Text == "MailHub.Help." + stepKey));
            }
        }
        finally
        {
            account?.Close();
            owner.Close();
            directory.Delete(recursive: true);
        }
    }

    [STATestMethod]
    public void SummaryRulesAcceptTypingAndKeepEditsAfterReopening()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-mail-rules-");
        var owner = new Wpf.Window();
        _ = new WindowInteropHelper(owner).EnsureHandle();
        MailHubWindow? hub = null;
        try
        {
            var files = new MailHubFiles(directory.FullName);
            var configuration = new MailHubConfiguration(files.Root);
            configuration.Save(new MailHubSettings { Priorities = "Проекты", Style = "Кратко" });
            SetPalette(owner, dark: true);
            hub = new(owner, RulesText, files);
            _ = new WindowInteropHelper(hub).EnsureHandle();
            var editors = RuleFields(hub);
            var policy = editors.First(field => field.IsReadOnly);
            var topics = editors.Single(field => AutomationProperties.GetAutomationId(field) == "MailHub.Topics");
            var style = editors.Single(field => AutomationProperties.GetAutomationId(field) == "MailHub.SpeechStyle");
            CollectionAssert.AreEqual(new[] { true, false, true, false, true }, editors.Select(field => field.IsReadOnly).ToArray());

            Type(topics, ", встречи");
            Assert.AreEqual("Проекты, встречи", topics.Text);
            topics.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, topics, style)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Assert.AreEqual("Проекты, встречи", configuration.Load().Priorities);
            Type(style, " и спокойно");
            Assert.AreEqual("Кратко и спокойно", style.Text);
            Type(policy, " Введённый текст");
            Assert.AreEqual("Защищённое начало.", policy.Text);

            hub.Close();
            hub = new(owner, RulesText, files);
            editors = RuleFields(hub);
            Assert.AreEqual("Проекты, встречи", editors.Single(field => AutomationProperties.GetAutomationId(field) == "MailHub.Topics").Text);
            Assert.AreEqual("Кратко и спокойно", editors.Single(field => AutomationProperties.GetAutomationId(field) == "MailHub.SpeechStyle").Text);
        }
        finally
        {
            hub?.Close();
            owner.Close();
            directory.Delete(recursive: true);
        }

        static string RulesText(string key) => key == "MailHub.PolicyText"
            ? "Защищённое начало. [[topics]] Правила отбора. [[style]] Защищённое окончание." : Localize(key);

        static TextBox[] RuleFields(MailHubWindow window)
        {
            var tabs = (TabControl)((DockPanel)window.Content).Children[1];
            tabs.SelectedIndex = 3;
            _ = Sample(tabs, 870, 480, 825, 443);
            var rules = (ScrollViewer)((TabItem)tabs.Items[3]).Content;
            return ((StackPanel)rules.Content).Children.OfType<TextBox>().ToArray();
        }

        static void Type(TextBox field, string text)
        {
            field.CaretIndex = field.Text.Length;
            var input = new TextComposition(InputManager.Current, field, text);
            field.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, input)
                { RoutedEvent = TextCompositionManager.TextInputEvent });
        }
    }

    [STATestMethod]
    public void MailWindowAndControlsFollowThemeChanges()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-mail-theme-");
        var owner = new Wpf.Window();
        _ = new WindowInteropHelper(owner).EnsureHandle();
        MailHubWindow? hub = null;
        MailHubAccountWindow? account = null;
        try
        {
            var files = new MailHubFiles(directory.FullName);
            SetPalette(owner, dark: true);
            hub = new(owner, Localize, files);
            var handle = new WindowInteropHelper(hub).EnsureHandle();
            account = new(hub, Localize, new MailHubConfiguration(files.Root),
                MailHubAccount.Create(MailHubProvider.MailRu, "test@mail.ru", "", "", 993, false, MailHubAuthentication.AppPassword));
            var tabs = (TabControl)((DockPanel)hub.Content).Children[1];
            var accountsPanel = (StackPanel)((TabItem)tabs.Items[0]).Content;
            var accountList = accountsPanel.Children.OfType<ListBox>().Single();
            var schedule = (DockPanel)((TabItem)tabs.Items[1]).Content;
            var calendar = (System.Windows.Controls.Grid)((ScrollViewer)schedule.Children[1]).Content;
            var cell = calendar.Children.OfType<ToggleButton>().First();
            var rules = (ScrollViewer)((TabItem)tabs.Items[3]).Content;
            var ruleEditors = ((StackPanel)rules.Content).Children.OfType<TextBox>().Where(field => !field.IsReadOnly).ToArray();
            var form = (StackPanel)((ScrollViewer)account.Content).Content;
            var password = form.Children.OfType<PasswordBox>().Single();

            VerifyPalette(dark: true);
            SetPalette(owner, dark: false);
            VerifyPalette(dark: false);

            void VerifyPalette(bool dark)
            {
                var panelColor = Parse(dark ? "#172033" : "#FFFFFF");
                var baseColor = Parse(dark ? "#111827" : "#F3F3F3");
                var inputColor = Parse(dark ? "#111827" : "#F8F8F8");
                var selectedColor = Parse(dark ? "#1E3A5F" : "#EAF1FF");
                Assert.AreEqual(baseColor, ((SolidColorBrush)hub.Background).Color);
                Assert.AreEqual(baseColor, ((SolidColorBrush)account.Background).Color);
                if (DwmGetWindowAttribute(handle, 20, out int darkCaption, sizeof(int)) == 0)
                    Assert.AreEqual(dark ? 1 : 0, darkCaption, "Native caption theme");
                tabs.SelectedIndex = 0;
                Assert.AreEqual(panelColor, Sample(tabs, 870, 480, 825, 443), "Empty tab panel");
                Assert.AreEqual(panelColor, Sample(accountList, 720, 180, 360, 160), "Mailbox list");
                tabs.SelectedIndex = 1;
                _ = Sample(tabs, 870, 480, 825, 443);
                cell.ApplyTemplate();
                var tile = (Border)cell.Template.FindName("Cell", cell);
                cell.IsChecked = false;
                Assert.AreEqual(inputColor, ((SolidColorBrush)tile.Background).Color, "Empty calendar cell");
                cell.IsChecked = true;
                Assert.AreEqual(selectedColor, ((SolidColorBrush)tile.Background).Color, "Selected calendar cell");
                tabs.SelectedIndex = 2;
                _ = Sample(tabs, 870, 480, 825, 443);
                var models = (StackPanel)((ScrollViewer)((TabItem)tabs.Items[2]).Content).Content;
                var combo = models.Children.OfType<ComboBox>().First();
                combo.ApplyTemplate();
                var choices = (Popup)combo.Template.FindName("PART_Popup", combo);
                Assert.AreEqual(panelColor, ((SolidColorBrush)((Border)choices.Child).Background).Color, "Model dropdown");
                tabs.SelectedIndex = 3;
                _ = Sample(tabs, 870, 480, 825, 443);
                Assert.AreEqual(panelColor, ((SolidColorBrush)rules.Background).Color);
                foreach (var field in ruleEditors)
                {
                    Assert.AreEqual(inputColor, ((SolidColorBrush)field.Background).Color);
                    Assert.AreEqual(Parse(dark ? "#93C5FD" : "#1D4ED8"), ((SolidColorBrush)field.Foreground).Color);
                }
                _ = Sample((Wpf.FrameworkElement)account.Content, 520, 585, 500, 560);
                Assert.AreEqual(inputColor, Sample(password, 380, 40, 260, 25), "Password entry");
            }
        }
        finally
        {
            account?.Close();
            hub?.Close();
            owner.Close();
            directory.Delete(recursive: true);
        }
    }

    private static string Localize(string key) => key == "MailHub.EveryDayAt" ? "Every day at {0}" : key;

    private static void SetPalette(Wpf.Window owner, bool dark)
    {
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Parse(dark ? "#111827" : "#F3F3F3"));
        owner.Resources["PanelBrush"] = new SolidColorBrush(Parse(dark ? "#172033" : "#FFFFFF"));
        owner.Resources["LineBrush"] = new SolidColorBrush(Parse(dark ? "#2D374B" : "#DADDE3"));
        owner.Resources["TextPrimaryBrush"] = new SolidColorBrush(Parse(dark ? "#F8FAFC" : "#1F1F1F"));
        owner.Resources["TextSecondaryBrush"] = new SolidColorBrush(Parse(dark ? "#AAB4C4" : "#5D6470"));
        owner.Resources["ChatUserTextBrush"] = new SolidColorBrush(Parse(dark ? "#93C5FD" : "#1D4ED8"));
        owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Parse(dark ? "#111827" : "#F8F8F8"));
        owner.Resources["StepBadgeBrush"] = new SolidColorBrush(Parse(dark ? "#1E3A5F" : "#EAF1FF"));
        owner.Resources["AccentBrush"] = new SolidColorBrush(Parse("#2563EB"));
        owner.Resources["UiBodyFontSize"] = 16d;
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    private static Color Sample(Wpf.FrameworkElement element, int width, int height, int x, int y)
    {
        element.Measure(new Wpf.Size(width, height));
        element.Arrange(new Wpf.Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Wpf.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
