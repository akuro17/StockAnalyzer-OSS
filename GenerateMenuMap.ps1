
$scriptRoot = $PSScriptRoot
$xamlPath = Join-Path $scriptRoot "StockAnalyzer.Avalonia\Views\IndicatorSettingsWindow.axaml"
$outputPath = Join-Path $scriptRoot "Tests\StockAnalyzer.UITests\Helpers\IndicatorMenuMap.cs"

# Read with UTF8
$reader = [System.IO.StreamReader]::new($xamlPath, [System.Text.Encoding]::UTF8)
$content = $reader.ReadToEnd()
$reader.Close()

# Parse XML (ignoring namespaces just in case, though usually fine)
$xml = New-Object System.Xml.XmlDocument
$xml.LoadXml($content)

$dicEntries = @()

function Get-PathString($pathList) {
    # Reverse path logic: Root -> Leaf
    # Original script stored leaf in stack last, then reversed.
    # Here $pathList is Root -> Leaf directly.
    
    $pathItems = $pathList | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
    return $pathItems -join ", "
}

function Recurse-Menu($node, $currentPath) {
    # Match by LocalName to ignore potential empty namespace issues
    if ($node.LocalName -eq "Button") {
        # Keep track of tree position, but Avalonia flyout has no hierarchical Header.
        # Buttons just have Content and CommandParameter.
        $header = $node.GetAttribute("Content")
        $cmd = $node.GetAttribute("Command")
        
        if ($cmd -match "AddIndicatorCommand") {
            $cmdParam = $node.GetAttribute("CommandParameter")
            $key = $header
            
            # Since there is no hierarchy in the Flyout, the path is just the button itself
            $newPath = @( $header )
            
            # Format entry
            $keySaf = $key -replace '"', '\"'
            $pathStr = Get-PathString $newPath
            
            $script:dicEntries += "            { `"$keySaf`", new[] { $pathStr } }"
        }
        
        if ($node.HasChildNodes) {
            foreach ($child in $node.ChildNodes) {
                Recurse-Menu $child $currentPath
            }
        }
    }
    else {
        # Recurse children even if not MenuItem
        if ($node.HasChildNodes) {
            foreach ($child in $node.ChildNodes) {
                Recurse-Menu $child $currentPath
            }
        }
    }
}

# Start recursion from root
Recurse-Menu $xml.DocumentElement @()

# Validate count
Write-Host "Total extracted entries: $($script:dicEntries.Count)"

# Sort entries for niceness
$script:dicEntries = $script:dicEntries | Sort-Object

$entriesBlock = $script:dicEntries -join ",`r`n"

$csharp = @"
using System.Collections.Generic;

namespace StockAnalyzer.UITests.Helpers
{
    public static class IndicatorMenuMap
    {
        public static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {
$entriesBlock
        };
    }
}
"@

# Write with UTF8 BOM
[System.IO.File]::WriteAllText($outputPath, $csharp, [System.Text.Encoding]::UTF8)

Write-Host "Map generated with $($script:dicEntries.Count) entries."
