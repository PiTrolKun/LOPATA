using System.Windows;
using System.Windows.Controls;

namespace AIHub.Controls;

/// <summary>Stacks settings rows when the content pane is narrow, preserving the original wide layout.</summary>
public static class SettingsResponsiveLayout
{
    public static readonly DependencyProperty StackWhenNarrowProperty = DependencyProperty.RegisterAttached(
        "StackWhenNarrow", typeof(bool), typeof(SettingsResponsiveLayout), new PropertyMetadata(false, Changed));
    public static void SetStackWhenNarrow(DependencyObject target, bool value) => target.SetValue(StackWhenNarrowProperty, value);
    public static bool GetStackWhenNarrow(DependencyObject target) => (bool)target.GetValue(StackWhenNarrowProperty);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Grid grid || args.NewValue is not true) return;
        var widths = grid.ColumnDefinitions.Select(column => column.Width).ToArray();
        var slots = grid.Children.Cast<FrameworkElement>().Select(child => (Child: child, Column: Grid.GetColumn(child), Row: Grid.GetRow(child), Margin: child.Margin)).ToArray();
        bool stacked = false;
        void Update()
        {
            bool narrow = grid.ActualWidth is > 0 and < 600;
            if (narrow == stacked) return;
            stacked = narrow;
            if (narrow)
            {
                grid.RowDefinitions.Clear();
                for (int i = 0; i < widths.Length; i++) grid.ColumnDefinitions[i].Width = i == 0 ? new(1, GridUnitType.Star) : new(0);
                for (int i = 0; i < slots.Length; i++)
                {
                    grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    Grid.SetColumn(slots[i].Child, 0); Grid.SetRow(slots[i].Child, i);
                    slots[i].Child.Margin = new(0, 0, 0, 8);
                }
            }
            else
            {
                grid.RowDefinitions.Clear();
                for (int i = 0; i < widths.Length; i++) grid.ColumnDefinitions[i].Width = widths[i];
                foreach (var slot in slots) { Grid.SetColumn(slot.Child, slot.Column); Grid.SetRow(slot.Child, slot.Row); slot.Child.Margin = slot.Margin; }
            }
        }
        // XAML children and columns are populated after attached properties.
        grid.Loaded += Loaded;
        void Loaded(object sender, RoutedEventArgs e)
        {
            grid.Loaded -= Loaded;
            widths = grid.ColumnDefinitions.Select(column => column.Width).ToArray();
            slots = grid.Children.Cast<FrameworkElement>().Select(child => (Child: child, Column: Grid.GetColumn(child), Row: Grid.GetRow(child), Margin: child.Margin)).ToArray();
            grid.SizeChanged += (_, _) => Update(); Update();
        }
    }
}
