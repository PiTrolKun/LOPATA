using System.IO;
using System.Text.Json;

namespace AIHub.Services.MusicScore;

/// <summary>Owned, bounded plan queue documents; injectable directory for isolated checks.</summary>
public sealed class MusicScorePlanStore(string directory)
{
    public string Folder(string id)=>Guid.TryParseExact(id,"N",out _)?Path.Combine(directory,id):throw new InvalidDataException("Music.Score.Invalid");
    public void Save(MusicScorePlanRequest work) {
        MusicScorePlanWork.Validate(work);var folder=Folder(work.Id);Directory.CreateDirectory(folder);
        var temporary=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
        try {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,work);file.Flush(true);}
            File.Move(temporary,Path.Combine(folder,"work.json"),true);
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public MusicScorePlanRequest Load(string id) {
        var path=Path.Combine(Folder(id),"work.json");if(new FileInfo(path).Length>16_000_000)throw new InvalidDataException("Music.Score.TooLarge");
        var work=JsonSerializer.Deserialize<MusicScorePlanRequest>(File.ReadAllText(path))??throw new InvalidDataException("Music.Score.Invalid");
        if(work.Id!=id)throw new InvalidDataException("Music.Score.Invalid");MusicScorePlanWork.Validate(work);return work;
    }
}
