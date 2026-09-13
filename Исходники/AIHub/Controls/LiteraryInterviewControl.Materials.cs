using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryInterviewControl
{
    private LiterarySourceIndex? _sourceIndex;
    private bool _preparingMaterials;
    private void BuildMaterials()
    {
        _body.Children.Add(LiteraryUi.Text(_l("Literary.Create.MaterialsHint")));
        foreach (var path in S.Materials.ToArray())
        {
            _body.Children.Add(LiteraryUi.Text(Path.GetFileName(path)));
            _body.Children.Add(LiteraryUi.Button(_l("Literary.Create.Remove"), () => Execute(async () =>
            {
                S.Materials.Remove(path); Change(); Save();
                if (_sourceIndex is not null) { await _sourceIndex.DisposeAsync(); _sourceIndex=null; }
            })));
        }
        _body.Children.Add(LiteraryUi.Button(_l("Literary.Create.AddMaterials"), () => Execute(async () =>
        {
            if (_session.Root is null) throw new IOException("Literary.Rag.LocationFirst");
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect=true, Filter="TXT, EPUB, PDF|*.txt;*.epub;*.pdf" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var folder = Path.Combine(_session.Root,"Preparation","Materials");
            LiteraryProjectLayout.CheckTreePath(folder); Directory.CreateDirectory(folder);
            foreach (var file in dialog.FileNames)
            {
                var destination = Path.Combine(folder,Guid.NewGuid().ToString("N")[..8] + "_" + Path.GetFileName(file));
                File.Copy(file,destination,false); S.Materials.Add(Path.GetRelativePath(_session.Root,destination));
            }
            Save(); await PrepareMaterialsAsync();
        })));
    }
    private async Task PrepareMaterialsAsync()
    {
        if (_session.Root is null) throw new IOException("Literary.Rag.LocationFirst");
        if (S.Materials.Count == 0) return;
        // Embedding and literary generation never run concurrently.
        _runtime?.Stop();
        var generation = ++_generation;
        using var cancellation = new CancellationTokenSource(); _cancel=cancellation; _busy=true; _preparingMaterials=true;
        _progress.IsIndeterminate=true; Render();
        try
        {
            if (_sourceIndex is not null) { await _sourceIndex.DisposeAsync(); _sourceIndex=null; }
            var index = new LiterarySourceIndex(projectRoot:_session.Root);
            try
            {
                var progress = new Progress<LiteraryPreparationProgress>(p =>
                {
                    if (generation != _generation) return;
                    _progress.IsIndeterminate=p.Percent < 0; if (p.Percent >= 0) _progress.Value=p.Percent;
                    _status.Text = _l("Literary.Rag." + p.Stage) + " " + p.Detail;
                });
                var paths = S.Materials.Select(path =>
                {
                    var full = Path.GetFullPath(Path.Combine(_session.Root,path));
                    if (!full.StartsWith(_session.Root + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("Source snapshot is outside the project.");
                    LiteraryProjectLayout.CheckTreePath(full);
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source snapshot links are not supported.");
                    return full;
                }).ToArray();
                await index.PrepareAsync(paths,progress,cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != _generation) throw new OperationCanceledException();
                _sourceIndex=index; index=null!;
            }
            finally { if (index is not null) await index.DisposeAsync(); }
        }
        finally { _busy=false; _preparingMaterials=false; _progress.IsIndeterminate=false; if (_cancel==cancellation) _cancel=null; }
    }
}
