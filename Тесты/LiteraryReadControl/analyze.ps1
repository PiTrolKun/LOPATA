param([Parameter(Mandatory=$true)][string]$Run)
$ErrorActionPreference = 'Stop'
$folder = (Resolve-Path -LiteralPath $Run).Path
$project = (Get-Content -LiteralPath (Join-Path $folder 'project-path.txt') -Raw).Trim()
$logs = @(Get-ChildItem -LiteralPath (Join-Path $project 'Diagnostics/LiteraryDetailed') -Recurse -Filter '*.jsonl')
$summary = @(Get-Content -LiteralPath (Join-Path $folder 'summary.json') -Raw | ConvertFrom-Json)
$audit = foreach ($row in $summary) {
    $resultFile = Get-Item -LiteralPath (Join-Path $folder ($row.key + '.json'))
    $log = $logs | Sort-Object { [Math]::Abs(($_.LastWriteTimeUtc - $resultFile.LastWriteTimeUtc).TotalSeconds) } | Select-Object -First 1
    if ([Math]::Abs(($log.LastWriteTimeUtc - $resultFile.LastWriteTimeUtc).TotalSeconds) -gt 2) { throw "No matching diagnostic for $($row.key)" }
    $events = @(Get-Content -LiteralPath $log.FullName | ForEach-Object { $_ | ConvertFrom-Json })
    $inputEvent = $events | Where-Object kind -eq 'input' | Select-Object -First 1
    if (!$row.error -and $inputEvent.data[-1].Content -cne $row.task) { throw "Task mismatch: $($row.key)" }
    $route = $events | Where-Object kind -eq 'read_route' | Select-Object -Last 1
    $buffer = $events | Where-Object kind -eq 'reading_buffer' | Select-Object -Last 1
    $request = $events | Where-Object kind -eq 'request' | Select-Object -Last 1
    $payload = if ($request) { $request.data.json | ConvertFrom-Json }
    $finalText = if ($payload) { $payload.messages[-1].content } else { '' }
    [pscustomobject]@{
        key=$row.key; error=$row.error; expectedScope=$row.expectedScope; actualScope=$route.data.Scope
        reads=@($events | Where-Object kind -eq 'source_read').Count
        mandatory=@($events | Where-Object kind -eq 'mandatory_read').Count
        missing=@($buffer.data.missing); seconds=$row.seconds
        finalHasCurrentDraft=($finalText.Contains([string]$row.draft))
        finalHasHistoryCode=$finalText.Contains('НЕФРИТ-523'); finalHasReferenceCode=$finalText.Contains('ЯНТАРЬ-684')
        answer=$row.result; diagnostic=$log.FullName
    }
}
$audit | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $folder 'audit.json') -Encoding utf8
$audit | Select-Object key,actualScope,reads,mandatory,@{n='missing';e={$_.missing -join ','}},finalHasHistoryCode,finalHasReferenceCode | Format-Table -AutoSize
Write-Output ('Requests: ' + $audit.Count + '; errors: ' + @($audit | Where-Object error).Count)
Write-Output ('Exact scope matches: ' + @($audit | Where-Object { $_.expectedScope -ceq $_.actualScope }).Count)
Write-Output 'Evidence flags describe delivery, not correctness of the answer. Inspect the saved answer texts separately.'
