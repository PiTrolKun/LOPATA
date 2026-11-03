using System.Windows;
using System.Windows.Media;

namespace AIHub.Services.Dictation;

/// <summary>Explicit opt-in for an application's embedded editor, never arbitrary web content.</summary>
internal interface IDictationTargetProvider
{
    bool CanDictate { get; }
    bool EditorFocused { get; }
    IDictationTextTarget CreateDictationTarget();
}

internal interface IAsyncDictationTextTarget : IDictationTextTarget
{
    Task ApplyAsync(int segment, string text, bool final, CancellationToken token);
}

internal static class RegisteredDictationTarget
{
    private static readonly DependencyProperty ProviderProperty = DependencyProperty.RegisterAttached(
        "Provider", typeof(IDictationTargetProvider), typeof(RegisteredDictationTarget));

    internal static void Register(FrameworkElement editor, IDictationTargetProvider provider) => editor.SetValue(ProviderProperty, provider);

    internal static IDictationTargetProvider? From(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element.GetValue(ProviderProperty) is IDictationTargetProvider { CanDictate: true } provider) return provider;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    internal static IReadOnlyList<IDictationTargetProvider> In(Window window)
    {
        var result = new List<IDictationTargetProvider>();
        void Visit(DependencyObject element)
        {
            if (element is FrameworkElement { IsVisible: false }) return;
            if (element.GetValue(ProviderProperty) is IDictationTargetProvider { CanDictate: true } provider)
            { result.Add(provider); return; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Visit(VisualTreeHelper.GetChild(element, i));
        }
        Visit(window); return result;
    }

    internal static IDictationTargetProvider? AtPoint(Window window, System.Windows.Point screenPoint)
    {
        // HwndHost children (WebView2) are absent from WPF InputHitTest. Limit the fallback
        // to the bounds of explicitly registered, visible editors in this exact window.
        foreach (var provider in In(window))
            if (provider is FrameworkElement { IsHitTestVisible: true } editor)
            {
                var point = editor.PointFromScreen(screenPoint);
                if (new Rect(0, 0, editor.ActualWidth, editor.ActualHeight).Contains(point)) return provider;
            }
        return null;
    }
}
