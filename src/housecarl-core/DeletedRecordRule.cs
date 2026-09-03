using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The ONE place the "a DELETED record has no live body" rule lives, so the scans that read a record's content can't
/// drift apart on it (#279). Three walk Mutagen's <see cref="IFormLinkContainerGetter.EnumerateFormLinks"/> over every
/// record in an order, and all three must treat a deleted record the same way:
///   • the scan's references= arm (<c>LoadOrderService.CrossQuery</c>) — #276, the first site; it was
///     housecarl_cross_plugin_query then and rides housecarl_records now.
///   • <see cref="ErrorCheck"/>'s dangling-ref sweep (housecarl_check findings=["errors"]), active AND
///     off-order passes.
///   • <see cref="RemapEngine.IdentifyExternalReferencers"/>'s compact/merge dependency scan.
/// The scan's where= arm is guarded alongside its references= arm, but it is NOT one of the link
/// walks: it reads a field leaf (<c>FieldPredicateSet.Matches</c> → <c>ReadEngine.ReadLeaf</c>), which already
/// catches its own read faults and answers "(unreadable: …)". It is here on the SEMANTIC ground below only — a
/// deleted record has no live field to test — never the crash ground.
///
/// THE RULE: a major record flagged Deleted carries no content by engine rule — the game reads the header, sees the
/// flag, and never looks at a body. So its outgoing links are not live: it references nothing, and there is no field
/// to test. Every walker excludes it BEFORE the link walk.
///
/// WHY IT IS ALSO THE CRASH GUARD FOR THE THREE LINK WALKS (#276, and the reason this is not merely cosmetic): an
/// ENGINE-authored deleted record can leave a content-free-but-not-clean residual body behind (the wild repro was
/// deleted PACKs in a follower mod). Mutagen's lazy parse then throws on that residual when the walk reaches for its
/// links, and the per-record fault isolation each walker has accounts it as an UNSCANNABLE skip with a raw exception
/// cause — a deleted record reading as a parser hole, so a genuine finding hiding in a "skipped" record looks
/// possible when it isn't (Q3).
/// Skipping it as deleted is the same answer arrived at honestly, with nothing left in the unscannable bucket.
/// (A Mutagen-AUTHORED deleted record is clean — Mutagen serialises an empty body — so this only bites on records
/// the engine or another tool wrote.)
///
/// SCOPE — this governs the LINK WALK and the body-content filters ONLY. Anything read from the record HEADER stays
/// live on a deleted record, because the header is parsed eagerly and is not what throws:
///   • its FormKey — <see cref="RemapEngine.IdentifyExternalReferencers"/>'s external-OVERRIDER test is identity-only
///     (a deleted override of a record about to be renumbered is still a dependent worth warning about), so it runs
///     BEFORE this guard, not behind it.
///   • its EditorID — cross_plugin_query's editoridContains= filter stays live (EDID is an early subrecord, read
///     before the deep body parse that can throw).
///
/// ACKNOWLEDGED BEHAVIOR CHANGE (stated, not implied): a deleted record whose body DOES parse and DOES link to a
/// searched target is no longer returned by references=, no longer reported as dangling by check_errors, and no
/// longer listed as an external referencer by the compact/merge scan. That is the "treat a deleted record as
/// referencing nothing" resolution, applied consistently rather than only where a crash forced it.
/// </summary>
public static class DeletedRecordRule
{
    /// <summary>True when <paramref name="body"/> is a DELETED major record — so its content, and every FormLink in
    /// it, is not live and must not be walked. Reads the record header's Deleted flag only; never touches the body,
    /// so it is safe on exactly the malformed records the walk would throw on.</summary>
    public static bool HasNoLiveBody(IMajorRecordGetter body) => body.IsDeleted;
}
