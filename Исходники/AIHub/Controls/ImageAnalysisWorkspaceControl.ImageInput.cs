using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using DragEventArgs = System.Windows.DragEventArgs;

namespace AIHub.Controls;

public sealed class ImageInputEventArgs(ImageInput input) : EventArgs
{
    public ImageInput Input { get; } = input;
}

public partial class ImageAnalysisWorkspaceControl
{
    public event EventHandler<ImageInputEventArgs>? ImageInputRequested;
    public bool CanImportImage => _session is not null && !_isBusy && !_readOnlyMode
        && _session.Status != ImageAnalysisLiteraryStatuses.Completed && !_session.ContextBlocked
        && !OmniSessionCompatibility.IsRetiredModelSession(_session);

    public void RefreshImageAvailability()
    {
        if (_isBusy || _session is null) return;
        ApplyFile(_session.File);
        SetInteractionEnabled(true);
    }

    private static bool IsTextTarget(object? value)
    {
        for (var node = value as DependencyObject; node is not null;)
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox || node is System.Windows.Controls.ComboBox { IsEditable: true }) return true;
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // Window-level keyboard routing must not depend on focus inside this control.
    public bool CanPasteImage(object? focusedElement) => CanImportImage
        && _currentStep == ImageAnalysisLiterarySteps.Image && !IsTextTarget(focusedElement);

    public void PasteImageFromClipboard()
    {
        if (!CanPasteImage(Keyboard.FocusedElement)) return;
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null) throw new ImageInputException("ImageInput.Unsupported");
            SubmitImage(data);
        }
        catch (Exception ex) { SetOperationError(_localize(ex is ImageInputException known ? known.Key : "ImageInput.ClipboardBusy")); }
    }

    private void SubmitImage(System.Windows.IDataObject data)
    {
        try { ImageInputRequested?.Invoke(this, new ImageInputEventArgs(ImageTransferReader.Read(data))); }
        catch (Exception ex) { SetOperationError(_localize(ex is ImageInputException known ? known.Key : "ImageInput.Unsupported")); }
    }

    private void ImageInput_DragOver(object sender, DragEventArgs e)
    {
        if (IsTextTarget(e.OriginalSource)) return;
        e.Handled = true;
        var accept = CanImportImage && ImageTransferReader.MayContainImage(e.Data);
        e.Effects = accept ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        if (accept) SelectedImageCard.BorderBrush = System.Windows.Media.Brushes.DodgerBlue;
        else ClearDropHighlight();
    }
    private void ImageInput_DragLeave(object sender, DragEventArgs e) => ClearDropHighlight();
    private void ClearDropHighlight() => SelectedImageCard.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
    private void ImageInput_Drop(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        if (IsTextTarget(e.OriginalSource)) return;
        e.Handled = true;
        if (CanImportImage) SubmitImage(e.Data);
    }
}
