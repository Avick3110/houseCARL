using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>One record created through the service's batch entry point, in the single-record shape the create
/// probes are written in. The service's own single-record overload had no caller in the shipped process and was
/// deleted (#497); a batch of one is the call the create tool makes, down to the origin label and the words its
/// refusals use — a probe asserting on a wording the shipped tool never emits would go green over a broken
/// refusal.</summary>
internal static class ProbeCreate
{
    public static WritePatchBuilder.CreateOutcome CreateOne(
        this LoadOrderService svc, string recordType, string editorid, IReadOnlyList<BulkOp> operations,
        string? patchName, string? into, bool fullReadback = false, string? parent = null,
        string? collection = null, string? grid = null,
        string? target = null, bool inPlace = false, bool acknowledge = false) =>
        svc.CreateRecordsBatch(
            new[]
            {
                new CreateOp
                {
                    RecordType = recordType, Editorid = editorid, Operations = operations.ToArray(),
                    Parent = parent, Collection = collection, Grid = grid,
                }
            },
            patchName, into, fullReadback, target, inPlace, acknowledge,
            origins: new[] { (string?)"records[0]" },
            naming: new LoadOrderService.CreateOpNaming("ops", "op=\"CopyFrom\""));
}
