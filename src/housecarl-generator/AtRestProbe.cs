using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Option-B AT-REST proof — the product-code complement to <see cref="HandleProbe"/>.
///
/// handle-probe proves the raw MECHANIC (a plain overlay locks the file; Dispose() releases it) but opens its
/// OWN overlays — it never touches the resolver / service / write code Option B actually changed. The risk that
/// remains after handle-probe is a WIRING bug: a session that doesn't dispose, a stray overlay held past a call,
/// an index rebuild that keeps a handle. This probe drives the REAL code path —
///   LoadOrderResolver.Build  →  a read via OverlaySession (GetRecord + ReadFields)  →  a create via
///   WritePatchBuilder.CreateRecords  —  and after EACH step, with no session open, asserts every plugin file is
/// RENAMABLE (and the created patch DELETABLE). A held handle would make the rename throw; renamable = zero
/// handles at rest, the whole point of Option B (and the live MO2 "delete-while-the-server-is-up" gate in miniature).
///
/// SAFE: it never touches real plugin data — it COPIES a few small plugins to a temp dir and works only on the
/// copies. The create is a self-contained (masterless) Keyword, so it needs no master present in the temp set.
///
/// Run: dotnet run --project src/housecarl-generator atrest-probe [nPlugins]   (default 5)
/// </summary>
public static class AtRestProbe
{
    public static int RunProbe(string[] args)
    {
        int n = args.Length > 0 && int.TryParse(args[0], out var v) ? v : 5;

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL atrest-probe — Option B holds ZERO handles at rest (product code)");
        Console.WriteLine("================================================================");

        var all = ResolveProbe.GatherPlugins();
        if (all.Count == 0) { Console.Error.WriteLine("error: no plugins found (check game roots)."); return 1; }

        // Copy the N smallest SUBSTANTIVE plugins (>=20 KB, so they bear real records) to a temp dir — work only on copies.
        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-atrest-probe");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        var smallest = all.Select(p => new FileInfo(p)).Where(fi => fi.Length >= 20_000)
                          .OrderBy(fi => fi.Length).Take(n).ToList();
        if (smallest.Count == 0) { Console.Error.WriteLine("error: no substantive plugins (>=20 KB) found to copy."); return 1; }

        var tempPaths = new List<string>(smallest.Count);
        foreach (var fi in smallest)
        {
            var dst = Path.Combine(tmpDir, fi.Name);
            File.Copy(fi.FullName, dst);
            tempPaths.Add(dst);
        }
        Console.WriteLine($"Working set: {tempPaths.Count} temp copies (smallest substantive plugins):");
        foreach (var fi in smallest) Console.WriteLine($"   {fi.Name}  ({fi.Length / 1024.0:N0} KB)");
        Console.WriteLine();

        bool whileOpenLocked = false; string whileOpenDetail = "(read step not reached)";
        bool readDidFetch = false;

        // ---- STEP 1: BUILD the resolver (index build opens each plugin one at a time, disposes). ----
        using var resolver = LoadOrderResolver.Build(tempPaths);
        Console.WriteLine($"BUILD: {resolver.PluginCount} plugins | {resolver.RecordCount:N0} records | " +
                          $"{resolver.ConflictCount:N0} conflicts | {resolver.LoadFailures.Count} load-failure(s)");
        var afterBuild = RenamableAtRest(tempPaths);
        Console.WriteLine($"   AT-REST after Build      : {(afterBuild.ok ? "RENAMABLE (no handle held) — PASS" : "LOCKED — FAIL")}  {afterBuild.detail}");

        // ---- STEP 2: READ through a session (GetRecord + ReadFields) — the read-floor path. ----
        // Find any winner record (broad type sweep) to read; note its winner plugin so we can prove the file is
        // locked WHILE the session is open and released after.
        var (fk, winnerName) = FirstWinner(resolver);
        if (winnerName is not null)
        {
            using (var session = resolver.OpenSession())
            {
                var body = resolver.GetRecord(session, winnerName, fk);
                if (body is not null)
                {
                    _ = ReadEngine.ReadFields(body, null);                 // materialise (forces a real body read)
                    readDidFetch = true;
                    // WHILE the session is OPEN the winner's file must be LOCKED (proves the session truly opened it).
                    var winnerPath = tempPaths.First(p => string.Equals(Path.GetFileName(p), winnerName, StringComparison.OrdinalIgnoreCase));
                    var (renOk, _) = RenamableAtRest(new[] { winnerPath });
                    whileOpenLocked = !renOk;                              // locked == expected
                    whileOpenDetail = whileOpenLocked
                        ? $"{winnerName} LOCKED while session open (expected)"
                        : $"{winnerName} was RENAMABLE while session open (UNEXPECTED — session didn't hold it)";
                }
            }
        }
        Console.WriteLine($"   READ via session         : {(readDidFetch ? $"fetched {fk} from {winnerName} + read fields" : "no readable record found (skipped)")}");
        Console.WriteLine($"   WHILE session open       : {whileOpenDetail}");
        var afterRead = RenamableAtRest(tempPaths);
        Console.WriteLine($"   AT-REST after read       : {(afterRead.ok ? "RENAMABLE (session released) — PASS" : "LOCKED — FAIL")}  {afterRead.detail}");

        // ---- STEP 3: CREATE through the write path (session opens masters to serialize, disposes). ----
        var rulebook = CorpusRulebook.Load();
        var outPath = Path.Combine(tmpDir, "houseCARL_AtRestProbe.esp");
        var spec = new WritePatchBuilder.CreateSpec
        {
            RecordType = "Keyword",                                        // self-contained → masterless patch (no master needed in the temp set)
            EditorId = "houseCARL_AtRestProbe_KW",
            Edits = Array.Empty<WriteRequest>(),
        };
        var created = WritePatchBuilder.CreateRecords(resolver, rulebook, new[] { spec }, outPath, extend: false);
        Console.WriteLine($"   CREATE via write path    : {(created.Success ? $"wrote {Path.GetFileName(outPath)} ({created.Bytes} b), masters [{string.Join(",", created.Masters)}]" : "FAIL: " + created.Error)}");
        var afterWriteSources = RenamableAtRest(tempPaths);
        Console.WriteLine($"   AT-REST sources after write : {(afterWriteSources.ok ? "RENAMABLE — PASS" : "LOCKED — FAIL")}  {afterWriteSources.detail}");

        // The created patch itself must be deletable at rest (gate #3 — a houseCARL patch the user can move/disable).
        bool patchDeletable = false; string patchDetail = "(not written)";
        if (created.Success && File.Exists(outPath))
        {
            patchDeletable = TryFileOp(() => File.Delete(outPath), out var perr);
            patchDetail = patchDeletable ? "deletable — PASS" : $"LOCKED — FAIL [{perr}]";
        }
        Console.WriteLine($"   AT-REST created patch    : {patchDetail}");

        // ---- VERDICT ----
        Console.WriteLine();
        Console.WriteLine("================================================================");
        bool readPart = !readDidFetch || (whileOpenLocked && afterRead.ok);   // if we read, it must lock-then-release
        bool pass = afterBuild.ok && afterRead.ok && readPart && afterWriteSources.ok
                    && created.Success && patchDeletable;
        Console.WriteLine(pass
            ? "=== PASS — the product code holds ZERO plugin handles at rest. Build, read, and create each open\n" +
              "    plugins only for their own duration and release them; every source + the created patch is\n" +
              "    renamable/deletable at rest. The cleanup-gotcha is fixed through the real code path. ==="
            : "=== FAIL — a step left a handle held (see the LOCKED line above). ===");
        Console.WriteLine($"    build-at-rest:{YN(afterBuild.ok)}  while-open-locked:{YN(!readDidFetch || whileOpenLocked)}  " +
                          $"read-at-rest:{YN(afterRead.ok)}  write-sources-at-rest:{YN(afterWriteSources.ok)}  " +
                          $"create-ok:{YN(created.Success)}  patch-deletable:{YN(patchDeletable)}");
        Console.WriteLine("================================================================");

        try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
        catch (Exception ex) { Console.WriteLine($"   (temp cleanup left {tmpDir}: {ex.GetType().Name} — would itself signal a lingering handle)"); }

        return pass ? 0 : 1;
    }

