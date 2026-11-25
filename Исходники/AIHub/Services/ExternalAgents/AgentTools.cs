using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.ExternalAgents;

internal static class AgentTools
{
    private sealed record Field(string Type, string Description);
    private sealed record Schema(string Type, Dictionary<string, Field> Properties, string[] Required, bool AdditionalProperties = false);
    private sealed record Tool(string Name, string Description, Schema InputSchema);

    private static Tool Define(string name, string description, string[] required, params (string Name, string Type, string Description)[] fields) =>
        new(name, description, new("object", fields.ToDictionary(f => f.Name, f => new Field(f.Type, f.Description)), required));

    private static readonly Tool[] Tools =
    [
        Define("lopata_info", "Identify the connected LOPATA build, installation, and agent executable.", []),
        Define("project_scenarios", "List the scenarios that can save and reopen native projects.", []),
        Define("project_instructions", "Get the selected scenario's accepted project fields and instructions.", ["scenario"],
            ("scenario", "string", "ID returned by project_scenarios.")),
        Define("project_create", "Create a new native project with the selected scenario's validated data.", ["scenario","data"],
            ("scenario","string","ID from project_scenarios."), ("data","object","Project content as specified by project_instructions.")),
        Define("logs_search", "Find technical events in saved LOPATA logs, including when the desktop application is closed.", [],
            ("source","string",string.Join(", ", AgentDiagnostics.Sources)),
            ("operation","string","Technical event, e.g. component_install_failed."), ("component","string","Component identifier."),
            ("scenario","string","Scenario identifier."), ("session","string","Session identifier returned by this tool."),
            ("from","string","Start time with timezone in ISO 8601."), ("until","string","End time with timezone in ISO 8601."),
            ("includeInfo","boolean","Include ordinary events and successful attempts."), ("limit","integer","Maximum results, 1 to 50.")),
        Define("logs_read", "Read a selected log event and related technical context.", ["eventId"], ("eventId","string","Identifier from logs_search.")),
        EnvironmentTool("environment_check", "Check prerequisites for a project or a selected scenario operation."),
        EnvironmentTool("environment_prepare", "Acquire only the selected operation's prerequisites through LOPATA. Poll environment_status."),
        EnvironmentTool("environment_status", "Read the state of the selected preparation."),
        EnvironmentTool("environment_cancel", "Cancel the selected active component preparation."),
#if DEBUG
        Define("dev_navigation", "Inspect the live application navigation catalogue and mechanic tags, including non-project scenarios.", []),
        Define("dev_open_scenario", "Exercise the normal desktop entry of a development scenario, including scenarios without projects.",
            ["scenario"], ("scenario","string","Available scenario ID returned by dev_navigation.")),
        Define("dev_video_readiness", "Run the native video readiness check on a development project document.", ["project"],
            ("project","object","Serialized VideoPreparationProject.")),
        Define("dev_controls", "Read the visible dev windows and their standard Windows accessibility controls.", []),
        Define("dev_control", "Exercise a control in the development copy. Use a control ID from a fresh dev_controls response.",
            ["control","action"], ("control","string","Runtime ID returned by dev_controls."),
            ("action","string","invoke, toggle, select, expand, collapse, or set_value."),
            ("value","string","New value for set_value.")),
#endif
    ];

    private static Tool EnvironmentTool(string name, string description) =>
        Define(name, description, ["scenario"], ("scenario","string","ID from project_scenarios."),
            ("operation","string","Defaults to project (no models). Model operations are listed in lopata_info."));

    internal static object List() => Tools;

    private static readonly Lazy<string> CapabilitiesRevision = new(() => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { tools = Tools, projects = AgentProjects.Contracts(),
            operations = AgentModelPlan.Operations, mechanics = ScenarioNavigationCatalog.CloudTags }, AgentProtocol.Json))));

    internal static async Task<object> CallAsync(string name, JsonElement args, AgentIdentity? client = null)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException("Unknown tool: " + name);
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object for arguments.");
        foreach (var field in args.EnumerateObject())
        {
            if (!tool.InputSchema.Properties.TryGetValue(field.Name, out var definition))
                throw new ArgumentException("Unknown field: " + field.Name);
            var valid = definition.Type switch
            {
                "string" => field.Value.ValueKind == JsonValueKind.String,
                "integer" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out _),
                "boolean" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => field.Value.ValueKind == JsonValueKind.Object,
                _ => false
            };
            if (!valid) throw new ArgumentException($"Expected {definition.Type} for {field.Name}.");
        }
        foreach (var key in tool.InputSchema.Required)
            if (!args.TryGetProperty(key, out _)) throw new ArgumentException("Missing field: " + key);

        using var activity = client == null ? null : AgentPresenceStore.Current.Begin(client, name);
        switch (name)
        {
            case "lopata_info":
                return new { version = ApplicationBuildVersion.Value, executable = Environment.ProcessPath, directory = AppContext.BaseDirectory,
                    developmentTools = Tools.Any(t => t.Name.StartsWith("dev_", StringComparison.Ordinal)),
                    capabilitiesRevision = CapabilitiesRevision.Value,
                    modelOperations = AgentModelPlan.Operations,
                    mechanics = ScenarioNavigationCatalog.CloudTags.Select(tag => new { tag.Id, scenario = tag.TargetId }) };
            case "project_scenarios": return AgentProjects.List();
            case "project_instructions": return AgentProjects.Describe(RequiredString(args, "scenario"));
            case "project_create": return AgentProjects.Create(RequiredString(args, "scenario"), args.GetProperty("data"));
            case "logs_search": return AgentDiagnostics.Search(args);
            case "logs_read": return AgentDiagnostics.Read(args);
#if DEBUG
            case "dev_navigation": return new { scenarios = ScenarioNavigationCatalog.Nodes, tags = ScenarioNavigationCatalog.CloudTags };
            case "dev_open_scenario":
                return await AgentEnvironmentConnection.RequestAsync(name, RequiredString(args, "scenario"), "");
            case "dev_controls":
            case "dev_control":
                var host = (JsonElement)await AgentEnvironmentConnection.RequestAsync("dev_desktop", "", "");
                return AgentDeveloperControls.Run(host.GetProperty("processId").GetInt32(), name, args);
            case "dev_video_readiness":
                var project = args.GetProperty("project").Deserialize<VideoPreparationProject>(AgentProtocol.Json)
                    ?? throw new InvalidDataException("Missing project.");
                project.Validate();
                return new { issues = VideoPreparationReadiness.Check(project) };
#endif
            default:
                var scenario = RequiredString(args, "scenario");
                var operation = args.TryGetProperty("operation", out _) ? RequiredString(args,"operation") : "project";
                if (operation == "project")
                {
                    _ = AgentProjects.Describe(scenario);
                    return new { scenario, operation, state = "ready", requiresModels = false };
                }
                AgentModelPlan.Validate(scenario, operation);
                return await AgentEnvironmentConnection.RequestAsync(name, scenario, operation, client);
        }
    }

    internal static string RequiredString(JsonElement args, string key)
    {
        if (args.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(field.GetString())) return field.GetString()!;
        throw new ArgumentException("Expected a non-empty string: " + key);
    }
}
