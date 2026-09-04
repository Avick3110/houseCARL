#requires -Version 5.1
<#
  generate-notices.ps1 - write the component table of THIRD-PARTY-NOTICES.txt from the publish output.

  The notices file claims to list every third-party component the plugin bundles in server/, with an
  exact version. That claim used to be hand-maintained and drifted three times (stale versions, missing
  components, an unattributed Apache-2.0 component). This script derives the list instead: it reads the
  publish output's deps.json, keeps only the packages that actually emitted a DLL beside the exe, resolves
  each to its licence, and rewrites the marked regions of plugin/THIRD-PARTY-NOTICES.txt in place. The
  repo-root copy is then written from the same string, so the two stay byte-identical.

  Everything else in the notices file - the licence texts and the per-component prose - stays hand-authored.
  Only the regions between the

      --- generated: <tag> ---
      --- end generated ---

  markers are written here. Two tags exist: "<licence> components" for a licence's component table, and
  "Mutagen release" for the GPLv3 section 6 corresponding-source line, which names the Mutagen version the
  publish output ships and the commit that release is tagged at.

  Three data files carry what the publish output cannot say:

      packaging/notices-display-names.json        package id -> the name the file lists it under
      packaging/notices-licence-exceptions.json   packages whose NuGet metadata has no licence expression
      packaging/notices-mutagen-commits.json      Mutagen release version -> repository commit

  A shipped DLL that resolves to neither a project nor a known package, a package with no licence
  expression and no exception entry, a licence with no section in the notices file, and a shipped Mutagen
  version with no recorded commit, all stop the build with the component named.

  Run standalone against any publish directory:
      pwsh scripts/generate-notices.ps1 -PublishDir dist/housecarl/server

  NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misreads UTF-8 non-ASCII bytes as CP1252
  and fails to parse.
#>
param(
  [Parameter(Mandatory = $true)][string] $PublishDir,
  [string] $RepoRoot
)
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
if (-not (Test-Path $PublishDir)) { throw "notices generation needs a publish directory, and there is nothing at $PublishDir; publish the server first" }
$PublishDir = (Resolve-Path $PublishDir).Path

$DepsFile     = Join-Path $PublishDir 'housecarl-mcp.deps.json'
$PluginCopy   = Join-Path $RepoRoot 'plugin/THIRD-PARTY-NOTICES.txt'
$RootCopy     = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt'
$NamesFile    = Join-Path $RepoRoot 'packaging/notices-display-names.json'
$ExceptFile   = Join-Path $RepoRoot 'packaging/notices-licence-exceptions.json'
$CommitsFile  = Join-Path $RepoRoot 'packaging/notices-mutagen-commits.json'

foreach ($f in @($DepsFile, $PluginCopy, $RootCopy, $NamesFile, $ExceptFile, $CommitsFile)) {
  if (-not (Test-Path $f)) { throw "notices generation needs a file that is not there: $f" }
}

function Read-JsonFile($path) {
  return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
}
function Get-Prop($obj, $name) {
  $p = $obj.PSObject.Properties[$name]
  if ($p) { return $p.Value }
  return $null
}

$displayNames   = Read-JsonFile $NamesFile
$exceptions     = Read-JsonFile $ExceptFile
$mutagenCommits = Read-JsonFile $CommitsFile

# ---- what the publish actually put beside the exe --------------------------
$shippedDlls = @{}
foreach ($d in (Get-ChildItem $PublishDir -File -Filter '*.dll')) { $shippedDlls[$d.Name.ToLowerInvariant()] = $true }
if ($shippedDlls.Count -eq 0) { throw "no DLLs in the publish directory: $PublishDir" }

$deps = Read-JsonFile $DepsFile
# The RID-specific target is the one that ships; fall back to the portable one.
$targetName = ($deps.targets.PSObject.Properties.Name | Where-Object { $_ -match '/' } | Select-Object -First 1)
if (-not $targetName) { $targetName = ($deps.targets.PSObject.Properties.Name | Select-Object -First 1) }
$target = Get-Prop $deps.targets $targetName

