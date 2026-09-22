using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

// ClosureCopy — internalize a ClosureWalk's reached set into a patch under fresh keys, then make
// the artifact honest about what it still points at.
// Contracts in docs/architecture/write-path.md; the walk itself is docs/architecture/select-and-walk.md.

/// <summary>One record internalized: where it came from, where it landed, and which source arm produced its body.</summary>
public sealed record CopiedRecord(
    FormKey OldKey, FormKey NewKey, string TypeName, string? EditorId,
    int ArmIndex, string ArmSpelling, string PulledBy);

/// <summary>One stripped link, or one attached seed: the field it sat on (with index, for a list) and what was
/// removed or written, with the clear and the whole-property cases marked for the render.</summary>
public sealed record StripEntry(string Field, string Removed, bool Cleared = false, bool WholeProperty = false);

/// <summary>Why an internalize/strip refused.</summary>
public enum CopyRefusalKind
{
    /// <summary>A REQUIRED (non-nullable, non-list) link into the bound universe.</summary>
    RequiredForeignLink,
    /// <summary>A link-bearing substruct carrying a bound link that the record model will not let us clear.</summary>
    UnclearableSubstruct,
    /// <summary>The patch's object-ID counter is past the 24-bit ceiling.</summary>
    IdExhausted,
    /// <summary>Duplicate/renumber failed, or a duplicate could not be placed in the patch.</summary>
    Transplant,
    /// <summary>A link into the bound universe survived on the attach target after the remap.</summary>
    DonorLeak,
    /// <summary>A seed path names a field whose SHAPE the attach lane does not support, refused by name.</summary>
    UnsupportedSeedShape,
    /// <summary>A <c>Type:stop</c> exclusion pruned a record living OFF the active load order, which cannot be mastered.</summary>
    StopOffOrder,
    /// <summary>The target record lives in a NESTED group (a placed reference, a cell, a dialog response).</summary>
    UnsupportedTargetShape,
    /// <summary>A record an EARLIER call left in the patch links to a plugin the active order does not carry, so
    /// this call is innocent and must not borrow <see cref="StopOffOrder"/>'s sentence.</summary>
    PatchOffOrderLink,
    /// <summary>A record THIS call copied carries an off-order link no exclusion pruned, on a field
    /// <c>seed_paths</c> never named, so the remedy is the caller's seed set.</summary>
    CopiedOffOrderLink,
    /// <summary>The seed's shape is SUPPORTED and the TARGET's property cannot take it, so the fault is the target.</summary>
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
    /// <summary>Marks a post-attach leak whose key an EXCLUSION pruned rather than one the target already carried.</summary>
    public const string ExclusionLeakMarker = "<pruned-by-exclusion>";

    /// <summary>Internalize a walk's reached set into <paramref name="patch"/> under fresh local keys.</summary>
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

