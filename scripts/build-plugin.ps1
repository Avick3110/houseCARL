#requires -Version 5.1
<#
  build-plugin.ps1 - assemble the houseCARL plugin AND pack the shippable release zip.

  The hand-authored plugin SOURCE lives in plugin/ (tracked); package-root extras (the
  START-HERE note + the local marketplace.json) live in packaging/ (tracked). This script
  regenerates the reflection rulebook, publishes the server framework-dependent (trimming OFF -
  houseCARL is reflection-driven; trimming would strip types = silent coverage loss = cornerstone
  risk), assembles the full shippable tree into dist/, and packs it into release/houseCARL-<ver>.zip.

  Outputs (all gitignored build artifacts, reproducible on demand):
      dist/housecarl/                         the plugin tree (what `claude plugin validate` checks)
      dist/houseCARL-Setup.exe                the no-CLI desktop installer
      dist/START-HERE.txt                     friend-facing note (version stamped from plugin.json)
      dist/.claude-plugin/marketplace.json    CLI-install descriptor (local marketplace)
      release/houseCARL-<ver>.zip             the single shippable zip (dist/ under a houseCARL/ root)

  The version is read from plugin/.claude-plugin/plugin.json (single source of truth) and stamped
  into START-HERE.txt and the zip name.

  After it succeeds, run the validation gate (necessary, not sufficient):
      claude plugin validate ./dist/housecarl --strict

  Reference:
      dev/plans/PLUGIN_BUILD_EXECUTION_2026-06-03.md   (the checklist this implements)
      dev/plans/PLUGIN_PACKAGING_PLAN_2026-06-03.md    (the why / layout / locked decisions)

  NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misreads UTF-8 non-ASCII bytes
  (em-dashes, section signs) as CP1252 and fails to parse.
#>
$ErrorActionPreference = 'Stop'

# ---- paths -----------------------------------------------------------------
$RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PkgRoot      = Join-Path $RepoRoot 'dist'              # package root: ships housecarl/ + houseCARL-Setup.exe + extras
$DistRoot     = Join-Path $PkgRoot 'housecarl'          # the plugin tree (what `claude plugin validate` checks)
$ServerDir    = Join-Path $DistRoot 'server'
$SkillsDir    = Join-Path $DistRoot 'skills'
$PluginSrc    = Join-Path $RepoRoot 'plugin'
$GeneratedDir = Join-Path $RepoRoot 'generated'
$CorpusSrc    = Join-Path $GeneratedDir 'corpus.json'
$RefDir       = Join-Path $RepoRoot '.claude\skills\mutagen-reference\references'
$GenProj      = Join-Path $RepoRoot 'src\housecarl-generator'
$McpProj      = Join-Path $RepoRoot 'src\housecarl-mcp'
$SetupProj    = Join-Path $RepoRoot 'src\housecarl-setup'
$PackagingSrc = Join-Path $RepoRoot 'packaging'        # tracked source for package-root extras
$ReleaseDir   = Join-Path $RepoRoot 'release'          # output dir for the shippable zip (gitignored)
$PluginManifest = Join-Path $PluginSrc '.claude-plugin\plugin.json'   # single source of truth for the version

# the 8 shipped skills (the modlist-authoring cluster was removed; tool-surface skills wait)
$Skills = @('mutagen-reference','papyrus-reference','skypatcher-authoring','spid-authoring','kid-authoring','papyrus-optimization','facegen-diagnostics','dialogue-authoring')

function Step($n,$msg) { Write-Host "`n=== [$n] $msg ===" -ForegroundColor Cyan }

# ---- version (single source of truth: plugin.json) -------------------------
if (-not (Test-Path $PluginManifest)) { throw "plugin manifest not found: $PluginManifest" }
$Version = (Get-Content $PluginManifest -Raw | ConvertFrom-Json).version
if (-not $Version) { throw "could not read 'version' from $PluginManifest" }
Write-Host ("Building houseCARL v{0}" -f $Version) -ForegroundColor Green

