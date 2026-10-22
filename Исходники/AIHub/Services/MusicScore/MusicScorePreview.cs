using System.IO;
using AIHub.Models;

namespace AIHub.Services.MusicScore;

/// <summary>Audition of parsed sounding notes; never a claim about generated vocals.</summary>
public static class MusicScorePreview
{
    public static void Write(string path,MusicScoreSheet sheet,CancellationToken token) {
        if(sheet.Seconds is <=0 or >360 || sheet.Notes.Vocal.Length+sheet.Notes.Ins.Length>40000)
            throw new InvalidDataException("Music.Score.Invalid");
        const int rate=48000; var frames=checked((int)Math.Ceiling(sheet.Seconds*rate));
        using var output=new BinaryWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write));
        output.Write(0x46464952u);output.Write((uint)(frames*4+36));output.Write(0x45564157u);
        output.Write(0x20746d66u);output.Write(16u);output.Write((ushort)1);output.Write((ushort)2);
        output.Write(rate);output.Write(rate*4);output.Write((ushort)4);output.Write((ushort)16);
        output.Write(0x61746164u);output.Write(frames*4);
        var voices=new[]{sheet.Notes.Vocal.OrderBy(n=>n.Start).ToArray(),sheet.Notes.Ins.OrderBy(n=>n.Start).ToArray()};
        var indices=new int[2];
        for(var frame=0;frame<frames;frame++) {
            if((frame&16383)==0) token.ThrowIfCancellationRequested();
            var seconds=frame/(double)rate;var tick=seconds*sheet.Bpm/60*sheet.PerQuarter;
            double mixed=0;
            for(var voice=0;voice<2;voice++) {
                var notes=voices[voice];while(indices[voice]<notes.Length&&tick>=notes[indices[voice]].Start+notes[indices[voice]].Length)indices[voice]++;
                if(indices[voice]>=notes.Length)continue;
                var note=notes[indices[voice]];if(tick<note.Start)continue;
                var age=(tick-note.Start)/sheet.PerQuarter*60/sheet.Bpm;
                var remaining=(note.Start+note.Length-tick)/sheet.PerQuarter*60/sheet.Bpm;
                mixed+=4000*Math.Min(1,age/.01)*Math.Min(1,remaining/.025)*Math.Sin(2*Math.PI*440*Math.Pow(2,(note.Pitch-69)/12d)*age);
            }
            output.Write((short)mixed);output.Write((short)mixed);
        }
    }
}
