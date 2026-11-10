param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NotesPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PreviousManifestPath,
    [string]$HistoryPath,
    [switch]$StandBuild,
    [switch]$Resume
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(-beta)?$') { throw 'An explicit public version is required.' }
$internal = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim() -replace '-dev$', ''
if (($Version -replace '-beta$', '') -ne $internal) { throw 'The release must use the current VERSION number.' }
# poka-yoke: an ordinary release must have an authenticated predecessor for preservation.
if (!$StandBuild -and !$PreviousManifestPath) { throw 'A production release requires -PreviousManifestPath. Use -StandBuild only for an isolated stand.' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $out -Force | Out-Null
. (Join-Path $PSScriptRoot 'ReleaseBuildCheckpoint.ps1')
$statePath=Join-Path $out 'release-build-state.json'
$lock=$null;$state=$null
try {
    # poka-yoke: one durable build owns the output; a dead tool-session is not a completed release.
    $lock=[IO.File]::Open((Join-Path $out '.release-build.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $commit=(& git -C $root rev-parse HEAD).Trim()
    if($LASTEXITCODE -or $commit -notmatch '^[a-f0-9]{40}$'){throw 'Cannot identify build sources.'}
    $dirty=@(& git -C $root status --porcelain --untracked-files=all -- 'Исходники' 'Инструменты' 'VERSION' 'Каталоги' 'LICENSE' 'NOTICE.md' 'THIRD_PARTY_NOTICES.md')
    if($LASTEXITCODE -or ($dirty.Count -and (!$StandBuild -or $Resume))){throw 'A production or resumed build requires clean committed sources.'}
    $notesHash=(Get-FileHash -LiteralPath $NotesPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $previousHash=if($PreviousManifestPath){(Get-FileHash -LiteralPath $PreviousManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()}else{''}
    $historyHash=if($HistoryPath){(Get-FileHash -LiteralPath $HistoryPath -Algorithm SHA256).Hash.ToLowerInvariant()}else{''}
    if($Resume){
        if(-not(Test-Path -LiteralPath $statePath)){throw 'No build checkpoint is available.'}
        $state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
        # poka-yoke: checkpoints from another commit/version/notes cannot label stale binaries as the new release.
        if($state.schemaVersion -ne 1 -or $state.version -cne $Version -or $state.sourceCommit -cne $commit -or
           $state.notesSha256 -cne $notesHash -or $state.previousManifestSha256 -cne $previousHash -or
           $state.historySha256 -cne $historyHash -or [bool]$state.standBuild -ne [bool]$StandBuild){
            $state=$null;throw 'Build checkpoint inputs changed. Use a new output directory.'
        }
        Assert-ReleaseStagePath $out $state.publishDirectory 'payload-'
        Assert-ReleaseStagePath $out $state.hostDirectory 'host-'
    }
    else {
        if(Test-Path -LiteralPath $statePath){throw 'An existing build must be continued with -Resume or kept in another output directory.'}
        $state=[ordered]@{schemaVersion=1;version=$Version;sourceCommit=$commit;notesSha256=$notesHash;
            previousManifestSha256=$previousHash;historySha256=$historyHash;standBuild=[bool]$StandBuild;
            publishDirectory=(Join-Path $out ('payload-'+[guid]::NewGuid().ToString('N')));
            hostDirectory=(Join-Path $out ('host-'+[guid]::NewGuid().ToString('N')));appReady=$false;hostReady=$false;phase='starting'}
    }
    $state.pid=$PID;$state.startedUtc=[DateTimeOffset]::UtcNow.ToString('o');$state.error=''
    $publish=$state.publishDirectory;$hostOutput=$state.hostDirectory
    $constants=if($StandBuild){'UPDATE_STAND'}else{''}
    $appSnapshot=Join-Path $out 'app-files.checkpoint.json';$hostSnapshot=Join-Path $out 'host-files.checkpoint.json'
    if(!$state.appReady -or !(Test-ReleaseBuildInventory $publish $appSnapshot 'Updater/LOPATA.Updater.exe')){
        $publish=Join-Path $out ('payload-'+[guid]::NewGuid().ToString('N'));$state.publishDirectory=$publish;$state.appReady=$false
        $state.phase='publishing-app';Write-ReleaseBuildCheckpoint $statePath $state
        & dotnet publish (Join-Path $root 'Исходники/AIHub/AIHub.csproj') -c Release -r win-x64 --self-contained true -o $publish `
            -p:PublishSingleFile=false -p:PublishReadyToRun=false "-p:AIHubVersion=$Version" "-p:DefineConstants=$constants"
        if($LASTEXITCODE){throw 'Application publish failed.'}
        if(!(Test-Path -LiteralPath (Join-Path $publish 'AIHub.dll'))){throw 'Application publish is incomplete.'}
        if($StandBuild){'Isolated stand. Never publish.' | Set-Content -LiteralPath (Join-Path $publish 'lopata-stand-build.marker') -Encoding utf8}
        Write-ReleaseBuildCheckpoint $appSnapshot @(Get-ReleaseBuildInventory $publish 'Updater/LOPATA.Updater.exe')
        $state.appReady=$true;$state.phase='app-ready';Write-ReleaseBuildCheckpoint $statePath $state
    }
    if(!$state.hostReady -or !(Test-ReleaseBuildInventory $hostOutput $hostSnapshot)){
        $hostOutput=Join-Path $out ('host-'+[guid]::NewGuid().ToString('N'));$state.hostDirectory=$hostOutput;$state.hostReady=$false
        $state.phase='publishing-updater';Write-ReleaseBuildCheckpoint $statePath $state
        & dotnet publish (Join-Path $root 'Исходники/LOPATA.Updater/LOPATA.Updater.csproj') -c Release -r win-x64 --self-contained true -o $hostOutput `
            -p:PublishSingleFile=true -p:PublishReadyToRun=false "-p:DefineConstants=$constants"
        if($LASTEXITCODE){throw 'Update host publish failed.'}
        if(!(Test-Path -LiteralPath (Join-Path $hostOutput 'LOPATA.Updater.exe'))){throw 'Update host publish is incomplete.'}
        Write-ReleaseBuildCheckpoint $hostSnapshot @(Get-ReleaseBuildInventory $hostOutput)
        $state.hostReady=$true;$state.phase='updater-ready';Write-ReleaseBuildCheckpoint $statePath $state
    }
    New-Item -ItemType Directory -Path (Join-Path $publish 'Updater') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $hostOutput 'LOPATA.Updater.exe') -Destination (Join-Path $publish 'Updater')
    if($StandBuild){'Isolated stand. Never publish.' | Set-Content -LiteralPath (Join-Path $publish 'lopata-stand-build.marker') -Encoding utf8}
    $catalog=Get-Content -LiteralPath (Join-Path $publish 'Licenses/catalog.json') -Raw | ConvertFrom-Json
    foreach($entry in $catalog){foreach($text in $entry.Texts){
        if(!(Test-Path -LiteralPath (Join-Path $publish "Licenses/$text"))){throw "Missing license: $text"}
    }}
    $packages=Join-Path $out 'packages';$state.phase='packing';Write-ReleaseBuildCheckpoint $statePath $state
    & (Join-Path $PSScriptRoot 'build-file-update.ps1') -Version $Version -PublishDir $publish -NotesPath $NotesPath `
        -ChatLlmBackendDir (Join-Path $root 'Runtime/Backends/chatllm.cpp/v24/win-x64') -OutputDirectory $packages `
        -PreviousManifestPath $PreviousManifestPath -HistoryPath $HistoryPath -StandBuild:$StandBuild
    if($LASTEXITCODE){throw 'Signed package build failed.'}
    # Preparation may legitimately restore authenticated predecessor files; checkpoint the verified result.
    Write-ReleaseBuildCheckpoint $appSnapshot @(Get-ReleaseBuildInventory $publish 'Updater/LOPATA.Updater.exe')
    $state.phase='complete';$state.completedUtc=[DateTimeOffset]::UtcNow.ToString('o');Write-ReleaseBuildCheckpoint $statePath $state
    @{version=$Version;publishDirectory=$publish;packageDirectory=$packages;fullInstallerBuilt=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'release-packages.build.json') -Encoding utf8
    Write-Host "Release packages ready: $packages. No offline installer was built or published."
}
catch {
    if($state){$state.phase='failed';$state.error=$_.Exception.Message;$state.failedUtc=[DateTimeOffset]::UtcNow.ToString('o');Write-ReleaseBuildCheckpoint $statePath $state}
    throw
}
finally {if($lock){$lock.Dispose()}}