    /// <summary>The first winner record found over a broad type sweep, with its winning plugin name — any record
    /// will do; we just need a real (FormKey, winner) to drive a read. (default, null) if none found.</summary>
    static (FormKey fk, string? winner) FirstWinner(LoadOrderResolver resolver)
    {
        var types = new[]
        {
            typeof(IKeywordGetter), typeof(IWeaponGetter), typeof(IArmorGetter), typeof(IMiscItemGetter),
            typeof(INpcGetter), typeof(ISpellGetter), typeof(IGlobalGetter), typeof(IGameSettingGetter),
            typeof(IContainerGetter), typeof(ICellGetter),
        };
        foreach (var (fk, _, _) in resolver.WinnerRecordsOfType(types))
            if (resolver.ResolveWinner(fk) is { } w) return (fk, w.WinnerPlugin);
        return (default, null);
    }

    /// <summary>Rename each file away and back; a held handle makes the move throw. Returns whether ALL were
    /// renamable + a detail of any that were locked (best-effort restore if a half-move happened).</summary>
    static (bool ok, string detail) RenamableAtRest(IReadOnlyList<string> paths)
    {
        var locked = new List<string>();
        foreach (var p in paths)
        {
            var tmp = p + ".renamecheck";
            try { File.Move(p, tmp); File.Move(tmp, p); }
            catch (Exception ex)
            {
                locked.Add($"{Path.GetFileName(p)} [{ex.GetType().Name}]");
                try { if (File.Exists(tmp) && !File.Exists(p)) File.Move(tmp, p); } catch { }   // restore if moved-but-not-back
            }
        }
        return (locked.Count == 0, locked.Count == 0 ? "(all)" : "locked: " + string.Join(", ", locked));
    }

    static bool TryFileOp(Action op, out string err)
    {
        try { op(); err = "ok"; return true; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message}"; return false; }
    }

    static string YN(bool b) => b ? "PASS" : "FAIL";
}
