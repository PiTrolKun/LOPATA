using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIHub.Services.VoiceNotifications;

/// <summary>Supplement semantic operation events with actual changes in known status surfaces.</summary>
internal sealed class VoiceStatusObserver : IDisposable
{
    private static WeakReference<VoiceStatusObserver>? Active;
    private static bool _registered;
    private static readonly DependencyPropertyDescriptor Text = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    private readonly Dictionary<TextBlock, (EventHandler Changed, RoutedEventHandler Unloaded)> _attached = [];
    private bool _disposed;
    internal VoiceStatusObserver()
    {
        Active = new(this);
        if (!_registered)
        {
            _registered = true;
            RoutedEventHandler loaded = (sender, _) => { if (Active?.TryGetTarget(out var observer) == true) observer.Inspect(sender); };
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, loaded, true);
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.UserControl), FrameworkElement.LoadedEvent, loaded, true);
            EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, loaded, true);
        }
    }
    private void Inspect(object? source)
    {
        if (_disposed || source is null || source.GetType().Namespace?.Contains("VoiceNotifications", StringComparison.Ordinal) == true) return;
        if (source is TextBlock block)
        {
            var id = block.Name + System.Windows.Automation.AutomationProperties.GetAutomationId(block);
            if (IsStatus(id)) Attach(block);
        }
        else
            for (var type = source.GetType(); type is not null && type.Assembly == typeof(MainWindow).Assembly; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (IsStatus(field.Name) && field.GetValue(source) is TextBlock status) Attach(status);
    }
    private static bool IsStatus(string value) => value.Contains("status", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith("Stage", StringComparison.OrdinalIgnoreCase) || value is "_stage" or "_state" or "_progressText";
    private void Attach(TextBlock block)
    {
        if (_attached.ContainsKey(block)) return;
        var previous = block.Text;
        EventHandler changed = (_, _) => {
            var value = block.Text; if (_disposed || value == previous) return; previous = value;
            if (VoiceNotificationEvents.HasOperation) VoiceNotificationEvents.Publish(value);
            else
            {
                var contexts = VoiceNotificationContext.Element(block);
                VoiceNotificationEvents.Publish(value, contexts.FirstOrDefault() ?? "application", contexts);
            }
        };
        RoutedEventHandler unloaded = (_, _) => Detach(block);
        Text.AddValueChanged(block, changed); block.Unloaded += unloaded; _attached.Add(block, (changed, unloaded));
    }
    private void Detach(TextBlock block)
    { if (_attached.Remove(block, out var handlers)) { Text.RemoveValueChanged(block, handlers.Changed); block.Unloaded -= handlers.Unloaded; } }
    public void Dispose()
    { _disposed = true; foreach (var block in _attached.Keys.ToArray()) Detach(block); }
}
