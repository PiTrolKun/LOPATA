using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services.AnimatedAssets;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.AnimatedAssets;

internal sealed partial class AnimationWorkshopControl
{
    private void ShowSetup()
    {
        Label("Setup");
        OptionButtons("Purpose", _project.Purpose, value => _project.Purpose = value,
            ("chat", "Chat"), ("decoration", "Decoration"));
        OptionButtons("Amount", _project.IsPack ? "pack" : "one", value =>
        {
            _project.IsPack = value == "pack";
            BuildPage();
        },
            ("one", "One"), ("pack", "Pack"));
        if (_project.IsPack)
            _form.Children.Add(ModeButton("AutoPackStart", "AutoPackStartHelp", quick: false));
        else
        {
            _form.Children.Add(LiteraryUi.Text(T("ChooseMethod")));
            _form.Children.Add(Pair(ModeButton("Quick", "QuickHelp", quick: true),
                ModeButton("Keyframes", "KeyframesHelp", quick: false), 520));
        }
        AddProjectTools(_form, includeEntryActions: true);
    }

    private void Start(bool quick)
    {
        _documentStarted = true;
        var switched = _project.Quick != quick;
        _project.Quick = quick;
        if (switched)
            foreach (var card in _project.Cards)
                card.Motion = quick ? "jump" : "frames";
        if (_project.Reference is null)
            _project.ReferenceIsEmoji = _project.Purpose == "chat";
        if (!_project.IsPack && _project.Cards.Count == 0)
        {
            var card = NewCard(quick ? "Quick" : _project.Purpose == "decoration" ? "Scene" : "Reaction", quick ? "jump" : "frames");
            if (_project.Purpose == "decoration") card.Kind = "scene";
            _project.Cards.Add(card);
        }
        _project.SelectedCard = 0;
        GoTo(ReferencePage);
    }

    private void ShowReference()
    {
        Label("Reference");
        AddButtons(Command("LoadImage", async () =>
        {
            if (await PickImageAsync() is { } data) SetReference(data, null);
        }, primary: _project.Reference is null));
        Choice("SourceType", _project.ReferenceIsEmoji ? "emoji" : "other", value =>
        {
            _project.ReferenceIsEmoji = value == "emoji";
            BuildPage();
        }, ("emoji", "Emoji"), ("other", "SceneImage"));
        if (_project.ReferenceIsEmoji && _project.Reference is not null)
            ShowEmojiPicker("SourceEmoji", _project.ReferenceEmoji, (emoji, name) =>
            {
                _project.ReferenceEmoji = emoji;
                _project.ReferenceEmojiName = name;
            }, inputName: _project.ReferenceEmojiName);

        if (_project.Reference is not null)
            AddButtons(PlainButton("ApproveReference", ApproveReference, true));
        AddFoldout("ReferenceDrawing", ShowReferenceDrawing);
        AddPicture(_project.Reference, _art, 420, _project.Reference is null ? null : T("Reference"));
        if (_project.ReferenceGuide is not null)
            AddPicture(_project.ReferenceGuide, _art, 128, T("Guide"));
    }

    private void ShowReferenceDrawing()
    {
        Edit("DescribeReference", _project.ReferenceDescription, text => _project.ReferenceDescription = text, multiline: true);
        var transparency = new CheckBox
        {
            Content = LiteraryUi.Text(T("Transparent")),
            IsChecked = _project.Transparent,
            Margin = new Thickness(0, 4, 0, 8)
        };
        transparency.Checked += (_, _) => { _project.Transparent = true; ScheduleSave(); };
        transparency.Unchecked += (_, _) => { _project.Transparent = false; ScheduleSave(); };
        _form.Children.Add(transparency);
        AddButtons(Command("DrawReference", () => DrawReferenceAsync(editCurrent: false), true));
        if (_project.Reference is not null)
            AddButtons(Command("EditReference", () => DrawReferenceAsync(editCurrent: true)));
        if (_project.ReferenceIsEmoji && _project.Reference is not null && !_project.Quick)
            AddButtons(Command("DrawCalm", () => DrawAsync("reference", F("CalmPrompt", _project.ReferenceDescription),
                _project.Reference)));
        var attach = Command("AttachGuide", async () =>
        {
            if (await PickImageAsync() is { } data) _project.ReferenceGuide = data;
        });
        if (_project.ReferenceGuide is null)
            AddButtons(attach);
        else
            AddButtons(attach, PlainButton("RemoveGuide", () =>
            {
                _project.ReferenceGuide = null;
                ScheduleSave();
                BuildPage();
            }));
    }

