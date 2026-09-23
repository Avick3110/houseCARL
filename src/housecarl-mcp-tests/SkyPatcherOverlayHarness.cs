using Mutagen.Bethesda.Plugins;

namespace HousecarlMcpTests;

/// <summary>The embedded catalog and field map, a line builder, and a stub resolver for driving
/// <see cref="SkyPatcherOverlay.Apply"/> on an in-memory record with no game data and no MO2 instance.</summary>
static class SkyPatcherOverlayHarness
{
    static readonly Lazy<SkyPatcherCatalog> CatalogLazy = new(SkyPatcherCatalog.Load);
    static readonly Lazy<SkyPatcherFieldMap> FieldMapLazy = new(SkyPatcherFieldMap.Load);

    public static SkyPatcherCatalog Catalog => CatalogLazy.Value;
    public static SkyPatcherFieldMap FieldMap => FieldMapLazy.Value;

    public static SkyPatcherOverlay.OrderedLine Line(string file, int n, string text)
        => new(file, n, SkyPatcherParse.ParseLine(text));

    /// <summary>Replays <paramref name="lines"/> onto <paramref name="record"/> through the given subfolder and record type's map.</summary>
    public static SkyPatcherOverlay.SkyPatcherOverlayResult Apply(object record, FormKey fk, string? editorId, string subfolder,
        string mutagenType, StubResolver resolver, params SkyPatcherOverlay.OrderedLine[] lines)
        => SkyPatcherOverlay.Apply(record, fk, editorId, Catalog, Catalog.ForSubfolder(subfolder)!,
            FieldMap.For(subfolder, mutagenType), lines, resolver);

    public sealed class StubResolver : SkyPatcherOverlay.IFormResolver
    {
        public Dictionary<string, FormKey> EditorIds = new(StringComparer.OrdinalIgnoreCase);
        // eid -> its Mutagen type; a lookup scoped to another type misses, as the real resolver does.
        public Dictionary<string, string> EditorIdTypes = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Plugins = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<FormKey, string> WinnerLeafs = new();
        public Dictionary<FormKey, IReadOnlyList<FormKey>> Keywords = new();
        public Dictionary<FormKey, string> Winners = new();
        public Dictionary<FormKey, string> Eids = new();

        public FormKey? ResolveEditorId(string editorId, string? mutagenType)
        {
            if (!EditorIds.TryGetValue(editorId, out var fk)) return null;
            if (EditorIdTypes.TryGetValue(editorId, out var t) && mutagenType is not null
                && !t.Equals(mutagenType, StringComparison.OrdinalIgnoreCase))
                return null;
            return fk;
        }
        public string? ReadWinnerLeaf(FormKey donor, string path) => WinnerLeafs.GetValueOrDefault(donor);
        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record) => Keywords.GetValueOrDefault(record);
        public bool PluginPresent(string pluginName) => Plugins.Contains(pluginName);
        public string? WinnerPluginOf(FormKey record) => Winners.GetValueOrDefault(record);
        public string? EditorIdOf(FormKey record) => Eids.GetValueOrDefault(record);
    }
}