        // Duplicate + remap in a SCRATCH mod sharing the patch's ModKey, then transplant.
        var scratch = new SkyrimMod(patch.ModKey, SkyrimRelease.SkyrimSE);
        var ren = RemapEngine.RenumberRecordsInto(scratch, nodes.Select(n => n.Body), map);
        if (!ren.Success)
            return CopyResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant, ren.Error!));

        foreach (var rec in scratch.EnumerateMajorRecords())
            if (!RemapEngine.TryAddToFlatGroup(patch, (IMajorRecord)rec))
                return CopyResult.Fail(new CopyRefusal(CopyRefusalKind.Transplant,
                    $"{RecordNaming.StripOverlay(rec.GetType().Name)} {FormIdToken.Of(rec.FormKey)} could not be placed in the patch",
                    Key: rec.FormKey));

        var copied = nodes.Select(n => new CopiedRecord(
            n.Key, map[n.Key], n.TypeName, n.EditorId, n.ArmIndex, n.ArmSpelling, n.PulledBy)).ToList();
        return new CopyResult(true, null, copied, map);
    }

    /// <summary>Remove every link on <paramref name="record"/> for which <paramref name="isBound"/> holds. One rule
    /// per shape: nullable link → null · list of links → drop the entries · list of link-BEARING elements → drop
    /// those elements · nullable link-bearing substruct → null the property · a REQUIRED link → loud refusal.</summary>
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
                        $"the record's REQUIRED field '{prop.Name}' points at {FormIdToken.Of(fk)}, which is in the source universe " +
                        "being copied away from: it cannot be nulled without inventing data, and keeping it would " +
                        "silently master that plugin", prop.Name, fk);
                continue;
            }
            if (val is IList || val is string) continue;
            if (val is IFormLinkContainerGetter sub && !prop.CanWrite)
            {
                var keys = sub.EnumerateFormLinks()
                    .Where(l => !l.FormKey.IsNull && isBound(l.FormKey))
                    .Select(l => FormIdToken.Of(l.FormKey)).Distinct().ToList();
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
                // Pass 1 has already proven this link is nullable; the else-branch is a backstop.
                if (TryNullLink(val))
                    stripped.Add(new StripEntry(prop.Name, FormIdToken.Of(fk)));
                else
                    return StripResult.Fail(new CopyRefusal(CopyRefusalKind.RequiredForeignLink,
                        $"the record's REQUIRED field '{prop.Name}' points at {FormIdToken.Of(fk)}, which is in the source universe " +
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
                        { list.RemoveAt(i); stripped.Add(new StripEntry($"{prop.Name}[{i}]", FormIdToken.Of(lk))); }
                    }
                    else if (el is IFormLinkContainerGetter elc)
                    {
                        var keys = elc.EnumerateFormLinks()
                            .Where(l => !l.FormKey.IsNull && isBound(l.FormKey))
                            .Select(l => FormIdToken.Of(l.FormKey)).Distinct().ToList();
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
                    .Select(l => FormIdToken.Of(l.FormKey)).Distinct().ToList();
                if (keys.Count == 0) continue;
                // Nulling the property removes EVERYTHING on it, marked so the render can say so.
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

    /// <summary>Null a single link's key iff the link is genuinely NULLABLE, judged on the RECORD MODEL's
    /// <c>IFormLinkNullable&lt;T&gt;</c>; false for a required link, which the caller escalates to the refusal.</summary>
    static bool TryNullLink(object link)
    {
        if (!IsNullableLink(link)) return false;
        var m = link.GetType().GetMethod("SetToNull", Type.EmptyTypes);
        if (m is null) return false;
        m.Invoke(link, null);
        return true;
    }

    /// <summary>The non-mutating half of the same judgement, so pass 1 can decide without touching the record.</summary>
    static bool IsNullableLink(object link) => link.GetType().GetInterfaces().Any(i =>
        i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkNullable<>));

    /// <summary>ATTACH — set the walked SEED fields on <paramref name="target"/> from <paramref name="source"/>,
    /// substituting the internalized keys, so only the seed paths are touched and only the target is written.</summary>
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

            // Shape is decided by ClassifySeed, never re-judged here, so this lane and the clone lane agree.
            var shape = ClosureWalk.ClassifySeed(sp);
            if (shape.Kind == SeedShapeKind.Unsupported)
                return StripResult.Fail(new CopyRefusal(CopyRefusalKind.UnsupportedSeedShape, shape.Reason!, path));

            if (shape.Kind == SeedShapeKind.Link)
            {
                if (tv is null)
                    return StripResult.Fail(new CopyRefusal(CopyRefusalKind.UnwritableTarget,
                        $"'{path}' is a record link on the source but the target's is not writable", path));
                var key = (sv as IFormLinkGetter)?.FormKeyNullable;
                // An UNSET source link CLEARS the target's; null is a state of this shape, not a refusal.
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

            // A null target list is a list carrying nothing, so it is assigned INTO rather than refused.
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
            // An EMPTY or absent source list clears the target's, reported as CLEARED rather than "0 link(s)".
            set.Add(mapped.Count == 0
                ? new StripEntry(path, "cleared", Cleared: true)
                : new StripEntry(path, $"{mapped.Count} link(s)"));
        }
        return new StripResult(true, null, set);
    }

    /// <summary>Clear a single link property; false for a REQUIRED link, which has no legal null.</summary>
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

    /// <summary>Construct an empty list for a target property whose own is null; null when the declared type cannot be instantiated.</summary>
    static IList? MakeList(Type propertyType)
    {
        try { return System.Activator.CreateInstance(propertyType) as IList; }
        catch { return null; }
    }

    /// <summary>Build a concrete <c>FormLink&lt;T&gt;</c> for a link-typed list without knowing T; null when the
    /// list is not link-typed, which the caller reports by name.</summary>
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

    /// <summary>Build the copy into a patch at <paramref name="outPath"/> and serialize it: internalize the reached
    /// set, run the mode lane (attach to a target / mint a clone and strip it), write multi-master, read the header
    /// back. An IN-PATCH target is resolved here, off the opened patch mod.</summary>
    public static ClosureCopyOutcome BuildAndWrite(
        string outPath, bool extend,
        FormKey sourceKey, SourceHit srcHit,
        WalkResult walk, IReadOnlyList<string> seedPaths,
        FormKey? targetKey, IMajorRecordGetter? targetActiveBody,
        string? newEditorid,
        Func<FormKey, bool> isBound, IReadOnlySet<ModKey> boundPlugins, bool nothingBound,
        Func<ModKey, bool> isOnOrder,
        Func<string, IReadOnlyList<ISkyrimModGetter>> mastersFor,
        IReadOnlyList<SourceArmRef> consulted,
        Func<Exception, string>? serializeFailure = null)
    {
        var patchFileName = Path.GetFileName(outPath);
        var patchModKey = ModKey.FromFileName(patchFileName);
        SkyrimMod patch;
        if (extend)
        {
            try { patch = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath)); }
            catch (Exception ex) { return ClosureCopyOutcome.Fail(engine: ex.Message, sources: consulted); }
        }
        else patch = new SkyrimMod(patchModKey, SkyrimRelease.SkyrimSE);

        // A pruned link must still be MASTERABLE, and in the ATTACH lane nothing strips, so every excluded
        // boundary survives and the check belongs here, before anything is built. The clone lane's half is below.
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
                    // Records in NESTED groups are ordinary caller input, so it is a named refusal, not a throw.
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
                // Why it leaked decides the remedy, an exclusion-pruned key having been attached unmapped here.
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
            // A walk that cycles back to the `from` record has ALREADY internalized it, so reuse that copy.
            if (copy.Map.TryGetValue(sourceKey, out var alreadyCloned)) newKey = alreadyCloned;
            else
            {
                // Otherwise the source record joins the copy as one more node, on the same internalize path.
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

        // The CLONE lane's half, asked of the ARTIFACT now that the strip has run, and split by cause:
        //   an exclusion-pruned boundary -> StopOffOrder · a carrier this call did not add -> PatchOffOrderLink ·
        //   our own carrier, never pruned -> CopiedOffOrderLink.
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

        // Only boundaries the artifact still carries are reported as kept, the strip having removed some.
        var reportedKept = liveLinks is null
            ? walk.Kept
            : walk.Kept.Where(k => !k.Excluded || liveLinks.Contains(k.Key)).ToList();

        // The copied records' asset paths, off a generic IAssetLinkGetter walk of the IN-PATCH duplicates.
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
            assetPaths = AssetLinkHarvest.HarvestAssetPaths(copiedBodies);
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
            var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
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
            // The `from` record's own arm, the one body an ordered source list exists to disambiguate.
            SourceArmRef.Of(srcHit.Arm), assetPaths,
            masters, sourceAmong, nothingBound, bytes, warning);
    }

    /// <summary>One off-order link in the built patch, with the record CARRYING it, which decides the refusal.</summary>
    readonly record struct OffOrderHit(FormKey Link, FormKey Carrier, string CarrierLabel);

    /// <summary>One pass over the built patch: every distinct link key it carries, and the FIRST off-order link.
    /// The cast is guarded rather than asserted, as <see cref="ClosureWalk.Run"/> treats the same one.</summary>
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
                        $"{RecordNaming.StripOverlay(rec.GetType().Name)} '{rec.EditorID ?? "<no editorid>"}' ({FormIdToken.Of(rec.FormKey)})");
            }
        }
        return (live, offender);
    }

    /// <summary>The check after an attach: nothing pointing into the BOUND universe may survive on
    /// <paramref name="record"/>. Returns the offending key, or null when clean.</summary>
    public static FormKey? FindBoundLeak(IMajorRecordGetter record, Func<FormKey, bool> isBound)
    {
        if (record is not IFormLinkContainerGetter flc) return null;
        foreach (var l in flc.EnumerateFormLinks())
            if (!l.FormKey.IsNull && isBound(l.FormKey)) return l.FormKey;
        return null;
    }
}

