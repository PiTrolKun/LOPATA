# Developer build checkpoints. No installed files or signing key contents belong here.
function Write-ReleaseBuildCheckpoint([string]$Path, $State) {
    $taskTemporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($taskTemporary, ($State | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $taskTemporary -Destination $Path -Force
    }
    finally { if(Test-Path -LiteralPath $taskTemporary){Remove-Item -LiteralPath $taskTemporary} }
}

function Get-ReleaseBuildInventory([string]$Root, [string]$Skip='') {
    if(-not(Test-Path -LiteralPath $Root -PathType Container)){throw 'A confirmed build stage is missing.'}
    $taskItems=@(Get-ChildItem -LiteralPath $Root -Recurse -Force)
    if((Get-Item -LiteralPath $Root).Attributes -band [IO.FileAttributes]::ReparsePoint -or
       @($taskItems | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'A build checkpoint cannot contain links.'}
    $taskRows=@(foreach($taskFile in $taskItems | Where-Object {-not $_.PSIsContainer}) {
        $taskRelative=[IO.Path]::GetRelativePath($Root,$taskFile.FullName).Replace('\','/')
        if($taskRelative -eq $Skip){continue}
        $taskStream=[IO.File]::OpenRead($taskFile.FullName)
        try {$taskDigest=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($taskStream))}
        finally {$taskStream.Dispose()}
        [ordered]@{path=$taskRelative;size=$taskFile.Length;sha256=$taskDigest}
    })
    return @($taskRows | Sort-Object {$_['path']})
}

function Test-ReleaseBuildInventory([string]$Root,[string]$Snapshot,[string]$Skip='') {
    if(-not(Test-Path -LiteralPath $Snapshot)){return $false}
    try {
        $taskExpected=@(Get-Content -LiteralPath $Snapshot -Raw | ConvertFrom-Json)
        $taskActual=@(Get-ReleaseBuildInventory $Root $Skip)
        if($taskActual.Count -ne $taskExpected.Count){return $false}
        for($taskIndex=0;$taskIndex -lt $taskActual.Count;$taskIndex++){
            if($taskActual[$taskIndex].path -cne $taskExpected[$taskIndex].path -or
               $taskActual[$taskIndex].size -ne $taskExpected[$taskIndex].size -or
               $taskActual[$taskIndex].sha256 -cne $taskExpected[$taskIndex].sha256){return $false}
        }
        return $true
    }
    catch {return $false}
}

function Assert-ReleaseStagePath([string]$Root,[string]$Path,[string]$Prefix) {
    $taskFull=[IO.Path]::GetFullPath($Path)
    $taskParent=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if([IO.Path]::GetDirectoryName($taskFull) -cne $taskParent -or
       [IO.Path]::GetFileName($taskFull) -notmatch ('^'+[regex]::Escape($Prefix)+'[a-f0-9]{32}$')){
        throw 'The checkpoint refers outside its dedicated output directory.'
    }
}