# ---- 0. clean --------------------------------------------------------------
# Clean the WHOLE package root, not just dist/housecarl: stale package-root extras (codex/,
# START-HERE.txt, a previous setup exe) would otherwise survive into the zip - a stale nested
# codex/codex/ subtree did exactly that at the 1.2.2 build.
Step '0/10' 'Clean dist/ (package root)'
if (Test-Path $PkgRoot) { Remove-Item $PkgRoot -Recurse -Force }
New-Item -ItemType Directory -Path $DistRoot -Force | Out-Null

# ---- 1. regenerate the rulebook (corpus.json + mutagen-reference shards, in sync by construction)
Step '1/10' 'Regenerate corpus.json (generator)'
# Explicit absolute args make this CWD-independent (Program.cs defaults are relative to CWD).
dotnet run --project $GenProj -c Release -- $GeneratedDir $RefDir
if ($LASTEXITCODE -ne 0) { throw "generator failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $CorpusSrc)) { throw "corpus not produced at $CorpusSrc" }
Write-Host ("corpus.json: {0:N1} MB" -f ((Get-Item $CorpusSrc).Length / 1MB))

# ---- 2. publish the server (framework-dependent; trimming OFF) -------------
# -p:Version stamps the plugin.json version into the exe, which ServerInfo reports over MCP
# (one version home; an unstamped dev build says 0.0.0-dev).
Step '2/10' 'Publish server (Release, win-x64, framework-dependent)'
dotnet publish $McpProj -c Release -r win-x64 --self-contained false -p:Version=$Version -o $ServerDir
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }
$Exe = Join-Path $ServerDir 'housecarl-mcp.exe'
if (-not (Test-Path $Exe)) { throw "server exe not produced at $Exe" }

# strip dev-only appsettings.json (carries absolute dev paths - must not ship)
Get-ChildItem $ServerDir -Filter 'appsettings*.json' -ErrorAction SilentlyContinue | Remove-Item -Force

# ---- 3. corpus beside the exe (the proven #1 requirement) ------------------
# Reads survive without it via a reflection fallback, but writes + type-filtered queries need it.
Step '3/10' 'Copy corpus.json beside the exe'
Copy-Item $CorpusSrc (Join-Path $ServerDir 'corpus.json') -Force

# ---- 4. bundle the 8 skills (exclude evals/ + _CORPUS_STATUS.md; KEEP all .jsonl) ----
Step '4/10' 'Bundle skills'
New-Item -ItemType Directory -Path $SkillsDir -Force | Out-Null
foreach ($s in $Skills) {
  $src = Join-Path $RepoRoot ".claude\skills\$s"
  if (-not (Test-Path $src)) { throw "skill not found: $src" }
  Copy-Item $src (Join-Path $SkillsDir $s) -Recurse -Force
}
# prune dev/QA meta from the copies (index.jsonl + the mutagen shards stay - load-bearing)
Get-ChildItem $SkillsDir -Directory -Recurse -Filter 'evals' | Remove-Item -Recurse -Force
Get-ChildItem $SkillsDir -File -Recurse -Filter '_CORPUS_STATUS.md' | Remove-Item -Force

# ---- 5. plugin source files ------------------------------------------------
Step '5/10' 'Copy plugin files'
Copy-Item (Join-Path $PluginSrc '.claude-plugin') $DistRoot -Recurse -Force   # -> dist/housecarl/.claude-plugin/plugin.json
foreach ($f in @('.mcp.json','LICENSE','THIRD-PARTY-NOTICES.txt','README.md','CHANGELOG.md')) {
  Copy-Item (Join-Path $PluginSrc $f) (Join-Path $DistRoot $f) -Force
}

