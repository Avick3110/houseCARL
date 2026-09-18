using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The ONE place the "a DELETED record has no live body" rule lives; contract in docs/architecture/check-family-tests.md.</summary>
public static class DeletedRecordRule
{
    /// <summary>True when <paramref name="body"/> is a DELETED major record, so its content and links are not live and must not be walked.</summary>
    public static bool HasNoLiveBody(IMajorRecordGetter body) => body.IsDeleted;
}
