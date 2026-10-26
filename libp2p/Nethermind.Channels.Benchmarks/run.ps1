param(
    [ValidateSet('main-listener', 'main-dialer', 'managed', 'owned')]
    [string[]] $Variants,
    [string[]] $Scenarios = @('full-block', 'fragmented-waitall', 'small-reads', 'forwarding'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'results-current'),
    [string] $SourceRoot = (Join-Path $PSScriptRoot '../..'),
    [string] $BaselineRoot = (Join-Path $PSScriptRoot '../../.worktrees/allocations-pr428'),
    [switch] $BaselineOnly
)

$ErrorActionPreference = 'Stop'
if (!$Variants) {
    $Variants = if ($BaselineOnly) { @('main-listener', 'main-dialer', 'managed') } else { @('owned') }
}
if ($BaselineOnly -and $Variants -contains 'owned') { throw 'Owned measurements require the native project.' }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$baselineDirectory = [IO.Path]::GetFullPath($BaselineRoot)
$baselineRevision = '70b6c7321a2471dca985c6d15e8de30d0721c56b'
$project = Join-Path $PSScriptRoot 'Nethermind.Channels.Benchmarks.csproj'

if ($Variants | Where-Object { $_ -ne 'owned' }) {
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
    git -C $baselineDirectory diff --quiet HEAD -- libp2p/Libp2p.Core
    if ($LASTEXITCODE -ne 0) { throw 'The pinned baseline Core sources have local changes.' }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
dotnet --info | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dotnet-info.txt')
$workingRevision = git -C $SourceRoot rev-parse HEAD
if (git -C $SourceRoot status --porcelain --untracked-files=no) { $workingRevision += '-dirty' }
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    foreach ($historical in @($false, $true)) {
        $selectedVariants = @($Variants | Where-Object { ($_ -ne 'owned') -eq $historical })
        if ($selectedVariants.Count -eq 0) { continue }
        dotnet build $project -c Release -p:SaveDiskSpace=true "-p:SourceRoot=$SourceRoot" "-p:BaselineRoot=$baselineDirectory" "-p:BaselineOnly=$($historical.ToString().ToLowerInvariant())" -warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
        if (!$historical) {
            $channelsDirectory = Join-Path $SourceRoot 'libp2p/Nethermind.Channels'
            if (!(Test-Path -LiteralPath (Join-Path $channelsDirectory 'Nethermind.Channels.csproj'))) {
                $channelsDirectory = Join-Path $SourceRoot 'src/channels/Nethermind.Channels'
            }
            $sourceFiles = Get-ChildItem (Join-Path $channelsDirectory '*.cs')
            $hashes = $sourceFiles | Sort-Object FullName | ForEach-Object {
                "$( (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash ) $([IO.Path]::GetRelativePath($SourceRoot, $_.FullName))"
            }
            $hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'owned-source-hashes.txt')
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