# ---- 6. package-root extras (START-HERE note + local marketplace.json) -----
# These live in the PACKAGE ROOT (dist/), beside - not inside - dist/housecarl/, so the leak-check's
# excluded-file scan (scoped to dist/housecarl) leaves them out, while the dev-path scan (step 8) still
# covers them. Source: packaging/ (tracked). START-HERE.txt is version-stamped from plugin.json;
# marketplace.json is the local CLI-install descriptor (`claude plugin marketplace add <this folder>`).
Step '6/10' 'Package-root extras (START-HERE.txt + marketplace.json + codex umbrella)'
$startHere = (Get-Content (Join-Path $PackagingSrc 'START-HERE.txt') -Raw) -replace '\{\{VERSION\}\}', $Version
if ($startHere -match '\{\{') { throw "START-HERE.txt has an unresolved {{token}} after substitution" }
[System.IO.File]::WriteAllText((Join-Path $PkgRoot 'START-HERE.txt'), $startHere, (New-Object System.Text.UTF8Encoding($false)))
$MpDir = Join-Path $PkgRoot '.claude-plugin'
New-Item -ItemType Directory -Path $MpDir -Force | Out-Null
Copy-Item (Join-Path $PackagingSrc 'marketplace.json') (Join-Path $MpDir 'marketplace.json') -Force

# Codex-only umbrella skill (the $housecarl entry point for Codex). Ships at the PACKAGE ROOT
# (dist/codex/), beside - not inside - dist/housecarl/, so the Claude install (which copies the
# housecarl/ plugin tree wholesale) never picks it up; only the setup utility's Codex path places it.
$CodexSrc = Join-Path $PluginSrc 'codex'
if (Test-Path $CodexSrc) { Copy-Item $CodexSrc (Join-Path $PkgRoot 'codex') -Recurse -Force }

# ---- 7. publish the setup utility into dist/ (beside the plugin) -----------
# houseCARL-Setup.exe: the no-CLI desktop installer a user double-clicks. It copies the plugin into
# ~/.claude/skills/housecarl/ (desktop auto-loads the skills) and registers the MCP server in
# ~/.claude.json (desktop spawns it per session). It ships in the PACKAGE ROOT (dist\), beside - not
# inside - the housecarl/ plugin tree, so the leak-check (which scans dist\housecarl only) correctly
# leaves it out of scope. Single-file, SELF-CONTAINED (trimmed + compressed): setup must run on a
# machine with no .NET installed at all, so it can preflight-check the two runtimes the
# framework-dependent SERVER needs (.NET Runtime + ASP.NET Core Runtime - separate installers on
# Windows) and say exactly which is missing. Trimming is safe HERE (setup uses only the
# System.Text.Json DOM, no reflection serialization) - the server's trimming ban is untouched.
Step '7/10' 'Publish the setup utility (houseCARL-Setup.exe) into dist/'
dotnet publish $SetupProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $PkgRoot
if ($LASTEXITCODE -ne 0) { throw "setup-utility publish failed (exit $LASTEXITCODE)" }
$SetupExe = Join-Path $PkgRoot 'houseCARL-Setup.exe'
if (-not (Test-Path $SetupExe)) { throw "setup utility not produced at $SetupExe" }
Write-Host ("houseCARL-Setup.exe: {0:N2} MB" -f ((Get-Item $SetupExe).Length / 1MB))

# ---- 8. leak-check ---------------------------------------------------------
Step '8/10' 'Leak-check assembled tree'
$leaks = @()
$leaks += Get-ChildItem $DistRoot -Recurse -Force -Filter 'appsettings*.json'
$leaks += Get-ChildItem $DistRoot -Recurse -Force -Filter '_CORPUS_STATUS.md'
$leaks += Get-ChildItem $DistRoot -Recurse -Force -Directory -Filter 'evals'
if ($leaks.Count -gt 0) {
  $leaks | ForEach-Object { Write-Host "  LEAK (excluded file present): $($_.FullName)" -ForegroundColor Red }
  throw "leak-check failed: excluded files present in dist"
}
# absolute dev paths embedded in any shipped text file (the appsettings class of leak). Scans the whole
# package root (dist/) so START-HERE.txt + marketplace.json are covered too, not just dist/housecarl.
# \\+ matches one-or-more backslashes, so it catches both the single-backslash (md/txt) and double-backslash (json) forms of a Windows user path.
$devPathPatterns = @('C:\\+Users\\+[^\\''/]+', '[A-Z]:\\+Steam')
$pathLeaks = @()
foreach ($tf in (Get-ChildItem $PkgRoot -Recurse -Force -File -Include *.json,*.jsonl,*.md,*.txt,*.yml,*.yaml)) {
  $content = Get-Content $tf.FullName -Raw -ErrorAction SilentlyContinue
  if (-not $content) { continue }
  foreach ($p in $devPathPatterns) {
    if ($content -match $p) { $pathLeaks += "$($tf.FullName)  [matched $p]"; break }
  }
}
if ($pathLeaks.Count -gt 0) {
  $pathLeaks | ForEach-Object { Write-Host "  PATH LEAK: $_" -ForegroundColor Red }
  throw "leak-check failed: absolute dev path embedded in a shipped text file"
}
Write-Host "leak-check clean." -ForegroundColor Green

