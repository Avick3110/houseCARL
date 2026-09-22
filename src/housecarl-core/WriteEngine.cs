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

/// <summary>The reflection-driven write engine: overlay → override → reflect → coerce → set → write with masters, for
/// ANY record group along NESTED paths. Contracts in docs/architecture/write-path.md; pre-flight is the caller's.</summary>
public static class WriteEngine
{
    // Dev harness `patch`: drive one-or-more edits through pre-flight + engine into ONE reviewable .esp, source untouched.
    //   patch --source "<plugin>" --type Armor (--editorid X | --formkey 012E46:Skyrim.esm)
    //        [--path P --verb Set --value V [--key K] | --op "Verb|path|key|value" …] [--name <patch>] [--out <path>]
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

        // Collect the edit(s): repeatable --op "Verb|path|key|value", else the single --path/--verb/--value/--key form.
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
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(source));

        // Resolve the getter interface for the named type — absent => a real coverage gap, surfaced, never guessed.
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
        Console.WriteLine($"Resolved:      {FormIdToken.Of(target.FormKey)} ({target.EditorID ?? "<no editorid>"})");
        foreach (var r in reqs)
            Console.WriteLine($"  before: {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")} = {ReadLeafDisplay(target, r.Path, r.Key)}");
        Console.WriteLine();

        // PRE-FLIGHT every edit: the writes go through the corpus rulebook first; refuse if ANY rejects, no mutation.
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
        var sourceCache = sourceMod.ToImmutableLinkCache();
        var patchMod = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        IMajorRecord patchRecord;
        try { patchRecord = GenericGetOrAddAsOverride(patchMod, target, sourceCache); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not override {type} {FormIdToken.Of(target.FormKey)} — {ex.Message}");
            return 1;
        }
        foreach (var r in reqs) ApplyVerb(patchRecord, r);

        // WRITE with masters. Cross-master records fail LOUD here — named, never a silent wrong patch.
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
        var patchBack = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
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
                else current = StepIntoElement(current, p, segName, segKey); // collection nav — best-effort display
            }
            var (leafName, _) = ParseSegment(path[^1]);
            var leaf = ResolveProperty(current!.GetType(), leafName);
            if (leaf is null) return $"(no field {leafName})";
            var val = leaf.GetValue(current);
            if (val is null) return "(null)";
            static string Fmt(object? o) =>
                o is null ? "(null)" : o is IFormLinkGetter fl ? FormIdToken.Of(fl.FormKey) : o.ToString() ?? "(null)";
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

    // `show` — read-to-plan: print a record's FormKey/EditorID, the requested --path values, and its Keywords as
    //   editorids.  show --source "<plugin>" [--type Weapon] --formkey 0F1AC1:Skyrim.esm [--path BasicStats.Damage]
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

        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(source));
        Type? iface = type is null ? null : typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.I" + type + "Getter");
        if (type is not null && iface is null) { Console.Error.WriteLine($"error: unknown record type '{type}'"); return 1; }
        FormKey? wantFk = null;
        if (formkeyRaw is not null) { try { wantFk = FormKey.Factory(formkeyRaw); } catch (Exception ex) { Console.Error.WriteLine($"error: bad --formkey '{formkeyRaw}': {ex.Message}"); return 1; } }

        var target = sourceMod.EnumerateMajorRecords()
            .FirstOrDefault(r => (iface is null || iface.IsInstanceOfType(r))
                && (wantFk is { } fk ? r.FormKey == fk : string.Equals(r.EditorID, editorid, StringComparison.OrdinalIgnoreCase)));
        if (target is null) { Console.Error.WriteLine($"error: not found in {Path.GetFileName(source)}"); return 1; }

        var typeName = RecordNaming.StripGetterInterface(PrimaryGetter(target.GetType())?.Name ?? "I?Getter");
        Console.WriteLine($"{typeName}  {FormIdToken.Of(target.FormKey)}  ({target.EditorID ?? "<no editorid>"})");
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

    // `condition-patch` — re-target a real FORM-mode condition THROUGH the engine into one reviewable single-master
    //   .esp.  condition-patch --source "<plugin>" [--target XXXXXX:Plugin.esp] [--out <path>] [--name <patch>]
    public static int RunConditionPatch(string[] args)
    {
        var f = ParseFlags(args);
        // --source is required, as it is on the sibling patch/show harnesses — no machine-specific default.
        var source = f.GetValueOrDefault("source");
        if (source is null) { Console.Error.WriteLine("error: missing required --source"); return 1; }
        if (!File.Exists(source)) { Console.Error.WriteLine($"error: source plugin not found: {source}"); return 1; }

        var shaBefore = Sha(source);
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(source));
        var cache = sourceMod.ToImmutableLinkCache();

        // Scan for the first FORM-mode condition target (no aliases, no package data, a populated FormKey).
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

        // New target: --target, else the first differing record of the linked type, else the Player ref.
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
        Console.WriteLine($"Condition:     {ownerCatalog} {FormIdToken.Of(owner.FormKey)} ({owner.EditorID ?? "<no editorid>"}) . {condField}[{condIndex}] . {armCatalog}.{floiProp}");
        Console.WriteLine($"Re-target:     {oldTarget}  ->  {newTarget}   (form mode)");
        Console.WriteLine();

        // PRE-FLIGHT rooted at the arm catalog; refuse if rejected.
        var req = new WriteRequest { RecordType = armCatalog, Path = new[] { floiProp }, Verb = "Set", Value = newTarget };
        var rulebook = CorpusRulebook.Load();
        if (rulebook.Validate(req) is { } reject) { Console.Error.WriteLine($"FAIL: pre-flight rejected the condition-target write: {reject}"); return 1; }
        Console.WriteLine("Pre-flight:    ACCEPT");

        // ENGINE: override the owner, navigate to the mutable arm, drive ApplyVerb -> SetFloi, write with masters.
        var patchMod = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        IMajorRecord ov;
        try { ov = GenericGetOrAddAsOverride(patchMod, owner, cache); }
        catch (Exception ex) { Console.Error.WriteLine($"error: could not override {ownerCatalog} {FormIdToken.Of(owner.FormKey)} — {ex.Message}"); return 1; }
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
        var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
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
            ? $"=== CONDITION PATCH WRITTEN — open {Path.GetFileName(outPath)} in xEdit: {ownerCatalog} {FormIdToken.Of(owner.FormKey)}, condition #{condIndex}, confirm the target is now {newTarget}. Original untouched. ==="
            : "=== FAIL — see above ===");
        return ok ? 0 : 1;
    }

    /// <summary>Yield (condition, owning-field-name, index) for every condition on a record.</summary>
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

    /// <summary>The form-mode FormKey off a live FormLinkOrIndex; null when unset or in index mode.</summary>
    internal static FormKey? ReadFloiFormKey(object? floi)
    {
        if (floi is null) return null;
        var link = floi.GetType().GetProperty("Link")?.GetValue(floi);
        return link is IFormLinkGetter fl && !fl.FormKey.IsNull ? fl.FormKey : (FormKey?)null;
    }

    /// <summary>Navigate a mutable override to the mutable ConditionData arm at field[index].Data — the in-list reference.</summary>
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

    /// <summary>A record of the FLOI's linked type differing from the current target; null if it cannot be sampled.</summary>
    static string? PickSameTypeTarget(ISkyrimModGetter mod, Type? linkedGetter, FormKey current)
    {
        if (linkedGetter is null) return null;
        foreach (var rec in mod.EnumerateMajorRecords())
            if (linkedGetter.IsInstanceOfType(rec) && rec.FormKey != current && !rec.FormKey.IsNull)
                return rec.FormKey.ToString();
        return null;
    }

    // ---- GENERIC PATCH-MOD LIFECYCLE ----

    /// <summary>True iff <paramref name="source"/> is in a NESTED group and so needs the source link cache; the same test as every flat-vs-nested decision.</summary>
    public static bool RecordNeedsSourceCache(IMajorRecordGetter source)
    {
        foreach (var (_, getterIface) in FlatGroupTypes)
            if (getterIface.IsInstanceOfType(source)) return false;
        return true;
    }

    // ---- CHILD-GROUP PRESERVATION ACROSS A DROP-THEN-COPY ----

    /// <summary>The child records a record OWNS, lifted off so a drop-then-copy can put them back; contract in docs/architecture/write-path.md.</summary>
    public readonly record struct ChildGroupCarry(
        IReadOnlyList<(PropertyInfo Prop, object? Value)> Held, int Count, IReadOnlyList<string> Names, bool Captured)
    {
        /// <summary>Nothing was held — the record owns no children. NOT a licence to skip the arrival tripwire.</summary>
        public bool IsEmpty => Count == 0;
    }

    /// <summary>Lift the record's owned child records off it, BEFORE the drop — the live collections, by reference.</summary>
    public static ChildGroupCarry CaptureChildGroup(IMajorRecord record)
    {
        var held = new List<(PropertyInfo, object?)>();
        foreach (var p in ChildBearingProperties(record.GetType()))
            held.Add((p, p.GetValue(record)));
        // Captured: written HERE and nowhere else; neither the carry's shape nor the record's type can stand in for it.
        return new ChildGroupCarry(held, ChildCountOf(record), ChildNamesOf(record, 10), Captured: true);
    }

    /// <summary>The capture, with a throw turned into a refusal naming the destination read rather than the copy.</summary>
    public static string? TryCaptureChildGroup(IMajorRecord record, string untouchedClause, out ChildGroupCarry carry)
    {
        carry = default;
        try { carry = CaptureChildGroup(record); return null; }
        catch (Exception ex)
        {
            return $"cannot forward {FormIdToken.Of(record.FormKey)}: reading the child records under the version the destination " +
                   $"already carries threw ({ex.GetType().Name}: {ex.Message}) — surfaced, not swallowed (Q3). " +
                   untouchedClause;
        }
    }

    /// <summary>Re-attach a carry onto the freshly copied record, AFTER the copy; null on success, else a refusal that
    /// fails the whole call. <paramref name="untouchedClause"/> is the lane's own statement, substituted in.</summary>
    public static string? RestoreChildGroup(IMajorRecord fresh, ChildGroupCarry carry, string untouchedClause)
    {
        // THE ONE GATE: did a capture happen. Deriving it from the carry being empty or the record's type both fail.
        if (!carry.Captured) return null;

        // The walks are INSIDE the try: a fault in Mutagen's containment enumeration is not the override-copy throwing.
        try
        {
            // The copy is expected to arrive EMPTY; if it ever does not, surface it rather than resolve it either way.
            var arrived = ChildCountOf(fresh);
            if (arrived > 0)
            {
                var sample = string.Join(", ", ChildNamesOf(fresh, 5)) + (arrived > 5 ? ", …" : "");
                return $"cannot forward {FormIdToken.Of(fresh.FormKey)}: the forwarded copy arrived carrying {arrived} child record(s) "
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

            // By-construction verification off Mutagen's OWN containment enumeration, not the reflected set that did
            // the re-attach — two independent readings, and it runs even when the carry held nothing to restore.
            var after = ChildCountOf(fresh);
            if (after != carry.Count)
                return $"cannot forward {FormIdToken.Of(fresh.FormKey)}: it carries {carry.Count} child record(s) " +
                       $"({string.Join(", ", carry.Names)}{(carry.Count > carry.Names.Count ? ", …" : "")}) and {after} " +
                       "are on the record after the replace — houseCARL will not write a plugin whose child records it " +
                       $"cannot account for (Q3). {untouchedClause} Please report this with the record type.";
            return null;
        }
        catch (Exception ex)
        {
            return $"cannot preserve the {carry.Count} child record(s) under {FormIdToken.Of(fresh.FormKey)}: carrying them across " +
                   $"the replace threw ({ex.GetType().Name}: {ex.Message}) — surfaced, not swallowed (Q3). " +
                   untouchedClause;
        }
    }

    /// <summary>How many major records are contained UNDER the record, by Mutagen's own containment walk — the yardstick the re-attach is checked against.</summary>
    internal static int ChildCountOf(IMajorRecordGetter record) =>
        record is IMajorRecordGetterEnumerable e ? e.EnumerateMajorRecords().Count() : 0;

    /// <summary>Up to <paramref name="max"/> child records of <paramref name="record"/>, named for a message.</summary>
    internal static List<string> ChildNamesOf(IMajorRecordGetter record, int max) =>
        record is IMajorRecordGetterEnumerable e
            ? e.EnumerateMajorRecords().Take(max).Select(r => r.EditorID ?? FormIdToken.Of(r.FormKey)).ToList()
            : new List<string>();

    /// <summary>The settable properties of <paramref name="t"/> that REACH an owned major record, MEMOIZED; contract in docs/architecture/write-path.md.</summary>
    internal static IReadOnlyList<PropertyInfo> ChildBearingProperties(Type t) => _childProps.GetOrAdd(t, static ty =>
        ty.GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                      && ReachesOwnedRecord(p.PropertyType, new HashSet<Type>(), 0))
          .ToList());
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> _childProps = new();

    /// <summary>Can a value of <paramref name="t"/> hold an owned major record? The recursion and the depth bound are in docs/architecture/write-path.md.</summary>
    static bool ReachesOwnedRecord(Type t, HashSet<Type> seen, int depth)
        => OwnedRecordTypeOf(t, seen, depth, throughInterfaces: false) is not null;

    /// <summary>The record type <paramref name="t"/> reaches, or null — the same walk, for the read side, stepping through INTERFACES too.</summary>
    internal static Type? OwnedRecordTypeOf(Type t) => OwnedRecordTypeOf(t, new HashSet<Type>(), 0, throughInterfaces: true);

    static Type? OwnedRecordTypeOf(Type t, HashSet<Type> seen, int depth, bool throughInterfaces)
    {
        if (depth > 6 || !seen.Add(t)) return null;
        // `seen` is a PATH set, not a memo, so a type first reached at the depth bound is never recorded unreachable.
        try
        {
            if (typeof(IFormLinkGetter).IsAssignableFrom(t)) return null;    // a reference, not a child
            if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) return t;
            if (ElementTypeOf(t) is { } elem) return OwnedRecordTypeOf(elem, seen, depth + 1, throughInterfaces);
            if (!t.IsClass && !(throughInterfaces && t.IsInterface)) return null;
            if (t.Namespace?.StartsWith("Mutagen", StringComparison.Ordinal) != true) return null;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (OwnedRecordTypeOf(p.PropertyType, seen, depth + 1, throughInterfaces) is { } found) return found;
            return null;
        }
        finally { seen.Remove(t); }
    }

    /// <summary>The element type <paramref name="t"/> enumerates, or null; <c>string</c> is excluded explicitly.</summary>
    internal static Type? ElementTypeOf(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        if (t == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return null;
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }

    /// <summary>The Type for Mutagen's typed <c>Remove</c>: the flat GROUP's <c>T</c> when one matches, else the runtime type — a subclass of an abstract T no-ops.</summary>
    public static Type RemovalTypeFor(IMajorRecordGetter record)
    {
        foreach (var (tMajor, getterIface) in FlatGroupTypes)
            if (getterIface.IsInstanceOfType(record)) return tMajor;
        return record.GetType();
    }

    /// <summary>The Type for Mutagen's typed ENUMERATION: the flat group's getter interface when one matches, else the record's own primary getter.</summary>
    public static Type SeekTypeFor(IMajorRecordGetter record)
    {
        foreach (var (_, getterIface) in FlatGroupTypes)
            if (getterIface.IsInstanceOfType(record)) return getterIface;
        return PrimaryGetter(record.GetType()) ?? record.GetType();
    }

    /// <summary>The flat groups' (T, getter-interface) pairs for <see cref="SkyrimMod"/>, MEMOIZED reflection metadata.</summary>
    static IReadOnlyList<(Type tMajor, Type getterIface)> FlatGroupTypes => _flatGroupTypes.Value;
    static readonly Lazy<IReadOnlyList<(Type tMajor, Type getterIface)>> _flatGroupTypes =
        new(() => EnumerateFlatGroups(typeof(SkyrimMod)).Select(g => (g.tMajor, g.getterIface)).ToList());

    /// <summary>Generic <c>GetOrAddAsOverride</c>: a flat-group record through its <c>SkyrimGroup&lt;T&gt;</c>, anything else through the nested path.</summary>
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

    /// <summary>Find the <c>SkyrimGroup&lt;T&gt;</c> whose T carries the record's getter interface; false IS the nested test.</summary>
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

    /// <summary>Resolve an override for a NESTED-group record: its CONTEXT by FormKey off the source cache, which rebuilds the parent chain in the patch.</summary>
    static IMajorRecord NestedGetOrAddAsOverride(SkyrimMod patchMod, IMajorRecordGetter source, ILinkCache? sourceLinkCache)
    {
        if (sourceLinkCache is null)
            throw new InvalidOperationException(
                $"Record {FormIdToken.Of(source.FormKey)} ({source.GetType().Name}) is in a nested group (no flat SkyrimGroup<T>) " +
                "and needs the source link cache to reconstruct its parent chain — pass sourceLinkCache " +
                "(sourceMod.ToImmutableLinkCache()) to GenericGetOrAddAsOverride. Surfaced, not guessed (Q3).");

        var getterIface = PrimaryGetter(source.GetType())
            ?? throw new InvalidOperationException($"No getter interface for nested record {source.GetType().Name}.");
        var setterName = RecordNaming.GetterToSetterInterface(getterIface.Name);   // INpcGetter -> INpc (the settable interface, NOT the catalog name)
        var setterIface = getterIface.Assembly.GetType((getterIface.Namespace ?? "Mutagen.Bethesda.Skyrim") + "." + setterName)
            ?? throw new InvalidOperationException($"No setter interface {setterName} for nested record {getterIface.Name}.");

        // ResolveContext<TSetter,TGetter>(FormKey, [ResolveTarget]) off the live cache; the trailing arg is immaterial.
        var resolveCtx = sourceLinkCache.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ResolveContext" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2 && m.GetParameters().Length >= 1
                && m.GetParameters()[0].ParameterType == typeof(FormKey))
            ?? throw new InvalidOperationException("No ResolveContext<TSetter,TGetter>(FormKey,...) on the source link cache.");

        // Mutagen's non-Try ResolveContext THROWS on a miss, so unwrap into a FormKey-named fail-closed message.
        object? ctx;
        try
        {
            ctx = resolveCtx.MakeGenericMethod(setterIface, getterIface)
                      .Invoke(sourceLinkCache, BuildResolveArgs(resolveCtx, source.FormKey));
        }
        catch (TargetInvocationException tie)
        {
            throw new InvalidOperationException(
                $"Nested record {FormIdToken.Of(source.FormKey)} ({source.GetType().Name}) not found in the source cache — " +
                "cannot reconstruct its parent chain (fail-closed, Q3).", tie.InnerException ?? tie);
        }
        if (ctx is null)
            throw new InvalidOperationException(
                $"ResolveContext returned null for nested record {FormIdToken.Of(source.FormKey)} — not found in the source cache.");

        var goao = ctx.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetOrAddAsOverride" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.IsInstanceOfType(patchMod))
            ?? throw new InvalidOperationException($"Nested context for {FormIdToken.Of(source.FormKey)} has no GetOrAddAsOverride(mod).");

        return (IMajorRecord)goao.Invoke(ctx, new object[] { patchMod })!;
    }

    /// <summary>Args for <c>ResolveContext&lt;T,TG&gt;</c>: the FormKey, then any trailing parameter by its default.</summary>
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

    /// <summary>The single source of truth for which records live in a flat <c>SkyrimGroup&lt;T&gt;</c>; a nested-group record is a surfaced gap.</summary>
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

    // ---- CREATE front-end ----
    //  The sibling of GenericGetOrAddAsOverride, allocating a brand-new record off the SAME EnumerateFlatGroups
    //  enumeration; an ABSTRACT-typed group takes its own arm branch. Contracts in docs/architecture/write-path.md.

    /// <summary>Every flat group whose T is ABSTRACT, with its concrete-arm Types, discovered off the same enumeration and the runtime hierarchy. MEMOIZED.</summary>
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

    /// <summary>If <paramref name="typeName"/> is the CONCRETE arm of an abstract group, resolve the live group + the arm's Type; null patch ⇒ the Type alone.</summary>
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

    /// <summary>Can a brand-new record of <paramref name="typeName"/> be created? True for a concrete flat T or a concrete ARM; each false names a boundary.</summary>
    public static bool CanCreateType(string typeName, out string? reason)
    {
        // A concrete arm is checked FIRST, so it always wins over the abstract-base refusal below.
        if (TryResolveAbstractGroupArm(null, typeName, out _, out _)) { reason = null; return true; }

        foreach (var (_, tm, _) in EnumerateAbstractGroups())
            if (string.Equals(tm.Name, typeName, StringComparison.OrdinalIgnoreCase))
            {
                // The bare abstract base: name which concrete arm, discovered rather than hand-listed.
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
                 "in this record's " + ToolNames.Create + " records= element pass parent= (the parent record's FormID, or the editorid of a " +
                 "record declared EARLIER in the same records= array) and, if the parent holds more than one child-collection, " +
                 "collection= there too. That same array is how a parent AND its children go in ONE call (a dialogue topic + its " +
                 "lines, a cell + its placed refs): declare the parent first, then the children whose parent= names its editorid. " +
                 "(A CELL is coordinate-keyed: an EXTERIOR cell is parent=<Worldspace FormID> + grid=<X,Y> in its element, an " +
                 "INTERIOR cell has neither.) If instead it's a subtype " +
                 "of an abstract group (like Global → GlobalFloat), create the concrete subtype. Flat top-level records — keywords, " +
                 "spells, perks, magic effects, factions, armor, weapons, leveled lists, … — create fine.";
        return false;
    }

    /// <summary>The CREATE front-end: allocate a brand-new record of <paramref name="typeName"/>, fed into the SAME <see cref="ApplyVerb"/> path.</summary>
    public static IMajorRecord GenericAddNew(SkyrimMod patchMod, string typeName, string? editorId)
    {
        if (!CanCreateType(typeName, out var reason)) throw new InvalidOperationException(reason);
        EnsureFormIdFloor(patchMod);   // a counter rehydrated below 0x800 would hand AddNew engine-reserved IDs
        EnsureAllocatable(patchMod);   // …and a counter past the 24-bit object-ID ceiling can't allocate — fail loud

        // Abstract-group arm: construct the concrete arm and Add(T) it — the abstract T cannot close InvokeAddNew.
        if (TryResolveAbstractGroupArm(patchMod, typeName, out var armGroup, out var armType))
            return AddConcreteArmToGroup(armGroup!, armType!, AllocateNextFormKey(patchMod), editorId);

        object? group = null; Type? tMajor = null;
        foreach (var (prop, tm, _) in EnumerateFlatGroups(patchMod.GetType()))
            if (string.Equals(tm.Name, typeName, StringComparison.OrdinalIgnoreCase)) { group = prop.GetValue(patchMod); tMajor = tm; break; }
        return InvokeAddNew(group!, tMajor!, editorId);   // CanCreateType guaranteed a concrete flat group exists
    }

    /// <summary>Construct a concrete abstract-group arm and add it through the group's own <c>Add(T)</c> — not an IList.</summary>
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

    /// <summary>UPSERT front-end for the extend path: a record the patch ITSELF defines is REPLACED at the same FormKey, three collisions refused. See the note.</summary>
    public static (IMajorRecord Record, bool Replaced) GenericUpsertNew(SkyrimMod patchMod, string typeName, string? editorId)
    {
        if (editorId is not null)
        {
            var matches = patchMod.EnumerateMajorRecords()
                .Where(r => string.Equals(r.EditorID, editorId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
            {
                // Replace-eligibility: ONLY a record this patch itself defines; a carried override keeps a foreign master.
                var foreign = matches.FirstOrDefault(r => r.FormKey.ModKey != patchMod.ModKey);
                if (foreign is not null)
                    throw new InvalidOperationException(
                        $"create refused: this patch already carries an OVERRIDE of {FormIdToken.Of(foreign.FormKey)} whose editorid is '{editorId}' — " +
                        $"re-creating over an override would blank the original plugin's record. Pick a different editorid for the new record, " +
                        "or edit the override's fields with " + ToolNames.Apply + ".");
                if (matches.Count > 1)
                    throw new InvalidOperationException(
                        $"create refused: this patch carries {matches.Count} records with editorid '{editorId}' " +
                        $"({string.Join(", ", matches.Select(m => FormIdToken.Of(m.FormKey)))}) — duplicate residue from re-running creates on a pre-fix houseCARL. " +
                        "External references may point at either copy, so which survives is your call: remove the extra(s) with " + ToolNames.Remove + ", then re-run.");
                var existing = matches[0];
                if (!CanCreateType(typeName, out var reason)) throw new InvalidOperationException(reason);
                var formKey = existing.FormKey;
                // The group is resolved FIRST, so a group that does not resolve is not reported as a type mismatch.
                var isArm = TryResolveAbstractGroupArm(patchMod, typeName, out var armGroup, out var armType);
                object? group = null; Type? tMajor = null;
                if (!isArm && !TryResolveFlatGroup(patchMod, typeName, out group, out tMajor))
                    throw new InvalidOperationException($"upsert: no flat group found for type '{typeName}'.");
                // The cross-type guard compares CONCRETE types, for an abstract-group ARM too.
                if (!UpsertWouldReplace(patchMod, typeName, matches))
                    throw new InvalidOperationException(
                        $"upsert refused: existing record '{editorId}' ({FormIdToken.Of(formKey)}) is a {existing.GetType().Name}, not a {typeName} — " +
                        "an EditorID collision across record types is a real authoring error, surfaced not swallowed (Q3).");

                // An arm re-adds through Add(T), not the abstract-base InvokeAddNewWithFormKey (which can't close AddNew<T>).
                if (isArm)
                {
                    InvokeRemove(armGroup!, formKey);
                    var freshArm = AddConcreteArmToGroup(armGroup!, armType!, formKey, editorId);
                    return (freshArm, true);
                }

                InvokeRemove(group!, formKey);
                var fresh = InvokeAddNewWithFormKey(group!, tMajor!, formKey);
                fresh.EditorID = editorId;
                return (fresh, true);
            }
        }
        return (GenericAddNew(patchMod, typeName, editorId), false);
    }

    /// <summary>Would <see cref="GenericUpsertNew"/> REPLACE what <paramref name="matches"/> holds, or refuse? False for each refusal, and for no group at all.</summary>
    public static bool UpsertWouldReplace(SkyrimMod patchMod, string typeName, IReadOnlyList<IMajorRecord> matches)
        => matches.Count == 1
           && matches[0].FormKey.ModKey == patchMod.ModKey
           && (TryResolveAbstractGroupArm(patchMod, typeName, out _, out var arm)
                   ? arm!.IsInstanceOfType(matches[0])
                   : TryResolveFlatGroup(patchMod, typeName, out _, out var tMajor) && tMajor!.IsInstanceOfType(matches[0]));

    /// <summary>The live flat <c>SkyrimGroup&lt;T&gt;</c> a record type is stored in, plus its T.</summary>
    static bool TryResolveFlatGroup(SkyrimMod patchMod, string typeName, out object? group, out Type? tMajor)
    {
        foreach (var (prop, tm, _) in EnumerateFlatGroups(patchMod.GetType()))
            if (string.Equals(tm.Name, typeName, StringComparison.OrdinalIgnoreCase))
            { group = prop.GetValue(patchMod); tMajor = tm; return true; }
        group = null; tMajor = null; return false;
    }

    /// <summary>Remove a record from a flat group by FormKey — instance method, else the extension scan; fails loud.</summary>
    static void InvokeRemove(object group, FormKey formKey)
    {
        var instance = group.GetType().GetMethod("Remove", new[] { typeof(FormKey) });
        if (instance is not null)
        {
            var result = instance.Invoke(group, new object[] { formKey });
            if (result is false)
                throw new InvalidOperationException(
                    $"Remove({FormIdToken.Of(formKey)}) reported nothing removed — the matched record vanished between match and replace " +
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

    /// <summary>The FormKey-preserving sibling of <see cref="InvokeAddNew"/>, so a replace consumes no new id.</summary>
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

    /// <summary>Raise the patch's allocator counter to its floor — 0x800, and past every record it defines, never lower. Floor contract in the note.</summary>
    public static void EnsureFormIdFloor(SkyrimMod patchMod)
    {
        uint floor = FormIdRange.EngineReservedFloor;
        foreach (var r in patchMod.EnumerateMajorRecords())
            if (r.FormKey.ModKey == patchMod.ModKey && r.FormKey.ID >= floor)
                floor = r.FormKey.ID + 1;
        if (patchMod.ModHeader.Stats.NextFormID < floor)
            patchMod.ModHeader.Stats.NextFormID = floor;
    }

    /// <summary>Guard that the patch can still allocate: the counter must be inside the 24-bit object-ID space. LOUD at the allocation boundary, not at the floor.</summary>
    static void EnsureAllocatable(SkyrimMod patchMod)
    {
        if (FormIdRange.ObjectIdSpaceExhausted(patchMod.ModHeader.Stats.NextFormID))
            throw new InvalidOperationException(
                $"cannot allocate a new FormID: the patch's NextObjectID counter is 0x{patchMod.ModHeader.Stats.NextFormID:X} — " +
                $"past the 24-bit object-ID ceiling (0x{FormIdRange.ObjectIdMax:X}). The plugin is full or its header counter is corrupt.");
    }

    /// <summary>Invoke Mutagen's <c>AddNew</c> on a flat group: a GENERIC EXTENSION, located like <see cref="OverrideMethod"/>, closed with the group's T.</summary>
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

    // ---- NESTED CREATE front-end ----
    //  Allocate a brand-new child INTO a parent's modeled child-collection, the add-target found REFLECTIVELY. The
    //  parent must already be settable in the patch; coordinate-keyed parents belong to the next section.

    /// <summary>Resolve a catalog name to its concrete Mutagen record <see cref="Type"/>; null ⇒ a coverage gap.</summary>
    public static Type? ResolveConcreteRecordType(string catalogName)
        => typeof(IArmorGetter).Assembly.GetType("Mutagen.Bethesda.Skyrim." + catalogName);

    /// <summary>Can a new <paramref name="childCatalogName"/> be created under <paramref name="parentType"/>, into <paramref name="collectionName"/> (null = the unique fitting slot)?</summary>
    public static bool CanCreateNested(string childCatalogName, Type parentType, string? collectionName, out string? reason)
        => TryResolveChildSlot(childCatalogName, parentType, collectionName, out _, out _, out reason);

    /// <summary>The same question, answering WHICH slot resolved and what SHAPE it has — pre-flight needs both.</summary>
    public static bool TryResolveChildSlot(string childCatalogName, Type parentType, string? collectionName,
        out string? slotName, out OwnedChildShape shape, out string? reason)
    {
        slotName = null; shape = OwnedChildShape.None;
        var childType = ResolveConcreteRecordType(childCatalogName);
        if (childType is null)
        {
            reason = $"'{childCatalogName}' is absent from the Mutagen corpus — a real coverage gap to surface, not a value to guess.";
            return false;
        }
        if (!TryFindChildSlot(parentType, childType, collectionName, out var prop, out shape, out _, out reason)) return false;
        slotName = prop!.Name;
        return true;
    }

    /// <summary>Find the parent's SETTABLE child slot for a new <paramref name="childType"/>, over both shapes — a fitting COLLECTION, or a SINGULAR slot.</summary>
    static bool TryFindChildSlot(Type parentType, Type childType, string? collectionName,
        out PropertyInfo? prop, out OwnedChildShape shape, out List<string> matches, out string? error)
    {
        prop = null; error = null; shape = OwnedChildShape.None;
        matches = new List<string>();
        var hits = new List<(PropertyInfo Prop, OwnedChildShape Shape)>();
        foreach (var p in parentType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetGetMethod() is null) continue;                          // need a readable list instance to Add to
            var elem = ListElementType(p.PropertyType);
            if (elem is not null)
            {
                if (!elem.IsAssignableFrom(childType)) continue;             // the child fits this list's element type
                if (!typeof(System.Collections.IList).IsAssignableFrom(p.PropertyType)) continue;  // addable (ExtendedList<T> is an IList)
                hits.Add((p, OwnedChildShape.Collection)); matches.Add(p.Name);
                continue;
            }
            // The SINGULAR half: a SETTABLE property holding one owned child record the child type satisfies.
            if (!p.CanWrite || p.GetIndexParameters().Length != 0) continue;
            if (!typeof(IMajorRecordGetter).IsAssignableFrom(p.PropertyType)) continue;
            if (!p.PropertyType.IsAssignableFrom(childType)) continue;
            hits.Add((p, OwnedChildShape.Singular)); matches.Add(p.Name);
        }
        var parentName = parentType.Name;   // concrete class name, already clean (never an I…Getter here)
        var childName = childType.Name;
        // A parent may ALSO file this child by COORDINATE, in a block tree no slot name reaches — derived from the child-bearing set.
        bool coordinateRoute = ReachesChildThroughContainers(parentType, childType);
        if (hits.Count == 0)
        {
            error = $"'{childName}' cannot be created under a '{parentName}' — that parent models no child slot that " +
                    "holds it (a real containment boundary, surfaced not guessed, Q3)." +
                    (coordinateRoute
                        ? $" A {childName} is filed by coordinate under a {parentName}, not held in a slot: create it with parent=<{parentName}> + grid=<X,Y>."
                        : childType == typeof(Cell)
                            ? " A Cell is coordinate-keyed, not collection-nested: create an EXTERIOR cell with parent=<Worldspace> + grid=<X,Y>, or an INTERIOR cell with no parent."
                            : "");
            return false;
        }
        if (collectionName is not null)
        {
            var named = hits.Where(h => string.Equals(h.Prop.Name, collectionName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
            {
                error = $"'{parentName}' has no child slot named '{collectionName}' that holds '{childName}'. Available: {string.Join(", ", matches)}"
                      + (coordinateRoute ? " (or grid=<X,Y>, which files it by coordinate rather than in a slot)" : "") + ".";
                return false;
            }
            (prop, shape) = named[0]; return true;
        }
        // More than one route and no discriminator: name them all and refuse. The coordinate route counts as a route
        // even when one slot matches, so a forgotten grid= never silently builds the wrong thing.
        if (hits.Count > 1 || coordinateRoute)
        {
            var routes = matches.Select(m => $"collection={m}").ToList();
            if (coordinateRoute) routes.Add("grid=<X,Y> for one filed by coordinate in its block tree");
            error = $"'{childName}' can go under a '{parentName}' more than one way ({string.Join(", ", routes)}) — " +
                    "name which one. (Q3 — never a silent default.)";
            return false;
        }
        (prop, shape) = hits[0]; return true;
    }

    /// <summary>Does this parent reach <paramref name="childType"/> through CONTAINERS rather than a nameable slot? The walk stops at any record type.</summary>
    static bool ReachesChildThroughContainers(Type parentType, Type childType) =>
        ChildBearingProperties(parentType).Any(p =>
            !typeof(IMajorRecordGetter).IsAssignableFrom(p.PropertyType)      // a singular slot, not a container route
            && ReachesRecordType(p.PropertyType, childType, new HashSet<Type>(), 0)
            && !(ListElementType(p.PropertyType) is { } e && e.IsAssignableFrom(childType)));   // a nameable list slot

    /// <summary>Can a value of <paramref name="t"/> hold a <paramref name="childType"/> record? The same walk, asked of ONE target type.</summary>
    static bool ReachesRecordType(Type t, Type childType, HashSet<Type> seen, int depth)
    {
        if (depth > 6 || !seen.Add(t)) return false;
        try
        {
            if (typeof(IFormLinkGetter).IsAssignableFrom(t)) return false;
            if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) return t.IsAssignableFrom(childType);
            if (ElementTypeOf(t) is { } elem) return ReachesRecordType(elem, childType, seen, depth + 1);
            if (!t.IsClass || t.Namespace?.StartsWith("Mutagen", StringComparison.Ordinal) != true) return false;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (ReachesRecordType(p.PropertyType, childType, seen, depth + 1)) return true;
            return false;
        }
        finally { seen.Remove(t); }
    }

    /// <summary>The element type of an <c>IList&lt;T&gt;</c>-shaped property, else null.</summary>
    static Type? ListElementType(Type t)
    {
        if (t == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return null;
        if (t.IsGenericType && t.GetGenericArguments() is { Length: 1 } ga) return ga[0];
        return t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
                ?.GetGenericArguments()[0];
    }

    /// <summary>Construct a concrete record via its discovered <c>(FormKey, release)</c> ctor; loud if absent.</summary>
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

    /// <summary>The next LOCAL FormKey off the patch's own allocator; the caller floors the counter first.</summary>
    static FormKey AllocateNextFormKey(SkyrimMod patchMod)
    {
        var m = patchMod.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(x => x.Name == "GetNextFormKey" && x.GetParameters().Length == 0)
            ?? throw new InvalidOperationException("nested create: no GetNextFormKey() on the patch mod — surfaced, not guessed (Q3).");
        return (FormKey)m.Invoke(patchMod, null)!;
    }

    /// <summary>The CREATE front-end for a NESTED record: a new child into the parent's modeled collection, the parent already settable in the patch.</summary>
    public static IMajorRecord NestedAddNew(SkyrimMod patchMod, IMajorRecord parentInPatch,
        string childCatalogName, string? collectionName, string? editorId)
    {
        var childType = ResolveConcreteRecordType(childCatalogName)
            ?? throw new InvalidOperationException($"nested create: '{childCatalogName}' is absent from the Mutagen corpus.");
        if (!TryFindChildSlot(parentInPatch.GetType(), childType, collectionName, out var prop, out var shape, out _, out var error))
            throw new InvalidOperationException(error);
        var parentName = parentInPatch.GetType().Name;

        // A SINGULAR slot holds exactly one child, so an occupied one is refused BEFORE anything is allocated. The
        // BACKSTOP: pre-flight asks the same question of the parent's real body, this one of the copy being written.
        if (shape == OwnedChildShape.Singular && prop!.GetValue(parentInPatch) is IMajorRecordGetter occupant)
            throw new InvalidOperationException(
                $"nested create: '{parentName}.{prop.Name}' already holds a {childType.Name} ({FormIdToken.Of(occupant.FormKey)}" +
                (occupant.EditorID is { } oe ? $" editorid={oe}" : "") + ") and it holds exactly one, so there is no " +
                "room to create another. Edit the one that is there by its own FormID, or remove it first with " +
                ToolNames.Remove + " and create again.");

        // A SINGULAR slot does not upsert and its child is reachable by another route, so it runs the same dedup.
        if (shape == OwnedChildShape.Singular) EnsureNoDuplicateEditorId(patchMod, childType, editorId);

        EnsureFormIdFloor(patchMod);   // a rehydrated (into=) counter below 0x800 would hand out engine-reserved IDs
        EnsureAllocatable(patchMod);
        var child = ConstructRecord(childType, AllocateNextFormKey(patchMod));
        if (editorId is not null) child.EditorID = editorId;
        if (shape == OwnedChildShape.Singular) { prop!.SetValue(parentInPatch, child); return child; }
        if (prop!.GetValue(parentInPatch) is not System.Collections.IList list)
            throw new InvalidOperationException($"nested create: collection '{prop.Name}' on '{parentName}' is not an addable list.");
        list.Add(child);
        return child;
    }

    // ---- COORDINATE-KEYED CREATE ----
    //  Cells, whose structural parents are FormKey-LESS block structs: exterior block=floor(grid/32)
    //  subblock=floor(grid/8), interior block=id%10 subblock=(id/10)%10. A created cell is a structural shell.

    /// <summary>Signed integer floor division — the block index from a possibly negative grid coordinate.</summary>
    internal static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>CREATE an EXTERIOR cell at (gridX,gridY): find-or-construct the block + subblock structs, set the Grid, allocate off the same counter.</summary>
    public static Cell AddExteriorCell(SkyrimMod patchMod, Worldspace worldspaceInPatch, int gridX, int gridY, string? editorId)
    {
        // Floor + dedup BEFORE mutating the block tree — nothing is mutated before validation.
        EnsureNoDuplicateEditorId(patchMod, typeof(Cell), editorId);   // no silent duplicate on an into= re-run (cells have a stable EditorID)
        EnsureFormIdFloor(patchMod);   // 0x800 floor, exactly like flat/nested create
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

    /// <summary>CREATE an INTERIOR cell — filed by the cell's OWN FormID digits, so the FormKey is allocated FIRST. <c>IsInteriorCell</c> ON.</summary>
    public static Cell AddInteriorCell(SkyrimMod patchMod, string? editorId)
    {
        EnsureNoDuplicateEditorId(patchMod, typeof(Cell), editorId);   // no silent duplicate on an into= re-run (cells have a stable EditorID)
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

    /// <summary>Refuse loud if the patch already carries that <paramref name="recordType"/> at <paramref name="editorId"/>: neither of these routes upserts.</summary>
    static void EnsureNoDuplicateEditorId(SkyrimMod patchMod, Type recordType, string? editorId)
    {
        if (string.IsNullOrEmpty(editorId)) return;
        foreach (var existing in patchMod.EnumerateMajorRecords())
            if (recordType.IsInstanceOfType(existing) && string.Equals(existing.EditorID, editorId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"a {recordType.Name} with editorid '{editorId}' already exists in this patch ({FormIdToken.Of(existing.FormKey)}); creating " +
                    $"it again would duplicate it. {recordType.Name} create does not upsert here — edit the existing record, or " +
                    "use a different editorid.");
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

    /// <summary>Ties output filename to ModKey; the single-known-master case, delegating to the overload below.</summary>
    internal static void WritePatch(SkyrimMod patchMod, ISkyrimModGetter sourceMod, string outputPath)
        => WritePatch(patchMod, new[] { sourceMod }, outputPath);

    /// <summary>Multi-master WritePatch: the FULL known-master set goes to the serializer so cross-plugin references
    /// resolve, while Mutagen keeps the header list lean; a referenced master absent from it still fails loud.</summary>
    internal static void WritePatch(SkyrimMod patchMod, IReadOnlyList<ISkyrimModGetter> knownMasters, string outputPath)
    {
        var expected = patchMod.ModKey.FileName.String;
        var actual = Path.GetFileName(outputPath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Output filename '{actual}' must match patch ModKey filename '{expected}'.");

        // A LOCALIZED output, which the extend lane reaches through an existing localized plugin: refused here,
        // before the staging directory exists, the way the in-place lane refuses every localized target.
        if (patchMod.UsingLocalization)
            throw LocalizedTargetUnsupportedException.FromSentence(
                $"houseCARL did not write '{expected}' — the file is unchanged and nothing was staged. It is flagged "
                + "LOCALIZED, so its text lives in separate .STRINGS files that houseCARL cannot write as one set with "
                + "the plugin. Write these edits into a fresh patch instead (drop into=), which carries its text inside "
                + "the plugin.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // Hand Mutagen the FULL load order so it can resolve + ORDER every referenced master; the header LIST stays
        // lean, derived from the records' own links. BASELINE MASTERS are force-included on top, idempotently, and
        // FILTERED to the ones the order carries, so a degenerate order cannot throw on an unresolvable extra.
        var ordered = knownMasters as ISkyrimModGetter[] ?? knownMasters.ToArray();
        var baseline = BaselineMasters.Where(bm => ordered.Any(km => km.ModKey == bm)).ToArray();
        // FORMID FLOOR: NoNextFormIDProcessing persists our in-memory counter verbatim instead of re-deriving it by
        // iteration, and EnsureFormIdFloor is what makes that counter conventional. Every product write comes here.
        EnsureFormIdFloor(patchMod);
        // ATOMIC WRITE: stage into a sibling temp and commit, so the target only ever holds the OLD or the NEW
        // complete file; it does not relax the handle discipline before the serialize.
        // SERIALIZE-BOUNDARY NULL-ARM CATCH: a composed record missing a REQUIRED sub-arm is a bare, field-nameless
        // NRE in Mutagen's writer that pre-flight has no by-construction signal to gate on, so only that is named.
        string staged;
        try { staged = WritePatchStaged(patchMod, ordered, baseline, outputPath); }
        catch (Exception ex) when (RootNullArm(ex) is { } nre) { throw new NullArmSerializeException(nre); }
        CommitStagedPatch(staged, outputPath);
    }

    /// <summary>Render an exception as <c>Type: message</c>, appending the inner's, so a re-stamp keeps its signal.</summary>
    internal static string Describe(Exception ex)
        => ex.InnerException is { } inner
            ? $"{ex.GetType().Name}: {ex.Message} [inner: {inner.GetType().Name}: {inner.Message}]"
            : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>Unwrap a serialize throw to the root NRE a composed null arm makes, or null unless EVERY flattened leaf's root is one.</summary>
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

    /// <summary>Stage 1 of the atomic write: serialize into a temp SUBDIRECTORY beside the target, so the stage-2 rename is atomic; temp cleaned on failure.</summary>
    static string WritePatchStaged(SkyrimMod patchMod, ISkyrimModGetter[] ordered, ModKey[] baseline, string outputPath)
    {
        var tmpDir = Path.Combine(Path.GetDirectoryName(outputPath)!, ".housecarl-tmp");
        var tmpPath = Path.Combine(tmpDir, Path.GetFileName(outputPath));
        Directory.CreateDirectory(tmpDir);

        void Serialize(Mutagen.Bethesda.Plugins.Binary.Streams.EncodingBundle encodings) =>
            patchMod.BeginWrite
                .ToPath(tmpPath)
                .WithLoadOrder(ordered)
                .WithExtraIncludedMasters(baseline)
                .NoNextFormIDProcessing()
                .WithEmbeddedEncodings(encodings)
                .Write();

        try
        {
            if (PluginTextEncoding.NewFileIsUtf8(patchMod)) Serialize(PluginTextEncoding.Utf8Bundle);
            else
                // The strict encoder is the CHECK: a value the language default cannot spell stops the write rather
                // than landing as '?'. A new file has no bytes to preserve, so the whole thing goes out as UTF-8.
                try { Serialize(PluginTextEncoding.LegacyBundle); }
                catch (Exception ex) when (PluginTextEncoding.RootUnspellable(ex) is not null)
                {
                    CleanupStaged(tmpPath);
                    Directory.CreateDirectory(tmpDir);
                    Serialize(PluginTextEncoding.Utf8Bundle);
                }
            return tmpPath;
        }
        catch
        {
            CleanupStaged(tmpPath);
            throw;
        }
    }

    /// <summary>Stage 2 of the atomic write: swap the staged temp over the target — File.Replace when it exists, a rename when it does not.</summary>
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

    /// <summary>Serialize a plugin edited IN PLACE back over ITSELF. DELIBERATELY NOT <see cref="WritePatch"/>: no
    /// baseline force-include and no floor, because this re-emits an EXISTING authored plugin. The caller MUST have
    /// released every overlay it holds on the target; see docs/architecture/write-path.md.</summary>
    public static void WriteInPlace(SkyrimMod targetMod, IReadOnlyList<ISkyrimModGetter> ownMasters, string outputPath,
                                    string? dataDir)
    {
        var expected = targetMod.ModKey.FileName.String;
        var actual = Path.GetFileName(outputPath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"In-place output filename '{actual}' must match the target's ModKey filename '{expected}'.");

        // The localized-target choke point, the backstop for every caller: it runs BEFORE the staging directory
        // exists, and THE OUTCOME IS DECIDED OFF targetMod.UsingLocalization AND NOTHING ELSE, so it cannot fail to
        // fire where a destination re-read could. The re-read below supplies only the SENTENCE. Every shape refuses.
        if (targetMod.UsingLocalization)
            throw LocalizedTargetUnsupportedException.FromSentence(
                LocalizedTargetUnsupportedException.Shaped(expected, LocalizedStrings.Assess(outputPath, dataDir)));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var ordered = ownMasters as ISkyrimModGetter[] ?? ownMasters.ToArray();
        // Same composed-null-arm serialize guard as WritePatch — an in-place edit can compose a record too.
        string staged;
        try { staged = WriteInPlaceStaged(targetMod, ordered, outputPath); }
        // A value this file's own encoding cannot spell: in place there is no second pass, so this is a refusal.
        catch (Exception ex) when (PluginTextEncoding.RootUnspellable(ex) is { } bad)
        {
            throw new UnspellableTextException(
                PluginTextEncoding.UnspellableRefusal(bad, Path.GetFileName(outputPath)), bad);
        }
        catch (Exception ex) when (RootNullArm(ex) is { } nre) { throw new NullArmSerializeException(nre); }
        // A localized target never reaches here, so the serialize emitted no tables and this is a single-file swap.
        CommitStagedPatch(staged, outputPath);
    }

    /// <summary>Is the plugin at <paramref name="path"/> flagged LOCALIZED — three answers, not a bool, which would
    /// answer false on a read fault. HEADER-ONLY and without the resolver's game-Data strings redirect, so
    /// <c>Unreadable</c> means the file would not open, never that its tables were not found.</summary>
    public static LocalizedFlagRead PluginIsLocalized(string path)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path));
            return ov.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Localized)
                ? LocalizedFlagRead.Localized
                : LocalizedFlagRead.NotLocalized;
        }
        catch { return LocalizedFlagRead.Unreadable; }
        finally { if (ov is IDisposable d) { try { d.Dispose(); } catch { } } }
    }

    /// <summary>Stage 1 of the in-place write: own declared masters, the counter verbatim with NO floor, no baseline, staged in the sibling temp.</summary>
    static string WriteInPlaceStaged(SkyrimMod targetMod, ISkyrimModGetter[] ordered, string outputPath)
    {
        var tmpDir = Path.Combine(Path.GetDirectoryName(outputPath)!, ".housecarl-tmp");
        var tmpPath = Path.Combine(tmpDir, Path.GetFileName(outputPath));
        Directory.CreateDirectory(tmpDir);
        try
        {
            targetMod.BeginWrite
                .ToPath(tmpPath)
                .WithLoadOrder(ordered)            // the target's OWN masters — no whole-order, no baseline
                .NoNextFormIDProcessing()          // persist the author's NextObjectID verbatim (no EnsureFormIdFloor)
                .WithEmbeddedEncodings(PluginTextEncoding.WriteInPlace(outputPath))
                .Write();
            return tmpPath;
        }
        catch
        {
            CleanupStaged(tmpPath);
            throw;
        }
    }

    /// <summary>The base-game masters every Skyrim plugin must carry, force-included on every patch write.</summary>
    internal static readonly ModKey[] BaselineMasters = { new("Skyrim", ModType.Master), new("Update", ModType.Master) };   // internal: the dry-run master preview mirrors the force-include

    // ---- PATH NAVIGATION + VERBS ----

    /// <summary>Parse one path segment into (field name, optional key/index). Brackets are MID-PATH only, and a malformed one fails LOUD.</summary>
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

    /// <summary>Walk <c>req.Path</c> from the record root, then apply <c>req.Verb</c> at the leaf — a plain hop
    /// descends a substruct, a bracketed one steps into an element, and leaf dispatch is on the RUNTIME shape.
    /// <paramref name="pathSlot"/> names the caller's own input slot, read only by the leaf-bracket throw.</summary>
    /// <returns>An apply-time note about what the write DID that the file cannot express — today only the list Add's
    /// membership answer; null when there is nothing to say.</returns>
    public static string? ApplyVerb(object record, WriteRequest req, string pathSlot = "field_path")
    {
        object current = record;
        for (int i = 0; i < req.Path.Length - 1; i++)
        {
            var (segName, segKey) = ParseSegment(req.Path[i]);
            var p = ResolveProperty(current.GetType(), segName)
                ?? throw new InvalidOperationException($"No property '{segName}' on {current.GetType().Name}");
            // No bracket → descend a substruct, materializing an absent optional one; a bracket steps INTO an element.
            current = segKey is null
                ? (p.GetValue(current) ?? MaterializeSubstruct(current, p, segName))
                : StepIntoElement(current, p, segName, segKey, materialize: true);   // write path may materialize a gendered arm on demand
        }
        var (leafName, leafKey) = ParseSegment(req.Path[^1]);
        if (leafKey is not null)
        {
            // A gendered LEAF renders as [0]/[1] but is not a list — redirect to the named halves. Twin of the corpus recogniser.
            var bracketProp = ResolveProperty(current.GetType(), leafName);
            if (bracketProp is { } gp && GenderedInterface(gp.PropertyType) is not null)
                throw new InvalidOperationException(
                    $"Gendered field '{leafName}' on {current.GetType().Name} renders as [0]/[1] but is not a list — set " +
                    $"its halves by name: '{leafName}.Male' (=[0]) / '{leafName}.Female' (=[1]).");
            // The gate's twin, for a path that bypassed pre-flight, so it must say the same thing rather than less.
            // Derived from the live property type — the engine is schema-blind — and the path comes from PathTo.
            var head = $"Path segment '{req.Path[^1]}' brackets a collection element at the LEAF. Brackets navigate "
                       + "mid-path only; ";
            // …including the KEY gate: a key that fails the two runtime recognisers falls back to the keyless form.
            if (bracketProp is not null && WriteVerbs.OfRuntimeType(bracketProp.PropertyType) is { } bshape)
                throw new InvalidOperationException(head
                    + (KeyShapeUsable(bracketProp.PropertyType, leafKey)
                        ? "to operate on that element, target the field itself and use the verb + Key: "
                          + $"{pathSlot}='{CorpusRulebook.PathTo(req.Path, req.Path.Length - 1, leafName)}', "
                          + $"key='{leafKey}' — {WriteVerbs.HowToAddress(bshape)}."
                        : $"to operate on that element, target the field '{leafName}' itself and use the verb "
                          + $"+ Key — {WriteVerbs.HowToAddress(bshape)}."));
            throw new InvalidOperationException(head
                + "to operate on a collection element at the leaf, target the collection field and use the verb + Key.");
        }
        var leaf = ResolveProperty(current.GetType(), leafName)
            ?? throw new InvalidOperationException($"No property '{leafName}' on {current.GetType().Name}");

        // Whole-value-coercible leaves are Set wholesale even where the runtime type also implements IList/IDict.
        if (CanCoerce(leaf.PropertyType)) { ApplyScalarVerb(current, leaf, req); return null; }

        var dictIface = ClosedInterface(leaf.PropertyType, typeof(IDictionary<,>));
        if (dictIface is not null) { ApplyDictVerb(current, leaf, dictIface, req); return null; }

        var listIface = ClosedInterface(leaf.PropertyType, typeof(IList<>));
        if (listIface is not null) return ApplyListVerb(current, leaf, listIface, req);

        ApplyScalarVerb(current, leaf, req);
        return null;
    }

    // ---- CopyFrom ----
    //  Transplant a FIELD's value from another plugin's version of a record into the patch's copy; every
    //  transplantable KIND is covered by shape, not a per-type list. Owned-child collections refuse at pre-flight.

    /// <summary>Deep-copy the value at <paramref name="path"/> from the source's version into the patch's copy; an ABSENT source value is refused.</summary>
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
                $"CopyFrom: the source plugin's version has '{string.Join('.', path)}' unset — nothing to copy (use op=Remove to clear the target).");

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

    /// <summary>Assign a getter-side value into leaf <paramref name="prop"/>, deep-copying; three shapes cover every kind, else a loud throw.</summary>
    static void TransplantValue(object parent, PropertyInfo prop, object srcVal)
    {
        var pt = prop.PropertyType;
        // Array-backed collections cannot be instantiated without a length, so CopyFrom refuses them cleanly. Tracked.
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

    // The DeepCopy method per getter runtime type, resolved once; a null entry means "no DeepCopy", memoised too.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (MethodInfo? m, bool isExtension)> _deepCopyOf = new();

    /// <summary>A DETACHED deep copy of a whole record, or null when Mutagen models no DeepCopy — the caller then REFUSES rather than use the live one.</summary>
    internal static IMajorRecordGetter? TryDeepCopyRecord(IMajorRecordGetter record)
        => TryDeepCopy(record) as IMajorRecordGetter;

    /// <summary>DeepCopy a Loqui getter to its settable concrete through Mutagen's generated EXTENSION; null when it has none. Per-type memoised.</summary>
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

    /// <summary>Locate a DeepCopy: a same-shape INSTANCE overload, else the static EXTENSION with the most-derived receiver.</summary>
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

    /// <summary>Copy ONE element to <paramref name="elemType"/>: an accepted element passes through, a Loqui one is DeepCopy'd.</summary>
    static object CopyElement(Type elemType, object elem)
    {
        if (elemType.IsInstanceOfType(elem)) return elem;                                   // formlink getter / value — share is safe
        if (TryDeepCopy(elem) is { } deep && elemType.IsInstanceOfType(deep)) return deep;   // Loqui element → settable copy
        throw new InvalidOperationException($"CopyFrom: cannot copy a {Pretty(elem.GetType())} element into a {Pretty(elemType)} list.");
    }

    /// <summary>Build a fresh settable collection holding copies of every element of <paramref name="src"/>.</summary>
    static object BuildCopiedList(Type collType, Type elemType, System.Collections.IEnumerable src)
    {
        var list = System.Activator.CreateInstance(collType)
            ?? throw new InvalidOperationException($"CopyFrom: could not instantiate collection {Pretty(collType)}.");
        var add = list.GetType().GetMethod("Add", new[] { elemType })
            ?? throw new InvalidOperationException($"CopyFrom: no Add({Pretty(elemType)}) on {Pretty(list.GetType())}.");
        foreach (var e in src) if (e is not null) add.Invoke(list, new[] { CopyElement(elemType, e) });
        return list;
    }

    /// <summary>Replace a live get-only collection's contents with copies of every element of <paramref name="src"/>.</summary>
    static void ReplaceListInPlace(object live, Type elemType, System.Collections.IEnumerable src)
    {
        var lt = live.GetType();
        lt.GetMethod("Clear")!.Invoke(live, null);
        var add = lt.GetMethod("Add", new[] { elemType })
            ?? throw new InvalidOperationException($"CopyFrom: no Add({Pretty(elemType)}) on {Pretty(lt)}.");
        foreach (var e in src) if (e is not null) add.Invoke(live, new[] { CopyElement(elemType, e) });
    }

    /// <summary>Reflectively call <c>SetTo(FormKey)</c> on a live get-only FormLink.</summary>
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

        // Parent-aware FormLinkOrIndex: the ctor needs the owning ARM as its flag source, so Coerce cannot serve it.
        if (req.Verb == "Set" && IsFormLinkOrIndex(prop.PropertyType)) { SetFloi(parent, prop, req.Value!); return; }

        // Add / valued-Remove on a [Flags] enum are BIT operations, so one flag flips without re-listing the others;
        // a VALUELESS Remove is the whole-field clear below. This fails LOUD for a caller that bypassed pre-flight.
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
                // A FormLink setter REJECTS a null reference, so the clear routes through EmptyFormLinkOf — which
                // would also blank a REQUIRED link, hence the loud refusal here for a pre-flight-bypassing caller.
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

    /// <summary>Flags-enum bit op: OR (<c>Add</c>) or AND-NOT (<c>Remove</c>) the operand into the leaf's CURRENT value, through the enum coercion a Set uses.</summary>
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

    /// <summary>Build a modeled struct FROM PARTS — the ONE composition primitive; its nested <c>sets</c> replay THROUGH <see cref="ApplyVerb"/> itself.</summary>
    static object BuildStruct(StructSpec spec)
    {
        var type = ResolveStructType(spec.Type);
        // A type whose every ctor takes arguments is built FROM the compose's own fields when no ctor_args were given.
        var fromFields = spec.CtorArgs is null ? CtorArgsFromFields(type, spec.Fields) : null;
        // The constructor CtorArgsFromFields chose is the one invoked, not one re-derived from the arg count.
        var instance = fromFields is { } ff ? Instantiate(ff.Ctor, ff.Args) : Instantiate(type, spec.CtorArgs);
        foreach (var (name, val) in spec.Fields ?? new())
        {
            // A field the ctor already carried is not re-set; a read-only discriminator would throw.
            if (fromFields?.Consumed.Contains(name) == true) continue;
            var p = ResolveProperty(type, name)
                ?? throw new InvalidOperationException($"No field '{name}' on '{spec.Type}'");
            if (!p.CanWrite) throw new InvalidOperationException($"Field '{name}' on '{spec.Type}' is not writable");
            // A FormLinkOrIndex field needs SetFloi, the just-built instance being the flag-bearing arm — as in ApplyScalarVerb.
            if (IsFormLinkOrIndex(p.PropertyType)) SetFloi(instance, p, val);
            else p.SetValue(instance, Coerce(val, p.PropertyType));
        }
        foreach (var req in spec.Sets ?? new())
            // General nested writes reuse the verb engine, rooted at the built struct, so a refusal names that slot.
            ApplyVerb(instance, req, "path");
        if (EmptyComposeRefusal(spec, type, instance) is { } why) throw new ExpectedApplyRejectionException(why);
        return instance;
    }

    /// <summary>Refuse a compose given NOTHING, whose built object serializes to ZERO bytes while the call reports it
    /// as landed. Deliberately NARROW — nothing supplied AND every settable property null — and it names what to set
    /// from the TYPE itself. Contract in docs/architecture/write-path.md.</summary>
    static string? EmptyComposeRefusal(StructSpec spec, Type type, object instance)
    {
        // `Length: > 0` on ctor_args: an EMPTY array is the 0-arg ctor, not a supplied discriminator.
        if (spec.CtorArgs is { Length: > 0 } || spec.Fields is { Count: > 0 } || spec.Sets is { Count: > 0 }) return null;
        // Instance, non-indexed properties ONLY, in a STABLE order: a static, an indexer, or CLR order would break it.
        var settable = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                           .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
                           .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        if (settable.Count == 0) return null;                      // nothing to advise; not this check's case
        foreach (var p in settable)
        {
            object? v;
            try { v = p.GetValue(instance); } catch { return null; }   // unreadable ⇒ can't claim emptiness
            if (v is not null) return null;                        // it carries something — let the write proceed
        }
        // The worked example is the line a caller COPIES, so it names a scalar they can actually pass as a string.
        static bool IsSimple(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u.IsPrimitive || u.IsEnum || u == typeof(string) || u == typeof(decimal);
        }
        var usable = settable.Where(p => !p.Name.StartsWith("Unknown", StringComparison.Ordinal)
                                      && !p.Name.Equals("Versioning", StringComparison.Ordinal)).ToList();
        var example = usable.FirstOrDefault(p => IsSimple(p.PropertyType)) ?? usable.FirstOrDefault() ?? settable[0];
        // Worded for ANY compose, not just a list Add: BuildStruct also serves a polymorphic-arm Set and SetAtIndex.
        return $"compose type='{spec.Type}' was given no fields, and a {spec.Type} built from nothing has no " +
               "serializable content: it would exist in memory and be written as ZERO bytes, so the field would be " +
               "unchanged on disk while the call reported success (#308). Name at least one field — e.g. " +
               $"compose={{\"type\":\"{spec.Type}\",\"fields\":{{\"{example.Name}\":\"<value>\"}}}}. Settable " +
               $"fields on {spec.Type}: {string.Join(", ", settable.Select(p => p.Name))}. " +
               "(This also refuses the two-step shape — compose an empty value, then set its fields in a LATER op of " +
               "the same call — which did work: the check runs as the value is built and cannot see the ops after " +
               "it. Compose it WITH its fields instead; one op, and it cannot half-land.)";
    }

    /// <summary>Resolve a struct catalog name to its concrete settable type, across every Mutagen assembly; loud if none resolves.</summary>
    static Type ResolveStructType(string name) =>
        typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim." + name)
        ?? AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen"))
            .SelectMany(SafeTypes).FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && t.Name == name)
        ?? throw new InvalidOperationException(
            $"Unknown struct type '{name}' — no concrete class in Mutagen.Bethesda.Skyrim nor any Mutagen assembly. " +
            "If Mutagen models it under another name, surface that; never guess.");

    /// <summary>True iff <see cref="BuildStruct"/> can instantiate this TYPE from a PARAMETERLESS compose — the gate-side twin of <see cref="Instantiate"/>.</summary>
    internal static bool IsPlainComposableStruct(string? typeName)
    {
        if (typeName is null) return false;
        Type t;
        try { t = ResolveStructType(typeName); }
        catch { return false; }
        return t.GetConstructor(Type.EmptyTypes) is not null;
    }

    /// <summary>Instantiate for build-from-parts: explicit positional ctor args → parameterless ctor → composition.</summary>
    static object Instantiate(Type t, string[]? ctorArgs)
    {
        if (ctorArgs is not null)
        {
            var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == ctorArgs.Length)
                ?? throw new InvalidOperationException(
                    $"{t.Name}: no constructor taking {ctorArgs.Length} arg(s). Ctors: {CtorList(t)}");
            return Instantiate(ctor, ctorArgs);
        }
        var paramless = t.GetConstructor(Type.EmptyTypes);
        if (paramless is not null) return paramless.Invoke(null);
        return InstantiateComposition(t);
    }

    /// <summary>Invoke ONE already-chosen constructor with positional string args; taking the ctor rather than re-selecting by arity is what makes one choice.</summary>
    static object Instantiate(ConstructorInfo ctor, string[] args) =>
        ctor.Invoke(ctor.GetParameters().Select((p, i) => Coerce(args[i], p.ParameterType)).ToArray());

    /// <summary>Build a type from the constructor its own compose fields satisfy, or null when none does.</summary>
    internal static object? BuildFromFieldConstructor(Type t, IReadOnlyDictionary<string, string>? fields) =>
        CtorArgsFromFields(t, fields) is { } ff ? Instantiate(ff.Ctor, ff.Args) : null;

    /// <summary>The compose FIELDS a constructor carries, for a compose with no <c>ctor_args</c> — the names both apply and the gate then skip.</summary>
    internal static IReadOnlySet<string> CtorConsumedFields(string structTypeName, IReadOnlyDictionary<string, string>? fields)
    {
        try
        {
            return CtorArgsFromFields(ResolveStructType(structTypeName), fields)?.Consumed ?? NoConsumedFields;
        }
        catch { return NoConsumedFields; }                        // unknown type — ResolveStructType says so at apply
    }

    static readonly HashSet<string> NoConsumedFields = new(StringComparer.Ordinal);

    /// <summary>Recognition-only mirror of <see cref="Instantiate"/>'s ctor-args path, resolving and checking the SAME way. Null = legal, else the mismatch.</summary>
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

    /// <summary>Positional ctor args from a compose's OWN fields: the SMALLEST ctor every parameter of which a field names and coerces, returned WITH that ctor.</summary>
    static (ConstructorInfo Ctor, string[] Args, HashSet<string> Consumed)? CtorArgsFromFields(Type t, IReadOnlyDictionary<string, string>? fields)
    {
        if (t.GetConstructor(Type.EmptyTypes) is not null || fields is not { Count: > 0 }) return null;
        foreach (var ctor in t.GetConstructors().Where(c => c.GetParameters().Length > 0)
                              .OrderBy(c => c.GetParameters().Length))
        {
            var ps = ctor.GetParameters();
            var args = new string[ps.Length];
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                var named = fields.Keys.FirstOrDefault(k => string.Equals(k, ps[i].Name, StringComparison.OrdinalIgnoreCase));
                if (named is null || !TryCoerce(fields[named], ps[i].ParameterType, out _)) { ok = false; break; }
                args[i] = fields[named];
                consumed.Add(named);
            }
            if (ok) return (ctor, args, consumed);
        }
        return null;
    }

    /// <summary>The pre-flight twin of the no-ctor_args instantiate: buildable from the fields supplied? Else the missing ctor parameter, NAMED.</summary>
    internal static string? TryRecognizeInstantiable(string structTypeName, IReadOnlyDictionary<string, string>? fields)
    {
        Type t;
        try { t = ResolveStructType(structTypeName); }
        catch { return null; }                                   // unknown type — ResolveStructType says so loudly at apply
        if (t.GetConstructor(Type.EmptyTypes) is not null) return null;
        if (CtorArgsFromFields(t, fields) is not null) return null;
        var ctor = t.GetConstructors().Where(c => c.GetParameters().Length > 0)
                    .OrderBy(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) return null;
        var ps = ctor.GetParameters();
        var named = ps.Select(p => FieldNameFor(t, p)).ToList();
        return $"compose type '{structTypeName}' has no parameterless constructor — it is built from " +
               string.Join(" and ", ps.Select((p, i) => $"'{named[i]}' ({Pretty(p.ParameterType)})")) +
               $", so a compose that leaves {(ps.Length == 1 ? "it" : "one")} out has nothing to build. Name " +
               $"{string.Join(" and ", named.Select(n => $"'{n}'"))} in fields= (e.g. " +
               $"fields={{\"{named[0]}\":\"<value>\"}}), or pass the same value(s) positionally in ctor_args. " +
               $"Constructors on {structTypeName}: {CtorList(t)}.";
    }

    /// <summary>The FIELD name a ctor parameter is named by — the type's own matching property, else the parameter.</summary>
    static string FieldNameFor(Type t, ParameterInfo p) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))?.Name
        ?? p.Name ?? "arg";

    /// <summary>A composition type has NO parameterless ctor: a <c>GenderedItem&lt;T&gt;</c> materializes each half, an <c>Array2d&lt;T&gt;</c> is a NAMED gap.</summary>
    static object InstantiateComposition(Type t)
    {
        var concrete = ConcreteOf(t) ?? t;     // the declared type is usually the getter interface (IGenderedItem<T>) — map to the concrete class
        var defName = (concrete.IsGenericType ? concrete.GetGenericTypeDefinition() : concrete).Name;
        if (defName.StartsWith("GenderedItem", StringComparison.Ordinal))
        {
            // GenderedItem<T>(T male, T female): pick the smallest positional ctor, then build each part per its kind (below).
            var ctor = concrete.GetConstructors().Where(c => c.GetParameters().Length > 0)
                .OrderBy(c => c.GetParameters().Length).First();
            // A FORMLINK half must be a NON-NULL empty link — the writer dereferences a null one, while an empty link
            // serializes to the absent slot the READER produces. A Model/ref half stays null, a value half default.
            var args = ctor.GetParameters().Select(p => EmptyFormLinkOf(p.ParameterType) ?? DefaultOf(p.ParameterType)).ToArray();
            return ctor.Invoke(args);
        }
        throw new CompositionRequiredException(t.Name, t);   // Array2d<T> (indexer-shaped) + any unknown composition — named, loud
    }

    static object? DefaultOf(Type t) => t.IsValueType ? System.Activator.CreateInstance(t) : null;

    /// <summary>A NON-NULL EMPTY link for a FormLink-family type, else null; materializes a GenderedItem half a null would NRE the writer on.</summary>
    static object? EmptyFormLinkOf(Type t) => TryFormLink("0", t, out var link) ? link : null;

    /// <summary>True iff <paramref name="t"/> is a REQUIRED (non-nullable) FormLink-family type — the branch <see cref="TryFormLink"/> keys off too.</summary>
    static bool IsRequiredFormLink(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(FormLink<>) || def == typeof(IFormLink<>) || def == typeof(IFormLinkGetter<>);
    }

    /// <summary>Map a getter/interface type to the concrete settable class the engine can instantiate, else null; one answer for every caller.</summary>
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

    /// <summary>True iff a collection's ELEMENT type is WHOLE-COERCIBLE — set as one value, the AssetLink family by catalog name when its AQ will not resolve.</summary>
    internal static bool IsWholeCoercibleElement(string? elementRef, string? elementAq)
    {
        if (elementAq is not null && ResolveType(elementAq) is { } rt && CanCoerce(ConcreteOf(rt) ?? rt)) return true;
        return elementRef is not null && elementRef.StartsWith("AssetLink", StringComparison.Ordinal);
    }

    /// <summary>True iff <paramref name="t"/> is a Mutagen <c>AssetLink&lt;T&gt;</c> family type, by generic-definition NAME; shared with the read path.</summary>
    internal static bool IsAssetLinkFamily(Type t)
    {
        if (!t.IsGenericType) return false;
        var n = t.GetGenericTypeDefinition().Name;
        return n.StartsWith("AssetLink", StringComparison.Ordinal) || n.StartsWith("IAssetLink", StringComparison.Ordinal);
    }

    static void ApplyDictVerb(object parent, PropertyInfo prop, Type dictIface, WriteRequest req)
    {
        // An ABSENT optional dict is materialized so a first entry can be set; Remove on one refuses BEFORE that happens.
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
        // The entry VALUE for Set/Add: a struct-VALUED dict builds FROM PARTS as a list Add does, a coercible-VALUE dict coerces.
        object? BuildValue() => req.Struct is not null ? BuildStruct(req.Struct) : Coerce(req.Value!, vType);

        switch (req.Verb)
        {
            case "Set": // REPLACES (indexer-set) — for a composable dict this overwrites an entry's composed value
                setItem.Invoke(dict, new[] { Coerce(req.Key!, kType), BuildValue() });
                break;
            case "Add": // distinct from Set: a duplicate key is refused (Mutagen's dict.Add throws). Pre-check ContainsKey so
            {           // the guidance names the fix — "use Set to overwrite" — instead of the raw Mutagen string.
                var addKey = Coerce(req.Key!, kType);
                var contains = dt.GetMethod("ContainsKey", new[] { kType });
                if (contains is not null && contains.Invoke(dict, new[] { addKey }) is true)
                    // EXPECTED apply rejection — live occupancy, so it renders as guidance, not under the wrapper.
                    throw new ExpectedApplyRejectionException(
                        $"Key '{req.Key}' already present in '{prop.Name}' — use Set to overwrite that entry, or choose a free key/index.");
                dt.GetMethod("Add", new[] { kType, vType })!.Invoke(dict, new[] { addKey, BuildValue() });
                break;
            }
            case "Remove":
                // SURFACE a no-op Remove: the runtime Remove returns false for an absent key — refuse it, cleanly.
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

    /// <returns>The apply-time note — non-null only for an Add that duplicated, before this write or within its batch.</returns>
    static string? ApplyListVerb(object parent, PropertyInfo prop, Type listIface, WriteRequest req)
    {
        // ARRAY-backed collection: the list verbs assume ExtendedList semantics, an array has none, and even
        // materializing an absent one throws. Refuse LOUD and NAMED — array-collection mutation is a distinct write
        // mechanism not yet built. Element COERCION is unaffected; this is the collection-shape gap. Tracked.
        if (prop.PropertyType.IsArray)
            // The refused SET is derived, not listed, so a verb added later cannot read as supported here.
            throw new ExpectedApplyRejectionException(
                $"'{prop.Name}' is an array-backed collection ({Pretty(prop.PropertyType)}); "
                + WriteVerbs.CollectionVerbNames(WriteVerbs.OfElement(CollectionKind.List, listIface.GetGenericArguments()[0]))
                + " are not yet supported on arrays (some, like Weather clouds, are fixed-size game structures). "
                + "Tracked gap — array-collection mutation is a distinct write mechanism not yet built.");

        // An ABSENT optional list is materialized so a first element can be added; Remove on one refuses BEFORE that happens.
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
                // A struct-element list builds FROM PARTS, a coercible one coerces, and composes= appends many in ONE
                // op. EVERY Add form reports whether the list ALREADY carried what it appended, because a duplicating
                // add and a clean one otherwise render identically. Composed elements compare structurally.
                if (req.Structs is { } addSpecs)
                {
                    var addM = AddMethod(lt, elem);
                    // BUILD every element and ask the membership question BEFORE appending any: asked mid-loop the
                    // check would report this op's own repeat as something the FILE carried. Counted and said apart.
                    var builtAll = addSpecs.Select(BuildStruct).ToList();
                    int dup = builtAll.Count(b => ListCarries(elem, list, b));
                    // A repeat is compared with the element type's own Equals — the structural override Contains uses.
                    int repeat = 0;
                    for (int i = 1; i < builtAll.Count; i++)
                        for (int j = 0; j < i; j++)
                            if (Equals(builtAll[i], builtAll[j])) { repeat++; break; }
                    foreach (var b in builtAll) addM.Invoke(list, new[] { b });
                    return ComposedAddNote(prop.Name, builtAll.Count, dup, repeat);
                }
                if (req.Struct is not null)
                {
                    var built = BuildStruct(req.Struct);
                    var carriedStruct = ListCarries(elem, list, built);
                    AddMethod(lt, elem).Invoke(list, new[] { built });
                    return ComposedAddNote(prop.Name, 1, carriedStruct ? 1 : 0, 0);
                }
                var addValue = Coerce(req.Value!, elem);
                var already = ListCarries(elem, list, addValue);
                AddMethod(lt, elem).Invoke(list, new[] { addValue });
                // CONDITIONAL voice and no count: this renders on a dry run too, and Contains answers presence.
                return already
                    ? $"duplicate: '{req.Value}' is already in '{prop.Name}', and Add appends rather than replacing "
                      + "— once this write lands the list carries another copy, so Remove it by value if it should hold one."
                    : null;
            case "SetAtIndex":
            {
                // EXPECTED apply rejection — live length: the in-range bound is apply's, pre-checked so it reads as guidance.
                int idx = int.Parse(req.Key!, CultureInfo.InvariantCulture);
                int count = CollectionCount(list);
                if (idx < 0 || idx >= count)
                    throw new ExpectedApplyRejectionException(IndexRangeMessage(prop.Name, idx, count, IndexOpKind.Overwrite));
                // Built the SAME way Add builds it, then OVERWRITTEN in place, because Remove+Add would move the row to the END.
                Indexer(lt).GetSetMethod()!.Invoke(list,
                    new[] { (object)idx, req.Struct is not null ? BuildStruct(req.Struct) : Coerce(req.Value!, elem) });
                break;
            }
            case "InsertAtIndex":
            {
                // The sibling neither Add nor SetAtIndex is: Insert puts a NEW element AT a position and shifts the
                // rest right, the only way to grow a POSITION-CONTIGUOUS run such as a CTDA OR-group in place.
                int idx = int.Parse(req.Key!, CultureInfo.InvariantCulture);
                int count = CollectionCount(list);
                // APPEND-INCLUSIVE bound: inserting AT count is legal and is what Add does, so it must not refuse.
                if (idx < 0 || idx > count)
                    throw new ExpectedApplyRejectionException(IndexRangeMessage(prop.Name, idx, count, IndexOpKind.Insert));
                // Built EXACTLY as Add and SetAtIndex build it, so only where it lands and what moves differ.
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
                    // SURFACE a no-op Remove-by-value: Remove returns false for an absent value — refuse it, cleanly.
                    if (lt.GetMethod("Remove", new[] { elem })!.Invoke(list, new[] { Coerce(req.Value!, elem) }) is false)
                        throw new ExpectedApplyRejectionException(
                            $"Value '{req.Value}' is not present in '{prop.Name}' — nothing to remove.");
                }
                break;
            case "ReplaceAll":
                lt.GetMethod("Clear")!.Invoke(list, null);
                var add = AddMethod(lt, elem);
                // composes= ReplaceAll clears then appends each BUILT element; a coercible-element list uses Values.
                if (req.Structs is { } replSpecs)
                    foreach (var s in replSpecs) add.Invoke(list, new[] { BuildStruct(s) });
                else
                    foreach (var v in req.Values ?? Array.Empty<string>()) add.Invoke(list, new[] { Coerce(v, elem) });
                break;
            default:
                throw new InvalidOperationException($"Verb '{req.Verb}' is not valid on list '{prop.Name}'.");
        }
        return null;
    }

    /// <summary>Does the list already carry this element? Asked through <c>Contains</c> on the CLOSED INTERFACE, and never a reason to fail the write.</summary>
    static bool ListCarries(Type elem, object list, object? value)
    {
        try
        {
            var contains = ContainsMethods.GetOrAdd(elem,
                t => typeof(ICollection<>).MakeGenericType(t).GetMethod("Contains", new[] { t }));
            return contains is not null && contains.Invoke(list, new[] { value }) is true;
        }
        catch { return false; }
    }

    /// <summary>The closed <c>ICollection&lt;T&gt;.Contains</c> per element type, resolved once — Add asks it per element.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo?> ContainsMethods = new();

    /// <summary>The note a COMPOSED Add owes the caller: counts, a remedy by INDEX, and separate clauses for the two ways it duplicates.</summary>
    static string? ComposedAddNote(string prop, int total, int dup, int repeat)
    {
        if (dup == 0 && repeat == 0) return null;
        var clauses = new List<string>(2);
        if (dup > 0) clauses.Add($"{Subject(dup)} already in '{prop}'");
        if (repeat > 0) clauses.Add($"{Subject(repeat)} a repeat of another element in this same op");
        return "duplicate: " + string.Join(" and ", clauses)
            + ", and Add appends rather than replacing — once this write lands the list carries another copy, "
            + "so Remove by index if it should hold one of each.";

        string Subject(int n) =>
            total == 1 ? "the composed element is"
            : n == 1 ? $"1 of the {total} composed elements is"
            : $"{n} of the {total} composed elements are";
    }

    /// <summary>Element count WITHOUT assuming the non-generic <c>ICollection</c> — enumerate, so an out-of-range index reads as an expected rejection.</summary>
    static int CollectionCount(object coll)
    {
        int n = 0;
        foreach (var _ in (System.Collections.IEnumerable)coll) n++;
        return n;
    }

    /// <summary>Which list index op is being refused. An enum, not a bool, because insert addresses a GAP and so has its own bound.</summary>
    enum IndexOpKind { Overwrite, RemoveAt, Insert }

    /// <summary>The clean out-of-range message — the bound it states is the one <see cref="ApplyListVerb"/> enforces.</summary>
    static string IndexRangeMessage(string field, int idx, int count, IndexOpKind kind)
    {
        // Insert's own sentence, because its legal range INCLUDES count and the shared one cannot say that.
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

    /// <summary>Materialize an absent optional collection so a first element or entry can be added; loud if unsettable.</summary>
    static object MaterializeCollection(object parent, PropertyInfo prop)
    {
        if (!prop.CanWrite)
            throw new InvalidOperationException($"Collection '{prop.Name}' is absent and not settable — cannot materialize.");
        var made = System.Activator.CreateInstance(prop.PropertyType)
            ?? throw new InvalidOperationException($"Could not instantiate collection type {prop.PropertyType.Name} for '{prop.Name}'.");
        prop.SetValue(parent, made);
        return made;
    }

    /// <summary>Materialize an absent intermediate substruct so a field inside it can be set; an unbuildable composition fails LOUD with the segment named.</summary>
    static object MaterializeSubstruct(object parent, PropertyInfo prop, string segment)
    {
        if (!prop.CanWrite)
            throw new InvalidOperationException($"Absent substruct '{segment}' ({Pretty(prop.PropertyType)}) is not settable — cannot materialize.");
        // An absent OWNED CHILD RECORD lacks a parameterless ctor because it has a FormKey, not because it composes.
        if (typeof(IMajorRecordGetter).IsAssignableFrom(prop.PropertyType))
            throw new ExpectedApplyRejectionException(
                $"'{segment}' holds an owned child RECORD ({Pretty(prop.PropertyType)}) and the record being written " +
                "carries none, so there is nothing to write into. houseCARL will not synthesize a record as a " +
                "sub-object — a record exists only with its own FormKey. To give the parent a child it lacks, create " +
                "one on the record axis: " + ToolNames.Create + $" with parent= the parent's FormID and " +
                $"collection='{segment}' in its records= element. Address an existing child by its own FormID " +
                "instead. If the version you are patching does carry one, note that a patch's fresh override of a " +
                "parent does not bring the parent's child records with it — the record axis reaches it, this path does not.");
        object made;
        try { made = Instantiate(prop.PropertyType, null); }
        catch (CompositionRequiredException) { throw new CompositionRequiredException(segment, prop.PropertyType); }  // re-stamp with the path segment
        prop.SetValue(parent, made);
        return made;
    }

    /// <summary>Step INTO a list/dict element mid-path: a list by int index (enumerated), a dict by coerced key. Loud on absent, bad or missing.</summary>
    internal static object StepIntoElement(object parent, PropertyInfo prop, string name, string key, bool materialize = false)
    {
        // A '*' key is a quantifier token that reached a walk which indexes ONE concrete element — say that.
        if (key.Length > 0 && key[0] == '*')
            throw new InvalidOperationException(
                $"'{name}[{key}]' cannot be indexed here — [*any], [*all] and [*none] fold a list into a boolean in " +
                $"where=, and [*] and [*count] are project/walk path steps; index a concrete element ('{name}[0]') instead.");

        // Gendered field ([0]=male / [1]=female): a fixed pair, not a list, through the named hop's own materialize-and-write-back.
        if (GenderedInterface(prop.PropertyType) is not null)
            return StepIntoGenderedArm(parent, prop, name, key, materialize);

        var coll = prop.GetValue(parent)
            ?? throw new ExpectedApplyRejectionException(   // live-state: empty/absent collection — clean, not the inconsistency wrapper
                $"Cannot navigate into '{name}[{key}]': the collection is absent (null). Add an element first " +
                "(element composition — wave 1 half B), then navigate into it.");

        // Recognise BOTH the mutable and read-only collection interfaces — a read navigates a getter overlay.
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

    /// <summary>The gendered-arm twin of <see cref="StepIntoElement"/>'s branches: a WRITE materializes and writes back, a READ fails LOUD.</summary>
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
            arm = MaterializeSubstruct(gendered, armProp, armName);   // materialize the ref arm + WRITE BACK via the setter, or it is an orphan
        }
        return arm;
    }

    /// <summary>The canonical gendered index→arm mapping, the ONE place navigation and the depth render both read.</summary>
    internal static readonly string[] GenderedArmNames = { "Male", "Female" };

    /// <summary>The closed gendered interface a type carries, else null, by generic-definition NAME; the corpus-side twin must agree.</summary>
    internal static Type? GenderedInterface(Type t)
    {
        static bool IsGen(Type x)
        {
            if (!x.IsGenericType) return false;
            var n = x.GetGenericTypeDefinition().Name;   // e.g. "GenderedItem`1" / "IGenderedItem`1" / "IGenderedItemGetter`1"
            var tick = n.IndexOf('`');
            if (tick > 0) n = n[..tick];
            // ANCHORED to the exact gendered family, so this cannot drift broader than the corpus-side check.
            return n is "GenderedItem" or "IGenderedItem" or "IGenderedItemGetter";
        }
        if (IsGen(t)) return t;
        return t.GetInterfaces().FirstOrDefault(IsGen);
    }

    static PropertyInfo Indexer(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 1 && p.CanWrite)
        ?? throw new InvalidOperationException($"No writable single-arg indexer on {t.Name}");

    /// <summary>The <c>IList&lt;T&gt;.Insert(int, T)</c> the <c>InsertAtIndex</c> arm drives, off the CLOSED INTERFACE; a <c>T[]</c> is refused before here.</summary>
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

    // ---- COERCION (string -> typed value) ----
    //  Recognition is SHARED between Coerce and CanCoerce through the Try* family: each returns true iff `u` is in
    //  its family and emits the value only when `text` is non-null, so the two surfaces cannot drift.

    /// <summary>Turn a string into a value of <paramref name="targetType"/>, or throw fail-loud.</summary>
    internal static object? Coerce(string text, Type targetType)
    {
        var u = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (TryPrimitive(text, u, out var r) || TryEnum(text, u, out r)
            || TryFormLink(text, u, out r) || TryValueType(text, u, out r))
            return r;
        throw new InvalidOperationException(
            $"No coercion rule for {targetType.FullName} (value={text}). If Mutagen models this as a writable " +
            "value type, it is a real coercion gap to add (extend TryValueType) — surface it via coerce-audit, never guess.");
    }

    /// <summary>Recognition-only mirror of <see cref="Coerce"/>, sharing the Try* recognisers so they cannot disagree.</summary>
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

    // ---- FormLink null-clear: a Set that CLEARS a link is a null-synonym value, a fixed set matched trimmed,
    //  case-insensitively and FULL-STRING, so a real FormID is never a clear. Apply and pre-flight share it.
    static readonly string[] FormKeyNullSynonyms = { "0", "00000000", "Null", "000000:Null" };

    /// <summary>True iff <paramref name="text"/> is a canonical FormKey null-clear synonym.</summary>
    internal static bool IsFormKeyNullSynonym(string? text)
    {
        var v = (text ?? "").Trim();
        foreach (var s in FormKeyNullSynonyms)
            if (v.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A null-synonym clears to <see cref="FormKey.Null"/>; anything else parses via FormKey.Factory.</summary>
    static FormKey ToFormKey(string text) => IsFormKeyNullSynonym(text) ? FormKey.Null : FormKey.Factory(text);

    /// <summary>Pre-flight value-shape check for a FormLink Set: a null-clear synonym, or a value that parses as a FormKey, which a type-only check would miss.</summary>
    internal static bool IsValidFormLinkValue(string? text) => IsFormKeyNullSynonym(text) || (text is not null && FormKey.TryFactory(text, out _));

    // ---- List INDEX value-shape: the recognizer mirrors apply's own int.Parse exactly (int32,
    //  NumberStyles.Integer, InvariantCulture) plus the non-negative pre-check. The UPPER bound is apply's.

    /// <summary>Can this collection actually be indexed by <paramref name="key"/>? The runtime twin of
    /// <c>CorpusRulebook.KeyShapeError</c>, built from the same two things apply keys on. Only the leaf throw asks.</summary>
    static bool KeyShapeUsable(Type collectionType, string key)
    {
        if (ClosedInterface(collectionType, typeof(IDictionary<,>)) is { } di)
            return TryCoerce(key, di.GetGenericArguments()[0], out _);
        if (ClosedInterface(collectionType, typeof(IList<>)) is not null)
            return IsValidListIndexValue(key);
        return true;
    }

    /// <summary>True iff <paramref name="text"/> is a legal list INDEX SHAPE — a non-negative int32; range is apply's.</summary>
    internal static bool IsValidListIndexValue(string? text) =>
        text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0;

    // ---- Same-call sibling reference: a create's field VALUE can forward-reference a record created EARLIER in the
    //  same call, "@<editorid>", substituted after allocation. CREATE-CONTEXT ONLY, and the recognizer is shared by
    //  pre-flight and the substitution. Where it is legal: docs/architecture/corpus-rulebook.md.
    internal const char SiblingRefSigil = '@';

    /// <summary>True iff <paramref name="text"/> is a same-call sibling reference (<c>@editorid</c>); the sigil cannot collide with a real value.</summary>
    internal static bool IsSameCallSiblingRef(string? text, out string editorId)
    {
        editorId = "";
        var v = (text ?? "").Trim();
        if (v.Length < 2 || v[0] != SiblingRefSigil) return false;
        editorId = v[1..].Trim();
        return editorId.Length > 0;
    }

    /// <summary>FormLink families — build the matching concrete from a "FORMID:ModName.esp" key, the target type deciding nullable or not.</summary>
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
        // IFormLinkOrIndex<T> is NOT coercible here — its ctor needs the owning arm, which Coerce has no access to.
        return false;
    }

    // ---- FORMLINKORINDEX — a condition target holds EITHER a FormID or a numeric alias / package-data index, and
    //  the owning arm's bools decide which serialises, so the ctor needs the arm and this lives OUTSIDE Coerce.
    //  IsFormLinkOrIndex is the ONE predicate the engine write, pre-flight and coerce-audit share.

    /// <summary>True iff <paramref name="t"/> is a Mutagen <c>FormLinkOrIndex&lt;T&gt;</c> family type, by generic definition.</summary>
    internal static bool IsFormLinkOrIndex(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (!u.IsGenericType) return false;
        var n = u.GetGenericTypeDefinition().Name;
        return n.StartsWith("IFormLinkOrIndex", StringComparison.Ordinal)
            || n.StartsWith("FormLinkOrIndex", StringComparison.Ordinal);
    }

    /// <summary>How a condition target serialises: a FormID, or an index read as a quest alias or package data.</summary>
    internal enum FloiMode { Form, IndexAlias, IndexPackData }

    /// <summary>Classify a condition-target VALUE from the value alone: <c>FORMID:Plugin.esp</c> is form mode, <c>alias N</c>/<c>packdata N</c> and a bare integer index.</summary>
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

    /// <summary>Non-throwing mirror of <see cref="ClassifyFloiValue"/> for pre-flight, so the two agree.</summary>
    internal static bool TryClassifyFloiValue(string value)
    {
        try { ClassifyFloiValue(value); return true; }
        catch { return false; }
    }

    /// <summary>Set a condition-data FormLinkOrIndex target, inferring the mode and setting the owning arm's discriminator to match. Fail-loud otherwise.</summary>
    static void SetFloi(object arm, PropertyInfo prop, string value)
    {
        if (arm is not IFormLinkOrIndexFlagGetter)
            throw new InvalidOperationException(
                $"'{prop.Name}' is a FormLinkOrIndex target but its parent {arm.GetType().Name} is not a condition-data " +
                "arm (no UseAliases/UsePackageData discriminator) — cannot set (surfaced, not guessed; Q3).");

        var (mode, key, index) = ClassifyFloiValue(value);   // throws fail-loud on an unclassifiable value

        // The concrete closed FormLinkOrIndex<T>: the live instance's type, else the declared interface mapped.
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

    /// <summary>Set one of the arm's discriminator bools through the engine's writable-property resolution.</summary>
    static void SetArmFlag(object arm, string flagName, bool value)
    {
        var p = ResolveProperty(arm.GetType(), flagName)
            ?? throw new InvalidOperationException($"Condition arm {arm.GetType().Name} has no '{flagName}' discriminator field.");
        if (!p.CanWrite) throw new InvalidOperationException($"Discriminator '{flagName}' on {arm.GetType().Name} is not writable.");
        p.SetValue(arm, value);
    }

    /// <summary>The corpus-derived value-type family coerce-audit enumerates; extend HERE when the audit surfaces a new writable value type.</summary>
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

        // Mutagen TranslatedString — set the whole localized string from a plain string, through the same implicit conversion, so it serialises identically.
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

        // Mutagen AssetLink<T> family — a path string. The INTERFACES are recognised too, because a collection
        // element's runtime type is the interface; every arm maps to the mutable AssetLink<T>. Shared by-name predicate.
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

    /// <summary>Build <paramref name="t"/> from a string via its implicit string operator, else a ctor / factory.</summary>
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

    // ---- SHARED REFLECTION HELPERS ----
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

    /// <summary>Set a member to null through the SAME settability resolution the engine writes through; false for a get-only property, which a caller can skip.</summary>
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

/// <summary>Thrown when a write must MATERIALIZE an absent substruct whose type has no parameterless ctor — Mutagen's composition types. A named gap.</summary>
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

/// <summary>A serialize-boundary <see cref="NullReferenceException"/> re-stamped as a loud, NAMED refusal; nothing is on disk by the time it throws.</summary>
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

/// <summary>The in-place write's refusal to re-serialize a plugin flagged LOCALIZED, thrown before the staging
/// directory exists. Why a localized rewrite corrupts the plugin whatever arrangement its tables are in is in
/// docs/architecture/write-path.md; <see cref="FromSentence"/> is the only way to build it, so the text has one home.</summary>
public sealed class LocalizedTargetUnsupportedException : InvalidOperationException
{
    /// <summary>The refusal for a localized plugin — one sentence per SHAPE, and a remedy only where one was walked on a fixture.</summary>
    public static string Shaped(string pluginFileName, LocalizedAssessment a, string? laneClause = null)
    {
        var head = $"houseCARL did not write '{pluginFileName}' — the file is unchanged and nothing was staged. ";
        var remedy = RemedyFor(a, laneClause);
        return head + ShapeBody(a) + (remedy is null ? "" : " " + remedy);
    }

    /// <summary>The shape half of <see cref="Shaped"/> alone, for a lane refusing over a plugin the caller did not name. THE RENDER SEAM.</summary>
    public static string ShapeBody(LocalizedAssessment a) => a.Shape switch
    {
        // NOT AN ARRANGEMENT: the file was never opened, so nothing is claimed about localization.
        LocalizedShape.Unreadable => UnreadableText + SettledUnreadable,

        LocalizedShape.NotLocalized
            or LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
            or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly or LocalizedShape.StringsFolderUnreadable
            or LocalizedShape.ModFolderUnreadable or LocalizedShape.Nowhere
            => WhereTheTextIs(a) + " " + WhyNotInPlace(a),

        // Enumerated above one by one, so a shape added later lands HERE and says nothing rather than inheriting.
        _ => "houseCARL has no wording for the arrangement this plugin classified into, so it will not describe it — "
             + "and it does not write in place against a destination it cannot describe.",
    };

    /// <summary>Which remedy a refusal ends on — per shape, and an UNREADABLE destination gets the one that matches what actually happened.</summary>
    static string? RemedyFor(LocalizedAssessment a, string? laneClause) => a.Shape switch
    {
        LocalizedShape.Unreadable => RemedyUnreadable,
        _ => laneClause,
    };

    /// <summary>The closing sentence every LOCALIZED shape ends on. NOT shared with <see cref="SettledUnreadable"/>.</summary>
    const string Settled = " It does not edit a localized plugin in place.";

    /// <summary>The close for a destination houseCARL could not open — <see cref="Settled"/> would claim it IS localized.</summary>
    const string SettledUnreadable =
        " houseCARL does not write to a destination it cannot classify. Nothing here says the file is or is not "
        + "localized — it was never opened, so neither was established.";

    /// <summary>What houseCARL can say about a file it could not open, and the only thing it can say; one home.</summary>
    const string UnreadableText = "houseCARL could not read the file at that path to see where its text lives.";

    /// <summary>The remedy for an unreadable destination, measured against the open that failed. Names no lane.</summary>
    public const string RemedyUnreadable =
        "Check whether something else has the file open — Mod Organizer refreshing, an antivirus scan, xEdit, the "
        + "running game — or whether that path names a file that exists at all, and retry once it is free.";

    /// <summary>Why this ARRANGEMENT cannot be rewritten in place — per shape, because a live set beside the plugin and a set elsewhere fail differently.</summary>
    public static string WhyNotInPlace(LocalizedAssessment a) => a.Shape switch
    {
        // A live set sits beside the plugin; a rewrite would have to replace those exact files in the same breath.
        LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate =>
            "A localized plugin's text is not in the plugin, so rewriting the plugin renumbers the indices its text is "
            + "looked up by and those .STRINGS files beside it would have to be replaced in the same breath — and "
            + "houseCARL cannot swap a plugin and its tables as one operation, so an interruption would leave records "
            + "reading text that belongs to other records." + Settled,

        // The files the indices point at are out of a plugin write's reach, so a new set beside it only shadows them.
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

        // The folder is there and could not be listed, so the ONE thing this shape cannot say is what is in it.
        LocalizedShape.StringsFolderUnreadable =>
            "A localized plugin's text is not in the plugin, and houseCARL could not read the Strings folder beside it "
            + "to see what is in there. Rewriting the plugin renumbers the indices its text is looked up by, and "
            + "houseCARL cannot tell whether the files written beside it would replace a set that is already there or "
            + "land next to one it never saw." + Settled,

        // The same, one level up: the folder holding the plugin would not list, so nothing beside it was established.
        LocalizedShape.ModFolderUnreadable =>
            "A localized plugin's text is not in the plugin, and houseCARL could not read the folder the plugin sits "
            + "in to see what is beside it. Rewriting the plugin renumbers the indices its text is looked up by, and "
            + "houseCARL cannot tell whether the files written beside it would replace a set that is already there, "
            + "shadow one it never saw, or sit next to an archive it could not look in." + Settled,

        // houseCARL cannot see the source, so it names the hazard it cannot rule out rather than one it verified.
        LocalizedShape.Nowhere =>
            "A localized plugin's text is not in the plugin, and houseCARL cannot see the files its indices point at. "
            + "Rewriting the plugin renumbers those indices, and houseCARL cannot tell whether a set written beside it "
            + "would replace what the game reads or shadow it, nor what would be left stale either way." + Settled,

        // The mod being written IS localized and the file already at that path is not; no arrangement is claimed.
        LocalizedShape.NotLocalized =>
            "A localized plugin's text is not in the plugin, and houseCARL could not establish where this one's is, so "
            + "it cannot tell what rewriting the plugin would do to that text." + Settled,

        // ShapeBody has its own arm; this one exists so a DIRECT caller gets no localized reasoning for an unread file.
        LocalizedShape.Unreadable =>
            "houseCARL could not open the file at that path, so it cannot tell what rewriting it would do to any text "
            + "it carries." + SettledUnreadable,

        // Enumerated above; a shape added later fails LOUD and generic, claiming no localization state of its own.
        _ => "houseCARL has no account of this plugin's arrangement, so it cannot say what rewriting it in place "
             + "would do to its text.",
    };

    /// <summary>Where the archive naming this plugin's tables was found — beside the plugin, or in the game folder.</summary>
    static string BsaWhere(LocalizedAssessment a) => a.BsaInGameData ? "in your game's Data folder" : "beside the plugin";

    /// <summary>What the <c>Strings\</c> folder beside the plugin holds, for a plugin nothing in it matched; it asserts no unchecked absence.</summary>
    static string NothingMatched(LocalizedAssessment a)
    {
        var u = a.UnmatchedTables;
        // A CHECKED absence: an unlistable folder classifies as StringsFolderUnreadable and never arrives here.
        if (u.Total == 0)
            return "no .STRINGS files beside it";
        // The COUNT is the folder's and the NAMES are only what fits, so what is on disk is never understated.
        return "the Strings folder beside it holds " + u.Total + " .STRINGS file(s) — "
             + string.Join(", ", u.Names)
             + (u.Unnamed > 0 ? ", and " + u.Unnamed + " more" : "")
             + " — but houseCARL matched none of them to this plugin in a language it recognises";
    }

    /// <summary>The SECOND location, when the archive shape carries one — naming one while two are on disk misleads.</summary>
    static string AlsoLoose(LocalizedAssessment a)
    {
        var also = new List<string>();
        if (a.Languages.Count > 0)
            also.Add(".STRINGS files for it in a Strings folder beside the plugin (" + string.Join(", ", a.Languages) + ")");
        if (a.GameDataLanguages.Count > 0)
            also.Add("a set for it in your game's Data\\Strings folder (" + string.Join(", ", a.GameDataLanguages) + ")");
        return also.Count == 0 ? "" : " — and there is also " + string.Join(", and ", also);
    }

    /// <summary>Where THIS plugin's text actually lives. Every arm states only what was checked and found.</summary>
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

            // WHERE the archive is, not merely its name; AlsoLoose names the OTHER location when one exists.
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

            // The folder is THERE, and nothing is claimed about its contents, so it is not described as empty.
            LocalizedShape.StringsFolderUnreadable =>
                "It is flagged LOCALIZED and there is a Strings folder beside it that houseCARL could not read, so "
                + "whether its text is in there — and in which languages — is unknown" + AlsoLoose(a) + ".",

            // The MOD folder is the one that would not list, so it asserts no absence — the reason it is not Nowhere.
            LocalizedShape.ModFolderUnreadable =>
                "It is flagged LOCALIZED and houseCARL could not read the folder the plugin sits in, so whether its "
                + "text is beside it — loose, or inside an archive there — is unknown" + AlsoLoose(a) + ".",

            // Says what was SEARCHED and what was FOUND, never what exists: a Strings folder may hold a neighbour's
            // tables, or this plugin's in a language Mutagen does not model, and neither is "nothing is there".
            LocalizedShape.Nowhere =>
                "It is flagged LOCALIZED and houseCARL cannot find its text: " + NothingMatched(a)
                + ", and no archive beside it"
                + (a.GameDataUnknown ? "" : " or in your game folder")
                + " carrying its tables"
                + (a.GameDataUnknown ? ", and your game's Data folder could not be determined, so it was not searched" : "")
                + ". Mod Organizer merges mod folders when the game runs, so the text may well exist in a different "
                + "mod folder than the plugin — houseCARL reads the folders as they sit on disk and does not see that "
                + "merge.",

            // The plugin could not be read at all, so nothing is claimed — and it REFUSES rather than proceed.
            LocalizedShape.Unreadable => UnreadableText,

            // The mod being written is flagged LOCALIZED and the file already at that path does not read as one.
            LocalizedShape.NotLocalized =>
                "It is flagged LOCALIZED, but the file already at that path does not read as a localized plugin, so "
                + "houseCARL could not establish where the text being written would resolve from.",

            // Enumerated above; a shape added later fails LOUD and generic, asserting NO localization state.
            _ => "houseCARL has no account of where this plugin's text lives.",
        };
    }

    /// <summary>What the three lanes with a new-plugin equivalent append; "a NEW plugin", because create shares it.</summary>
    public const string RemedyDefaultLane =
        "This is the in-place lane only: drop in_place= and houseCARL writes the same change into a NEW plugin instead, " +
        "leaving this file untouched.";

    /// <summary>Remove's clause. It names NO remedy, because a new plugin cannot un-define a record.</summary>
    public const string RemoveNoEquivalent =
        "This lane has no new-plugin form: a separate plugin can override a record but cannot un-define one, so there is " +
        "no way to remove this record without rewriting the plugin that defines it.";

    /// <summary>Where this plugin's strings are, as a clause a lane refusing for its OWN reason can drop in.</summary>
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
        // The same absence discipline as WhereTheTextIs's arm: it says it could not FIND them, never that none exist.
        LocalizedShape.Nowhere =>
            "It is flagged LOCALIZED and houseCARL cannot find its .STRINGS files: " + NothingMatched(a) + ".",
        LocalizedShape.StringsFolderUnreadable =>
            "It is flagged LOCALIZED and houseCARL could not read the Strings folder beside it, so where its text "
            + "lives is unknown.",
        LocalizedShape.ModFolderUnreadable =>
            "It is flagged LOCALIZED and houseCARL could not read the folder the plugin sits in, so where its text "
            + "lives is unknown.",
        LocalizedShape.Unreadable =>
            "houseCARL could not read it to see whether it is localized or where its text lives.",
        // Asserts no localization state, for the same reason WhereTheTextIs' arm does not — the render seam.
        _ =>
            "houseCARL has no account of where its text lives.",
    };

    /// <summary>Throw a sentence that is already whole — <see cref="Shaped"/>'s. The ONLY way to build this exception.</summary>
    public static LocalizedTargetUnsupportedException FromSentence(string message) => new(message);

    LocalizedTargetUnsupportedException(string message) : base(message) { }
}

/// <summary>A refusal whose cause is LIVE collection/record STATE the schema-only pre-flight cannot see — occupancy,
/// length, a Remove that removes nothing, a navigation into an absent collection, key or index — so it renders without
/// the gate/apply-inconsistency wrapper. A bad-SHAPE index and a present-but-null element are the other two.</summary>
public sealed class ExpectedApplyRejectionException : InvalidOperationException
{
    public ExpectedApplyRejectionException(string message) : base(message) { }
}

/// <summary>A refusal whose cause is the TARGET record's own malformed data — a present-but-null element or entry.
/// The THIRD apply-rejection category: no input for the user to fix, and not an engine bug either, so it renders
/// cleanly without the wrapper. houseCARL never writes that state, so it comes from pre-existing plugins.</summary>
public sealed class MalformedTargetDataException : InvalidOperationException
{
    public MalformedTargetDataException(string message) : base(message) { }
}
