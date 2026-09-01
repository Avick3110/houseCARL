using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

/// <summary>
/// The reflection-driven write engine: overlay → <c>GetOrAddAsOverride</c> → reflect the property → coerce →
/// set → write with masters, for <b>any</b> record group and along <b>nested</b> paths (substruct, dict-by-key,
/// list element, polymorphic arm). It is blind to which record it edits, which is what makes coverage a property
/// of Mutagen's model rather than of a hand-written per-type map.
///
/// <para>Two contracts every entry point here keeps: a request is validated against the corpus rulebook before
/// anything is mutated, and a path the engine cannot navigate or a value it cannot coerce is refused by name
/// rather than skipped. The default destination is a new mod; the in-place lane is the caller's explicit,
/// consented request, gated above this layer. <c>apply-guard</c> drives all three from the tool surface.</para>
///
/// <para>What the engine writes is proven per KIND by <c>oracle</c> — each cell run twice, through here and
/// through a hand-written typed setter, and required byte-identical.</para>
///
/// <para>The acceptance proof and the build-start diagnostics moved to <c>WriteDiagnosticsProbe</c>
/// (housecarl-generator) with #453. Still here, and still console entry points in a library: the <c>patch</c> /
/// <c>show</c> / <c>condition-patch</c> dev harnesses, and the <c>coerce-audit</c> / <c>coerce-selftest</c>
/// CI probes.</para>
/// </summary>
public static class WriteEngine
{
    // ======================================================================
    //  WAVE 2 — generic real-patch dev harness (concrete edits -> a real .esp).
    //  The embryo of the eventual MCP housecarl_set_field tool: take a concrete request
    //  (source plugin, record type, record id, one-or-more field edits), drive each through
    //  the SAME pre-flight (CorpusRulebook) + engine (GetOrAddAsOverride/ApplyVerb) +
    //  write-with-masters path the proofs use, emit ONE real reviewable .esp carrying all
    //  the edits, leave the source byte-for-byte untouched. Aaron then xEdit-verifies.
    //  Cross-master serialization (follow-up #3) fails LOUD here — never silently wrong (Q3).
    //
    //  Single edit (flags):
    //    patch --source "<plugin>" --type Armor --editorid ArmorIronCuirass \
    //          --path ArmorRating --verb Set --value 30 [--key <dictKey/listIdx>]
    //  Multiple edits into one patch (repeatable --op "Verb|path|key|value", key/value optional):
    //    patch --source "<plugin>" --type Weapon --formkey 0F1AC1:Skyrim.esm \
    //          --op "Set|BasicStats.Damage||20" --op "SetAtIndex|Keywords|0|01E71F:Skyrim.esm"
    //  Locate with EITHER --editorid OR --formkey (012E46:Skyrim.esm). [--name <patch>] [--out <path>]
    //  Sibling `show` resolves a record + prints its fields/keywords (read-to-plan).
    // ======================================================================
    public static int RunPatch(string[] args)
    {
        var f = ParseFlags(args);
        var source = f.GetValueOrDefault("source");
        var type = f.GetValueOrDefault("type");
        if (source is null) { Console.Error.WriteLine("error: missing required --source"); return 1; }
        if (type is null) { Console.Error.WriteLine("error: missing required --type"); return 1; }
        if (!File.Exists(source)) { Console.Error.WriteLine($"error: source plugin not found: {source}"); return 1; }

        var editorid = f.GetValueOrDefault("editorid");
        var formkeyRaw = f.GetValueOrDefault("formkey");
        if (editorid is null && formkeyRaw is null) { Console.Error.WriteLine("error: locate the record with --editorid <EDID> or --formkey <id:Master.esm>"); return 1; }

        static string Label(WriteRequest r) =>
            $"{r.Verb} {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")}{(r.Value is not null ? " = " + r.Value : "")}";

        // Collect the edit(s): one-or-more repeatable --op "Verb|path|key|value" (key/value optional),
        // else the single-edit --path/--verb/--value/--key form. All edits target the same record below.
        var reqs = new List<WriteRequest>();
        var opStrings = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--op", StringComparison.OrdinalIgnoreCase)) opStrings.Add(args[i + 1]);

        if (opStrings.Count > 0)
        {
            foreach (var s in opStrings)
            {
                var parts = s.Split('|');
                if (parts.Length < 2 || parts[1].Trim().Length == 0) { Console.Error.WriteLine($"error: --op '{s}' must be Verb|path[|key[|value]]"); return 1; }
                var v = parts[0].Trim();
                var p = parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var k = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
                var val = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
                if ((v is "Set" or "Add" or "SetAtIndex" or "InsertAtIndex") && val is null) { Console.Error.WriteLine($"error: --op '{s}': verb '{v}' needs a value (Verb|path|key|value)."); return 1; }
                reqs.Add(new WriteRequest { RecordType = type, Path = p, Verb = v, Key = k, Value = val });
            }
        }
        else
        {
            var pathRaw = f.GetValueOrDefault("path");
            if (pathRaw is null) { Console.Error.WriteLine("error: give --path (single edit) or one-or-more --op (multi edit)"); return 1; }
            var verb = f.GetValueOrDefault("verb") ?? "Set";
            var value = f.GetValueOrDefault("value");
            if ((verb is "Set" or "Add" or "SetAtIndex" or "InsertAtIndex") && value is null) { Console.Error.WriteLine($"error: --value is required for verb '{verb}'."); return 1; }
            reqs.Add(new WriteRequest
            {
                RecordType = type,
                Path = pathRaw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Verb = verb,
                Key = f.GetValueOrDefault("key"),
                Value = value,
            });
        }

        var name = f.GetValueOrDefault("name") ?? "Patch";
        var outPath = f.GetValueOrDefault("out");
        if (outPath is null)
        {
            var outDir = Path.GetFullPath(Path.Combine("write-output", "patch"));
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
            Directory.CreateDirectory(outDir);
            outPath = Path.Combine(outDir, name + ".esp");
        }
        else
        {
            outPath = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        }
        // WritePatch ties the output filename to the patch ModKey; keep them in lockstep.
        name = Path.GetFileNameWithoutExtension(outPath);

        Console.WriteLine($"Source plugin: {source}");
        Console.WriteLine($"Target:        {type} {(editorid is not null ? "EDID=" + editorid : "FormKey=" + formkeyRaw)}");
        Console.WriteLine($"Output patch:  {outPath}  (ModKey {name}.esp)");
        Console.WriteLine($"Edits ({reqs.Count}):");
        foreach (var r in reqs) Console.WriteLine($"    {Label(r)}");
        Console.WriteLine();

        var shaBefore = Sha(source);
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE);

        // Resolve the getter interface for the named type — absent => a real coverage gap, never guessed (Q3).
        var iface = typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.I" + type + "Getter");
        if (iface is null)
        {
            Console.Error.WriteLine($"error: unknown record type '{type}' — no I{type}Getter in the Mutagen corpus. If Mutagen models it under another name, surface that; never guess.");
            return 1;
        }

        FormKey? wantFk = null;
        if (formkeyRaw is not null)
        {
            try { wantFk = FormKey.Factory(formkeyRaw); }
            catch (Exception ex) { Console.Error.WriteLine($"error: --formkey '{formkeyRaw}' is not a valid FormKey (expected like 012E46:Skyrim.esm): {ex.Message}"); return 1; }
        }

        var target = sourceMod.EnumerateMajorRecords()
            .FirstOrDefault(r => iface.IsInstanceOfType(r)
                && (wantFk is { } fk ? r.FormKey == fk
                    : string.Equals(r.EditorID, editorid, StringComparison.OrdinalIgnoreCase)));
        if (target is null)
        {
            Console.Error.WriteLine($"error: no {type} with {(editorid is not null ? "EditorID '" + editorid + "'" : "FormKey " + formkeyRaw)} in {Path.GetFileName(source)}.");
            return 1;
        }
        Console.WriteLine($"Resolved:      {target.FormKey} ({target.EditorID ?? "<no editorid>"})");
        foreach (var r in reqs)
            Console.WriteLine($"  before: {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")} = {ReadLeafDisplay(target, r.Path, r.Key)}");
        Console.WriteLine();

        // PRE-FLIGHT every edit (Q3): the writes go through the corpus rulebook first; refuse if ANY rejects, no mutation.
        var rulebook = CorpusRulebook.Load();
        var rejects = new List<string>();
        foreach (var r in reqs) if (rulebook.Validate(r) is { } msg) rejects.Add($"{Label(r)} -> {msg}");
        if (rejects.Count > 0)
        {
            Console.Error.WriteLine("REJECTED by pre-flight (no mutation performed):");
            foreach (var rj in rejects) Console.Error.WriteLine($"  - {rj}");
            return 1;
        }
        Console.WriteLine($"Pre-flight:    ACCEPT (all {reqs.Count})");

        // ENGINE: one override of the record, then apply every edit to it.
        // Nested-group targets (Cell/Placed*/INFO/Navmesh/Landscape) reconstruct their parent chain from the
        // source's link cache; flat-group targets ignore it. One resolution path serves both (wave 3).
        var sourceCache = sourceMod.ToImmutableLinkCache();
        var patchMod = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        IMajorRecord patchRecord;
        try { patchRecord = GenericGetOrAddAsOverride(patchMod, target, sourceCache); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not override {type} {target.FormKey} — {ex.Message}");
            return 1;
        }
        foreach (var r in reqs) ApplyVerb(patchRecord, r);

        // WRITE with masters. Cross-master records (#3) fail LOUD here — named, never a silent wrong patch.
        try { WritePatch(patchMod, sourceMod, outPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"CROSS-MASTER LIMITATION (follow-up #3): could not serialize the patch — {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("A referenced record lives in a master beyond the single source plugin; the current write path knows only one. Pick a single-master target for now, or this needs the cross-master write path (MCP wave).");
            return 2;
        }
        Console.WriteLine($"Wrote patch ({new FileInfo(outPath).Length} bytes).");

        // Re-open the patch: surface its masters (cross-master cleanliness) + read every edited field back.
        var patchBack = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
        var masters = patchBack.ModHeader.MasterReferences.Select(m => m.Master.ToString()).ToList();
        Console.WriteLine($"  masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}");
        var patched = patchBack.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == target.FormKey);
        if (patched is null) { Console.Error.WriteLine("FAIL: edited record not found in the written patch."); return 1; }
        foreach (var r in reqs)
            Console.WriteLine($"  after:  {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")} = {ReadLeafDisplay(patched, r.Path, r.Key)}");

        var sourceUnchanged = shaBefore == Sha(source);
        Console.WriteLine($"  source unchanged: {(sourceUnchanged ? "YES" : "NO")}");
        Console.WriteLine();

        Console.WriteLine(sourceUnchanged
            ? $"=== PATCH WRITTEN — open {Path.GetFileName(outPath)} in xEdit and confirm the {reqs.Count} edit(s) above. Original untouched. ==="
            : "=== FAIL: source changed ===");
        return sourceUnchanged ? 0 : 1;
    }

    /// <summary>Minimal <c>--flag value</c> parser (bare <c>--flag</c> => "true").</summary>
    internal static Dictionary<string, string> ParseFlags(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var k = args[i][2..];
            d[k] = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
        }
        return d;
    }

    /// <summary>Best-effort read of a leaf for before/after display (xEdit is the authority on correctness).</summary>
    static string ReadLeafDisplay(object rec, string[] path, string? key)
    {
        try
        {
            object? current = rec;
            for (int i = 0; i < path.Length - 1; i++)
            {
                var (segName, segKey) = ParseSegment(path[i]);
                var p = ResolveProperty(current!.GetType(), segName);
                if (p is null) return $"(no field {segName})";
                if (segKey is null)
                {
                    current = p.GetValue(current);
                    if (current is null) return "(absent substruct)";
                }
                else current = StepIntoElement(current, p, segName, segKey); // collnav (wave 1) — best-effort display
            }
            var (leafName, _) = ParseSegment(path[^1]);
            var leaf = ResolveProperty(current!.GetType(), leafName);
            if (leaf is null) return $"(no field {leafName})";
            var val = leaf.GetValue(current);
            if (val is null) return "(null)";
            static string Fmt(object? o) =>
                o is null ? "(null)" : o is IFormLinkGetter fl ? fl.FormKey.ToString() : o.ToString() ?? "(null)";
            if (key is not null)
            {
                if (val is System.Collections.IDictionary dd)
                {
                    foreach (System.Collections.DictionaryEntry e in dd)
                        if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                            return Fmt(e.Value);
                    return $"(no key {key})";
                }
                // Mutagen's ExtendedList<T> is IList<T> but not non-generic IList — enumerate to the index.
                if (int.TryParse(key, out var idx) && val is System.Collections.IEnumerable seq)
                {
                    int j = 0;
                    foreach (var item in seq) if (j++ == idx) return Fmt(item);
                    return "(index oob)";
                }
                return "(keyed leaf — inspect in xEdit)";
            }
            return Fmt(val);
        }
        catch (Exception ex) { return $"(unreadable: {ex.Message})"; }
    }

    // ----------------------------------------------------------------------
    //  Read-to-plan: resolve a record and print what's needed to author a
    //  correct write — its FormKey/EditorID, requested --path values, and its
    //  Keywords resolved to editorids (so material keywords + their list index
    //  are visible). Not the read PRODUCT; the minimum read to ground a write.
    //  Reuses the same resolve path as `patch`.
    //    dotnet run --project src/housecarl-generator show \
    //      --source "<plugin>" [--type Weapon] --formkey 0F1AC1:Skyrim.esm \
    //      [--path BasicStats.Damage] [--path BasicStats.Value]
    // ----------------------------------------------------------------------
    public static int RunShow(string[] args)
    {
        var f = ParseFlags(args);
        var source = f.GetValueOrDefault("source");
        if (source is null) { Console.Error.WriteLine("error: --source is required"); return 1; }
        if (!File.Exists(source)) { Console.Error.WriteLine($"error: source plugin not found: {source}"); return 1; }
        var type = f.GetValueOrDefault("type");
        var editorid = f.GetValueOrDefault("editorid");
        var formkeyRaw = f.GetValueOrDefault("formkey");
        if (editorid is null && formkeyRaw is null) { Console.Error.WriteLine("error: locate the record with --editorid or --formkey"); return 1; }

        var paths = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--path", StringComparison.OrdinalIgnoreCase)) paths.Add(args[i + 1]);

        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE);
        Type? iface = type is null ? null : typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.I" + type + "Getter");
        if (type is not null && iface is null) { Console.Error.WriteLine($"error: unknown record type '{type}'"); return 1; }
        FormKey? wantFk = null;
        if (formkeyRaw is not null) { try { wantFk = FormKey.Factory(formkeyRaw); } catch (Exception ex) { Console.Error.WriteLine($"error: bad --formkey '{formkeyRaw}': {ex.Message}"); return 1; } }

        var target = sourceMod.EnumerateMajorRecords()
            .FirstOrDefault(r => (iface is null || iface.IsInstanceOfType(r))
                && (wantFk is { } fk ? r.FormKey == fk : string.Equals(r.EditorID, editorid, StringComparison.OrdinalIgnoreCase)));
        if (target is null) { Console.Error.WriteLine($"error: not found in {Path.GetFileName(source)}"); return 1; }

        var typeName = RecordNaming.StripGetterInterface(PrimaryGetter(target.GetType())?.Name ?? "I?Getter");
        Console.WriteLine($"{typeName}  {target.FormKey}  ({target.EditorID ?? "<no editorid>"})");
        foreach (var p in paths)
            Console.WriteLine($"  {p} = {ReadLeafDisplay(target, p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), null)}");

        if (ReadEngine.KeywordKeys(target) is { } kwKeys)   // the ONE keyword walk (shared)
        {
            var map = new Dictionary<FormKey, string?>();
            foreach (var k in sourceMod.Keywords) map[k.FormKey] = k.EditorID;
            Console.WriteLine("  Keywords:");
            for (int i = 0; i < kwKeys.Count; i++)
            {
                var edid = map.TryGetValue(kwKeys[i], out var n) ? (n ?? "(no edid)") : "(other master / unresolved)";
                Console.WriteLine($"    [{i}] {kwKeys[i]}  {edid}");
            }
        }
        return 0;
    }

    // ======================================================================
    //  WAVE 4 — condition-target write demonstration (condition-patch).
    //  The xEdit lock for the FLOI capability: locate a real FORM-mode condition target in a source plugin,
    //  RE-TARGET it to a different real form THROUGH THE ENGINE (pre-flight rooted at the arm + ApplyVerb -> SetFloi),
    //  and emit ONE reviewable single-master .esp. Aaron opens it in xEdit and confirms the condition now points at
    //  the new target; the original stays byte-for-byte untouched (Q3). The write is ARM-ROOTED — the same engine
    //  entry BuildStruct's nested Sets use; reaching arm sub-fields from a record-root path is the broader arm-breadth
    //  nav surface (reconciles at the final completion sweep), deliberately not this wave.
    //    dotnet run --project src/housecarl-generator condition-patch \
    //        --source "<plugin>" [--target XXXXXX:Plugin.esp] [--out <path>] [--name <patch>]
    // ======================================================================
    public static int RunConditionPatch(string[] args)
    {
        var f = ParseFlags(args);
        // --source is required, as it is on the sibling patch/show harnesses. It used to default to one
        // machine's Steam install, which failed naming a path the caller never typed (#453).
        var source = f.GetValueOrDefault("source");
        if (source is null) { Console.Error.WriteLine("error: missing required --source"); return 1; }
        if (!File.Exists(source)) { Console.Error.WriteLine($"error: source plugin not found: {source}"); return 1; }

        var shaBefore = Sha(source);
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE);
        var cache = sourceMod.ToImmutableLinkCache();

        // Scan for the first FORM-mode condition target (UseAliases=UsePackageData=false, a populated FormKey) —
        // form mode reads cleanest in xEdit (a real object). Record its owner + condition field + index + arm + prop.
        IMajorRecordGetter? owner = null;
        string condField = ""; int condIndex = -1; string armCatalog = "", floiProp = "";
        FormKey oldTarget = default; Type? linkedT = null;
        foreach (var rec in sourceMod.EnumerateMajorRecords())
        {
            foreach (var (cond, field, idx) in ConditionsOf(rec))
            {
                var data = cond.GetType().GetProperty("Data")?.GetValue(cond);
                if (data is null) continue;
                if ((bool)(data.GetType().GetProperty("UseAliases")?.GetValue(data) ?? false)) continue;
                if ((bool)(data.GetType().GetProperty("UsePackageData")?.GetValue(data) ?? false)) continue;
                foreach (var fp in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsFormLinkOrIndex(fp.PropertyType)) continue;
                    if (ReadFloiFormKey(fp.GetValue(data)) is not { } got || got.IsNull) continue;
                    owner = rec; condField = field; condIndex = idx;
                    armCatalog = RecordNaming.StripOverlay(data.GetType().Name); floiProp = fp.Name; oldTarget = got;
                    linkedT = fp.PropertyType.GetGenericArguments().FirstOrDefault();
                    break;
                }
                if (owner is not null) break;
            }
            if (owner is not null) break;
        }
        if (owner is null) { Console.Error.WriteLine($"error: no populated form-mode condition target found in {Path.GetFileName(source)}."); return 1; }

        // New target: --target, else the first real record of the linked type that differs from the current one (so
        // xEdit resolves it to a sensible same-type name), else the Player ref as a last resort.
        var newTarget = f.GetValueOrDefault("target") ?? PickSameTypeTarget(sourceMod, linkedT, oldTarget) ?? "000014:Skyrim.esm";

        var name = f.GetValueOrDefault("name") ?? "houseCARL_ConditionPatch";
        var outPath = f.GetValueOrDefault("out");
        if (outPath is null)
        {
            var outDir = Path.GetFullPath(Path.Combine("write-output", "condition-patch"));
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
            Directory.CreateDirectory(outDir);
            outPath = Path.Combine(outDir, name + ".esp");
        }
        else { outPath = Path.GetFullPath(outPath); Directory.CreateDirectory(Path.GetDirectoryName(outPath)!); }
        name = Path.GetFileNameWithoutExtension(outPath);   // WritePatch ties the output filename to the patch ModKey

        var ownerCatalog = RecordNaming.StripGetterInterface(PrimaryGetter(owner.GetType())?.Name ?? "I?Getter");
        Console.WriteLine($"Source plugin: {source}");
        Console.WriteLine($"Output patch:  {outPath}  (ModKey {name}.esp)");
        Console.WriteLine($"Condition:     {ownerCatalog} {owner.FormKey} ({owner.EditorID ?? "<no editorid>"}) . {condField}[{condIndex}] . {armCatalog}.{floiProp}");
        Console.WriteLine($"Re-target:     {oldTarget}  ->  {newTarget}   (form mode)");
        Console.WriteLine();

        // PRE-FLIGHT rooted at the arm catalog — proves the rulebook now ACCEPTS an FLOI target; refuse if rejected (Q3).
        var req = new WriteRequest { RecordType = armCatalog, Path = new[] { floiProp }, Verb = "Set", Value = newTarget };
        var rulebook = CorpusRulebook.Load();
        if (rulebook.Validate(req) is { } reject) { Console.Error.WriteLine($"FAIL: pre-flight rejected the condition-target write: {reject}"); return 1; }
        Console.WriteLine("Pre-flight:    ACCEPT");

        // ENGINE: override the owner, navigate to the mutable arm, drive ApplyVerb -> SetFloi, write with masters.
        var patchMod = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        IMajorRecord ov;
        try { ov = GenericGetOrAddAsOverride(patchMod, owner, cache); }
        catch (Exception ex) { Console.Error.WriteLine($"error: could not override {ownerCatalog} {owner.FormKey} — {ex.Message}"); return 1; }
        object arm;
        try { arm = NavigateToConditionArm(ov, condField, condIndex); }
        catch (Exception ex) { Console.Error.WriteLine($"error: could not navigate to the condition arm — {ex.Message}"); return 1; }
        ApplyVerb(arm, req);

        try { WritePatch(patchMod, sourceMod, outPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CROSS-MASTER LIMITATION: could not serialize — {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("Pick a single-master record, or this needs the cross-master write path (MCP wave).");
            return 2;
        }
        Console.WriteLine($"Wrote patch ({new FileInfo(outPath).Length} bytes).");

        // Reopen + read the new target back off the written patch; confirm masters + source untouched.
        var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
        var masters = back.ModHeader.MasterReferences.Select(m => m.Master.ToString()).ToList();
        var patched = back.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == owner.FormKey);
        FormKey? readBack = null;
        if (patched is not null)
            foreach (var (cond, _, idx) in ConditionsOf(patched))
                if (idx == condIndex)
                {
                    var d = cond.GetType().GetProperty("Data")?.GetValue(cond);
                    readBack = ReadFloiFormKey(d is null ? null : d.GetType().GetProperty(floiProp)?.GetValue(d));
                    break;
                }

        var sourceUnchanged = shaBefore == Sha(source);
        Console.WriteLine($"  masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}");
        Console.WriteLine($"  target read back from patch: {readBack?.ToString() ?? "(unread)"}");
        Console.WriteLine($"  source unchanged: {(sourceUnchanged ? "YES" : "NO")}");
        Console.WriteLine();

        var ok = sourceUnchanged && readBack is { } rb && !rb.IsNull;
        Console.WriteLine(ok
            ? $"=== CONDITION PATCH WRITTEN — open {Path.GetFileName(outPath)} in xEdit: {ownerCatalog} {owner.FormKey}, condition #{condIndex}, confirm the target is now {newTarget}. Original untouched. ==="
            : "=== FAIL — see above ===");
        return ok ? 0 : 1;
    }

    /// <summary>Yield (condition, owning-field-name, index) for every condition on a record — conditions live in a
    /// list property whose element is IConditionGetter (the same shape the wave-4 scout used).</summary>
    internal static IEnumerable<(IConditionGetter cond, string field, int index)> ConditionsOf(IMajorRecordGetter rec)
    {
        foreach (var p in rec.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            object? coll; try { coll = p.GetValue(rec); } catch { continue; }
            if (coll is not System.Collections.IEnumerable e || coll is string) continue;
            int i = 0;
            foreach (var item in e) { if (item is IConditionGetter c) yield return (c, p.Name, i); i++; }
        }
    }

    /// <summary>Read the form-mode FormKey off a live FormLinkOrIndex via its <c>.Link</c> (an IFormLinkGetter);
    /// null when unset or in index mode.</summary>
    internal static FormKey? ReadFloiFormKey(object? floi)
    {
        if (floi is null) return null;
        var link = floi.GetType().GetProperty("Link")?.GetValue(floi);
        return link is IFormLinkGetter fl && !fl.FormKey.IsNull ? fl.FormKey : (FormKey?)null;
    }

    /// <summary>Navigate a mutable override to the mutable ConditionData arm at <paramref name="field"/>[<paramref name="index"/>].Data.
    /// Enumerates to the index (Mutagen's ExtendedList is not reliably non-generic IList); the yielded element is the
    /// in-list reference, so mutating its arm mutates the override.</summary>
    internal static object NavigateToConditionArm(IMajorRecord ov, string field, int index)
    {
        var list = ResolveProperty(ov.GetType(), field)?.GetValue(ov)
            ?? throw new InvalidOperationException($"No condition list '{field}' on the override.");
        int i = 0; object? cond = null;
        foreach (var item in (System.Collections.IEnumerable)list) { if (i++ == index) { cond = item; break; } }
        if (cond is null) throw new InvalidOperationException($"Condition #{index} not found in '{field}'.");
        return cond.GetType().GetProperty("Data")?.GetValue(cond)
            ?? throw new InvalidOperationException($"Condition #{index} has no Data arm.");
    }

    /// <summary>Pick a real record of the FLOI's linked type (so xEdit resolves the new target to a sensible same-type
    /// name) whose FormKey differs from the current target; null if the type can't be sampled here.</summary>
    static string? PickSameTypeTarget(ISkyrimModGetter mod, Type? linkedGetter, FormKey current)
    {
        if (linkedGetter is null) return null;
        foreach (var rec in mod.EnumerateMajorRecords())
            if (linkedGetter.IsInstanceOfType(rec) && rec.FormKey != current && !rec.FormKey.IsNull)
                return rec.FormKey.ToString();
        return null;
    }

    // ======================================================================
    //  GENERIC PATCH-MOD LIFECYCLE  (plan §4.1; step-1 confirm done via write-api)
    // ======================================================================

    /// <summary>True iff <paramref name="source"/> lives in a NESTED group (no flat <c>SkyrimGroup&lt;T&gt;</c>) and so
    /// needs the source link cache to reconstruct its parent chain when overridden (Cell / the Placed* family / INFO /
    /// Navmesh / Landscape). Lets the write cleave build the COSTLY per-overlay link cache ONLY for nested records and
    /// never for the flat common case (<see cref="LoadOrderResolver.LinkCacheFor"/> is seconds + GBs). Mirrors
    /// <see cref="TryResolveGroup"/>'s flat test off the SAME <see cref="EnumerateFlatGroups"/> enumeration (no drift),
    /// without needing a patch mod in hand.</summary>
    public static bool RecordNeedsSourceCache(IMajorRecordGetter source)
    {
        foreach (var (_, getterIface) in FlatGroupTypes)
            if (getterIface.IsInstanceOfType(source)) return false;
        return true;
    }

    // ======================================================================
    //  CHILD-GROUP PRESERVATION ACROSS A DROP-THEN-COPY  (#324)
    // ======================================================================

    /// <summary>
    /// The child records a record OWNS, lifted off it so a drop-then-copy can put them back (#324).
    /// <para/>
    /// <b>Why this exists.</b> Both <c>forward</c> lanes replace a FormKey the destination already carries by
    /// <c>Remove</c>-ing the whole record and then copying the source body in (the F1 semantic — a collision must not
    /// silently SKIP the copy, HCBR-2026-07-08-01). The drop takes the record's child GROUP with it, and
    /// <see cref="GenericGetOrAddAsOverride"/> carries none back in, so every child under the replaced record was
    /// silently destroyed while the call reported success: INFOs under a DIAL, placed refs under a CELL. Measured on
    /// PR #323 and filed as #324; the <c>in_place=</c> half deleted them out of the caller's OWN plugin.
    /// <para/>
    /// <b>There is nothing to reconcile.</b> The copy brings no children in, so this is re-attach-what-was-there, not
    /// a merge — and the forward semantic is unchanged by it (a forwarded parent asserts the source's FIELDS; the
    /// source's own children stay in the source's plugin, which is what lets a DIAL override not fight other mods'
    /// added lines). <see cref="RestoreChildGroup"/> nonetheless REFUSES rather than overwrites if the copy ever does
    /// arrive carrying children — that assumption is Mutagen's, not ours, and a bump that changed it would otherwise
    /// turn this preservation into a silent discard (Q3).
    /// </summary>
    public readonly record struct ChildGroupCarry(
        IReadOnlyList<(PropertyInfo Prop, object? Value)> Held, int Count, IReadOnlyList<string> Names, bool Captured)
    {
        /// <summary>Nothing was held — the record owns no children (the overwhelmingly common case: every record type
        /// but Cell / DialogTopic / Worldspace), so there is nothing to re-attach. NOT a licence to skip the arrival
        /// tripwire in <see cref="RestoreChildGroup"/>: see the comment there.</summary>
        public bool IsEmpty => Count == 0;
    }

    /// <summary>Lift <paramref name="record"/>'s owned child records off it, BEFORE the drop (#324). The values are the
    /// live collections — the record is on its way out of the mod and nothing else references it, so re-attaching them
    /// by reference is lossless and needs no deep copy.</summary>
    public static ChildGroupCarry CaptureChildGroup(IMajorRecord record)
    {
        var held = new List<(PropertyInfo, object?)>();
        foreach (var p in ChildBearingProperties(record.GetType()))
            held.Add((p, p.GetValue(record)));
        // Captured: true is written HERE and nowhere else — it is the fact "a drop-then-copy is happening to this
        // record", and RestoreChildGroup keys off it rather than inferring the same thing from the carry's shape or
        // the record's type. Both inferences were tried and both were wrong (rounds 1 and 2): emptiness cannot tell a
        // childless replace from no replace at all, and a type test fires on records the call itself just created.
        return new ChildGroupCarry(held, ChildCountOf(record), ChildNamesOf(record, 10), Captured: true);
    }

    /// <summary>The capture, with a throw turned into a refusal naming the RIGHT operation (round-1 review [low]). The
    /// lanes run this inside the try whose catch reports "the override-copy threw" — true of the copy, false of a
    /// fault reading the destination's own existing record, which is what would send a caller debugging the wrong
    /// file. Returns null on success, or the refusal.</summary>
    public static string? TryCaptureChildGroup(IMajorRecord record, string untouchedClause, out ChildGroupCarry carry)
    {
        carry = default;
        try { carry = CaptureChildGroup(record); return null; }
        catch (Exception ex)
        {
            return $"cannot forward {record.FormKey}: reading the child records under the version the destination " +
                   $"already carries threw ({ex.GetType().Name}: {ex.Message}) — surfaced, not swallowed (Q3). " +
                   untouchedClause;
        }
    }

    /// <summary>Re-attach a <see cref="CaptureChildGroup"/> carry onto the freshly copied record, AFTER the copy (#324).
    /// Returns null on success, or a refusal naming the cause — the caller fails the whole call with nothing
    /// serialized, never a partial write. <paramref name="untouchedClause"/> is the lane's own statement of what was
    /// left alone, SUBSTITUTED into each message rather than appended by the caller (the <c>SerializeFailure</c>
    /// precedent) so the reassurance lands before "please report it" instead of after it.</summary>
    public static string? RestoreChildGroup(IMajorRecord fresh, ChildGroupCarry carry, string untouchedClause)
    {
        // THE ONE GATE: did a capture happen — i.e. is this record being replaced by a drop-then-copy? Nothing else.
        // The lane knows that as a fact and CaptureChildGroup writes it; this method must not re-derive it. Both
        // derivations were tried and both were wrong. Deriving it from the carry being EMPTY skipped the arrival
        // tripwire in the case where an arriving set would be an INJECTION of the source's children into a
        // destination that deliberately holds none (round 1). Deriving it from the record's TYPE fired on records the
        // call had just created itself — forwarding an INFO and its DIAL in one call refused the whole write and
        // blamed Mutagen — and short-circuited the count check for any type whose containers the walk cannot see,
        // which is the silent-loss shape this whole function exists to prevent (round 2).
        if (!carry.Captured) return null;

        // The walks are INSIDE the try with the re-attach: a fault in Mutagen's containment enumeration is not the
        // override-copy throwing, and the lane's catch would call it that.
        try
        {
            // The copy is expected to arrive EMPTY (see the type doc). If it ever does not, surface it instead of
            // silently resolving it either way. A Mutagen bump that starts carrying children in trips this loudly.
            var arrived = ChildCountOf(fresh);
            if (arrived > 0)
            {
                var sample = string.Join(", ", ChildNamesOf(fresh, 5)) + (arrived > 5 ? ", …" : "");
                return $"cannot forward {fresh.FormKey}: the forwarded copy arrived carrying {arrived} child record(s) "
                     + $"of its own ({sample}), " + (carry.IsEmpty
                         ? "which a forward does not mean — it asserts the source's FIELDS and leaves the source's own "
                           + "children in the source's plugin, so houseCARL refuses rather than silently import them (Q3). "
                         : $"while the destination held {carry.Count} of its own; re-attaching would discard one of the two "
                           + "sets and there is no defensible way to guess which, so houseCARL refuses (Q3). ")
                     + untouchedClause + " This is an engine-level change in Mutagen's override-copy semantics, not "
                     + "something the call did wrong; please report it.";
            }

            foreach (var (prop, value) in carry.Held)
                prop.SetValue(fresh, value);

            // By-construction verification, off Mutagen's OWN containment enumeration rather than the reflected
            // property set that did the re-attach: if a containment path exists that ChildBearingProperties cannot
            // see, the counts disagree and the call refuses. This runs even when the carry held NOTHING to re-attach,
            // because the two readings are independent — a carry can count children off Mutagen's walk while the
            // reflected set that would restore them is empty, and that disagreement is exactly the silent loss.
            var after = ChildCountOf(fresh);
            if (after != carry.Count)
                return $"cannot forward {fresh.FormKey}: it carries {carry.Count} child record(s) " +
                       $"({string.Join(", ", carry.Names)}{(carry.Count > carry.Names.Count ? ", …" : "")}) and {after} " +
                       "are on the record after the replace — houseCARL will not write a plugin whose child records it " +
                       $"cannot account for (Q3). {untouchedClause} Please report this with the record type.";
            return null;
        }
        catch (Exception ex)
        {
            return $"cannot preserve the {carry.Count} child record(s) under {fresh.FormKey}: carrying them across " +
                   $"the replace threw ({ex.GetType().Name}: {ex.Message}) — surfaced, not swallowed (Q3). " +
                   untouchedClause;
        }
    }

    /// <summary>How many major records are contained UNDER <paramref name="record"/>, by Mutagen's own containment
    /// walk — the independent yardstick <see cref="RestoreChildGroup"/> checks the reflected re-attach against. Count
    /// only: the names cost a string per descendant, which on a worldspace is six figures, so they are materialized
    /// by <see cref="ChildNamesOf"/> on the refusal paths and on capture (ten, for the message) alone.</summary>
    static int ChildCountOf(IMajorRecordGetter record) =>
        record is IMajorRecordGetterEnumerable e ? e.EnumerateMajorRecords().Count() : 0;

    /// <summary>Up to <paramref name="max"/> child records of <paramref name="record"/>, named for a message.</summary>
    static List<string> ChildNamesOf(IMajorRecordGetter record, int max) =>
        record is IMajorRecordGetterEnumerable e
            ? e.EnumerateMajorRecords().Take(max).Select(r => r.EditorID ?? r.FormKey.ToString()).ToList()
            : new List<string>();

    /// <summary>The settable properties of <paramref name="t"/> that can REACH an owned major record, MEMOIZED per type
    /// (the <see cref="_flatGroupTypes"/> precedent — pure reflection metadata, constant for the process lifetime, and
    /// this runs per replaced record).
    /// <para/>
    /// Reachability is RECURSIVE and that is load-bearing, not defensive: <c>Worldspace.SubCells</c> reaches its cells
    /// through two non-record container types (<c>WorldspaceBlock</c> → <c>WorldspaceSubBlock</c> → <c>Cell</c>), so a
    /// one-level "is this property a major record" test sees <c>Cell</c>/<c>DialogTopic</c> and misses every exterior
    /// cell in the game. <c>IFormLink</c>s are excluded — a link REFERENCES a record, it does not own one, and walking
    /// them would sweep in most of the schema. Against Mutagen 0.53.1 this answers: Cell (Landscape, NavigationMeshes,
    /// Persistent, Temporary), DialogTopic (Responses), Worldspace (TopCell, SubCells) — and nothing else. The guard
    /// pins that over EVERY concrete record type Mutagen models, not a sample of them (round-1 review [low]: a pin on
    /// six names leaves the other 127 free to grow a container silently), so a Mutagen bump that adds one shows up as
    /// a failing arm rather than as a refusal in a caller's face.</summary>
    internal static IReadOnlyList<PropertyInfo> ChildBearingProperties(Type t) => _childProps.GetOrAdd(t, static ty =>
        ty.GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                      && ReachesOwnedRecord(p.PropertyType, new HashSet<Type>(), 0))
          .ToList());
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> _childProps = new();

    /// <summary>Can a value of <paramref name="t"/> hold an owned major record? See <see cref="ChildBearingProperties"/>
    /// for why this recurses and why links are cut. The depth bound and the visited set close the cycles Mutagen's
    /// object graph has (a Cell reaches a Cell through a Worldspace). The bound is a CONSTANT with one level of slack,
    /// not a by-construction limit: the deepest real path is Worldspace.SubCells at 5 (ExtendedList → WorldspaceBlock
    /// → Items → WorldspaceSubBlock → Items → Cell). A Mutagen bump that nests deeper than 6 degrades fail-closed —
    /// the property drops out of the set, the count check in <see cref="RestoreChildGroup"/> refuses, and the guard's
    /// all-types pin goes red first — so the constant is a tripwire, not a correctness assumption.</summary>
    static bool ReachesOwnedRecord(Type t, HashSet<Type> seen, int depth)
    {
        if (depth > 6 || !seen.Add(t)) return false;
        // `seen` is a PATH set, not a memo: the entry comes back out on the way up. Left in, a type first reached at
        // the depth bound would be recorded as unreachable and then skipped when a shallower branch reaches it, which
        // memoizes a depth-truncated answer as a depth-independent one. Cycles are still closed — a type on the
        // CURRENT path is what the set holds (a Cell reaches a Cell through a Worldspace).
        try
        {
            if (typeof(IFormLinkGetter).IsAssignableFrom(t)) return false;   // a reference, not a child
            if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) return true;
            if (ElementTypeOf(t) is { } elem) return ReachesOwnedRecord(elem, seen, depth + 1);
            if (!t.IsClass || t.Namespace?.StartsWith("Mutagen", StringComparison.Ordinal) != true) return false;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (ReachesOwnedRecord(p.PropertyType, seen, depth + 1)) return true;
            return false;
        }
        finally { seen.Remove(t); }
    }

    /// <summary>The element type <paramref name="t"/> enumerates, or null if it is not a collection. <c>string</c> is
    /// excluded explicitly — it enumerates chars and would otherwise recurse pointlessly on every text field.</summary>
    static Type? ElementTypeOf(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        if (t == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return null;
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }

    /// <summary>The Type to hand Mutagen's typed <c>Remove(FormKey, Type, throwIfUnknown)</c> for
    /// <paramref name="record"/>: the record's FLAT GROUP's <c>T</c> when one matches, else the runtime type (nested-group
    /// records — the shape the remove-record-probe proved reaches Cell/Placed*/INFO/Navmesh/Landscape). The flat-group
    /// answer matters for the abstract-base groups (Global, GameSetting): HCBR-2026-07-08-01 F3 proved that passing a
    /// concrete SUBCLASS of the group's T (a <c>GlobalShort</c> under <c>SkyrimGroup&lt;Global&gt;</c>) makes Mutagen's
    /// remove routing silently NO-OP — <c>throwIfUnknown:true</c> notwithstanding — while the group's own T removes
    /// correctly. Same <see cref="EnumerateFlatGroups"/> enumeration as every other flat-vs-nested decision (no drift).</summary>
    public static Type RemovalTypeFor(IMajorRecordGetter record)
    {
        foreach (var (tMajor, getterIface) in FlatGroupTypes)
            if (getterIface.IsInstanceOfType(record)) return tMajor;
        return record.GetType();
    }

    /// <summary>The flat groups' (T, getter-interface) pairs for <see cref="SkyrimMod"/>, MEMOIZED (the
    /// <see cref="_abstractGroups"/> precedent): pure reflection metadata, constant for the process lifetime —
    /// <see cref="RemovalTypeFor"/> runs per record when the remove lanes index a whole plugin (PR #163 review #2),
    /// so the per-call property walk was an avoidable O(records × properties). Derived from the SAME
    /// <see cref="EnumerateFlatGroups"/> enumeration (no drift).</summary>
    static IReadOnlyList<(Type tMajor, Type getterIface)> FlatGroupTypes => _flatGroupTypes.Value;
    static readonly Lazy<IReadOnlyList<(Type tMajor, Type getterIface)>> _flatGroupTypes =
        new(() => EnumerateFlatGroups(typeof(SkyrimMod)).Select(g => (g.tMajor, g.getterIface)).ToList());

    /// <summary>
    /// Generic <c>GetOrAddAsOverride</c>. Flat-group records (the common case) resolve by matching the
    /// <c>SkyrimGroup&lt;T&gt;</c> whose <c>T</c> carries the record's getter interface, then invoking the
    /// <c>GetOrAddAsOverrideMixIns</c> extension reflectively. Records in a NESTED group (Cell / the Placed*
    /// family / NavigationMesh / Landscape / DialogResponses-INFO — no flat <c>SkyrimGroup&lt;T&gt;</c>) fall
    /// through to <see cref="NestedGetOrAddAsOverride"/>, which resolves the record's context by FormKey from
    /// <paramref name="sourceLinkCache"/> and reconstructs its parent chain in the patch. The flat-vs-nested
    /// decision is ONE point — does a flat group match? — by construction; everything downstream of resolution
    /// (<see cref="ApplyVerb"/>, coercion, absent-materialization) is the SAME settable-record path for both
    /// (wave 3; mechanism scouted + proven by NestedProbe). <paramref name="sourceLinkCache"/> is optional and
    /// unused for flat records, so existing flat-only callers are unaffected; a nested record without it fails loud.
    /// </summary>
    public static IMajorRecord GenericGetOrAddAsOverride(
        SkyrimMod patchMod, IMajorRecordGetter source, ILinkCache? sourceLinkCache = null)
    {
        if (TryResolveGroup(patchMod, source, out var group, out var tMajor, out var tMajorGetter))
        {
            var open = OverrideMethod();
            var closed = open.MakeGenericMethod(tMajor!, tMajorGetter!);
            return (IMajorRecord)closed.Invoke(null, new object[] { group!, source })!;
        }
        // No flat SkyrimGroup<T> matched ⇒ a nested-group record (by construction) — take the nested path.
        return NestedGetOrAddAsOverride(patchMod, source, sourceLinkCache);
    }

    /// <summary>Find the mod's <c>SkyrimGroup&lt;T&gt;</c> property whose T carries the record's getter interface.
    /// Returns false (rather than throwing) when none matches — that IS the by-construction nested-group test, so
    /// <see cref="GenericGetOrAddAsOverride"/> can then take the nested path. The single flat-vs-nested decision.</summary>
    static bool TryResolveGroup(SkyrimMod mod, IMajorRecordGetter source,
        out object? group, out Type? tMajor, out Type? tMajorGetter)
    {
        foreach (var (p, tm, getterIface) in EnumerateFlatGroups(mod.GetType()))
            if (getterIface.IsInstanceOfType(source))
            {
                group = p.GetValue(mod)!; tMajor = tm; tMajorGetter = getterIface;
                return true;
            }
        group = null; tMajor = null; tMajorGetter = null;
        return false;
    }

    /// <summary>
    /// Resolve an override for a record stored in a NESTED group — records with no top-level
    /// <c>SkyrimGroup&lt;T&gt;</c>, so <see cref="TryResolveGroup"/> can't reach them. Resolves the record's
    /// CONTEXT by FormKey from the source link cache (the context knows the parent chain), then
    /// <c>GetOrAddAsOverride</c> reconstructs that chain in the patch mod and returns a settable override root —
    /// fed straight into the SAME <see cref="ApplyVerb"/> path as a flat record. Proven end-to-end by the wave-3
    /// scout (<c>NestedProbe</c>) across every distinct nested container shape against vanilla Skyrim.esm, source
    /// byte-untouched.
    ///
    /// By FormKey, NOT typed <c>EnumerateMajorRecordContexts&lt;T,TG&gt;</c>: the latter throws
    /// InvalidCastException on the sparse placed subtypes (a sibling cast to the wrong <c>IPlaced*Getter</c>);
    /// by-FormKey <c>ResolveContext</c> is unaffected (scout §6.1). The MethodInfo is re-resolved off the LIVE
    /// (closed-generic) cache — an open-generic definition can't be invoked on the closed instance.
    /// </summary>
    static IMajorRecord NestedGetOrAddAsOverride(SkyrimMod patchMod, IMajorRecordGetter source, ILinkCache? sourceLinkCache)
    {
        if (sourceLinkCache is null)
            throw new InvalidOperationException(
                $"Record {source.FormKey} ({source.GetType().Name}) is in a nested group (no flat SkyrimGroup<T>) " +
                "and needs the source link cache to reconstruct its parent chain — pass sourceLinkCache " +
                "(sourceMod.ToImmutableLinkCache()) to GenericGetOrAddAsOverride. Surfaced, not guessed (Q3).");

        var getterIface = PrimaryGetter(source.GetType())
            ?? throw new InvalidOperationException($"No getter interface for nested record {source.GetType().Name}.");
        var setterName = RecordNaming.GetterToSetterInterface(getterIface.Name);   // INpcGetter -> INpc (the settable interface, NOT the catalog name)
        var setterIface = getterIface.Assembly.GetType((getterIface.Namespace ?? "Mutagen.Bethesda.Skyrim") + "." + setterName)
            ?? throw new InvalidOperationException($"No setter interface {setterName} for nested record {getterIface.Name}.");

        // ResolveContext<TSetter,TGetter>(FormKey, [ResolveTarget]) off the live cache. A trailing ResolveTarget
        // (Winner/Origin) is immaterial on a single-mod cache; BuildResolveArgs supplies its default.
        var resolveCtx = sourceLinkCache.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ResolveContext" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2 && m.GetParameters().Length >= 1
                && m.GetParameters()[0].ParameterType == typeof(FormKey))
            ?? throw new InvalidOperationException("No ResolveContext<TSetter,TGetter>(FormKey,...) on the source link cache.");

        // Mutagen's non-Try ResolveContext THROWS on a miss, so MethodInfo.Invoke wraps it in a
        // TargetInvocationException — catch and rethrow a clear, FormKey-named fail-closed message (the prior
        // "returned null" guard was a dead branch; the throw-on-miss path is proven by write-proof Phase 8).
        // The null-guard remains in case some resolve path returns null instead of throwing.
        object? ctx;
        try
        {
            ctx = resolveCtx.MakeGenericMethod(setterIface, getterIface)
                      .Invoke(sourceLinkCache, BuildResolveArgs(resolveCtx, source.FormKey));
        }
        catch (TargetInvocationException tie)
        {
            throw new InvalidOperationException(
                $"Nested record {source.FormKey} ({source.GetType().Name}) not found in the source cache — " +
                "cannot reconstruct its parent chain (fail-closed, Q3).", tie.InnerException ?? tie);
        }
        if (ctx is null)
            throw new InvalidOperationException(
                $"ResolveContext returned null for nested record {source.FormKey} — not found in the source cache.");

        var goao = ctx.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetOrAddAsOverride" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.IsInstanceOfType(patchMod))
            ?? throw new InvalidOperationException($"Nested context for {source.FormKey} has no GetOrAddAsOverride(mod).");

        return (IMajorRecord)goao.Invoke(ctx, new object[] { patchMod })!;
    }

    /// <summary>Args for <c>ResolveContext&lt;T,TG&gt;(FormKey, [ResolveTarget])</c>: the FormKey, then any trailing
    /// parameter (the ResolveTarget enum — immaterial single-master) by its declared default or a zero value.</summary>
    static object?[] BuildResolveArgs(MethodInfo m, FormKey fk)
    {
        var ps = m.GetParameters();
        var argv = new object?[ps.Length];
        argv[0] = fk;
        for (int i = 1; i < ps.Length; i++)
            argv[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                : ps[i].ParameterType.IsValueType ? System.Activator.CreateInstance(ps[i].ParameterType) : null;
        return argv;
    }

    /// <summary>
    /// The single source of truth for "which records live in a flat <c>SkyrimGroup&lt;T&gt;</c> on the mod" —
    /// the records the generic lifecycle can <c>GetOrAddAsOverride</c>. <see cref="ResolveGroup"/> (the engine's
    /// per-write record resolution) and the write census (reachability classification) both derive from this one
    /// enumeration, so they can never disagree about what is group-reachable. The Loqui convention gives each
    /// concrete <c>Npc</c> the getter interface <c>INpcGetter</c>; records stored in NESTED groups (Cell under a
    /// cell-block, placed refs under a cell, INFO under a topic) have no top-level <c>SkyrimGroup&lt;T&gt;</c> and
    /// are therefore absent here — a real, surfaced reachability gap, never silently treated as covered.
    /// </summary>
    internal static IEnumerable<(PropertyInfo prop, Type tMajor, Type getterIface)> EnumerateFlatGroups(Type modType)
    {
        foreach (var p in modType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var pt = p.PropertyType;
            if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(SkyrimGroup<>)) continue;
            var tMajor = pt.GetGenericArguments()[0];
            var getterIface = tMajor.GetInterface("I" + tMajor.Name + "Getter");
            if (getterIface is not null) yield return (p, tMajor, getterIface);
        }
    }

    // ======================================================================
    //  CREATE front-end (capability arc — the last build). The sibling of
    //  GenericGetOrAddAsOverride: where that OVERRIDES an existing record into
    //  the patch, this ALLOCATES a brand-new one. Both resolve their group from
    //  the SAME EnumerateFlatGroups enumeration, so the create surface IS the
    //  flat-group surface BY CONSTRUCTION — every concrete flat record type is
    //  createable, nothing else is silently treated as covered (CLAUDE.md §3).
    //
    //  A small minority of flat groups are typed by an ABSTRACT base — exactly two
    //  by construction (SkyrimGroup<Global>, SkyrimGroup<GameSetting>); a Global is
    //  stored as a GlobalFloat / GlobalInt / GlobalShort, never a bare Global. The
    //  generic AddNew path can't close Mutagen's AddNew<T> with an abstract T (it
    //  throws), so abstract-group create takes a distinct branch keyed off the
    //  runtime hierarchy (T.IsAssignableFrom(concrete) && !concrete.IsAbstract):
    //  the caller names the CONCRETE arm ('GlobalFloat'), which is constructed via
    //  the same ConstructRecord/AllocateNextFormKey helpers the nested create uses
    //  and Add()-ed to the group's own instance Add(T) (a SkyrimGroup<T> is NOT an
    //  IList). Naming the bare abstract base ('Global') stays a loud refusal that
    //  lists the arms (Q3 — never a guessed default). The arm set is discovered, not
    //  hand-listed, so the branch serves GMST and any future abstract group equally.
    // ======================================================================

    /// <summary>Every flat group whose T is ABSTRACT, paired with its concrete-arm record Types — discovered by walking
    /// the SAME <see cref="EnumerateFlatGroups"/> enumeration that defines the create surface, testing <c>T.IsAbstract</c>,
    /// then collecting every non-abstract record class the base is assignable from (the runtime hierarchy, exactly as
    /// <see cref="GenericAddNew"/> keys off it). By construction: the abstract-group set IS Mutagen's (two today —
    /// Global, GameSetting), and each base's arm set IS its concrete subtype set — neither is hand-listed.
    ///
    /// MEMOIZED (the <see cref="OverrideMethod"/> precedent): the result is pure reflection METADATA (<c>PropertyInfo</c>
    /// + <c>Type</c>s), constant for the process lifetime and PATCH-INDEPENDENT, so the ~10k-type <c>GetTypes()</c> walk
    /// runs once. <see cref="CanCreateType"/> calls this ≥2× on EVERY create (incl. common Keyword/Spell creates and every
    /// record of a bulk_create), so the cache is a pure win. The per-patch group INSTANCE is NOT cached — callers resolve
    /// it live via <c>prop.GetValue(patchMod)</c> against the tuple's (patch-independent) <c>PropertyInfo</c>.</summary>
    static readonly Lazy<IReadOnlyList<(PropertyInfo prop, Type baseType, IReadOnlyList<Type> arms)>> _abstractGroups =
        new(() =>
        {
            var skyrimAsm = typeof(IArmorGetter).Assembly;
            var list = new List<(PropertyInfo, Type, IReadOnlyList<Type>)>();
            foreach (var (prop, tMajor, _) in EnumerateFlatGroups(typeof(SkyrimMod)))
            {
                if (!tMajor.IsAbstract) continue;
                var arms = skyrimAsm.GetTypes()
                    .Where(t => t.IsClass && !t.IsAbstract && tMajor.IsAssignableFrom(t) && typeof(IMajorRecord).IsAssignableFrom(t))
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();
                list.Add((prop, tMajor, arms));
            }
            return list;
        });

    internal static IReadOnlyList<(PropertyInfo prop, Type baseType, IReadOnlyList<Type> arms)> EnumerateAbstractGroups()
        => _abstractGroups.Value;

    /// <summary>If <paramref name="typeName"/> is the CONCRETE arm of an abstract record group (e.g. "GlobalFloat" under
    /// SkyrimGroup&lt;Global&gt;), resolve the live group + the arm's Type; else false. The single point both the
    /// pre-flight (<see cref="CanCreateType"/>) and the allocation (<see cref="GenericAddNew"/> / <see cref="GenericUpsertNew"/>)
    /// key off, so they can't drift. <paramref name="patchMod"/> null ⇒ the group instance isn't resolved (pre-flight,
    /// no patch yet) — only the arm Type is returned.</summary>
    static bool TryResolveAbstractGroupArm(SkyrimMod? patchMod, string typeName, out object? group, out Type? arm)
    {
        group = null; arm = null;
        foreach (var (prop, _, arms) in EnumerateAbstractGroups())
        {
            var hit = arms.FirstOrDefault(a => string.Equals(a.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (hit is null) continue;
            arm = hit;
            group = patchMod is null ? null : prop.GetValue(patchMod);
            return true;
        }
        return false;
    }

    /// <summary>Can a brand-new record of <paramref name="typeName"/> (a catalog name, e.g. "Keyword") be created by the
    /// generic create dispatch? True iff a flat <c>SkyrimGroup&lt;T&gt;</c> models it with a CONCRETE T, OR
    /// <paramref name="typeName"/> is a concrete ARM of an abstract group (e.g. "GlobalFloat" under SkyrimGroup&lt;Global&gt;).
    /// The two false cases are the named loud-fail boundaries (Q3 — surfaced, never a silent wrong create): NO flat group ⇒ a
    /// nested/placed record (Cell/Placed*/INFO/Navmesh/Landscape) that needs parent context; the bare ABSTRACT base
    /// ('Global'/'GameSetting') ⇒ name a concrete arm (the message lists the DISCOVERED arms — never a guessed default).
    /// <paramref name="reason"/> carries the user-facing explanation when false. Mod-instance-free (walks
    /// <c>typeof(SkyrimMod)</c>), so it serves the pre-flight before any patch exists.</summary>
    public static bool CanCreateType(string typeName, out string? reason)
    {
        // A concrete arm of an abstract group ('GlobalFloat', 'GameSettingFloat', …) — createable via the abstract-group
        // branch in GenericAddNew. Checked FIRST so a concrete arm always wins over the abstract-base refusal below.
        if (TryResolveAbstractGroupArm(null, typeName, out _, out _)) { reason = null; return true; }

        foreach (var (_, tm, _) in EnumerateAbstractGroups())
            if (string.Equals(tm.Name, typeName, StringComparison.OrdinalIgnoreCase))
            {
                // The bare abstract base ('Global'/'GameSetting'). The record is always stored as one of its concrete
                // arms — name which (Q3, no guessed default). Arms are DISCOVERED, not hand-listed (cornerstone §3).
                var arms = EnumerateAbstractGroups().First(g => g.baseType == tm).arms.Select(a => a.Name);
                reason = $"'{typeName}' is an abstract record group — a {typeName} is always stored as one of its concrete " +
                         $"subtypes ({string.Join(" / ", arms)}). Name the concrete subtype to create (e.g. {tm.Name}Float).";
                return false;
            }

        foreach (var (_, tm, _) in EnumerateFlatGroups(typeof(SkyrimMod)))
            if (string.Equals(tm.Name, typeName, StringComparison.OrdinalIgnoreCase))
            {
                reason = null;   // a concrete flat T (abstract T's were handled by the EnumerateAbstractGroups loop above)
                return true;
            }
        reason = $"'{typeName}' has no top-level group, so it can't be created on its own — it's a nested/placed record " +
                 "(a placed object/NPC, a dialogue line, navmesh or terrain) that needs a parent. Create it WITH its parent: " +
                 "pass parent= (the parent record's FormID, or the editorid of a record created EARLIER in the same call) and, " +
                 "if the parent holds more than one child-collection, collection= — or use housecarl_bulk_create to make a parent " +
                 "and its children (a dialogue topic + its lines, a cell + its placed refs) in ONE call. (A CELL is coordinate-keyed: " +
                 "create an EXTERIOR cell with parent=<Worldspace FormID> + grid=<X,Y>, or an INTERIOR cell with no parent.) If instead it's a subtype " +
                 "of an abstract group (like Global → GlobalFloat), create the concrete subtype. Flat top-level records — keywords, " +
                 "spells, perks, magic effects, factions, armor, weapons, leveled lists, … — create fine.";
        return false;
    }

    /// <summary>The CREATE front-end: allocate a brand-new record of <paramref name="typeName"/> in <paramref name="patchMod"/>,
    /// returning a settable root fed into the SAME <see cref="ApplyVerb"/> path as an override. For a concrete flat type the
    /// flat group's <c>AddNew</c> allocates a fresh LOCAL FormID (the new plugin's own 0x800+ ESP range, incrementing — measured
    /// by create-probe C2, with the floor guaranteed by <see cref="EnsureFormIdFloor"/>); for a concrete ARM of an abstract group
    /// ('GlobalFloat' / 'GameSettingFloat'), the arm is constructed via <see cref="ConstructRecord"/> + <see cref="AllocateNextFormKey"/>
    /// (the same allocator the nested create draws from, so the floor + counter are shared) and added through the group's own
    /// instance <c>Add(T)</c> — a <c>SkyrimGroup&lt;T&gt;</c> is NOT an IList, and Mutagen's generic AddNew&lt;T&gt; can't be
    /// closed with the abstract base. The new record's master is the patch itself. <paramref name="editorId"/> sets the EditorID
    /// (null uses the engine-assigned one). Throws loud (Q3) on the boundaries via <see cref="CanCreateType"/> — callers
    /// pre-flight with that, so a throw here means the surface changed under us.</summary>
    public static IMajorRecord GenericAddNew(SkyrimMod patchMod, string typeName, string? editorId)
    {
        if (!CanCreateType(typeName, out var reason)) throw new InvalidOperationException(reason);
        EnsureFormIdFloor(patchMod);   // a counter rehydrated below 0x800 would hand AddNew engine-reserved IDs (HCBR-2026-06-09-04)
        EnsureAllocatable(patchMod);   // …and a counter past the 24-bit object-ID ceiling can't allocate — fail loud (Q3)

        // Abstract-group arm (GlobalFloat / GameSettingFloat / …): construct the concrete arm + Add(T) it (the abstract T
        // can't go through InvokeAddNew). CanCreateType admitted the arm, so resolution here can't fail benignly.
        if (TryResolveAbstractGroupArm(patchMod, typeName, out var armGroup, out var armType))
            return AddConcreteArmToGroup(armGroup!, armType!, AllocateNextFormKey(patchMod), editorId);

        object? group = null; Type? tMajor = null;
        foreach (var (prop, tm, _) in EnumerateFlatGroups(patchMod.GetType()))
            if (string.Equals(tm.Name, typeName, StringComparison.OrdinalIgnoreCase)) { group = prop.GetValue(patchMod); tMajor = tm; break; }
        return InvokeAddNew(group!, tMajor!, editorId);   // CanCreateType guaranteed a concrete flat group exists
    }

    /// <summary>Construct a concrete abstract-group arm (<paramref name="armType"/>, e.g. <c>GlobalFloat</c>) at
    /// <paramref name="formKey"/> and add it to its group via the group's own instance <c>Add(T)</c> method (reflected —
    /// a <c>SkyrimGroup&lt;T&gt;</c> is NOT an IList, so the nested-create IList.Add path would crash). The same
    /// <see cref="ConstructRecord"/> idiom the nested create uses. Throws loud (Q3) if the expected <c>Add(T)</c> shape
    /// is absent — never a silent no-op.</summary>
    static IMajorRecord AddConcreteArmToGroup(object group, Type armType, FormKey formKey, string? editorId)
    {
        var rec = ConstructRecord(armType, formKey);
        if (editorId is not null) rec.EditorID = editorId;
        var baseType = group.GetType().IsGenericType ? group.GetType().GetGenericArguments()[0] : armType;
        var add = group.GetType().GetMethod("Add", new[] { baseType })
            ?? throw new InvalidOperationException(
                $"abstract-group create: no Add({baseType.Name}) on {group.GetType().Name} — surfaced, not guessed (Q3).");
        add.Invoke(group, new object[] { rec });
        return rec;
    }

    /// <summary>UPSERT front-end for the extend path: like <see cref="GenericAddNew"/>, but if the patch already
    /// carries a record IT ITSELF DEFINES with the same EditorID (a re-run of the same create against the same
    /// <c>into=</c> target), the stale copy is REPLACED — removed from its group and re-created FRESH at the SAME
    /// FormKey — instead of a duplicate being appended. This makes create calls IDEMPOTENT: re-running neither
    /// appends a second copy (the dup-append failure) nor accumulates list items inside a reused record (re-applying
    /// edits to a live record would re-Add keyword/effect list entries), and the stable FormKey keeps cross-record
    /// links and external references (script properties, SKSE framework configs) valid across re-runs.
    ///
    /// Three collisions are refused LOUD (Q3), never absorbed into a replace:
    ///   - an OVERRIDE the patch carries (another plugin's record, matched by its carried EditorID): replacing it
    ///     would serialize a blank override that GUTS the original plugin's record — the opposite of
    ///     originals-untouched. Overrides are edited with set_field/bulk_apply, never re-created.
    ///   - DUPLICATE residue (2+ same-EditorID records, the pre-fix dup-append bug's own product): which FormKey
    ///     survives is the caller's call — external references may point at either copy. Named, not guessed.
    ///   - a cross-TYPE EditorID collision: a real authoring error, surfaced not swallowed.
    /// Returns the record plus whether an existing one was replaced — the caller MUST surface a replace to the user
    /// (a replace discards the prior record state, including any set_field edits made since the original create).</summary>
    public static (IMajorRecord Record, bool Replaced) GenericUpsertNew(SkyrimMod patchMod, string typeName, string? editorId)
    {
        if (editorId is not null)
        {
            var matches = patchMod.EnumerateMajorRecords()
                .Where(r => string.Equals(r.EditorID, editorId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
            {
                // Replace-eligibility guard: ONLY a record this patch itself defines (its own ModKey) may be
                // replaced. A match on a carried OVERRIDE keeps the foreign FormKey — re-creating "at the same
                // FormKey" there would emit a field-wiping override of the ORIGINAL plugin's record.
                var foreign = matches.FirstOrDefault(r => r.FormKey.ModKey != patchMod.ModKey);
                if (foreign is not null)
                    throw new InvalidOperationException(
                        $"create refused: this patch already carries an OVERRIDE of {foreign.FormKey} whose editorid is '{editorId}' — " +
                        $"re-creating over an override would blank the original plugin's record. Pick a different editorid for the new record, " +
                        "or edit the override's fields with housecarl_set_field / housecarl_bulk_apply.");
                if (matches.Count > 1)
                    throw new InvalidOperationException(
                        $"create refused: this patch carries {matches.Count} records with editorid '{editorId}' " +
                        $"({string.Join(", ", matches.Select(m => m.FormKey))}) — duplicate residue from re-running creates on a pre-fix houseCARL. " +
                        "External references may point at either copy, so which survives is your call: remove the extra(s) with housecarl_remove_record, then re-run.");
                var existing = matches[0];
                if (!CanCreateType(typeName, out var reason)) throw new InvalidOperationException(reason);
                var formKey = existing.FormKey;

                // Abstract-group arm (GlobalFloat / GameSettingFloat / …): the cross-type guard compares the existing
                // record's CONCRETE type to the requested arm (re-running a GlobalFloat create over a stored GlobalInt
                // of the same editorid IS a cross-type collision — different concrete records), and the re-add goes
                // through Add(T), not the abstract-base InvokeAddNewWithFormKey (which can't close AddNew<T>).
                if (TryResolveAbstractGroupArm(patchMod, typeName, out var armGroup, out var armType))
                {
                    if (!armType!.IsInstanceOfType(existing))
                        throw new InvalidOperationException(
                            $"upsert refused: existing record '{editorId}' ({formKey}) is a {existing.GetType().Name}, not a {typeName} — " +
                            "an EditorID collision across record types is a real authoring error, surfaced not swallowed (Q3).");
                    InvokeRemove(armGroup!, formKey);
                    var freshArm = AddConcreteArmToGroup(armGroup!, armType, formKey, editorId);
                    return (freshArm, true);
                }

                object? group = null; Type? tMajor = null;
                foreach (var (prop, tm, _) in EnumerateFlatGroups(patchMod.GetType()))
                    if (string.Equals(tm.Name, typeName, StringComparison.OrdinalIgnoreCase)) { group = prop.GetValue(patchMod); tMajor = tm; break; }
                if (group is null || tMajor is null)
                    throw new InvalidOperationException($"upsert: no flat group found for type '{typeName}'.");
                if (!tMajor.IsInstanceOfType(existing))
                    throw new InvalidOperationException(
                        $"upsert refused: existing record '{editorId}' ({formKey}) is a {existing.GetType().Name}, not a {typeName} — " +
                        "an EditorID collision across record types is a real authoring error, surfaced not swallowed (Q3).");
                InvokeRemove(group, formKey);
                var fresh = InvokeAddNewWithFormKey(group, tMajor, formKey);
                fresh.EditorID = editorId;
                return (fresh, true);
            }
        }
        return (GenericAddNew(patchMod, typeName, editorId), false);
    }

    /// <summary>Remove a record from a flat group by FormKey. Tries the group's own instance <c>Remove(FormKey)</c>
    /// first, then falls back to the same Mutagen static-extension scan <see cref="InvokeAddNew"/> uses (Q3 —
    /// fails loud if neither shape exists, never a silent no-op).</summary>
    static void InvokeRemove(object group, FormKey formKey)
    {
        var instance = group.GetType().GetMethod("Remove", new[] { typeof(FormKey) });
        if (instance is not null)
        {
            var result = instance.Invoke(group, new object[] { formKey });
            if (result is false)
                throw new InvalidOperationException(
                    $"Remove({formKey}) reported nothing removed — the matched record vanished between match and replace " +
                    "(engine inconsistency, surfaced not swallowed).");
            return;
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
            foreach (var t in SafeTypes(asm).Where(t => t is { IsAbstract: true, IsSealed: true }))
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Remove"))
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 2 || ps[1].ParameterType != typeof(FormKey)) continue;
                    var closed = m;
                    if (m.IsGenericMethodDefinition)
                    {
                        if (m.GetGenericArguments().Length != 1) continue;
                        var tArg = group.GetType().IsGenericType ? group.GetType().GetGenericArguments()[0] : null;
                        if (tArg is null) continue;
                        try { closed = m.MakeGenericMethod(tArg); } catch { continue; }
                    }
                    if (!closed.GetParameters()[0].ParameterType.IsInstanceOfType(group)) continue;
                    closed.Invoke(null, new object[] { group, formKey });
                    return;
                }
        throw new InvalidOperationException($"Could not locate a Remove(FormKey) accepting {group.GetType().Name}.");
    }

    /// <summary>The FormKey-preserving sibling of <see cref="InvokeAddNew"/>: Mutagen's <c>AddNew(IGroup&lt;T&gt;, FormKey)</c>
    /// extension, located by the same proven candidate scan. Used by upsert so a replaced record keeps its FormID
    /// (the allocator is untouched — no new ID is consumed by a replace).</summary>
    static IMajorRecord InvokeAddNewWithFormKey(object group, Type tMajor, FormKey formKey)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
            foreach (var t in SafeTypes(asm).Where(t => t is { IsAbstract: true, IsSealed: true }))
                foreach (var open in t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                             .Where(m => m.Name == "AddNew" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1))
                {
                    var ps = open.GetParameters();
                    if (ps.Length != 2 || ps[1].ParameterType != typeof(FormKey)) continue;
                    MethodInfo closed;
                    try { closed = open.MakeGenericMethod(tMajor); } catch { continue; }
                    if (!closed.GetParameters()[0].ParameterType.IsInstanceOfType(group)) continue;
                    return (IMajorRecord)closed.Invoke(null, new object[] { group, formKey })!;
                }
        throw new InvalidOperationException($"Could not locate an AddNew(FormKey) extension accepting {group.GetType().Name}.");
    }

    /// <summary>
    /// Raise the patch's FormID allocator counter (<c>HEDR.NextObjectID</c>, in memory <c>ModHeader.Stats.NextFormID</c>)
    /// to its safe floor: at least 0x800 (object IDs below 0x800 are engine-reserved — and 0x000000 is the NULL-reference
    /// bit pattern; the CK, ESL compaction, and xEdit checks all assume the 0x800+ floor) AND past every record the patch
    /// ITSELF already defines (so a tampered/legacy counter can never re-allocate a live ID). Never lowers the counter.
    ///
    /// Why this exists (HCBR-2026-06-09-04): Mutagen's serializer keeps the header counter in sync by ITERATION
    /// (<c>NextFormIDOption.Iterate</c> = max originating FormID present, measured by formid-floor-probe S2) — an
    /// override-only patch (the bulk_apply/set_field shape) therefore persists <c>NextObjectID = 0</c>, and a later
    /// extend (<c>into=</c>) rehydrates that 0 straight into the allocator: under header 1.71 Mutagen itself accepts
    /// the lower range (<c>GetDefaultInitialNextFormID(null)</c> == 0, probe S1), so AddNew happily allocated 000000.
    /// Called at BOTH chokepoints: <see cref="GenericAddNew"/> (every allocation ≥ 0x800 by construction, healing
    /// patches already on disk with a zeroed counter) and <see cref="WritePatch(SkyrimMod,IReadOnlyList{ISkyrimModGetter},string)"/>
    /// (every written patch PERSISTS a floored counter — see the NoNextFormIDProcessing note there).
    /// </summary>
    public static void EnsureFormIdFloor(SkyrimMod patchMod)
    {
        uint floor = FormIdRange.EngineReservedFloor;
        foreach (var r in patchMod.EnumerateMajorRecords())
            if (r.FormKey.ModKey == patchMod.ModKey && r.FormKey.ID >= floor)
                floor = r.FormKey.ID + 1;
        if (patchMod.ModHeader.Stats.NextFormID < floor)
            patchMod.ModHeader.Stats.NextFormID = floor;
    }

    /// <summary>Guard that the patch can still allocate: its NextObjectID counter must be within the 24-bit object-ID
    /// space (≤ <see cref="FormIdRange.ObjectIdMax"/>). Past it (a tampered header, or a truly full plugin) no
    /// allocation is possible — fail LOUD here, at the allocation boundary (Q3), NOT in <see cref="EnsureFormIdFloor"/>:
    /// a full-but-valid patch must still SERIALIZE (WritePatch floors the same counter), it just can't grow (PR #30
    /// review). The four create entry points (flat / nested / exterior-cell / interior-cell) share this one guard so
    /// the ceiling and its message live in ONE place rather than four identical copies.</summary>
    static void EnsureAllocatable(SkyrimMod patchMod)
    {
        if (FormIdRange.ObjectIdSpaceExhausted(patchMod.ModHeader.Stats.NextFormID))
            throw new InvalidOperationException(
                $"cannot allocate a new FormID: the patch's NextObjectID counter is 0x{patchMod.ModHeader.Stats.NextFormID:X} — " +
                $"past the 24-bit object-ID ceiling (0x{FormIdRange.ObjectIdMax:X}). The plugin is full or its header counter is corrupt.");
    }

    /// <summary>Invoke Mutagen's <c>AddNew</c> on a flat group instance. <c>AddNew</c> is NOT a plain instance method on
    /// <c>SkyrimGroup&lt;T&gt;</c> (a direct GetMethod misses it — measured by create-probe C1): like
    /// <c>GetOrAddAsOverride</c> it's a GENERIC EXTENSION (IGroupMixIns) in a Mutagen static class, so it's located the same
    /// way <see cref="OverrideMethod"/> finds its method, closed with the group's T, and the receiver is verified to accept
    /// the live group before invoke (Q3). Iterates candidates (no commit-to-first cache) so a wrong-shaped AddNew overload
    /// can't shadow the right one — the proven create-probe resolver.</summary>
    static IMajorRecord InvokeAddNew(object group, Type tMajor, string? editorId)
    {
        bool withEdid = editorId is not null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
            foreach (var t in SafeTypes(asm).Where(t => t is { IsAbstract: true, IsSealed: true }))
                foreach (var open in t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                             .Where(m => m.Name == "AddNew" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1))
                {
                    if (open.GetParameters().Length != (withEdid ? 2 : 1)) continue;
                    if (withEdid && open.GetParameters()[1].ParameterType != typeof(string)) continue;
                    MethodInfo closed;
                    try { closed = open.MakeGenericMethod(tMajor); } catch { continue; }   // T didn't satisfy the constraints
                    if (!closed.GetParameters()[0].ParameterType.IsInstanceOfType(group)) continue;   // receiver must accept this group
                    return (IMajorRecord)closed.Invoke(null, withEdid ? new object[] { group, editorId! } : new object[] { group })!;
                }
        throw new InvalidOperationException(
            $"Could not locate an AddNew({(withEdid ? "string" : "")}) extension accepting {group.GetType().Name} in the Mutagen assemblies.");
    }

    // ======================================================================
    //  NESTED CREATE front-end (nested/dialogue plan, Layer A — STEP 0 proven).
    //  The sibling of NestedGetOrAddAsOverride: where that OVERRIDES an existing
    //  nested record into the patch, this ALLOCATES a brand-new child INTO a
    //  parent's modeled child-collection. By construction for the FormKey-
    //  parented families (an INFO under a DialogTopic; a Placed* into a Cell):
    //  the add-target collection is found REFLECTIVELY — the child type alone
    //  picks it (outcome i) or the caller names one of the parent's enumerable
    //  child-collections (outcome ii) — never a hand-coded per-family selector.
    //  The parent must already be settable IN the patch (the caller overrides or
    //  creates it first). Coordinate-keyed parents (an exterior Cell under the
    //  FormKey-LESS WorldspaceBlock/SubBlock structs) are a SEPARATE §4-(b) seam,
    //  not reachable here — TryFindNestedCollection fails loud for them (Q3).
    // ======================================================================

    /// <summary>Resolve a catalog/record-type name to its concrete Mutagen record <see cref="Type"/>. The namespace
    /// is <c>Mutagen.Bethesda.Skyrim</c> by construction (the Loqui convention). Null ⇒ absent from the modeled set,
    /// a real coverage gap to surface (Q3), never a value to guess.</summary>
    public static Type? ResolveConcreteRecordType(string catalogName)
        => typeof(IArmorGetter).Assembly.GetType("Mutagen.Bethesda.Skyrim." + catalogName);

    /// <summary>Can a brand-new <paramref name="childCatalogName"/> record be created as a nested child of a parent of
    /// <paramref name="parentType"/>, into <paramref name="collectionName"/> (null = the unique collection that accepts
    /// the child)? The by-construction add-target test (nested plan §1.4 Q2): the parent's settable child-collections are
    /// found reflectively, so "createable-under" is defined by the model, not a hand-coded per-family list. Every false
    /// (no such containment, an ambiguous unnamed target, an unknown collection name) names what it checked (Q3).</summary>
    public static bool CanCreateNested(string childCatalogName, Type parentType, string? collectionName, out string? reason)
    {
        var childType = ResolveConcreteRecordType(childCatalogName);
        if (childType is null)
        {
            reason = $"'{childCatalogName}' is absent from the Mutagen corpus — a real coverage gap to surface, not a value to guess.";
            return false;
        }
        return TryFindNestedCollection(parentType, childType, collectionName, out _, out _, out reason);
    }

    /// <summary>Find the parent type's SETTABLE child-collection to allocate a <paramref name="childType"/> into — the
    /// generic add-target resolver. Reflects the parent's list/ExtendedList properties whose element type the child
    /// satisfies. Outcomes: exactly one match ⇒ derivable by type (i), returned; several ⇒ the caller must NAME one
    /// (<paramref name="collectionName"/> picks it) (ii); zero ⇒ the child cannot nest under this parent (a real
    /// containment boundary, Q3). <paramref name="error"/> names the unnamed-ambiguous / no-containment / bad-name cases;
    /// null on success.</summary>
    static bool TryFindNestedCollection(Type parentType, Type childType, string? collectionName,
        out PropertyInfo? prop, out List<string> matches, out string? error)
    {
        prop = null; error = null;
        matches = new List<string>();
        var hits = new List<PropertyInfo>();
        foreach (var p in parentType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetGetMethod() is null) continue;                          // need a readable list instance to Add to
            var elem = ListElementType(p.PropertyType);
            if (elem is null) continue;
            if (!elem.IsAssignableFrom(childType)) continue;                 // the child fits this list's element type
            if (!typeof(System.Collections.IList).IsAssignableFrom(p.PropertyType)) continue;  // addable (ExtendedList<T> is an IList)
            hits.Add(p); matches.Add(p.Name);
        }
        var parentName = parentType.Name;   // concrete class name, already clean (never an I…Getter here)
        var childName = childType.Name;
        if (hits.Count == 0)
        {
            error = $"'{childName}' cannot be created under a '{parentName}' — that parent models no child-collection that " +
                    "holds it (a real containment boundary, surfaced not guessed, Q3)." +
                    (childType == typeof(Cell)
                        ? " A Cell is coordinate-keyed, not collection-nested: create an EXTERIOR cell with parent=<Worldspace> + grid=<X,Y>, or an INTERIOR cell with no parent."
                        : "");
            return false;
        }
        if (collectionName is not null)
        {
            var named = hits.FirstOrDefault(h => string.Equals(h.Name, collectionName, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                error = $"'{parentName}' has no child-collection named '{collectionName}' that holds '{childName}'. Available: {string.Join(", ", matches)}.";
                return false;
            }
            prop = named; return true;
        }
        if (hits.Count > 1)
        {
            error = $"'{childName}' can be added to more than one collection on '{parentName}' ({string.Join(", ", matches)}) — " +
                    "name which one (the collection= discriminator). (Q3 — never a silent default.)";
            return false;
        }
        prop = hits[0]; return true;
    }

    /// <summary>The element type of an <c>IList&lt;T&gt;</c>/<c>ExtendedList&lt;T&gt;</c>-shaped property, else null
    /// (a scalar, a string, a read-only sequence). Used to match a parent's child-collections to a child type.</summary>
    static Type? ListElementType(Type t)
    {
        if (t == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return null;
        if (t.IsGenericType && t.GetGenericArguments() is { Length: 1 } ga) return ga[0];
        return t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
                ?.GetGenericArguments()[0];
    }

    /// <summary>Construct a concrete Mutagen record via its <c>(FormKey, &lt;release enum&gt;)</c> constructor — the
    /// net-new nested child. The ctor shape is discovered reflectively (measure, don't assume); throws loud if absent.</summary>
    static IMajorRecord ConstructRecord(Type concrete, FormKey fk)
    {
        var ctor = concrete.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 2
                && c.GetParameters()[0].ParameterType == typeof(FormKey) && c.GetParameters()[1].ParameterType.IsEnum);
        if (ctor is null)
            throw new InvalidOperationException($"nested create: no (FormKey, release) constructor on {concrete.Name} — surfaced, not guessed (Q3).");
        var release = Enum.Parse(ctor.GetParameters()[1].ParameterType, "SkyrimSE");
        return (IMajorRecord)ctor.Invoke(new object[] { fk, release });
    }

    /// <summary>Allocate the next LOCAL FormKey from the patch's own allocator (<c>GetNextFormKey</c>), discovered
    /// reflectively. The caller floors the counter first (<see cref="EnsureFormIdFloor"/>), so the returned id is in
    /// the 0x800+ ESP-local range and shares the SAME incrementing counter flat <c>AddNew</c> draws from (STEP 0).</summary>
    static FormKey AllocateNextFormKey(SkyrimMod patchMod)
    {
        var m = patchMod.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(x => x.Name == "GetNextFormKey" && x.GetParameters().Length == 0)
            ?? throw new InvalidOperationException("nested create: no GetNextFormKey() on the patch mod — surfaced, not guessed (Q3).");
        return (FormKey)m.Invoke(patchMod, null)!;
    }

    /// <summary>The CREATE front-end for a NESTED record: allocate a brand-new <paramref name="childCatalogName"/> child
    /// into <paramref name="parentInPatch"/>'s modeled child-collection (named, or the unique one), returning a settable
    /// root fed into the SAME <see cref="ApplyVerb"/> path as a flat create or an override. The new record gets a fresh
    /// local 0x800+ FormKey from the SAME floor/counter as flat <see cref="GenericAddNew"/> (the nested plan §1.5 inherited
    /// item — proven sharing in STEP 0). The parent MUST already be settable in the patch (overridden or created by the
    /// caller). Throws loud (Q3) via <see cref="TryFindNestedCollection"/> on a containment/ambiguity the pre-flight
    /// (<see cref="CanCreateNested"/>) should have caught — a throw here means the surface changed under us.</summary>
    public static IMajorRecord NestedAddNew(SkyrimMod patchMod, IMajorRecord parentInPatch,
        string childCatalogName, string? collectionName, string? editorId)
    {
        var childType = ResolveConcreteRecordType(childCatalogName)
            ?? throw new InvalidOperationException($"nested create: '{childCatalogName}' is absent from the Mutagen corpus.");
        if (!TryFindNestedCollection(parentInPatch.GetType(), childType, collectionName, out var prop, out _, out var error))
            throw new InvalidOperationException(error);
        if (prop!.GetValue(parentInPatch) is not System.Collections.IList list)
            throw new InvalidOperationException($"nested create: collection '{prop.Name}' on '{parentInPatch.GetType().Name}' is not an addable list.");

        EnsureFormIdFloor(patchMod);   // a rehydrated (into=) counter below 0x800 would hand engine-reserved IDs (HCBR-2026-06-09-04)
        EnsureAllocatable(patchMod);
        var child = ConstructRecord(childType, AllocateNextFormKey(patchMod));
        if (editorId is not null) child.EditorID = editorId;
        list.Add(child);
        return child;
    }

    // ======================================================================
    //  COORDINATE-KEYED CREATE (the §4-(b) seam) — cells, the one family whose
    //  structural parents are FormKey-LESS block structs the FormKey locator
    //  (NestedAddNew) cannot address: exterior cells under WorldspaceBlock/
    //  WorldspaceSubBlock, interior cells under CellBlock/CellSubBlock. Placed
    //  by DERIVED block arithmetic, not a parent FormKey. STEP-0 proven
    //  (CoordCellProbe, round-tripped vs vanilla): exterior block=floor(grid/32)
    //  subblock=floor(grid/8) (4000/4000); interior block=id%10 subblock=(id/10)%10
    //  (590/590); thin Worldspace override (a 1-cell delta, not all of Tamriel).
    //  ONE generic algorithm per cell-kind — NOT a per-type shim (dev/plans/
    //  COORD_KEYED_CELL_CREATE_BUILD_2026-06-20.md). Mutagen DROPS the OFST
    //  seek-cache on write (Aaron-accepted 2026-06-20); the engine rebuilds it
    //  from the block tree at load. A created cell is a STRUCTURAL SHELL —
    //  lighting/land/navmesh stay the author's (the shell report surfaces this
    //  loudly; the engine stays policy-free, Q3).
    // ======================================================================

    /// <summary>Signed integer floor division (toward -∞) — the exterior block/subblock index from a (possibly
    /// negative) cell grid coordinate. C# truncates toward zero (<c>-1/8 == 0</c>), but the cell at grid -1 sits in
    /// subblock -1; this floors so negative coordinates key correctly (the scout confirmed floor vs 4000 vanilla cells).</summary>
    internal static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>CREATE an EXTERIOR cell at grid (<paramref name="gridX"/>,<paramref name="gridY"/>) under
    /// <paramref name="worldspaceInPatch"/> (already settable in the patch — the caller overrides it in, thin). Computes
    /// the block (floor(grid/32)) + subblock (floor(grid/8)) indices, FINDS-OR-CONSTRUCTS the FormKey-less
    /// <see cref="WorldspaceBlock"/> + <see cref="WorldspaceSubBlock"/> structs (the add-target is the model's block tree,
    /// not a FormKey lookup), constructs the <see cref="Cell"/> with its Grid set and <c>IsInteriorCell</c> OFF, allocates
    /// a fresh local 0x800+ FormKey from the SAME floored counter as flat/nested create, and adds it. Returns the new cell
    /// (settable, fed into the same <see cref="ApplyVerb"/> path). STEP-0 proven (CoordCellProbe).</summary>
    public static Cell AddExteriorCell(SkyrimMod patchMod, Worldspace worldspaceInPatch, int gridX, int gridY, string? editorId)
    {
        // Floor + dedup BEFORE mutating the block tree (a failed call discards the patch, but ordering it like
        // AddInteriorCell keeps the no-mutation-before-validation property clean — PR #94 review nit).
        EnsureNoDuplicateCellEditorId(patchMod, editorId);   // no silent duplicate on an into= re-run (cells have a stable EditorID)
        EnsureFormIdFloor(patchMod);   // 0x800 floor, exactly like flat/nested create (HCBR-2026-06-09-04)
        EnsureAllocatable(patchMod);
        int bx = FloorDiv(gridX, 32), by = FloorDiv(gridY, 32), sx = FloorDiv(gridX, 8), sy = FloorDiv(gridY, 8);
        var block = worldspaceInPatch.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
        if (block is null)
        {
            block = new WorldspaceBlock { BlockNumberX = (short)bx, BlockNumberY = (short)by, GroupType = GroupTypeEnum.ExteriorCellBlock };
            worldspaceInPatch.SubCells.Add(block);
        }
        var sub = block.Items.FirstOrDefault(s => s.BlockNumberX == sx && s.BlockNumberY == sy);
        if (sub is null)
        {
            sub = new WorldspaceSubBlock { BlockNumberX = (short)sx, BlockNumberY = (short)sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            block.Items.Add(sub);
        }
        var cell = new Cell(AllocateNextFormKey(patchMod), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(gridX, gridY) } };
        if (editorId is not null) cell.EditorID = editorId;
        sub.Items.Add(cell);
        return cell;
    }

    /// <summary>CREATE an INTERIOR cell — files into the patch's top-level <see cref="SkyrimMod.Cells"/> group by the
    /// cell's OWN FormID digits (block = id%10, subblock = (id/10)%10 — STEP-0 proven 590/590 vanilla). The FormKey is
    /// allocated FIRST (the digits key off it), then the <see cref="CellBlock"/>/<see cref="CellSubBlock"/> are
    /// found-or-constructed. <c>IsInteriorCell</c> ON. Returns the new cell (settable, fed into the same
    /// <see cref="ApplyVerb"/> path).</summary>
    public static Cell AddInteriorCell(SkyrimMod patchMod, string? editorId)
    {
        EnsureNoDuplicateCellEditorId(patchMod, editorId);   // no silent duplicate on an into= re-run (cells have a stable EditorID)
        EnsureFormIdFloor(patchMod);
        EnsureAllocatable(patchMod);
        var fk = AllocateNextFormKey(patchMod);
        uint id = fk.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = patchMod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        var cell = new Cell(fk, SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
        if (editorId is not null) cell.EditorID = editorId;
        sub.Cells.Add(cell);
        return cell;
    }

    /// <summary>Refuse loud (Q3) if <paramref name="patchMod"/> ALREADY defines a cell with <paramref name="editorId"/>.
    /// Coordinate-keyed cell create does NOT upsert (unlike flat <see cref="GenericUpsertNew"/>), so an into= re-run would
    /// otherwise silently APPEND a second cell at the same identity — and a cell DOES carry a stable EditorID (the flat
    /// nested-children carve-out's "no stable handle to de-dup on" rationale, WritePatchBuilder, does not transfer to
    /// cells; PR #94 review). A no-op on a fresh patch (no prior cells). Within a single bulk_create, same-editorid specs
    /// are already caught by the pre-flight's per-call editorid set; this closes the cross-call into= gap.</summary>
    static void EnsureNoDuplicateCellEditorId(SkyrimMod patchMod, string? editorId)
    {
        if (string.IsNullOrEmpty(editorId)) return;
        foreach (var existing in patchMod.EnumerateMajorRecords<ICellGetter>())
            if (string.Equals(existing.EditorID, editorId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"a cell with editorid '{editorId}' already exists in this patch ({existing.FormKey}); creating it again " +
                    "would duplicate it. Cell create does not upsert — edit the existing cell, or use a different editorid.");
    }

    static MethodInfo? _overrideMethod;
    /// <summary>The 2-arg <c>GetOrAddAsOverride&lt;TMajor,TMajorGetter&gt;(IGroup&lt;TMajor&gt;, TMajorGetter)</c> extension.</summary>
    static MethodInfo OverrideMethod()
    {
        if (_overrideMethod is not null) return _overrideMethod;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
        {
            foreach (var t in SafeTypes(asm).Where(t => t is { IsAbstract: true, IsSealed: true }))
            {
                var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
                    m.Name == "GetOrAddAsOverride" &&
                    m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 2 &&
                    m.GetParameters().Length == 2);
                if (m is not null) return _overrideMethod = m;
            }
        }
        throw new InvalidOperationException("Could not locate GetOrAddAsOverride extension in Mutagen assemblies.");
    }

    /// <summary>Port of the spike's WritePatch — already generic. Ties output filename to ModKey. The single-known-
    /// master case (the standalone harness opens ONE source plugin); delegates to the multi-master overload with a
    /// one-element set, so both paths share one BeginWrite incantation + filename check.</summary>
    internal static void WritePatch(SkyrimMod patchMod, ISkyrimModGetter sourceMod, string outputPath)
        => WritePatch(patchMod, new[] { sourceMod }, outputPath);

    /// <summary>
    /// Multi-master WritePatch — the MCP-wave capability the single-source standalone harness could not reach.
    /// Hands the serializer the FULL set of known masters (the whole load order's overlays, via the resolver), so a
    /// patch record that references forms across SEVERAL plugins serializes with every needed master in its header.
    /// Mutagen syncs the header master list to what the records ACTUALLY reference, so offering the whole order is
    /// correct AND lean — only the referenced masters land (the write-proof's byte-identity-vs-native across phases
    /// 3/4/6/7/9/11, all run with the full <c>allMasters</c> set, is the standing proof of this). A referenced master
    /// absent from <paramref name="knownMasters"/> still fails loud (Q3) — never a silent wrong patch. This is what
    /// makes a cross-master merge patch (e.g. a leveled list pulling entries from several mods) writable; the
    /// single-master overload above is the degenerate one-master case.
    /// </summary>
    /// <remarks>Every written plugin force-includes <see cref="BaselineMasters"/> (Skyrim.esm + Update.esm) — see the chain below.</remarks>
    internal static void WritePatch(SkyrimMod patchMod, IReadOnlyList<ISkyrimModGetter> knownMasters, string outputPath)
    {
        var expected = patchMod.ModKey.FileName.String;
        var actual = Path.GetFileName(outputPath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Output filename '{actual}' must match patch ModKey filename '{expected}'.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // Hand Mutagen the FULL load order (every overlay, priority order) so it can resolve + ORDER every referenced
        // master. WithLoadOrderFromHeaderMasters() can't serve a freshly-built cross-master patch: the header doesn't
        // yet list the masters the new references need, so its master-sort throws MissingModException. WithLoadOrder
        // gives the real order to sort against; the master LIST stays lean — Mutagen derives it from the records'
        // actual FormLinks (only-referenced), the load order only resolves + orders them. A referenced master absent
        // from the set still fails loud (Q3).
        //
        // BASELINE MASTERS (Aaron 2026-06-02): every Skyrim plugin MUST carry Skyrim.esm + Update.esm — exactly what the
        // Creation Kit stamps on every plugin ("if ck stamps both then we should too"). A masterless plugin (e.g. a
        // self-contained CREATED record references nothing, so the derived set is empty) is malformed by convention.
        // WithExtraIncludedMasters force-includes them ON TOP of the derived set: a no-op for any already referenced
        // (idempotent — no duplicate, bytes unchanged, so the existing byte-identity proofs are unaffected), and the fix
        // when absent (proven: master-probe M2/M3). FILTERED to baselines actually IN the load order — both ship with SE
        // so in any real order both are forced (matching the CK); the filter only keeps a degenerate order (or a minimal
        // single-master test harness) from throwing on an unresolvable extra master. The master LIST stays otherwise lean.
        var ordered = knownMasters as ISkyrimModGetter[] ?? knownMasters.ToArray();
        var baseline = BaselineMasters.Where(bm => ordered.Any(km => km.ModKey == bm)).ToArray();
        // FORMID FLOOR (HCBR-2026-06-09-04): Mutagen's default NextFormID handling re-derives the persisted
        // HEDR.NextObjectID by ITERATING originating records (max + 1, or 0 when there are none — formid-floor-probe
        // S2), so an override-only patch lands on disk with a 0 counter that a later extend rehydrates straight into
        // the allocator. NoNextFormIDProcessing makes the serializer persist OUR in-memory counter verbatim (probe S5),
        // and EnsureFormIdFloor guarantees that counter is ≥ 0x800 AND past every record the patch defines — the same
        // invariant Iterate maintained, plus the floor, minus the regression (a remove no longer shrinks the counter,
        // so a freed ID is never re-allocated). Every product write funnels through here, so every houseCARL-written
        // plugin carries a conventional counter regardless of which tool created it.
        EnsureFormIdFloor(patchMod);
        // ATOMIC WRITE (Q3): stage + commit. Serializing IN PLACE has two failure shapes once a patch lives in an
        // MO2 mods folder: a crash mid-serialize leaves a truncated .esp (a torn original), and an external folder
        // watcher (MO2 refreshing its plugin list) can open a half-written file. Staging into a sibling temp dir and
        // committing via AtomicFile.Commit (File.Replace over an existing target — the Win32 atomic-replace primitive —
        // or a rename onto a fresh one) means the target path only ever holds the OLD complete file or the NEW complete
        // file, never a missing or partial one: true crash-ATOMIC replacement, not merely crash-TEAR safety. NOTE: this
        // does NOT relax the PR #24 self-lock guard
        // (ReleaseOverlay + AllMastersExcept before the serialize): a swap onto a still-mapped target fails exactly
        // like an in-place write would, so callers must still release every handle they hold on the target first.
        // Staging buys crash-tear safety and shrinks the external-watcher window; the handle discipline stays
        // load-bearing.
        // SERIALIZE-BOUNDARY NULL-ARM CATCH (HCBR-2026-06-15-01 PR-C, PART B): Mutagen's binary writer dereferences a
        // record's modeled sub-fields as it writes; a COMPOSED record that left a REQUIRED polymorphic sub-field unset
        // (the canonical case: a Condition composed without its Data arm) is null at that deref → a bare
        // NullReferenceException carrying NO field name. Pre-flight can't reject it: the corpus DOES now carry faithful
        // polymorphic nullability (S4 Track D), but that flag is NOT a "required arm at serialize" signal — Condition.Data
        // reads Nullable=false and throws when null, yet NpcConfiguration.Level ALSO reads Nullable=false and serializes
        // fine when null (nullarm-guard B2). A pre-flight gate on the flag would over-reject a legitimately-absent field
        // like Level, or need a hand-curated required-arm list (cornerstone §3) — so there is still no by-construction
        // required/optional signal to gate on. The serialize boundary is the honest place to fail it (Q3). WritePatchStaged
        // already discards its temp on any throw, so nothing is on disk; re-stamp ONLY a null-arm NRE — whether BARE (the
        // synchronous case, e.g. Condition.Data) OR wrapped in the parallel writer's nested AggregateException (a null
        // required sub-field can take either serialize path) — as a NAMED refusal via RootNullArm; other serialize errors
        // keep their own type/message. The existing WritePatchBuilder serialize catches render it loud + all-or-nothing.
        string staged;
        try { staged = WritePatchStaged(patchMod, ordered, baseline, outputPath); }
        catch (Exception ex) when (RootNullArm(ex) is { } nre) { throw new NullArmSerializeException(nre); }
        CommitStagedPatch(staged, outputPath);
    }

    /// <summary>Render an exception as <c>Type: message</c>, APPENDING its inner exception's type+message when present.
    /// A re-stamped wrapper (e.g. <see cref="NullArmSerializeException"/> over the raw writer NRE) otherwise hides the
    /// discriminating inner signal at the user surface — so the serialize-failure render sites use this to keep the
    /// loud NAMED outer message AND the inner that distinguishes a genuine engine NRE from a composed-null-arm one
    /// (the codebase's InnerException-unwrap idiom). Q3 — no opaque, no signal-stripping error.</summary>
    internal static string Describe(Exception ex)
        => ex.InnerException is { } inner
            ? $"{ex.GetType().Name}: {ex.Message} [inner: {inner.GetType().Name}: {inner.Message}]"
            : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>Unwrap a serialize-boundary throw to the root <see cref="NullReferenceException"/> that signals a
    /// composed-null-arm failure — or null when the failure is NOT purely a null-arm one. A null required modeled
    /// sub-field (canonically a COMPOSED record missing a required polymorphic arm — a Condition without its Data arm)
    /// is dereferenced by Mutagen's writer as a bare NRE. But that writer runs record writes through a PARALLEL path,
    /// so the SAME NRE can surface WRAPPED — one or more nested <see cref="AggregateException"/>s around a Mutagen
    /// <c>SubrecordException</c> (HCBR-2026-07-04 captured a doubly-nested one). A bare-<see cref="NullReferenceException"/>
    /// catch misses the wrapped shape and lets the opaque AggregateException render raw instead of as the loud NAMED
    /// refusal — so, regardless of which serialize path a record takes, this normalizes both. It flattens the aggregate
    /// nesting (<see cref="AggregateException.Flatten"/>) and, for each leaf, walks its
    /// <see cref="Exception.InnerException"/> chain to the ROOT cause — re-stamping ONLY when EVERY leaf's root is a
    /// <see cref="NullReferenceException"/> (the whole failure IS the null-arm case), returning the first such NRE as the
    /// preserved inner. Any leaf whose root is NOT an NRE returns null, so that genuine other error keeps its own type +
    /// message (Q3 — never mask an unrelated throw). (The discovery case — a single-gender GenderedItem formlink half —
    /// is now prevented at the root by <see cref="EmptyFormLinkOf"/>, so this stays as the general safety net for the
    /// composition null-arm that can still occur.) Unit-covered by nullarm-guard R1–R5.</summary>
    internal static NullReferenceException? RootNullArm(Exception ex)
    {
        static NullReferenceException? Root(Exception e)
        {
            while (e.InnerException is { } inner) e = inner;   // Mutagen wraps the writer NRE in a SubrecordException (parallel path)
            return e as NullReferenceException;
        }
        if (ex is AggregateException agg)
        {
            var leaves = agg.Flatten().InnerExceptions;
            if (leaves.Count == 0) return null;
            NullReferenceException? first = null;
            foreach (var leaf in leaves)
            {
                if (Root(leaf) is not { } nre) return null;   // a non-NRE-rooted leaf → not purely a null-arm failure
                first ??= nre;
            }
            return first;
        }
        return Root(ex);
    }

    /// <summary>Stage 1 of the atomic write: serialize the patch into a temp SUBDIRECTORY beside the target —
    /// same filename (Mutagen's writer ties filename to ModKey), same parent directory (guarantees same NTFS
    /// volume so the stage-2 rename is atomic). Does not open the target file itself. Cleans its temp on serialize
    /// failure (Q3 — a failed stage leaves no residue).</summary>
    static string WritePatchStaged(SkyrimMod patchMod, ISkyrimModGetter[] ordered, ModKey[] baseline, string outputPath)
    {
        var tmpDir = Path.Combine(Path.GetDirectoryName(outputPath)!, ".housecarl-tmp");
        var tmpPath = Path.Combine(tmpDir, Path.GetFileName(outputPath));
        Directory.CreateDirectory(tmpDir);
        try
        {
            patchMod.BeginWrite
                .ToPath(tmpPath)
                .WithLoadOrder(ordered)
                .WithExtraIncludedMasters(baseline)
                .NoNextFormIDProcessing()
                .Write();
            return tmpPath;
        }
        catch
        {
            CleanupStaged(tmpPath);
            throw;
        }
    }

    /// <summary>Stage 2 of the atomic write: swap the staged temp over the target via AtomicFile.Commit — crash-atomic
    /// File.Replace when the target exists, a rename when it does not (stage 1 guaranteed same-volume placement).
    /// Requires the PR #24 handle discipline to already hold (no handle of ours on the target). Temp is removed
    /// afterward; a cleanup failure never masks the result (Q3).</summary>
    static void CommitStagedPatch(string tmpPath, string outputPath)
    {
        try { AtomicFile.Commit(tmpPath, outputPath); }
        finally { CleanupStaged(tmpPath); }
    }

    static void CleanupStaged(string tmpPath)
    {
        try
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            var dir = Path.GetDirectoryName(tmpPath);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, recursive: false);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Serialize a plugin edited IN PLACE back over ITSELF — the model-C, xEdit-parity re-emit the Wave 0
    /// round-trip probe validated (in-place write lane, <c>WritePatchBuilder.ApplyInPlace</c>). DELIBERATELY NOT
    /// <see cref="WritePatch"/>: in-place re-emits an EXISTING authored plugin, so it must NOT apply WritePatch's
    /// NEW-patch conventions — no Skyrim.esm/Update.esm baseline force-include (<see cref="WritePatch"/>'s
    /// <c>WithExtraIncludedMasters</c> would ADD masters the author never declared, reindexing the file) and no
    /// <see cref="EnsureFormIdFloor"/> (<c>NoNextFormIDProcessing</c> persists the author's <c>HEDR.NextObjectID</c>
    /// verbatim). This is EXACTLY the probe's incantation (<c>RoundTripProbe</c>: <c>.WithLoadOrder(&lt;own declared
    /// masters&gt;).NoNextFormIDProcessing().Write()</c>) — the surface against which model C ("verify the touched
    /// records, trust Mutagen for the rest") was measured (~80% benign reorder, ZERO record drops), so routing in-place
    /// through it (not WritePatch) is what makes that result actually transfer. <paramref name="ownMasters"/> is the
    /// target's OWN declared masters, resolved to overlays in load order (Mutagen orders + lean-derives the list exactly
    /// as it did for the probe). Stage + crash-atomic swap (<see cref="AtomicFile.Commit"/>) is shared with the patch
    /// lane, so the original only ever holds the OLD or the NEW complete file. The caller MUST already hold the PR #24
    /// self-lock discipline (every overlay it holds on the target released) — here on a FOREIGN target.</summary>
    public static void WriteInPlace(SkyrimMod targetMod, IReadOnlyList<ISkyrimModGetter> ownMasters, string outputPath,
                                    string? dataDir)
    {
        var expected = targetMod.ModKey.FileName.String;
        var actual = Path.GetFileName(outputPath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"In-place output filename '{actual}' must match the target's ModKey filename '{expected}'.");

        // The localized-target choke point — the backstop for every caller of this method. It runs BEFORE the staging
        // directory exists and before the serialize, so a refused write leaves nothing at all behind.
        //
        // THE OUTCOME IS DECIDED OFF targetMod.UsingLocalization AND NOTHING ELSE. That is a fact about the mod this
        // call already holds in memory: it cannot fail to be read, so this refusal cannot fail to fire. An earlier
        // form of this branch made the decision itself a re-read of the destination file, whose fault path answered
        // "not localized" — and a destination held by another process for the instant of that read (an AV scan, MO2
        // refreshing, xEdit, the game) let the write through and replaced the user's plugin with one reading every
        // value empty. Measured. The re-read below supplies only the SENTENCE.
        //
        // EVERY localized shape is refused, the complete-loose-set one included. An earlier form of this branch let
        // that one shape through and committed the emitted tables over the user's live set; review measured that the
        // machinery making that survivable destroyed its own recovery set and instructed users into the very
        // corruption it existed to prevent, so the whole arm was cut (2026-08-26).
        if (targetMod.UsingLocalization)
            throw LocalizedTargetUnsupportedException.FromSentence(
                LocalizedTargetUnsupportedException.Shaped(expected, LocalizedStrings.Assess(outputPath, dataDir)));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var ordered = ownMasters as ISkyrimModGetter[] ?? ownMasters.ToArray();
        // Same composed-null-arm serialize guard as WritePatch (an in-place edit can compose a record too): a required
        // sub-field left null surfaces as a bare NRE at the writer deref, OR — from the parallel writer — as an
        // AggregateException-wrapped one; RootNullArm unwraps both and re-stamps ONLY that as a NAMED refusal; the staged
        // temp is already discarded on any throw (nothing on disk), original byte-intact.
        string staged;
        try { staged = WriteInPlaceStaged(targetMod, ordered, outputPath); }
        catch (Exception ex) when (RootNullArm(ex) is { } nre) { throw new NullArmSerializeException(nre); }
        // A localized target never reaches here (refused above), so the serialize emitted no tables and this is the
        // single-file atomic swap it has always been.
        CommitStagedPatch(staged, outputPath);
    }

    /// <summary>Is the plugin at <paramref name="path"/> flagged LOCALIZED — read it, and say which of THREE things
    /// happened. The pre-flight read behind <see cref="LocalizedStrings.Assess"/>, and through it every lane that has
    /// to answer BEFORE reaching the write: a dry run (whose job is to give the same answer the real call would), and
    /// any lane whose refusal carries a remedy clause of its own.
    ///
    /// <para>Reads the header only, and deliberately with the BARE overlay: the localized FLAG is in the header, so
    /// unlike reading the strings themselves this needs no game-Data fallback and cannot be wrong about the flag
    /// because a strings source is missing.</para>
    ///
    /// <para><b>It returns three answers rather than a bool, and that is the point.</b> The bool form answered
    /// <c>false</c> on any fault, and its docstring justified that by pointing at the write's own choke point as the
    /// backstop — but this read had by then become that choke point's own decision, so the net it named was the net
    /// it was inside. A locked destination classified as not-localized and the write proceeded. Callers must handle
    /// <see cref="LocalizedFlagRead.Unreadable"/> as unknown; the compiler makes them.</para></summary>
    public static LocalizedFlagRead PluginIsLocalized(string path)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            return ov.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Localized)
                ? LocalizedFlagRead.Localized
                : LocalizedFlagRead.NotLocalized;
        }
        catch { return LocalizedFlagRead.Unreadable; }
        finally { if (ov is IDisposable d) { try { d.Dispose(); } catch { } } }
    }

    /// <summary>Stage 1 of the in-place write — the probe-faithful re-serialize: own declared masters as the load order,
    /// the counter persisted verbatim (<c>NoNextFormIDProcessing</c>, NO floor), NO baseline force-include. Stages into
    /// the <c>.housecarl-tmp</c> sibling of the target (same parent ⇒ same NTFS volume ⇒ the stage-2 swap is atomic),
    /// and cleans its temp on a serialize failure (Q3 — a failed stage leaves no residue, the original untouched).</summary>
    static string WriteInPlaceStaged(SkyrimMod targetMod, ISkyrimModGetter[] ordered, string outputPath)
    {
        var tmpDir = Path.Combine(Path.GetDirectoryName(outputPath)!, ".housecarl-tmp");
        var tmpPath = Path.Combine(tmpDir, Path.GetFileName(outputPath));
        Directory.CreateDirectory(tmpDir);
        try
        {
            targetMod.BeginWrite
                .ToPath(tmpPath)
                .WithLoadOrder(ordered)            // the target's OWN masters (no whole-order, no baseline) — the probe's set
                .NoNextFormIDProcessing()          // persist the author's NextObjectID verbatim (no EnsureFormIdFloor)
                .Write();
            return tmpPath;
        }
        catch
        {
            CleanupStaged(tmpPath);
            throw;
        }
    }

    /// <summary>The base-game masters EVERY Skyrim plugin must carry — Skyrim.esm + Update.esm, exactly what the Creation
    /// Kit stamps on every plugin (Aaron 2026-06-02: "if ck stamps both then we should too"). Force-included on every
    /// <see cref="WritePatch(SkyrimMod, IReadOnlyList{ISkyrimModGetter},string)"/> via WithExtraIncludedMasters so even a
    /// self-contained created record yields a valid, conventionally-mastered plugin. Both ship with SE → always present in
    /// the order, so this never fails; the load order sorts them (Skyrim.esm before Update.esm). Proven by master-probe.</summary>
    internal static readonly ModKey[] BaselineMasters = { new("Skyrim", ModType.Master), new("Update", ModType.Master) };   // internal: the dry-run master preview mirrors the force-include (#225)

    // ======================================================================
    //  PATH NAVIGATION + VERBS  (plan §4.2 / §3 P-VERBS; the highest-risk delta)
    // ======================================================================

    /// <summary>
    /// Parse one path segment into (field name, optional collection key/index). <c>Effects[0]</c> →
    /// ("Effects","0"); <c>Foo</c> → ("Foo", null). Brackets carry MID-PATH collection navigation only — this is
    /// the boundary parse from the textual path skin to the engine's typed per-hop form (the user-facing wire
    /// format proper is a later MCP-API decision, kept out of the engine). Fails LOUD on a malformed bracket
    /// (Q3 — a silent misparse could retarget a write). The leaf uses <see cref="WriteRequest.Key"/>, never a
    /// bracket; both <see cref="ApplyVerb"/> and the rulebook reject a bracketed LEAF segment.
    /// </summary>
    internal static (string name, string? key) ParseSegment(string segment)
    {
        var open = segment.IndexOf('[');
        if (open < 0)
        {
            if (segment.Contains(']'))
                throw new InvalidOperationException($"Malformed path segment '{segment}': ']' without a matching '['.");
            return (segment, null);
        }
        if (!segment.EndsWith("]", StringComparison.Ordinal))
            throw new InvalidOperationException($"Malformed path segment '{segment}': '[' must be closed by ']' at the segment end.");
        var name = segment[..open];
        var key = segment[(open + 1)..^1];
        if (name.Length == 0) throw new InvalidOperationException($"Malformed path segment '{segment}': no field name before '['.");
        if (key.Length == 0) throw new InvalidOperationException($"Malformed path segment '{segment}': empty index/key in '[]'.");
        if (key.Contains('[') || key.Contains(']'))
            throw new InvalidOperationException($"Malformed path segment '{segment}': nested or extra brackets.");
        return (name, key);
    }

    /// <summary>
    /// Walk <c>req.Path</c> from the record root, then apply <c>req.Verb</c> at the leaf. A plain hop descends a
    /// substruct (materializing an absent one); a bracketed hop (<c>Effects[0]</c>) steps INTO a collection
    /// element (wave-1 collection-nav). Dispatch at the leaf is on its <i>runtime</i> shape (dict / list /
    /// scalar), so execution stays corpus-independent: the corpus drives pre-flight (<see cref="CorpusRulebook"/>),
    /// reflection drives the write.
    /// </summary>
    public static void ApplyVerb(object record, WriteRequest req)
    {
        object current = record;
        for (int i = 0; i < req.Path.Length - 1; i++)
        {
            var (segName, segKey) = ParseSegment(req.Path[i]);
            var p = ResolveProperty(current.GetType(), segName)
                ?? throw new InvalidOperationException($"No property '{segName}' on {current.GetType().Name}");
            // No bracket → descend a substruct. Writable-by-construction: an ABSENT intermediate optional substruct
            // (null) is materialized so a field inside it can be set — "set a field in a data block the record lacks"
            // must work, not throw. Multi-level absent chains materialize one hop at a time. (Other half of approach A.)
            // A bracket (Effects[0]) → step INTO that collection element and keep descending (wave-1 collection-nav).
            current = segKey is null
                ? (p.GetValue(current) ?? MaterializeSubstruct(current, p, segName))
                : StepIntoElement(current, p, segName, segKey, materialize: true);   // write path may materialize a gendered arm on demand
        }
        var (leafName, leafKey) = ParseSegment(req.Path[^1]);
        if (leafKey is not null)
        {
            // A gendered field at the LEAF (Set Priority[0]) renders as [0]/[1] but is NOT a list/dict — redirect to the
            // named halves, not the list-verb message. Runtime twin of CorpusRulebook's "GenderedItem<" leaf recogniser
            // (two recognisers that must agree). Pre-flight normally gates this first; the engine keeps the message honest
            // for any direct / CLI --op call that bypasses pre-flight. (HCBR-2026-06-15-01 PR-H follow-up.)
            var bracketProp = ResolveProperty(current.GetType(), leafName);
            if (bracketProp is { } gp && GenderedInterface(gp.PropertyType) is not null)
                throw new InvalidOperationException(
                    $"Gendered field '{leafName}' on {current.GetType().Name} renders as [0]/[1] but is not a list — set " +
                    $"its halves by name: '{leafName}.Male' (=[0]) / '{leafName}.Female' (=[1]).");
            // The gate's twin, and now the same DERIVATION rather than the same hand-typed recital. The corpus is
            // not in scope here — the engine is schema-blind by design — so the shape comes off the live property
            // type; WriteVerbs.OfRuntimeType and its schema-side sibling are held to the same answer on every
            // collection field in the corpus by remedy-verbs-guard. A property that resolves to no collection at
            // all still gets the rule, with no verb named for it.
            var head = $"Path segment '{req.Path[^1]}' brackets a collection element at the LEAF. Brackets navigate "
                       + "mid-path only; ";
            if (bracketProp is not null && WriteVerbs.OfRuntimeType(bracketProp.PropertyType) is { } bshape)
                throw new InvalidOperationException(head
                    + $"to operate on an element of '{leafName}', target the field itself and use the verb + Key — "
                    + $"{WriteVerbs.HowToAddress(bshape)}.");
            throw new InvalidOperationException(head
                + "to operate on a collection element at the leaf, target the collection field and use the verb + Key.");
        }
        var leaf = ResolveProperty(current.GetType(), leafName)
            ?? throw new InvalidOperationException($"No property '{leafName}' on {current.GetType().Name}");

        // Whole-value-coercible leaves (Color, MemorySlice blobs, AssetLink paths, …) are Set wholesale even when
        // the runtime type also implements IList/IDict — coercion owns them, not the collection verbs.
        if (CanCoerce(leaf.PropertyType)) { ApplyScalarVerb(current, leaf, req); return; }

        var dictIface = ClosedInterface(leaf.PropertyType, typeof(IDictionary<,>));
        if (dictIface is not null) { ApplyDictVerb(current, leaf, dictIface, req); return; }

        var listIface = ClosedInterface(leaf.PropertyType, typeof(IList<>));
        if (listIface is not null) { ApplyListVerb(current, leaf, listIface, req); return; }

        ApplyScalarVerb(current, leaf, req);
    }

    // ======================================================================
    //  P8b — CopyFrom: transplant a FIELD's value from another plugin's version of a record into the patch's copy.
    //  The reflection-generic generalisation of the hand-typed NpcAppearanceCopy.CopyAppearanceFields: by construction,
    //  every transplantable field KIND is covered by the shape of the target property + source value, not a per-type
    //  list. Owned-child record collections are refused at PRE-FLIGHT (CorpusRulebook.CopyFromLegality) — this only
    //  ever runs on a transplantable leaf. Byte-identity of copy-then-readback is proven by bulk-primitives-wave3-guard.
    // ======================================================================

    /// <summary>Deep-copy the value at <paramref name="path"/> from <paramref name="source"/>'s version of a record into
    /// the patch's settable copy <paramref name="target"/>. An ABSENT/null source value is refused (nothing to copy) —
    /// never a silent destructive clear (Q3; use Remove to clear). Throws an <see cref="ExpectedApplyRejectionException"/>
    /// for a clean live-state refusal (absent source), else a plain throw for a genuine engine inconsistency (surfaced,
    /// all-or-nothing at the cleave). The source overlay must stay OPEN through the patch serialize (the cleave holds its
    /// session; an off-order source overlay is held by the service) — reference-shared immutables (strings, formlink
    /// getters) are only valid while it is.</summary>
    public static void CopyField(IMajorRecordGetter source, IMajorRecord target, string[] path)
    {
        // --- navigate the SOURCE (read-only, never materialize) to the leaf's value ---
        object srcCur = source;
        for (int i = 0; i < path.Length - 1; i++)
        {
            var (segName, segKey) = ParseSegment(path[i]);
            var p = ResolveProperty(srcCur.GetType(), segName)
                ?? throw new InvalidOperationException($"CopyFrom: the source's version has no field '{segName}' on {srcCur.GetType().Name}.");
            var next = segKey is null ? p.GetValue(srcCur) : StepIntoElement(srcCur, p, segName, segKey);
            if (next is null)
                throw new ExpectedApplyRejectionException(
                    $"CopyFrom: the source plugin's version has no value at '{string.Join('.', path[..(i + 1)])}' — nothing to copy.");
            srcCur = next;
        }
        var (leafName, leafKey) = ParseSegment(path[^1]);
        if (leafKey is not null)
            throw new InvalidOperationException(
                "CopyFrom copies a WHOLE field (its entire contents) — brackets at the leaf aren't supported; name the collection/field itself.");
        var srcLeaf = ResolveProperty(srcCur.GetType(), leafName)
            ?? throw new InvalidOperationException($"CopyFrom: the source's version has no field '{leafName}' on {srcCur.GetType().Name}.");
        var srcVal = srcLeaf.GetValue(srcCur);
        if (srcVal is null)
            throw new ExpectedApplyRejectionException(
                $"CopyFrom: the source plugin's version has '{string.Join('.', path)}' unset — nothing to copy (use verb=Remove to clear the target).");

        // --- navigate the TARGET (materialize absent intermediate substructs, exactly like ApplyVerb) to the leaf's owner ---
        object tgtCur = target;
        for (int i = 0; i < path.Length - 1; i++)
        {
            var (segName, segKey) = ParseSegment(path[i]);
            var p = ResolveProperty(tgtCur.GetType(), segName)
                ?? throw new InvalidOperationException($"CopyFrom: the target has no field '{segName}' on {tgtCur.GetType().Name}.");
            tgtCur = segKey is null
                ? (p.GetValue(tgtCur) ?? MaterializeSubstruct(tgtCur, p, segName))
                : StepIntoElement(tgtCur, p, segName, segKey, materialize: true);
        }
        var tgtLeaf = ResolveProperty(tgtCur.GetType(), leafName)
            ?? throw new InvalidOperationException($"CopyFrom: the target has no field '{leafName}' on {tgtCur.GetType().Name}.");

        TransplantValue(tgtCur, tgtLeaf, srcVal);
    }

    /// <summary>Assign a getter-side <paramref name="srcVal"/> into leaf <paramref name="prop"/> on
    /// <paramref name="parent"/>, deep-copying so the patch owns its own instances. Three property shapes cover every
    /// transplantable kind: a SETTABLE property (a value assigned directly; a Loqui getter DeepCopy'd; a modeled/formlink
    /// list rebuilt from copied elements); a GET-ONLY FormLink (SetTo the source FormKey); a GET-ONLY collection (its
    /// contents replaced with copied elements). A shape it can't place is a loud throw (Q3 — never a silent partial copy;
    /// pre-flight already excluded owned-child records).</summary>
    static void TransplantValue(object parent, PropertyInfo prop, object srcVal)
    {
        var pt = prop.PropertyType;
        // Array-backed collections (a fixed-size game structure, e.g. Weather clouds) implement IList<T> but can't be
        // Activator-instantiated without a length — the write verbs already refuse them; CopyFrom does too, CLEANLY (a
        // clean apply-time refusal, not the "pre-flight accepted but apply threw" inconsistency wrapper). Tracked gap.
        if (pt.IsArray)
            throw new ExpectedApplyRejectionException(
                $"CopyFrom does not transplant the array-backed collection '{prop.Name}' ({Pretty(pt)}) — a fixed-size game structure; a tracked gap, mirroring the write verbs.");
        if (prop.CanWrite)
        {
            // a value/enum/struct-value (int, float, enum, Color, Percent, FormKey…) — copied by value on assign
            if (srcVal.GetType().IsValueType && pt.IsInstanceOfType(srcVal)) { prop.SetValue(parent, srcVal); return; }
            // a settable modeled collection (ExtendedList<T>? — Keywords, Perks, Effects…) — rebuild from copied elements
            if (ClosedInterface(pt, typeof(IList<>)) is { } wlif && srcVal is System.Collections.IEnumerable wsrc)
            { prop.SetValue(parent, BuildCopiedList(pt, wlif.GetGenericArguments()[0], wsrc)); return; }
            // a Loqui sub-object / TranslatedString / any getter with a generated DeepCopy() — deep copy to the settable concrete
            if (TryDeepCopy(srcVal) is { } deep && pt.IsInstanceOfType(deep)) { prop.SetValue(parent, deep); return; }
            // a directly-assignable immutable reference (string, MemorySlice…) — safe to share while the source overlay lives
            if (pt.IsInstanceOfType(srcVal)) { prop.SetValue(parent, srcVal); return; }
            // a settable FormLink slot (rare) — build the matching concrete from the source key
            if (srcVal is IFormLinkGetter sfl && TryFormLink(sfl.FormKey.ToString(), Nullable.GetUnderlyingType(pt) ?? pt, out var mk)
                && mk is not null && pt.IsInstanceOfType(mk)) { prop.SetValue(parent, mk); return; }
            throw new ExpectedApplyRejectionException(
                $"CopyFrom cannot assign a {Pretty(srcVal.GetType())} into settable '{prop.Name}' ({Pretty(pt)}) — a field kind CopyFrom doesn't transplant yet (a clean refusal, not a silent skip).");
        }

        // get-only: mutate the live instance in place
        if (srcVal is IFormLinkGetter fl)   // get-only FormLink (IFormLink<T> / IFormLinkNullable<T>) → SetTo the source key
        {
            var live = prop.GetValue(parent)
                ?? throw new InvalidOperationException($"CopyFrom: get-only formlink '{prop.Name}' is null on the target.");
            InvokeSetTo(live, fl.FormKey); return;
        }
        if (ClosedInterface(pt, typeof(IList<>)) is { } lif && srcVal is System.Collections.IEnumerable lsrc)
        {
            var live = prop.GetValue(parent)
                ?? throw new InvalidOperationException($"CopyFrom: get-only collection '{prop.Name}' is null on the target.");
            ReplaceListInPlace(live, lif.GetGenericArguments()[0], lsrc); return;
        }
        throw new ExpectedApplyRejectionException(
            $"CopyFrom cannot transplant get-only '{prop.Name}' ({Pretty(pt)}) — not a formlink or collection (a field kind CopyFrom doesn't transplant yet; a clean refusal, not a silent skip).");
    }

    // Cache of the DeepCopy method (instance or Mutagen extension) per getter runtime type — the reflection search below
    // is done ONCE per type. A null entry means "no DeepCopy" (a plain scalar/value) — memoised too.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (MethodInfo? m, bool isExtension)> _deepCopyOf = new();

    /// <summary>A DETACHED deep copy of a whole record, or null if Mutagen models no DeepCopy for its type (the
    /// caller then REFUSES — falling back to the live record is the bug this exists to prevent). Used by the
    /// in-place lane to snapshot a copy source that lives in the file being rewritten: the source must not be the
    /// live mutable record, or (a) later ops in the same call would be visible to it, and (b) the element-level
    /// share in <see cref="CopyElement"/> — safe when a source is a read-only overlay, since an overlay element is
    /// never the settable concrete type — would alias the two records' collection elements.</summary>
    internal static IMajorRecordGetter? TryDeepCopyRecord(IMajorRecordGetter record)
        => TryDeepCopy(record) as IMajorRecordGetter;

    /// <summary>DeepCopy a Loqui getter to its settable concrete. Mutagen generates DeepCopy as an EXTENSION method
    /// (<c>SomethingMixIn.DeepCopy(this ISomethingGetter, TranslationMask? = null)</c>), not a parameterless instance
    /// method, so this finds and invokes it (or a same-shape instance overload where one exists). Returns null when the
    /// value has no DeepCopy (a plain scalar/value/string — the caller then assigns it directly). Per-type memoised.</summary>
    static object? TryDeepCopy(object val)
    {
        var t = val.GetType();
        var (m, isExtension) = _deepCopyOf.GetOrAdd(t, FindDeepCopy);
        if (m is null) return null;
        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        int start = 0;
        if (isExtension) { args[0] = val; start = 1; }
        for (int i = start; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        return m.Invoke(isExtension ? null : val, args);
    }

    /// <summary>Locate a DeepCopy for <paramref name="getterType"/>: first a same-shape INSTANCE overload (all args
    /// optional), else the Mutagen static EXTENSION (<c>DeepCopy(this &lt;getter&gt;, optional…)</c>) — the most-derived
    /// first-parameter among matches (so a specific arm's extension is preferred over a base one). Non-void, non-generic.</summary>
    static (MethodInfo? m, bool isExtension) FindDeepCopy(Type getterType)
    {
        var inst = getterType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(mm => mm.Name == "DeepCopy" && mm.ReturnType != typeof(void) && !mm.IsGenericMethodDefinition
                      && mm.GetParameters().All(pp => pp.IsOptional))
            .OrderBy(mm => mm.GetParameters().Length).FirstOrDefault();
        if (inst is not null) return (inst, false);

        MethodInfo? best = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
            foreach (var type in SafeTypes(asm))
            {
                if (!(type.IsAbstract && type.IsSealed)) continue;   // a C# static class (holds extension methods)
                foreach (var mm in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mm.Name != "DeepCopy" || mm.ReturnType == typeof(void) || mm.IsGenericMethodDefinition) continue;
                    var ps = mm.GetParameters();
                    if (ps.Length == 0 || !ps[0].ParameterType.IsAssignableFrom(getterType)) continue;   // receiver accepts this getter
                    if (!ps.Skip(1).All(pp => pp.IsOptional)) continue;                                   // every other arg optional
                    if (best is null || best.GetParameters()[0].ParameterType.IsAssignableFrom(ps[0].ParameterType))
                        best = mm;   // prefer the most-derived receiver type among matches
                }
            }
        return (best, true);
    }

    /// <summary>Copy ONE collection element to a target element of <paramref name="elemType"/>: a formlink/value element
    /// the target already accepts passes through (immutable while the source overlay lives); a Loqui element is
    /// DeepCopy'd. Loud on an unhandled element kind.</summary>
    static object CopyElement(Type elemType, object elem)
    {
        if (elemType.IsInstanceOfType(elem)) return elem;                                   // formlink getter / value — share is safe
        if (TryDeepCopy(elem) is { } deep && elemType.IsInstanceOfType(deep)) return deep;   // Loqui element → settable copy
        throw new InvalidOperationException($"CopyFrom: cannot copy a {Pretty(elem.GetType())} element into a {Pretty(elemType)} list.");
    }

    /// <summary>Build a fresh settable collection of <paramref name="collType"/> (ExtendedList&lt;T&gt;) holding copies of
    /// every element of <paramref name="src"/> — the settable-collection transplant.</summary>
    static object BuildCopiedList(Type collType, Type elemType, System.Collections.IEnumerable src)
    {
        var list = System.Activator.CreateInstance(collType)
            ?? throw new InvalidOperationException($"CopyFrom: could not instantiate collection {Pretty(collType)}.");
        var add = list.GetType().GetMethod("Add", new[] { elemType })
            ?? throw new InvalidOperationException($"CopyFrom: no Add({Pretty(elemType)}) on {Pretty(list.GetType())}.");
        foreach (var e in src) if (e is not null) add.Invoke(list, new[] { CopyElement(elemType, e) });
        return list;
    }

    /// <summary>Replace a live get-only collection's contents with copies of every element of <paramref name="src"/>
    /// (Clear then Add) — the get-only-collection transplant.</summary>
    static void ReplaceListInPlace(object live, Type elemType, System.Collections.IEnumerable src)
    {
        var lt = live.GetType();
        lt.GetMethod("Clear")!.Invoke(live, null);
        var add = lt.GetMethod("Add", new[] { elemType })
            ?? throw new InvalidOperationException($"CopyFrom: no Add({Pretty(elemType)}) on {Pretty(lt)}.");
        foreach (var e in src) if (e is not null) add.Invoke(live, new[] { CopyElement(elemType, e) });
    }

    /// <summary>Reflectively call <c>SetTo(FormKey)</c> on a live get-only FormLink (IFormLink&lt;T&gt; /
    /// IFormLinkNullable&lt;T&gt;) — the same mutation NpcAppearanceCopy does typed.</summary>
    static void InvokeSetTo(object link, FormKey fk)
    {
        var m = link.GetType().GetMethod("SetTo", new[] { typeof(FormKey) });
        if (m is not null) { m.Invoke(link, new object[] { fk }); return; }
        var mn = link.GetType().GetMethod("SetTo", new[] { typeof(FormKey?) });
        if (mn is not null) { mn.Invoke(link, new object?[] { (FormKey?)fk }); return; }
        throw new InvalidOperationException($"CopyFrom: no SetTo(FormKey) on formlink {Pretty(link.GetType())}.");
    }

    static void ApplyScalarVerb(object parent, PropertyInfo prop, WriteRequest req)
    {
        if (!prop.CanWrite) throw new InvalidOperationException($"Property '{prop.Name}' is not writable");
        if (req.Verb == "Set" && req.Struct is not null) { prop.SetValue(parent, BuildStruct(req.Struct)); return; }

        // Parent-aware FormLinkOrIndex (condition-data targets, wave 4): the concrete ctor needs the owning ARM
        // (parent) as its discriminator-flag source, so an FLOI cannot go through the parentless Coerce path. The
        // engine auto-infers form-vs-index from the value and sets the arm's flag to match (Aaron 2026-05-31; scout
        // §E.1). Recognised by the generic definition (IsFormLinkOrIndex) — no per-record-type wiring.
        if (req.Verb == "Set" && IsFormLinkOrIndex(prop.PropertyType)) { SetFloi(parent, prop, req.Value!); return; }

        // Add / valued-Remove on a [Flags] enum are BIT operations (HCBR-2026-07-15), NOT whole-value Set/clear: Add
        // ORs the operand's bit(s) into the current value, Remove ANDs them out — so a single flag flips without the
        // caller re-listing every OTHER bit (the silent-clobber this closes: a literal Set dropped every unlisted bit).
        // Gated to [Flags] enums. A VALUELESS Remove is NOT a bit op — it keeps its pre-bit-verb meaning (the whole-field
        // clear of a nullable scalar, the case below), so it falls through here; that preserves the only path to make a
        // nullable flags field absent (pre-flight admits it only when nullable). Add on a non-flags scalar still hits the
        // default reject. Pre-flight validated the operand, but we fail LOUD here for a pre-flight-bypassing caller (Q3).
        if (req.Verb == "Add" || (req.Verb == "Remove" && req.Value is not null))
        {
            var ut = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (ut.IsEnum && ut.IsDefined(typeof(FlagsAttribute), false)) { ApplyFlagsBitVerb(parent, prop, ut, req); return; }
        }

        switch (req.Verb)
        {
            case "Set":
                prop.SetValue(parent, Coerce(req.Value!, prop.PropertyType));
                break;
            case "Remove": // clear a nullable scalar / substruct / formlink / polymorphic
                // A FormLink field is a struct-backed slot whose setter REJECTS a null reference, so SetValue(null)
                // threw TargetInvocationException at apply even though pre-flight accepted the Remove (HCBR-2026-07-06;
                // the Set "000000:…" workaround dodged it by coercing an EMPTY link). Route the clear through
                // EmptyFormLinkOf: a FormLink-family type → its empty link (FormKey.Null — a TRUE null link, cleaner
                // than the workaround's 000000:Skyrim.esm), every other nullable scalar / substruct / polymorphic → null,
                // exactly as before (EmptyFormLinkOf returns null for non-FormLink types). This makes Remove on a
                // nullable FormLink identical to Set = "0" (a null-synonym clear), which already worked.
                //
                // Q3 belt-and-suspenders (PR #150 review): EmptyFormLinkOf also produces a non-null empty link for a
                // REQUIRED FormLink, which would SILENTLY blank a required target. Pre-flight (CorpusRulebook) already
                // refuses Remove on a non-nullable link, but ApplyVerb does no validation, so a direct/CLI caller that
                // bypasses pre-flight must still fail LOUD rather than write an empty required link. Mirrors the
                // gendered-leaf throw above — the engine keeps the message honest for pre-flight-bypassing callers.
                if (IsRequiredFormLink(prop.PropertyType))
                    throw new InvalidOperationException(
                        $"Remove is not valid on the required (non-nullable) FormLink '{prop.Name}' — a required link " +
                        "can't be dropped, only re-pointed. To clear it to a null link, Set it to a null-synonym value (\"0\").");
                prop.SetValue(parent, EmptyFormLinkOf(prop.PropertyType));
                break;
            default:
                throw new InvalidOperationException($"Verb '{req.Verb}' is not valid on scalar/substruct '{prop.Name}'.");
        }
    }

    /// <summary>Flags-enum bit op (HCBR-2026-07-15): OR (<c>Add</c>) or AND-NOT (<c>Remove</c>) the operand's bit(s)
    /// into the leaf's CURRENT value, so one flag flips while every other bit is preserved — the fix for the
    /// silent-clobber a literal <see cref="Coerce"/> Set caused (an unlisted bit was dropped). The operand is resolved
    /// through the SAME enum coercion a Set uses (<see cref="Coerce"/> → <c>Enum.Parse</c>: a flag NAME, a
    /// case-insensitive comma-combo, or a decimal literal) so pre-flight (CheckValue → TryCoerce) and apply can't
    /// disagree on a legal operand; its bits and the current value's bits are read through
    /// <see cref="ReadEngine.TryEnumBits"/> (robust across every underlying integer type). The combined pattern is
    /// re-boxed to the enum type via <c>Enum.ToObject</c>. Called only for a confirmed <c>[Flags]</c> leaf.</summary>
    static void ApplyFlagsBitVerb(object parent, PropertyInfo prop, Type enumType, WriteRequest req)
    {
        if (!prop.CanWrite) throw new InvalidOperationException($"Property '{prop.Name}' is not writable");
        if (req.Value is null)
            throw new InvalidOperationException($"Flags {req.Verb} on '{prop.Name}' requires a flag value (the bit to {(req.Verb == "Add" ? "set" : "clear")}).");

        var current = prop.GetValue(parent);
        ulong curBits = 0;
        if (current is not null && !ReadEngine.TryEnumBits(current, enumType, out curBits))
            throw new InvalidOperationException($"Flags {req.Verb} on '{prop.Name}': could not read the current {enumType.Name} value as bits.");

        var opVal = Coerce(req.Value, enumType);   // Enum.Parse via the enum coercion family — fail-loud on a bad flag
        if (opVal is null || !ReadEngine.TryEnumBits(opVal, enumType, out var opBits))
            throw new InvalidOperationException($"Flags {req.Verb} on '{prop.Name}': '{req.Value}' is not a legal {enumType.Name} flag name or bit value.");

        ulong combined = req.Verb == "Add" ? (curBits | opBits) : (curBits & ~opBits);
        prop.SetValue(parent, Enum.ToObject(enumType, combined));
    }

    /// <summary>
    /// Build a modeled struct FROM PARTS — the ONE composition primitive (wave-1 half B), generalizing the prior
    /// BuildArm. Resolve the concrete type, instantiate it (parameterless, or via positional <see cref="StructSpec.CtorArgs"/>
    /// for discriminator-/composition-ctor types), apply the flat <see cref="StructSpec.Fields"/> (a coerced Set-leaf
    /// each), then apply the general nested <see cref="StructSpec.Sets"/> THROUGH <see cref="ApplyVerb"/> itself — so
    /// nested sub-structs, struct-element Adds, and lists are handled by the same proven path, and a built struct can
    /// never miss a field kind the engine already handles. Used for: a polymorphic-arm Set, and a struct-element Add.
    /// </summary>
    static object BuildStruct(StructSpec spec)
    {
        var type = ResolveStructType(spec.Type);
        var instance = Instantiate(type, spec.CtorArgs);
        foreach (var (name, val) in spec.Fields ?? new())
        {
            var p = ResolveProperty(type, name)
                ?? throw new InvalidOperationException($"No field '{name}' on '{spec.Type}'");
            if (!p.CanWrite) throw new InvalidOperationException($"Field '{name}' on '{spec.Type}' is not writable");
            // A condition FormLinkOrIndex field (GetEquipped.ItemOrList, GetGlobalValue.Global, …) needs the parent-aware
            // SetFloi: the just-built `instance` IS the flag-bearing arm, so its UseAliases/UsePackageData mode is set to
            // match the value. The parentless Coerce rejects FLOI (it has no arm). Mirrors ApplyScalarVerb's FLOI gate so
            // the flat-field and nested-Sets compose paths produce the IDENTICAL write. (Pre-flight already validated val.)
            if (IsFormLinkOrIndex(p.PropertyType)) SetFloi(instance, p, val);
            else p.SetValue(instance, Coerce(val, p.PropertyType));
        }
        foreach (var req in spec.Sets ?? new())
            ApplyVerb(instance, req);     // general nested writes — reuse the verb engine; recurses on struct-element Adds
        if (EmptyComposeRefusal(spec, type, instance) is { } why) throw new ExpectedApplyRejectionException(why);
        return instance;
    }

    /// <summary>#308 — refuse a compose the caller gave NOTHING to, whose built object carries nothing to serialize.
    /// The reported case: <c>Add Ranks compose={"type":"Rank"}</c>. Mutagen builds the Rank, the list holds it, and
    /// the writer emits ZERO bytes for it — so the plugin does not grow, every later read shows the list still empty,
    /// and (before this) the write reported it as landed. Q3: a write that cannot land must be refused, not reported.
    ///
    /// <para>Deliberately NARROW, because "would this serialize?" is Mutagen's question and not one this engine can
    /// answer in general. It fires only when the caller supplied literally nothing — no <c>fields</c>, no nested
    /// <c>sets</c>, no <c>ctor_args</c> — AND every SETTABLE property of the built object is null. A type with a
    /// non-nullable member (an enum, a number, a struct) never trips it, because that member has a value and
    /// serializes; a ctor-arg or discriminator compose never trips it, because the caller supplied the discriminator;
    /// and read-only reflection surface (Loqui's <c>StaticRegistration</c>) is excluded, because a compose cannot set
    /// it and it is not content. What is NOT an exemption, though an earlier draft of this paragraph said so: the
    /// TYPE itself. A polymorphic arm survives because Mutagen's arm types carry a non-nullable member, not because
    /// naming the arm counts as content — so an arm whose whole settable surface is nullable references WOULD be
    /// refused here, with advice to name a field, when the type was the content (review [low]). I have NOT found such
    /// an arm, and I have not proven there is none — that is an assertion about the whole modelled universe with no
    /// probe behind it, in a codebase whose cornerstone is that coverage claims are derived (review [low], round 9).
    /// The by-construction form is cheap and worth adding: an arm asserting every <see cref="IsPlainComposableStruct"/>
    /// type carries at least one non-nullable settable property. Until then this paragraph is a bound, not a fact. The general case — a struct that serializes to less than the caller believes — is
    /// caught from the other end: <c>WritePatchBuilder.VerifyLandedAgainstFile</c> reports content that is GONE (a
    /// count that moved, a leaf that now holds nothing), which is where an unserializable struct usually lands — but
    /// NOT an element that lands with fewer fields than supplied, because that cannot be told from the format
    /// representing a value its own way. Claimed here as more than that until a review proved the probe inert.</para>
    ///
    /// <para>Names what to set from the TYPE itself (settable properties, reflection-derived), never a hand-kept list
    /// per struct — the same by-construction rule the rest of the write surface follows.</para></summary>
    static string? EmptyComposeRefusal(StructSpec spec, Type type, object instance)
    {
        // `Count: > 0` on ctor_args too (review [nit]): an EMPTY array is "the 0-arg ctor", not a supplied
        // discriminator, so reading it as one let the empty compose through the check it should meet.
        if (spec.CtorArgs is { Length: > 0 } || spec.Fields is { Count: > 0 } || spec.Sets is { Count: > 0 }) return null;
        // Instance, non-indexed properties ONLY, in a STABLE order (reviews): the default flags include public
        // STATICS — one with a value would suppress the refusal entirely while being listed as something a compose
        // could set — an INDEXER throws TargetParameterCountException in GetValue below, whose catch would bail the
        // whole check out, and GetProperties' order is unspecified by the CLR, so the worked example could name a
        // different field between runtimes.
        var settable = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                           .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
                           .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        if (settable.Count == 0) return null;                      // nothing to advise; not this check's case
        foreach (var p in settable)
        {
            object? v;
            try { v = p.GetValue(instance); } catch { return null; }   // unreadable ⇒ can't claim emptiness (Q3)
            if (v is not null) return null;                        // it carries something — let the write proceed
        }
        // The worked example is the line a caller COPIES, so it names a field they can actually pass as a string —
        // a scalar, never plumbing (review [low]: the ordinal sort that made the choice deterministic also made it
        // `FemaleTitle` for a Rank, where `Number` is the obvious field). The full list below is unchanged.
        static bool IsSimple(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u.IsPrimitive || u.IsEnum || u == typeof(string) || u == typeof(decimal);
        }
        var usable = settable.Where(p => !p.Name.StartsWith("Unknown", StringComparison.Ordinal)
                                      && !p.Name.Equals("Versioning", StringComparison.Ordinal)).ToList();
        var example = usable.FirstOrDefault(p => IsSimple(p.PropertyType)) ?? usable.FirstOrDefault() ?? settable[0];
        // Worded for ANY compose, not just a list Add (review [low]): BuildStruct also serves a polymorphic-arm Set
        // and SetAtIndex, and a caller who hit this on `Set Archetype compose={…}` was given advice about a list.
        return $"compose type='{spec.Type}' was given no fields, and a {spec.Type} built from nothing has no " +
               "serializable content: it would exist in memory and be written as ZERO bytes, so the field would be " +
               "unchanged on disk while the call reported success (#308). Name at least one field — e.g. " +
               $"compose={{\"type\":\"{spec.Type}\",\"fields\":{{\"{example.Name}\":\"<value>\"}}}}. Settable " +
               $"fields on {spec.Type}: {string.Join(", ", settable.Select(p => p.Name))}. " +
               "(This also refuses the two-step shape — compose an empty value, then set its fields in a LATER op of " +
               "the same call — which did work: the check runs as the value is built and cannot see the ops after " +
               "it. Compose it WITH its fields instead; one op, and it cannot half-land.)";
    }

    /// <summary>Resolve a struct catalog name to its concrete settable type. Most modeled structs live in
    /// <c>Mutagen.Bethesda.Skyrim</c>, but some (e.g. <c>MasterReference</c>, a header sub-element) live in the core
    /// <c>Mutagen.Bethesda.Plugins</c> assembly — so fall back to a by-simple-name search across ALL Mutagen
    /// assemblies (the same cross-assembly resolution <see cref="ConcreteOf"/> uses for generic interfaces). Recognised
    /// by name, not a hand-listed set of types; fails LOUD if Mutagen models no such concrete class (Q3). The by-name
    /// fallback intentionally omits an assignability filter (unlike <see cref="ConcreteOf"/>'s interface branch): the
    /// input is a generator-emitted CATALOG name, not a runtime interface, so there is no target type to constrain
    /// against — a wrong/colliding type is caught loud downstream by Instantiate + the per-field ResolveProperty.</summary>
    static Type ResolveStructType(string name) =>
        typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim." + name)
        ?? AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen"))
            .SelectMany(SafeTypes).FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && t.Name == name)
        ?? throw new InvalidOperationException(
            $"Unknown struct type '{name}' — no concrete class in Mutagen.Bethesda.Skyrim nor any Mutagen assembly. " +
            "If Mutagen models it under another name, surface that; never guess.");

    /// <summary>True iff <see cref="BuildStruct"/> can instantiate this catalog TYPE from a PARAMETERLESS compose (no
    /// ctor_args, no composition): the name resolves via <see cref="ResolveStructType"/> to a concrete Mutagen type with
    /// a public parameterless ctor. The gate-side twin of what <see cref="Instantiate"/> does with no ctor_args — it
    /// EXCLUDES the composition-residuals <c>GenderedItem&lt;T&gt;</c> / <c>Array2d&lt;T&gt;</c> (no parameterless ctor,
    /// so Instantiate routes to <see cref="InstantiateComposition"/>, which builds only gendered halves and throws for the
    /// rest) and any name ResolveStructType can't resolve. Used by the substruct-leaf compose gate so it accepts EXACTLY
    /// what apply can build (gate==apply, by construction; no per-type list). A ctor-arg-only type is (correctly) excluded:
    /// a substruct leaf whose whole value needs positional ctor args is not a plain compose target.</summary>
    internal static bool IsPlainComposableStruct(string? typeName)
    {
        if (typeName is null) return false;
        Type t;
        try { t = ResolveStructType(typeName); }
        catch { return false; }
        return t.GetConstructor(Type.EmptyTypes) is not null;
    }

    /// <summary>Instantiate a concrete type for build-from-parts. Order: explicit positional ctor args (discriminator
    /// arm like <c>MagicEffectArchetype(TypeEnum)</c>, or composition parts) → parameterless ctor (the common case) →
    /// composition build (no parameterless ctor + no explicit args).</summary>
    static object Instantiate(Type t, string[]? ctorArgs)
    {
        if (ctorArgs is not null)
        {
            var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == ctorArgs.Length)
                ?? throw new InvalidOperationException(
                    $"{t.Name}: no constructor taking {ctorArgs.Length} arg(s). Ctors: {CtorList(t)}");
            var ps = ctor.GetParameters();
            return ctor.Invoke(ps.Select((p, i) => Coerce(ctorArgs[i], p.ParameterType)).ToArray());
        }
        var paramless = t.GetConstructor(Type.EmptyTypes);
        if (paramless is not null) return paramless.Invoke(null);
        return InstantiateComposition(t);
    }

    /// <summary>Recognition-only mirror of <see cref="Instantiate"/>'s ctor-args path — the write pre-flight gate's twin
    /// of the apply-time ctor build (G4). Does this struct type have a constructor of the supplied arity, and does each
    /// supplied arg COERCE to its parameter type? Resolves the type the SAME way <see cref="BuildStruct"/> feeds
    /// Instantiate (<see cref="ResolveStructType"/>), selects the ctor the SAME way Instantiate does
    /// (<c>GetConstructors().FirstOrDefault(len==N)</c>), and checks each arg with <see cref="TryCoerce"/> — the
    /// non-throwing twin of the very same <c>Coerce</c> Instantiate calls per arg — so the gate and apply cannot drift on
    /// arity OR value-shape. Returns null = legal; else a fail-loud message: the arity mismatch (mirroring Instantiate's
    /// own throw text, incl. <see cref="CtorList"/>) or the first arg that won't coerce, NAMED (Q3). The corpus models no
    /// ctor signatures (it is schema-driven; apply is reflection-driven), so this is the ONE gap whose recognizer is new
    /// — but it composes entirely from existing engine primitives, by construction, no generator change. Called only when
    /// <c>spec.CtorArgs</c> is non-null; an empty array means "the 0-arg ctor" and is checked like any other arity.</summary>
    internal static string? TryRecognizeCtorArgs(string structTypeName, string[] ctorArgs)
    {
        Type t;
        try { t = ResolveStructType(structTypeName); }
        catch (Exception ex) { return ex.Message; }   // unknown struct type — surface ResolveStructType's own loud message
        var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == ctorArgs.Length);
        if (ctor is null)
            return $"{t.Name}: no constructor taking {ctorArgs.Length} arg(s). Ctors: {CtorList(t)}";
        var ps = ctor.GetParameters();
        for (int i = 0; i < ps.Length; i++)
            if (!TryCoerce(ctorArgs[i], ps[i].ParameterType, out _))
                return $"ctor arg #{i} ('{ctorArgs[i]}') for '{structTypeName}' does not coerce to " +
                       $"{Pretty(ps[i].ParameterType)} (parameter '{ps[i].Name}').";
        return null;
    }

    /// <summary>A composition type has NO parameterless ctor — it is built only from its parts. Recognised by its
    /// generic definition (the engine's normal type-recognition, like IList&lt;&gt;/FormLink&lt;&gt; — NOT a hand-listed
    /// set of record types, which the cornerstone forbids):
    /// <list type="bullet">
    /// <item><c>GenderedItem&lt;T&gt;</c> — a male/female pair whose BOTH halves are mutable (corpus: Male/Female
    /// writable). Materialize each part per its kind: a FORMLINK half as a NON-NULL empty link
    /// (<see cref="EmptyFormLinkOf"/> — a null formlink half is dereferenced by the writer, HCBR-2026-07-04); a
    /// MODEL / ref half as <c>null</c> (the writer tolerates it and it materializes on demand if navigated into); a
    /// value half as <c>default</c> (0). An un-set half then matches what the binary READER produces for an absent
    /// half, so a single-gender item (e.g. a skin ArmorAddon's <c>SkinTexture</c>) serializes to a valid record.
    /// Byte-proven by write-proof Phase 4 + nullarm-guard B3.</item>
    /// <item><c>Array2d&lt;T&gt;</c> — a terrain grid (on Cell/Landscape: VertexHeightMap/VertexNormals/VertexColors,
    /// CellMaxHeightData). Its cells are reached through a 2D indexer (<c>grid[x,y]</c>), NOT named members, so they sit
    /// BELOW the reflectable-member granularity houseCARL's surface is built from: an indexer-shaped Mutagen-modeling
    /// residual (named like the PEX delta — Aaron 2026-06-01), NOT a wave deferral. Materialize-from-absent has no public
    /// ctor + needs grid dimensions: a NAMED residual (loud throw), never a wrong 0×0 shell.</item>
    /// </list></summary>
    static object InstantiateComposition(Type t)
    {
        var concrete = ConcreteOf(t) ?? t;     // the declared type is usually the getter interface (IGenderedItem<T>) — map to the concrete class
        var defName = (concrete.IsGenericType ? concrete.GetGenericTypeDefinition() : concrete).Name;
        if (defName.StartsWith("GenderedItem", StringComparison.Ordinal))
        {
            // GenderedItem<T>(T male, T female): pick the smallest positional ctor, then build each part per its kind (below).
            var ctor = concrete.GetConstructors().Where(c => c.GetParameters().Length > 0)
                .OrderBy(c => c.GetParameters().Length).First();
            // A FORMLINK half must be a NON-NULL empty link, not null: Mutagen's writer dereferences a null formlink
            // half (HCBR-2026-07-04, ArmorAddon.SkinTexture), while an empty link serializes to an absent/00000000 slot
            // — exactly what the binary READER produces for an un-set gender half, so a single-gender skin AA authored
            // fresh now WRITES instead of throwing. A Model / ref half stays null (the writer tolerates it, and it
            // materializes on demand if navigated into — WorldModel); a value half stays default(0).
            var args = ctor.GetParameters().Select(p => EmptyFormLinkOf(p.ParameterType) ?? DefaultOf(p.ParameterType)).ToArray();
            return ctor.Invoke(args);
        }
        throw new CompositionRequiredException(t.Name, t);   // Array2d<T> (indexer-shaped Mutagen residual, named like PEX) + any unknown composition — named, loud (Q3)
    }

    static object? DefaultOf(Type t) => t.IsValueType ? System.Activator.CreateInstance(t) : null;

    /// <summary>If <paramref name="t"/> is a FormLink-family type (nullable or not, mutable/getter interface or the
    /// concrete struct), return a NON-NULL EMPTY link — a Null-FormKey instance of the matching concrete struct; else
    /// null. Recognised by generic definition via <see cref="TryFormLink"/> (a null-synonym value), the same
    /// by-construction predicate the engine already uses to coerce a formlink value — never a per-record-type
    /// hand-list. Used to materialize a GenderedItem's formlink half (HCBR-2026-07-04): an un-set half must be an
    /// empty link the writer serializes as an absent/00000000 slot, NOT a null the parallel writer dereferences into
    /// an AggregateException-wrapped NRE.</summary>
    static object? EmptyFormLinkOf(Type t) => TryFormLink("0", t, out var link) ? link : null;

    /// <summary>True iff <paramref name="t"/> is a REQUIRED (non-nullable) FormLink-family type — the mutable
    /// <c>IFormLink&lt;T&gt;</c>, the getter <c>IFormLinkGetter&lt;T&gt;</c>, or the concrete <c>FormLink&lt;T&gt;</c>,
    /// but NOT the <c>IFormLinkNullable&lt;T&gt;</c> / <c>FormLinkNullable&lt;T&gt;</c> variant. Recognised by generic
    /// definition — the SAME required-arm branch <see cref="TryFormLink"/> keys off, so the two can't drift — never a
    /// per-record-type hand-list. Used by the Remove case to fail LOUD on a required-link clear rather than let
    /// <see cref="EmptyFormLinkOf"/> silently blank it (Q3; PR #150 review).</summary>
    static bool IsRequiredFormLink(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(FormLink<>) || def == typeof(IFormLink<>) || def == typeof(IFormLinkGetter<>);
    }

    /// <summary>Map a (possibly getter/interface) type to the concrete settable class the engine can instantiate: a
    /// generic interface <c>IFoo&lt;T&gt;</c> → concrete <c>Foo&lt;T&gt;</c> (GenderedItem/Array2d live in
    /// Mutagen.Bethesda.Plugins, across assemblies); a simple interface <c>IFoo</c> → <c>Foo</c>; an already-concrete
    /// class passes through. Returns null when no concrete class resolves. Shared by composition materialization and
    /// the substruct-probe diagnostic so they can never disagree about interface→concrete.</summary>
    internal static Type? ConcreteOf(Type t)
    {
        if (t is { IsInterface: true, IsGenericType: true })
        {
            var openDef = t.GetGenericTypeDefinition();
            var implDef = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")).SelectMany(SafeTypes)
                .FirstOrDefault(x => x is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: true }
                    && x.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openDef));
            if (implDef is not null) { try { return implDef.MakeGenericType(t.GetGenericArguments()); } catch { } }
        }
        if (t is { IsInterface: true })
        {
            var simple = RecordNaming.StripInterfaceToConcrete(t.Name);   // INpcGetter->Npc, and the non-Getter IFoo->Foo case
            var asm = typeof(SkyrimMod).Assembly;
            var impl = asm.GetType("Mutagen.Bethesda.Skyrim." + simple)
                       ?? SafeTypes(asm).FirstOrDefault(x => x.IsClass && !x.IsAbstract && x.Name == simple && t.IsAssignableFrom(x));
            if (impl is { IsClass: true, IsAbstract: false }) return impl;
        }
        if (t is { IsClass: true, IsAbstract: false }) return t;
        return null;
    }

    /// <summary>True iff a collection's ELEMENT type is WHOLE-COERCIBLE — set as one value (a path string), not built
    /// from parts: an <c>AssetLink&lt;T&gt;</c> texture/model/sound path, etc. Tries the coercion recogniser on the
    /// resolved element type (mapping a getter interface to its concrete), AND recognises the AssetLink family by its
    /// catalog name as a robust fallback — the cross-assembly nested-generic getter AQ (IAssetLinkGetter&lt;T&gt;) does
    /// not resolve via <c>Type.GetType</c>, so the name check is what actually fires for AssetLink elements. Used by
    /// BOTH the rulebook and the proof to keep "is this a build-from-parts struct element" decisions identical.</summary>
    internal static bool IsWholeCoercibleElement(string? elementRef, string? elementAq)
    {
        if (elementAq is not null && ResolveType(elementAq) is { } rt && CanCoerce(ConcreteOf(rt) ?? rt)) return true;
        return elementRef is not null && elementRef.StartsWith("AssetLink", StringComparison.Ordinal);
    }

    /// <summary>True iff <paramref name="t"/> is a Mutagen <c>AssetLink&lt;T&gt;</c> family type — the mutable
    /// concrete <c>AssetLink&lt;T&gt;</c>, its getter overlay <c>AssetLinkGetter&lt;T&gt;</c>, or the
    /// <c>IAssetLink(Getter)&lt;T&gt;</c> interfaces a collection element exposes. Recognised by generic-definition
    /// NAME (the same shape the FLOI family uses, and the cross-assembly nested-generic getter interface does not
    /// always resolve by AQ), so the recogniser is robust to which interface arm a list/dict element happens to
    /// surface. SHARED by the read path (<see cref="ReadEngine"/> emits the stored path) and the write coercion
    /// (<see cref="TryValueType"/> builds a new AssetLink from a path) so the two can't drift on what an asset link
    /// is — the same one-recogniser discipline the FormLink family uses.</summary>
    internal static bool IsAssetLinkFamily(Type t)
    {
        if (!t.IsGenericType) return false;
        var n = t.GetGenericTypeDefinition().Name;
        return n.StartsWith("AssetLink", StringComparison.Ordinal) || n.StartsWith("IAssetLink", StringComparison.Ordinal);
    }

    static void ApplyDictVerb(object parent, PropertyInfo prop, Type dictIface, WriteRequest req)
    {
        // Writable-by-construction: an ABSENT optional dict (null) is materialized so a first entry can be set.
        // Remove on an absent dict SURFACES "nothing to remove" (Gap 3 — a Remove that removes nothing is no longer a
        // silent no-op; the key is trivially not present when the whole dict is absent) — thrown as the EXPECTED kind
        // BEFORE materializing, so it still must NOT create an empty dict.
        var dict = prop.GetValue(parent);
        if (dict is null)
        {
            if (req.Verb == "Remove")
                throw new ExpectedApplyRejectionException(
                    $"Remove on dict '{prop.Name}': the collection is absent (no entries) — nothing to remove.");
            dict = MaterializeCollection(parent, prop);
        }
        var kType = dictIface.GetGenericArguments()[0];
        var vType = dictIface.GetGenericArguments()[1];
        var dt = dict.GetType();
        var setItem = Indexer(dt).GetSetMethod()!;
        void Set(string k, string v) => setItem.Invoke(dict, new[] { Coerce(k, kType), Coerce(v, vType) });
        // The entry VALUE for Set/Add: a struct/arm-VALUED dict (Package.Data — the only one Mutagen models) builds the
        // value FROM PARTS (Gap 3: dict-element composition), mirroring ApplyListVerb's Add; a coercible-VALUE dict
        // (Class.SkillWeights, Race.Regen, …) coerces the plain value as before. The gate (CorpusRulebook) accepts a dict
        // Set/Add carrying a StructSpec ONLY for a composable element, so a spec on a coercible dict can't reach here.
        object? BuildValue() => req.Struct is not null ? BuildStruct(req.Struct) : Coerce(req.Value!, vType);

        switch (req.Verb)
        {
            case "Set": // REPLACES (indexer-set) — for a composable dict this overwrites an entry's composed value (Gap 3)
                setItem.Invoke(dict, new[] { Coerce(req.Key!, kType), BuildValue() });
                break;
            case "Add": // distinct from Set: a duplicate key is refused (Mutagen's dict.Add throws). Pre-check ContainsKey so
            {           // the guidance names the fix (Gap 3) — "use Set to overwrite" — instead of the raw Mutagen string.
                var addKey = Coerce(req.Key!, kType);
                var contains = dt.GetMethod("ContainsKey", new[] { kType });
                if (contains is not null && contains.Invoke(dict, new[] { addKey }) is true)
                    // EXPECTED apply rejection (live occupancy — pre-flight is schema-only, can't see it): thrown as the
                    // distinct kind so WritePatchBuilder renders this guidance cleanly, NOT under the "real inconsistency"
                    // wrapper reserved for genuine gate/apply drift (gap-audit Finding 3).
                    throw new ExpectedApplyRejectionException(
                        $"Key '{req.Key}' already present in '{prop.Name}' — use Set to overwrite that entry, or choose a free key/index.");
                dt.GetMethod("Add", new[] { kType, vType })!.Invoke(dict, new[] { addKey, BuildValue() });
                break;
            }
            case "Remove":
                // SURFACE a no-op Remove (Gap 3): the runtime Remove returns false when the key is not present — refuse it
                // as the EXPECTED kind (the symmetric twin of Add's duplicate-key refusal), clean, not a silent success.
                if (dt.GetMethod("Remove", new[] { kType })!.Invoke(dict, new[] { Coerce(req.Key!, kType) }) is false)
                    throw new ExpectedApplyRejectionException(
                        $"Key '{req.Key}' is not present in '{prop.Name}' — nothing to remove.");
                break;
            case "ReplaceAll":
                dt.GetMethod("Clear")!.Invoke(dict, null);
                foreach (var kv in req.Entries ?? new()) Set(kv.Key, kv.Value);
                break;
            case "Merge":
                foreach (var kv in req.Entries ?? new()) Set(kv.Key, kv.Value);
                break;
            default:
                throw new InvalidOperationException($"Verb '{req.Verb}' is not valid on dict '{prop.Name}'.");
        }
    }

    static void ApplyListVerb(object parent, PropertyInfo prop, Type listIface, WriteRequest req)
    {
        // ARRAY-backed collection (a C# T[], e.g. Weather.CloudTextures / Weather.Clouds — fixed-size game
        // structures Mutagen models as a plain array, not a growable ExtendedList). The list verbs assume
        // ExtendedList semantics (Clear / Add / RemoveAt); an array has none, so without this guard Add / ReplaceAll /
        // Remove NRE at apply on the missing method, and even materialize of an absent array throws (arrays need a
        // length) — an UNNAMED accept-then-throw (the schema-only gate accepts the verb). Refuse LOUD and NAMED (Q3):
        // array-collection mutation is a distinct write mechanism not yet built (some, like Weather clouds, are a
        // fixed 29-layer format, so an arbitrary-length write may not even serialize validly — it needs its own
        // investigation). SetAtIndex is refused too: the array may be absent (can't index a null) and a set-only
        // surface would surprise. InsertAtIndex is refused for the plainer reason that a fixed-size array cannot grow
        // at all — T[]'s IList<T>.Insert throws NotSupportedException — so it is refused HERE, by name, rather than
        // reaching the arm. Element COERCION is unaffected — this is the collection-shape gap, not the value
        // gap (an asset-link element coerces fine; see IsAssetLinkFamily). The gate could pre-check IsArray later
        // (array-ness is schema-visible) to move this to pre-flight — tracked.
        if (prop.PropertyType.IsArray)
            // The refused SET is derived, not listed. A hand-typed enumeration of the collection verbs here is the
            // same rot in its other direction: it went stale on Add, then again on InsertAtIndex, and a verb missing
            // from it would read as supported. The cardinality is settled by the control flow that reached this
            // method (ApplyVerb matched IList<T>), so the shape needs no fallback arm.
            throw new ExpectedApplyRejectionException(
                $"'{prop.Name}' is an array-backed collection ({Pretty(prop.PropertyType)}); "
                + WriteVerbs.CollectionVerbNames(WriteVerbs.OfElement(CollectionKind.List, listIface.GetGenericArguments()[0]))
                + " are not yet supported on arrays (some, like Weather clouds, are fixed-size game structures). "
                + "Tracked gap — array-collection mutation is a distinct write mechanism not yet built.");

        // Writable-by-construction: an ABSENT optional list (null) is materialized so a first element can be
        // added — "add a keyword to a record that has none" must work, not throw. Remove on an absent list SURFACES
        // "nothing to remove" (Gap 3 — no silent no-op; the value/index is trivially not present when the whole list is
        // absent) — thrown as the EXPECTED kind BEFORE materializing, so it still must NOT create an empty list.
        var list = prop.GetValue(parent);
        if (list is null)
        {
            if (req.Verb == "Remove")
                throw new ExpectedApplyRejectionException(
                    $"Remove on list '{prop.Name}': the collection is absent (no elements) — nothing to remove.");
            list = MaterializeCollection(parent, prop);
        }
        var elem = listIface.GetGenericArguments()[0];
        var lt = list.GetType();
        switch (req.Verb)
        {
            case "Add":
                // struct-element list (modeled-struct elements) → build the new element FROM PARTS (wave-1 half B);
                // coercible-element list → coerce the plain value as before. ResolveProperty/AddMethod handle the rest.
                // P8a composes= appends MANY built elements in ONE op (each pre-flighted by ComposesLegality).
                if (req.Structs is { } addSpecs)
                {
                    var addM = AddMethod(lt, elem);
                    foreach (var s in addSpecs) addM.Invoke(list, new[] { BuildStruct(s) });
                    break;
                }
                AddMethod(lt, elem).Invoke(list,
                    new[] { req.Struct is not null ? BuildStruct(req.Struct) : Coerce(req.Value!, elem) });
                break;
            case "SetAtIndex":
            {
                // EXPECTED apply rejection (live length): pre-flight gates the index SHAPE (parseable non-negative int)
                // but leaves the in-range bound to apply — it has no live collection to know the length (KEYSHAPE / gap-
                // audit Finding 3). Pre-check it so an out-of-range index surfaces as clean guidance, not a reflection-
                // wrapped ArgumentOutOfRangeException under the "real inconsistency" wrapper.
                int idx = int.Parse(req.Key!, CultureInfo.InvariantCulture);
                int count = CollectionCount(list);
                if (idx < 0 || idx >= count)
                    throw new ExpectedApplyRejectionException(IndexRangeMessage(prop.Name, idx, count, IndexOpKind.Overwrite));
                // Build the replacement the SAME way Add does — a composable (struct/arm) element FROM PARTS
                // (req.Struct → BuildStruct), a coercible element by coercing req.Value — then OVERWRITE in place,
                // preserving the element's list position. Closes HCBR-2026-07-10: replacing one condition row no longer
                // needs Remove+Add, which moved the row to the list END (harmless for an AND row, but breaking an
                // OR-group). The gate (CorpusRulebook's composable block) admits a SetAtIndex compose ONLY for a
                // Struct/Arm element, through the SAME StructElementLegality Add passes, so req.Struct is pre-validated
                // here; a coercible element still coerces req.Value exactly as before (req.Struct null → the Coerce arm).
                Indexer(lt).GetSetMethod()!.Invoke(list,
                    new[] { (object)idx, req.Struct is not null ? BuildStruct(req.Struct) : Coerce(req.Value!, elem) });
                break;
            }
            case "InsertAtIndex":
            {
                // The sibling neither Add nor SetAtIndex is (#302). Add appends — it moves nothing, but only ever
                // lands at the END; SetAtIndex holds a position — but only over an element that is already there.
                // Insert puts a NEW element AT a position and shifts the rest right, which is the only way to grow a
                // POSITION-CONTIGUOUS run in place: a CTDA OR-group chains each row to the row after it, so a new arm
                // must sit ADJACENT to the group, and an appended one AND-groups instead (it can only restrict the
                // gate, never relax it). Before this the only in-vocabulary answers were ReplaceAll (re-transcribe
                // every row) or SetAtIndex (drop an arm to make room) — the workaround #302 was filed on.
                int idx = int.Parse(req.Key!, CultureInfo.InvariantCulture);
                int count = CollectionCount(list);
                // APPEND-INCLUSIVE bound — `> count`, not `>= count`. Inserting AT count means "after the last
                // element", which is legal and is exactly what Add does; refusing it would make the one index a
                // caller computes from the live length (insert after the group's last row) the one index that fails.
                if (idx < 0 || idx > count)
                    throw new ExpectedApplyRejectionException(IndexRangeMessage(prop.Name, idx, count, IndexOpKind.Insert));
                // Built EXACTLY as Add and SetAtIndex build it — a composable (struct/arm) element FROM PARTS
                // (req.Struct → BuildStruct), a coercible element by coercing req.Value. Same construction, same
                // pre-flight (CorpusRulebook admits an InsertAtIndex compose through the SAME StructElementLegality),
                // so a composed condition row INSERTS as the identical element it would APPEND as — the only
                // difference between the two verbs is where it lands and what moves.
                InsertMethod(listIface, elem).Invoke(list,
                    new[] { (object)idx, req.Struct is not null ? BuildStruct(req.Struct) : Coerce(req.Value!, elem) });
                break;
            }
            case "Remove":
                if (req.Key is not null)
                {
                    // Same EXPECTED out-of-range rejection for Remove-by-index (RemoveAt) — clean, not the wrapper.
                    int idx = int.Parse(req.Key, CultureInfo.InvariantCulture);
                    int count = CollectionCount(list);
                    if (idx < 0 || idx >= count)
                        throw new ExpectedApplyRejectionException(IndexRangeMessage(prop.Name, idx, count, IndexOpKind.RemoveAt));
                    lt.GetMethod("RemoveAt", new[] { typeof(int) })!.Invoke(list, new object[] { idx });
                }
                else
                {
                    // SURFACE a no-op Remove-by-value (Gap 3): List<T>.Remove returns false when the value is not present —
                    // refuse it as the EXPECTED kind (Remove-by-INDEX is range-checked above), clean, not a silent success.
                    if (lt.GetMethod("Remove", new[] { elem })!.Invoke(list, new[] { Coerce(req.Value!, elem) }) is false)
                        throw new ExpectedApplyRejectionException(
                            $"Value '{req.Value}' is not present in '{prop.Name}' — nothing to remove.");
                }
                break;
            case "ReplaceAll":
                lt.GetMethod("Clear")!.Invoke(list, null);
                var add = AddMethod(lt, elem);
                // P8a composes= ReplaceAll = clear then append each BUILT element (the modeled-list replace the singular
                // compose block still defers); a coercible-element list still ReplaceAlls plain req.Values as before.
                if (req.Structs is { } replSpecs)
                    foreach (var s in replSpecs) add.Invoke(list, new[] { BuildStruct(s) });
                else
                    foreach (var v in req.Values ?? Array.Empty<string>()) add.Invoke(list, new[] { Coerce(v, elem) });
                break;
            default:
                throw new InvalidOperationException($"Verb '{req.Verb}' is not valid on list '{prop.Name}'.");
        }
    }

    /// <summary>Element count of a (possibly Mutagen <c>ExtendedList&lt;T&gt;</c>) collection WITHOUT assuming the
    /// non-generic <c>ICollection</c> — enumerate, mirroring <see cref="StepIntoElement"/>'s index walk. Used to
    /// pre-check a list index against live length so an out-of-range SetAtIndex/Remove-by-index surfaces as an
    /// <see cref="ExpectedApplyRejectionException"/> (clean, live-state user error) rather than a reflection-wrapped
    /// <c>ArgumentOutOfRangeException</c> under the gate/apply-inconsistency wrapper.</summary>
    static int CollectionCount(object coll)
    {
        int n = 0;
        foreach (var _ in (System.Collections.IEnumerable)coll) n++;
        return n;
    }

    /// <summary>Which list index op is being refused. The three do NOT share a bound: <c>SetAtIndex</c> and
    /// <c>Remove</c>-by-index address an element that must already exist (<c>0..count-1</c>), while
    /// <c>InsertAtIndex</c> addresses a GAP between elements, of which there is one more than there are elements
    /// (<c>0..count</c> — inserting at <c>count</c> is a legal append). This was a <c>bool append</c> that meant "may
    /// the message offer Add?"; a third mode whose BOUND differs cannot be spelled as a second reading of that
    /// bool.</summary>
    enum IndexOpKind { Overwrite, RemoveAt, Insert }

    /// <summary>The clean out-of-range message for a list index op — the bound it states is the bound
    /// <see cref="ApplyListVerb"/> actually enforces for that verb, and the empty-list case names the right next step
    /// for each.</summary>
    static string IndexRangeMessage(string field, int idx, int count, IndexOpKind kind)
    {
        // Insert's own sentence, because its legal range INCLUDES count and the shared one below cannot say that
        // without lying to the other two. Naming what index `count` means matters more here than elsewhere: a caller
        // extending a mid-list run computes its index from the live length, so the append slot is the likely target.
        if (kind == IndexOpKind.Insert)
            return count == 0
                ? $"Index {idx} out of range for '{field}' — the list is empty; insert at index 0 (or Add, which is the same thing here)."
                : $"Index {idx} out of range for '{field}' (it has {count} element(s)) — InsertAtIndex takes an index "
                  + $"in 0..{count}, where {count} inserts AFTER the last element (the same result as Add).";
        bool append = kind == IndexOpKind.Overwrite;
        return count == 0
            ? $"Index {idx} out of range for '{field}' — the list is empty"
              + (append ? "; Add an element first." : "; nothing to remove.")
            : $"Index {idx} out of range for '{field}' (it has {count} element(s)) — use an index in 0..{count - 1}"
              + (append ? ", or Add to append a new element." : ".");
    }

    /// <summary>Materialize an absent (null) optional collection so a first element/entry can be added. Instantiates
    /// the property's declared concrete collection type (Mutagen's settable ExtendedList&lt;T&gt; / dictionary) and
    /// assigns it. Fails LOUD if the property is not settable or the type has no usable constructor — never a silent
    /// skip. (Byte-identity of init-then-add vs a natively-populated record is proven by write-proof Phase 3.)</summary>
    static object MaterializeCollection(object parent, PropertyInfo prop)
    {
        if (!prop.CanWrite)
            throw new InvalidOperationException($"Collection '{prop.Name}' is absent and not settable — cannot materialize.");
        var made = System.Activator.CreateInstance(prop.PropertyType)
            ?? throw new InvalidOperationException($"Could not instantiate collection type {prop.PropertyType.Name} for '{prop.Name}'.");
        prop.SetValue(parent, made);
        return made;
    }

    /// <summary>Materialize an absent (null) intermediate optional substruct so a field inside it can be set —
    /// paralleling <see cref="MaterializeCollection"/>. Delegates to <see cref="Instantiate"/>: a parameterless ctor
    /// for the common case, OR composition build-from-parts (wave-1 half B) for a no-parameterless-ctor type —
    /// <c>GenderedItem&lt;T&gt;</c> materializes with default(T) parts (both halves mutable, so navigation then
    /// populates them). A still-unbuildable composition (<c>Array2d&lt;T&gt;</c> terrain grids — an indexer-shaped Mutagen residual, named like PEX; or any unknown) FAILS
    /// LOUD (<see cref="CompositionRequiredException"/>, re-stamped with the path segment): a real write into it
    /// surfaces as an explicit, named deferral, never a silent wrong result (Q3). Composition is recognised BY TYPE,
    /// derived from Mutagen's model — never a hand-listed set of record types.</summary>
    static object MaterializeSubstruct(object parent, PropertyInfo prop, string segment)
    {
        if (!prop.CanWrite)
            throw new InvalidOperationException($"Absent substruct '{segment}' ({Pretty(prop.PropertyType)}) is not settable — cannot materialize.");
        // An absent OWNED CHILD RECORD (Cell.Landscape, Worldspace.TopCell) reaches here only since #335 corrected
        // their classification — before it, the gate refused the descent as a formlink. A record has no parameterless
        // ctor because it is identified by a FormKey, NOT because it is one of Mutagen's composition types, so falling
        // through would name the wrong gap ("buildable only from its parts") and point at a wave that will never
        // deliver it. Say what is actually true instead: houseCARL does not invent records as sub-objects.
        if (typeof(IMajorRecordGetter).IsAssignableFrom(prop.PropertyType))
            throw new ExpectedApplyRejectionException(
                $"'{segment}' holds an owned child RECORD ({Pretty(prop.PropertyType)}) and the record being written " +
                "carries none, so there is nothing to write into. houseCARL will not synthesize a record as a " +
                "sub-object — a record exists only with its own FormKey, and giving a parent a child it lacks is an " +
                "open gap (#350). Address the child record by its own FormID instead. If the version you are patching " +
                "does carry one, note that a patch's fresh override of a parent does not bring the parent's child " +
                "records with it — the record axis reaches it, this path does not.");
        object made;
        try { made = Instantiate(prop.PropertyType, null); }
        catch (CompositionRequiredException) { throw new CompositionRequiredException(segment, prop.PropertyType); }  // re-stamp with the path segment
        prop.SetValue(parent, made);
        return made;
    }

    /// <summary>
    /// Collection-nav (wave 1): step INTO a list/dict element mid-path so a sub-field can be edited
    /// (e.g. <c>Effects[0].Data.Magnitude</c>). List → index by int (Mutagen's ExtendedList&lt;T&gt; is
    /// IList&lt;T&gt; but not the non-generic IList — enumerate to the index); dict → coerce the key to its key
    /// type and look it up. The element is a navigable STRUCT (record-elements are the nested-group wave). Fails
    /// LOUD (Q3) on an absent collection (add an element first — composition, half B), a bad/out-of-bounds index,
    /// or a missing key — never a silent wrong target.
    /// </summary>
    internal static object StepIntoElement(object parent, PropertyInfo prop, string name, string key, bool materialize = false)
    {
        // Gendered field ([0]=male / [1]=female): a fixed two-slot pair (IGenderedItem<T>), NOT a list/dict, so it
        // never reaches the IList/IDictionary branches below. Its named arms (.Male/.Female) already navigate as
        // plain hops; [0]/[1] is the render-matching navigable alias (HCBR PR-H). Handled by the same materialize-
        // and-write-back primitive the named plain hop uses, so a freshly-built arm can't become a silently-dropped
        // orphan (Q3). Recognised by the runtime IGenderedItem<> — the engine twin of the corpus "GenderedItem<"
        // recogniser CorpusRulebook pre-flight keys off (two recognisers that must agree, never one shared classifier).
        if (GenderedInterface(prop.PropertyType) is not null)
            return StepIntoGenderedArm(parent, prop, name, key, materialize);

        var coll = prop.GetValue(parent)
            ?? throw new ExpectedApplyRejectionException(   // live-state: empty/absent collection — clean, not the inconsistency wrapper
                $"Cannot navigate into '{name}[{key}]': the collection is absent (null). Add an element first " +
                "(element composition — wave 1 half B), then navigate into it.");

        // Recognise BOTH the mutable and read-only collection interfaces: the write path navigates the concrete
        // mutable list/dict, but a read (show / before-display) navigates a getter overlay exposing IReadOnly*.
        var dictIface = ClosedInterface(prop.PropertyType, typeof(IDictionary<,>))
                     ?? ClosedInterface(prop.PropertyType, typeof(IReadOnlyDictionary<,>));
        if (dictIface is not null)
        {
            var kType = dictIface.GetGenericArguments()[0];
            var keyObj = Coerce(key, kType);
            var dt = coll.GetType();
            var contains = dt.GetMethod("ContainsKey", new[] { kType });
            if (contains is not null && contains.Invoke(coll, new[] { keyObj }) is false)
                throw new ExpectedApplyRejectionException($"No entry with key '{key}' in dict '{name}'.");  // live-state: absent key
            var idxer = dt.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.GetIndexParameters().Length == 1 && p.CanRead)
                ?? throw new InvalidOperationException($"No readable indexer on dict '{name}' ({dt.Name}).");
            return idxer.GetValue(coll, new object[] { keyObj! })
                ?? throw new MalformedTargetDataException(   // present-but-null entry: a SOURCE-data anomaly (its own third category), not gate/apply drift
                    $"Entry '{name}[{key}]' is present but null — the target record's data is malformed here (a source-data anomaly, not an engine fault).");
        }

        var listIface = ClosedInterface(prop.PropertyType, typeof(IList<>))
                     ?? ClosedInterface(prop.PropertyType, typeof(IReadOnlyList<>));
        if (listIface is not null)
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx < 0)
                throw new InvalidOperationException($"List '{name}' must be indexed by a non-negative integer; got '{key}'.");
            int j = 0;
            foreach (var item in (System.Collections.IEnumerable)coll)
                if (j++ == idx)
                    return item ?? throw new MalformedTargetDataException(   // present-but-null element: a SOURCE-data anomaly (its own third category), not gate/apply drift
                        $"Element '{name}[{idx}]' is present but null — the target record's data is malformed here (a source-data anomaly, not an engine fault).");
            throw new ExpectedApplyRejectionException($"Index {idx} out of bounds for list '{name}' (has {j} element(s)).");  // live-state: out of range
        }

        throw new InvalidOperationException($"'{name}' is not a navigable collection (no [read-only] IList/IDictionary).");
    }

    /// <summary>The gendered-arm twin of <see cref="StepIntoElement"/>'s list/dict branches: map a bracketed index
    /// (<c>[0]</c>=male, <c>[1]</c>=female — see <see cref="GenderedArmNames"/>) to the pair's named arm and return it.
    /// On a WRITE (<paramref name="materialize"/>=true) an absent pair OR an absent ref arm is materialized AND written
    /// back through the SAME <see cref="MaterializeSubstruct"/> setter the named plain hop uses — so the bracket alias
    /// and the named path produce identical results, and a freshly-built arm is never an orphan a later sub-field write
    /// silently lands on and loses (the Q3 trap). On a READ (<paramref name="materialize"/>=false) an absent pair/arm
    /// fails LOUD — a read never mutates a record, exactly as stepping into an absent list element does.</summary>
    static object StepIntoGenderedArm(object parent, PropertyInfo prop, string name, string key, bool materialize)
    {
        int idx = key switch { "0" => 0, "1" => 1, _ => -1 };
        if (idx < 0)
            throw new InvalidOperationException(
                $"Gendered field '{name}' is indexed by [0] (male) or [1] (female); got '{key}'. " +
                $"(Its halves are also reachable by name: '{name}.Male' / '{name}.Female'.)");

        var gendered = prop.GetValue(parent);
        if (gendered is null)
        {
            if (!materialize)
                throw new InvalidOperationException($"Cannot navigate into '{name}[{key}]': the gendered field is absent (null).");
            gendered = MaterializeSubstruct(parent, prop, name);   // build the pair (default parts) + write back — named-path parity
        }

        var armName = GenderedArmNames[idx];
        var armProp = ResolveProperty(gendered.GetType(), armName)
            ?? throw new InvalidOperationException($"Gendered type {gendered.GetType().Name} has no '{armName}' arm.");
        var arm = armProp.GetValue(gendered);
        if (arm is null)
        {
            if (!materialize)
                throw new InvalidOperationException($"Gendered arm '{name}[{key}]' ({armName}) is absent (null).");
            arm = MaterializeSubstruct(gendered, armProp, armName);   // materialize the ref arm + WRITE BACK via the setter (the Q3 orphan trap)
        }
        return arm;
    }

    /// <summary>The canonical gendered index→arm mapping, the ONE place navigation (<see cref="StepIntoGenderedArm"/>)
    /// and the depth-render (<c>ReadEngine.Expand</c>) both read — so the <c>[0]/[1]</c> a read SHOWS is exactly the
    /// <c>[0]/[1]</c> a write/read ACCEPTS, with no enumeration-order drift between the two surfaces.</summary>
    internal static readonly string[] GenderedArmNames = { "Male", "Female" };

    /// <summary>The closed gendered interface a type carries (<c>IGenderedItem&lt;T&gt;</c> / <c>IGenderedItemGetter&lt;T&gt;</c>,
    /// or the concrete <c>GenderedItem&lt;T&gt;</c>), else null. Recognised by the generic definition's NAME — the same
    /// by-the-generic-definition recognition the engine uses for IList&lt;&gt;/FormLink&lt;&gt;, never a hand-listed set
    /// of arm-bearing record types (the cornerstone forbids that). The corpus-side twin is the <c>"GenderedItem&lt;"</c>
    /// TypeRef check in <c>CorpusRulebook</c>; the two recognisers must agree.</summary>
    internal static Type? GenderedInterface(Type t)
    {
        static bool IsGen(Type x)
        {
            if (!x.IsGenericType) return false;
            var n = x.GetGenericTypeDefinition().Name;   // e.g. "GenderedItem`1" / "IGenderedItem`1" / "IGenderedItemGetter`1"
            var tick = n.IndexOf('`');
            if (tick > 0) n = n[..tick];
            // ANCHORED to the exact gendered family (not a StartsWith) so the runtime recogniser can't drift broader
            // than the corpus-side "GenderedItem<" TypeRef check — a hypothetical "GenderedItemList<T>" must NOT match.
            return n is "GenderedItem" or "IGenderedItem" or "IGenderedItemGetter";
        }
        if (IsGen(t)) return t;
        return t.GetInterfaces().FirstOrDefault(IsGen);
    }

    static PropertyInfo Indexer(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 1 && p.CanWrite)
        ?? throw new InvalidOperationException($"No writable single-arg indexer on {t.Name}");

    /// <summary>The <c>IList&lt;T&gt;.Insert(int, T)</c> the <c>InsertAtIndex</c> arm drives. Taken off the CLOSED
    /// INTERFACE rather than the concrete list type (unlike <see cref="AddMethod"/>, which needs the concrete type's
    /// own overload set): a list reaches <see cref="ApplyListVerb"/> BECAUSE it implements <c>IList&lt;T&gt;</c>
    /// (ApplyVerb's dispatch resolves that interface to get here), so the method is present by construction even where
    /// a concrete type implements it explicitly, and a reflection invoke dispatches to the implementation. The
    /// one IList&lt;T&gt; whose Insert throws — a <c>T[]</c> — never reaches here: the array-backed guard at the top of
    /// ApplyListVerb refuses every list verb on it by name first.</summary>
    static MethodInfo InsertMethod(Type listIface, Type elem) =>
        listIface.GetMethod("Insert", new[] { typeof(int), elem })!;

    static MethodInfo AddMethod(Type listType, Type elem) =>
        listType.GetMethod("Add", new[] { elem })
        ?? listType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
            m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(elem))
        ?? throw new InvalidOperationException($"No compatible Add on {listType.Name} for element {elem.Name}");

    internal static Type? ClosedInterface(Type type, Type openGeneric)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric) return type;
        return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric);
    }

    // ======================================================================
    //  COERCION  (string -> typed value)
    //
    //  Recognition is SHARED between Coerce (convert) and CanCoerce (recognise-only,
    //  used by pre-flight + the coerce-audit guard) through the Try* family methods:
    //  each returns true iff `u` is in its family, and — when `text` is non-null — also
    //  emits the coerced value. Passing text=null makes them pure recognisers, so the
    //  two surfaces CANNOT drift on which types are coercible.
    //
    //  TryValueType is the corpus-derived value-type surface (Color, Percent, P3*, …):
    //  `coerce-audit` enumerates every writable scalar/enum/value/formlink leaf (+ list/
    //  dict elements) in corpus.json and asserts each resolves to a coercible type — so
    //  coverage is complete BY CONSTRUCTION, not by a hand-kept list.
    // ======================================================================

    /// <summary>Turn a string into a value of <paramref name="targetType"/>, or throw fail-loud.</summary>
    static object? Coerce(string text, Type targetType)
    {
        var u = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (TryPrimitive(text, u, out var r) || TryEnum(text, u, out r)
            || TryFormLink(text, u, out r) || TryValueType(text, u, out r))
            return r;
        throw new InvalidOperationException(
            $"No coercion rule for {targetType.FullName} (value={text}). If Mutagen models this as a writable " +
            "value type, it is a real coercion gap to add (extend TryValueType) — surface it via coerce-audit, never guess.");
    }

    /// <summary>Recognition-only mirror of <see cref="Coerce"/>: does a coercion rule exist for this type?
    /// Shares the Try* recognisers, so it can never disagree with Coerce about what is coercible.</summary>
    internal static bool CanCoerce(Type targetType)
    {
        var u = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return TryPrimitive(null, u, out _) || TryEnum(null, u, out _)
            || TryFormLink(null, u, out _) || TryValueType(null, u, out _);
    }

    // -- coercion families. text==null => recognise only (result stays null). --

    static bool TryPrimitive(string? text, Type u, out object? result)
    {
        result = null;
        if (u == typeof(string)) { if (text != null) result = text; return true; }
        if (u == typeof(bool)) { if (text != null) result = bool.Parse(text); return true; }
        if (u == typeof(int)) { if (text != null) result = int.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(uint)) { if (text != null) result = ParseUInt(text); return true; }
        if (u == typeof(short)) { if (text != null) result = short.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(ushort)) { if (text != null) result = ushort.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(long)) { if (text != null) result = long.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(ulong)) { if (text != null) result = ulong.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(float)) { if (text != null) result = float.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(double)) { if (text != null) result = double.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(byte)) { if (text != null) result = byte.Parse(text, CultureInfo.InvariantCulture); return true; }
        if (u == typeof(sbyte)) { if (text != null) result = sbyte.Parse(text, CultureInfo.InvariantCulture); return true; }
        return false;
    }

    static uint ParseUInt(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text[2..], NumberStyles.HexNumber)
            : uint.Parse(text, CultureInfo.InvariantCulture);

    static bool TryEnum(string? text, Type u, out object? result)
    {
        result = null;
        if (!u.IsEnum) return false;
        if (text != null) result = Enum.Parse(u, text, ignoreCase: true);
        return true;
    }

    // ---- FormLink null-clear (HCBR-2026-06-15-01 / PR-F) -------------------------------------------------------
    //  A Set that CLEARS a FormLink (points it at no target) is expressed by a null-synonym value. The canonical
    //  set is fixed (Aaron 2026-06-16): "0", "00000000", "Null", "000000:Null" — trimmed, case-insensitive,
    //  FULL-STRING, so a real FormID ("012345:Skyrim.esm") is never mistaken for a clear. A synonym routes to
    //  FormKey.Null; anything else parses through FormKey.Factory (which throws fail-loud on a malformed id — that
    //  throw is caught at the gate by the pre-flight value-shape check, never reached as an accept-then-throw). This
    //  ONE recognizer is shared by the apply path (ToFormKey, via TryFormLink) and pre-flight (CorpusRulebook ->
    //  IsValidFormLinkValue) so the two can't drift on what counts as a clear — the same shared-predicate shape the
    //  engine already uses for IsFormLinkOrIndex. (Without it, "00000000"/"0" was ACCEPTED by pre-flight then threw
    //  "Malformed FormKey string" at apply — a Q3 accept-then-throw hole; and a required link had no clear path.)
    static readonly string[] FormKeyNullSynonyms = { "0", "00000000", "Null", "000000:Null" };

    /// <summary>True iff <paramref name="text"/> is a canonical FormKey null-clear synonym (trimmed, case-insensitive,
    /// full-string). Shared by apply (<see cref="ToFormKey"/>) and pre-flight (<see cref="IsValidFormLinkValue"/>).</summary>
    internal static bool IsFormKeyNullSynonym(string? text)
    {
        var v = (text ?? "").Trim();
        foreach (var s in FormKeyNullSynonyms)
            if (v.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A null-synonym clears to <see cref="FormKey.Null"/>; anything else parses via FormKey.Factory
    /// (fail-loud on a malformed id — pre-flight's value-shape check rejects that string before apply is reached).</summary>
    static FormKey ToFormKey(string text) => IsFormKeyNullSynonym(text) ? FormKey.Null : FormKey.Factory(text);

    /// <summary>Pre-flight value-shape check for a NORMAL FormLink Set: a null-clear synonym, or a value that parses
    /// as a FormKey. The type-only <c>CoercibilityReject</c> never inspected the value, so "00000000"/"0" were
    /// accepted then threw at FormKey.Factory on apply; this closes that hole at the gate. Shares the synonym
    /// recognizer with the apply path so the gate and the engine agree on a legal value. Null-tolerant for symmetry
    /// with the FLOI sibling (<see cref="TryClassifyFloiValue"/>): all current callers guard non-null, but a future
    /// caller can't trip an NRE in FormKey.TryFactory.</summary>
    internal static bool IsValidFormLinkValue(string? text) => IsFormKeyNullSynonym(text) || (text is not null && FormKey.TryFactory(text, out _));

    // ---- List INDEX value-shape (write pre-flight: key/index value-shape gate) ---------------------------------
    //  A list SetAtIndex / InsertAtIndex / Remove(with a key) parses req.Key as the index at apply (ApplyListVerb:
    //  int.Parse(req.Key!, CultureInfo.InvariantCulture)). A non-integer threw FormatException; a NEGATIVE value
    //  parsed but then threw ArgumentOutOfRangeException at the indexer — both UNNAMED accept-then-throws. This
    //  recognizer mirrors that parse EXACTLY (int32, NumberStyles.Integer, InvariantCulture — int.Parse(s, provider)'s
    //  own default style) so the gate and apply can't drift on what counts as a legal index, plus the non-negative
    //  pre-check the indexer would otherwise enforce by throwing. The UPPER bound (index < the live element count) is
    //  NOT checked here — pre-flight has no live collection; an in-range-shaped but too-large index is left to apply,
    //  where it fails named (Q3). Same shared-recognizer discipline as IsValidFormLinkValue / IsFormKeyNullSynonym.
    /// <summary>True iff <paramref name="text"/> is a legal list INDEX SHAPE: a non-negative int32 under the same
    /// parse the apply path uses (<see cref="ApplyListVerb"/>). Shape only — the in-range check is apply's.</summary>
    internal static bool IsValidListIndexValue(string? text) =>
        text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0;

    // ---- Same-call sibling reference (HCBR-2026-06-15-01 Layer B / unit A) -------------------------------------
    //  A create's field VALUE can forward-reference a record created EARLIER in the SAME create call, by its
    //  editorid, written "@<editorid>". The referenced record's local 0x800+ FormKey is not allocated until the
    //  apply phase, so the caller cannot write a literal FormID — this is how the INFO PNAM order-chain
    //  (PreviousDialog -> the prior line) and the Topic back-link (-> the same-call DialogTopic) are authored in
    //  ONE housecarl_bulk_create. WritePatchBuilder.CreateRecords substitutes the token with the sibling's real
    //  FormKey AFTER allocation (single-pass: the prior sibling is already allocated, in spec order). CREATE-CONTEXT
    //  ONLY — the override/set_field (Apply) path has no siblings, so pre-flight there (CorpusRulebook called with a
    //  null sibling set) rejects the token LOUD rather than letting it through to a substitute-nothing apply (Q3, no
    //  accept-then-throw). The recognizer is SHARED by pre-flight (CorpusRulebook) and the apply-side substitution
    //  (CreateRecords) so the gate and the engine cannot drift on the token shape — the same shared-predicate
    //  discipline as IsFormKeyNullSynonym / IsFormLinkOrIndex.
    internal const char SiblingRefSigil = '@';

    /// <summary>True iff <paramref name="text"/> is a same-call sibling reference (<c>@editorid</c>, trimmed); emits
    /// the referenced editorid (also trimmed). The sigil '@' begins neither a Skyrim EditorID nor a FormID, so the
    /// token can't collide with a real FormLink value. Shared by pre-flight (<see cref="CorpusRulebook"/>) and the
    /// create-time substitution (<see cref="WritePatchBuilder.CreateRecords"/>).</summary>
    internal static bool IsSameCallSiblingRef(string? text, out string editorId)
    {
        editorId = "";
        var v = (text ?? "").Trim();
        if (v.Length < 2 || v[0] != SiblingRefSigil) return false;
        editorId = v[1..].Trim();
        return editorId.Length > 0;
    }

    /// <summary>FormLink families — build the matching concrete (nullable vs not) from a "FORMID:ModName.esp" key.
    /// Mutagen distinguishes IFormLink&lt;T&gt; (required) from IFormLinkNullable&lt;T&gt; (optional); the wrong
    /// concrete won't assign to the property, so the target type decides which we construct. A null-synonym value
    /// clears the link to <see cref="FormKey.Null"/> (see <see cref="ToFormKey"/>) rather than throwing.</summary>
    static bool TryFormLink(string? text, Type u, out object? result)
    {
        result = null;
        if (!u.IsGenericType) return false;
        var def = u.GetGenericTypeDefinition();
        var targetGetter = u.GetGenericArguments()[0];
        if (def == typeof(IFormLinkNullable<>) || def == typeof(IFormLinkNullableGetter<>) || def == typeof(FormLinkNullable<>))
        {
            if (text != null)
                result = System.Activator.CreateInstance(typeof(FormLinkNullable<>).MakeGenericType(targetGetter), ToFormKey(text));
            return true;
        }
        if (def == typeof(FormLink<>) || def == typeof(IFormLink<>) || def == typeof(IFormLinkGetter<>))
        {
            if (text != null)
                result = System.Activator.CreateInstance(typeof(FormLink<>).MakeGenericType(targetGetter), ToFormKey(text));
            return true;
        }
        // IFormLinkOrIndex<T> (condition-data targets) is NOT coercible here: its ctor needs the owning arm as a
        // discriminator-flag source, which the parentless Coerce path lacks. It is handled by the parent-aware
        // SetFloi branch in ApplyScalarVerb (wave 4), recognised via IsFormLinkOrIndex. (Was a tracked deferral.)
        return false;
    }

    // ======================================================================
    //  FORMLINKORINDEX — condition-data targets (wave 4). A FormLinkOrIndex<T> holds EITHER a real FormID (form
    //  mode) OR a numeric quest-alias / package-data index (index mode); the owning *ConditionData arm's
    //  UseAliases/UsePackageData bools decide which serialises. The concrete ctor takes the arm as that flag source
    //  (scout Phase A), so this lives OUTSIDE Coerce (which has no parent) — a parent-aware branch in
    //  ApplyScalarVerb. IsFormLinkOrIndex is the ONE predicate the engine write, the pre-flight (CorpusRulebook),
    //  and coerce-audit all share, so they cannot drift on which leaves are FLOI.
    // ======================================================================

    /// <summary>True iff <paramref name="t"/> (nullable-unwrapped) is a Mutagen <c>FormLinkOrIndex&lt;T&gt;</c>
    /// family type (the mutable, getter, or concrete form). Recognised by its generic definition — like the engine
    /// already recognises IFormLink&lt;&gt;/IList&lt;&gt;/GenderedItem&lt;&gt; — so coverage is by construction, never
    /// a per-record-type hand-list.</summary>
    internal static bool IsFormLinkOrIndex(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (!u.IsGenericType) return false;
        var n = u.GetGenericTypeDefinition().Name;
        return n.StartsWith("IFormLinkOrIndex", StringComparison.Ordinal)
            || n.StartsWith("FormLinkOrIndex", StringComparison.Ordinal);
    }

    /// <summary>How a condition target serialises: a real FormID, or a numeric index read as a quest alias or a
    /// package-data index. The arm's UseAliases/UsePackageData bools carry this on disk.</summary>
    internal enum FloiMode { Form, IndexAlias, IndexPackData }

    /// <summary>Classify a condition-target VALUE into its mode + payload, auto-inferred from the value alone
    /// (Aaron 2026-05-31; scout §E.1): a <c>FORMID:Plugin.esp</c> is form mode; explicit <c>alias N</c> /
    /// <c>packdata N</c> is the named index mode; a bare integer is index mode defaulting to alias (the common index
    /// case). Throws fail-loud on anything else (Q3 — never guessed/wrong four bytes).</summary>
    static (FloiMode mode, FormKey key, uint index) ClassifyFloiValue(string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new InvalidOperationException("Empty condition-target value.");
        if (TryIndexPrefix(v, "alias", out var ai)) return (FloiMode.IndexAlias, default, ai);
        if (TryIndexPrefix(v, "packdata", out var pi)) return (FloiMode.IndexPackData, default, pi);
        if (v.Contains(':')) return (FloiMode.Form, FormKey.Factory(v), 0u);   // FormKey.Factory throws on a malformed id
        return (FloiMode.IndexAlias, default, ParseUInt(v));                    // bare integer -> index (default alias); throws if not a uint
    }

    static bool TryIndexPrefix(string v, string prefix, out uint index)
    {
        index = 0;
        if (!v.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)) return false;
        index = ParseUInt(v[(prefix.Length + 1)..].Trim());
        return true;
    }

    /// <summary>Non-throwing mirror of <see cref="ClassifyFloiValue"/> for pre-flight — does this value name a legal
    /// condition target? — so the rulebook and the engine agree on what a valid FLOI value is.</summary>
    internal static bool TryClassifyFloiValue(string value)
    {
        try { ClassifyFloiValue(value); return true; }
        catch { return false; }
    }

    /// <summary>Set a condition-data <c>FormLinkOrIndex&lt;T&gt;</c> target from a value, auto-inferring the mode and
    /// setting the owning arm's discriminator to match. The concrete ctor (scout Phase A) takes the arm as the flag
    /// source: <c>(arm, FormKey)</c> [form] or <c>(arm, uint)</c> [index]. Fail-loud if the parent is not a
    /// flag-bearing arm, the value is unclassifiable, or the ctor is absent (Q3).</summary>
    static void SetFloi(object arm, PropertyInfo prop, string value)
    {
        if (arm is not IFormLinkOrIndexFlagGetter)
            throw new InvalidOperationException(
                $"'{prop.Name}' is a FormLinkOrIndex target but its parent {arm.GetType().Name} is not a condition-data " +
                "arm (no UseAliases/UsePackageData discriminator) — cannot set (surfaced, not guessed; Q3).");

        var (mode, key, index) = ClassifyFloiValue(value);   // throws fail-loud on an unclassifiable value

        // The concrete closed FormLinkOrIndex<T>: prefer the live instance's runtime type, else map the declared
        // interface to its concrete (IFormLinkOrIndex<T> -> FormLinkOrIndex<T>) via the shared ConcreteOf.
        var concrete = prop.GetValue(arm)?.GetType()
            ?? ConcreteOf(Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType)
            ?? throw new InvalidOperationException($"No concrete FormLinkOrIndex type for {Pretty(prop.PropertyType)}.");

        // Set the arm's discriminator to match the inferred mode: form = both off; index = the matching flag on.
        SetArmFlag(arm, "UseAliases", mode == FloiMode.IndexAlias);
        SetArmFlag(arm, "UsePackageData", mode == FloiMode.IndexPackData);

        // Construct via the discovered (arm, FormKey)/(arm, uint) ctor and assign.
        var payloadType = mode == FloiMode.Form ? typeof(FormKey) : typeof(uint);
        var arg = mode == FloiMode.Form ? (object)key : index;
        var ctor = concrete.GetConstructors().FirstOrDefault(c =>
                c.GetParameters() is { Length: 2 } p
                && typeof(IFormLinkOrIndexFlagGetter).IsAssignableFrom(p[0].ParameterType)
                && p[1].ParameterType == payloadType)
            ?? throw new InvalidOperationException(
                $"{Pretty(concrete)}: no (IFormLinkOrIndexFlagGetter, {payloadType.Name}) constructor — SURFACE. Ctors: {CtorList(concrete)}");
        prop.SetValue(arm, ctor.Invoke(new[] { arm, arg }));
    }

    /// <summary>Set one of the arm's discriminator bools (UseAliases / UsePackageData) through the engine's writable-
    /// property resolution. Fail-loud if absent or get-only (a real condition arm always carries both; Q3).</summary>
    static void SetArmFlag(object arm, string flagName, bool value)
    {
        var p = ResolveProperty(arm.GetType(), flagName)
            ?? throw new InvalidOperationException($"Condition arm {arm.GetType().Name} has no '{flagName}' discriminator field.");
        if (!p.CanWrite) throw new InvalidOperationException($"Discriminator '{flagName}' on {arm.GetType().Name} is not writable.");
        p.SetValue(arm, value);
    }

    /// <summary>The corpus-derived value-type family (coerce-audit enumerates exactly these): Color, Percent,
    /// Noggog point structs (P2*/P3*), MemorySlice&lt;byte&gt; blobs, AssetLink paths, ModKey/FormKey, and the
    /// PEX-metadata leftovers (DateTime/Char/String[]). Construction is reflection-robust (ctor / static factory /
    /// implicit op) so it targets whatever surface Mutagen/Noggog expose. Extend HERE when the audit surfaces a
    /// new writable value type; the audit keeps this complete by construction.</summary>
    static bool TryValueType(string? text, Type u, out object? result)
    {
        result = null;

        // System.Drawing.Color — "R,G,B" or "R,G,B,A" (bytes 0-255).
        if (u == typeof(System.Drawing.Color)) { if (text != null) result = ParseColor(text); return true; }

        // Time/date value types — Climate sun times (TimeOnly) + PEX-file metadata (DateTime).
        if (u == typeof(DateTime)) { if (text != null) result = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); return true; }
        if (u == typeof(TimeOnly)) { if (text != null) result = TimeOnly.Parse(text, CultureInfo.InvariantCulture); return true; }
        // PEX-file metadata leftovers (Char / delimited String[]).
        if (u == typeof(char)) { if (text != null) result = char.Parse(text); return true; }
        if (u == typeof(string[])) { if (text != null) result = text.Length == 0 ? Array.Empty<string>() : text.Split(','); return true; }

        // Mutagen value types: FormKey/ModKey (non-identity content uses, e.g. MasterReference.Master) + the 4-char RecordType.
        if (u == typeof(FormKey)) { if (text != null) result = FormKey.Factory(text); return true; }
        if (u == typeof(ModKey)) { if (text != null) result = ConstructFromString(u, text); return true; }
        if (u == typeof(RecordType)) { if (text != null) result = ConstructFromString(u, text); return true; }

        // Mutagen TranslatedString — set the whole localized string from a plain string (the common "set a name /
        // description" case), via the SAME implicit conversion `record.Name = "x"` uses, so it serialises identically.
        // (Modeled as a substruct in the corpus; recognising it here also makes ApplyVerb Set it wholesale.)
        if (u.FullName == "Mutagen.Bethesda.Strings.TranslatedString")
        {
            if (text != null) result = ImplicitFromString(u, text);
            return true;
        }

        // Noggog.Percent — a [0..1] fraction (single-component ctor).
        if (u.FullName == "Noggog.Percent") { if (text != null) result = ConstructByCtor(u, new[] { text }); return true; }

        // Noggog point structs P2*/P3* — comma-separated components, each coerced to its ctor param type.
        if (u.Namespace == "Noggog" && (u.Name.StartsWith("P2") || u.Name.StartsWith("P3")))
        {
            if (text != null) result = ConstructByCtor(u, text.Split(','));
            return true;
        }

        // Noggog (ReadOnly)MemorySlice<byte> — raw blob as a hex string.
        if (u.IsGenericType
            && (u.GetGenericTypeDefinition() == typeof(MemorySlice<>) || u.GetGenericTypeDefinition() == typeof(ReadOnlyMemorySlice<>))
            && u.GetGenericArguments()[0] == typeof(byte))
        {
            if (text != null) result = ConstructFromValue(u, Convert.FromHexString(text));
            return true;
        }

        // Mutagen AssetLink<T> family — a path string (texture / model / sound / …). Recognises the mutable concrete
        // AssetLink<T>, its getter overlay AssetLinkGetter<T>, AND the IAssetLink(Getter)<T> INTERFACES — because a
        // COLLECTION element's runtime type is the interface, not the concrete (List<IAssetLinkGetter<T>> /
        // ExtendedList<IAssetLink<T>>), so the concrete-only check missed every asset-link LIST element (Heisen
        // 2026-06-26: SoundDescriptor.SoundFiles). Every arm maps to the MUTABLE AssetLink<T> we construct — the same
        // getter-interface→concrete map TryFormLink does for IFormLinkGetter<T>→FormLink<T> — and AssetLink<T>
        // implements both interfaces, so the built element assigns into either list. Recognised by the SAME by-name
        // family predicate the read path uses (IsAssetLinkFamily), so the two surfaces can't drift on what an asset
        // link is.
        if (u.IsGenericType && IsAssetLinkFamily(u))
        {
            if (text != null)
            {
                var concrete = typeof(Mutagen.Bethesda.Plugins.Assets.AssetLink<>).MakeGenericType(u.GetGenericArguments()[0]);
                result = ConstructFromString(concrete, text);
            }
            return true;
        }

        return false;
    }

    /// <summary>Parse "R,G,B" (alpha 255) or "R,G,B,A" (bytes 0-255) into a <see cref="System.Drawing.Color"/>.</summary>
    static object ParseColor(string text)
    {
        var p = text.Split(',').Select(s => byte.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        return p.Length switch
        {
            3 => System.Drawing.Color.FromArgb(p[0], p[1], p[2]),
            4 => System.Drawing.Color.FromArgb(p[3], p[0], p[1], p[2]), // R,G,B,A -> FromArgb(a,r,g,b)
            _ => throw new InvalidOperationException($"Color expects 'R,G,B' or 'R,G,B,A' bytes; got '{text}'."),
        };
    }

    /// <summary>Construct a multi-component value struct (Percent / P2* / P3*) by splitting into ctor params and coercing each.</summary>
    static object ConstructByCtor(Type t, string[] parts)
    {
        var ctor = t.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == parts.Length)
            ?? throw new InvalidOperationException(
                $"{t.Name}: no public ctor taking {parts.Length} component(s). Ctors: {CtorList(t)}");
        var ps = ctor.GetParameters();
        var argv = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++) argv[i] = Coerce(parts[i].Trim(), ps[i].ParameterType);
        return ctor.Invoke(argv);
    }

    static object ConstructFromString(Type t, string s) => ConstructFromArg(t, s, typeof(string));
    static object ConstructFromValue(Type t, object v) => ConstructFromArg(t, v, v.GetType());

    /// <summary>Build <paramref name="t"/> from a single argument via the first matching ctor, static factory, or implicit operator.</summary>
    static object ConstructFromArg(Type t, object arg, Type argType)
    {
        var ctor = t.GetConstructors().FirstOrDefault(c =>
            c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsAssignableFrom(argType));
        if (ctor is not null) return ctor.Invoke(new[] { arg });

        var statics = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == t && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(argType));
        var fac = statics.FirstOrDefault(m => m.Name is not ("op_Implicit" or "op_Explicit")) ?? statics.FirstOrDefault();
        if (fac is not null) return fac.Invoke(null, new[] { arg })!;

        throw new InvalidOperationException(
            $"{t.Name}: no ctor / static factory / implicit op accepting {argType.Name}. Ctors: {CtorList(t)}");
    }

    static string CtorList(Type t) =>
        string.Join(" | ", t.GetConstructors().Select(c => $"({string.Join(", ", c.GetParameters().Select(p => Pretty(p.ParameterType)))})"));

    /// <summary>Build <paramref name="t"/> from a string via its implicit string operator (preferred, so the result
    /// matches `field = "x"` byte-for-byte), falling back to a ctor / factory.</summary>
    static object ImplicitFromString(Type t, string s)
    {
        var op = t.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
            m.Name == "op_Implicit" && m.ReturnType == t
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        return op is not null ? op.Invoke(null, new object[] { s })! : ConstructFromString(t, s);
    }

    /// <summary>Non-throwing coercion, for the rulebook's pre-flight value check.</summary>
    internal static bool TryCoerce(string text, Type type, out object? result)
    {
        try { result = Coerce(text, type); return true; }
        catch { result = null; return false; }
    }

    /// <summary>Resolve a runtime type from an assembly-qualified name (corpus AQ fields).</summary>
    internal static Type? ResolveType(string assemblyQualifiedName)
    {
        try { return Type.GetType(assemblyQualifiedName); }
        catch { return null; }
    }

    // ======================================================================
    //  SHARED REFLECTION HELPERS
    // ======================================================================
    internal static PropertyInfo? ResolveProperty(Type type, string name)
    {
        var candidates = new List<PropertyInfo>();
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(type);
        foreach (var i in type.GetInterfaces()) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p is not null) candidates.Add(p);
        }
        return candidates.OrderByDescending(p => p.CanWrite).FirstOrDefault();
    }

    /// <summary>Set a member to null through the SAME settability resolution the engine writes through
    /// (<see cref="ResolveProperty"/> — prefers the writable declaration across the type's interfaces). Returns
    /// false when the member resolves only to a get-only property (a Mutagen always-present collection/substruct
    /// that is never absent), so an absent-materialization proof can skip it cleanly. Shared by the proof's clear
    /// helpers so they clear state through the engine's settability definition, not a divergent GetProperty()
    /// (baseline review S3).</summary>
    internal static bool TrySetMemberToNull(object parent, string member)
    {
        var prop = ResolveProperty(parent.GetType(), member);
        if (prop is null || !prop.CanWrite) return false;
        prop.SetValue(parent, null);
        return true;
    }

    internal static Type? PrimaryGetter(Type recordRuntimeType) =>
        recordRuntimeType.GetInterfaces()
            .Where(i => typeof(IMajorRecordGetter).IsAssignableFrom(i) && i != typeof(IMajorRecordGetter) && i.Name.EndsWith("Getter"))
            .OrderByDescending(i => i.GetInterfaces().Length)
            .FirstOrDefault();

    static string Sha(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    // ======================================================================
    //  COERCE-AUDIT  (coerce-audit) — completeness guard for the value-type surface.
    //
    //  By construction: walks every WRITABLE leaf in corpus.json, resolves the CLR type a
    //  Set/Add must coerce to (scalar/enum/value/formlink field types; scalar list/dict
    //  element types), and asserts CanCoerce holds for each. Any uncoercible type is the
    //  exact, deduplicated gap to add to TryValueType — derived from Mutagen's own model,
    //  never guessed. Also flags AQ names that fail to resolve. Q3: report, never silent-skip.
    // ======================================================================
    public static int RunCoerceAudit(string[] args)
    {
        var corpusPath = args.Length > 0 ? args[0] : CorpusRulebook.CorpusPath;
        Corpus corpus;
        try { corpus = CorpusRulebook.LoadCorpus(corpusPath); }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }

        var uncoercible = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var typeErased = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var ownedRecord = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var unresolved = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var substructWhole = new SortedDictionary<string, bool>(StringComparer.Ordinal); // type -> coercible-as-whole
        int hardTargets = 0, navOrBuild = 0, floiHandled = 0;

        static void Bump(SortedDictionary<string, (int, List<string>)> bag, string key, string example)
        {
            var e = bag.TryGetValue(key, out var v) ? v : (0, new List<string>());
            e.Item1++;
            if (e.Item2.Count < 4) e.Item2.Add(example);
            bag[key] = e;
        }

        foreach (var ts in corpus.Types.Values)
        foreach (var f in ts.Fields)
        {
            if (!f.Writable) continue;
            if (f.IsIdentity) continue; // record identity (FormKey/ModKey) -> flat-reject upstream, never coerced
            var site = $"{ts.Name}({ts.Kind}).{f.Name}";

            string? aq;
            switch (f.Cardinality)
            {
                case "scalar":
                case "enum":
                case "value":
                case "formlink":
                    aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
                    break;
                case "list":
                case "dict":
                    // scalar/enum/formlink elements are coercion targets; modeled-struct/arm/record elements
                    // (ElementTypeRef) are build-cases — EXCEPT a WHOLE-COERCIBLE element (an AssetLink path), which
                    // carries an ElementTypeRef yet is SET as one coerced value, not built from parts. The bare
                    // `ElementTypeRef is null` test mis-skipped those (SoundDescriptor.SoundFiles,
                    // Weather.CloudTextures) into navOrBuild, hiding them from the audit denominator — the blind spot
                    // that let the asset-link LIST element ship uncoercible (Heisen 2026-06-26). Route a whole-
                    // coercible element to the SAME resolve+CanCoerce path the scalar elements take, recognised by the
                    // SAME predicate the rulebook/classifier use (no drift on what a whole-coercible element is); its
                    // getter-interface AQ resolves at runtime (verified — ci-all green on Linux too), so it lands on
                    // CanCoerce, not the unresolved bucket.
                    // COUPLING NOTE: this routes the getter AQ through the shared ResolveType, so the audit here is
                    // STRICTER than IsWholeCoercibleElement itself — that predicate carries a by-NAME fallback for when
                    // the cross-assembly nested-generic getter AQ does NOT resolve via Type.GetType, which this branch
                    // does not. Today every asset-link element's AQ resolves; if a future Mutagen shape stops resolving,
                    // it lands in `unresolved` → audit RED (loud, Q3-fine — never a silent skip), which is the cue to
                    // mirror the name-fallback here.
                    if (f.ElementTypeRef is null && f.ElementTypeAssemblyQualified is { } eaq) aq = eaq;
                    else if (IsWholeCoercibleElement(f.ElementTypeRef, f.ElementTypeAssemblyQualified)
                             && f.ElementTypeAssemblyQualified is { } weaq) aq = weaq;
                    else { navOrBuild++; continue; }
                    break;
                case "substruct":
                {
                    // navigate-into; record whole-coercibility (the TranslatedString-style case) but don't gate on it.
                    var saq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
                    if (ResolveType(saq) is { } sst) substructWhole[sst.FullName ?? saq] = CanCoerce(sst);
                    navOrBuild++;
                    continue;
                }
                default: // polymorphic, etc. -> arm-build, not scalar coercion
                    navOrBuild++;
                    continue;
            }

            if (string.IsNullOrEmpty(aq)) { navOrBuild++; continue; }
            hardTargets++;
            var rt = ResolveType(aq);
            if (rt is null) { Bump(unresolved, aq, site); continue; }
            // FormLinkOrIndex condition targets are now WRITABLE via the parent-aware SetFloi branch (wave 4) — they
            // pass the gate like any coercible leaf, counted positively below (was the 156-site deferred bucket).
            if (IsFormLinkOrIndex(rt)) { floiHandled++; continue; }
            if (!CanCoerce(rt))
            {
                var u = Nullable.GetUnderlyingType(rt) ?? rt;
                var ex = $"{site} [{f.Cardinality}]";
                // Partition by PRINCIPLE (not a hand-list). Each deferred bucket names its own WIRE-WHEN trigger in
                // the report below, so a future stage picks it up at the right time instead of forgetting it.
                if (u == typeof(object)) Bump(typeErased, u.FullName ?? u.Name, ex);
                else if (corpus.Types.TryGetValue(u.Name, out var ut) && ut.Kind == "record") Bump(ownedRecord, u.FullName ?? u.Name, ex);
                else Bump(uncoercible, u.FullName ?? u.Name, ex);
            }
        }

        Console.WriteLine($"=== coerce-audit over {corpus.TotalTypes} types ===");
        Console.WriteLine($"Hard coercion targets (writable scalar/enum/value/formlink leaves + scalar list/dict elements): {hardTargets}");
        Console.WriteLine($"Navigate/build leaves skipped (substruct/polymorphic/struct-element): {navOrBuild}");
        Console.WriteLine();

        if (unresolved.Count > 0)
        {
            Console.WriteLine($"!! {unresolved.Count} assembly-qualified type name(s) FAILED to resolve (corpus/runtime mismatch):");
            foreach (var (k, v) in unresolved)
                Console.WriteLine($"   {v.count,5}x  {k}\n            e.g. {string.Join(", ", v.examples)}");
            Console.WriteLine();
        }

        void Dump(string header, SortedDictionary<string, (int count, List<string> examples)> bag)
        {
            var sites = bag.Values.Sum(v => v.count);
            Console.WriteLine($"{header}: {bag.Count} distinct, {sites} field-site(s)" + (bag.Count == 0 ? "." : ":"));
            foreach (var (k, v) in bag.OrderByDescending(kv => kv.Value.count))
                Console.WriteLine($"   {v.count,5}x  {k}\n            e.g. {string.Join("; ", v.examples)}");
        }

        if (uncoercible.Count == 0)
            Console.WriteLine("UNCOERCIBLE value types (real gaps): none — every coercible writable value-leaf is covered.");
        else
            Dump("UNCOERCIBLE value types (REAL GAPS — extend TryValueType)", uncoercible);
        Console.WriteLine();

        // Expected non-coercible-from-string (honest loud reject, NOT a gap). Each names its WIRE-WHEN trigger so a
        // future stage plumbs it in at the right time. Surfaced here every run; never silently skipped.
        Dump("DEFERRED — type-erased `object` condition params. WIRE-WHEN: a typed-value wire format exists (the value " +
             "carries its own type), i.e. the step-8 MCP API", typeErased);
        Console.WriteLine();
        Console.WriteLine($"HANDLED (wave 4) — FormLinkOrIndex condition targets, via the parent-aware SetFloi branch " +
                          $"(auto-infers form-vs-index from the value): {floiHandled} site(s) — was the deferred bucket.");
        Console.WriteLine();
        Dump("DEFERRED — owned-child records (whole-record assignment, not a string). WIRE-WHEN: record creation/composition lands", ownedRecord);
        Console.WriteLine();

        var coercibleSubstructs = substructWhole.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        Console.WriteLine("Substruct types coercible-as-whole (informational — e.g. TranslatedString): " +
                          (coercibleSubstructs.Count == 0 ? "none" : string.Join(", ", coercibleSubstructs)));
        Console.WriteLine();

        var honestReject = typeErased.Values.Sum(v => v.count) + ownedRecord.Values.Sum(v => v.count);
        var pass = uncoercible.Count == 0 && unresolved.Count == 0;
        Console.WriteLine(pass
            ? $"=== PASS: coercion surface complete by construction ({honestReject} site(s) honestly non-coercible, reported above) ==="
            : "=== INCOMPLETE: extend TryValueType with the REAL GAPS above, then re-run ===");
        return pass ? 0 : 1;
    }

    // ======================================================================
    //  COERCE-SELFTEST  (coerce-selftest) — proves the value-type CONSTRUCTIONS actually
    //  build a valid, assignable instance from a sample string (coerce-audit proves only
    //  RECOGNITION). Diagnoses on failure by dumping the type's ctor surface.
    // ======================================================================
    public static int RunCoerceSelftest(string[] args)
    {
        var texAsset = typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.Assets.SkyrimTextureAssetType");

        var samples = new List<(string label, Type type, string text)>
        {
            ("Color rgb",         typeof(System.Drawing.Color), "255,128,0"),
            ("Color rgba",        typeof(System.Drawing.Color), "255,128,0,64"),
            ("DateTime",          typeof(DateTime), "2026-05-30T12:00:00"),
            ("Char",              typeof(char), "G"),
            ("String[]",          typeof(string[]), "Foo,Bar"),
            ("FormKey",           typeof(FormKey), "012E4B:Skyrim.esm"),
            ("ModKey",            typeof(ModKey), "Skyrim.esm"),
            ("Percent",           typeof(Noggog.Percent), "0.5"),
            ("P3Float",           typeof(Noggog.P3Float), "1.5,2.5,3.5"),
            ("P2Int16",           typeof(Noggog.P2Int16), "4,5"),
            ("MemorySlice<byte>", typeof(MemorySlice<byte>), "DEADBEEF"),
            ("ReadOnlyMemSlice",  typeof(ReadOnlyMemorySlice<byte>), "CAFE"),
            ("RecordType",        typeof(RecordType), "EDID"),
            ("TimeOnly",          typeof(TimeOnly), "06:30:00"),
        };
        if (texAsset is not null)
            samples.Add(("AssetLink<Texture>", typeof(Mutagen.Bethesda.Plugins.Assets.AssetLink<>).MakeGenericType(texAsset), @"textures\hc\test.dds"));
        // AssetLink INTERFACE forms — the runtime type a COLLECTION element exposes, NOT the concrete: a
        // List<IAssetLinkGetter<T>> / ExtendedList<IAssetLink<T>> element coerces to the getter/setter INTERFACE,
        // which the concrete-only rule missed (Heisen 2026-06-26: SoundDescriptor.SoundFiles). Each must build the
        // mutable AssetLink<T> and be assignable to its interface (what list .Add demands). Both asset TYPES (sound +
        // texture) and both interface arms (setter + getter) are covered, so the family fix is proven generic, not
        // sound-special.
        var sndAsset = typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType");
        if (sndAsset is not null)
        {
            samples.Add(("IAssetLink<Sound> (setter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLink<>).MakeGenericType(sndAsset), @"Sound\fx\hc\test.wav"));
            samples.Add(("IAssetLinkGetter<Sound> (getter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter<>).MakeGenericType(sndAsset), @"Sound\fx\hc\test.wav"));
        }
        if (texAsset is not null)
        {
            samples.Add(("IAssetLink<Texture> (setter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLink<>).MakeGenericType(texAsset), @"textures\hc\test2.dds"));
            samples.Add(("IAssetLinkGetter<Texture> (getter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter<>).MakeGenericType(texAsset), @"textures\hc\test2.dds"));
        }

        int ok = 0;
        foreach (var (label, type, text) in samples)
        {
            try
            {
                var v = Coerce(text, type);
                var assignable = v is not null && type.IsInstanceOfType(v);
                Console.WriteLine($"  [{(assignable ? "OK" : "??")}] {label,-20} '{text}' -> {v?.GetType().Name ?? "null"}  (= {v})");
                if (assignable) ok++;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException is { } ie ? $" / {ie.GetType().Name}: {ie.Message}" : "";
                Console.WriteLine($"  [FAIL] {label,-20} '{text}' -> {ex.GetType().Name}: {ex.Message}{inner}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"=== coerce-selftest: {ok}/{samples.Count} constructed + assignable ===");
        return ok == samples.Count ? 0 : 1;
    }

    static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    static string Pretty(Type t)
    {
        if (t.IsByRef) return Pretty(t.GetElementType()!) + "&";
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
        }
        return t.Name;
    }
}

/// <summary>Thrown when a write needs to MATERIALIZE an absent substruct whose concrete type has no parameterless
/// constructor — Mutagen's composition types (GenderedItem&lt;T&gt; male/female pairs, Array2d&lt;T&gt; grids) that
/// can only be built from their parts. This is the absent-substruct COMPOSITION deferral: a real, named gap that
/// rides the composition wave (wave 1) with the struct-element collections, never a silent skip (Q3). It is an
/// <see cref="InvalidOperationException"/> so existing fail-loud handlers still catch it, while a proof/instrument
/// can catch it SPECIFICALLY to tally the deferral by construction (no hand-listing of the composition types).</summary>
public sealed class CompositionRequiredException : InvalidOperationException
{
    public string Segment { get; }
    public Type SubstructType { get; }
    public CompositionRequiredException(string segment, Type substructType)
        : base($"Absent substruct '{segment}' of type {substructType.Name} has no parameterless constructor — it is a " +
               "COMPOSITION type (e.g. GenderedItem<T> / Array2d<T>) buildable only from its parts. Deferred to the " +
               "composition wave (wave 1); surfaced as a named deferral, never synthesized to a wrong value.")
    {
        Segment = segment;
        SubstructType = substructType;
    }
}

/// <summary>A serialize-boundary <see cref="NullReferenceException"/> re-stamped as a loud, NAMED refusal
/// (HCBR-2026-06-15-01 PR-C, PART B). Mutagen's binary writer throws a bare NRE — no field name — when it dereferences
/// a record's REQUIRED modeled sub-field that was left null; the dominant cause is a COMPOSED record missing a required
/// polymorphic sub-arm (a Condition without its Data arm, an element missing a required part) — and, per HCBR-2026-07-04,
/// the null may surface bare OR wrapped in the parallel writer's AggregateException (see <see cref="WriteEngine.RootNullArm"/>).
/// The corpus now carries
/// faithful polymorphic nullability (S4 Track D), but that flag is NOT a "required arm at serialize" signal —
/// NpcConfiguration.Level reads <c>Nullable=false</c> yet serializes fine when null, while Condition.Data (also
/// <c>Nullable=false</c>) throws — so a pre-flight gate on the flag would over-reject or need a hand-curated list
/// (cornerstone §3), and this stays caught at the serialize boundary instead. The staged temp is already discarded by the time this throws (nothing on disk; the
/// target is untouched), and the caller's serialize catch renders it as an all-or-nothing <c>Fail</c>. The original
/// NRE is preserved as <see cref="Exception.InnerException"/>. Q3 — no silent failure, no opaque message.</summary>
public sealed class NullArmSerializeException : InvalidOperationException
{
    public NullArmSerializeException(Exception inner)
        : base("a required modeled sub-field was null when Mutagen serialized the patch (a NullReferenceException in the " +
               "writer). The cause is a COMPOSED record that left a required polymorphic sub-field unset — e.g. a Condition " +
               "composed without its Data arm, or a leveled-list / effect element missing a required part. Compose that " +
               "sub-field too (select the arm via compose). Nothing was written — the staged file was discarded and the " +
               "target is untouched. If no composition was involved, this is an engine/Mutagen inconsistency — surface it, " +
               "don't work around it.", inner)
    {
    }
}

/// <summary>The in-place write's refusal to re-serialize a plugin flagged LOCALIZED — thrown by
/// <see cref="WriteEngine.WriteInPlace"/> before the staging directory is created, so nothing is written, staged, or
/// cleaned up.
///
/// <para>WHY, measured (<c>repoint-strings-probe</c> — which now stops at this very refusal, so reproducing the
/// measurement means disabling the check below and re-running; the probe's summary says so): a localized plugin keeps its text in sibling
/// <c>.STRINGS</c>/<c>.DLSTRINGS</c>/<c>.ILSTRINGS</c> files and carries only integer indices into them.
/// <see cref="WriteEngine.WriteInPlace"/> stages the serialize, which emits a freshly numbered set of those files
/// beside the staged plugin, and then commits THE PLUGIN ALONE — the emitted tables are discarded with the staging
/// directory. The committed plugin's new indices are therefore read against the plugin's old, untouched strings file,
/// and the values land on records they do not belong to: in the probe a weapon read a book's name. The probe's arm C
/// shows this needs no strings-resolution problem of any kind — a plugin whose strings sit correctly beside it, read
/// correctly through the ordinary open, is corrupted just the same.</para>
///
/// <para>So this is NOT the read-side blanking of a localized plugin whose strings resolve elsewhere; that one is a
/// separate defect on the way IN. This is the round-trip's write half, and it is why resolving the strings correctly
/// on the way in does not on its own make the rewrite faithful. The settled answer to both is this refusal: houseCARL
/// does not rewrite a localized plugin in place, whatever arrangement its tables are in.</para>
///
/// <para><b>One sentence, one home.</b> This class held a second, constructor-built refusal sentence describing the
/// pre-refusal behaviour ("some records kept their text, some went blank…") long after the live sentence had stopped
/// saying that. Two refusal texts for one behaviour is one of them waiting to be found by whoever wires up the
/// obvious-looking constructor, so the constructor and its sentence are gone: the only way to build this exception is
/// <see cref="FromSentence"/>, off <see cref="Shaped"/>.</para></summary>
public sealed class LocalizedTargetUnsupportedException : InvalidOperationException
{
    /// <summary>The refusal for a localized plugin whose strings houseCARL cannot rewrite as a set — one sentence per
    /// SHAPE, because the shapes fail for different reasons and a single sentence covering all of them can only be
    /// true of the one it was written for.
    ///
    /// <para>Every remedy named here is one the guard WALKS on a fixture before this text may claim it (the standing
    /// rule that a refusal's remedy is measured before the refusal names it). Where a shape has no measured remedy of
    /// its own, this says so and falls back to <paramref name="laneClause"/> rather than inventing one.</para></summary>
    public static string Shaped(string pluginFileName, LocalizedAssessment a, string? laneClause = null)
    {
        var head = $"houseCARL did not write '{pluginFileName}' — the file is unchanged and nothing was staged. ";
        var remedy = RemedyFor(a, laneClause);
        return head + ShapeBody(a) + (remedy is null ? "" : " " + remedy);
    }

    /// <summary>The shape half of <see cref="Shaped"/> on its own — no "houseCARL did not write X" head. For a lane
    /// refusing over a plugin the caller did not name (a compaction blocked by one of its referencers): the caller
    /// needs that plugin's reason and remedy, but nesting a whole second refusal sentence inside the first reads as
    /// two refusals rather than one explained one.
    ///
    /// <para><b>THE RENDER SEAM.</b> Every shape gets an arm here, and no sentence is rendered off a collapsed
    /// boolean. The DECISION may collapse — "anything that is not NotLocalized refuses" is right, and fail-closed —
    /// but the WORDS may not, because the collapse makes <see cref="LocalizedShape.Unreadable"/> inherit a localized
    /// plugin's sentences: "is flagged LOCALIZED", "its text lives in separate .STRINGS files", asserted about a file
    /// houseCARL explicitly could not open. That is what the fail-open sentence used to do in mirror image — name a
    /// cause it had not established instead of the one it had — and it stopped being harmless the moment the service
    /// pre-flights began failing CLOSED on an unreadable target (Aaron's review of PR #436).</para></summary>
    public static string ShapeBody(LocalizedAssessment a) => a.Shape switch
    {
        // NOT AN ARRANGEMENT, so not composed from the arrangement halves at all. The file was never opened: nothing
        // is claimed about localization, and the close is the unclassifiable one rather than Settled — which is a
        // sentence about localized plugins.
        LocalizedShape.Unreadable => UnreadableText + SettledUnreadable,

        LocalizedShape.NotLocalized
            or LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
            or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly or LocalizedShape.StringsFolderUnreadable
            or LocalizedShape.Nowhere
            => WhereTheTextIs(a) + " " + WhyNotInPlace(a),

        // Enumerated above one by one, so a shape added later lands HERE and says nothing rather than inheriting
        // another shape's words (Q3). Deliberately claims no localization state: the wrong half of this arm to guess
        // at is the half the whole seam exists to stop guessing at.
        _ => "houseCARL has no wording for the arrangement this plugin classified into, so it will not describe it — "
             + "and it does not write in place against a destination it cannot describe.",
    };

    /// <summary>Which remedy a refusal ends on — per shape, for the same reason the body is.
    ///
    /// <para>A lane's clause answers "this plugin is localized, so what should I do instead": drop <c>in_place=</c>,
    /// or (the remove lane) there is no new-plugin form at all. Appended to an UNREADABLE destination it answers a
    /// question nobody established the answer to, and the remove lane's spelling turns a momentary file lock into a
    /// permanent dead end — the #374 shape, reappearing on this arm. So the unreadable destination gets the remedy
    /// that matches what actually happened, and the lane's clause is not appended at all.</para></summary>
    static string? RemedyFor(LocalizedAssessment a, string? laneClause) => a.Shape switch
    {
        LocalizedShape.Unreadable => RemedyUnreadable,
        _ => laneClause,
    };

    /// <summary>The closing sentence every LOCALIZED shape's refusal ends on. Shared verbatim because it is the one
    /// part that IS the same for all of them: the outcome. Deliberately NOT shared with
    /// <see cref="LocalizedShape.Unreadable"/> — see <see cref="SettledUnreadable"/>.</summary>
    const string Settled = " It does not edit a localized plugin in place.";

    /// <summary>The close for a destination houseCARL could not open. <see cref="Settled"/>'s counterpart, and a
    /// separate constant on purpose: "it does not edit a localized plugin in place" states that the file IS one.</summary>
    const string SettledUnreadable =
        " houseCARL does not write to a destination it cannot classify. Nothing here says the file is or is not "
        + "localized — it was never opened, so neither was established.";

    /// <summary>What houseCARL can say about a file it could not open, and the only thing it can say. One home,
    /// because <see cref="WhereTheTextIs"/> and <see cref="ShapeBody"/> both render it.</summary>
    const string UnreadableText = "houseCARL could not read the file at that path to see where its text lives.";

    /// <summary>The remedy for an unreadable destination — measured against what actually failed (the open), not
    /// against a localization state nobody established. Names no lane, because every lane's next step reads the same
    /// file and meets the same failure.</summary>
    public const string RemedyUnreadable =
        "Check whether something else has the file open — Mod Organizer refreshing, an antivirus scan, xEdit, the "
        + "running game — or whether that path names a file that exists at all, and retry once it is free.";

    /// <summary>Why this ARRANGEMENT cannot be rewritten in place — per shape, because the hazard genuinely differs
    /// and one sentence covering all of them can only be true of the one it was written for.
    ///
    /// <para>The shared spelling this replaces described an interruption leaving records reading other records' text.
    /// That is exact for a plugin with a live table set beside it, which a write would have to replace — and false
    /// for the shapes where a write would replace NO existing table: with the text in game-Data or inside an archive,
    /// a plugin write cannot reach the files the indices point at at all, so what it would actually do is leave a
    /// competing set beside the plugin, shadowing the real one and leaving it stale. Naming a mid-write scramble
    /// there describes a hazard that cannot happen and hides the one that can.</para>
    ///
    /// <para>Deliberately says nothing about how to reach an arrangement houseCARL WOULD rewrite. An earlier form
    /// ended each shape with exactly that ("add the missing file, then retry", "move them beside the plugin, then
    /// retry"); those are false now that no arrangement is rewritten in place — one was measured dead-ending in a
    /// second refusal even before the arm was cut. The remedy that ships is the calling lane's.</para></summary>
    public static string WhyNotInPlace(LocalizedAssessment a) => a.Shape switch
    {
        // A live set sits beside the plugin. A rewrite renumbers the indices and would have to replace those exact
        // files in the same breath — the swap houseCARL cannot make atomic.
        LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate =>
            "A localized plugin's text is not in the plugin, so rewriting the plugin renumbers the indices its text is "
            + "looked up by and those .STRINGS files beside it would have to be replaced in the same breath — and "
            + "houseCARL cannot swap a plugin and its tables as one operation, so an interruption would leave records "
            + "reading text that belongs to other records." + Settled,

        // The files the indices point at are somewhere a plugin write cannot reach. Nothing would be replaced; a new
        // set would land beside the plugin and SHADOW the real one, which stays on disk and goes stale.
        LocalizedShape.BsaEmbedded when a.BsaUnreadable =>
            "A localized plugin's text is not in the plugin, and until that archive can be read houseCARL cannot tell "
            + "what rewriting the plugin would leave its text resolving against — nor whether a set written beside the "
            + "plugin would replace that text or merely shadow it." + Settled,

        LocalizedShape.BsaEmbedded =>
            "A localized plugin's text is not in the plugin, and a plugin write cannot rewrite the inside of an "
            + "archive. Rewriting the plugin renumbers the indices its text is looked up by, and the only place "
            + "houseCARL could put matching files is beside the plugin — where they would SHADOW the archive's rather "
            + "than replace them, leaving the archive carrying a table that no longer describes the plugin." + Settled,

        LocalizedShape.GameDataOnly =>
            "A localized plugin's text is not in the plugin, and a plugin write does not reach your game's Data folder. "
            + "Rewriting the plugin renumbers the indices its text is looked up by, and the only place houseCARL could "
            + "put matching files is beside the plugin — where they would SHADOW the set in Data\\Strings rather than "
            + "replace it, leaving that set on disk describing a plugin that has changed underneath it." + Settled,

        // The folder is there and could not be listed, so the ONE thing this shape cannot say is what is in it. Both
        // hazards are live and houseCARL cannot tell which — which is the sentence.
        LocalizedShape.StringsFolderUnreadable =>
            "A localized plugin's text is not in the plugin, and houseCARL could not read the Strings folder beside it "
            + "to see what is in there. Rewriting the plugin renumbers the indices its text is looked up by, and "
            + "houseCARL cannot tell whether the files written beside it would replace a set that is already there or "
            + "land next to one it never saw." + Settled,

        // houseCARL cannot see the source, so it cannot name a hazard it has verified. Saying which one it cannot
        // rule out is the honest form.
        LocalizedShape.Nowhere =>
            "A localized plugin's text is not in the plugin, and houseCARL cannot see the files its indices point at. "
            + "Rewriting the plugin renumbers those indices, and houseCARL cannot tell whether a set written beside it "
            + "would replace what the game reads or shadow it, nor what would be left stale either way." + Settled,

        // The mod being written IS localized (this arm is only reached from the write's choke point, which decides off
        // the mod in memory), and the file already at that path is not. Nothing about an arrangement is claimed.
        LocalizedShape.NotLocalized =>
            "A localized plugin's text is not in the plugin, and houseCARL could not establish where this one's is, so "
            + "it cannot tell what rewriting the plugin would do to that text." + Settled,

        // The file itself could not be opened. ShapeBody does not route here — it has its own arm — but this one
        // exists so a DIRECT caller cannot get a localized plugin's reasoning for a file nobody read either.
        LocalizedShape.Unreadable =>
            "houseCARL could not open the file at that path, so it cannot tell what rewriting it would do to any text "
            + "it carries." + SettledUnreadable,

        // Enumerated above; a shape added later fails LOUD and generic rather than inheriting another's reason (Q3),
        // and claims no localization state of its own — the seam's rule applies to this arm too.
        _ => "houseCARL has no account of this plugin's arrangement, so it cannot say what rewriting it in place "
             + "would do to its text.",
    };

    /// <summary>Where the archive naming this plugin's tables was found — beside the plugin, or in the game folder.</summary>
    static string BsaWhere(LocalizedAssessment a) => a.BsaInGameData ? "in your game's Data folder" : "beside the plugin";

    /// <summary>The SECOND location, when the archive shape is carrying one. An archive decides the shape whatever
    /// else is on disk, but a loose set beside the plugin (or a set in game-Data) is still there and still what a
    /// modder would find if they went looking — so the sentence names both rather than the one that won the
    /// classification.</summary>
    /// <summary>What the <c>Strings\</c> folder beside the plugin actually holds, for a plugin nothing in it matched.
    /// Names the files when there are some, so the modder is not told "nothing is there" while looking at a folder
    /// with files in it — and asserts an absence only where the absence was checked.</summary>
    static string NothingMatched(LocalizedAssessment a)
    {
        var u = a.UnmatchedTables;
        // A CHECKED absence, now that it is one: a folder that could not be listed classifies as
        // StringsFolderUnreadable and never arrives here, so "there are none" is a claim about a folder houseCARL
        // actually read (or found absent) rather than one whose enumeration threw.
        if (u.Total == 0)
            return "no .STRINGS files beside it";
        // The COUNT is the folder's; the NAMES are what fits. Rendering the capped list's length as the count made
        // this sentence false about the modder's disk a third way — the folder holds thirty, the sentence said eight,
        // and the list stopped with no ellipsis to say it had.
        return "the Strings folder beside it holds " + u.Total + " .STRINGS file(s) — "
             + string.Join(", ", u.Names)
             + (u.Unnamed > 0 ? ", and " + u.Unnamed + " more" : "")
             + " — but houseCARL matched none of them to this plugin in a language it recognises";
    }

    static string AlsoLoose(LocalizedAssessment a)
    {
        var also = new List<string>();
        if (a.Languages.Count > 0)
            also.Add(".STRINGS files for it in a Strings folder beside the plugin (" + string.Join(", ", a.Languages) + ")");
        if (a.GameDataLanguages.Count > 0)
            also.Add("a set for it in your game's Data\\Strings folder (" + string.Join(", ", a.GameDataLanguages) + ")");
        return also.Count == 0 ? "" : " — and there is also " + string.Join(", and ", also);
    }

    /// <summary>Where THIS plugin's text actually lives, in the caller's terms — the half of the refusal that is worth
    /// reading, because it tells a modder something about their install they cannot easily see. Every arm states only
    /// what was checked and found.</summary>
    public static string WhereTheTextIs(LocalizedAssessment a)
    {
        return a.Shape switch
        {
            LocalizedShape.LooseComplete =>
                "It is flagged LOCALIZED and its text lives in separate .STRINGS files in a Strings folder beside it ("
                + string.Join(", ", a.Languages) + ").",

            LocalizedShape.LoosePartial =>
                "It is flagged LOCALIZED and its text lives in separate .STRINGS files beside it, of which "
                + string.Join("; ", a.IncompleteLanguages.Select(kv => $"{kv.Key} has no .{string.Join("/.", kv.Value)} file"))
                + ".",

            LocalizedShape.LooseWithGameDataDuplicate =>
                "It is flagged LOCALIZED and its text is in two places at once: separate .STRINGS files beside the "
                + "plugin, and a set for this same plugin in your game's Data\\Strings folder ("
                + string.Join(", ", a.GameDataLanguages) + ").",

            // WHERE the archive is, not merely its name. The game-Data fallback exists precisely because the vanilla
            // masters' tables live in a game archive, so "the archive beside it" was false for that entire class.
            // AlsoLoose names the OTHER location when one exists: the archive decides the shape, but the assessment is
            // still carrying a loose set beside the plugin, and a sentence naming one location while two sit on disk
            // sends the modder to look in the wrong place.
            LocalizedShape.BsaEmbedded when a.BsaUnreadable =>
                "It is flagged LOCALIZED and the archive " + Path.GetFileName(a.BsaPath) + " " + BsaWhere(a)
                + " could not be read, so houseCARL cannot tell whether this plugin's text is inside it"
                + AlsoLoose(a) + ".",

            LocalizedShape.BsaEmbedded =>
                "It is flagged LOCALIZED and its text is inside the archive " + Path.GetFileName(a.BsaPath) + " "
                + BsaWhere(a) + AlsoLoose(a) + ".",

            LocalizedShape.GameDataOnly =>
                "It is flagged LOCALIZED and its text is not beside it — it resolves from your game's Data\\Strings "
                + "folder (" + string.Join(", ", a.GameDataLanguages) + ").",

            // The folder is THERE. Nothing is claimed about its contents, because nothing could be read — the arm that
            // exists so an unlistable folder cannot arrive at Nowhere's sentence and be described as an empty one.
            LocalizedShape.StringsFolderUnreadable =>
                "It is flagged LOCALIZED and there is a Strings folder beside it that houseCARL could not read, so "
                + "whether its text is in there — and in which languages — is unknown" + AlsoLoose(a) + ".",

            // Says what was SEARCHED and what was FOUND, never what exists.
            //
            // This sentence has now been false about the modder's disk twice, each time by asserting an absence it had
            // not checked. First: "no Strings folder beside it", while the folder was there holding a neighbour's
            // tables. Then, after that fix: "no .STRINGS files for this plugin beside it", while ZRef_ptbr.STRINGS sat
            // in that folder — houseCARL had not matched it (ptbr is not a language Mutagen models), which is not the
            // same claim. So the sentence no longer asserts an absence at all: NothingMatched describes the folder as
            // it actually is, and the only claim made is the one houseCARL can stand behind — that nothing in it
            // matched this plugin in a language it recognises.
            LocalizedShape.Nowhere =>
                "It is flagged LOCALIZED and houseCARL cannot find its text: " + NothingMatched(a)
                + ", and no archive beside it"
                + (a.GameDataUnknown ? "" : " or in your game folder")
                + " carrying its tables"
                + (a.GameDataUnknown ? ", and your game's Data folder could not be determined, so it was not searched" : "")
                + ". Mod Organizer merges mod folders when the game runs, so the text may well exist in a different "
                + "mod folder than the plugin — houseCARL reads the folders as they sit on disk and does not see that "
                + "merge.",

            // The plugin could not be read at all, so nothing is said about an arrangement nobody looked at. This is
            // the answer a locked, absent or unparseable destination gets — and it REFUSES rather than proceeding,
            // which is the whole reason the read behind it reports three answers instead of two.
            LocalizedShape.Unreadable => UnreadableText,

            // The mod being written is flagged LOCALIZED, but the file already at that path does not read as one. The
            // write refuses on the mod in hand — this arm is only reached from there, never from a pre-flight, which
            // takes a clear flag as its answer and proceeds.
            LocalizedShape.NotLocalized =>
                "It is flagged LOCALIZED, but the file already at that path does not read as a localized plugin, so "
                + "houseCARL could not establish where the text being written would resolve from.",

            // Every shape is enumerated above; this arm exists so a shape added later fails LOUD and generic rather
            // than silently inheriting another shape's sentence (Q3). It asserts NO localization state: the old
            // spelling ("It is flagged LOCALIZED, so its text lives in separate .STRINGS files…") is exactly the
            // inherited-words defect the seam exists to stop, one level down.
            _ => "houseCARL has no account of where this plugin's text lives.",
        };
    }

    /// <summary>What the three lanes that HAVE a new-plugin equivalent append. Measured before it was named: the
    /// compact guard's REMEDY arm runs the default lane against the localized fixture and reads the written patch back,
    /// with the localized original byte-untouched. Deliberately "a NEW plugin" and not "a new override plugin" —
    /// <c>create</c> shares this clause and authors new records rather than overrides.</summary>
    public const string RemedyDefaultLane =
        "This is the in-place lane only: drop in_place= and houseCARL writes the same change into a NEW plugin instead, " +
        "leaving this file untouched.";

    /// <summary>Remove's clause. It names NO remedy, because there is none to name: a new plugin can override a record
    /// but cannot un-define one, so "do it in a patch instead" would be false here in a way it is not for the other
    /// three. Stating the asymmetry is what this lane owes the caller. (Remove's remedy machinery is being reworked
    /// separately; this deliberately adds nothing for that work to collide with.)</summary>
    public const string RemoveNoEquivalent =
        "This lane has no new-plugin form: a separate plugin can override a record but cannot un-define one, so there is " +
        "no way to remove this record without rewriting the plugin that defines it.";

    /// <summary>Where this plugin's strings actually are, as a clause a lane can drop into its own sentence — for the
    /// lanes that refuse for their OWN reason (compact rebuilds rather than re-serializes) but still owe the caller an
    /// accurate account of the shape, rather than the generic "it is flagged LOCALIZED" that was true of every shape
    /// and specific to none.</summary>
    public static string ShapeClause(LocalizedAssessment a) => a.Shape switch
    {
        LocalizedShape.LooseComplete =>
            "It is flagged LOCALIZED and its text lives in separate .STRINGS files in a Strings folder beside it ("
            + string.Join(", ", a.Languages) + ").",
        LocalizedShape.LoosePartial =>
            "It is flagged LOCALIZED, its text lives in separate .STRINGS files beside it, and "
            + string.Join("; ", a.IncompleteLanguages.Select(kv => $"{kv.Key} is missing its .{string.Join("/.", kv.Value)}"))
            + ".",
        LocalizedShape.LooseWithGameDataDuplicate =>
            "It is flagged LOCALIZED and its text lives in separate .STRINGS files, present BOTH beside the plugin and "
            + "in your game's Data\\Strings folder.",
        LocalizedShape.BsaEmbedded when a.BsaUnreadable =>
            "It is flagged LOCALIZED and the archive " + Path.GetFileName(a.BsaPath) + " " + BsaWhere(a)
            + " could not be read, so houseCARL cannot tell where its text is" + AlsoLoose(a) + ".",
        LocalizedShape.BsaEmbedded =>
            "It is flagged LOCALIZED and its text lives inside " + Path.GetFileName(a.BsaPath) + AlsoLoose(a) + ".",
        LocalizedShape.GameDataOnly =>
            "It is flagged LOCALIZED and its text lives in separate .STRINGS files in your game's Data\\Strings folder, "
            + "not beside the plugin.",
        // The same absence discipline as WhereTheTextIs's arm: houseCARL says it could not FIND them, and where a
        // Strings folder is sitting there with files in it, it says that too rather than claiming the folder is bare.
        LocalizedShape.Nowhere =>
            "It is flagged LOCALIZED and houseCARL cannot find its .STRINGS files: " + NothingMatched(a) + ".",
        LocalizedShape.StringsFolderUnreadable =>
            "It is flagged LOCALIZED and houseCARL could not read the Strings folder beside it, so where its text "
            + "lives is unknown.",
        LocalizedShape.Unreadable =>
            "houseCARL could not read it to see whether it is localized or where its text lives.",
        // Asserts no localization state, for the same reason WhereTheTextIs' does not (Q3, and the render seam).
        _ =>
            "houseCARL has no account of where its text lives.",
    };

    /// <summary>Throw a sentence that is already whole — <see cref="Shaped"/>'s. The ONLY way to build this
    /// exception, and deliberately so: a second, filename-taking constructor built its own refusal text, which went on
    /// asserting the pre-refusal behaviour long after the live sentence had stopped saying it. One behaviour, one
    /// sentence, one home.</summary>
    public static LocalizedTargetUnsupportedException FromSentence(string message) => new(message);

    LocalizedTargetUnsupportedException(string message) : base(message) { }
}

/// <summary>A refusal whose cause is LIVE COLLECTION/RECORD STATE the schema-only pre-flight provably CANNOT see —
/// distinct from a gate/apply <i>inconsistency</i>. This is the whole class of "expected, user-fixable apply rejections":
/// <list type="bullet">
///   <item>a dict <c>Add</c> of an ALREADY-PRESENT key (occupancy — Gap 3 / Package.Data);</item>
///   <item>a list <c>SetAtIndex</c> / <c>InsertAtIndex</c> / <c>Remove</c>-by-index at an OUT-OF-RANGE index (length —
///         pre-flight gates the index shape but leaves the in-range bound to apply, having no live collection; the
///         bound itself is the verb's, and Insert's includes the append slot);</item>
///   <item>a <c>Remove</c> that removes NOTHING — a dict <c>Remove</c> of a key not present, a list
///         <c>Remove</c>-by-value of a value not present, or a <c>Remove</c> on an absent (null) collection
///         (occupancy — Gap 3 / PR #83 follow-up; the symmetric twin of the duplicate-key <c>Add</c> refusal, so a
///         Remove that thought it removed something but didn't surfaces instead of silently succeeding);</item>
///   <item>a mid-path navigation into an ABSENT collection, an ABSENT dict key, or an OUT-OF-BOUNDS list index
///         (<see cref="WriteEngine.StepIntoElement"/>).</item>
/// </list>
/// Each is correctly caught at the boundary it manifests, with a clear, actionable message. The all-or-nothing catch in
/// <see cref="WritePatchBuilder"/> renders this kind's message VERBATIM — still refusing the whole call with no file
/// written (Q3 all-or-nothing holds) — WITHOUT the generic "pre-flight ACCEPTED it but the apply threw — a real
/// inconsistency" wrapper, which would mislabel an expected, fixable user error as an internal bug. Genuinely-unexpected
/// throws (a real gate/apply drift) keep that wrapper. <see cref="WriteEngine.StepIntoElement"/> is shared with the READ
/// path; a read has no inconsistency wrapper, so it simply renders this message exactly as it did when these were plain
/// <see cref="InvalidOperationException"/>s — no read behavior changes. Use this ONLY where the throw is a known,
/// live-state user error with self-explanatory guidance — never to quiet a throw whose cause is unclear, which would
/// re-introduce the silent-failure this project exists to avoid (a bad-SHAPE index throw deliberately stays
/// un-reclassified for that reason; a present-but-null element/entry is its own <see cref="MalformedTargetDataException"/>
/// — a distinct THIRD category — rather than this kind). It is an <see cref="InvalidOperationException"/> so any plain
/// fail-loud handler still catches it.</summary>
public sealed class ExpectedApplyRejectionException : InvalidOperationException
{
    public ExpectedApplyRejectionException(string message) : base(message) { }
}

/// <summary>A refusal whose cause is the TARGET record's own data being malformed/unexpected — a present-but-null
/// collection element or dict entry that <see cref="WriteEngine.StepIntoElement"/> cannot navigate into. This is the
/// THIRD apply-rejection category, deliberately distinct from both siblings: it is NOT an
/// <see cref="ExpectedApplyRejectionException"/> (the user did nothing wrong — there's no input to fix; a null element is
/// a data anomaly, not a routine occupancy/length/presence rejection), and it is NOT a gate/apply <i>inconsistency</i>
/// (the schema-only pre-flight provably cannot see live data, so it is not a "pre-flight ACCEPTED it but apply threw"
/// engine bug). The honest middle: surfaced LOUD and accurately as malformed SOURCE data — houseCARL reads it but never
/// wrote it, so the present-but-null state arises only from pre-existing malformed plugins, never from houseCARL's own
/// write path (the null gates forbid writing one). The all-or-nothing catch in <see cref="WritePatchBuilder"/> renders
/// this kind's message cleanly (no inconsistency wrapper), still refusing the whole call with no file written (Q3).
/// <see cref="WriteEngine.StepIntoElement"/> is shared with the READ path, which has no wrapper, so a read renders this
/// message exactly as it did when these were plain <see cref="InvalidOperationException"/>s — no read behavior change. It
/// is an <see cref="InvalidOperationException"/> so any plain fail-loud handler still catches it.</summary>
public sealed class MalformedTargetDataException : InvalidOperationException
{
    public MalformedTargetDataException(string message) : base(message) { }
}
