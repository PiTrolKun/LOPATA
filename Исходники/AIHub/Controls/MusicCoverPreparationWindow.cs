using System.Windows;

namespace AIHub.Controls;

/// <summary>Hosts the existing image component screen without entering its generation workspace.</summary>
public sealed class MusicCoverPreparationWindow : Window
{
    private readonly ImageGenerationControl _control;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _prepared;
    public Task<bool> Completion => _completion.Task;
    public MusicCoverPreparationWindow(ImageGenerationControl control, Func<string, string> localize, Window? owner)
    {
        _control = control;
        MusicWishUi.PrepareWindow(this, localize("Generation.Preparation.Title") + " · Z-Image Turbo", "Music.Cover.Preparation", 900);
        Height = 540; MinWidth = 600; MinHeight = 400;
        if (owner is not null) { Owner = owner; Resources.MergedDictionaries.Insert(0, owner.Resources); }
        Content = control;
        control.PreparationCompleted += Prepared;
        Loaded += async (_, _) => await control.OpenPreparationOnlyAsync("z-image");
        Closed += (_, _) => {
            control.PreparationCompleted -= Prepared; control.DisposeRuntime();
            _completion.TrySetResult(_prepared);
        };
    }
    private void Prepared() { _prepared = true; Close(); }
    public void DownloadConnections(int connections) => _control.DownloadConnections(connections);
}
