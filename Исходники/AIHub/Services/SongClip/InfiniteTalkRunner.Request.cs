using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.SongClip;

internal static partial class InfiniteTalkRunner
{
    internal static JsonObject Request(SongClipAttempt attempt,SongClipStore store,string job,InfiniteTalkEnvironment env)
    {
        var s=attempt.Settings.InfiniteTalk??new();
        var models=new (string Category,string Name)[] {
            ("diffusion_models","wan2.1-i2v-14b-480p-Q4_K_M.gguf"),
            ("diffusion_models","Wan2_1-InfiniteTalk_Single_Q4_K_M.gguf"),
            ("text_encoders","umt5_xxl_fp8_e4m3fn_scaled.safetensors"),
            ("vae","wan_2.1_vae.safetensors"), ("clip_vision","clip_vision_h.safetensors"),
            ("wav2vec2","wav2vec2-chinese-base_fp16.safetensors")};
        var files=SongClipVariantCatalog.DescribeFiles(env.Cards);
        string DirectoryFor(string name)=>Path.GetDirectoryName(files.Single(f=>Path.GetFileName(f.Path)==name).Path)!;
        var folders=models.GroupBy(m=>m.Category).ToDictionary(g=>g.Key,g=>g.Select(m=>DirectoryFor(m.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        return JsonSerializer.SerializeToNode(new {
            job,operationId=attempt.Id,programVersion=ApplicationBuildVersion.Value,modelId=ModelId,sourceRevision=InfiniteTalkComponents.Revision,
            libraries=env.Libraries,comfy=env.Comfy,wrapper=env.Wrapper,extras=env.Extras,device=env.Python.Device,
            song=store.Resolve(attempt.ProjectId,attempt.Song),reference=store.Resolve(attempt.ProjectId,SongClipReferences.For(attempt).Single().File),
            referenceRole=SongClipReferences.For(attempt).Single().Description,
            width=s.Width,height=s.Height,steps=s.Steps,frameWindow=s.FrameWindow,motionFrames=s.MotionFrames,
            audioCfg=s.AudioCfg,vramReserveGb=s.VramReserveGb,seed=attempt.Settings.Seed,
            outputWidth=attempt.Settings.Width,outputHeight=attempt.Settings.Height,quality=attempt.Settings.Quality,
            ffmpeg=Path.Combine(VideoRuntime.DirectoryPath,"ffmpeg.exe"),ffprobe=Path.Combine(VideoRuntime.DirectoryPath,"ffprobe.exe"),
            model=models[0].Name,adapter=models[1].Name,encoder=models[2].Name,vae=models[3].Name,vision=models[4].Name,wav2vec=models[5].Name,
            modelFolders=folders,pins=env.Cards.ToDictionary(c=>c.ModelArtifactId,c=>c.Revision)
        })!.AsObject();
    }
}
