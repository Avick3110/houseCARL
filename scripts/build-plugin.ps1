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
      dist/codex/skills/housecarl/            the Codex umbrella skill, under a skills/ root
      dist/houseCARL-Setup.exe                the no-CLI desktop installer
      dist/START-HERE.txt                     friend-facing note (version stamped from plugin.json)
      dist/.claude-plugin/marketplace.json    CLI-install descriptor (local marketplace)
      release/houseCARL-<ver>.zip             the single shippable zip (dist/ under a houseCARL/ root)

  The version is read from plugin/.claude-plugin/plugin.json (single source of truth) and stamped
  into START-HERE.txt and the zip name.

  After it succeeds, run the validation gate (necessary, not sufficient):
      claude plugin validate ./dist/housecarl --strict

  -PluginTreeOnly assembles just dist/housecarl (skills + plugin files) and the Codex skill tree,
  runs the skill leak-check over both, and stops: no corpus, no server publish, no setup exe, no
  zip. dist/housecarl is the whole tree `claude plugin validate` reads, and it is what CI runs the
  real validator against; the leak-check runs here so CI runs it too.

  Reference:
      dev/plans/PLUGIN_BUILD_EXECUTION_2026-06-03.md   (the checklist this implements)
      dev/plans/PLUGIN_PACKAGING_PLAN_2026-06-03.md    (the why / layout / locked decisions)

  NOTE: keep this file ASCII-only. Windows PowerShell 5.1 misreads UTF-8 non-ASCII bytes
  (em-dashes, section signs) as CP1252 and fails to parse.
#>
param([switch]$PluginTreeOnly)
$ErrorActionPreference = 'Stop'

# ---- paths -----------------------------------------------------------------
$RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PkgRoot      = Join-Path $RepoRoot 'dist'              # package root: ships housecarl/ + houseCARL-Setup.exe + extras
$DistRoot     = Join-Path $PkgRoot 'housecarl'          # the plugin tree (what `claude plugin validate` checks)
$ServerDir    = Join-Path $DistRoot 'server'
$SkillsDir    = Join-Path $DistRoot 'skills'
$CodexRoot    = Join-Path $PkgRoot 'codex'              # the Codex bundle root (beside, not inside, the plugin tree)
$CodexSkills  = Join-Path $CodexRoot 'skills'           # Codex skill dirs are immediate children of a skills/ root
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

# the 8 shipped skills (the modlist-authoring cluster was removed; facegen-diagnostics became the facegen
# findings family on housecarl_check, with its causes table at docs/facegen.md)
$Skills = @('mutagen-reference','papyrus-reference','skypatcher-authoring','spid-authoring','kid-authoring','dialogue-authoring','open-animation-replacer','skse-plugin-authoring')

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
Step '0/12' 'Clean dist/ (package root)'
if (Test-Path $PkgRoot) { Remove-Item $PkgRoot -Recurse -Force }
New-Item -ItemType Directory -Path $DistRoot -Force | Out-Null