# ---- the NuGet cache the nuspecs are read from -----------------------------
# Ask the toolchain rather than guessing: a nuget.config globalPackagesFolder moves it off the default path.
$NugetCache = ((dotnet nuget locals global-packages --list) | Where-Object { $_ -match 'global-packages:' } | Select-Object -First 1) -replace '^.*global-packages:\s*', ''
if (-not $NugetCache -or -not (Test-Path $NugetCache)) { $NugetCache = $env:NUGET_PACKAGES }
if (-not $NugetCache) { $NugetCache = Join-Path $env:USERPROFILE '.nuget/packages' }
if (-not (Test-Path $NugetCache)) { throw "NuGet package cache not found at $NugetCache; set NUGET_PACKAGES" }

function Get-NuspecFacts($id, $version) {
  $dir = Join-Path $NugetCache ("{0}/{1}" -f $id.ToLowerInvariant(), $version.ToLowerInvariant())
  $nuspec = Join-Path $dir ("{0}.nuspec" -f $id.ToLowerInvariant())
  if (-not (Test-Path $nuspec)) { return $null }
  $xml = [xml][System.IO.File]::ReadAllText($nuspec, [System.Text.Encoding]::UTF8)
  $md = $xml.package.metadata
  $expr = $null
  if ($md.license -and $md.license.type -eq 'expression') { $expr = [string]$md.license.'#text' }
  $url = $null
  if ($md.repository -and $md.repository.url) { $url = [string]$md.repository.url }
  elseif ($md.projectUrl) { $url = [string]$md.projectUrl }
  return [pscustomobject]@{ Licence = $expr; Url = $url }
}

function Format-SourceUrl($url) {
  if (-not $url) { return '' }
  $u = $url -replace '^https?://', ''
  $u = $u -replace '\.git$', ''
  return $u.TrimEnd('/')
}

# ---- resolve every shipped DLL to a component ------------------------------
$components = @()
$claimed = @{}
foreach ($p in $target.PSObject.Properties) {
  $id, $version = $p.Name -split '/', 2
  $files = @()
  foreach ($kind in @('runtime', 'native')) {
    $assets = Get-Prop $p.Value $kind
    if ($assets) { $files += $assets.PSObject.Properties.Name | ForEach-Object { Split-Path $_ -Leaf } }
  }
  $mine = @($files | Where-Object { $shippedDlls.ContainsKey($_.ToLowerInvariant()) })
  if ($mine.Count -eq 0) { continue }
  foreach ($m in $mine) { $claimed[$m.ToLowerInvariant()] = $true }

  $lib = Get-Prop $deps.libraries $p.Name
  # First-party assemblies are ours, not third-party: they belong in no notices file.
  if ($lib -and $lib.type -eq 'project') { continue }

  $facts = Get-NuspecFacts $id $version
  $licence = $null
  if ($facts) { $licence = $facts.Licence }
  $exception = Get-Prop $exceptions $id
  if (-not $licence -and $exception) { $licence = $exception.licence }
  if (-not $licence) {
    throw "$id $version ships but declares no licence expression in its NuGet metadata; record the determined licence in packaging/notices-licence-exceptions.json"
  }
  $name = Get-Prop $displayNames $id
  if (-not $name) { $name = $id }

  $sourceUrl = $null
  if ($facts) { $sourceUrl = Format-SourceUrl $facts.Url }
  # A copyleft component with no source pointer would leave the GPLv3 section 6 claim unbacked.
  if ($licence -like 'GPL*' -and -not $sourceUrl) {
    throw "$id $version is $licence but its NuGet metadata names no repository or project URL, so the notices file cannot point at its corresponding source"
  }

  $components += [pscustomobject]@{
    Id      = $id
    Name    = $name
    Version = $version
    Licence = $licence
    Source  = $sourceUrl
  }
}

$unclaimed = @($shippedDlls.Keys | Where-Object { -not $claimed.ContainsKey($_) })
if ($unclaimed.Count -gt 0) {
  throw ("these DLLs ship but resolve to no package in the publish output: {0}" -f ($unclaimed -join ', '))
}

