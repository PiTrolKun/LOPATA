using System.IO;
using System.Text.Json;
using AIHub.Models;
using SkiaSharp;

namespace AIHub.Services;

internal static class FileMetadataPreview
{
    internal static void ValidateImage(byte[] bytes)
    {
        if (bytes.Length is < 8 or > 16*1024*1024) throw new InvalidDataException("Metadata.InvalidImage");
        using var codec=SKCodec.Create(new SKMemoryStream(bytes));
        if(codec is null || codec.Info.Width is <1 or >32768 || codec.Info.Height is <1 or >32768
            || (long)codec.Info.Width*codec.Info.Height>64*1024*1024) throw new InvalidDataException("Metadata.InvalidImage");
        var dimensions=codec.GetScaledDimensions(Math.Min(1,256f/Math.Max(codec.Info.Width,codec.Info.Height)));
        using var check=new SKBitmap(new SKImageInfo(dimensions.Width,dimensions.Height,SKColorType.Bgra8888,SKAlphaType.Premul));
        if(codec.GetPixels(check.Info,check.GetPixels())!=SKCodecResult.Success)throw new InvalidDataException("Metadata.InvalidImage");
    }
    internal static byte[] Thumbnail(byte[] bytes)
    {
        ValidateImage(bytes);
        using var codec=SKCodec.Create(new SKMemoryStream(bytes))!;
        var dimensions=codec.GetScaledDimensions(Math.Min(1,512f/Math.Max(codec.Info.Width,codec.Info.Height)));
        using var bitmap=new SKBitmap(new SKImageInfo(dimensions.Width,dimensions.Height,SKColorType.Bgra8888,SKAlphaType.Premul));
        var result=codec.GetPixels(bitmap.Info,bitmap.GetPixels());
        if(result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))throw new InvalidDataException("Metadata.PreviewUnavailable");
        var origin=codec.EncodedOrigin;
        using var surface=SKSurface.Create(new SKImageInfo(origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom ? bitmap.Height:bitmap.Width,
            origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom ? bitmap.Width:bitmap.Height));
        var c=surface.Canvas;
        switch(origin)
        {
            case SKEncodedOrigin.TopRight:c.Translate(bitmap.Width,0);c.Scale(-1,1);break;
            case SKEncodedOrigin.BottomRight:c.Translate(bitmap.Width,bitmap.Height);c.RotateDegrees(180);break;
            case SKEncodedOrigin.BottomLeft:c.Translate(0,bitmap.Height);c.Scale(1,-1);break;
            case SKEncodedOrigin.LeftTop:c.RotateDegrees(90);c.Scale(1,-1);break;
            case SKEncodedOrigin.RightTop:c.Translate(bitmap.Height,0);c.RotateDegrees(90);break;
            case SKEncodedOrigin.RightBottom:c.Translate(bitmap.Height,bitmap.Width);c.RotateDegrees(90);c.Scale(-1,1);break;
            case SKEncodedOrigin.LeftBottom:c.Translate(0,bitmap.Width);c.RotateDegrees(-90);break;
        }
        c.DrawBitmap(bitmap,0,0);using var image=surface.Snapshot();return image.Encode(SKEncodedImageFormat.Png,90).ToArray();
    }
    internal static async Task<byte[]?> ReadAsync(FileMetadataSnapshot file,CancellationToken token)
    {
        if(file.Kind=="image")
        {
            if(file.Length>16*1024*1024)return null;
            return await Task.Run(async()=>Thumbnail(await File.ReadAllBytesAsync(file.Path,token)),token);
        }
        if(file.Kind!="video")return null;
        await ComponentLicenseGate.EnsureAsync("capture.ffmpeg",token);
        var root=Path.Combine(AppContext.BaseDirectory,"CaptureRuntime");
        var bytes=await FileMetadataProcess.RunAsync(Path.Combine(root,"ffprobe.exe"),
            ["-v","error","-select_streams","v:0","-show_streams","-of","json",file.Path],token,maximumBytes:1024*1024);
        using var json=JsonDocument.Parse(bytes);var streams=json.RootElement.GetProperty("streams");
        if(streams.GetArrayLength()==0)return null;
        var stream=streams[0];var width=stream.GetProperty("width").GetInt32();var height=stream.GetProperty("height").GetInt32();
        if(width<=0||height<=0||width>32768||height>32768)throw new InvalidDataException("Metadata.PreviewUnavailable");
        var scale=256d/Math.Max(width,height);var w=Math.Max(2,(int)(width*scale)/2*2);var h=Math.Max(2,(int)(height*scale)/2*2);
        var raw=await FileMetadataProcess.RunAsync(Path.Combine(root,"ffmpeg.exe"),
            ["-nostdin","-v","error","-threads","1","-noautorotate","-i",file.Path,"-map","0:v:0","-an","-vf",$"scale={w}:{h}",
             "-frames:v","3","-c:v","rawvideo","-pix_fmt","bgra","-f","rawvideo","pipe:1"],token,maximumBytes:3*w*h*4);
        var size=w*h*4;var count=raw.Length/size;if(count==0)return null;
        using var bitmap=new SKBitmap(w,h,SKColorType.Bgra8888,SKAlphaType.Opaque);
        System.Runtime.InteropServices.Marshal.Copy(raw,count>=3?2*size:0,bitmap.GetPixels(),size);
        var rotation=0;
        if(stream.TryGetProperty("side_data_list",out var sides))foreach(var side in sides.EnumerateArray())
            if(side.TryGetProperty("rotation",out var value))rotation=-(int)value.GetDouble();
        var swap=Math.Abs(rotation)%180==90;
        using var surface=SKSurface.Create(new SKImageInfo(swap?h:w,swap?w:h));var canvas=surface.Canvas;
        canvas.Translate((swap?h:w)/2f,(swap?w:h)/2f);canvas.RotateDegrees(rotation);canvas.DrawBitmap(bitmap,-w/2f,-h/2f);
        using var image=surface.Snapshot();return image.Encode(SKEncodedImageFormat.Png,90).ToArray();
    }
}
