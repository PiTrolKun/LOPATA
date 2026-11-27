using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Xml.Linq;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly StackPanel _layoutTools = new();
    private readonly ComboBox _arrangement = NewChoiceBox();
    private readonly ListBox _elementList = new() { MinHeight = 62, MaxHeight = 225, SelectedValuePath = nameof(Choice.Value) };
    private readonly TextBox _elementText = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 36, MaxHeight = 90 };
    private readonly StackPanel _elementDetails = new();
    private readonly Button _addElement = new();
    private readonly Button _removeElement = new();
    private readonly TextBlock _arrangementLabel = new() { Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap };
    private XElement? _element;
    private ConstructorElementKind _lastElement = ConstructorElementKind.Button;

    private static ComboBox NewChoiceBox() => new() { SelectedValuePath = nameof(Choice.Value), DisplayMemberPath = nameof(Choice.Label) };

    private ScrollViewer BuildLayoutTools()
    {
        var heading = Label("Elements", new Thickness(0, 0, 0, 8));
        heading.FontWeight = FontWeights.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        Configure(_removeElement, "RemoveElement");
        Configure(_addElement, "AddElement");
        DockPanel.SetDock(_removeElement, Dock.Right);
        DockPanel.SetDock(_addElement, Dock.Right);
        top.Children.Add(_removeElement);
        top.Children.Add(_addElement);
        top.Children.Add(heading);
        _layoutTools.Children.Add(top);
        _addElement.Click += (_, _) => TryEdit(AddElement);
        _removeElement.Click += (_, _) => TryEdit(RemoveElement);

        _layoutTools.Children.Add(_elementList);
        StyleList(_elementList);
        AutomationProperties.SetAutomationId(_elementList, "VisualConstructor.Elements");
        _elementList.SelectionChanged += (_, _) =>
        {
            if (!_loading) TryEdit(SelectElement);
        };

        _layoutTools.Children.Add(_arrangementLabel);
        AutomationProperties.SetLabeledBy(_arrangement, _arrangementLabel);
        AutomationProperties.SetAutomationId(_arrangement, "VisualConstructor.Arrangement");
        _layoutTools.Children.Add(_arrangement);
        _arrangement.SelectionChanged += (_, _) =>
        {
            if (!_loading && _design is not null && _arrangement.SelectedValue is string value)
                TryEdit(() => { _design.SetArrangement(value, SelectedGroup()); DesignChanged(); });
        };

        var textLabel = Label("ElementText", new Thickness(0, 10, 0, 4));
        _elementDetails.Children.Add(textLabel);
        _elementDetails.Children.Add(_elementText);
        _layoutTools.Children.Add(_elementDetails);
        AutomationProperties.SetLabeledBy(_elementText, textLabel);
        AutomationProperties.SetAutomationId(_elementText, "VisualConstructor.ElementText");
        _elementText.TextChanged += (_, _) =>
        {
            if (_loading || _element is null) return;
            TryEdit(() => { ConstructorLayout.SetCaption(_element, _elementText.Text); DesignChanged(); });
        };
        _elementText.LostKeyboardFocus += (_, _) =>
        {
            if (!_loading && _element is { } item) RefreshElementList(ConstructorLayout.Name(item));
        };
        AddActionTools();
        _layoutTools.Children.Add(_actionTools);
        ConfigureElementLabels();
        SelectElement();
        return new ScrollViewer
        {
            Content = _layoutTools, Margin = new Thickness(0, 0, 14, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private void ConfigureElementLabels()
    {
        var loading = _loading;
        _loading = true;
        try
        {
            var layout = _arrangement.SelectedValue ?? "column";
            _arrangement.ItemsSource = new[] { new Choice("column", T("Column")), new Choice("wrap", T("Wrap")), new Choice("grid", T("ArrangementGrid")) };
            _arrangement.SelectedValue = layout;
            SetCommandLabel(_addElement, "AddElement");
            SetCommandLabel(_removeElement, "RemoveElement");
            AutomationProperties.SetName(_elementList, T("Elements"));
            ConfigureActionLabels();
        }
        finally { _loading = loading; }
        RefreshElementList(_element is null ? null : ConstructorLayout.Name(_element));
    }

    private void AddElement()
    {
        if (_design is null || ChooseElement() is not { } choice) return;
        _lastElement = choice.Kind;
        var element = _design.Add(choice.Kind, choice.Text, SelectedGroup(), T("FirstTab"));
        if (choice.Kind == ConstructorElementKind.Choice) ConstructorLayout.SetCaption(element, choice.Text);
        RefreshElementList(ConstructorLayout.Name(element));
        DesignChanged();
    }

    private void RemoveElement()
    {
        if (_element is null) return;
        var removed = _element.DescendantsAndSelf().Where(ConstructorLayout.IsElement).ToArray();
        var handlers = removed.SelectMany(node => ConstructorSteps.Events(_form, node)
                .Select(name => (string?)node.Attribute(name))).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var ids = removed.Where(ConstructorLayout.IsElement).Select(ConstructorLayout.Name).ToArray();
        if (_project!.ReferencesElements(_form, ids, handlers))
            throw new InvalidOperationException("VisualConstructor.ElementInUse");
        _element.Remove();
        RefreshElementList(null);
        DesignChanged();
        if (handlers.Length == 0) return;
        Save();
        foreach (var handler in handlers)
        {
            if (_design!.Root.DescendantsAndSelf().SelectMany(node => node.Attributes()).All(attribute => attribute.Value != handler))
                _project!.ArchiveBinding(_form, handler);
        }
    }

    private void RefreshElementList(string? selected)
    {
        var loading = _loading;
        _loading = true;
        try
        {
            _elementList.ItemsSource = _design?.Elements.Select(node => new Choice(ConstructorLayout.Name(node), DescribeElement(node))).ToArray() ?? [];
            _elementList.SelectedValue = selected;
            SelectElement();
        }
        finally { _loading = loading; }
    }

    private void SelectElement()
    {
        _element = _elementList.SelectedValue is string selected
            ? _design?.Elements.FirstOrDefault(node => ConstructorLayout.Name(node) == selected) : null;
        var loading = _loading;
        _loading = true;
        try
        {
            _elementText.IsEnabled = _removeElement.IsEnabled = _element is not null;
            _elementDetails.Visibility = _element is null ? Visibility.Collapsed : Visibility.Visible;
            _elementText.Text = _element is null ? "" : ConstructorLayout.Caption(_element);
            var group = SelectedGroup();
            _arrangementLabel.Text = T(group is null ? "FormArrangement" : "GroupArrangement");
            _arrangement.SelectedValue = _design?.Arrangement(group) ?? "column";
            ShowPreviewSelection();
            RefreshActionList();
        }
        finally { _loading = loading; }
    }

    private XElement? SelectedGroup() => _element?.Name.LocalName is "GroupBox" or "TabControl" or "TabItem" ? _element : null;

    private string DescribeElement(XElement node)
    {
        var kind = T("Element." + ConstructorLayout.Kind(node));
        var caption = ConstructorLayout.Caption(node).ReplaceLineEndings(" ").Trim();
        return caption.Length == 0 || caption == kind ? kind : $"{kind} · {caption}";
    }

    private void DesignChanged()
    {
        Changed();
        RenderDesign();
    }

    private void TryEdit(Action operation)
    {
        try { operation(); }
        catch (Exception ex) { ShowError(ex); }
    }
}
