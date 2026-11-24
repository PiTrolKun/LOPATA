using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AIHub.Services.AnimatedAssets;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.AnimatedAssets;

internal sealed partial class AnimationWorkshopControl
{
    private int _poseIndex;

    private void ShowMotion()
    {
        Label("Motion");
        ChooseCard();
        var card = Card;
        if (card.Motion != "frames")
        {
            ShowQuickMotion(card);
            if (!_project.Quick) AddFoldout("MoreOptions", () => ShowMethodChoice(card));
            return;
        }

        OptionButtons("AnimationType", card.Kind, value =>
        {
            card.Kind = value;
            BuildPage();
        }, ("reaction", "Reaction"), ("sequence", "Sequence"), ("scene", "Scene"));
        ShowDuration(card);

        switch (card.Kind)
        {
            case "reaction":
                ShowEmojiPicker("TargetEmoji", card.TargetEmoji, (emoji, _) => card.TargetEmoji = emoji);
                Edit("ReactionDetails", card.ReactionDetails, value => card.ReactionDetails = value, multiline: true);
                _form.Children.Add(LiteraryUi.Text(T("ReactionHelp")));
                AddButtons(PlainButton("CreatePlan", () => CreateEmojiPlan(card), true));
                break;
            case "sequence":
                Edit("Sequence", card.Sequence, value => card.Sequence = value);
                _form.Children.Add(LiteraryUi.Text(T("SequenceHelp")));
                AddButtons(PlainButton("CreatePlan", () => CreateEmojiPlan(card), true));
                break;
            case "scene":
                Edit("DescribeScene", card.Story, value => card.Story = value, multiline: true);
                AddButtons(Command("Consult", () => PlanAsync(card), true),
                    PlainButton("EditPoses", () => { _poseIndex = 0; GoTo(PosesPage); }));
                break;
        }
        if (card.Kind != "scene" && card.Poses.Count > 0)
            AddButtons(PlainButton("EditPoses", () => { _poseIndex = 0; GoTo(PosesPage); }));
        AddFoldout("MoreOptions", () =>
        {
            if (card.Kind == "scene")
            {
                Edit("Keep", card.Keep, value => card.Keep = value);
                Edit("Ambient", card.Ambient, value => card.Ambient = value);
            }
            ShowMethodChoice(card);
        });
        AddPicture(BaseImage(card), _art, 400);
        ShowTimeline(card, _art);
    }

    private void ShowMethodChoice(AnimationCard card) =>
        Choice("Method", card.Motion == "frames" ? "frames" : "quick", value =>
        {
            card.Motion = value == "frames" ? "frames" : "jump";
            BuildPage();
        }, ("frames", "Keyframes"), ("quick", "Quick"));

    private void ShowQuickMotion(AnimationCard card)
    {
        Choice("Quick", card.Motion, value => { card.Motion = value; BuildPage(); },
            ("jump", "Jump"), ("sway", "Sway"), ("blink", "Blink"));
        ShowDuration(card);
        if (card.Motion == "blink")
        {
            _form.Children.Add(LiteraryUi.Text(T("BlinkHelp")));
            AddButtons(Command("LoadClosedEye", async () =>
            {
                if (await PickImageAsync() is { } data) card.ClosedEye = data;
            }));
            var source = BaseImage(card);
            try { if (source is not null) ShowEyeSelector(card, source); }
            catch (System.IO.InvalidDataException error) { _art.Children.Add(LiteraryUi.Text(_localize(error.Message))); }
            AddPicture(card.ClosedEye, _art, 140, T("ClosedEye"));
        }
        else
        {
            Slide("Strength", card.Strength, 0, 1, .05, value => card.Strength = value);
            AddPicture(BaseImage(card), _art, 380);
        }
        AddButtons(Command("Preview", () => PreparePreviewAsync(card), true));
    }

    private void ShowDuration(AnimationCard card) =>
        Slide("Duration", card.Duration, 2, 30, .25, duration =>
        {
            var scale = duration / card.Duration;
            foreach (var pose in card.Poses)
            {
                pose.Time *= scale;
                foreach (var effect in pose.Effects)
                    effect.Duration = Math.Min(effect.Duration * scale, duration - pose.Time);
            }
            card.Duration = duration;
        });

