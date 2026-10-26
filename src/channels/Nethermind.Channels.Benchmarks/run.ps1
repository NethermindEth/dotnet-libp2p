param(
    [ValidateSet('main-listener', 'main-dialer', 'managed', 'owned', 'core-owned', 'core-legacy')]
    [string[]] $Variants,
    [string[]] $Scenarios = @('full-block', 'fragmented-waitall', 'small-reads', 'forwarding'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'results-current'),
    [switch] $BaselineOnly
)

$ErrorActionPreference = 'Stop'
if (!$Variants) {
    $Variants = if ($BaselineOnly) { @('main-listener', 'main-dialer', 'managed') } else { @('owned', 'core-owned', 'core-legacy') }
}
if ($BaselineOnly -and ($Variants | Where-Object { $_ -in @('owned', 'core-owned', 'core-legacy') })) {
    throw 'Current measurements require the native project.'
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$baselineRevision = '70b6c7321a2471dca985c6d15e8de30d0721c56b'
$baselineDirectory = Join-Path $repoRoot '.worktrees/allocations-pr428'
$project = Join-Path $PSScriptRoot 'Nethermind.Channels.Benchmarks.csproj'

if (!(Test-Path -LiteralPath $baselineDirectory)) {
    $excludeFile = git -C $repoRoot rev-parse --path-format=absolute --git-path info/exclude
    if (!(Get-Content -LiteralPath $excludeFile | Where-Object { $_ -eq '/.worktrees/' })) {
        Add-Content -LiteralPath $excludeFile -Value '/.worktrees/'
    }
    git -C $repoRoot worktree add --detach $baselineDirectory $baselineRevision
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the pinned baseline worktree.' }
}
if ((git -C $baselineDirectory rev-parse HEAD) -ne $baselineRevision) {
    throw 'The baseline worktree is not at the required revision.'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
dotnet --info | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dotnet-info.txt')
$workingRevision = git -C $repoRoot rev-parse HEAD
if (git -C $repoRoot status --porcelain --untracked-files=no) { $workingRevision += '-dirty' }
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    foreach ($currentCore in @($false, $true)) {
        $selectedVariants = @($Variants | Where-Object { ($_ -in @('core-owned', 'core-legacy')) -eq $currentCore })
        if ($selectedVariants.Count -eq 0) { continue }
        dotnet build $project -c Release -p:SaveDiskSpace=true "-p:BaselineOnly=$($BaselineOnly.IsPresent.ToString().ToLowerInvariant())" "-p:CoreBenchmarks=$($currentCore.ToString().ToLowerInvariant())" -warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
        if ($currentCore -or $selectedVariants -contains 'owned') {
            $sourceFiles = @(Get-ChildItem (Join-Path $repoRoot 'src/channels/Nethermind.Channels/*.cs'))
            if ($currentCore) { $sourceFiles += Get-ChildItem (Join-Path $repoRoot 'libp2p/Libp2p.Core/*.cs') }
            $hashes = $sourceFiles | Sort-Object FullName | ForEach-Object {
                "$( (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash ) $([IO.Path]::GetRelativePath($repoRoot, $_.FullName))"
            }
            $hashFile = if ($currentCore) { 'core-source-hashes.txt' } else { 'owned-source-hashes.txt' }
            $hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory $hashFile)
        }
        foreach ($variant in $selectedVariants) {
            foreach ($scenario in $Scenarios) {
                Write-Host "Measuring $variant / $scenario"
                $result = & dotnet (Join-Path $PSScriptRoot 'bin/Release/net10.0/Nethermind.Channels.Benchmarks.dll') $variant $scenario $workingRevision
                if ($LASTEXITCODE -ne 0) { throw "Measurement failed: $variant / $scenario" }
                $null = $result | ConvertFrom-Json
                $result | Set-Content -LiteralPath (Join-Path $OutputDirectory "$variant-$scenario.json")
            }
        }
    }
}
finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
