using System.Windows;
using System.Windows.Controls;
using AIHub.Models;

namespace AIHub.Controls;

public partial class ImageAnalysisWorkspaceControl
{
    private void RenderBatchHistory(IReadOnlyList<ImageAnalysisLiterarySession> sessions, IReadOnlyList<ImageBatchJob> batches)
    {
        var dates = sessions.ToDictionary(s => s.SessionId, s => s.UpdatedAt);
        foreach (var job in batches)
        {
            var id = "batch:" + job.Id;
            dates[id] = job.Created;
            var button = new System.Windows.Controls.Button
            {
                Tag = id,
                Content = new TextBlock { Text = $"{_localize("Batch.Title")} · {job.Created.LocalDateTime:g} · {job.Items.Count} · {_localize("Batch.State." + job.Status)}", TextWrapping = TextWrapping.Wrap },
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 7), Padding = new Thickness(10, 8, 10, 8),
                Style = TryFindResource("SecondaryButtonStyle") as Style
            };
            button.Click += HistoryButton_Click;
            HistoryItemsPanel.Children.Add(button);
        }
        var ordered = HistoryItemsPanel.Children.Cast<System.Windows.Controls.Button>()
            .OrderByDescending(b => dates[(string)b.Tag]).ToArray();
        HistoryItemsPanel.Children.Clear();
        foreach (var button in ordered) HistoryItemsPanel.Children.Add(button);
        _historyCount = ordered.Length;
        HistoryEmptyText.Visibility = ordered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryTitle();
    }
}