    private void Slide(string key, double value, double min, double max, double tick, Action<double> change)
    {
        var label = LiteraryUi.Text(T(key) + " · " + value.ToString("0.##", CultureInfo.CurrentCulture));
        _form.Children.Add(label);
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, TickFrequency = tick,
            IsSnapToTickEnabled = false, SmallChange = tick, LargeChange = tick * 4,
            Margin = new Thickness(0, 0, 0, 6)
        };
        slider.ValueChanged += (_, args) =>
        {
            var number = Math.Round(args.NewValue / tick) * tick;
            label.Text = T(key) + " · " + number.ToString("0.##", CultureInfo.CurrentCulture);
            change(number);
            ScheduleSave();
        };
        _form.Children.Add(slider);
    }

    private byte[]? BaseImage(AnimationCard card) =>
        card.Motion == "frames" && card.Kind != "scene"
            ? _project.Reference ?? card.Still
            : card.Still ?? _project.Reference;

    private void CreateEmojiPlan(AnimationCard card)
    {
        var stages = card.Kind == "sequence" ? card.Sequence.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) :
            [T("CalmFace"), card.TargetEmoji.Trim()];
        if (stages.Length is < 2 or > 15 || string.IsNullOrWhiteSpace(stages[1]))
        { ShowMessage("NeedSequence"); return; }
        if (card.Kind == "sequence") card.Duration = stages.Length;
        var poses = new List<AnimationPose>();
        for (var index = 1; index < stages.Length; index++)
        {
            poses.Add(new()
            {
                Time = card.Duration * index / stages.Length,
                Text = stages[index],
                Prompt = ReactionInstruction(stages[index],
                    card.Kind == "reaction" ? card.ReactionDetails : "", T),
                Image = card.Kind == "reaction" ? card.Still : null,
                Models = card.Kind == "reaction" && card.Still is not null ? [.. card.Models] : []
            });
        }
        card.Poses = poses;
        _poseIndex = 0;
        GoTo(PosesPage);
    }

    private void ShowTimeline(AnimationCard card, Panel panel)
    {
        if (card.Poses.Count == 0) return;
        panel.Children.Add(LiteraryUi.Text(F("Timeline", 0d, T("StartFrame"))));
        foreach (var pose in card.Poses.OrderBy(pose => pose.Time))
        {
            panel.Children.Add(LiteraryUi.Text(F("Timeline", pose.Time, pose.Text)));
            if (pose.Effects.Count > 0)
                panel.Children.Add(LiteraryUi.Text(string.Join(" · ", pose.Effects.Select(effect => effect.Text))));
        }
        panel.Children.Add(LiteraryUi.Text(F("Timeline", card.Duration, T("ReturnFrame"))));
        if (!string.IsNullOrWhiteSpace(card.Ambient)) panel.Children.Add(LiteraryUi.Text(T("Ambient") + " · " + card.Ambient));
    }

    private void ShowPoses()
    {
        var card = Card;
        Label(card.Motion == "frames" ? "Poses" : "Motion");
        ChooseCard();
        if (card.Motion != "frames")
        {
            ShowQuickMotion(card);
            return;
        }

        var addPose = PlainButton("AddPose", () =>
        {
            var times = card.Poses.Select(pose => pose.Time).Prepend(0).Append(card.Duration).Order().ToArray();
            var gap = times.Zip(times.Skip(1)).OrderByDescending(pair => pair.Second - pair.First).First();
            var pose = new AnimationPose { Time = Math.Round((gap.First + gap.Second) / 2, 2) };
            card.Poses.Add(pose);
            _poseIndex = card.Poses.OrderBy(p => p.Time).ToList().IndexOf(pose);
            ScheduleSave();
            BuildPage();
        }, primary: card.Poses.Count == 0);

        card.Poses.Sort((left, right) => left.Time.CompareTo(right.Time));
        if (card.Poses.Count == 0)
        {
            AddButtons(addPose);
            AddPicture(BaseImage(card), _art, 300);
            return;
        }

        _poseIndex = Math.Clamp(_poseIndex, 0, card.Poses.Count - 1);
        var selected = card.Poses[_poseIndex];
        if (card.Poses.Count > 1)
        {
            var picker = new System.Windows.Controls.ComboBox
            {
                ItemsSource = card.Poses.Select((pose, index) => F("PoseNumber", index + 1, pose.Time)).ToList(),
                SelectedIndex = _poseIndex,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Automation.AutomationProperties.SetName(picker, T("SelectedPose"));
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0) return;
                _poseIndex = picker.SelectedIndex;
                BuildPage();
            };
            _form.Children.Add(picker);
        }

        Edit("AtSecond", selected.Time.ToString("0.##", CultureInfo.CurrentCulture),
            value => { if (TryNumber(value, out var time)) selected.Time = time; });
        Edit("PoseText", selected.Text, value => selected.Text = value, multiline: true);
        var missing = card.Poses.Count(pose => pose.Image is null);
        AddButtons(Command("DrawPose", () => DrawPoseAsync(card, selected), primary: missing == 1 && selected.Image is null));
        if (missing > 1 || missing == 1 && selected.Image is not null)
            AddButtons(Command("DrawMissing", () => DrawMissingAsync(card), true));
        if (missing == 0)
            AddButtons(Command("Preview", () => PreparePreviewAsync(card), true));
        AddFoldout("PoseOptions", () =>
        {
            Edit("ArtistInstruction", ReactionPoseInstruction(_project, card, selected, T), value => selected.Prompt = value, multiline: true);
            AddButtons(addPose,
                Command("LoadPose", async () =>
                {
                    if (await PickImageAsync() is { } data) { selected.Image = data; selected.Models.Clear(); }
                }),
                PlainButton("RemovePose", () =>
                {
                    card.Poses.Remove(selected);
                    ScheduleSave();
                    BuildPage();
                }));
            ShowEffects(card, selected);
        });

        if (selected.Image is not null)
        {
            var before = new StackPanel();
            var after = new StackPanel();
            AddPicture(BaseImage(card), before, 220, T("Reference"));
            AddPicture(selected.Image, after, 220, T("SelectedPose"));
            _art.Children.Add(Pair(before, after, 360));
        }
        else AddPicture(BaseImage(card), _art, 300, T("Reference"));
        ShowTimeline(card, _art);
    }

    private void ShowEffects(AnimationCard card, AnimationPose pose)
    {
        if (card.Kind != "scene") return;
        var panel = new StackPanel();
        var effectOptions = new WrapPanel();
        foreach (var preset in new[] { (Kind: "lightning", Title: "Flash"), (Kind: "notes", Title: "Notes") })
        {
            effectOptions.Children.Add(PlainButton(preset.Title, () =>
            {
                var remaining = card.Duration - pose.Time;
                if (remaining <= 0) { ShowMessage("BadTimeline"); return; }
                pose.Effects.Add(new() { Kind = preset.Kind, Text = T(preset.Title), Duration = Math.Min(remaining, .4) });
                ScheduleSave();
                BuildPage();
            }));
        }
        panel.Children.Add(effectOptions);
        foreach (var effect in pose.Effects)
        {
            var line = new WrapPanel();
            var field = new System.Windows.Controls.TextBox
            {
                Text = effect.Duration.ToString("0.##", CultureInfo.CurrentCulture), Width = 64,
                Margin = new Thickness(4), ToolTip = T("Duration")
            };
            field.TextChanged += (_, _) => { if (TryNumber(field.Text, out var seconds)) effect.Duration = seconds; ScheduleSave(); };
            line.Children.Add(LiteraryUi.Text(effect.Text));
            line.Children.Add(field);
            line.Children.Add(PlainButton("RemoveEffect", () => { pose.Effects.Remove(effect); ScheduleSave(); BuildPage(); }));
            panel.Children.Add(line);
        }
        _form.Children.Add(new Expander { Header = T("Effects"), Content = panel });
    }

    private void ShowEyeSelector(AnimationCard card, byte[] bytes)
    {
        _art.Children.Add(LiteraryUi.Text(T("EyeRegion")));
        var bitmap = AnimationRenderer.LoadStill(bytes);
        var factor = Math.Min(330.0 / bitmap.PixelWidth, 300.0 / bitmap.PixelHeight);
        var width = bitmap.PixelWidth * factor;
        var height = bitmap.PixelHeight * factor;
        var area = new Canvas { Width = width, Height = height, ClipToBounds = true, Cursor = System.Windows.Input.Cursors.Cross };
        area.Children.Add(new Image { Source = bitmap, Width = width, Height = height, Stretch = Stretch.Fill });
        var outline = new Rectangle { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, IsHitTestVisible = false };
        area.Children.Add(outline);
        void Outline(AnimationEyeRegion bounds)
        {
            Canvas.SetLeft(outline, bounds.X * width);
            Canvas.SetTop(outline, bounds.Y * height);
            outline.Width = bounds.Width * width;
            outline.Height = bounds.Height * height;
        }
        if (card.EyeRegion is { } saved) Outline(saved);
        Point? origin = null;
        AnimationEyeRegion Region(Point a, Point b)
        {
            var left = Math.Clamp(Math.Min(a.X, b.X), 0, width);
            var top = Math.Clamp(Math.Min(a.Y, b.Y), 0, height);
            return new(left / width, top / height, (Math.Clamp(Math.Max(a.X, b.X), 0, width) - left) / width,
                (Math.Clamp(Math.Max(a.Y, b.Y), 0, height) - top) / height);
        }
        area.MouseLeftButtonDown += (_, args) => { origin = args.GetPosition(area); area.CaptureMouse(); };
        area.MouseMove += (_, args) => { if (origin is { } start) Outline(Region(start, args.GetPosition(area))); };
        area.MouseLeftButtonUp += (_, args) =>
        {
            if (origin is not { } start) return;
            var bounds = Region(start, args.GetPosition(area));
            origin = null;
            area.ReleaseMouseCapture();
            if (bounds.Width > .01 && bounds.Height > .01) card.EyeRegion = bounds;
            if (card.EyeRegion is { } current) Outline(current);
            ScheduleSave();
        };
        _art.Children.Add(new Viewbox { Child = area, MaxWidth = width, MaxHeight = height, StretchDirection = StretchDirection.DownOnly });
    }

    private static bool TryNumber(string value, out double number) =>
        (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        && double.IsFinite(number);
}
