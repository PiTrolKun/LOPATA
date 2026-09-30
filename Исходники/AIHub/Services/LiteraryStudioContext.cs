namespace AIHub.Services;

public static class LiteraryStudioContext
{
    public static bool Contains(LiteraryStudioState state, StudioMessage message)
    {
        if (!message.Complete || message.Session != state.Session || !message.InContext || message.Role == "ContextEvent") return false;
        if (state.Role == LiteraryChatProfile.Advisor) return true;
        var writer = StudioContextPlan.Writer(state);
        return message.Role switch
        {
            "Task" => message.Text == writer.Task,
            "Writer" => message.Text == writer.Target && (state.Action != "Continue" || state.ContinueFromChat),
            "User" => state.Action != "Continue" && writer.Requirements.Any(r=>r.Contains(message.Text,StringComparison.Ordinal)),
            _ => false
        };
    }
}