    private Task DrawReferenceAsync(bool editCurrent)
    {
        var (prompt, source) = ReferenceDrawingRequest(_project, editCurrent, T);
        return DrawAsync("reference", prompt, source);
    }

    internal static (string Prompt, byte[]? Source) ReferenceDrawingRequest(
        AnimationProject project, bool editCurrent, Func<string, string> text)
    {
        var description = project.ReferenceDescription.Trim();
        if (editCurrent)
        {
            var source = project.Reference ?? throw new InvalidOperationException("Animation.NeedImage");
            if (description.Length == 0) throw new InvalidOperationException("Animation.NeedDescription");
            return (description, source);
        }
        if (description.Length == 0)
        {
            if (!project.ReferenceIsEmoji) throw new InvalidOperationException("Animation.NeedDescription");
            description = text("CalmFace");
        }
        return (string.Format(text("ReferencePrompt"), description), project.ReferenceGuide);
    }

    private void SetReference(byte[] bytes, AnimationModelUse? model)
    {
        _project.Reference = bytes;
        _project.ReferenceApproved = false;
        _project.Models = model is null ? [] : [model];
    }

    private void ApproveReference()
    {
        if (_project.Reference is null) { ShowMessage("NeedImage"); return; }
        if (_project.ReferenceIsEmoji && string.IsNullOrWhiteSpace(_project.ReferenceEmoji))
        {
            ShowMessage("NeedEmoji");
            return;
        }
        _project.ReferenceApproved = true;
        if (_project.IsPack)
            GoTo(PackPage);
        else
            GoTo(MotionPage);
    }

    private void ShowPack()
    {
        Label("Pack");
        if (!IsAutoEmojiPack(_project) || _project.Cards.Count == 0)
            Choice("PackKind", _project.IsTeasePack ? "teases" : "emoji", value =>
            {
                _project.IsTeasePack = value == "teases";
                BuildPage();
            }, ("emoji", "EmojiPack"), ("teases", "TeasePack"));
        if (!_project.IsTeasePack && _project.Cards.Count == 0)
            _project.AutoPackEmojis ??= AnimationEmojiCatalog.Entries.Select(item => item.Emoji).ToList();
        if (IsAutoEmojiPack(_project))
        {
            ShowAutoEmojiPack();
            return;
        }
        Edit("DescribePack", _project.PackDescription, text => _project.PackDescription = text, multiline: true);
        AddButtons(PlainButton("SuggestPack", () =>
        {
            if (_project.IsTeasePack)
            {
                AddTease("SlowClap");
                AddTease("Bow");
                AddTease("Curtain");
            }
            else
            {
                foreach (var (emoji, key, _) in Reactions.Take(12))
                {
                    if (_project.Cards.Any(card => card.Kind == "reaction" && card.TargetEmoji == emoji)) continue;
                    var card = NewCard(key);
                    card.TargetEmoji = emoji;
                    _project.Cards.Add(card);
                }
            }
            ScheduleSave();
            BuildPage();
        }), PlainButton("AddCard", () =>
        {
            var card = NewCard(_project.IsTeasePack ? "Tease" : "Reaction");
            card.TargetEmoji = "";
            if (_project.IsTeasePack) card.Kind = "scene";
            _project.Cards.Add(card);
            _project.SelectedCard = _project.Cards.Count - 1;
            ScheduleSave();
            BuildPage();
        }));
        if (_project.Cards.Count > 0)
        {
            ChooseCard();
            Edit("CardName", Card.Name, name => Card.Name = name);
            if (Card.Kind == "scene")
                Edit("DescribeScene", Card.Story, text => Card.Story = text, multiline: true);
            else
                ShowEmojiPicker("TargetEmoji", Card.TargetEmoji, (emoji, _) => Card.TargetEmoji = emoji);
            AddButtons(PlainButton("RemoveCard", () =>
            {
                _project.Cards.Remove(Card);
                _project.SelectedCard = Math.Max(0, _project.SelectedCard - 1);
                ScheduleSave();
                BuildPage();
            }));
        }
        AddButtons(PlainButton("Continue", () =>
        {
            if (_project.Cards.Count == 0 || _project.Cards.Any(card => string.IsNullOrWhiteSpace(card.Name)))
            { ShowMessage("NeedCard"); return; }
            if (_project.Cards.Where(card => card.Kind == "reaction")
                    .GroupBy(card => card.TargetEmoji.Trim()).Any(group => group.Key.Length == 0 || group.Count() > 1) ||
                _project.Cards.GroupBy(card => card.Name.Trim(), StringComparer.CurrentCultureIgnoreCase).Any(group => group.Count() > 1))
            { ShowMessage("DistinctCards"); return; }
            GoTo(SketchPage);
        }, true));
        _art.Children.Add(LiteraryUi.Text(T("PackHelp")));
        AddPicture(_project.Reference, _art, 340, T("Reference"));
    }

