function Get-IslandReleaseCommit {
    $pin = Join-Path $PSScriptRoot 'nabyscape-release-commit.txt'
    if (!(Test-Path -LiteralPath $pin -PathType Leaf)) { return }
    $commit = [IO.File]::ReadAllText($pin).Trim()
    if ($commit -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid NabyScape fork commit pin.' }
    $commit
}

function Add-IslandReleasePayload([string]$PublishDirectory, [string]$Commit, [string]$IslandForkDirectory) {
    if (!$Commit) { return }
    if (!$IslandForkDirectory) { $IslandForkDirectory = $env:LOPATA_ISLAND_FORK }
    if (!$IslandForkDirectory) {
        $repo = Split-Path -Parent $PSScriptRoot
        $IslandForkDirectory = @((Join-Path (Split-Path -Parent $repo) 'NabyScape-LOPATA-Edition'),
            (Join-Path (Split-Path -Parent $repo) 'nabyscape'),
            (Join-Path $repo '_tmp/nabyscape-stage2')) |
            Where-Object { Test-Path -LiteralPath (Join-Path $_ 'src/NabyScape/NabyScape.csproj') -PathType Leaf } |
            Select-Object -First 1
    }
    if (!$IslandForkDirectory) {
        throw "Use -IslandForkDirectory or LOPATA_ISLAND_FORK to supply the PiTrolKun/NabyScape-LOPATA-Edition checkout at $Commit."
    }
    $current = & git -C $IslandForkDirectory rev-parse --verify HEAD
    if ($LASTEXITCODE -or $current -cne $Commit) { throw "The NabyScape checkout must be at pinned commit $Commit." }
    & (Join-Path $PSScriptRoot 'build-nabyscape-edition.ps1') -ForkDirectory $IslandForkDirectory -HostOutputDirectory $PublishDirectory |
        Out-Host
    Assert-IslandReleasePayload $PublishDirectory $Commit
}

function Assert-IslandReleasePayload([string]$PublishDirectory, [string]$Commit) {
    if (!$Commit) { return }
    $island = Join-Path $PublishDirectory 'NabyScape'
    $receipt = Get-Content -LiteralPath (Join-Path $island 'island-package.json') -Raw | ConvertFrom-Json
    $executable = Get-Item -LiteralPath (Join-Path $island 'LopataIsland.exe')
    $files = @(Get-ChildItem -LiteralPath $island -File -Recurse -Force)
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    if ($receipt.Commit -cne $Commit -or $executable.Length -ne $receipt.Length -or
        (Get-FileHash -LiteralPath $executable.FullName).Hash -cne $receipt.Sha256 -or
        !(Test-Path -LiteralPath (Join-Path $island 'LICENSE') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $island 'licenses/LOPATA-GPL-3.0.txt') -PathType Leaf)) {
        throw 'NabyScape release payload failed verification.'
    }
    if ($size -ge 500000000) { throw "NabyScape has reached the distribution threshold: $size bytes." }
}
