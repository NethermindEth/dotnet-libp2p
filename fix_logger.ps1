# PowerShell script to fix ambiguous ILogger references
$projectDir = "src\libp2p\Libp2p.Protocols.KadDht"
$files = Get-ChildItem -Path $projectDir -Recurse -Include "*NodeHealthTracker.cs", "*IteratorNodeLookup.cs", "*Kademlia.cs", "*KBucketTree.cs", "*LookupKNearestNeighbour.cs"

foreach ($file in $files) {
    $content = Get-Content -Path $file.FullName -Raw
    
    # Replace ILogger with Microsoft.Extensions.Logging.ILogger
    $newContent = $content -replace "ILogger(\s+\w+)", "Microsoft.Extensions.Logging.ILogger`$1"
    
    # Replace ILogger<> with Microsoft.Extensions.Logging.ILogger<>
    $newContent = $newContent -replace "ILogger<", "Microsoft.Extensions.Logging.ILogger<"
    
    # Also replace any remaining references to the custom ILogger
    $newContent = $newContent -replace "Libp2p\.Protocols\.KadDht\.InternalTable\.Logging\.ILogger", "Microsoft.Extensions.Logging.ILogger"
    
    if ($content -ne $newContent) {
        Set-Content -Path $file.FullName -Value $newContent
        Write-Host "Fixed ILogger references in $($file.Name)"
    }
}

Write-Host "Logger fixes applied" 