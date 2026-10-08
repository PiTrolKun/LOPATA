param([string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
$folder=Join-Path $Root 'Runtime/MusicAce/ca1e85fe9430179831e6bc6be790c332190a3866'
$manifest=Get-Content (Join-Path $folder 'manifest.json') -Raw|ConvertFrom-Json
$files=@($manifest.Files.PSObject.Properties.Name)+@('manifest.json')
if(@(Get-ChildItem $folder -Recurse -File).Count -ne $files.Count){throw 'Unexpected official source file'}
foreach($file in $manifest.Files.PSObject.Properties){
 if((Get-FileHash -LiteralPath (Join-Path $folder $file.Name)).Hash -ne $file.Value){throw ('Source hash mismatch: '+$file.Name)}
}
$output=Join-Path $Root 'Runtime/MusicAce/source.zip'
if(Test-Path -LiteralPath $output){throw 'Archive already exists; preserve it before rebuilding'}
$stream=[IO.File]::Create($output)
try{
 $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
 try{
  foreach($file in $files|Sort-Object -CaseSensitive){
   $entry=$zip.CreateEntry($file,[IO.Compression.CompressionLevel]::Optimal)
   $entry.LastWriteTime=[DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
   $input=[IO.File]::OpenRead((Join-Path $folder $file)); $target=$entry.Open()
   try{$input.CopyTo($target)}finally{$input.Dispose();$target.Dispose()}
  }
 }finally{$zip.Dispose()}
}finally{$stream.Dispose()}
Get-Item -LiteralPath $output|Select-Object Name,Length
Get-FileHash -LiteralPath $output
