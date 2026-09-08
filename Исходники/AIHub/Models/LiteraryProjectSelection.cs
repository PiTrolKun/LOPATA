namespace AIHub.Models;

// Navigation metadata only; the manuscript format is intentionally not defined here.
public sealed record LiteraryProjectEntry(string Id, string Title, string ProjectPath = "");

public sealed class LiteraryProjectSelection
{
    private readonly List<LiteraryProjectEntry> _projects = [];
    public IReadOnlyList<LiteraryProjectEntry> Projects => _projects.AsReadOnly();
    public string? ActiveId { get; private set; }
    public LiteraryProjectEntry? ActiveProject => _projects.Find(p => p.Id == ActiveId);

    public void SetProjects(IEnumerable<LiteraryProjectEntry> projects)
    {
        var items = projects.ToList();
        if (items.Any(p => string.IsNullOrWhiteSpace(p.Id)) || items.Select(p => p.Id).Distinct().Count() != items.Count)
            throw new ArgumentException("Project identifiers must be nonempty and unique.", nameof(projects));
        _projects.Clear();
        _projects.AddRange(items);
        if (!_projects.Any(p => p.Id == ActiveId)) ActiveId = null;
    }

    public void SetActive(string id)
    {
        if (!_projects.Any(p => p.Id == id)) throw new ArgumentException("Unknown project.", nameof(id));
        ActiveId = id;
    }
}
