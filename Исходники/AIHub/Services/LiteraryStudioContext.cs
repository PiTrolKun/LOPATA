namespace AIHub.Services;

public static class LiteraryStudioContext
{
    public static bool Contains(LiteraryStudioState state, StudioMessage message)
    {
        if (!message.Complete || message.Session != state.Session || !message.InContext) return false;
        if (state.Role == LiteraryChatProfile.Advisor) return true;
        return message.Role switch
        {
            "Task" => message.Text == state.Task,
            "Writer" => message.Text == state.Result && (state.Action != "Continue" || state.ContinueFromChat),
            "User" => state.Action != "Continue" && state.RevisionRequirements.Any(r=>r.Contains(message.Text,StringComparison.Ordinal)),
            _ => false
        };
    }
}