# ---- the corresponding-source line, from the Mutagen version that ships -----
$mutagenVersions = @($components | Where-Object { $_.Id -like 'Mutagen.*' } | ForEach-Object { $_.Version } | Sort-Object -Unique)
if ($mutagenVersions.Count -eq 0) {
  throw "no Mutagen package ships in $PublishDir, so the corresponding-source line in THIRD-PARTY-NOTICES.txt cannot name a release"
}
if ($mutagenVersions.Count -gt 1) {
  throw ("the publish output ships more than one Mutagen version ({0}), so the corresponding-source line in THIRD-PARTY-NOTICES.txt cannot name one release" -f ($mutagenVersions -join ', '))
}
$mutagenVersion = $mutagenVersions[0]
$mutagenCommit  = Get-Prop $mutagenCommits $mutagenVersion
if (-not $mutagenCommit) {
  throw "Mutagen $mutagenVersion ships but packaging/notices-mutagen-commits.json records no commit for it; add the commit the release is tagged at, from github.com/Mutagen-Modding/Mutagen/releases/tag/$mutagenVersion"
}
$mutagenLine = '    (release {0}; repository commit {1})' -f $mutagenVersion, $mutagenCommit

# ---- render one table per licence ------------------------------------------
function Format-ComponentTable($rows, $withSource) {
  $sorted = $rows | Sort-Object { $_.Name.ToLowerInvariant() }
  $nameWidth = ($sorted | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum + 3
  $verWidth  = ($sorted | ForEach-Object { $_.Version.Length } | Measure-Object -Maximum).Maximum + 3
  $lines = @()
  foreach ($r in $sorted) {
    if ($withSource) {
      $lines += ('  {0}{1}{2}' -f $r.Name.PadRight($nameWidth), $r.Version.PadRight($verWidth), $r.Source).TrimEnd()
    } else {
      $lines += ('  {0}{1}' -f $r.Name.PadRight($nameWidth), $r.Version).TrimEnd()
    }
  }
  return $lines
}

$text = [System.IO.File]::ReadAllText($PluginCopy, [System.Text.Encoding]::UTF8)
$newline = if ($text -match "`r`n") { "`r`n" } else { "`n" }
$lines = $text -split "`r?`n"

$byLicence = @{}
foreach ($g in ($components | Group-Object Licence)) { $byLicence[$g.Name] = $g.Group }

# Walk the file's markers, not the components, so a region whose components all stopped shipping is
# emptied rather than left standing.
$regions = @()
for ($i = 0; $i -lt $lines.Length; $i++) {
  if ($lines[$i] -match '^  --- generated: (.+) ---$') {
    $tag = $Matches[1]
    if ($tag -ne 'Mutagen release' -and $tag -notmatch ' components$') {
      throw "THIRD-PARTY-NOTICES.txt marks a generated region '$tag' that this script does not know how to write"
    }
    $endIdx = [Array]::IndexOf($lines, '  --- end generated ---', $i)
    if ($endIdx -lt 0) { throw "the $tag generated region in THIRD-PARTY-NOTICES.txt has no end marker" }
    $regions += [pscustomobject]@{ Tag = $tag; Begin = $i; End = $endIdx }
  }
}
if (@($regions | Where-Object { $_.Tag -eq 'Mutagen release' }).Count -ne 1) {
  throw "THIRD-PARTY-NOTICES.txt needs exactly one 'Mutagen release' generated region for the corresponding-source line"
}
foreach ($lic in $byLicence.Keys) {
  if (-not ($regions | Where-Object { $_.Tag -eq "$lic components" })) {
    throw "$lic components ship but THIRD-PARTY-NOTICES.txt has no '$lic' section; add the section, its markers and its licence text, then rerun the build"
  }
}
# Last region first, so the earlier regions' line numbers stay valid as the file grows and shrinks.
for ($k = $regions.Count - 1; $k -ge 0; $k--) {
  $r = $regions[$k]
  $body = @()
  if ($r.Tag -eq 'Mutagen release') {
    $body = @($mutagenLine)
  } else {
    $lic = $r.Tag -replace ' components$', ''
    # The source column is only shown for the copyleft components, whose corresponding source has to be findable.
    if ($byLicence.ContainsKey($lic)) { $body = Format-ComponentTable $byLicence[$lic] ($lic -like 'GPL*') }
  }
  $lines = @($lines[0..$r.Begin]) + @($body) + @($lines[$r.End..($lines.Length - 1)])
}

$rendered = ($lines -join $newline)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($PluginCopy, $rendered, $utf8NoBom)
[System.IO.File]::WriteAllText($RootCopy, $rendered, $utf8NoBom)

$changed = ($rendered -ne $text)
Write-Host ("notices: {0} components from {1}{2}" -f $components.Count, $PublishDir, $(if ($changed) { ' (generated regions updated)' } else { '' }))
