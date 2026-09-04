# Lists the public and internal members of LoadOrderService that nothing under
# src/housecarl-mcp calls - the members the shipped process does not reach.
#
# It reads identifiers, not types. A call counts when the name is used bare (inside
# the class) or through a receiver this script believes is the service; a use through
# any other receiver, and a use in a comment, is counted apart and printed by -Sites,
# because "WritePatchBuilder.CreateRecords" is not a call to the service's own
# CreateRecords. Overloads share one name, so a name is dead only when every overload
# of it is. Deleting the member and building is the last word.
#
#   scripts/find-unused-service-members.ps1
#   scripts/find-unused-service-members.ps1 -Sites          # with the other-receiver uses
#   scripts/find-unused-service-members.ps1 -All

param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')),
    [string]$Service = 'src/housecarl-mcp/LoadOrderService.cs',
    [string]$Scope = 'src/housecarl-mcp',
    [int]$Threshold = 0,
    [switch]$Sites,
    [switch]$All
)

$servicePath = Join-Path $Root $Service
if (-not (Test-Path -LiteralPath $servicePath)) { throw "not found: $servicePath" }

# --- the declarations: lines indented exactly one level inside the class ---
$declLines = Get-Content -LiteralPath $servicePath
$members = New-Object System.Collections.Generic.List[object]

# The file carries the service's result types after the class, so bound the walk to the
# class body: its opening line, to the first closing brace in column one after it.
$start = ($declLines | Select-String -Pattern '^public sealed class LoadOrderService\b' | Select-Object -First 1).LineNumber
if (-not $start) { throw "class LoadOrderService not found in $Service" }
$end = $declLines.Count
for ($i = $start; $i -lt $declLines.Count; $i++) {
    if ($declLines[$i] -eq '}') { $end = $i; break }
}

for ($i = $start; $i -lt $end; $i++) {
    $line = $declLines[$i]
    if ($line -notmatch '^    (public|internal)\s') { continue }

    $name = $null
    $kind = 'member'
    if ($line -match '\b(class|record|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)') {
        $name = $Matches[2]
        $kind = $Matches[1]
    }
    elseif ($line -match '([A-Za-z_][A-Za-z0-9_]*)\s*\(') {
        # a method or delegate: the name is the identifier the parenthesis touches
        $name = $Matches[1]
        $kind = if ($line -match '\bdelegate\b') { 'delegate' } else { 'method' }
    }
    else {
        # a property or field: cut the initializer or accessor block off first
        $head = ($line -split '=>|\{|=|;')[0]
        $idents = [regex]::Matches($head, '[A-Za-z_][A-Za-z0-9_]*')
        if ($idents.Count -gt 0) {
            $name = $idents[$idents.Count - 1].Value
            $kind = 'property/field'
        }
    }

    if (-not $name) {
        Write-Warning "no name parsed at ${Service}:$($i + 1): $($line.Trim())"
        continue
    }
    if ($name -eq 'LoadOrderService') { continue }   # the constructor

    $members.Add([pscustomobject]@{ Name = $name; Kind = $kind; Line = $i + 1 })
}

# --- the names that hold a service, so a qualified use can be told from a lookalike ---
$scopePath = Join-Path $Root $Scope
$files = @(Get-ChildItem -LiteralPath $scopePath -Recurse -Filter *.cs)
$serviceNames = [System.Collections.Generic.HashSet[string]]::new()
foreach ($n in 'this', 'LoadOrderService') { [void]$serviceNames.Add($n) }
foreach ($file in $files) {
    $whole = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($m in [regex]::Matches($whole, '\bLoadOrderService\??\s+([A-Za-z_][A-Za-z0-9_]*)')) {
        [void]$serviceNames.Add($m.Groups[1].Value)
    }
    foreach ($m in [regex]::Matches($whole, '([A-Za-z_][A-Za-z0-9_]*)\s*=\s*LoadOrderService\.')) {
        [void]$serviceNames.Add($m.Groups[1].Value)
    }
}

# --- every identifier mentioned anywhere in the shipped process, with its receiver ---
$mentions = @{}
$identifier = [regex]'[A-Za-z_][A-Za-z0-9_]*'
$receiverOf = [regex]'([A-Za-z_][A-Za-z0-9_]*)\s*[?!]?\s*\.\s*$'

foreach ($file in $files) {
    $rel = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
    $n = 0
    foreach ($text in [System.IO.File]::ReadLines($file.FullName)) {
        $n++
        $trimmed = $text.TrimStart()
        $isComment = $trimmed.StartsWith('//') -or $trimmed.StartsWith('*') -or $trimmed.StartsWith('/*')
        foreach ($m in $identifier.Matches($text)) {
            $key = $m.Value
            $prefix = $text.Substring(0, $m.Index)
            $receiver = $null
            $r = $receiverOf.Match($prefix)
            if ($r.Success) { $receiver = $r.Groups[1].Value }
            elseif ($prefix -match '\.\s*$') { $receiver = '(expression)' }

            if (-not $mentions.ContainsKey($key)) {
                $mentions[$key] = New-Object System.Collections.Generic.List[object]
            }
            $mentions[$key].Add([pscustomobject]@{
                File = $rel; Line = $n; Comment = $isComment; Receiver = $receiver
                OnService = ($null -eq $receiver) -or $serviceNames.Contains($receiver)
            })
        }
    }
}

$serviceRel = $Service -replace '/', '\'
$rows = foreach ($group in $members | Group-Object Name) {
    $own = @($group.Group.Line)
    $hits = @()
    if ($mentions.ContainsKey($group.Name)) {
        $hits = @($mentions[$group.Name] | Where-Object {
            -not ($_.File -eq $serviceRel -and $own -contains $_.Line)
        })
    }
    $code = @($hits | Where-Object { -not $_.Comment })
    [pscustomobject]@{
        Name      = $group.Name
        Kind      = ($group.Group.Kind | Select-Object -Unique) -join '/'
        Lines     = $own -join ','
        Calls     = @($code | Where-Object { $_.OnService }).Count
        Elsewhere = @($code | Where-Object { -not $_.OnService }).Count
        InProse   = @($hits | Where-Object { $_.Comment }).Count
        Other     = @($code | Where-Object { -not $_.OnService })
    }
}

if ($All) {
    $rows | Sort-Object Calls, Name | Format-Table Name, Kind, Lines, Calls, Elsewhere, InProse -AutoSize
}
else {
    $dead = @($rows | Where-Object { $_.Calls -eq 0 } | Sort-Object Name)
    Write-Output "$($members.Count) public/internal members, $($rows.Count) distinct names, $($dead.Count) uncalled in $Scope"
    $dead | Format-Table Name, Kind, Lines, Elsewhere, InProse -AutoSize
    if ($Sites) {
        foreach ($row in $dead | Where-Object { $_.Elsewhere -gt 0 }) {
            Write-Output "$($row.Name) - same name, other receiver:"
            foreach ($site in $row.Other) { Write-Output "    $($site.File):$($site.Line)  ($($site.Receiver))" }
        }
    }
}
