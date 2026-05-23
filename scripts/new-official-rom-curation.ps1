param(
    [string]$RomRoot = "gba_collection",
    [string]$OutputDir = "curated_official_gba",
    [string]$ManifestPath = "docs\official-rom-curation.csv",
    [string]$SummaryPath = "docs\official-rom-curation-summary.md",
    [string]$CompatReport = "compat-retail-full-20260518-0851-3734\cumulative\compat-all.csv",
    [int]$MaxRoms = 300,
    [ValidateSet("HardLink", "Copy", "ManifestOnly")]
    [string]$LinkMode = "HardLink",
    [switch]$IncludeGbaVideo
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-RelativePath {
    param([string]$BasePath, [string]$Path)
    $base = [IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($base.Length)
    }

    return $full
}

function Read-AsciiString {
    param([byte[]]$Bytes, [int]$Offset, [int]$Length)
    if ($Bytes.Length -lt ($Offset + $Length)) { return "" }
    return ([Text.Encoding]::ASCII.GetString($Bytes, $Offset, $Length) -replace "`0", "").Trim()
}

function Read-GbaHeaderBytes {
    param([string]$Path)
    $buffer = New-Object byte[] 192
    try {
        $read = @(Get-Content -LiteralPath $Path -Encoding Byte -TotalCount 192)
    } catch {
        return $null
    }

    for ($i = 0; $i -lt [Math]::Min($buffer.Length, $read.Count); $i++) {
        $buffer[$i] = [byte]$read[$i]
    }
    return $buffer
}

function Get-ArchiveClass {
    param([string]$RelativePath)
    if ($RelativePath -match '\\1 USA - ') { return "retail-usa" }
    if ($RelativePath -match '\\2 Europe - ') { return "retail-europe" }
    if ($RelativePath -match '\\2 Japan - ') { return "retail-japan" }
    if ($RelativePath -match '\\2 Other Regions - ') { return "retail-other-region" }
    if ($RelativePath -match '\\2 GBA Video\\') { return "gba-video" }
    return "excluded"
}

function Test-IsOfficialCandidate {
    param([string]$RelativePath, [bool]$IncludeVideo)
    $archiveClass = Get-ArchiveClass $RelativePath
    if ($archiveClass -eq "gba-video") { return $IncludeVideo }
    if ($archiveClass -notin @("retail-usa", "retail-europe", "retail-japan", "retail-other-region")) { return $false }

    $noisePatterns = @(
        '\\2 Virtual Console\\',
        'Virtual Console',
        '\\2 Unlicensed - ',
        '\\3 Nintendo Game Boy & Game Boy Color\\',
        '\\4 Beta, Prototypes, Revisions\\',
        '\\4 Hacks\\',
        '\\4 Homebrew\\',
        '\\4 Translations\\',
        '\\5 Tools & Service Test Carts\\',
        '\(Hack',
        '\[h',
        '\[t',
        '\(Beta',
        '\(Proto',
        '\(Sample',
        '\(Demo',
        '\(Unl',
        '\(Pirate',
        'Translation'
    )

    foreach ($pattern in $noisePatterns) {
        if ($RelativePath -match $pattern) { return $false }
    }

    return $true
}

function Get-Category {
    param([string]$Name)
    $n = $Name.ToLowerInvariant()
    if ($n -match 'boktai|twisted|topsy|tilt|wario ware - twisted|warioware - twisted') { return "hardware-special" }
    if ($n -match 'pokemon|golden sun|fire emblem|final fantasy|breath of fire|lunar|sword of mana|tactics ogre|riviera|summon night|mario & luigi|mother|shining soul|demikids|medabots|megaman battle network') { return "rpg-adventure" }
    if ($n -match 'zelda|metroid|castlevania|klonoa|kirby|sonic|mario|wario land|astro boy|mega man zero|gunstar|drill dozer|ninja five-o') { return "platform-action" }
    if ($n -match 'advance wars|yu-gi-oh|yugioh|card|chess|monopoly|risk|stratego|super robot|zone of the enders') { return "strategy-card" }
    if ($n -match 'mario kart|f-zero|need for speed|racing|racer|rally|gt advance|v-rally|motocross|atv|car battler') { return "racing-driving" }
    if ($n -match 'tetris|puzzle|puyo|dr\. mario|kuru|chu chu|columns|denki|polarium|super collapse|zoocube|warioware|wario ware') { return "puzzle-arcade" }
    if ($n -match 'street fighter|mortal kombat|tekken|king of fighters|guilty gear|dragon ball|dbz|beyblade|boxing|wrestl|ufc') { return "fighting-combat" }
    if ($n -match 'madden|fifa|baseball|mlb|nba|nfl|nhl|soccer|tennis|golf|tony hawk|skate|snowboard|surf|basketball|football') { return "sports" }
    if ($n -match 'doom|duke nukem|wolfenstein|contra|metal slug|max payne|gta|grand theft|alien|predator|splinter cell|007|bond') { return "action-shooter" }
    if ($n -match 'spongebob|scooby|powerpuff|muppets|disney|nicktoons|crash|spyro|shrek|harry potter|lord of the rings|star wars|simpsons|fairly oddparents|kim possible|teenage mutant') { return "licensed-variety" }
    if ($n -match 'gba video|game boy advance video') { return "gba-video" }
    return "other-retail"
}

function Get-RegionScore {
    param([string]$ArchiveClass)
    switch ($ArchiveClass) {
        "retail-usa" { return 400 }
        "retail-europe" { return 260 }
        "retail-japan" { return 170 }
        "retail-other-region" { return 130 }
        "gba-video" { return 40 }
        default { return 0 }
    }
}

function Get-NormalizedName {
    param([string]$Name)
    $n = [IO.Path]::GetFileNameWithoutExtension($Name).ToLowerInvariant()
    $n = $n -replace '\([^)]*\)', ''
    $n = $n -replace '\[[^]]*\]', ''
    $n = $n -replace '[^a-z0-9]+', ' '
    return $n.Trim()
}

function Get-SafeFileName {
    param([string]$Name)
    $safe = $Name
    foreach ($char in [IO.Path]::GetInvalidFileNameChars()) {
        $safe = $safe.Replace($char, '_')
    }
    return $safe
}

$anchorPatterns = @(
    'pokemon ruby', 'pokemon sapphire', 'pokemon emerald', 'pokemon firered', 'pokemon leafgreen',
    'sonic advance', 'sonic advance 2', 'sonic advance 3',
    'super mario advance', 'mario kart', 'mario & luigi', 'wario ware', 'warioware', 'wario land',
    'metroid fusion', 'metroid zero mission', 'zelda', 'minish cap',
    'advance wars', 'advance wars 2', 'fire emblem', 'golden sun',
    'castlevania', 'circle of the moon', 'harmony of dissonance', 'aria of sorrow',
    'kirby', 'nightmare in dream land', 'amazing mirror',
    'f-zero', 'final fantasy', 'tactics ogre', 'rivieria', 'riviera',
    'boktai', 'yoshi topsy-turvy', 'wario ware - twisted', 'warioware - twisted',
    'doom', 'doom ii', 'duke nukem', 'wolfenstein', 'metal slug', 'contra',
    'grand theft auto', 'banjo-pilot', 'banjo pilot', 'powerpuff', 'muppets', 'scooby-doo', 'spy m uppets',
    'tony hawk', 'need for speed', 'madden', 'fifa', 'yu-gi-oh', 'mega man battle network', 'mega man zero',
    'dr. mario', 'kuru kuru', 'tetris', 'puyo'
)

$categoryQuota = [ordered]@{
    "rpg-adventure" = 42
    "platform-action" = 42
    "action-shooter" = 30
    "strategy-card" = 28
    "racing-driving" = 26
    "puzzle-arcade" = 28
    "sports" = 28
    "fighting-combat" = 20
    "licensed-variety" = 34
    "hardware-special" = 8
    "other-retail" = 34
    "gba-video" = 3
}

$rootFullPath = (Resolve-Path -LiteralPath $RomRoot).Path
$compatByPath = @{}
if ($CompatReport -and (Test-Path -LiteralPath $CompatReport)) {
    foreach ($row in Import-Csv -LiteralPath $CompatReport) {
        if (-not $compatByPath.ContainsKey($row.path)) {
            $compatByPath[$row.path] = $row
        }
    }
}

$roms = Get-ChildItem -LiteralPath $rootFullPath -Recurse -Filter *.gba | Sort-Object FullName
$candidates = New-Object System.Collections.Generic.List[object]
$ordinal = 0

foreach ($rom in $roms) {
    $ordinal++
    $relativePath = ConvertTo-RelativePath $rootFullPath $rom.FullName
    if (-not (Test-IsOfficialCandidate $relativePath ([bool]$IncludeGbaVideo))) {
        continue
    }

    $headerBytes = Read-GbaHeaderBytes $rom.FullName
    if ($null -eq $headerBytes) {
        continue
    }
    $compatRow = $null
    if ($compatByPath.ContainsKey($relativePath)) {
        $compatRow = $compatByPath[$relativePath]
    }

    $fileName = $rom.Name
    $archiveClass = Get-ArchiveClass $relativePath
    $category = Get-Category $fileName
    $normalizedName = Get-NormalizedName $fileName
    $title = Read-AsciiString $headerBytes 0xA0 12
    $gameCode = Read-AsciiString $headerBytes 0xAC 4
    $maker = Read-AsciiString $headerBytes 0xB0 2
    $saveType = if ($compatRow -and $compatRow.saveType) { $compatRow.saveType } else { "Unknown" }
    $nameLower = $fileName.ToLowerInvariant()
    $isAnchor = $false
    foreach ($pattern in $anchorPatterns) {
        if ($nameLower.Contains($pattern)) {
            $isAnchor = $true
            break
        }
    }

    $score = Get-RegionScore $archiveClass
    if ($isAnchor) { $score += 1000 }
    if ($saveType -ne "None") { $score += 25 }
    if ($relativePath -match '\(USA') { $score += 70 }
    if ($relativePath -match '\(Europe') { $score += 30 }
    if ($relativePath -match '\(Japan') { $score += 10 }
    if ($relativePath -match '\(Rev ') { $score -= 40 }
    if ($relativePath -match '2 Games|2 in 1|3 Game|3 Games|Double Pack|Twin Pack') { $score -= 35 }

    $candidates.Add([pscustomobject]@{
        ordinal = $ordinal
        selectedOrder = 0
        category = $category
        archiveClass = $archiveClass
        score = $score
        anchor = $isAnchor
        normalizedName = $normalizedName
        title = $title
        gameCode = $gameCode
        makerCode = $maker
        saveType = $saveType
        romSize = $rom.Length
        fileName = $fileName
        relativePath = $relativePath
        fullPath = $rom.FullName
    })
}

$selected = New-Object System.Collections.Generic.List[object]
$selectedNames = New-Object 'System.Collections.Generic.HashSet[string]'
$selectedCodes = New-Object 'System.Collections.Generic.HashSet[string]'

function Add-Selection {
    param([object]$Item)
    if ($selected.Count -ge $MaxRoms) { return }
    $nameKey = $Item.normalizedName
    $codeKey = $Item.gameCode
    if ($codeKey -and $selectedCodes.Contains($codeKey)) { return }
    if ($nameKey -and $selectedNames.Contains($nameKey)) { return }
    $Item.selectedOrder = $selected.Count + 1
    [void]$selected.Add($Item)
    if ($nameKey) { [void]$selectedNames.Add($nameKey) }
    if ($codeKey) { [void]$selectedCodes.Add($codeKey) }
}

foreach ($category in $categoryQuota.Keys) {
    $quota = [Math]::Min($categoryQuota[$category], $MaxRoms - $selected.Count)
    if ($quota -le 0) { break }
    $chosenForCategory = 0
    $pool = $candidates | Where-Object { $_.category -eq $category } | Sort-Object @{ Expression = "score"; Descending = $true }, @{ Expression = "fileName"; Descending = $false }
    foreach ($item in $pool) {
        if ($chosenForCategory -ge $quota) { break }
        $before = $selected.Count
        Add-Selection $item
        if ($selected.Count -gt $before) { $chosenForCategory++ }
    }
}

if ($selected.Count -lt $MaxRoms) {
    $pool = $candidates | Sort-Object @{ Expression = "score"; Descending = $true }, @{ Expression = "fileName"; Descending = $false }
    foreach ($item in $pool) {
        if ($selected.Count -ge $MaxRoms) { break }
        Add-Selection $item
    }
}

$manifestFullPath = Join-Path (Get-Location) $ManifestPath
$summaryFullPath = Join-Path (Get-Location) $SummaryPath
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifestFullPath) | Out-Null

