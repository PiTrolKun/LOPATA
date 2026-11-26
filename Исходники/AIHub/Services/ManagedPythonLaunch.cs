using System.Diagnostics;
using System.IO;

namespace AIHub.Services;

/// <summary>Keep isolated imports and Windows DLL loads valid beyond MAX_PATH.</summary>
internal static class ManagedPythonLaunch
{
    internal const string Prelude = "import sys,os\n"
        // The pinned ROCm 7.2.1 distribution provides only the 'custom' package.
        // Disallow external SDK overrides and its stdout-writing offload-arch discovery.
        + "for k in list(os.environ):\n if k.startswith('ROCM_SDK_'): del os.environ[k]\n"
        + "os.environ['ROCM_SDK_TARGET_FAMILY']='custom'\n"
        + "sys.stdout.reconfigure(encoding='utf-8',write_through=True)\n"
        + "sys.stderr.reconfigure(encoding='utf-8',write_through=True)\n"
        + "def _lopata_extended_path(p):\n return p if p.startswith('\\\\\\\\?\\\\') else ('\\\\\\\\?\\\\UNC\\\\'+p[2:] if p.startswith('\\\\\\\\') else '\\\\\\\\?\\\\'+os.path.abspath(p))\n"
        + "sys.path[:]=[_lopata_extended_path(p) for p in sys.path]\n";

    // Workers insert audited source directories after the initial sys.path setup.
    // Normalize at import time so a persisted worker request keeps its original bytes.
    private const string ExtendImports = """
        _lopata_import_root = os.path.normcase(os.path.normpath(os.environ.pop('LOPATA_PYTHON_EXTENDED_IMPORT_ROOT')))
        _lopata_import_hooks = tuple(sys.path_hooks)
        def _lopata_extended_import(path):
            normalized = os.path.normcase(os.path.normpath(path))
            if normalized == _lopata_import_root or normalized.startswith(_lopata_import_root + os.sep):
                for hook in _lopata_import_hooks:
                    try:
                        return hook(_lopata_extended_path(path))
                    except ImportError:
                        continue
            raise ImportError(path)
        sys.path_hooks.insert(0, _lopata_extended_import)
        """ + "\n";

    internal static void ScriptArguments(ProcessStartInfo info, IEnumerable<string> arguments, string? extendedImportRoot = null)
    {
        ManagedWorkStorage.Current.SetWorkerTemporary(info);
        var values = arguments.ToArray();
        var prelude = Prelude;
        if (extendedImportRoot is not null)
        {
            info.Environment["LOPATA_PYTHON_EXTENDED_IMPORT_ROOT"] = Path.GetFullPath(extendedImportRoot);
            prelude += ExtendImports;
        }
        if (values.Length >= 2 && values[0] == "-c")
        {
            info.ArgumentList.Add("-I"); info.ArgumentList.Add("-B"); info.ArgumentList.Add("-c");
            info.ArgumentList.Add(prelude + values[1]);
            foreach (var value in values.Skip(2)) info.ArgumentList.Add(value);
            return;
        }
        if (values.Length >= 2 && values[0] == "-m"
            && System.Text.RegularExpressions.Regex.IsMatch(values[1], @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z"))
        {
            info.ArgumentList.Add("-I"); info.ArgumentList.Add("-B"); info.ArgumentList.Add("-c");
            info.ArgumentList.Add(prelude + "import runpy\nmodule=sys.argv[1]; sys.argv=sys.argv[1:]; runpy.run_module(module,run_name='__main__',alter_sys=True)\n");
            foreach (var value in values.Skip(1)) info.ArgumentList.Add(value);
            return;
        }
        if (values.Length == 0 || !Path.IsPathFullyQualified(values[0]))
            throw new ArgumentException("A fully qualified Python script path is required.", nameof(arguments));
        info.ArgumentList.Add("-I"); info.ArgumentList.Add("-B"); info.ArgumentList.Add("-c");
        info.ArgumentList.Add(prelude + "import runpy\nscript=sys.argv[1]; sys.argv=sys.argv[1:]; runpy.run_path(script,run_name='__main__')\n");
        foreach (var argument in values) info.ArgumentList.Add(argument);
    }

    internal static string OrdinaryPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