    private void AddTease(string key)
    {
        if (_project.Cards.Any(card => card.Name == T(key))) return;
        var card = NewCard(key);
        card.Kind = "scene";
        card.Story = T(key + "Action");
        _project.Cards.Add(card);
    }

    private AnimationCard NewCard(string key, string? motion = null) =>
        new() { Name = T(key), Motion = motion ?? (_project.Quick ? "jump" : "frames") };

    private void ShowSketches()
    {
        Label("Sketches");
        ChooseCard();
        var card = Card;
        Edit("CardDescription", card.Kind == "scene" ? card.Story : card.Name, text =>
        {
            if (card.Kind == "scene") card.Story = text;
            else card.Name = text;
        }, multiline: true);
        AddButtons(Command("DrawSketch", () => DrawAsync($"card/{card.Id}/still", SketchPrompt(card), _project.Reference)),
            Command("LoadImage", async () =>
            {
                if (await PickImageAsync() is { } data) { card.Still = data; card.Models.Clear(); card.Reviewed = false; }
            }));
        if (card.Kind == "reaction" && card.Still is not null && !card.Reviewed)
        {
            AddButtons(PlainButton("ApproveSketch", () =>
            {
                card.Reviewed = true;
                ScheduleSave();
                BuildPage();
            }));
        }
        AddPicture(card.Still ?? _project.Reference, _form, 170);
        ShowCardGrid(_art);
        if (_project.Cards.Any(candidate => candidate.Still is null))
            AddButtons(Command("DrawPackSketches", DrawPackSketchesAsync));
        AddButtons(PlainButton("ApproveSketches", () =>
        {
            if (_project.Cards.Any(candidate => candidate.Still is null))
            { ShowMessage("NeedPackImages"); return; }
            foreach (var candidate in _project.Cards) candidate.Reviewed = true;
            GoTo(MotionPage);
        }, true));
    }

    private async Task DrawPackSketchesAsync()
    {
        if (!_models.Prepare(OwnerWindow, artist: true)) return;
        foreach (var card in _project.Cards.Where(candidate => candidate.Still is null).ToList())
        {
            await DrawAsync($"card/{card.Id}/still", SketchPrompt(card), _project.Reference, prepare: false);
            if (card.Still is null || Pending is not null) break;
        }
    }

    private string SketchPrompt(AnimationCard card) => card.Kind == "scene"
        ? F("SceneSketchPrompt", card.Story, _project.PackDescription)
        : ArtistReactionInstruction(card.TargetEmoji, card.ReactionDetails);

