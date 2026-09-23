[CmdletBinding()]
param(
    [ValidateRange(1, 120)][int] $TimeoutSeconds = 30,
    [ValidateRange(100, 2000)][int] $MaximumLeaves = 1000
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$watch = [Diagnostics.Stopwatch]::StartNew()
$documents = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
$leaves = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$parents = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$graph = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$phaseCounts = [Collections.Generic.List[object]]::new()
$script:operations = 0

function Check-Budget {
    $script:operations++
    if ($script:operations -gt 100000 -or $watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        throw 'Roadmap validation exceeded its operation or wall-clock bound.'
    }
}

function Read-Document([string] $RelativePath) {
    Check-Budget
    $full = [IO.Path]::GetFullPath($RelativePath, $root)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Document escapes repository: $RelativePath"
    }
    if ($documents.ContainsKey($full)) { return $documents[$full] }
    if ($documents.Count -ge 128) { throw 'Document read count exceeded 128.' }
    $item = Get-Item -LiteralPath $full
    if ($item.PSIsContainer -or $item.Length -gt 1MB) { throw "Invalid/oversized document: $RelativePath" }
    $value = [IO.File]::ReadAllText($full).Replace("`r`n", "`n")
    if ($value.Split("`n").Count -gt 4096) { throw "Document line limit exceeded: $RelativePath" }
    $documents.Add($full, $value)
    return $value
}

function Status-Marker([string] $Text) {
    $match = [regex]::Match($Text, '^(✅|🚧|⏳|⛔|❌)\s+\S')
    if (-not $match.Success) { throw "Missing status marker: $Text" }
    return $match.Groups[1].Value
}

