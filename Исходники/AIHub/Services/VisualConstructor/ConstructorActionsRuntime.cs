namespace AIHub.Services.VisualConstructor;

internal static class ConstructorActionsRuntime
{
    internal const string Source = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Data;
        using System.Globalization;
        using System.IO;
        using System.Runtime.CompilerServices;
        using System.Text.RegularExpressions;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Windows;
        using System.Windows.Controls;
        using System.Windows.Input;

        namespace VisualApp;

        public sealed class ActionContext
        {
            private readonly object _scope;
            private readonly Dictionary<string, object?> _values;
            internal ActionContext(object scope, object sender, EventArgs args, Dictionary<string, object?> values)
            {
                _scope = scope; Sender = sender; Event = args; _values = values;
                Window = sender as Window ?? (sender is DependencyObject element ? System.Windows.Window.GetWindow(element)
                    : Application.Current?.MainWindow);
            }

            public object Sender { get; }
            public EventArgs Event { get; }
            public Window? Window { get; }
            public int StepNumber { get; internal set; }
            public bool Stopped { get; private set; }
            public void Stop() => Stopped = true;
            public void Set(string name, object? value) => _values[name] = value;
            public object? Get(string name) => _values.TryGetValue(name, out var value) ? value
                : throw new InvalidOperationException("Variable: " + name);
            public object? Resolve(string value) => value.StartsWith("$$", StringComparison.Ordinal) ? value[1..]
                : value.StartsWith('$') ? Get(value[1..]) : value;

            public object? Read(string name) => Element(name) switch
            {
                TextBox text => text.Text,
                TextBlock label => label.Text,
                CheckBox check => check.IsChecked,
                ComboBox selection => (selection.SelectedItem as ComboBoxItem)?.Content ?? selection.SelectedItem,
                _ => throw new InvalidOperationException("Input: " + name)
            };

            public void Write(string name, object? value)
            {
                var text = Format(value);
                using var hold = FormActions.HoldEvents();
                switch (Element(name))
                {
                    case TextBox input: input.Text = text; break;
                    case TextBlock label: label.Text = text; break;
                    case CheckBox check: check.IsChecked = ToFlag(value); break;
                    case ComboBox list:
                        for (var index = 0; index < list.Items.Count; index++)
                        {
                            var item = list.Items[index];
                            if (Format((item as ComboBoxItem)?.Content ?? item) != text) continue;
                            list.SelectedIndex = index;
                            return;
                        }
                        throw new InvalidOperationException("List item: " + text);
                    case ContentControl content: content.Content = text; break;
                    default: throw new InvalidOperationException("Output: " + name);
                }
            }

            public decimal Calculate(string formula)
            {
                formula = Regex.Replace(formula.Replace(',', '.'), @"\$([\p{L}_][\p{L}\p{Nd}_]*)",
                    match => "(" + Number(Get(match.Groups[1].Value)).ToString(CultureInfo.InvariantCulture) + ")");
                if (!Regex.IsMatch(formula, @"^[0-9.\s()+*/%\-]+$")) throw new FormatException(formula);
                using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
                return Convert.ToDecimal(table.Compute(formula, null), CultureInfo.InvariantCulture);
            }

            public void Require(object? left, string operation, object? right, string message)
            {
                var comparison = operation is "Equal" or "NotEqual"
                    ? StringComparer.Ordinal.Compare(Format(left), Format(right))
                    : Number(left).CompareTo(Number(right));
                var passed = operation switch
                {
                    "Equal" => comparison == 0,
                    "NotEqual" => comparison != 0,
                    "Greater" => comparison > 0,
                    "Less" => comparison < 0,
                    "AtLeast" => comparison >= 0,
                    "AtMost" => comparison <= 0,
                    _ => throw new InvalidOperationException(operation)
                };
                if (passed) return;
                if (message.Length != 0) Show(message);
                Stop();
            }

            public void Show(object? value)
            {
                if (Window is { IsVisible: true } owner) MessageBox.Show(owner, Format(value));
                else MessageBox.Show(Format(value));
            }

            public void ChooseTextFile(string variable)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Text (*.txt;*.csv;*.json;*.md;*.xml)|*.txt;*.csv;*.json;*.md;*.xml|*.*|*.*",
                    CheckFileExists = true
                };
                var accepted = Window is { IsVisible: true } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
                if (accepted == true) Set(variable, dialog.FileName);
                else Stop();
            }

            public async Task ReadTextFileAsync(string value, string variable)
            {
                var text = await File.ReadAllTextAsync(Format(Resolve(value)));
                Set(variable, text);
            }

            private object Element(string name) => (_scope as FrameworkElement)?.FindName(name)
                ?? throw new InvalidOperationException("Element: " + name);

            private static string Format(object? value) => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
            private static decimal Number(object? value) =>
                decimal.Parse(Format(value).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
            private static bool ToFlag(object? value) => Format(value).Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "да" or "1" => true,
                "false" or "no" or "нет" or "0" => false,
                _ => throw new FormatException(Format(value))
            };
        }

        internal static class FormActions
        {
            private static readonly Dictionary<string, object?> Values = new(StringComparer.OrdinalIgnoreCase);
            private static readonly ConditionalWeakTable<object, HashSet<string>> Executing = new();
            private static readonly HashSet<Window> ClosingAgain = new();
            private static int _pausedEvents;

            internal static IDisposable HoldEvents()
            {
                _pausedEvents++;
                return new EventHold();
            }

            private sealed class EventHold : IDisposable
            {
                public void Dispose() => _pausedEvents--;
            }

            internal static async Task ExecuteAsync(object scope, object sender, EventArgs args, string key, Func<ActionContext,Task> run)
            {
                if (_pausedEvents != 0) return;
                var closeWindow = args is CancelEventArgs ? sender as Window : null;
                if (closeWindow is { IsLoaded: false }) return;
                if (closeWindow is not null && ClosingAgain.Contains(closeWindow)) return;
                var active = Executing.GetValue(sender, _ => new HashSet<string>(StringComparer.Ordinal));
                var closing = closeWindow is not null ? (CancelEventArgs)args : null;
                if (closing is not null) closing.Cancel = true;
                if (!active.Add(key)) return;
                var context = new ActionContext(scope, sender, args, Values);
                try
                {
                    await run(context);
                    if (closeWindow is not null && !context.Stopped)
                    {
                        // The original Closing event must return before resuming the requested close.
                        await Task.Yield();
                        ClosingAgain.Add(closeWindow);
                        try { closeWindow.Close(); }
                        finally { ClosingAgain.Remove(closeWindow); }
                    }
                }
                catch (Exception error)
                {
                    var prefix = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "Действие " : "Action ";
                    context.Show(prefix + context.StepNumber + ": " + error.Message);
                }
                finally { active.Remove(key); }
            }
        }
        """;
}
