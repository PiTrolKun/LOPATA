using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Controls;

internal sealed partial class ShortsControl
{
    private readonly RadioButton _standardVariant = new() { GroupName = "ShortsVariations" };
    private readonly RadioButton _denoVariant = new() { GroupName = "ShortsVariations" };
    private readonly CheckBox _denoAcceleration = new();

    private void AddDescriptionVariants(WrapPanel row)
    {
        Add(_standardVariant, "Shorts.StandardVariant", "Shorts.Variant.Standard");
        Add(_denoVariant, "Shorts.DenoVariant", "Shorts.Variant.Deno");
        Add(_denoAcceleration, "SongClip.DenoAccelerate", "Shorts.Variant.Accelerate");
        _standardVariant.Checked += (_, _) => ChangeDescriptionVariant(false);
        _denoVariant.Checked += (_, _) => ChangeDescriptionVariant(true);
        _denoAcceleration.Checked += (_, _) => ChangeAcceleration(true);
        _denoAcceleration.Unchecked += (_, _) => ChangeAcceleration(false);

        void Add(ContentControl control, string label, string id)
        {
            control.Margin = new(12, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            control.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(control, id);
            _localized.Add((control, label));
            row.Children.Add(control);
        }
    }

    private void BindDescriptionVariants()
    {
        _denoVariant.IsChecked = _draft.UseDeno;
        _standardVariant.IsChecked = !_draft.UseDeno;
        _denoAcceleration.IsChecked = _draft.AccelerateDeno;
        _denoAcceleration.ToolTip = _localize("SongClip.DenoAccelerateHint");
        AutomationProperties.SetHelpText(_denoAcceleration, (string)_denoAcceleration.ToolTip);
    }

    private void ShowDescriptionVariants()
    {
        _standardVariant.Visibility = _denoVariant.Visibility = _draft.FromSong ? Visibility.Collapsed : Visibility.Visible;
        _denoAcceleration.Visibility = !_draft.FromSong && _draft.UseDeno ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChangeDescriptionVariant(bool deno)
    {
        if (_binding || _draft.FromSong) return;
        Edit(_draft with { UseDeno = deno });
        ShowDescriptionVariants();
    }

    private void ChangeAcceleration(bool enabled)
    {
        if (!_binding && !_draft.FromSong && _draft.UseDeno)
            Edit(_draft with { AccelerateDeno = enabled });
    }
}
