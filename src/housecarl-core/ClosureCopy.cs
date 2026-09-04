using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

// ClosureCopy — internalize a ClosureWalk's reached set into a patch under fresh keys, then make
// the artifact honest about what it still points at.
//
// Copies whole records via Mutagen's Duplicate(newKey) + RemapLinks through RemapEngine, never
// field-by-field: Duplicate carries every field, so no field can be forgotten.
//
// The scratch-mod step is a correctness requirement, not an optimization: RenumberRecordsInto
// remaps links over the WHOLE target mod, which would silently repoint a record the user already
// put in an EXTENDED patch. Duplicates are built in a scratch mod sharing the patch's ModKey (so
// allocated keys are final) and transplanted in; the patch's own records never reach a remap.
//
// The strip is generic: after internalize + remap, any link still pointing into the bound source
// universe is by definition not part of what was copied, and is removed by link identity with
// every removal named. A REQUIRED link it cannot clear is a loud refusal — keeping it would
// master the very plugin the artifact claims to be free of.

/// <summary>One record internalized: where it came from, where it landed, and which source arm produced the body
/// that was copied. The arm attribution must survive the copy — without it the report cannot say which source a
/// record came from.</summary>
public sealed record CopiedRecord(
    FormKey OldKey, FormKey NewKey, string TypeName, string? EditorId,
    int ArmIndex, string ArmSpelling, string PulledBy);

/// <summary>One stripped link, or one attached seed: the field it sat on (with index, for a list) and what was
/// removed or written.
/// <para><paramref name="Cleared"/> marks an attach whose source carried nothing, so the target's own value was
/// cleared rather than overwritten. A clear and a skip are indistinguishable to the caller afterwards but have
/// opposite consequences, so the clear is reported rather than left silent.</para>
/// <para><paramref name="WholeProperty"/> marks a strip that nulled an ENTIRE property to remove the link(s) named,
/// rather than removing just them — nulling <c>VirtualMachineAdapter</c> to clear one bound script property drops
/// every script on the clone. The render must distinguish the two; naming only the offending link understates the
/// cost by the whole rest of the property.</para></summary>
public sealed record StripEntry(string Field, string Removed, bool Cleared = false, bool WholeProperty = false);

/// <summary>Why an internalize/strip refused.</summary>
public enum CopyRefusalKind
{
    /// <summary>A REQUIRED (non-nullable, non-list) link into the bound universe. Clearing it would invent data;
    /// keeping it would silently master the source.</summary>
    RequiredForeignLink,
    /// <summary>A link-bearing substruct carrying a bound link that the record model will not let us clear.</summary>
    UnclearableSubstruct,
    /// <summary>The patch's object-ID counter is past the 24-bit ceiling.</summary>
    IdExhausted,
    /// <summary>Duplicate/renumber failed, or a duplicate could not be placed in the patch.</summary>
    Transplant,
    /// <summary>A link into the bound universe survived on the attach target after the remap.</summary>
    DonorLeak,
    /// <summary>A seed path names a field whose SHAPE the attach lane does not support — a list of link-bearing
    /// elements, or a target property the lane cannot write. Refused by name, never silently no-oped or
    /// emptied.</summary>
    UnsupportedSeedShape,
    /// <summary>A <c>Type:stop</c> exclusion pruned a record that lives OFF the active load order. The artifact
    /// would have to master a plugin the game does not load, which cannot be serialized at all — so it refuses up
    /// front with both remedies rather than throwing out of the writer.</summary>
    StopOffOrder,
    /// <summary>The target record lives in a NESTED group (a placed reference, a cell, a dialog response). Ordinary
    /// caller input, so it is a typed refusal naming the shape — not a throw rendered as an internal fault.</summary>
    UnsupportedTargetShape,
    /// <summary>A record ALREADY in the patch — put there by an earlier call, reached through <c>into=</c> — links
    /// to a plugin the active order does not carry, so the patch cannot be serialized. Nothing this call did caused
    /// it, so it must not borrow <see cref="StopOffOrder"/>'s sentence, which names an <c>exclude_types</c> prune
    /// the caller never asked for.</summary>
    PatchOffOrderLink,
    /// <summary>A record THIS call copied carries a link to a plugin the active order does not carry, and no
    /// exclusion pruned it — it sits on a field <c>seed_paths</c> never named, so the walk never saw it and it was
    /// duplicated across as-is. Distinct because the remedy is the caller's seed set, not their exclusions or
    /// their patch.</summary>
    CopiedOffOrderLink,
    /// <summary>The seed's shape is SUPPORTED and the TARGET's property cannot take it — unset and read-only, or a
    /// required link that cannot be cleared. Distinct from <see cref="UnsupportedSeedShape"/> because the remedy
    /// differs: the caller's seed path is legal and the fault is the target.</summary>
    UnwritableTarget,
}

/// <summary>A refusal as data — the render owns the words.</summary>
public sealed record CopyRefusal(CopyRefusalKind Kind, string Detail, string? Field = null, FormKey Key = default);