# Steps 1-4 build the SERVER half of the tree. -PluginTreeOnly skips them: nothing the plugin
# validator reads comes out of them, and they are the slow part (corpus reflection + two publishes).
if (-not $PluginTreeOnly) {

  # ---- 1. regenerate the rulebook (corpus.json + mutagen-reference shards, in sync by construction)
  Step '1/12' 'Regenerate corpus.json (generator)'
  # Explicit absolute args make this CWD-independent (Program.cs defaults are relative to CWD).
  dotnet run --project $GenProj -c Release -- $GeneratedDir $RefDir
  if ($LASTEXITCODE -ne 0) { throw "generator failed (exit $LASTEXITCODE)" }
  if (-not (Test-Path $CorpusSrc)) { throw "corpus not produced at $CorpusSrc" }
  Write-Host ("corpus.json: {0:N1} MB" -f ((Get-Item $CorpusSrc).Length / 1MB))

  # ---- 2. publish the server (framework-dependent; trimming OFF) -------------
  # -p:Version stamps the plugin.json version into the exe, which ServerInfo reports over MCP
  # (one version home; an unstamped dev build says 0.0.0-dev).
  Step '2/12' 'Publish server (Release, win-x64, framework-dependent)'
  dotnet publish $McpProj -c Release -r win-x64 --self-contained false -p:Version=$Version -o $ServerDir
  if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }
  $Exe = Join-Path $ServerDir 'housecarl-mcp.exe'
  if (-not (Test-Path $Exe)) { throw "server exe not produced at $Exe" }

  # strip dev-only appsettings.json (carries absolute dev paths - must not ship)
  Get-ChildItem $ServerDir -Filter 'appsettings*.json' -ErrorAction SilentlyContinue | Remove-Item -Force

  # ---- 3. generated notices regions from the publish output ------------------
  # THIRD-PARTY-NOTICES.txt names every third-party component in server/ with its version, and names the
  # Mutagen release its corresponding-source line points at. Both are written here from the publish just
  # made, so neither can drift from what ships; the licence texts stay hand-authored. Both tracked copies
  # (repo root and plugin/) are rewritten from one string, so they stay byte-identical.
  Step '3/12' 'Write the generated notices regions from the publish output'
  & (Join-Path $PSScriptRoot 'generate-notices.ps1') -PublishDir $ServerDir -RepoRoot $RepoRoot

  # ---- 4. corpus beside the exe (the proven #1 requirement) ------------------
  # Reads survive without it via a reflection fallback, but writes + type-filtered queries need it.
  Step '4/12' 'Copy corpus.json beside the exe'
  Copy-Item $CorpusSrc (Join-Path $ServerDir 'corpus.json') -Force
}

# ---- 5. bundle the skills, both trees (exclude evals/ + _CORPUS_STATUS.md; KEEP all .jsonl) ----
# The Codex umbrella skill (the $housecarl entry point for Codex) ships at the PACKAGE ROOT, beside -
# not inside - dist/housecarl/, so the Claude install (which copies the housecarl/ plugin tree
# wholesale) never picks it up; only the setup utility's Codex path places it. Its skill dir is an
# immediate child of a skills/ root (dist/codex/skills/housecarl), the shape Codex accepts. The whole
# plugin/codex tree ships - a loose file at its root lands beside skills/, not inside it.
Step '5/12' 'Bundle skills (plugin tree + Codex umbrella)'
New-Item -ItemType Directory -Path $SkillsDir -Force | Out-Null
foreach ($s in $Skills) {
  $src = Join-Path $RepoRoot ".claude\skills\$s"
  if (-not (Test-Path $src)) { throw "skill not found: $src" }
  Copy-Item $src (Join-Path $SkillsDir $s) -Recurse -Force
}
$CodexSrc = Join-Path $PluginSrc 'codex'
$SkillRoots = @($SkillsDir)   # skill-directory roots: the markdown pointer scan walks these
$LeakRoots  = @($SkillsDir)   # excluded-file scan: wider, so a loose Codex package file is covered too
if (Test-Path $CodexSrc) {
  New-Item -ItemType Directory -Path $CodexSkills -Force | Out-Null
  # the whole plugin/codex tree ships: every directory (hidden ones too) as a skill under skills/, and
  # any loose file at the Codex package root, where a package-level Codex file belongs.
  Get-ChildItem $CodexSrc -Directory -Force | ForEach-Object { Copy-Item $_.FullName $CodexSkills -Recurse -Force }
  Get-ChildItem $CodexSrc -File -Force | ForEach-Object { Copy-Item $_.FullName $CodexRoot -Force }
  $SkillRoots += $CodexSkills
  $LeakRoots  += $CodexRoot
}
# prune dev/QA meta from the copies (index.jsonl + the mutagen shards stay - load-bearing). The eval
# file is evals/evals.json (dev/DECISIONS.md, 2026-09-07, ruling 3); the whole directory is stripped.
foreach ($r in $LeakRoots) {
  Get-ChildItem $r -Directory -Recurse -Filter 'evals' | Remove-Item -Recurse -Force
  Get-ChildItem $r -File -Recurse -Filter '_CORPUS_STATUS.md' | Remove-Item -Force
}

