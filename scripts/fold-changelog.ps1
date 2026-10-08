#requires -Version 5.1
<#
  fold-changelog.ps1 - fold the plugin/changelog.d/ fragments into plugin/CHANGELOG.md.

  Each PR adds its changelog entry as its own file, plugin/changelog.d/<branch>.md (see the README
  there), so open PRs never conflict on one shared section. This script writes them out.

      ./scripts/fold-changelog.ps1 -Version 2.0.5
          Release cut. Writes `## 2.0.5 - <today>` under an empty `## Unreleased`, with any lines
          already under `## Unreleased` and then every fragment in merge order beneath it, and
          deletes the fragments. Commit the result with the release.

      ./scripts/fold-changelog.ps1 -Version Unreleased -OutFile <path>
          What build-plugin.ps1 runs: writes the changelog with every fragment under `## Unreleased`
          to <path>, leaving the repo's CHANGELOG.md and the fragments untouched.

  Merge order is the commit time of the commit that added each fragment (rebase-merge stamps it at
  merge), ties in commit order; a fragment not yet committed comes last.
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Date = (Get-Date -Format 'yyyy-MM-dd'),
  [string]$OutFile
)
$ErrorActionPreference = 'Stop'

$RepoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Changelog = Join-Path $RepoRoot 'plugin\CHANGELOG.md'
$FragDir   = Join-Path $RepoRoot 'plugin\changelog.d'
$Crlf      = "`r`n"

# fragments, keyed by repo-relative path
$frags = @()
if (Test-Path $FragDir) {
  $frags = @(Get-ChildItem $FragDir -File -Filter '*.md' | Where-Object { $_.Name -ne 'README.md' })
}

# commit time and commit order of the latest commit that added each fragment
$added = @{}
$log = & git -C $RepoRoot -c core.quotepath=off log --reverse --diff-filter=A --format='@%ct' --name-only -- 'plugin/changelog.d'
if ($LASTEXITCODE -ne 0) { throw "git log failed in $RepoRoot; run this from a git checkout." }
$ct = 0; $n = 0
foreach ($line in $log) {
  if ($line -match '^@(\d+)$') { $ct = [long]$Matches[1]; continue }
  if ($line) { $n++; $added[$line] = @{ Time = $ct; Order = $n } }
}
$ordered = $frags | Sort-Object `
  @{ Expression = { $k = 'plugin/changelog.d/' + $_.Name; if ($added[$k]) { $added[$k].Time } else { [long]::MaxValue } } }, `
  @{ Expression = { $k = 'plugin/changelog.d/' + $_.Name; if ($added[$k]) { $added[$k].Order } else { [int]::MaxValue } } }, `
  Name

$entries = @()
foreach ($f in $ordered) {
  $body = ([IO.File]::ReadAllText($f.FullName)).Trim() -replace "`r?`n", $Crlf
  if ($body) { $entries += $body }
}

# split the changelog around the Unreleased section
$text  = [IO.File]::ReadAllText($Changelog)
$lines = $text -split "`r?`n"
$head  = [array]::IndexOf($lines, '## Unreleased')
if ($head -lt 0) { throw "$Changelog has no '## Unreleased' heading; add one above the newest release." }
$next = $lines.Count
for ($i = $head + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith('## ')) { $next = $i; break } }
$pending = (($lines[($head + 1)..($next - 1)] -join $Crlf).Trim())
if ($next -eq $head + 1) { $pending = '' }
if ($pending) { $entries = @($pending) + $entries }

if ($Version -eq 'Unreleased') {
  $section = @('## Unreleased', '') + $(if ($entries) { $entries + '' } else { @() })
} else {
  if (-not $entries) { throw "Nothing to fold: plugin/changelog.d has no fragments and '## Unreleased' is empty." }
  $section = @('## Unreleased', '', ('## {0} {1} {2}' -f $Version, [char]0x2014, $Date), '') + $entries + ''
}
$before = if ($head -gt 0) { $lines[0..($head - 1)] } else { @() }
$after  = if ($next -lt $lines.Count) { $lines[$next..($lines.Count - 1)] } else { @() }
$result = (@($before) + $section + @($after)) -join $Crlf

$target = if ($OutFile) { $OutFile } else { $Changelog }
[IO.File]::WriteAllText($target, $result, (New-Object Text.UTF8Encoding $true))
if (-not $OutFile) { $ordered | Remove-Item -Force }
Write-Host ("Folded {0} fragment(s) under '{1}' into {2}" -f @($ordered).Count, $(if ($Version -eq 'Unreleased') { 'Unreleased' } else { $Version }), $target)
