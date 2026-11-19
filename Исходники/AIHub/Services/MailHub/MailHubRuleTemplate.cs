namespace AIHub.Services.MailHub;

internal static class MailHubRuleTemplate
{
    internal static string[] Sections(Func<string, string> localize)
    {
        var template = localize("MailHub.PolicyText");
        var sections = template.Split(["[[topics]]", "[[style]]"], StringSplitOptions.TrimEntries);
        return sections.Length == 3 ? sections : [template, "", ""];
    }

    internal static string ProtectedText(Func<string, string> localize) =>
        string.Join(" ", Sections(localize).Where(section => section.Length > 0));
}
