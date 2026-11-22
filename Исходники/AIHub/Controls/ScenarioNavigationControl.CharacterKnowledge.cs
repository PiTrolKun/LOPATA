using System.Windows;
using AIHub.Controls.CharacterKnowledge;
using AIHub.Services;
using AIHub.Services.CharacterKnowledge;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    internal CharacterKnowledgeControl? CharacterKnowledgeView { get; private set; }
    internal bool IsCharacterKnowledge { get; private set; }

    internal void ShowCharacterKnowledge(Window owner, Func<string> languageCode, KnowledgeStore? store = null)
    {
        CharacterKnowledgeView ??= new(owner, _localize, languageCode, store);
        IsCharacterKnowledge = true;
        SelectedDirectionId = ScenarioNavigationCatalog.Utilities;
        Content = CharacterKnowledgeView;
        CharacterKnowledgeView.Enter();
        StateChanged?.Invoke();
    }

    private bool LeaveCharacterKnowledge()
    {
        if (IsCharacterKnowledge && CharacterKnowledgeView is { } view)
        {
            if (!view.Checkpoint()) return false;
            LiterarySpellChecking.ReleaseTree(view);
        }
        IsCharacterKnowledge = false;
        return true;
    }
}
