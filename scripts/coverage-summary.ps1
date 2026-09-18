<#
.SYNOPSIS
  Turns a Cobertura coverage file into a markdown table for the CI run's job summary.

.DESCRIPTION
  Visibility only. This script never fails a build on a coverage number: it has no threshold and
  no gate, and it exits 0 whenever it could read a report. When it cannot — no report, an
  unparseable one, or one holding neither subject assembly — it says which of those happened in
  one sentence and still exits 0. It never publishes an empty table.

  It keeps the two assemblies a coverage number means something for — housecarl-mcp and
  housecarl-core — and drops the test assembly, the build-time generator, the installer and the
  third-party ones. Line numbers are unioned per FILE, not summed per class, so a file holding
  several classes (or one class split across partials) is counted once.

.PARAMETER Path
  A .cobertura.xml file, or a directory to search for the newest one.

.PARAMETER OutFile
  Where to write the markdown. Defaults to $env:GITHUB_STEP_SUMMARY when set, else stdout.

.PARAMETER Top
  How many per-file rows to print per assembly, worst-covered first. 0 prints every file.
#>
param(
  [Parameter(Mandatory = $true)][string]$Path,
  [string]$OutFile = $env:GITHUB_STEP_SUMMARY,
  [int]$Top = 40
)

$ErrorActionPreference = 'Stop'

$report = $null
if (Test-Path -PathType Leaf $Path) {
  $report = Get-Item $Path
} elseif (Test-Path -PathType Container $Path) {
  $report = Get-ChildItem -Path $Path -Recurse -Filter '*.cobertura.xml' |
            Sort-Object LastWriteTime | Select-Object -Last 1
}
if (-not $report) {
  Write-Host "No .cobertura.xml under '$Path' - nothing to summarise."
  exit 0
}

$subjects = @('housecarl-mcp', 'housecarl-core')

# file -> @{ total; covered }, keyed per assembly.
$byAssembly = [ordered]@{}
try {
  $xml = [xml](Get-Content -LiteralPath $report.FullName -Raw)
} catch {
  Write-Host "Could not parse '$($report.Name)' as Cobertura - nothing to summarise."
  exit 0
}

$seen = @()
foreach ($pkg in $xml.coverage.packages.package) {
  if ($pkg.name) { $seen += $pkg.name }
  if ($subjects -notcontains $pkg.name) { continue }
  # A report can carry more than one package element per assembly, so merge rather than replace.
  if (-not $byAssembly.Contains($pkg.name)) { $byAssembly[$pkg.name] = @{} }
  $files = $byAssembly[$pkg.name]
  foreach ($cls in $pkg.classes.class) {
    $name = $cls.filename
    if (-not $name) { continue }
    # Absolute runner paths -> repo-relative, so the table reads the same locally and in CI.
    $name = ($name -replace '\\', '/')
    if ($name -match '(src/housecarl-[^/]+/.*)$') { $name = $Matches[1] }
    elseif ($name -match '^(housecarl-[^/]+/.*)$') { $name = 'src/' + $Matches[1] }
    if (-not $files.ContainsKey($name)) { $files[$name] = @{} }
    foreach ($line in $cls.lines.line) {
      $n = [int]$line.number
      $hit = ([int]$line.hits) -gt 0
      if ($hit -or -not $files[$name].ContainsKey($n)) { $files[$name][$n] = $hit }
    }
  }
}

$out = [System.Collections.Generic.List[string]]::new()
$out.Add('## Coverage (visibility only, not a gate)')
$out.Add('')
$out.Add("Report: ``$($report.Name)``")
$out.Add('')

# An empty table would read as a real answer. Say what happened instead: which assemblies were
# wanted, and what the report actually held.
$measured = @($byAssembly.Keys | Where-Object { @($byAssembly[$_].Values | ForEach-Object { $_.Values }).Count -gt 0 })
if ($measured.Count -eq 0) {
  $had = if ($seen.Count -gt 0) { ($seen | Sort-Object -Unique) -join ', ' } else { 'no packages at all' }
  $out.Add("No coverage package matched $($subjects -join ' or '). The report contained: $had.")
  $text = $out -join "`n"
  if ($OutFile) { Add-Content -LiteralPath $OutFile -Value $text -Encoding utf8 } else { Write-Host $text }
  exit 0
}

$out.Add('| Assembly | Lines | Covered | % |')
$out.Add('|---|---:|---:|---:|')

foreach ($asm in $byAssembly.Keys) {
  $lines = $byAssembly[$asm].Values | ForEach-Object { $_.Values }
  $total = @($lines).Count
  $covered = @($lines | Where-Object { $_ }).Count
  $pct = if ($total -gt 0) { [math]::Round(100.0 * $covered / $total, 1) } else { 0 }
  $out.Add("| $asm | $total | $covered | $pct% |")
}

foreach ($asm in $byAssembly.Keys) {
  $rows = foreach ($file in $byAssembly[$asm].Keys) {
    $v = $byAssembly[$asm][$file].Values
    $total = @($v).Count
    $covered = @($v | Where-Object { $_ }).Count
    [pscustomobject]@{
      File = $file; Lines = $total; Covered = $covered
      Missed = $total - $covered
      Pct = if ($total -gt 0) { [math]::Round(100.0 * $covered / $total, 1) } else { 0 }
    }
  }
  $rows = $rows | Sort-Object -Property Missed -Descending
  $shown = if ($Top -gt 0) { $rows | Select-Object -First $Top } else { $rows }

  $out.Add('')
  $out.Add("<details><summary>$asm - per file, most uncovered lines first ($(@($shown).Count) of $(@($rows).Count) files)</summary>")
  $out.Add('')
  $out.Add('| File | Lines | Covered | Missed | % |')
  $out.Add('|---|---:|---:|---:|---:|')
  foreach ($r in $shown) { $out.Add("| $($r.File) | $($r.Lines) | $($r.Covered) | $($r.Missed) | $($r.Pct)% |") }
  $out.Add('')
  $out.Add('</details>')
}

$text = $out -join "`n"
if ($OutFile) { Add-Content -LiteralPath $OutFile -Value $text -Encoding utf8 } else { Write-Host $text }
