param([string]$DownloadsDirectory, [string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$DownloadsDirectory){$DownloadsDirectory=Join-Path $root 'Runtime/Downloads/Metadata'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'Runtime/Metadata/ExifTool-13.59'}
$expected='44B512B25AF500724BA579D0A53C8FC5851628B692DD5E5D94AE4A15C2CBA9EC'
$url='https://downloads.sourceforge.net/project/exiftool/exiftool-13.59_64.zip'
New-Item -ItemType Directory -Path $DownloadsDirectory -Force|Out-Null
$zip=Join-Path $DownloadsDirectory 'exiftool-13.59_64.zip'
if(!(Test-Path -LiteralPath $zip)){Invoke-WebRequest -Uri $url -OutFile $zip}
if((Get-FileHash -LiteralPath $zip).Hash -ne $expected){throw 'ExifTool archive checksum mismatch'}
$bundle=Join-Path $OutputDirectory 'exiftool-13.59_64'
if(!(Test-Path -LiteralPath $bundle)){
 New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
 Expand-Archive -LiteralPath $zip -DestinationPath $OutputDirectory
 Move-Item -LiteralPath (Join-Path $bundle 'exiftool(-k).exe') -Destination (Join-Path $bundle 'exiftool.exe')
}
$manifest=Join-Path $root 'Исходники/AIHub/Tools/metadata-exiftool-files.json'
$expectedFiles=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
$files=@(Get-ChildItem -LiteralPath $bundle -File -Recurse)
if($files.Count -ne $expectedFiles.Count){throw 'Unexpected ExifTool runtime files'}
foreach($file in $expectedFiles){
 $path=[IO.Path]::GetFullPath((Join-Path $bundle $file.Path))
 if(!$path.StartsWith([IO.Path]::GetFullPath($bundle)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe manifest path'}
 $item=Get-Item -LiteralPath $path
 if($item.Length -ne $file.Size -or (Get-FileHash -LiteralPath $path).Hash -ne $file.Sha256){throw ('Runtime differs: '+$file.Path)}
}
Write-Output 'Verified ExifTool 13.59 x64 bundle. No system installation or PATH changes.'
