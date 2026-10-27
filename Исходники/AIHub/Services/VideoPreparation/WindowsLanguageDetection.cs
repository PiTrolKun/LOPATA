using System.Runtime.InteropServices;
using System.Text;

namespace AIHub.Services.VideoPreparation;

/// <summary>Local Windows ELS service; only language recognition, no downloads or model runtime.</summary>
internal static class WindowsLanguageDetection
{
    public static string[] Recognize(string text)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var guid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        nint services = 0;
        var bag = new PropertyBag { Size = (nuint)Marshal.SizeOf<PropertyBag>() };
        var recognized = false;
        try
        {
            // ELS_GUID_LANGUAGE_DETECTION, documented by Microsoft in elssrvc.h.
            Marshal.StructureToPtr(new Guid("CF7E00B1-909B-4D95-A8F4-611F7C377702"), guid, false);
            var options = new EnumOptions { Size = (nuint)Marshal.SizeOf<EnumOptions>(), Guid = guid };
            if (MappingGetServices(ref options, out services, out var count) < 0 || count == 0 || services == 0) return [];
            text = text.Normalize(NormalizationForm.FormC);
            if (MappingRecognizeText(services, text, checked((uint)text.Length), 0, 0, ref bag) < 0) return [];
            recognized = true;
            if (bag.Count != 1 || bag.Ranges == 0) return [];
            var range = Marshal.PtrToStructure<DataRange>(bag.Ranges);
            if (range.Data == 0 || range.DataSize is < 4 or > 65536 || range.DataSize % 2 != 0) return [];
            var languages = Marshal.PtrToStringUni(range.Data, checked((int)range.DataSize / 2));
            return languages?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ArgumentException)
        { return []; }
        finally
        {
            if (recognized) _ = MappingFreePropertyBag(ref bag);
            if (services != 0) _ = MappingFreeServices(services);
            Marshal.FreeHGlobal(guid);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumOptions
    {
        public nuint Size;
        public nint Category, InputLanguage, OutputLanguage, InputScript, OutputScript, InputContent, OutputContent, Guid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyBag
    {
        public nuint Size;
        public nint Ranges;
        public uint Count;
        public nint ServiceData;
        public uint ServiceDataSize;
        public nint CallerData;
        public uint CallerDataSize;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataRange
    {
        public uint Start, End;
        public nint Description;
        public uint DescriptionLength;
        public nint Data;
        public uint DataSize;
        public nint ContentType, ActionIds;
        public uint ActionCount;
        public nint ActionDisplayNames;
    }

    [DllImport("elscore.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MappingGetServices(ref EnumOptions options, out nint services, out uint count);
    [DllImport("elscore.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MappingRecognizeText(nint service, string text, uint length, uint index, nint options, ref PropertyBag bag);
    [DllImport("elscore.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MappingFreePropertyBag(ref PropertyBag bag);
    [DllImport("elscore.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MappingFreeServices(nint services);
}
