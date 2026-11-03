using System.Windows;
using AIHub.Models;

namespace AIHub;

public partial class MainWindow
{
    private CoreMemoryStatus _debugMemoryStatus = CoreMemoryStatus.Inactive();

    private void RefreshUniversalMemoryIndicator()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshUniversalMemoryIndicator); return; }
        var chat = ScenarioNavigationPage.LetItOutChat;
        if (chat?.IsVisible != true)
        {
            UpdateCoreMemoryIndicator(_debugChatWindow is null ? CoreMemoryStatus.Inactive() : _debugMemoryStatus);
            var description = CoreMemoryIndicatorPanel.ToolTip?.ToString() ?? "";
            System.Windows.Automation.AutomationProperties.SetName(CoreMemoryProgressBar, description);
            System.Windows.Automation.AutomationProperties.SetHelpText(CoreMemoryTrimButton, description);
            CoreMemoryTrimButton.IsEnabled = false; return;
        }
        UpdateCoreMemoryIndicator(chat.ContextInfo?.Status ?? CoreMemoryStatus.Inactive());
        CoreMemoryTrimButton.IsEnabled = chat.CanTrimContext;
        var label = L("LetItOut.Context.Trim");
        System.Windows.Automation.AutomationProperties.SetName(CoreMemoryTrimButton, label);
        if (chat.ContextInfo is not { } info)
        {
            System.Windows.Automation.AutomationProperties.SetHelpText(CoreMemoryTrimButton, L("CoreMemory.TooltipInactive"));
            System.Windows.Automation.AutomationProperties.SetName(CoreMemoryProgressBar, L("CoreMemory.TooltipInactive"));
            return;
        }
        var tooltip = string.Format(L("LetItOut.Context.Tooltip"), info.UsedTokens, info.Capacity,
            info.Status.FillPercent, Services.LetItOutContextWindow.AnswerReserve);
        tooltip += "\n" + L(chat.CanTrimContext ? "LetItOut.Context.TrimHint" : "LetItOut.Context.KeepLatest");
        CoreMemoryIndicatorPanel.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetHelpText(CoreMemoryTrimButton, tooltip);
        System.Windows.Automation.AutomationProperties.SetName(CoreMemoryProgressBar, tooltip);
    }

    private async void CoreMemoryTrimButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScenarioNavigationPage.LetItOutChat is { IsVisible: true } chat) await chat.TrimContextAsync();
        RefreshUniversalMemoryIndicator();
    }
}
