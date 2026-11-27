namespace AIHub.Services.VisualConstructor;

internal static class ConstructorRuntimeTemplate
{
    internal const string Navigation = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Windows;
        using System.Windows.Controls;
        using System.Windows.Input;

        namespace VisualApp;

        public class FormView : UserControl
        {
            public string WindowTitle { get; set; } = "";
            public double WindowWidth { get; set; } = 640;
            public double WindowHeight { get; set; } = 450;
            public double WindowMinWidth { get; set; } = 320;
            public double WindowMinHeight { get; set; } = 240;
            public ResizeMode WindowResizeMode { get; set; } = ResizeMode.CanResize;

            internal Window CreateWindow()
            {
                var window = new Window { Content = this };
                ApplyWindowSettings(window);
                return window;
            }

            internal void ApplyWindowSettings(Window window)
            {
                window.Title = WindowTitle;
                window.Width = WindowWidth;
                window.Height = WindowHeight;
                window.MinWidth = WindowMinWidth;
                window.MinHeight = WindowMinHeight;
                window.ResizeMode = WindowResizeMode;
            }
        }

        internal static class FormNavigation
        {
            private sealed record Page(Type Type, FrameworkElement Content, string Title, IInputElement? Focus = null);

            private sealed class NavigationState
            {
                internal NavigationState(Window window)
                {
                    var content = (FrameworkElement)window.Content;
                    Current = new Page(content is FormView ? content.GetType() : window.GetType(), content, window.Title);
                    StartType = Current.Type;
                    Pages.Add(Current.Type, Current);
                }

                internal Page Current;
                internal Type StartType { get; }
                internal readonly Dictionary<Type, Page> Pages = new();
                internal readonly Stack<Page> History = new();
            }

            private static readonly ConditionalWeakTable<Window, NavigationState> States = new();
            private static readonly Dictionary<Type, Window> OpenWindows = new();

            internal static void Inside<T>(object sender, Func<T> create) where T : FrameworkElement
            {
                var window = Host(sender);
                var state = States.GetValue(window, host => new NavigationState(host));
                if (state.Current.Type == typeof(T)) return;
                if (!state.Pages.TryGetValue(typeof(T), out var next))
                {
                    next = create() switch
                    {
                        FormView view => new Page(typeof(T), view, view.WindowTitle),
                        Window form => TakeContent(form),
                        _ => throw new InvalidOperationException("The target is not a form.")
                    };
                    state.Pages.Add(typeof(T), next);
                }
                state.History.Push(state.Current with { Focus = Keyboard.FocusedElement });
                Show(window, state, next);
            }

            internal static void Back(object sender)
            {
                var window = Host(sender);
                if (States.TryGetValue(window, out var state) && state.History.TryPop(out var previous))
                    Show(window, state, previous);
            }

            internal static void Separate<T>(object sender, Func<T> create) where T : FrameworkElement
            {
                var owner = Host(sender);
                if (Application.Current?.MainWindow is { } main
                    && States.GetValue(main, host => new NavigationState(host)).StartType == typeof(T))
                {
                    Activate(main);
                    return;
                }
                if (OpenWindows.TryGetValue(typeof(T), out var existing))
                {
                    Activate(existing);
                    return;
                }
                var next = create() switch
                {
                    FormView view => view.CreateWindow(),
                    Window window => window,
                    _ => throw new InvalidOperationException("The target is not a form.")
                };
                next.Owner = owner;
                next.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                OpenWindows.Add(typeof(T), next);
                next.Closed += (_, _) => OpenWindows.Remove(typeof(T));
                try { next.Show(); }
                catch
                {
                    OpenWindows.Remove(typeof(T));
                    next.Close();
                    throw;
                }
            }

            private static Window Host(object sender) => sender as Window ?? Window.GetWindow((DependencyObject)sender)
                ?? throw new InvalidOperationException("The action needs an open window.");

            private static Page TakeContent(Window template)
            {
                // The original blank-project Window remains a normal WPF startup file.
                var content = (FrameworkElement)template.Content;
                template.Content = null;
                if (template.Resources.Count != 0 || template.Resources.MergedDictionaries.Count != 0)
                    content.Resources.MergedDictionaries.Insert(0, template.Resources);
                var result = new Page(template.GetType(), content, template.Title);
                template.Close();
                return result;
            }

            private static void Show(Window window, NavigationState state, Page page)
            {
                window.Content = page.Content;
                window.Title = page.Title;
                state.Current = page;
                if (page.Focus is not null)
                    window.Dispatcher.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(window.Content, page.Content)) Keyboard.Focus(page.Focus);
                    }, System.Windows.Threading.DispatcherPriority.Input);
            }

            private static void Activate(Window window)
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
        }
        """;
}
