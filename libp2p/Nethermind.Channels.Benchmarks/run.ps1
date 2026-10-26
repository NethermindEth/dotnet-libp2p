param(
    [ValidateSet('main-listener', 'main-dialer', 'managed', 'owned', 'core-owned', 'core-legacy')]
    [string[]] $Variants,
    [string[]] $Scenarios = @('full-block', 'fragmented-waitall', 'small-reads', 'forwarding', 'steady-small-reads'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'results-current'),
    [string] $SourceRoot = (Join-Path $PSScriptRoot '../..'),
    [ValidateSet('allocation', 'throughput')]
    [string] $Measurement = 'allocation',
    [ValidateRange(1, 10)]
    [int] $Replicas = 3,
    [switch] $BaselineOnly
)

$ErrorActionPreference = 'Stop'
if (!$Variants) {
    $Variants = if ($BaselineOnly) { @('main-listener', 'main-dialer', 'managed') } else { @('owned', 'core-owned', 'core-legacy') }
}
if ($BaselineOnly -and ($Variants | Where-Object { $_ -in @('owned', 'core-owned', 'core-legacy') })) {
    throw 'Current measurements require the native project.'
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$baselineRevision = '70b6c7321a2471dca985c6d15e8de30d0721c56b'
$baselineDirectory = Join-Path $repoRoot '.worktrees/allocations-pr428'
$project = Join-Path $PSScriptRoot 'Nethermind.Channels.Benchmarks.csproj'

if ($Variants | Where-Object { $_ -in @('main-listener', 'main-dialer', 'managed') }) {
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
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
dotnet --info | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dotnet-info.txt')
$workingRevision = git -C $SourceRoot rev-parse HEAD
if (git -C $SourceRoot status --porcelain --untracked-files=no) { $workingRevision += '-dirty' }
$benchmarkRevision = git -C $repoRoot rev-parse HEAD
if (git -C $repoRoot status --porcelain --untracked-files=no -- $PSScriptRoot) { $benchmarkRevision += '-dirty' }
$benchmarkFiles = @('Program.cs', 'run.ps1', 'Nethermind.Channels.Benchmarks.csproj')
$benchmarkFiles | ForEach-Object {
    "$( (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $PSScriptRoot $_)).Hash ) $_"
} | Set-Content -LiteralPath (Join-Path $OutputDirectory 'benchmark-source-hashes.txt')
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    foreach ($currentCore in @($false, $true)) {
        $selectedVariants = @($Variants | Where-Object { ($_ -in @('owned', 'core-owned', 'core-legacy')) -eq $currentCore })
        if ($selectedVariants.Count -eq 0) { continue }
        dotnet build $project -c Release -p:SaveDiskSpace=true "-p:SourceRoot=$SourceRoot" "-p:BaselineOnly=$((!$currentCore).ToString().ToLowerInvariant())" "-p:CoreBenchmarks=$($currentCore.ToString().ToLowerInvariant())" -warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
        if ($currentCore) {
            $channelsDirectory = Join-Path $SourceRoot 'libp2p/Nethermind.Channels'
            if (!(Test-Path -LiteralPath (Join-Path $channelsDirectory 'Nethermind.Channels.csproj'))) {
                $channelsDirectory = Join-Path $SourceRoot 'src/channels/Nethermind.Channels'
            }
            $sourceFiles = @(Get-ChildItem (Join-Path $channelsDirectory '*.cs'))
            $sourceFiles | Sort-Object FullName | ForEach-Object {
                "$( (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash ) $([IO.Path]::GetRelativePath($SourceRoot, $_.FullName))"
            } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'owned-source-hashes.txt')
            $sourceFiles += Get-ChildItem (Join-Path $SourceRoot 'libp2p/Libp2p.Core/*.cs')
            $hashes = $sourceFiles | Sort-Object FullName | ForEach-Object {
                "$( (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash ) $([IO.Path]::GetRelativePath($SourceRoot, $_.FullName))"
            }
            $hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'core-source-hashes.txt')
        }
        foreach ($variant in $selectedVariants) {
            foreach ($scenario in $Scenarios) {
                $processCount = if ($Measurement -eq 'throughput') { $Replicas } else { 1 }
                for ($replica = 1; $replica -le $processCount; $replica++) {
                    Write-Host "Measuring $Measurement / $variant / $scenario / $replica"
                    $result = & dotnet (Join-Path $PSScriptRoot 'bin/Release/net10.0/Nethermind.Channels.Benchmarks.dll') $variant $scenario $workingRevision $Measurement $benchmarkRevision
                    if ($LASTEXITCODE -ne 0) { throw "Measurement failed: $variant / $scenario / $replica" }
                    $null = $result | ConvertFrom-Json
                    $suffix = if ($Measurement -eq 'throughput') { "-throughput-$replica" } else { '' }
                    $result | Set-Content -LiteralPath (Join-Path $OutputDirectory "$variant-$scenario$suffix.json")
                }
            }
        }
    }
}
finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