    private void ChooseCard()
    {
        if (!_project.IsPack || _project.Cards.Count == 0) return;
        _form.Children.Add(LiteraryUi.Text(T("ChooseCard")));
        var box = new System.Windows.Controls.ComboBox
        {
            ItemsSource = _project.Cards.Select(card => card.Name).ToList(),
            SelectedIndex = Math.Clamp(_project.SelectedCard, 0, _project.Cards.Count - 1),
            Margin = new Thickness(0, 0, 0, 8)
        };
        AutomationProperties.SetName(box, T("ChooseCard"));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            _project.SelectedCard = box.SelectedIndex;
            ScheduleSave();
            BuildPage();
        };
        _form.Children.Add(box);
    }

    private void ShowCardGrid(Panel host)
    {
        var grid = new WrapPanel();
        for (var index = 0; index < _project.Cards.Count; index++)
        {
            var number = index;
            var card = _project.Cards[number];
            var tile = new StackPanel { Width = 124, Margin = new Thickness(4) };
            AddPicture(card.Still ?? _project.Reference, tile, 96);
            tile.Children.Add(LiteraryUi.Text(card.Name));
            var button = LiteraryUi.Button(card.Name, () =>
            {
                _project.SelectedCard = number;
                ScheduleSave();
                BuildPage();
            }, number == _project.SelectedCard);
            button.Content = tile;
            AutomationProperties.SetName(button, card.Name);
            button.Padding = new Thickness(4);
            grid.Children.Add(button);
        }
        host.Children.Add(grid);
    }

    private void ShowEmojiPicker(string label, string current, Action<string, string> change, string? inputName = null)
    {
        _form.Children.Add(LiteraryUi.Text(T(label)));
        var options = CatalogEmojiOptions().Append(AnimationEmojiOption.Create("", T("CustomEmoji"))).ToList();
        var canonical = AnimationEmojiCatalog.Find(current)?.Emoji;
        var match = options.FindIndex(item => item.Emoji == canonical);
        var customIndex = options.Count - 1;
        var custom = new StackPanel { Visibility = match < 0 ? Visibility.Visible : Visibility.Collapsed };
        var input = new TextBox { Text = match < 0 ? current : "", Margin = new Thickness(0, 2, 0, 4) };
        var name = new TextBox { Text = match < 0 ? inputName ?? "" : "", Margin = new Thickness(0, 2, 0, 4) };
        AutomationProperties.SetName(input, T("CustomEmoji"));
        custom.Children.Add(input);
        if (inputName is not null)
        {
            custom.Children.Add(LiteraryUi.Text(T("EmojiName")));
            AutomationProperties.SetName(name, T("EmojiName"));
            custom.Children.Add(name);
        }
        void SaveCustom()
        {
            change(input.Text.Trim(), name.Text.Trim());
            ScheduleSave();
        }
        input.TextChanged += (_, _) => SaveCustom();
        name.TextChanged += (_, _) => SaveCustom();
        var picker = new System.Windows.Controls.ComboBox
        {
            ItemsSource = options,
            ItemTemplate = AnimationEmojiOption.CreateTemplate(),
            SelectedIndex = match < 0 ? customIndex : match,
            Margin = new Thickness(0, 0, 0, 4)
        };
        AutomationProperties.SetName(picker, T(label));
        picker.SelectionChanged += (_, _) =>
        {
            var selected = picker.SelectedIndex;
            if (selected < 0) return;
            custom.Visibility = selected == customIndex ? Visibility.Visible : Visibility.Collapsed;
            if (selected == customIndex) SaveCustom();
            else change(options[selected].Emoji, options[selected].Label);
            ScheduleSave();
        };
        _form.Children.Add(picker);
        _form.Children.Add(custom);
    }

    private TextBox Edit(string key, string value, Action<string> change, bool multiline = false)
    {
        _form.Children.Add(LiteraryUi.Text(T(key)));
        var box = new TextBox
        {
            Text = value, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
            MinHeight = multiline ? 66 : 28, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(4)
        };
        AutomationProperties.SetName(box, T(key));
        box.TextChanged += (_, _) => { change(box.Text); ScheduleSave(); };
        _form.Children.Add(box);
        return box;
    }

    private void AddPicture(byte[]? bytes, Panel host, double maximum, string? title = null)
    {
        if (title is not null) host.Children.Add(LiteraryUi.Text(title));
        if (bytes is null) return;
        try
        {
            var still = AnimationRenderer.LoadStill(bytes);
            host.Children.Add(new Image { Source = still, MaxHeight = maximum, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 8) });
        }
        catch (InvalidDataException error) { host.Children.Add(LiteraryUi.Text(_localize(error.Message))); }
    }

    private async Task<byte[]?> PickImageAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("ImageFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(OwnerWindow) != true) return null;
        if (new FileInfo(dialog.FileName).Length > 24 * 1024 * 1024)
            throw new InvalidDataException("Animation.ImageTooLarge");
        var bytes = await File.ReadAllBytesAsync(dialog.FileName);
        _ = AnimationRenderer.LoadStill(bytes);
        return bytes;
    }

    private void ShowMessage(string key)
    {
        _status = T(key);
        BuildPage();
    }
}
