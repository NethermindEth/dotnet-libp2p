param(
    [string[]] $Variants,
    [string[]] $Scenarios = @('full-block', 'fragmented-waitall', 'small-reads', 'forwarding'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'results'),
    [switch] $BaselineOnly
)

$ErrorActionPreference = 'Stop'
if (!$Variants) {
    $Variants = @('main-listener', 'main-dialer', 'managed')
    if (!$BaselineOnly) { $Variants += 'owned' }
}
if ($BaselineOnly -and $Variants -contains 'owned') { throw 'Owned measurements require the native project.' }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$baselineRevision = '70b6c7321a2471dca985c6d15e8de30d0721c56b'
$baselineDirectory = Join-Path $repoRoot '.worktrees/allocations-pr428'
$project = Join-Path $PSScriptRoot 'Nethermind.Channels.Benchmarks.csproj'

if (!(Test-Path -LiteralPath $baselineDirectory)) {
    $excludeFile = Join-Path $repoRoot '.git/info/exclude'
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
    dotnet build $project -c Release -p:SaveDiskSpace=true "-p:BaselineOnly=$($BaselineOnly.IsPresent.ToString().ToLowerInvariant())" -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
    if ($Variants -contains 'owned') {
        $sourceFiles = Get-ChildItem (Join-Path $repoRoot 'src/channels/Nethermind.Channels/*.cs')
        $hashes = $sourceFiles | Sort-Object FullName | ForEach-Object {
            "$( (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash ) $([IO.Path]::GetRelativePath($repoRoot, $_.FullName))"
        }
        $hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'owned-source-hashes.txt')
    }
    foreach ($variant in $Variants) {
        foreach ($scenario in $Scenarios) {
            Write-Host "Measuring $variant / $scenario"
            $result = & dotnet (Join-Path $PSScriptRoot 'bin/Release/net10.0/Nethermind.Channels.Benchmarks.dll') $variant $scenario $workingRevision
            if ($LASTEXITCODE -ne 0) { throw "Measurement failed: $variant / $scenario" }
            $null = $result | ConvertFrom-Json
            $result | Set-Content -LiteralPath (Join-Path $OutputDirectory "$variant-$scenario.json")
        }
    }
}
finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
