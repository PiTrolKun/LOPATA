using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class LiteraryInterviewControl
{
    private LiteraryAnchorCompressionWindow? _compressionWindow;
    private Button? _reviewCreate;

    private void BuildReview()
    {
        _body.Children.Add(LiteraryUi.Text(T("Review"), true));
        if (!_session.Complete) _body.Children.Add(LiteraryUi.Text(T("MissingSteps")));
        S.InitialPositive ??= string.Join("\n", S.Records.Where(r => r.Step is 31 or 33 or 34).Select(r => r.Text));
        S.InitialNegative ??= string.Join("\n", S.Records.Where(r => r.Step == 32).Select(r => r.Text));
        _body.Children.Add(new LiteraryAnchorReviewPanel(_l,
            Input(S.InitialPositive, text => S.InitialPositive = text),
            Input(S.InitialNegative, text => S.InitialNegative = text), OpenCompression));
        _body.Children.Add(LiteraryUi.Text(_l("Literary.MemorySetup.AnchorsHint")));
        foreach (var record in S.Records) _body.Children.Add(LiteraryUi.Text(record.Question + "\n" + record.Text));
        _reviewCreate = LiteraryUi.Button(_l("Literary.Create.Save"),
            _session.Complete ? () => Execute(CreateAsync) : null, true);
        _body.Children.Add(_reviewCreate); UpdateReviewAvailability();
    }

    private void UpdateReviewAvailability()
    {
        if (_reviewCreate is not null) _reviewCreate.IsEnabled = _session.Complete && _compressionWindow?.IsWorking != true;
    }

    private void OpenCompression()
    {
        if (_compressionWindow is { } existing) { existing.Activate(); return; }
        if (_busy || _closing || _session.Root is null) return;
        try
        {
            // Capture exactly two current fields before queueing; do not reuse interview messages.
            var request = LiteraryAnchorCompression.Request(S.InitialPositive ?? "", S.InitialNegative ?? "", _l);
            _runtime ??= new LiteraryChatRuntime(_session.Root, preparing: true);
            var runtime = _runtime;
            var window = new LiteraryAnchorCompressionWindow(_l, (progress, ct) => runtime.FreeChatAsync(request, progress, ct))
            { Owner = System.Windows.Window.GetWindow(this) };
            _compressionWindow = window;
            window.WorkingChanged += UpdateReviewAvailability;
            window.Closed += (_, _) =>
            {
                window.WorkingChanged -= UpdateReviewAvailability;
                if (_compressionWindow == window) _compressionWindow = null;
                UpdateReviewAvailability();
            };
            window.Show();
        }
        catch (Exception) { _status.Text = _l("Paragraph.Failure"); }
    }
}
