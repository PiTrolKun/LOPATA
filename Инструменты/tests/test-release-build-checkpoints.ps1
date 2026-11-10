$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskFixture=Join-Path ([IO.Path]::GetTempPath()) ('LOPATA-ReleaseStateTest-'+[guid]::NewGuid().ToString('N'))
$taskSavedGit=Get-Item Function:\global:git -ErrorAction SilentlyContinue
$taskSavedDotnet=Get-Item Function:\global:dotnet -ErrorAction SilentlyContinue
function Assert-Task([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
try {
    $taskTools=Join-Path $taskFixture 'Инструменты';New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'Инструменты/build-release-packages.ps1'),(Join-Path $taskRoot 'Инструменты/ReleaseBuildCheckpoint.ps1') -Destination $taskTools
    [IO.File]::WriteAllText((Join-Path $taskFixture 'VERSION'),'1.2.3-dev')
    [IO.File]::WriteAllText((Join-Path $taskFixture 'notes.md'),'Checkpoint fixture')
    [IO.File]::WriteAllText((Join-Path $taskFixture 'previous.signed.json'),'Fixture predecessor identity')
    @'
param($Version,$PublishDir,$NotesPath,$ChatLlmBackendDir,$OutputDirectory,$PreviousManifestPath,$HistoryPath,[switch]$StandBuild)
$global:ReleaseFixturePackCalls++
if($global:ReleaseFixturePackFailure){throw 'Fixture packing interrupted'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$global:LASTEXITCODE=0
'@ | Set-Content -LiteralPath (Join-Path $taskTools 'build-file-update.ps1') -Encoding utf8
    $global:ReleaseFixtureSource='a'*40;$global:ReleaseFixturePublishCalls=0;$global:ReleaseFixturePackCalls=0;$global:ReleaseFixturePackFailure=$true
    function global:git {
        $global:LASTEXITCODE=0
        if($args -contains 'rev-parse'){$global:ReleaseFixtureSource}
    }
    function global:dotnet {
        $taskArguments=@($args);$taskOutput=$taskArguments[[Array]::IndexOf($taskArguments,'-o')+1]
        New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
        $global:ReleaseFixturePublishCalls++
        if(($taskArguments -join ' ') -like '*AIHub.csproj*'){
            [IO.File]::WriteAllText((Join-Path $taskOutput 'AIHub.dll'),'fixture application')
            New-Item -ItemType Directory -Path (Join-Path $taskOutput 'Licenses') -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $taskOutput 'Licenses/catalog.json'),'[]')
        }
        else {[IO.File]::WriteAllText((Join-Path $taskOutput 'LOPATA.Updater.exe'),'fixture updater')}
        $global:LASTEXITCODE=0
    }
    $taskScript=Join-Path $taskTools 'build-release-packages.ps1'
    $taskOutput=Join-Path $taskFixture 'output'
    $taskParameters=@{Version='1.2.3-beta';NotesPath=(Join-Path $taskFixture 'notes.md');OutputDirectory=$taskOutput;
        PreviousManifestPath=(Join-Path $taskFixture 'previous.signed.json')}
    $taskRejected=$false
    try {& $taskScript -Version '1.2.3-beta' -NotesPath $taskParameters.NotesPath -OutputDirectory $taskOutput}
    catch {$taskRejected=$_.Exception.Message -like '*requires -PreviousManifestPath*'}
    Assert-Task $taskRejected 'A production release without a predecessor was allowed'
    $taskRejected=$false
    try {& $taskScript @taskParameters}catch {$taskRejected=$_.Exception.Message -like '*Fixture packing interrupted*'}
    Assert-Task $taskRejected 'The fixture interruption was not recorded'
    $taskStatePath=Join-Path $taskOutput 'release-build-state.json'
    $taskState=Get-Content -LiteralPath $taskStatePath -Raw | ConvertFrom-Json
    Assert-Task ($taskState.phase -eq 'failed' -and $taskState.appReady -and $taskState.hostReady) 'Confirmed stages were lost'
    Assert-Task ($global:ReleaseFixturePublishCalls -eq 2) 'The initial publish stages did not complete'
    $taskFirstPayload=$taskState.publishDirectory
    $global:ReleaseFixturePackFailure=$false
    & $taskScript @taskParameters -Resume
    $taskState=Get-Content -LiteralPath $taskStatePath -Raw | ConvertFrom-Json
    Assert-Task ($taskState.phase -eq 'complete' -and $global:ReleaseFixturePublishCalls -eq 2) 'A valid resume repeated publish stages'
    Assert-Task ($taskState.publishDirectory -eq $taskFirstPayload) 'A confirmed payload was replaced unnecessarily'
    [IO.File]::WriteAllText((Join-Path $taskFirstPayload 'AIHub.dll'),'tampered')
    [IO.File]::WriteAllText((Join-Path $taskFirstPayload 'unexpected.txt'),'not a compiler output')
    & $taskScript @taskParameters -Resume
    $taskState=Get-Content -LiteralPath $taskStatePath -Raw | ConvertFrom-Json
    Assert-Task ($taskState.publishDirectory -ne $taskFirstPayload -and $global:ReleaseFixturePublishCalls -eq 3) 'A damaged stage was reused'
    Assert-Task (!(Test-Path -LiteralPath (Join-Path $taskState.publishDirectory 'unexpected.txt'))) 'Unknown files entered the resumed payload'
    Assert-Task (Test-Path -LiteralPath (Join-Path $taskFirstPayload 'unexpected.txt')) 'Old evidence was destructively removed'
    $taskBefore=[IO.File]::ReadAllText($taskStatePath)
    $global:ReleaseFixtureSource='b'*40;$taskRejected=$false
    try {& $taskScript @taskParameters -Resume}catch {$taskRejected=$_.Exception.Message -like '*inputs changed*'}
    Assert-Task $taskRejected 'A checkpoint from another source commit was accepted'
    Assert-Task ([IO.File]::ReadAllText($taskStatePath) -ceq $taskBefore) 'The original checkpoint was overwritten by mismatched inputs'
    $global:ReleaseFixtureSource='a'*40
    $taskLock=[IO.File]::Open((Join-Path $taskOutput '.release-build.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $taskRejected=$false
        try {& $taskScript @taskParameters -Resume}catch {$taskRejected=$true}
        Assert-Task $taskRejected 'Concurrent ownership of the same output was allowed'
    }
    finally {$taskLock.Dispose()}
    $taskBeforeCalls=$global:ReleaseFixturePublishCalls
    $taskStand=@{Version='1.2.3-beta';NotesPath=$taskParameters.NotesPath;OutputDirectory=(Join-Path $taskFixture 'stand');StandBuild=$true}
    & $taskScript @taskStand
    & $taskScript @taskStand -Resume
    Assert-Task ($global:ReleaseFixturePublishCalls -eq $taskBeforeCalls+2) 'A valid isolated stand repeated publish stages'
    $taskRejected=$false
    try {& (Join-Path $taskRoot 'Инструменты/build-file-update.ps1') -Version '1.2.3-beta' -PublishDir $taskFixture `
        -ChatLlmBackendDir $taskFixture -NotesPath $taskParameters.NotesPath -OutputDirectory $taskOutput}
    catch {$taskRejected=$_.Exception.Message -like '*requires the previous signed manifest*'}
    Assert-Task $taskRejected 'Direct production packaging allowed a missing predecessor'
    $taskRejected=$false
    try {& (Join-Path $taskRoot 'Инструменты/build-installer.ps1') -ExplicitFullInstaller}
    catch {$taskRejected=$_.Exception.Message -like '*requires -PreviousManifestPath*'}
    Assert-Task $taskRejected 'Offline packaging failed to reject a missing predecessor before publish'
    'PASS: interrupted build, verified resume, damaged stage isolation, source mismatch, concurrent output lock, stand resume, all production entry guards'
}
finally {
    if($taskSavedGit){Set-Item Function:\global:git $taskSavedGit.ScriptBlock}else{Remove-Item Function:\global:git -ErrorAction SilentlyContinue}
    if($taskSavedDotnet){Set-Item Function:\global:dotnet $taskSavedDotnet.ScriptBlock}else{Remove-Item Function:\global:dotnet -ErrorAction SilentlyContinue}
    Remove-Variable ReleaseFixtureSource,ReleaseFixturePublishCalls,ReleaseFixturePackCalls,ReleaseFixturePackFailure -Scope Global -ErrorAction SilentlyContinue
    $taskFull=[IO.Path]::GetFullPath($taskFixture);$taskTemp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(!$taskFull.StartsWith($taskTemp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($taskFull) -notmatch '^LOPATA-ReleaseStateTest-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup'}
    if(Test-Path -LiteralPath $taskFull){Remove-Item -LiteralPath $taskFull -Recurse -Force}
}
