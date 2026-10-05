param([string]$Directory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$Directory) { $Directory = Join-Path $root 'Runtime/Backends/yue2.cpp/11c1ecb084329200e22fcb286e252b847442ea5c' }
foreach ($pack in @('win-x64','win-cuda128-x64')) {
    $folder = Join-Path $Directory $pack
    $manifest = Get-Content -LiteralPath (Join-Path $folder 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.SourceRevision -ne '11c1ecb084329200e22fcb286e252b847442ea5c' -or
        $manifest.GgmlRevision -ne '40e16e4a814f7fe851a0c486fb9e8c722e957830') { throw 'Unexpected YuE2 source revision.' }
    $expected = @('yue-plan.exe','yue-synth.exe','ggml.dll','ggml-base.dll','ggml-cpu.dll')
    if ($pack -eq 'win-cuda128-x64') { $expected += @('ggml-cuda.dll','cudart64_12.dll','cublas64_12.dll','cublasLt64_12.dll') }
    if (Compare-Object $expected @($manifest.Files.Name)) { throw 'Unexpected native manifest file set.' }
    foreach ($file in $manifest.Files) {
        if ((Get-FileHash -LiteralPath (Join-Path $folder $file.Name)).Hash -ne $file.Sha256) { throw "Native file changed: $pack/$($file.Name)" }
    }
    $crt = @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')
    foreach ($name in $crt) {
        $file = Join-Path $folder $name
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw "Unverified Microsoft runtime: $file" }
    }
    $licenses = @('LICENSE-yue2.txt','LICENSE-ggml.txt','LICENSE-yyjson.txt','LICENSE-MSVC.txt')
    if ($pack -eq 'win-cuda128-x64') { $licenses += 'LICENSE-CUDA.txt' }
    foreach ($name in $licenses) { if (!(Test-Path -LiteralPath (Join-Path $folder $name))) { throw "Missing license: $pack/$name" } }
    $allowed = @($expected) + $crt + $licenses + @('manifest.json','windows-utf8.manifest')
    if (Get-ChildItem -LiteralPath $folder -File | Where-Object Name -NotIn $allowed) { throw "Unexpected runtime payload in $pack" }
}
Write-Host 'Pinned YuE2 CPU/CUDA files, Microsoft signatures and licenses verified.'
