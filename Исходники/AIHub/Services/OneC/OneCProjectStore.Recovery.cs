using AIHub.Models.OneC;

namespace AIHub.Services.OneC;

internal sealed partial class OneCProjectStore
{
    internal OneCProject RecoverDrafts(string id, string label)
    {
        lock (Gate(id))
        {
        var project = Load(id);
        var drafts = project.Files.Where(f => f.RecoveryCode.Length > 0).ToArray();
        if (drafts.Length == 0) return project;
        foreach (var file in drafts)
        {
            var recovered = new OneCFile { Name = file.Name, Reference = label + " · " + file.Name,
                Code = file.RecoveryCode, SavedCodeHash = Hash(file.RecoveryCode) };
            file.RecoveryCode = ""; project.Files.Add(recovered); project.OpenFileIds.Add(recovered.Id); project.ActiveFileId = recovered.Id;
            project.History.Add(new(OneCIds.New(), "recoveredConflictDraft", recovered.Id, DateTimeOffset.UtcNow));
        }
        Save(project); return project;
        }
    }
}
