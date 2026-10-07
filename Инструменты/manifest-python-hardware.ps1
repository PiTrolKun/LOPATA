param(
    [Parameter(Mandatory)][string]$Stage,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
# Run only against an independently verified stage assembled from the pinned upstream archives.
$taskRoot = (Resolve-Path -LiteralPath $Stage).Path
$taskPending = [Collections.Generic.Queue[string]]::new()
$taskPending.Enqueue($taskRoot)
$taskRows = [Collections.Generic.List[object]]::new()
while ($taskPending.Count -gt 0) {
    $taskCurrent = $taskPending.Dequeue()
    foreach ($taskItem in Get-ChildItem -LiteralPath $taskCurrent -Force) {
        if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Runtime manifest cannot contain links.' }
        if ($taskItem.PSIsContainer) { $taskPending.Enqueue($taskItem.FullName); continue }
        $taskRelative = [IO.Path]::GetRelativePath($taskRoot, $taskItem.FullName).Replace('\', '/')
        if ($taskRelative -eq 'runtime-manifest.json') { throw 'Use a clean stage without a prior manifest.' }
        $taskRows.Add([ordered]@{path=$taskRelative; size=$taskItem.Length;
            sha256=(Get-FileHash -LiteralPath $taskItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
}
$taskManifest = [ordered]@{profile='python312-torch210-cpu-transformers530';
    files=@($taskRows | Sort-Object -Property path -CaseSensitive)}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Output),
    ($taskManifest | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
Get-FileHash -LiteralPath $Output -Algorithm SHA256