# ---- 9. pack the shippable zip ---------------------------------------------
# One zip, single 'houseCARL/' root (so unzipping never scatters files), built from dist/ via the
# .NET zip API (Compress-Archive can't set a custom root). Lands in release/ - outside dist/, so it
# never includes itself. FileMode::Create overwrites a same-version zip in place.
Step '9/10' 'Pack release zip'
New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null
$ZipPath = Join-Path $ReleaseDir ("houseCARL-{0}.zip" -f $Version)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
try {
  $zipArchive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($f in (Get-ChildItem $PkgRoot -Recurse -File -Force)) {
      $rel = $f.FullName.Substring($PkgRoot.Length).TrimStart('\')
      $entryName = 'houseCARL/' + ($rel -replace '\\','/')
      [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $f.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
  } finally { $zipArchive.Dispose() }
} finally { $fs.Dispose() }
$zipCheck = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
$ZipEntryCount = $zipCheck.Entries.Count
$zipCheck.Dispose()
$ZipMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 2)
Write-Host ("packed {0} entries -> {1} ({2} MB)" -f $ZipEntryCount, $ZipPath, $ZipMB) -ForegroundColor Green

# ---- 10. summary -----------------------------------------------------------
Step '10/10' 'Summary'
$fileCount  = (Get-ChildItem $DistRoot -Recurse -Force -File).Count
$totalMB    = (Get-ChildItem $DistRoot -Recurse -Force -File | Measure-Object Length -Sum).Sum / 1MB
$skillCount = (Get-ChildItem $SkillsDir -Directory).Count
Write-Host ("houseCARL v{0}" -f $Version)
Write-Host ("Assembled plugin: {0}" -f $DistRoot)
Write-Host ("  files: {0}   size: {1:N1} MB   skills: {2}" -f $fileCount, $totalMB, $skillCount)
Write-Host ("  exe:         {0}" -f (Test-Path $Exe))
Write-Host ("  corpus:      {0}" -f (Test-Path (Join-Path $ServerDir 'corpus.json')))
Write-Host ("  manifest:    {0}" -f (Test-Path (Join-Path $DistRoot '.claude-plugin\plugin.json')))
Write-Host ("  setup util:  {0}   ({1})" -f (Test-Path $SetupExe), (Split-Path $SetupExe -Leaf))
Write-Host ("  start-here:  {0}" -f (Test-Path (Join-Path $PkgRoot 'START-HERE.txt')))
Write-Host ("  marketplace: {0}" -f (Test-Path (Join-Path $PkgRoot '.claude-plugin\marketplace.json')))
Write-Host ("  codex skill: {0}" -f (Test-Path (Join-Path $PkgRoot 'codex\housecarl\SKILL.md')))
Write-Host ("Shippable zip:    {0}  ({1} MB, {2} entries)" -f $ZipPath, $ZipMB, $ZipEntryCount)
Write-Host "`nDONE." -ForegroundColor Green
Write-Host "Next - validation gate (necessary, not sufficient):" -ForegroundColor Yellow
Write-Host ("    claude plugin validate `"{0}`" --strict" -f $DistRoot)
