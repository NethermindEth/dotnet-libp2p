# PowerShell script to add using statements to KadDht files
$projectDir = "src\libp2p\Libp2p.Protocols.KadDht"
$files = Get-ChildItem -Path $projectDir -Recurse -Filter "*.cs"

$commonImports = @"
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using Nethermind.Libp2p.Core;
"@

foreach ($file in $files) {
    $content = Get-Content -Path $file.FullName -Raw
    
    # Skip if the file already has most of these imports
    if ($content -match "using Microsoft.Extensions.Logging;" -and 
        $content -match "using Libp2p.Protocols.KadDht.InternalTable.Crypto;") {
        Write-Host "Skipping $($file.Name) - already has imports"
        continue
    }
    
    # Get the first line of the file
    $firstLine = $content -split "`n" | Select-Object -First 1
    
    # Check if it's a comment line
    if ($firstLine -match "^\/\/") {
        # Find the end of the comment block
        $lines = $content -split "`n"
        $insertIndex = 0
        foreach ($i in 0..($lines.Count-1)) {
            if (-not ($lines[$i] -match "^\/\/")) {
                $insertIndex = $i
                break
            }
        }
        
        # Insert imports after comment block
        $newContent = ($lines | Select-Object -First $insertIndex) -join "`n"
        $newContent += "`n`n$commonImports`n`n"
        $newContent += ($lines | Select-Object -Skip $insertIndex) -join "`n"
        
        # Fix namespace if needed
        $newContent = $newContent -replace "namespace Nethermind\.Network\.Discovery\.Kademlia", "namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia"
        
        Set-Content -Path $file.FullName -Value $newContent
        Write-Host "Updated imports for $($file.Name)"
    }
    else {
        # Insert imports at the beginning of the file
        $newContent = "$commonImports`n`n$content"
        
        # Fix namespace if needed
        $newContent = $newContent -replace "namespace Nethermind\.Network\.Discovery\.Kademlia", "namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia"
        
        Set-Content -Path $file.FullName -Value $newContent
        Write-Host "Updated imports for $($file.Name)"
    }
}

Write-Host "Import statements have been added to all files" 