/// <summary>The internalize outcome. A refusal carries nothing usable.</summary>
public sealed record CopyResult(
    bool Success, CopyRefusal? Refusal,
    IReadOnlyList<CopiedRecord> Copied,
    IReadOnlyDictionary<FormKey, FormKey> Map)
{
    public static CopyResult Fail(CopyRefusal r) =>
        new(false, r, Array.Empty<CopiedRecord>(), new Dictionary<FormKey, FormKey>());
}

/// <summary>The strip outcome: what was removed (each named), or a loud refusal.</summary>
public sealed record StripResult(bool Success, CopyRefusal? Refusal, IReadOnlyList<StripEntry> Stripped)
{
    public static StripResult Fail(CopyRefusal r) => new(false, r, Array.Empty<StripEntry>());
}

public static class ClosureCopy
{
    /// <summary>Marks a post-attach leak whose key an EXCLUSION pruned rather than one the target already carried.
    /// The two need different remedies — the generic sentence sends the caller to inspect a target that never had
    /// the reference. Carried as data so core states the cause and the tool layer owns the words.</summary>
    public const string ExclusionLeakMarker = "<pruned-by-exclusion>";

    /// <summary>
    /// Internalize a walk's reached set into <paramref name="patch"/> under fresh local keys.
    /// <para>Allocation comes off the patch's own counter, so an EXTENDED patch keeps counting from where it was.
    /// The duplicates are built in a scratch mod and transplanted — the file header says why that is a correctness
    /// step, not a performance one.</para>
    /// </summary>
    public static CopyResult Internalize(SkyrimMod patch, IReadOnlyList<WalkNode> nodes)
    {
        WriteEngine.EnsureFormIdFloor(patch);

        var map = new Dictionary<FormKey, FormKey>();
        uint next = patch.ModHeader.Stats.NextFormID;
        foreach (var n in nodes)
        {
            if (FormIdRange.ObjectIdSpaceExhausted(next))
                return CopyResult.Fail(new CopyRefusal(CopyRefusalKind.IdExhausted,
                    $"the patch's NextObjectID counter is past 0x{FormIdRange.ObjectIdMax:X}", Key: n.Key));
            map[n.Key] = new FormKey(patch.ModKey, next++);
        }
        patch.ModHeader.Stats.NextFormID = next;

        // Duplicate + remap in a SCRATCH mod sharing the patch's ModKey, then transplant. The patch's own
        // pre-existing records are never passed to a remap.
        var scratch = new SkyrimMod(patch.ModKey, SkyrimRelease.SkyrimSE);
        var ren = RemapEngine.RenumberRecordsInto(scratch, nodes.Select(n => n.Body), map);
        if (!ren.Success)
            return CopyResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant, ren.Error!));

        foreach (var rec in scratch.EnumerateMajorRecords())
            if (!RemapEngine.TryAddToFlatGroup(patch, (IMajorRecord)rec))
                return CopyResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant,
                    $"{RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey} could not be placed in the patch",
                    Key: rec.FormKey));

        var copied = nodes.Select(n => new CopiedRecord(
            n.Key, map[n.Key], n.TypeName, n.EditorId, n.ArmIndex, n.ArmSpelling, n.PulledBy)).ToList();
        return new CopyResult(true, null, copied, map);
    }

    /// <summary>
    /// Remove every link on <paramref name="record"/> for which <paramref name="isBound"/> holds — after an
    /// internalize + remap those are exactly the references that were NOT part of what was copied, and leaving one
    /// would master the plugin the artifact claims to be free of. One rule per shape:
    ///   nullable link → null · list of links → drop the entries · list of link-BEARING elements → drop those
    ///   elements · nullable link-bearing substruct → null the property · a REQUIRED link → loud refusal.
    /// Every removal is reported by field name (and index, for a list).
    /// </summary>
    /// <para><b>Two passes, and the first one does not touch the record.</b> Pass 1 looks for a shape this strip
    /// cannot clear — a required bound link, an unclearable substruct — and refuses before anything is removed.
    /// Only a clean scan runs pass 2, which mutates. A single mutating pass would refuse part-way through with the
    /// record already half-stripped, leaving a state no call intended for a caller that retries or inspects it.</para>
    public static StripResult StripBoundLinks(IMajorRecord record, Func<FormKey, bool> isBound)
    {
        if (ScanForUnstrippable(record, isBound) is { } blocked) return StripResult.Fail(blocked);
        return ApplyStrip(record, isBound);
    }

    /// <summary>Pass 1 — read-only: is there a bound link this strip could not remove? Returns the refusal, or null.</summary>
    static CopyRefusal? ScanForUnstrippable(IMajorRecord record, Func<FormKey, bool> isBound)
    {
        foreach (var prop in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            if (prop.Name is "FormKey" or "EditorID") continue;
            object? val;
            try { val = prop.GetValue(record); } catch { continue; }
            if (val is null) continue;

            if (val is IFormLinkGetter singleLink)
            {
                if (singleLink.FormKeyNullable is not { } fk || fk.IsNull || !isBound(fk)) continue;
                if (!IsNullableLink(val))
                    return new CopyRefusal(CopyRefusalKind.RequiredForeignLink,
                        $"the record's REQUIRED field '{prop.Name}' points at {fk}, which is in the source universe " +
                        "being copied away from: it cannot be nulled without inventing data, and keeping it would " +
                        "silently master that plugin", prop.Name, fk);
                continue;
            }
            if (val is IList || val is string) continue;
            if (val is IFormLinkContainerGetter sub && !prop.CanWrite)
            {
                var keys = sub.EnumerateFormLinks()
                    .Where(l => !l.FormKey.IsNull && isBound(l.FormKey))
                    .Select(l => l.FormKey.ToString()).Distinct().ToList();
                if (keys.Count > 0)
                    return new CopyRefusal(CopyRefusalKind.UnclearableSubstruct,
                        $"the record's field '{prop.Name}' carries reference(s) into the source universe " +
                        $"({string.Join(", ", keys)}) and the property cannot be cleared", prop.Name);
            }
        }
        return null;
    }

    /// <summary>Pass 2 — the mutating pass, reached only after a clean scan.</summary>
    static StripResult ApplyStrip(IMajorRecord record, Func<FormKey, bool> isBound)
    {
        var stripped = new List<StripEntry>();
        foreach (var prop in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            if (prop.Name is "FormKey" or "EditorID") continue;
            object? val;
            try { val = prop.GetValue(record); } catch { continue; }
            if (val is null) continue;

            // 1. A single link property, nullable or required.
            if (val is IFormLinkGetter singleLink)
            {
                if (singleLink.FormKeyNullable is not { } fk || fk.IsNull || !isBound(fk)) continue;
                // Pass 1 has already proven this link is nullable; the else-branch is a backstop against the two
                // passes ever disagreeing, not the primary refusal path.
                if (TryNullLink(val))
                    stripped.Add(new StripEntry(prop.Name, fk.ToString()));
                else
                    return StripResult.Fail(new CopyRefusal(CopyRefusalKind.RequiredForeignLink,
                        $"the record's REQUIRED field '{prop.Name}' points at {fk}, which is in the source universe " +
                        "being copied away from: it cannot be nulled without inventing data, and keeping it would " +
                        "silently master that plugin", prop.Name, fk));
                continue;
            }

            // 2. A list — of links directly, or of link-bearing elements.
            if (val is IList list && val is not string)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var el = list[i];
                    if (el is IFormLinkGetter elLink)
                    {
                        if (elLink.FormKeyNullable is { } lk && !lk.IsNull && isBound(lk))
                        { list.RemoveAt(i); stripped.Add(new StripEntry($"{prop.Name}[{i}]", lk.ToString())); }
                    }
                    else if (el is IFormLinkContainerGetter elc)
                    {
                        var keys = elc.EnumerateFormLinks()
                            .Where(l => !l.FormKey.IsNull && isBound(l.FormKey))
                            .Select(l => l.FormKey.ToString()).Distinct().ToList();
                        if (keys.Count > 0)
                        { list.RemoveAt(i); stripped.Add(new StripEntry($"{prop.Name}[{i}]", string.Join(", ", keys))); }
                    }
                }
                continue;
            }

            // 3. A link-bearing substruct: any bound link inside it takes the whole (optional) property.
            if (val is IFormLinkContainerGetter sub)
            {
                var keys = sub.EnumerateFormLinks()
                    .Where(l => !l.FormKey.IsNull && isBound(l.FormKey))
                    .Select(l => l.FormKey.ToString()).Distinct().ToList();
                if (keys.Count == 0) continue;
                // Nulling the property removes EVERYTHING on it, not just the bound link(s) that forced the removal.
                // Marked so the render can say so: for VirtualMachineAdapter this is every script on the clone.
                if (prop.CanWrite)
                { prop.SetValue(record, null); stripped.Add(new StripEntry(prop.Name, string.Join(", ", keys), WholeProperty: true)); }
                else
                    return StripResult.Fail(new CopyRefusal(CopyRefusalKind.UnclearableSubstruct,
                        $"the record's field '{prop.Name}' carries reference(s) into the source universe " +
                        $"({string.Join(", ", keys)}) and the property cannot be cleared", prop.Name));
            }
        }
        return new StripResult(true, null, stripped);
    }

    /// <summary>Null a single link's key iff the link is genuinely NULLABLE — judged on the RECORD MODEL's
    /// <c>IFormLinkNullable&lt;T&gt;</c>, never on whether a <c>SetToNull</c> method exists.
    /// <para>Mutagen's REQUIRED <c>FormLink&lt;T&gt;</c> also exposes <c>SetToNull</c>, so deciding by method
    /// presence makes the required-link refusal dead code and silently writes a null into a required field (a
    /// record shipped with <c>Class=00000000</c>). Only the interface says whether null is legal for the
    /// field.</para>
    /// Returns false for a required link, which the caller escalates to the loud refusal.</summary>
    static bool TryNullLink(object link)
    {
        if (!IsNullableLink(link)) return false;
        var m = link.GetType().GetMethod("SetToNull", Type.EmptyTypes);
        if (m is null) return false;
        m.Invoke(link, null);
        return true;
    }

    /// <summary>The non-mutating half of the same judgement, so pass 1 can decide without touching the record.
    /// Judged on the interface, never on method presence — see <see cref="TryNullLink"/>.</summary>
    static bool IsNullableLink(object link) => link.GetType().GetInterfaces().Any(i =>
        i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkNullable<>));

    /// <summary>
    /// ATTACH — set the walked SEED fields on <paramref name="target"/> from <paramref name="source"/>, substituting
    /// the internalized keys. This is the link-bearing half of "copy the appearance onto an existing NPC": the
    /// inline fields (tints, morphs, weights) are a field-bundle copy the `apply` zip already does. Only the seed
    /// paths are touched here, so everything outside them is untouched by construction.
    /// <para><b>The remap is confined to the target record</b> — links are written already-mapped rather than
    /// written raw and fixed up by a mod-wide pass, so a patch record the caller never named is unreachable from
    /// this operation. Same property the scratch-mod step gives <see cref="Internalize"/>.</para>
    /// <para>A target inside the bound universe REFUSES: standalone-izing a record onto itself is incoherent — the
    /// artifact would master the very plugin it is being freed from, and the "target" and the thing being removed
    /// are the same record.</para>
    /// </summary>
    public static StripResult AttachSeedFields(
        IMajorRecord target, IMajorRecordGetter source, IReadOnlyList<string> seedPaths,
        IReadOnlyDictionary<FormKey, FormKey> map, Func<FormKey, bool> isBound)
    {
        if (isBound(target.FormKey))
            return StripResult.Fail(new CopyRefusal(CopyRefusalKind.DonorLeak,
                "the target record lives in the source universe being copied away from — it cannot be " +
                "standalone-ized onto itself", Key: target.FormKey));

        var set = new List<StripEntry>();
        var srcType = source.GetType();
        foreach (var path in seedPaths)
        {
            var sp = srcType.GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
            var tp = target.GetType().GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
            if (sp is null || tp is null)
                return StripResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant,
                    $"'{path}' is not a field on both the source and the target", path));

            object? sv, tv;
            try { sv = sp.GetValue(source); tv = tp.GetValue(target); }
            catch (Exception ex)
            {
                return StripResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant,
                    $"'{path}' could not be read: {ex.Message}", path));
            }

            FormKey Mapped(FormKey k) => map.TryGetValue(k, out var n) ? n : k;

            // Shape is decided by ClassifySeed, never re-judged here — this lane and the clone lane must give the
            // same verdict for the same field, or one refuses what the other silently skips.
            var shape = ClosureWalk.ClassifySeed(sp);
            if (shape.Kind == SeedShapeKind.Unsupported)
                return StripResult.Fail(new CopyRefusal(CopyRefusalKind.UnsupportedSeedShape, shape.Reason!, path));

            if (shape.Kind == SeedShapeKind.Link)
            {
                if (tv is null)
                    return StripResult.Fail(new CopyRefusal(CopyRefusalKind.UnwritableTarget,
                        $"'{path}' is a record link on the source but the target's is not writable", path));
                var key = (sv as IFormLinkGetter)?.FormKeyNullable;
                // An UNSET source link CLEARS the target's: leaving the target's own value would build a face out
                // of two records. Null is a state of this shape, not a reason to refuse it.
                if (key is null || key.Value.IsNull)
                {
                    if (!TryClearLink(tv))
                        return StripResult.Fail(new CopyRefusal(CopyRefusalKind.UnwritableTarget,
                            $"'{path}' is unset on the source and the target's is REQUIRED, so it cannot be cleared " +
                            "without inventing data", path));
                    set.Add(new StripEntry(path, "cleared", Cleared: true));
                    continue;
                }
                if (!TrySetLink(tv, Mapped(key.Value)))
                    return StripResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant,
                        $"'{path}' could not be set on the target", path));
                set.Add(new StripEntry(path, Mapped(key.Value).ToString()));
                continue;
            }

            // LinkList. Read the source BEFORE touching the target, so a failure leaves the target untouched.
            var mapped = new List<FormKey>();
            if (sv is IEnumerable sList and not string)
                foreach (var el in sList)
                    if (el is IFormLinkGetter l && l.FormKeyNullable is { } lk && !lk.IsNull) mapped.Add(Mapped(lk));

            // The target's list may itself be null — Mutagen models these non-nullable and the overlay hands back
            // null when the subrecord is absent. That is a list carrying nothing, so it is assigned INTO rather
            // than refused; only a property that cannot be given one at all is a refusal.
            if (tv is not IList tList)
            {
                if (mapped.Count == 0) { set.Add(new StripEntry(path, "cleared", Cleared: true)); continue; }
                if (!tp.CanWrite || MakeList(tp.PropertyType) is not { } fresh)
                    return StripResult.Fail(new CopyRefusal(CopyRefusalKind.UnwritableTarget,
                        $"'{path}' is a list of record links but the target's cannot be written " +
                        $"({(tv is null ? "it is unset and the property is read-only" : tv.GetType().Name)})", path));
                tp.SetValue(target, fresh);
                tList = fresh;
            }

            var made = mapped.Select(k => MakeLink(tList.GetType(), k)).ToList();
            if (made.Any(m => m is null))
                return StripResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant,
                    $"'{path}' element could not be constructed on the target", path));

            tList.Clear();
            foreach (var m in made) tList.Add(m);
            // An EMPTY or absent source list clears the target's, as the unset link above does. Reported as
            // CLEARED, not "0 link(s)", which reads as a no-op.
            set.Add(mapped.Count == 0
                ? new StripEntry(path, "cleared", Cleared: true)
                : new StripEntry(path, $"{mapped.Count} link(s)"));
        }
        return new StripResult(true, null, set);
    }

    /// <summary>Clear a single link property. A REQUIRED link has no legal null, so this returns false and the
    /// caller refuses rather than writing an invented zero. Judged on the record model, not on whether a
    /// <c>SetToNull</c> method exists — the same rule as the clone lane's strip.</summary>
    static bool TryClearLink(object linkObj)
    {
        if (!IsNullableLink(linkObj)) return false;
        var m = linkObj.GetType().GetMethod("SetToNull", Type.EmptyTypes);
        if (m is null) return false;
        m.Invoke(linkObj, null);
        return true;
    }

    /// <summary>Set a single link property's key, whichever of the required/nullable SetTo overloads it carries.</summary>
    static bool TrySetLink(object linkObj, FormKey key)
    {
        foreach (var m in linkObj.GetType().GetMethods().Where(x => x.Name == "SetTo"))
        {
            var ps = m.GetParameters();
            if (ps.Length != 1) continue;
            if (ps[0].ParameterType == typeof(FormKey)) { m.Invoke(linkObj, new object?[] { key }); return true; }
            if (ps[0].ParameterType == typeof(FormKey?)) { m.Invoke(linkObj, new object?[] { (FormKey?)key }); return true; }
        }
        return false;
    }

    /// <summary>Construct an empty list for a target property whose own is null. Returns null when the declared
    /// type cannot be instantiated (an interface, typically), which the caller reports by name rather than
    /// swallowing — a target we cannot give a list to is a refusal, not a silent skip.</summary>
    static IList? MakeList(Type propertyType)
    {
        try { return System.Activator.CreateInstance(propertyType) as IList; }
        catch { return null; }
    }

    /// <summary>Build a concrete <c>FormLink&lt;T&gt;</c> for a list whose element type is <c>IFormLinkGetter&lt;T&gt;</c>
    /// (or already <c>FormLink&lt;T&gt;</c>) — the reflection counterpart of writing <c>new FormLink&lt;IHeadPartGetter&gt;(key)</c>
    /// without knowing T. Returns null when the list is not link-typed, which the caller reports by name.</summary>
    static object? MakeLink(Type listType, FormKey key)
    {
        var element = listType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();
        if (element is null) return null;
        var inner = element.IsGenericType ? element.GetGenericArguments().FirstOrDefault() : null;
        if (inner is null) return null;
        // System.Activator, spelled out: Mutagen.Bethesda.Skyrim has an ACTI record type called Activator.
        try { return System.Activator.CreateInstance(typeof(FormLink<>).MakeGenericType(inner), key); }
        catch { return null; }
    }

    /// <summary>
    /// Build the copy into a patch at <paramref name="outPath"/> and serialize it: internalize the walk's reached
    /// set, run the mode lane (attach to a target / mint a clone and strip it), write multi-master, read the
    /// header back. Core owns records and serialization; the service owns lanes, folders and MO2 and hands in the
    /// master set through <paramref name="mastersFor"/>.
    /// <para>An IN-PATCH target (its FormID names the patch) is resolved HERE, off the opened patch mod:
    /// <paramref name="targetActiveBody"/> is null in that case by construction, because the patch is not in the
    /// load order and resolving it there would find nothing — or, on an extended patch, a body missing the very
    /// edits this call is adding.</para>
    /// <para>Every refusal returns with nothing usable written; a post-commit read-back failure is a WARNING on a
    /// success, never a refusal — the patch IS on disk by then, and mislabelling it invites a duplicate
    /// re-run.</para></summary>
    public static ClosureCopyOutcome BuildAndWrite(
        string outPath, bool extend,
        FormKey sourceKey, SourceHit srcHit,
        WalkResult walk, IReadOnlyList<string> seedPaths,
        FormKey? targetKey, IMajorRecordGetter? targetActiveBody,
        string? newEditorid,
        Func<FormKey, bool> isBound, IReadOnlySet<ModKey> boundPlugins, bool nothingBound,
        Func<ModKey, bool> isOnOrder,
        Func<string, IReadOnlyList<ISkyrimModGetter>> mastersFor,
        IReadOnlyList<string> consulted,
        Func<Exception, string>? serializeFailure = null)
    {
        var patchFileName = Path.GetFileName(outPath);
        var patchModKey = ModKey.FromFileName(patchFileName);
        SkyrimMod patch;
        if (extend)
        {
            try { patch = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { return ClosureCopyOutcome.Fail(engine: ex.Message, sources: consulted); }
        }
        else patch = new SkyrimMod(patchModKey, SkyrimRelease.SkyrimSE);

        // A pruned link must still be MASTERABLE. Masters come from the active order, so a `stop` on a record in an
        // off-order plugin cannot be serialized at all (the writer throws MissingModException). Checked before
        // anything is built, so the refusal writes nothing.
        //
        // Scoped per lane to links that survive into the artifact. In the ATTACH lane nothing strips, so every
        // excluded boundary survives and this up-front check is right. The CLONE lane strips bound links off the
        // clone, so some boundaries are gone before the writer sees them; that lane is checked AFTER the strip,
        // against the artifact itself, below.
        var cloneLane = targetKey is null;
        if (!cloneLane && walk.Kept.FirstOrDefault(k => k.Excluded && !isOnOrder(k.Key.ModKey)) is { } offOrder)
            return ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.StopOffOrder,
                    offOrder.Key.ModKey.FileName.String, Key: offOrder.Key),
                sources: consulted);

        var copy = Internalize(patch, walk.Reached);
        if (!copy.Success) return ClosureCopyOutcome.Fail(copy: copy.Refusal, sources: consulted);

        string mode; FormKey newKey;
        IReadOnlyList<StripEntry> attached = Array.Empty<StripEntry>(), stripped = Array.Empty<StripEntry>();

        if (targetKey is { } tk)
        {
            mode = "attach";
            newKey = tk;
            IMajorRecord target;
            if (tk.ModKey == patchModKey)
            {
                var inPatch = patch.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == tk);
                if (inPatch is null)
                    return ClosureCopyOutcome.Fail(
                        copy: new CopyRefusal(CopyRefusalKind.Transplant, "this patch defines no such record", Key: tk),
                        sources: consulted);
                target = (IMajorRecord)inPatch;
            }
            else if (targetActiveBody is not null)
            {
                try { target = WriteEngine.GenericGetOrAddAsOverride(patch, targetActiveBody); }
                catch (Exception ex)
                {
                    // A placed reference, a cell, a dialog response — records in NESTED groups. Ordinary caller
                    // input, so it is a named refusal; letting it throw would render as an internal failure.
                    return ClosureCopyOutcome.Fail(
                        copy: new CopyRefusal(CopyRefusalKind.UnsupportedTargetShape,
                            RecordNaming.StripOverlay(targetActiveBody.GetType().Name), Key: tk),
                        sources: consulted);
                }
            }
            else
                return ClosureCopyOutcome.Fail(
                    copy: new CopyRefusal(CopyRefusalKind.Transplant, "no target body was supplied", Key: tk),
                    sources: consulted);

            var att = AttachSeedFields(target, srcHit.Body, seedPaths, copy.Map, isBound);
            if (!att.Success) return ClosureCopyOutcome.Fail(copy: att.Refusal, sources: consulted);
            attached = att.Stripped;

            if (FindBoundLeak(target, isBound) is { } leak)
            {
                // Why it leaked decides the remedy: a key an exclusion pruned was attached unmapped by this call,
                // so blaming the target would send the caller to edit a record that never had it.
                var fromExclusion = walk.Kept.Any(k => k.Excluded && k.Key == leak);
                return ClosureCopyOutcome.Fail(
                    copy: new CopyRefusal(CopyRefusalKind.DonorLeak,
                        "a link into the source universe survived on the target",
                        fromExclusion ? ExclusionLeakMarker : null, leak),
                    sources: consulted);
            }
        }
        else
        {
            mode = "clone";
            // A walk that cycles back to the `from` record has ALREADY internalized it; minting a second duplicate
            // would leave a stray unreferenced copy in the patch sharing the clone's EditorID. Reuse the walk's.
            if (copy.Map.TryGetValue(sourceKey, out var alreadyCloned)) newKey = alreadyCloned;
            else
            {
                // Otherwise the source record joins the copy as one more node, so the clone rides the SAME
                // internalize machinery rather than a second code path.
                var selfNode = new WalkNode(sourceKey, srcHit.Body,
                    RecordNaming.StripOverlay(srcHit.Body.GetType().Name), srcHit.Body.EditorID,
                    srcHit.ArmIndex, srcHit.Arm.Spelling, new[] { sourceKey }, "from", 0);
                var selfCopy = Internalize(patch, new[] { selfNode });
                if (!selfCopy.Success) return ClosureCopyOutcome.Fail(copy: selfCopy.Refusal, sources: consulted);
                newKey = selfCopy.Map[sourceKey];
            }

            var cloneRec = patch.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == newKey);
            if (cloneRec is null)
                return ClosureCopyOutcome.Fail(
                    copy: new CopyRefusal(CopyRefusalKind.Transplant, "the clone vanished after the renumber", Key: newKey),
                    sources: consulted);
            var clone = (IMajorRecord)cloneRec;
            if (!string.IsNullOrWhiteSpace(newEditorid)) clone.EditorID = newEditorid.Trim();

            clone.RemapLinks(copy.Map);
            var strip = StripBoundLinks(clone, isBound);
            if (!strip.Success) return ClosureCopyOutcome.Fail(copy: strip.Refusal, sources: consulted);
            stripped = strip.Stripped;
        }

        // The CLONE lane's half of the off-order check, asked of the ARTIFACT now that the strip has run: is there
        // a link here naming a plugin the game does not load, which the writer therefore cannot master? The
        // walk-level proxy cannot answer it — a boundary reached from an INTERNALIZED record is not on the clone,
        // so the strip never sees it, yet it still has to be mastered.
        // The measurement is artifact-level because it predicts a real serialization failure, including for records
        // a previous call left in an extended patch. The RENDER splits by cause, so the remedy names something the
        // caller actually did:
        //   the key is a boundary an exclusion pruned  -> StopOffOrder, the caller's own 'stop'
        //   the carrier is not a record this call added -> PatchOffOrderLink, pre-existing, this call innocent
        //   the carrier IS ours, link never pruned      -> CopiedOffOrderLink, an unseeded field carried across
        var (liveLinks, offender) = cloneLane
            ? ScanPatchLinks(patch, patchModKey, isOnOrder)
            : (null, null);
        if (offender is { } hit)
        {
            var excludedBoundary = walk.Kept.Any(k => k.Excluded && k.Key == hit.Link);
            var addedThisCall = copy.Copied.Any(c => c.NewKey == hit.Carrier) || hit.Carrier == newKey;
            var kind = excludedBoundary ? CopyRefusalKind.StopOffOrder
                     : addedThisCall ? CopyRefusalKind.CopiedOffOrderLink
                     : CopyRefusalKind.PatchOffOrderLink;
            return ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(kind, hit.Link.ModKey.FileName.String, hit.CarrierLabel, hit.Link),
                sources: consulted);
        }

        // The same measurement decides what may be reported as kept. An exclusion "keeps" a link, but in the clone
        // lane the strip may have removed it, so only boundaries the artifact still carries are reported as kept.
        var reportedKept = liveLinks is null
            ? walk.Kept
            : walk.Kept.Where(k => !k.Excluded || liveLinks.Contains(k.Key)).ToList();

        // The asset paths the copied records reference — a generic IAssetLinkGetter walk, no record-type
        // vocabulary. Harvested from the IN-PATCH duplicates and BEFORE serialize: the donor bodies may be
        // overlay-backed and are released at serialize.
        var assetPaths = Array.Empty<string>() as IReadOnlyList<string>;
        try
        {
            var copiedBodies = copy.Copied
                .Select(c => patch.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == c.NewKey))
                .Where(r => r is not null)
                .Select(r => (IMajorRecordGetter)r!)
                .ToList();
            if (targetKey is null && patch.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == newKey) is { } cloneBody)
                copiedBodies.Add(cloneBody);
            assetPaths = NpcAppearanceAssets.HarvestAssetPaths(copiedBodies);
        }
        catch { /* an unreadable asset link is not a reason to fail a written copy; the list is a report */ }

        try { WriteEngine.WritePatch(patch, mastersFor(patchFileName), outPath); }
        catch (Exception ex)
        {
            return ClosureCopyOutcome.Fail(
                engine: serializeFailure?.Invoke(ex) ?? WriteEngine.Describe(ex), sources: consulted);
        }

        var masters = new List<string>();
        long bytes = 0; bool sourceAmong = false; string? warning = null;
        try
        {
            var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            try
            {
                masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
                bytes = new FileInfo(outPath).Length;
            }
            finally { (back as IDisposable)?.Dispose(); }
            sourceAmong = masters.Any(m => { try { return boundPlugins.Contains(ModKey.FromFileName(m)); } catch { return false; } });
        }
        catch (Exception ex) { warning = ex.Message; }

        return new ClosureCopyOutcome(
            true, null, null, null, mode, sourceKey, newKey, outPath, extend,
            copy.Copied, reportedKept, walk.Cycles, attached, stripped, consulted,
            // The `from` record's own arm: every internalized record names the arm that produced it, and this is
            // the one body an ordered source list exists to disambiguate.
            srcHit.Arm.Spelling, assetPaths,
            masters, sourceAmong, nothingBound, bytes, warning);
    }

    /// <summary>One link in the built patch that names a plugin the active order does not carry, together with the
    /// record CARRYING it — which is what decides whose fault it is, and so which refusal the caller reads.</summary>
    readonly record struct OffOrderHit(FormKey Link, FormKey Carrier, string CarrierLabel);

    /// <summary>One pass over the built patch: every distinct link key it carries, and the FIRST link naming a
    /// plugin the active order does not have. Asks the ARTIFACT what survived rather than inferring it from the
    /// walk — the two disagree in the clone lane, where the strip runs between them.
    /// <para>The cast is guarded, not asserted. <see cref="ClosureWalk.Run"/> treats the same cast as fallible and
    /// the two must agree: asserting it here would throw out of BuildAndWrite AFTER the internalize, which renders
    /// as an internal failure the caller cannot act on.</para></summary>
    static (HashSet<FormKey> Live, OffOrderHit? Offender) ScanPatchLinks(
        SkyrimMod patch, ModKey patchModKey, Func<ModKey, bool> isOnOrder)
    {
        var live = new HashSet<FormKey>();
        OffOrderHit? offender = null;
        foreach (var rec in patch.EnumerateMajorRecords())
        {
            if (rec is not IFormLinkContainerGetter flc) continue;
            foreach (var l in flc.EnumerateFormLinks())
            {
                if (l.FormKey.IsNull) continue;
                live.Add(l.FormKey);
                if (offender is null && l.FormKey.ModKey != patchModKey && !isOnOrder(l.FormKey.ModKey))
                    offender = new OffOrderHit(l.FormKey, rec.FormKey,
                        $"{RecordNaming.StripOverlay(rec.GetType().Name)} '{rec.EditorID ?? "<no editorid>"}' ({rec.FormKey})");
            }
        }
        return (live, offender);
    }

    /// <summary>The belt-and-braces check after an attach: nothing pointing into the BOUND universe may survive on
    /// <paramref name="record"/>. Returns the offending key, or null when clean.
    /// <para><b>Scoped to bound keys ONLY.</b> A broader test — "any link that does not resolve" — also catches a
    /// target's PRE-EXISTING dangling reference (mod-update dirt whose master is still declared), which is not this
    /// operation's defect and must not block a legitimate copy.</para></summary>
    /// <para>The cast is guarded for the same reason <see cref="ScanPatchLinks"/>'s is. A record carrying no link
    /// container carries no links, so there is nothing here for it to leak.</para>
    public static FormKey? FindBoundLeak(IMajorRecordGetter record, Func<FormKey, bool> isBound)
    {
        if (record is not IFormLinkContainerGetter flc) return null;
        foreach (var l in flc.EnumerateFormLinks())
            if (!l.FormKey.IsNull && isBound(l.FormKey)) return l.FormKey;
        return null;
    }
}