function Parse-Rows([string] $Text, [string] $Path, [switch] $ParentRows) {
    $result = [Collections.Generic.List[object]]::new()
    $pattern = if ($ParentRows) { '^\|\s*(P[0-6]-[0-9]{2})\s*\|' } else { '^\|\s*(P[0-6]-(?:[0-9]{2}|GATE)\.[0-9]{2})\s*\|' }
    foreach ($line in $Text.Split("`n")) {
        Check-Budget
        if ($line -notmatch $pattern) { continue }
        $cells = @([regex]::Split($line.Trim().Trim('|'), '(?<!\\)\|') | ForEach-Object { $_.Trim() })
        if ($cells.Count -ne 6 -or @($cells | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
            throw "Expected six nonempty columns at $Path : $line"
        }
        $deps = @([regex]::Matches($cells[3], 'P[0-6]-(?:[0-9]{2}|GATE)(?:\.[0-9]{2})?') | ForEach-Object Value | Sort-Object -Unique)
        $result.Add([pscustomobject]@{ Id=$cells[0]; Status=(Status-Marker $cells[1]); Dependencies=$deps; Cells=$cells; Path=$Path })
    }
    return $result.ToArray()
}

function Assert-Equal($First, $Second, [string] $Context) {
    Check-Budget
    if (($First -join "`n") -cne ($Second -join "`n")) { throw "Bilingual mismatch: $Context" }
}

function Check-Language-Pair([string] $English, [string] $Chinese) {
    $en = Read-Document $English
    $zh = Read-Document $Chinese
    Assert-Equal @([regex]::Matches($en, '(?m)^#{1,6} ') | ForEach-Object Value) @([regex]::Matches($zh, '(?m)^#{1,6} ') | ForEach-Object Value) "$English heading levels"
    Assert-Equal @([regex]::Matches($en, '(?ms)^```[^\n]*\n(.*?)^```') | ForEach-Object { $_.Groups[1].Value }) @([regex]::Matches($zh, '(?ms)^```[^\n]*\n(.*?)^```') | ForEach-Object { $_.Groups[1].Value }) "$English fenced commands"
    $enLinks = @([regex]::Matches($en, '\[[^\]\n]*\]\(([^)\n]+)\)') | ForEach-Object { $_.Groups[1].Value.Replace('_zh.md', '.md') } | Sort-Object)
    $zhLinks = @([regex]::Matches($zh, '\[[^\]\n]*\]\(([^)\n]+)\)') | ForEach-Object { $_.Groups[1].Value.Replace('_zh.md', '.md') } | Sort-Object)
    Assert-Equal $enLinks $zhLinks "$English link targets"
}

$mainEn = Read-Document 'ROADMAP.md'
$mainZh = Read-Document 'ROADMAP_zh.md'
$enParents = @(Parse-Rows $mainEn 'ROADMAP.md' -ParentRows)
$zhParents = @(Parse-Rows $mainZh 'ROADMAP_zh.md' -ParentRows)
if ($enParents.Count -ne 68 -or $zhParents.Count -ne 68) { throw 'Expected all 68 original parent IDs.' }
for ($index = 0; $index -lt $enParents.Count; $index++) {
    $parent = $enParents[$index]
    Assert-Equal @($parent.Id, $parent.Status) @($zhParents[$index].Id, $zhParents[$index].Status) 'parent IDs/status'
    Assert-Equal $parent.Dependencies $zhParents[$index].Dependencies "$($parent.Id) parent prerequisites"
    if ($parents.ContainsKey($parent.Id)) { throw "Duplicate parent: $($parent.Id)" }
    $parents.Add($parent.Id, $parent)
}

Check-Language-Pair 'README.md' 'README_zh.md'
Check-Language-Pair 'ROADMAP.md' 'ROADMAP_zh.md'
Check-Language-Pair 'docs/roadmap/README.md' 'docs/roadmap/README_zh.md'
for ($phase = 0; $phase -le 6; $phase++) {
    Check-Budget
    $enPath = "docs/roadmap/P$phase.md"
    $zhPath = "docs/roadmap/P${phase}_zh.md"
    Check-Language-Pair $enPath $zhPath
    $enText = Read-Document $enPath
    $zhText = Read-Document $zhPath
    $english = @(Parse-Rows $enText $enPath)
    $chinese = @(Parse-Rows $zhText $zhPath)
    if ($english.Count -eq 0 -or $english.Count -ne $chinese.Count) { throw "Missing/mismatched leaves in P$phase" }
    for ($index = 0; $index -lt $english.Count; $index++) {
        Check-Budget
        $leaf = $english[$index]
        $other = $chinese[$index]
        Assert-Equal @($leaf.Id, $leaf.Status) @($other.Id, $other.Status) "$enPath leaf IDs/status"
        Assert-Equal $leaf.Dependencies $other.Dependencies "$($leaf.Id) prerequisites"
        Assert-Equal @([regex]::Matches(($leaf.Cells -join ' '), '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value }) @([regex]::Matches(($other.Cells -join ' '), '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value }) "$($leaf.Id) inline contracts/paths"
        if (-not $leaf.Id.StartsWith("P$phase-", [StringComparison]::Ordinal)) { throw "Wrong phase: $($leaf.Id)" }
        if ($leaves.Count -ge $MaximumLeaves -or $leaves.ContainsKey($leaf.Id)) { throw "Duplicate/over-budget leaf: $($leaf.Id)" }
        $group = $leaf.Id.Split('.')[0]
        if ($group -ne "P$phase-GATE" -and -not $parents.ContainsKey($group)) { throw "Unknown parent: $group" }
        if ($leaf.Dependencies -contains $leaf.Id -or $leaf.Dependencies -contains $group -or $leaf.Dependencies -contains "P$phase-GATE") {
            # P5-07 is explicitly evaluated after, and excluded from, the P5 gate.
            if ($group -ne 'P5-07' -or $leaf.Dependencies -contains $leaf.Id -or $leaf.Dependencies -contains $group) {
                throw "Circular self/parent/phase prerequisite: $($leaf.Id)"
            }
        }
        $leaves.Add($leaf.Id, $leaf)
        $graph.Add($leaf.Id, @($leaf.Dependencies))
    }
    $groupIds = @($english | ForEach-Object { $_.Id.Split('.')[0] } | Sort-Object -Unique)
    foreach ($group in $groupIds) {
        $children = @($english | Where-Object { $_.Id.StartsWith($group + '.', [StringComparison]::Ordinal) } | Sort-Object Id)
        for ($index = 0; $index -lt $children.Count; $index++) {
            if ($children[$index].Id -cne ($group + '.' + ($index + 1).ToString('00'))) { throw "Noncontiguous leaf IDs: $group" }
        }
        $anchor = '<a id="' + $group.ToLowerInvariant() + '"></a>'
        if (-not $enText.Contains($anchor) -or -not $zhText.Contains($anchor)) { throw "Missing stable anchor: $group" }
        $graph.Add($group, @($children | ForEach-Object Id))
        if ($parents.ContainsKey($group) -and $parents[$group].Status -eq '✅' -and @($children | Where-Object Status -ne '✅').Count -ne 0) {
            throw "Completed parent has incomplete leaves: $group"
        }
    }
    if (-not $graph.ContainsKey("P$phase-GATE")) { throw "No phase gate: P$phase" }
    $gates = @($english | Where-Object Id -like "P$phase-GATE.*")
    $phaseCounts.Add([pscustomobject][ordered]@{
        phase="P$phase"; parents=@($parents.Keys | Where-Object { $_.StartsWith("P$phase-", [StringComparison]::Ordinal) }).Count
        implementationLeaves=$english.Count-$gates.Count; gateLeaves=$gates.Count; totalLeaves=$english.Count
        complete=@($english | Where-Object Status -eq '✅').Count
        inProgress=@($english | Where-Object Status -eq '🚧').Count
        planned=@($english | Where-Object Status -eq '⏳').Count
    })
}
foreach ($parent in $parents.Keys) {
    Check-Budget
    if (-not $graph.ContainsKey($parent)) { throw "Parent has no breakdown: $parent" }
}

# Topological validation expands parent and phase aggregates into their leaves.
$pending = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
$dependents = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$queue = [Collections.Generic.Queue[string]]::new()
foreach ($node in $graph.Keys) { $dependents.Add($node, [Collections.Generic.List[string]]::new()) }
foreach ($node in $graph.Keys) {
    Check-Budget
    $pending.Add($node, @($graph[$node]).Count)
    foreach ($dependency in $graph[$node]) {
        Check-Budget
        if (-not $graph.ContainsKey($dependency)) { throw "Unknown prerequisite $dependency in $node" }
        $dependents[$dependency].Add($node)
    }
    if ($pending[$node] -eq 0) { $queue.Enqueue($node) }
}
$visited = 0
for ($step = 0; $step -lt $graph.Count -and $queue.Count -gt 0; $step++) {
    Check-Budget
    $node = $queue.Dequeue()
    $visited++
    foreach ($dependent in $dependents[$node]) {
        Check-Budget
        $pending[$dependent]--
        if ($pending[$dependent] -eq 0) { $queue.Enqueue($dependent) }
    }
}
if ($visited -ne $graph.Count) {
    $cycle = @($pending.Keys | Where-Object { $pending[$_] -gt 0 } | Sort-Object | Select-Object -First 20)
    throw ('Cyclic prerequisites (first 20 affected nodes): ' + ($cycle -join ', '))
}

# A completed leaf (including a gate) cannot bypass unfinished prerequisites.
# Aggregate dependencies expand to their children; checking every completed leaf
# also enforces transitive completion without unbounded recursive traversal.
foreach ($leaf in $leaves.Values) {
    Check-Budget
    if ($leaf.Status -ne '✅') { continue }
    foreach ($dependency in $leaf.Dependencies) {
        Check-Budget
        $required = if ($leaves.ContainsKey($dependency)) { @($dependency) } else { @($graph[$dependency]) }
        foreach ($id in $required) {
            Check-Budget
            if ($leaves[$id].Status -ne '✅') {
                throw "Completed leaf $($leaf.Id) has unfinished prerequisite: $id"
            }
        }
    }
}

# Every gate must transitively cover its required parent groups; P5-07 is post-gate.
foreach ($counts in $phaseCounts) {
    $phase = $counts.phase
    $reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $work = [Collections.Generic.Stack[string]]::new()
    $null = $reachable.Add($phase + '-GATE')
    $work.Push($phase + '-GATE')
    for ($step = 0; $step -lt $graph.Count -and $work.Count -gt 0; $step++) {
        Check-Budget
        $node = $work.Pop()
        foreach ($dependency in $graph[$node]) {
            Check-Budget
            if ($reachable.Add($dependency)) { $work.Push($dependency) }
        }
    }
    foreach ($parent in $parents.Keys) {
        if ($parent.StartsWith($phase + '-', [StringComparison]::Ordinal) -and $parent -ne 'P5-07' -and -not $reachable.Contains($parent)) {
            throw "Phase gate omits required parent: $parent"
        }
    }
    foreach ($text in @($mainEn, $mainZh)) {
        $summary = [regex]::Match($text, '(?m)^\| ' + $phase + ' \| ([^|]+) \| ([0-9]+) \| ([0-9]+) \| ([0-9]+) \|')
        if (-not $summary.Success -or [int]$summary.Groups[2].Value -ne $counts.parents -or [int]$summary.Groups[3].Value -ne $counts.implementationLeaves -or [int]$summary.Groups[4].Value -ne $counts.gateLeaves) {
            throw "Phase overview counts do not match detailed leaves: $phase"
        }
        $gateChildren = @($graph[$phase + '-GATE'])
        $allGateLeavesComplete = @($gateChildren | Where-Object { $leaves[$_].Status -ne '✅' }).Count -eq 0
        if (((Status-Marker $summary.Groups[1].Value.Trim()) -eq '✅') -ne $allGateLeavesComplete) {
            throw "Phase completion marker does not match gate leaves: $phase"
        }
    }
}
$null = Read-Document 'docs/p1-gap-matrix.md'

# Validate only links in the maintained roadmap/front-page set. Targets are bounded reads.
$maintainedPaths = @($documents.Keys)
foreach ($path in $maintainedPaths) {
    Check-Budget
    $links = [regex]::Matches($documents[$path], '\[[^\]\n]*\]\(([^)\n]+)\)')
    if ($links.Count -gt 1024) { throw "Link count limit exceeded: $path" }
    foreach ($link in $links) {
        Check-Budget
        $target = $link.Groups[1].Value.Trim('<', '>')
        if ($target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { continue }
        $parts = $target.Split('#', 2)
        $targetPath = if ($parts[0].Length -eq 0) { $path } else { [IO.Path]::GetFullPath($parts[0], [IO.Path]::GetDirectoryName($path)) }
        if (-not $targetPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Link escapes repository: $target" }
        if (-not (Test-Path -LiteralPath $targetPath)) { throw "Broken relative link in $path : $target" }
        if ($parts.Count -eq 2 -and $parts[1] -match '^p[0-6]-(?:[0-9]{2}|gate)$') {
            $content = Read-Document ([IO.Path]::GetRelativePath($root, $targetPath))
            if (-not $content.Contains('<a id="' + $parts[1] + '"></a>')) { throw "Broken task anchor: $target" }
        }
    }
}

[ordered]@{
    schemaVersion=1; status='passed'; purpose='documentation consistency, not runtime acceptance'
    parentCount=$parents.Count; implementationLeafCount=@($leaves.Keys | Where-Object { $_ -notmatch '-GATE\.' }).Count
    gateLeafCount=@($leaves.Keys | Where-Object { $_ -match '-GATE\.' }).Count; totalLeafCount=$leaves.Count
    postGateParent='P5-07'; graphNodes=$graph.Count; graphAcyclic=$true; phases=$phaseCounts.ToArray()
    boundedExecution=[ordered]@{ timeoutSeconds=$TimeoutSeconds; maximumLeaves=$MaximumLeaves; operations=$script:operations; elapsedSeconds=[Math]::Round($watch.Elapsed.TotalSeconds, 3); pid=$PID; command='eng/Test-Roadmap.ps1'; ownedChildProcesses=0; cleanup='no child processes or temporary files created' }
} | ConvertTo-Json -Depth 6