# ---- 6. plugin source files ------------------------------------------------
Step '6/12' 'Copy plugin files'
Copy-Item (Join-Path $PluginSrc '.claude-plugin') $DistRoot -Recurse -Force   # -> dist/housecarl/.claude-plugin/plugin.json
foreach ($f in @('.mcp.json','LICENSE','THIRD-PARTY-NOTICES.txt','README.md','CHANGELOG.md')) {
  Copy-Item (Join-Path $PluginSrc $f) (Join-Path $DistRoot $f) -Force
}

# ---- 7. leak-check the shipped skill trees (excluded files + markdown pointers) ----
# Runs on both trees before the -PluginTreeOnly return, so CI (which assembles with that switch)
# runs it too. Two invariants, from dev/DECISIONS.md 2026-09-07 ruling 2: nothing this script
# excludes may survive into a shipped copy, and every file a shipped markdown file points at must
# exist in that same copy and must not be a file this script excludes. Both pointer forms are read:
# the references/<file> hop, and the bare sibling name that most of the corpus uses. The scan reads
# every .md in the skill, not just SKILL.md - a reference file pointing at a stripped file is the
# same dead link. It matches either slash and any case, and a backslash pointer is itself a defect
# (skill pointers are written with forward slashes). This checks that a pointer RESOLVES; the ruling's
# form half - that a pointer is written as references/<file> - is enforced per skill by the regrade in
# that skill's rewrite wave, not here.
Step '7/12' 'Leak-check skills (excluded files + markdown pointers)'
$skillLeaks = @()
foreach ($r in $LeakRoots) {
  $skillLeaks += Get-ChildItem $r -Recurse -Force -File -Filter '_CORPUS_STATUS.md'
  $skillLeaks += Get-ChildItem $r -Recurse -Force -Directory -Filter 'evals'
}
if ($skillLeaks.Count -gt 0) {
  $skillLeaks | ForEach-Object { Write-Host "  LEAK (excluded file present): $($_.FullName)" -ForegroundColor Red }
  throw "leak-check failed: excluded files present in dist"
}
$pointerFails = @()
$docCount = 0
foreach ($r in $SkillRoots) {
  foreach ($skillDir in (Get-ChildItem $r -Directory)) {
    # every file name this skill ships - what a bare sibling pointer has to resolve to
    $shipped = @{}
    Get-ChildItem $skillDir.FullName -Recurse -Force -File | ForEach-Object { $shipped[$_.Name] = $true }
    foreach ($doc in (Get-ChildItem $skillDir.FullName -Recurse -Force -File -Filter '*.md')) {
      $docCount++
      $rel     = ($doc.FullName.Substring($skillDir.FullName.Length).TrimStart('\')) -replace '\\','/'
      $docText = Get-Content $doc.FullName -Raw
      if (-not $docText) { $pointerFails += ("{0} ships an empty {1}." -f $skillDir.Name, $rel); continue }
      # a references/ or evals/ pointer in any spelling, plus a bare mention of the stripped status note
      $hits  = @([regex]::Matches($docText, '(?<![A-Za-z0-9_./\\-])(?:references|evals)[/\\][A-Za-z0-9_./\\-]+', 'IgnoreCase') | ForEach-Object { $_.Value })
      $hits += @([regex]::Matches($docText, '(?<![A-Za-z0-9_./\\-])_CORPUS_STATUS\.md', 'IgnoreCase') | ForEach-Object { $_.Value })
      foreach ($hit in $hits) {
        $ptr = $hit.TrimEnd('.', ',', ';', ':', ')')
        if ($ptr -match '\\') {
          $pointerFails += ("{0} ({1}) writes {2} with a backslash; skill pointers use forward slashes." -f $skillDir.Name, $rel, $ptr)
          continue
        }
        $leaf = Split-Path $ptr -Leaf
        if ($ptr.EndsWith('/') -or ($leaf -notmatch '\.')) { continue }   # names the directory, not a file
        if ($ptr -like 'evals/*' -or $leaf -eq '_CORPUS_STATUS.md') {
          $pointerFails += ("{0} ({1}) points at {2}, which this script strips from every shipped copy." -f $skillDir.Name, $rel, $ptr)
        } elseif (-not (Test-Path (Join-Path $skillDir.FullName $ptr))) {
          $pointerFails += ("{0} ({1}) points at {2}, which is not in the shipped copy." -f $skillDir.Name, $rel, $ptr)
        }
      }
      # the bare sibling form, the dominant one in the corpus (a plain value-tables.md, no references/ hop):
      # it resolves against every name the skill ships. Only .md and .jsonl are candidates - those are the
      # extensions shipped skill files use, so a bare .json/.ini/.psc name here is a file in the modded game
      # (OAR's config.json, SPID's _DISTR.ini, a vanilla Form.psc), never a pointer at a sibling document.
      foreach ($m in [regex]::Matches($docText, '(?<![A-Za-z0-9_./\\-])[A-Za-z0-9_-]+\.(?:md|jsonl)(?![A-Za-z0-9])', 'IgnoreCase')) {
        if ($m.Value -eq '_CORPUS_STATUS.md') { continue }   # the stripped-file rule above owns this name
        if (-not $shipped.ContainsKey($m.Value)) {
          $pointerFails += ("{0} ({1}) points at {2}, which is not in the shipped copy." -f $skillDir.Name, $rel, $m.Value)
        }
      }
    }
  }
}
if ($pointerFails.Count -gt 0) {
  $pointerFails | Sort-Object -Unique | ForEach-Object { Write-Host "  SKILL DOC: $_" -ForegroundColor Red }
  throw "leak-check failed: a shipped skill markdown file is empty or points at a file that does not ship beside it"
}
Write-Host ("skill leak-check clean ({0} skills, {1} markdown files, across {2} tree(s))." -f ($SkillRoots | ForEach-Object { (Get-ChildItem $_ -Directory).Count } | Measure-Object -Sum).Sum, $docCount, $SkillRoots.Count) -ForegroundColor Green

# The validator's whole input is assembled now. Stop here when only that was asked for.
if ($PluginTreeOnly) {
  $skillCount = (Get-ChildItem $SkillsDir -Directory).Count
  Write-Host ("`nPlugin tree assembled: {0}   skills: {1}" -f $DistRoot, $skillCount) -ForegroundColor Green
  if (Test-Path $CodexSkills) { Write-Host ("Codex skill tree:      {0}" -f $CodexSkills) }
  else                        { Write-Host "Codex skill tree:      (none - plugin/codex not found)" }
  Write-Host "Server, setup utility and zip were skipped (-PluginTreeOnly)."
  return
}

# ---- 8. package-root extras (START-HERE note + local marketplace.json) -----
# These live in the PACKAGE ROOT (dist/), beside - not inside - dist/housecarl/. Step 10 covers them
# both ways: its excluded-file scan and its dev-path scan each walk the whole package root.
# Source: packaging/ (tracked). START-HERE.txt is version-stamped from plugin.json;
# marketplace.json is the local CLI-install descriptor (`claude plugin marketplace add <this folder>`).
Step '8/12' 'Package-root extras (START-HERE.txt + marketplace.json)'
$startHere = (Get-Content (Join-Path $PackagingSrc 'START-HERE.txt') -Raw) -replace '\{\{VERSION\}\}', $Version
if ($startHere -match '\{\{') { throw "START-HERE.txt has an unresolved {{token}} after substitution" }
[System.IO.File]::WriteAllText((Join-Path $PkgRoot 'START-HERE.txt'), $startHere, (New-Object System.Text.UTF8Encoding($false)))
$MpDir = Join-Path $PkgRoot '.claude-plugin'
New-Item -ItemType Directory -Path $MpDir -Force | Out-Null
Copy-Item (Join-Path $PackagingSrc 'marketplace.json') (Join-Path $MpDir 'marketplace.json') -Force

# ---- 9. publish the setup utility into dist/ (beside the plugin) -----------
# houseCARL-Setup.exe: the no-CLI desktop installer a user double-clicks. It copies the plugin into
# ~/.claude/skills/housecarl/ (desktop auto-loads the skills) and registers the MCP server in
# ~/.claude.json (desktop spawns it per session). It ships in the PACKAGE ROOT (dist\), beside - not
# inside - the housecarl/ plugin tree, so a Claude install (which copies that tree wholesale) never
# picks it up. Single-file, SELF-CONTAINED (trimmed + compressed): setup must run on a
# machine with no .NET installed at all, so it can preflight-check the two runtimes the
# framework-dependent SERVER needs (.NET Runtime + ASP.NET Core Runtime - separate installers on
# Windows) and say exactly which is missing. Trimming is safe HERE (setup uses only the
# System.Text.Json DOM, no reflection serialization) - the server's trimming ban is untouched.
Step '9/12' 'Publish the setup utility (houseCARL-Setup.exe) into dist/'
dotnet publish $SetupProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $PkgRoot
if ($LASTEXITCODE -ne 0) { throw "setup-utility publish failed (exit $LASTEXITCODE)" }
$SetupExe = Join-Path $PkgRoot 'houseCARL-Setup.exe'
if (-not (Test-Path $SetupExe)) { throw "setup utility not produced at $SetupExe" }
Write-Host ("houseCARL-Setup.exe: {0:N2} MB" -f ((Get-Item $SetupExe).Length / 1MB))

# ---- 10. leak-check the server half + the whole package root ----------------
# The skill trees were leak-checked at step 7 (excluded files + markdown pointers); this covers what
# only a full build produces: the published server, and every shipped text file under the package
# root. All three excluded-file scans walk the whole package root, so an excluded file that lands
# outside dist/housecarl - a stray copy under .claude-plugin/, or an appsettings.json the setup-utility
# publish at step 9 drops beside houseCARL-Setup.exe - is still caught.
Step '10/12' 'Leak-check assembled tree'
$leaks = @()
$leaks += Get-ChildItem $PkgRoot -Recurse -Force -Filter 'appsettings*.json'
$leaks += Get-ChildItem $PkgRoot -Recurse -Force -File -Filter '_CORPUS_STATUS.md'
$leaks += Get-ChildItem $PkgRoot -Recurse -Force -Directory -Filter 'evals'
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

# ---- 11. pack the shippable zip ---------------------------------------------
# One zip, single 'houseCARL/' root (so unzipping never scatters files), built from dist/ via the
# .NET zip API (Compress-Archive can't set a custom root). Lands in release/ - outside dist/, so it
# never includes itself. FileMode::Create overwrites a same-version zip in place.
Step '11/12' 'Pack release zip'
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

# ---- 12. summary -----------------------------------------------------------
Step '12/12' 'Summary'
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
Write-Host ("  codex skill: {0}" -f (Test-Path (Join-Path $CodexSkills 'housecarl\SKILL.md')))
Write-Host ("Shippable zip:    {0}  ({1} MB, {2} entries)" -f $ZipPath, $ZipMB, $ZipEntryCount)
Write-Host "`nDONE." -ForegroundColor Green
Write-Host "Next - validation gate (necessary, not sufficient):" -ForegroundColor Yellow
Write-Host ("    claude plugin validate `"{0}`" --strict" -f $DistRoot)
