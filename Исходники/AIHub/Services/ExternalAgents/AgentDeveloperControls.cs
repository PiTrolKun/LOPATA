#if DEBUG
using System.Text.Json;
using System.Windows.Automation;

namespace AIHub.Services.ExternalAgents;

internal static class AgentDeveloperControls
{
    internal static object Run(int processId, string command, JsonElement args)
    {
        var controls = new List<object>();
        var target = command == "dev_control" ? AgentTools.RequiredString(args, "control") : null;
        var owned = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, owned))
        {
            foreach (AutomationElement node in window.FindAll(TreeScope.Subtree, Automation.ControlViewCondition))
            {
                try
                {
                    var current = node.Current;
                    if (current.ProcessId != processId || current.IsOffscreen) continue;
                    var id = string.Join(".", node.GetRuntimeId());
                    if (id == target)
                    {
                        Apply(node, args);
                        return new { control = id, state = "requested" };
                    }
                    if (target is null)
                        controls.Add(new { control = id, automationId = current.AutomationId,
                            name = current.Name, type = current.ControlType.ProgrammaticName, enabled = current.IsEnabled });
                }
                catch (ElementNotAvailableException) { }
            }
        }
        if (target is not null) throw new ArgumentException("Control is no longer visible. Request dev_controls again.");
        return new { processId, controls };
    }

    private static void Apply(AutomationElement node, JsonElement args)
    {
        switch (AgentTools.RequiredString(args, "action"))
        {
            case "invoke": ((InvokePattern)node.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); break;
            case "toggle": ((TogglePattern)node.GetCurrentPattern(TogglePattern.Pattern)).Toggle(); break;
            case "select": ((SelectionItemPattern)node.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); break;
            case "expand": ((ExpandCollapsePattern)node.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); break;
            case "collapse": ((ExpandCollapsePattern)node.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); break;
            case "set_value":
                if (!args.TryGetProperty("value", out var value)) throw new ArgumentException("Missing field: value.");
                ((ValuePattern)node.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value.GetString()!);
                break;
            default: throw new ArgumentException("Unknown control action.");
        }
    }
}
#endif
