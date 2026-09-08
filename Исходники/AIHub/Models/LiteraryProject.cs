namespace AIHub.Models;

public sealed class LiteraryProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ProjectName { get; set; } = "";
    public string WorkTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string LanguageCode { get; set; } = "ru";
    public string Form { get; set; } = "free";
    public List<string> Genres { get; set; } = [];
    public string CustomGenres { get; set; } = "";
    public string Premise { get; set; } = "";
    public string Include { get; set; } = "";
    public string Avoid { get; set; } = "";
    public bool BasedOnExistingWorld { get; set; }
    public string WorldSource { get; set; } = "";
    public List<string> Materials { get; set; } = [];
    public List<string> CultureCountries { get; set; } = [];
    public string CultureNotes { get; set; } = "";
}

public sealed class LiteraryProjectIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveId { get; set; }
    public List<LiteraryProjectEntry> Projects { get; set; } = [];
}
