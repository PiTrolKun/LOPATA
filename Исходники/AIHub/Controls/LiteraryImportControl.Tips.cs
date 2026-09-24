using System.Windows;
using System.Windows.Controls;
using System.IO;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private FrameworkElement CreateAnalysisTips()
    {
        var tips = LiteraryTipCatalog.Load(_language);

        var panel = new StackPanel();
        panel.Children.Add(LiteraryUi.Text(I("Пока собирается книга", "While the book is being assembled"), true));
        panel.Children.Add(LiteraryUi.Text(I(
            "Короткие советы по работе с ЛОПАТОЙ сменяются автоматически.",
            "Short tips for working with LOPATA change automatically.")));
        panel.Children.Add(LiteraryUi.Text(L("TipsHold")));
        panel.Children.Add(new LiteraryFloatingTips(tips, L("TipsHold"),
            () => _showingAnalysis && _analysisStarted && _first is not null && !_showingLegacy,
            Path.Combine(AppDataPaths.BaseDirectory, "Literary", "tips-history.json")));
        return panel;
    }
}
