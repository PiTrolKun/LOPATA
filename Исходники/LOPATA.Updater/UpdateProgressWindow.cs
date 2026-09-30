namespace Lopata.Updater;

/// <summary>Content-sized, DPI-aware progress UI; installation remains owned by UpdateHost.</summary>
internal sealed class UpdateProgressWindow : Form
{
    private readonly Label _status;
    private readonly Button _cancel;
    public event EventHandler? CancelRequested;

    public UpdateProgressWindow(string initialStatus, string cancelText)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        Text = "ЛОПАТА / LOPATA";
        ClientSize = new Size(520, 170);
        MinimumSize = new Size(520, 200);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ControlBox = false;
        BackColor = Color.FromArgb(17, 24, 39);
        ForeColor = Color.FromArgb(235, 240, 249);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20), ColumnCount = 1, RowCount = 3,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status = new Label
        {
            AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(480, 0),
            Text = initialStatus, AutoEllipsis = false, Margin = new Padding(0, 0, 0, 20),
            UseMnemonic = false
        };
        var progress = new ProgressBar
        {
            Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 35, Margin = new Padding(0, 0, 0, 16)
        };
        _cancel = new Button
        {
            Text = cancelText, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 36),
            Anchor = AnchorStyles.Right, Margin = Padding.Empty, Padding = new Padding(14, 6, 14, 6),
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            BackColor = Color.FromArgb(30, 41, 59), ForeColor = ForeColor
        };
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(112, 126, 147);
        _cancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 58, 79);
        _cancel.Click += (_, _) =>
        {
            if (!_cancel.Enabled) return;
            _cancel.Enabled = false;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        };
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(progress, 0, 1);
        layout.Controls.Add(_cancel, 0, 2);
        Controls.Add(layout);
        ResumeLayout(true);
    }

    public void SetStatus(string text)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
        _status.Text = text;
    }
}
