$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$OutputEncoding = [Text.UTF8Encoding]::new($false)
$build = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../StandCleanup/bin/Release/net10.0')).ProviderPath
$fixture = Join-Path $build ('StandCleanup-Test-' + [guid]::NewGuid().ToString('N'))
$temporary = Join-Path $fixture '_tmp'
$proof = Join-Path $fixture 'Проверка.txt'
$executable = Join-Path $fixture 'StandCleanup.exe'
$junction = Join-Path $temporary 'со ссылкой/переход'

function Assert-Test([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Invoke-Cleanup([string[]]$Command, [int]$Expected = 0, [string[]]$Reply) {
    $result = if ($Reply) { $Reply | & $executable @Command 2>&1 } else { & $executable @Command 2>&1 }
    $output = $result | Out-String
    if ($LASTEXITCODE -ne $Expected) { throw "Unexpected exit ($LASTEXITCODE) for $($Command -join ' '). $output" }
    return $output
}

function New-Stand([string]$Name) {
    return [IO.Directory]::CreateDirectory((Join-Path $temporary $Name)).FullName
}

function Add-Stand([string]$Path, [string]$Evidence = $proof) {
    $text = Invoke-Cleanup @('add', $Path, 'Синтетическая проверка стенда', $Evidence)
    Assert-Test ($text -match 'Запись ([a-f0-9]{32})') 'Missing queue number'
    return $Matches[1]
}

function Invoke-Git([string]$Path, [string[]]$Command) {
    $result = & git -C $Path @Command 2>&1
    if ($LASTEXITCODE) { throw ($result | Out-String) }
    return $result
}

try {
    [void][IO.Directory]::CreateDirectory($temporary)
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'Инструкции'))
    [IO.File]::WriteAllText((Join-Path $fixture 'Инструкции/AGENTS.md'), 'Isolated test workspace')
    [IO.File]::WriteAllText($proof, 'Preserved evidence')
    foreach ($file in @('StandCleanup.exe', 'StandCleanup.dll', 'StandCleanup.deps.json', 'StandCleanup.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $build $file) -Destination $fixture
    }

    $normal = New-Stand 'стенд с пробелами'
    $ordinary = Join-Path $normal 'данные.txt'
    [IO.File]::WriteAllText($ordinary, 'temp')
    [void](New-Item -ItemType HardLink -Path (Join-Path $normal 'вторая ссылка.txt') -Target $ordinary)
    $long = $normal
    foreach ($number in 1..4) { $long = Join-Path $long ('длинный путь ' + 'я' * 61) }
    [void][IO.Directory]::CreateDirectory($long)
    [IO.File]::WriteAllText((Join-Path $long 'данные.tmp'), 'long path')
    Assert-Test ($long.Length -gt 260) 'Long path fixture is too short'

    [void](Invoke-Cleanup @('add', $fixture, 'outside', $proof) 1)
    [void](Invoke-Cleanup @('add', $temporary, 'root', $proof) 1)
    [void](Invoke-Cleanup @('add', $normal, 'inside proof', $ordinary) 1)
    [void](New-Stand 'со ссылкой')
    [void](New-Item -ItemType Junction -Path $junction -Target $fixture)
    [void](Invoke-Cleanup @('add', (Split-Path -Parent $junction), 'linked', $proof) 1)
    Assert-Test ([IO.File]::Exists($proof)) 'A link led to removal of evidence'
    Write-Output 'PASS Scope and junction protection'

    $normalId = Add-Stand $normal
    Assert-Test ((Add-Stand $normal) -eq $normalId) 'Duplicate queue item'
    [void](Invoke-Cleanup @('clean', $normalId, $normal))
    Assert-Test ([IO.File]::Exists($ordinary)) 'Preview removed a file'
    $empty = New-Stand 'пустой стенд'
    [void](Add-Stand $empty)
    [void](Invoke-Cleanup @('clean', $normalId, $empty, '--apply') 1)
    [void](Invoke-Cleanup @('interactive') -Reply @('все', 'нет'))
    Assert-Test ([IO.Directory]::Exists($normal) -and [IO.Directory]::Exists($empty)) 'Cancellation removed a directory'
    [void](Invoke-Cleanup @('interactive') -Reply @('все', 'да'))
    Assert-Test (![IO.Directory]::Exists($normal) -and ![IO.Directory]::Exists($empty)) 'Selected directories survived'
    Assert-Test ([IO.File]::Exists($proof)) 'Evidence was lost'
    Write-Output 'PASS Confirmed group cleanup, Cyrillic, hard links and long paths'

    $changed = New-Stand 'изменённый'
    $changedFile = Join-Path $changed 'existing.txt'
    [IO.File]::WriteAllText($changedFile, 'old!')
    $changedId = Add-Stand $changed
    $oldWrite = [IO.File]::GetLastWriteTimeUtc($changedFile)
    [IO.File]::WriteAllText($changedFile, 'new!')
    [IO.File]::SetLastWriteTimeUtc($changedFile, $oldWrite)
    [void](Invoke-Cleanup @('clean', $changedId, $changed, '--apply') 2)
    Assert-Test ([IO.File]::ReadAllText($changedFile) -eq 'new!') 'Modified file was removed'
    $extra = New-Stand 'новый файл'
    $original = Join-Path $extra 'original.txt'
    [IO.File]::WriteAllText($original, 'original')
    $extraId = Add-Stand $extra
    [IO.File]::WriteAllText((Join-Path $extra 'added-later.txt'), 'must survive')
    [void](Invoke-Cleanup @('clean', $extraId, $extra, '--apply') 2)
    Assert-Test ([IO.File]::Exists($original) -and [IO.File]::Exists((Join-Path $extra 'added-later.txt'))) 'Changed stand was partly removed'
    $relocated = New-Stand 'пересозданный'
    [IO.File]::WriteAllText((Join-Path $relocated 'файл.txt'), 'original folder')
    $relocatedId = Add-Stand $relocated
    Rename-Item -LiteralPath $relocated -NewName 'сохранённая копия'
    [void](New-Stand 'пересозданный')
    [IO.File]::WriteAllText((Join-Path $relocated 'файл.txt'), 'replacement')
    [void](Invoke-Cleanup @('clean', $relocatedId, $relocated, '--apply') 2)
    Assert-Test ([IO.File]::ReadAllText((Join-Path $relocated 'файл.txt')) -eq 'replacement') 'Replacement folder was removed'
    $withoutEvidence = New-Stand 'пропал протокол'
    $secondaryProof = Join-Path $fixture 'Другой протокол.txt'
    [IO.File]::WriteAllText($secondaryProof, 'evidence')
    $withoutEvidenceId = Add-Stand $withoutEvidence $secondaryProof
    [IO.File]::Delete($secondaryProof)
    [void](Invoke-Cleanup @('clean', $withoutEvidenceId, $withoutEvidence, '--apply') 2)
    Assert-Test ([IO.Directory]::Exists($withoutEvidence)) 'Stand without verified evidence was removed'
    Write-Output 'PASS New and modified files survive'

    $busy = New-Stand 'занятый'
    $busyFile = Join-Path $busy 'занят.txt'
    [IO.File]::WriteAllText($busyFile, 'busy')
    $goneFile = Join-Path $busy 'already gone.txt'
    [IO.File]::WriteAllText($goneFile, 'temp')
    $busyId = Add-Stand $busy
    [IO.File]::Delete($goneFile)
    $hold = [IO.File]::Open($busyFile, 'Open', 'Read', 'Read')
    try { [void](Invoke-Cleanup @('clean', $busyId, $busy, '--apply') 2) }
    finally { $hold.Dispose() }
    Assert-Test ([IO.File]::Exists($busyFile)) 'Busy file was removed'
    [void](Invoke-Cleanup @('clean', $busyId, $busy, '--apply'))
    Assert-Test (![IO.Directory]::Exists($busy)) 'Retry of incomplete cleanup failed'
    $manual = New-Stand 'удален вручную'
    $manualId = Add-Stand $manual
    [IO.Directory]::Delete($manual)
    $history = @(Invoke-Cleanup @('list','--all','--json') | ConvertFrom-Json)
    Assert-Test (@($history | Where-Object { $_.id.Replace('-', '') -eq $manualId -and $_.completed }).Count -eq 1) 'Manual deletion was not recorded'
    Write-Output 'PASS Busy file retry and manual cleanup receipt'

    $repo = [IO.Directory]::CreateDirectory((Join-Path $fixture 'repo')).FullName
    [void](Invoke-Git $repo @('init','-q','-b','main'))
    [IO.File]::WriteAllText((Join-Path $repo 'source.txt'), 'preserved source')
    [void](Invoke-Git $repo @('add','source.txt'))
    [void](Invoke-Git $repo @('-c','user.name=Cleanup Test','-c','user.email=cleanup-test@example.invalid','commit','-qm','Fixture'))
    $head = Invoke-Git $repo @('rev-parse','HEAD')
    $release = New-Stand 'временный выпуск'
    $worktree = Join-Path $release 'source'
    [void](Invoke-Git $repo @('worktree','add','-q','--detach',$worktree,$head))
    [void](Invoke-Cleanup @('add', $release, 'local-only commit', $proof) 1)
    [void](Invoke-Git $repo @('update-ref','refs/remotes/origin/main',$head))
    [IO.File]::WriteAllText((Join-Path $worktree 'source.txt'), 'local work')
    [void](Invoke-Cleanup @('add', $release, 'local edits', $proof) 1)
    [void](Invoke-Git $worktree @('restore','source.txt'))
    [void](Invoke-Git $worktree @('update-index','--assume-unchanged','source.txt'))
    [void](Invoke-Cleanup @('add', $release, 'ignored local edits', $proof) 1)
    [void](Invoke-Git $worktree @('update-index','--no-assume-unchanged','source.txt'))
    [IO.File]::WriteAllText((Join-Path $worktree 'build.tmp'), 'reproducible')
    $releaseId = Add-Stand $release
    [void](Invoke-Cleanup @('clean', $releaseId, $release, '--apply'))
    Assert-Test (![IO.Directory]::Exists($release)) 'Verified worktree stand survived'
    Assert-Test ((Invoke-Git $repo @('rev-parse','HEAD')) -eq $head) 'Original Git repository was changed'
    Assert-Test ([IO.File]::ReadAllText((Join-Path $repo 'source.txt')) -eq 'preserved source') 'Original Git source was changed'
    Write-Output 'PASS Clean published worktree and preservation of local changes'
}
finally {
    if (Test-Path -LiteralPath $fixture) {
        $full = (Resolve-Path -LiteralPath $fixture).ProviderPath
        Assert-Test (([IO.Path]::GetDirectoryName($full) -eq $build) -and ([IO.Path]::GetFileName($full) -match '^StandCleanup-Test-[a-f0-9]{32}$')) 'Unsafe test cleanup path'
        if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
        $entries = @(Get-ChildItem -LiteralPath $full -Recurse -Force)
        Assert-Test (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -eq 0) 'Unexpected links in test cleanup'
        foreach ($file in $entries | Where-Object { !$_.PSIsContainer }) {
            if ($file.Attributes -band [IO.FileAttributes]::ReadOnly) { $file.IsReadOnly = $false }
            [IO.File]::Delete($file.FullName)
        }
        foreach ($directory in $entries | Where-Object PSIsContainer | Sort-Object { $_.FullName.Length } -Descending) { [IO.Directory]::Delete($directory.FullName) }
        [IO.Directory]::Delete($full)
    }
}
