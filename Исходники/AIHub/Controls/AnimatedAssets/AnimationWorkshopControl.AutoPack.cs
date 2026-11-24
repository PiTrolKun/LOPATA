using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.AnimatedAssets;
using ListBox = System.Windows.Controls.ListBox;
using SelectionMode = System.Windows.Controls.SelectionMode;

namespace AIHub.Controls.AnimatedAssets;

internal sealed partial class AnimationWorkshopControl
{
    private TextBlock? _packProgress;

    private static bool IsAutoEmojiPack(AnimationProject project) =>
        project.IsPack && !project.IsTeasePack && project.AutoPackEmojis is not null;

    private string CatalogEmojiName(AnimationEmojiEntry entry) =>
        T("EmojiCatalogLanguage") == "en" ? entry.EnglishName : entry.RussianName;

    private IEnumerable<AnimationEmojiOption> CatalogEmojiOptions() =>
        AnimationEmojiCatalog.Entries.Select(entry => AnimationEmojiOption.Create(entry.Emoji, CatalogEmojiName(entry)));

    private void ShowAutoEmojiPack()
    {
        _packProgress = LiteraryUi.Text("");
        _form.Children.Add(_packProgress);
        AddPicture(_project.Reference, _art, 340, T("Reference"));
        if (_running || Pending is not null)
        {
            UpdatePackProgress();
            return;
        }

        _form.Children.Add(LiteraryUi.Text(T("AutoPackHelp")));
        Slide("Duration", _project.AutoPackDuration, AnimationRenderer.MinimumDuration,
            AnimationRenderer.MaximumDuration, .25, duration => _project.AutoPackDuration = duration);

        var create = Command("AutoPackCreate", CreateAutoEmojiPackAsync, true);
        void RefreshSelection()
        {
            var count = _project.AutoPackEmojis!.Count;
            _packProgress.Text = F("AutoPackSelected", count, AnimationEmojiCatalog.Entries.Count);
            create.IsEnabled = count > 0;
        }
        AddFoldout("AutoPackComposition", () => ShowAutoPackComposition(RefreshSelection));
        AddButtons(create);
        RefreshSelection();
    }

    private void ShowAutoPackComposition(Action selectionChanged)
    {
        var list = new ListBox
        {
            ItemsSource = CatalogEmojiOptions().ToArray(),
            ItemTemplate = AnimationEmojiOption.CreateTemplate(),
            SelectionMode = SelectionMode.Multiple,
            MaxHeight = 360,
            Margin = new Thickness(0, 4, 0, 6)
        };
        AutomationProperties.SetAutomationId(list, "Animation.AutoPackCatalog");
        AutomationProperties.SetName(list, T("AutoPackComposition"));
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var selected = new HashSet<string>(_project.AutoPackEmojis!, StringComparer.Ordinal);
        foreach (var option in list.Items.OfType<AnimationEmojiOption>())
            if (selected.Contains(option.Emoji))
                list.SelectedItems.Add(option);
        list.SelectionChanged += (_, _) =>
        {
            _project.AutoPackEmojis = list.SelectedItems.OfType<AnimationEmojiOption>().Select(item => item.Emoji).ToList();
            selectionChanged();
            ScheduleSave();
        };
        AddButtons(PlainButton("AutoPackSelectAll", list.SelectAll), PlainButton("AutoPackSelectNone", list.UnselectAll));
        _form.Children.Add(list);
    }

    private async Task CreateAutoEmojiPackAsync()
    {
        if (!_project.ReferenceApproved || _project.Reference is not { } reference)
            throw new InvalidOperationException("Animation.NeedImage");

        var hash = Convert.ToHexString(SHA256.HashData(reference));
        var cards = AnimationEmojiPackPlan.Build(_project, _project.AutoPackEmojis!, _project.AutoPackDuration,
            CatalogEmojiName, emoji => AnimationEmojiCatalog.Find(emoji)!.ArtistAction + PreserveReactionFrame,
            reuseExisting: string.Equals(_project.AutoPackReferenceSha256, hash, StringComparison.Ordinal));
        var missing = cards.Where(card => !AnimationEmojiPackPlan.IsReady(card))
            .Select(card => new AnimationPackDrawItem($"card/{card.Id}/pose/{card.Poses[0].Id}", card.Poses[0].Prompt))
            .ToArray();
        if (missing.Length > 0 && !_models.Prepare(OwnerWindow, artist: true, withImages: true))
            return;

        _project.Cards = cards;
        _project.AutoPackEmojis = cards.Select(card => card.TargetEmoji).ToList();
        _project.SelectedCard = 0;
        _project.Quick = false;
        _project.AutoPackReferenceSha256 = hash;
        UpdatePackProgress();
        await CheckpointAsync();
        if (missing.Length == 0)
            await ApplyModelResultOnUiAsync(new("pack"));
        else
            await _models.DrawPackAsync(_recoveryPath, reference, missing, ApplyModelResultAsync,
                ApplicationBackgroundOperations.ExitToken);
    }

    private void UpdatePackProgress()
    {
        if (_packProgress is not null)
            _packProgress.Text = F("AutoPackProgress", _project.Cards.Count(AnimationEmojiPackPlan.IsReady),
                _project.AutoPackEmojis!.Count);
    }
}