$selected |
    Sort-Object selectedOrder |
    Select-Object selectedOrder, ordinal, category, archiveClass, anchor, title, gameCode, makerCode, saveType, romSize, fileName, relativePath |
    Export-Csv -NoTypeInformation -Path $manifestFullPath

if ($LinkMode -ne "ManifestOnly") {
    $outputFullPath = Join-Path (Get-Location) $OutputDir
    $workspaceFullPath = (Get-Location).Path.TrimEnd('\') + '\'
    $resolvedOutputParent = [IO.Path]::GetFullPath($outputFullPath)
    if (-not $resolvedOutputParent.StartsWith($workspaceFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean output outside workspace: $resolvedOutputParent"
    }

    if (Test-Path -LiteralPath $resolvedOutputParent) {
        Remove-Item -LiteralPath $resolvedOutputParent -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null
    foreach ($item in ($selected | Sort-Object selectedOrder)) {
        $categoryDir = Join-Path $outputFullPath $item.category
        New-Item -ItemType Directory -Force -Path $categoryDir | Out-Null
        $targetName = "{0:D3} - {1}" -f $item.selectedOrder, (Get-SafeFileName $item.fileName)
        $targetPath = Join-Path $categoryDir $targetName
        if (Test-Path -LiteralPath $targetPath) {
            Remove-Item -LiteralPath $targetPath -Force
        }

        if ($LinkMode -eq "HardLink") {
            New-Item -ItemType HardLink -Path $targetPath -Target $item.fullPath | Out-Null
        } else {
            Copy-Item -LiteralPath $item.fullPath -Destination $targetPath
        }
    }
}

$byCategory = $selected | Group-Object category | Sort-Object Name
$byArchiveClass = $selected | Group-Object archiveClass | Sort-Object Name
$bySaveType = $selected | Group-Object saveType | Sort-Object Name

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("# Official GBA ROM Curation")
$summary.Add("")
$summary.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$summary.Add("")
$summary.Add("This is a local emulator-test collection built from the existing archive. It keeps official retail release directories, excludes hacks, mods, translations, prototypes, homebrew, virtual-console injects, tools/service carts, and unlicensed folders, then selects a varied set by category, region, save type, and known emulator stress value.")
$summary.Add("")
$summary.Add("- Selected ROMs: $($selected.Count)")
$summary.Add("- Candidate official ROMs scanned: $($candidates.Count)")
$summary.Add("- Link mode: $LinkMode")
$summary.Add("- Manifest: ``$ManifestPath``")
$summary.Add("- Output folder: ``$OutputDir``")
$summary.Add("")
$summary.Add("## By Category")
$summary.Add("")
$summary.Add("| Category | Count |")
$summary.Add("| --- | ---: |")
foreach ($group in $byCategory) {
    $summary.Add("| $($group.Name) | $($group.Count) |")
}
$summary.Add("")
$summary.Add("## By Archive Class")
$summary.Add("")
$summary.Add("| Archive Class | Count |")
$summary.Add("| --- | ---: |")
foreach ($group in $byArchiveClass) {
    $summary.Add("| $($group.Name) | $($group.Count) |")
}
$summary.Add("")
$summary.Add("## By Save Type")
$summary.Add("")
$summary.Add("| Save Type | Count |")
$summary.Add("| --- | ---: |")
foreach ($group in $bySaveType) {
    $summary.Add("| $($group.Name) | $($group.Count) |")
}
$summary.Add("")
$summary.Add("## Notes")
$summary.Add("")
$summary.Add("- USA releases are preferred when duplicates exist, then Europe, Japan, and other-region official releases.")
$summary.Add("- Multi-packs are allowed but lightly de-prioritized to avoid crowding out single-cart releases.")
$summary.Add("- Known emulator stress titles are deliberately included even when they currently fail, because this collection is for compatibility development.")

$summary | Set-Content -Path $summaryFullPath -Encoding ASCII

Write-Host "Selected $($selected.Count) ROMs from $($candidates.Count) official candidates."
Write-Host "Manifest: $manifestFullPath"
if ($LinkMode -ne "ManifestOnly") {
    Write-Host "Collection: $(Join-Path (Get-Location) $OutputDir)"
}
Write-Host "Summary: $summaryFullPath"