/// <summary>The whole closure-copy operation's outcome — data only, no prose; the render owns the words.
/// Exactly one of the three refusal slots is set on a failure; all are null on success.
/// <para><see cref="SourcesConsulted"/> is the ordered universe as the caller spelled it, carried so the readback
/// can name which sources were in play even when nothing was found in any of them.</para></summary>
public sealed record ClosureCopyOutcome(
    bool Success,
    WalkRefusal? WalkRefusal, CopyRefusal? CopyRefusal, string? EngineError,
    string Mode,                                   // "attach" | "clone"
    FormKey SourceKey, FormKey NewKey,
    string? OutPath, bool Extended,
    IReadOnlyList<CopiedRecord> Copied,
    IReadOnlyList<WalkBoundary> Kept,
    IReadOnlyList<WalkCycle> Cycles,
    IReadOnlyList<StripEntry> Attached,
    IReadOnlyList<StripEntry> Stripped,
    IReadOnlyList<string> SourcesConsulted,
    string FromArmSpelling,
    IReadOnlyList<string> AssetPaths,
    IReadOnlyList<string> Masters, bool SourceAmongMasters, bool NothingBound,
    long Bytes, string? ReadBackWarning)
{
    static readonly IReadOnlyList<CopiedRecord> NoRecords = Array.Empty<CopiedRecord>();
    static readonly IReadOnlyList<StripEntry> NoEntries = Array.Empty<StripEntry>();
    static readonly IReadOnlyList<string> NoStrings = Array.Empty<string>();

    public static ClosureCopyOutcome Fail(
        WalkRefusal? walk = null, CopyRefusal? copy = null, string? engine = null,
        IReadOnlyList<string>? sources = null)
        => new(false, walk, copy, engine, "", default, default, null, false,
               NoRecords, Array.Empty<WalkBoundary>(), Array.Empty<WalkCycle>(),
               NoEntries, NoEntries, sources ?? NoStrings, "", NoStrings,
               NoStrings, false, false, 0, null);
}
