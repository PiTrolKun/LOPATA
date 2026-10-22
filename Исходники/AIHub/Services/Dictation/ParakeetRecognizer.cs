using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace AIHub.Services.Dictation;

/// <summary>Official sherpa API/configuration. UTF-8 config pointers also support non-ASCII Windows model paths.</summary>
public sealed class ParakeetRecognizer : IDisposable
{
    private nint _recognizer;
    private static nint _library;
    private static readonly object LibraryGate=new();
    public ParakeetRecognizer(string[] files,int threads)
    {
        lock(LibraryGate) {
            if(_library==0) {
                _library=LoadLibraryEx(Path.Combine(DictationComponents.RuntimeDirectory,"sherpa-onnx-c-api.dll"),0,0x1100);
                if(_library==0)throw new System.ComponentModel.Win32Exception();
                NativeLibrary.SetDllImportResolver(typeof(ParakeetRecognizer).Assembly,(name,_,_)=>name=="lopata-parakeet"?_library:0);
                NativeLibrary.SetDllImportResolver(typeof(OfflineStream).Assembly,(_,_,_)=>_library);
            }
        }
        var config=new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate=16000;config.FeatConfig.FeatureDim=128;
        config.ModelConfig.Transducer.Encoder=files[0];config.ModelConfig.Transducer.Decoder=files[1];config.ModelConfig.Transducer.Joiner=files[2];
        config.ModelConfig.Tokens=files[3];config.ModelConfig.NumThreads=threads;config.ModelConfig.Provider="cpu";config.ModelConfig.ModelType="nemo_transducer";
        using var utf8=new Utf8Config(config);
        _recognizer=Create(utf8.Pointer);
        if(_recognizer==0)throw new InvalidDataException("Parakeet recognizer initialization failed.");
    }
    public string Decode(float[] samples)
    {
        ObjectDisposedException.ThrowIf(_recognizer==0,this);
        using var stream=new OfflineStream(CreateStream(_recognizer));
        stream.AcceptWaveform(16000,samples);DecodeStream(_recognizer,stream.Handle);
        return stream.Result.Text;
    }
    public void Dispose() { if(_recognizer!=0){Destroy(_recognizer);_recognizer=0;} }

    // The upstream managed config uses LPStr (Windows ACP). Only marshaling changes here;
    // native structs, feature extraction and inference stay the official implementation.
    private sealed class Utf8Config : IDisposable
    {
        private readonly List<nint> _strings=[];
        public nint Pointer { get; }
        public Utf8Config(OfflineRecognizerConfig config) {
            var size=Marshal.SizeOf<OfflineRecognizerConfig>();Pointer=Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size],0,Pointer,size);
            try{Write(config,typeof(OfflineRecognizerConfig),Pointer);}catch{Dispose();throw;}
        }
        private void Write(object value,Type type,nint pointer) {
            foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.Public)) {
                var at=pointer+(int)Marshal.OffsetOf(type,field.Name);var item=field.GetValue(value);var kind=field.FieldType;
                if(kind==typeof(string)){var text=Marshal.StringToCoTaskMemUTF8((string?)item??"");_strings.Add(text);Marshal.WriteIntPtr(at,text);}
                else if(kind==typeof(int))Marshal.WriteInt32(at,(int)item!);
                else if(kind==typeof(float))Marshal.WriteInt32(at,BitConverter.SingleToInt32Bits((float)item!));
                else if(kind==typeof(double))Marshal.WriteInt64(at,BitConverter.DoubleToInt64Bits((double)item!));
                else if(kind.IsValueType&&item is not null)Write(item,kind,at);
                else throw new NotSupportedException("Unsupported sherpa config field: "+field.Name);
            }
        }
        public void Dispose(){foreach(var s in _strings)Marshal.FreeCoTaskMem(s);Marshal.FreeHGlobal(Pointer);}
    }
    [DllImport("kernel32.dll",EntryPoint="LoadLibraryExW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint LoadLibraryEx(string path,nint file,uint flags);
    [DllImport("lopata-parakeet",EntryPoint="SherpaOnnxCreateOfflineRecognizer")] private static extern nint Create(nint config);
    [DllImport("lopata-parakeet",EntryPoint="SherpaOnnxDestroyOfflineRecognizer")] private static extern void Destroy(nint recognizer);
    [DllImport("lopata-parakeet",EntryPoint="SherpaOnnxCreateOfflineStream")] private static extern nint CreateStream(nint recognizer);
    [DllImport("lopata-parakeet",EntryPoint="SherpaOnnxDecodeOfflineStream")] private static extern void DecodeStream(nint recognizer,nint stream);
}