/// <summary>The whole closure-copy operation's outcome — data only, the render owning the words. Exactly one of
/// the three refusal slots is set on a failure, and <see cref="SourcesConsulted"/> is the ordered universe as the
/// caller spelled it, each arm carrying the mod folder it resolved from.</summary>
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
    IReadOnlyList<SourceArmRef> SourcesConsulted,
    SourceArmRef? FromArm,
    IReadOnlyList<string> AssetPaths,
    IReadOnlyList<string> Masters, bool SourceAmongMasters, bool NothingBound,
    long Bytes, string? ReadBackWarning)
{
    static readonly IReadOnlyList<CopiedRecord> NoRecords = Array.Empty<CopiedRecord>();
    static readonly IReadOnlyList<StripEntry> NoEntries = Array.Empty<StripEntry>();
    static readonly IReadOnlyList<string> NoStrings = Array.Empty<string>();
    static readonly IReadOnlyList<SourceArmRef> NoArms = Array.Empty<SourceArmRef>();

    public static ClosureCopyOutcome Fail(
        WalkRefusal? walk = null, CopyRefusal? copy = null, string? engine = null,
        IReadOnlyList<SourceArmRef>? sources = null)
        => new(false, walk, copy, engine, "", default, default, null, false,
               NoRecords, Array.Empty<WalkBoundary>(), Array.Empty<WalkCycle>(),
               NoEntries, NoEntries, sources ?? NoArms, null, NoStrings,
               NoStrings, false, false, 0, null);
}
