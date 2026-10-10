$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scratch = Join-Path $root ('_tmp/mini-publish-mock-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Инструменты/publish-file-update.ps1') -Destination $scratch
# Test-only verified-bundle adapter. Crypto and ZIP validation are covered by C# tests;
# every GitHub call below is intercepted, so this fixture cannot publish anything.
@'
param($ManifestPath,$Version,$SourceCommit)
[pscustomobject]@{Manifest=[pscustomobject]@{notes=@([pscustomobject]@{version=$Version;text='Mock mini publication'});files=@('Updater/LOPATA.Updater.exe','Licenses/installer.txt','Licenses/installer-receipt.json','Installer/bootstrap-v1.txt')|ForEach-Object{[pscustomobject]@{root='app';path=$_}}};Assets=@($ManifestPath);Reused=@()}
'@ | Set-Content -LiteralPath (Join-Path $scratch 'get-update-bundle.ps1') -Encoding utf8
'mock signed adapter input' | Set-Content -LiteralPath (Join-Path $scratch 'lopata-files.json') -Encoding utf8
$commit = 'a' * 40
@{schemaVersion=1;version='0.5.1-beta';sourceCommit=$commit;sourceDirty=$false;standBuild=$false;
  manifestSha256=(Get-FileHash -LiteralPath (Join-Path $scratch 'lopata-files.json')).Hash.ToLowerInvariant()} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'file-update.build.json') -Encoding utf8
$mini = Join-Path $scratch 'mini'; New-Item -ItemType Directory -Path $mini | Out-Null
'mock exe' | Set-Content -LiteralPath (Join-Path $mini 'LOPATA_Setup.exe') -Encoding ascii
$miniReceipt=@{schemaVersion=1;bootstrapProtocol=1;standBuild=$false;sourceDirty=$false;sourceCommit=$commit;
  fileName='LOPATA_Setup.exe';size=(Get-Item -LiteralPath (Join-Path $mini 'LOPATA_Setup.exe')).Length;
  sha256=(Get-FileHash -LiteralPath (Join-Path $mini 'LOPATA_Setup.exe')).Hash.ToLowerInvariant()}
$miniReceipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mini 'mini-installer.build.json') -Encoding utf8
$global:MiniMockCalls=[Collections.Generic.List[string]]::new()
$global:MiniMockAssets=[Collections.Generic.List[object]]::new()
function global:gh {
 $global:LASTEXITCODE=0; $line=$args -join ' ';$global:MiniMockCalls.Add($line)
 if($line -match '^api repos/.+/releases\?'){return '[]'}
 if($line -match '^api repos/.+/commits/'){return $commit}
 if($line -match '^release view '){return '987654'}
 if($line -match '^release upload '){$file=Get-Item -LiteralPath $args[3];$global:MiniMockAssets.Add(@{name=$file.Name;size=$file.Length;digest='sha256:'+(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()});return}
 if($line -match '^api repos/.+/releases/987654$'){return @{draft=$true;assets=@($global:MiniMockAssets)}|ConvertTo-Json -Depth 6}
 if($line -match '^release (create|edit) '){return}
 throw "Unexpected mocked command: $line"
}
try {
 $publisher=Join-Path $scratch 'publish-file-update.ps1'
 & $publisher -PackageDirectory $scratch -MiniInstallerDirectory $mini
 if(@($global:MiniMockCalls | Where-Object {$_ -match '^release '}).Count){throw 'Preview mutated GitHub'}
 & $publisher -PackageDirectory $scratch -MiniInstallerDirectory $mini -Publish
 if($global:MiniMockAssets.Count-ne3){throw 'Mini EXE and receipt were not uploaded alongside the file manifest'}
 if(($global:MiniMockCalls -join "`n") -notmatch '--draft=false'){throw 'Verified release was not finalized'}
 $global:MiniMockCalls.Clear();$miniReceipt.standBuild=$true
 $miniReceipt|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $mini 'mini-installer.build.json') -Encoding utf8
 $rejected=$false;try{& $publisher -PackageDirectory $scratch -MiniInstallerDirectory $mini -Publish}catch{$rejected=$true}
 if(!$rejected-or$global:MiniMockCalls.Count){throw 'Stand mini was not rejected before GitHub calls'}
 $miniReceipt.standBuild=$false;$miniReceipt.size=10000001
 $miniReceipt|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $mini 'mini-installer.build.json') -Encoding utf8
 $rejected=$false;try{& $publisher -PackageDirectory $scratch -MiniInstallerDirectory $mini -Publish}catch{$rejected=$true}
 if(!$rejected-or$global:MiniMockCalls.Count){throw 'Invalid mini size was accepted'}
 Write-Host 'PASS: optional mini artifacts, preview, draft verification, stand rejection, size rejection; all GitHub calls mocked'
} finally { Remove-Item Function:\gh }
