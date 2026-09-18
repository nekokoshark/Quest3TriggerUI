$ErrorActionPreference = 'Stop'
$gameRoot = if ($args.Count -gt 0) { $args[0] } else { (Resolve-Path "$PSScriptRoot\..\..\..").Path }
$appearanceRoot = Join-Path $gameRoot 'Custom\Atom\Person\Appearance'
$sample = Get-ChildItem -LiteralPath $appearanceRoot -Filter '*.vap' -File |
    Where-Object {
        $content = Get-Content -Raw -LiteralPath $_.FullName
        $content.Contains('"clothing"') -and $content.Contains('"hair"')
    } | Select-Object -First 1
if ($null -eq $sample) { throw 'No appearance fixture with clothing and hair was found.' }

$source = Get-Content -Raw -LiteralPath $sample.FullName
$preset = $source | ConvertFrom-Json
$geometry = $preset.storables | Where-Object id -EQ 'geometry' | Select-Object -First 1
$prefixes = @($geometry.clothing | ForEach-Object {
    if ($_.internalId) { [string]$_.internalId } else { [string]$_.id }
} | Where-Object { $_ } | Select-Object -Unique)
$hairBefore = @($geometry.hair).Count
$beforeCount = @($preset.storables).Count

$geometry.PSObject.Properties.Remove('clothing')
$preset.storables = @($preset.storables | Where-Object {
    $id = [string]$_.id
    $isClothing = $false
    foreach ($prefix in $prefixes) {
        if ($id.StartsWith($prefix, [StringComparison]::Ordinal)) {
            $isClothing = $true
            break
        }
    }
    -not $isClothing
})

$geometryAfter = $preset.storables | Where-Object id -EQ 'geometry' | Select-Object -First 1
$clothingKeyRemoved = -not ($geometryAfter.PSObject.Properties.Name -contains 'clothing')
$dynamicClothingLeft = @($preset.storables | Where-Object {
    $id = [string]$_.id
    @($prefixes | Where-Object { $id.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0
}).Count
$hairAfter = @($geometryAfter.hair).Count
$sourceUntouched = (Get-Content -Raw -LiteralPath $sample.FullName) -ceq $source
$removed = $beforeCount - @($preset.storables).Count
$passed = $clothingKeyRemoved -and $dynamicClothingLeft -eq 0 -and
          $hairBefore -eq $hairAfter -and $sourceUntouched -and $removed -gt 0

Write-Output (
    'APPEARANCE FILTER RESULT=' + $(if ($passed) { 'PASS' } else { 'FAIL' }) +
    "; ClothingPrefixes=$($prefixes.Count); RemovedDynamicStorables=$removed; " +
    "DynamicClothingLeft=$dynamicClothingLeft; HairBefore=$hairBefore; " +
    "HairAfter=$hairAfter; SourceUntouched=$sourceUntouched")
if (-not $passed) { exit 1 }
exit 0
