# PowerShell script to fix remaining namespace issues
$projectDir = "src\libp2p\Libp2p.Protocols.KadDht"
$files = Get-ChildItem -Path $projectDir -Recurse -Filter "*.cs"

foreach ($file in $files) {
    $content = Get-Content -Path $file.FullName -Raw
    
    # Replace Nethermind.Network.Discovery.Kademlia namespace with Libp2p.Protocols.KadDht.InternalTable.Kademlia
    if ($content -match "Nethermind\.Network\.Discovery\.Kademlia") {
        $newContent = $content -replace "using Nethermind\.Network\.Discovery\.Kademlia", "using Libp2p.Protocols.KadDht.InternalTable.Kademlia"
        Set-Content -Path $file.FullName -Value $newContent
        Write-Host "Fixed namespace in $($file.Name)"
    }
    
    # Fix ambiguous ILogger references
    if ($content -match "Microsoft\.Extensions\.Logging\.ILogger" -and $content -match "Libp2p\.Protocols\.KadDht\.InternalTable\.Logging\.ILogger") {
        # Remove the custom ILogger and use Microsoft's
        $newContent = $content -replace "using Libp2p\.Protocols\.KadDht\.InternalTable\.Logging;", ""
        $newContent = $newContent -replace "ILogger(?!\<)", "Microsoft.Extensions.Logging.ILogger"
        Set-Content -Path $file.FullName -Value $newContent
        Write-Host "Fixed ambiguous ILogger in $($file.Name)"
    }
}

Write-Host "Namespace fixes applied" 