using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.BsaExtractArchives;

namespace HousecarlMcpTests;

/// <summary>The list door (#1284): every list parameter reads <c>["@&lt;absolute path&gt;"]</c> the same way, a file
/// answering as the same list inline, a FormID artifact epoch-checked, and one sentence per mistake on every list.</summary>
[Trait("tier", "unit")]
public sealed class ListDoorTests : IClassFixture<AssetWalkWorld>
{
    readonly AssetWalkWorld _w;
    public ListDoorTests(AssetWalkWorld w) => _w = w;

    string Temp(string name) => Path.Combine(_w.Root, "door-" + Guid.NewGuid().ToString("N") + "-" + name);

    /// <summary>A plain list file, one entry per line, as "@path".</summary>
    string Lines(params string[] entries)
    {
        var path = Temp("list.txt");
        File.WriteAllLines(path, entries);
        return "@" + path;
    }

    /// <summary>A result artifact with one identity column, as "@path".</summary>
    string Artifact(string identity, string epoch, params string[] values)
    {
        var path = Temp("art.jsonl");
        using var writer = new ResultArtifact.Writer();
        foreach (var v in values)
            writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString(identity, v); w.WriteEndObject(); });
        Assert.Null(writer.Save(ArtifactTarget.Named(path), ToolNames.Records, Array.Empty<KeyValuePair<string, string>>(),
                                identity, new[] { identity }, "input order", total: values.Length, epoch: epoch).Error);
        return "@" + path;
    }

    /// <summary>Two answers to the same question, read times aside.</summary>
    static void Same(string expected, string actual)
        => Assert.Equal(System.Text.RegularExpressions.Regex.Replace(expected, "[0-9]+ ms", "N ms"),
                        System.Text.RegularExpressions.Regex.Replace(actual, "[0-9]+ ms", "N ms"));

    string Epoch => _w.Svc.CaptureView().Stamp.Epoch;
    string[] Ids(params string[] editorIds) => editorIds.Select(_w.Id).ToArray();

    // ---- asset_status walk records= (#1284) --------------------------------------------------------------

    /// <summary>The #1284 repro: a records= artifact walks the same nodes as its FormIDs inline.</summary>
    [Fact]
    public void AWalkRecordsArtifactWalksTheSameNodesAsTheListInline()
    {
        var ids = Ids("HcWalkHero", "HcWalkSge");
        var inline = AssetTools.AssetStatus(_w.Svc, records: ids, walk: true);
        var fromFile = AssetTools.AssetStatus(_w.Svc, records: new[] { Artifact("formid", Epoch, ids) }, walk: true);

        Assert.DoesNotContain("error:", inline);
        Same(inline, fromFile);
    }

    /// <summary>A records= artifact from another build is refused naming both epochs.</summary>
    [Fact]
    public void AStaleWalkRecordsArtifactIsRefusedNamingBothEpochs()
    {
        var r = AssetTools.AssetStatus(_w.Svc, records: new[] { Artifact("formid", "e2-deadbeef", Ids("HcWalkHero")) }, walk: true);

        Assert.StartsWith("error:", r);
        Assert.Contains("e2-deadbeef", r);
        Assert.Contains(Epoch, r);
    }

    /// <summary>A path artifact on records= is refused by its identity, not read as FormIDs; a mixed list in the door's sentence.</summary>
    [Fact]
    public void RecordsRefusesAPathArtifactByIdentityAndAMixedListInTheDoorsSentence()
    {
        var byIdentity = AssetTools.AssetStatus(_w.Svc, records: new[] { Artifact("path", Epoch, AssetWalkWorld.CapeNif) }, walk: true);
        Assert.Contains("carries 'path' identities, not 'formid' ones", byIdentity);

        var mixed = AssetTools.AssetStatus(_w.Svc, records: new[] { Lines(Ids("HcWalkHero")), _w.Id("HcWalkSge") }, walk: true);
        Assert.Contains("records= mixes an '@file' entry with inline entries", mixed);
    }

    /// <summary>from_source= reads its plugin list from a file: the walk names the same sources.</summary>
    [Fact]
    public void FromSourceReadsThePluginListFromAFile()
    {
        var ids = Ids("HcWalkHero");
        var sources = new[] { AssetWalkWorld.OverridePlugin, "winner" };
        var inline = AssetTools.AssetStatus(_w.Svc, records: ids, from_source: sources, walk: true);
        var fromFile = AssetTools.AssetStatus(_w.Svc, records: ids, from_source: new[] { Lines(sources) }, walk: true);

        Assert.Contains(AssetWalkWorld.OverridePlugin, inline);
        Same(inline, fromFile);
    }

    // ---- under= ------------------------------------------------------------------------------------------

    /// <summary>asset_status under= from a file expands to its selectors, with the same per-selector notes.</summary>
    [Fact]
    public void AssetStatusUnderReadsItsSelectorsFromAFile()
    {
        var selectors = new[] { "meshes/hcwalk", "meshes/nothing-here" };
        var inline = AssetTools.AssetStatus(_w.Svc, under: selectors);
        var fromFile = AssetTools.AssetStatus(_w.Svc, under: new[] { Lines(selectors) });

        Assert.Contains("'meshes/nothing-here' matched no file", inline);
        Assert.Contains("cape.nif", fromFile);
        Same(inline, fromFile);
    }

    /// <summary>bsa_list and bsa_extract under= from a file expand to its selectors, with the same notes.</summary>
    [Fact]
    public void BsaUnderReadsItsSelectorsFromAFile()
    {
        var archive = Write(Path.GetDirectoryName(Temp("x"))!, "door-" + Guid.NewGuid().ToString("N") + ".bsa", BsaBuilder.Build(105, Named, ThreeFiles));
        var selectors = new[] { "scripts/main.pex", "scripts/mian.pex" };

        var inline = BsaTools.BsaList(archive, under: selectors);
        Assert.Contains("under 'scripts/mian.pex' matched no file", inline);
        Same(inline, BsaTools.BsaList(archive, under: new[] { Lines(selectors) }));

        var dest = Temp("out");
        var extracted = BsaTools.BsaExtract(null!, archive, out_path: dest, under: new[] { Lines(selectors) });
        Assert.Contains("under 'scripts/mian.pex' matched no file", extracted);
        Assert.True(File.Exists(Path.Combine(dest, "scripts", "main.pex")), extracted);
    }

    // ---- nif_inspect ------------------------------------------------------------------------------------

    /// <summary>mesh_paths= from a file answers one row per listed mesh, as inline (finding 12).</summary>
    [Fact]
    public void NifMeshPathsFromAFileAnswerOneRowPerMesh()
    {
        var meshes = new[] { AssetWalkWorld.CapeNif, AssetWalkWorld.HairNif };
        var inline = NifTools.NifInspect(_w.Svc, mesh_paths: meshes);
        var fromFile = NifTools.NifInspect(_w.Svc, mesh_paths: new[] { Lines(meshes) });

        Assert.Contains("cape.nif", fromFile);
        Assert.DoesNotContain("list.txt", fromFile);
        Same(inline, fromFile);
    }

    /// <summary>npc= takes a FormID artifact, epoch-checked.</summary>
    [Fact]
    public void NifNpcTakesAFormidArtifactEpochChecked()
    {
        var ids = Ids("HcWalkHero");
        Same(NifTools.NifInspect(_w.Svc, npc: ids),
                     NifTools.NifInspect(_w.Svc, npc: new[] { Artifact("formid", Epoch, ids) }));

        var stale = NifTools.NifInspect(_w.Svc, npc: new[] { Artifact("formid", "e2-deadbeef", ids) });
        Assert.StartsWith("error:", stale);
        Assert.Contains("e2-deadbeef", stale);
    }

    // ---- check ------------------------------------------------------------------------------------------

    /// <summary>check formids= takes an artifact and seeds= a file, each a FormID artifact epoch-checked.</summary>
    [Fact]
    public void CheckFormidsAndSeedsReadFilesEpochChecked()
    {
        var ids = Ids("HcWalkHero");
        Same(CheckTools.CheckTool(_w.Svc, formids: ids),
                     CheckTools.CheckTool(_w.Svc, formids: new[] { Artifact("formid", Epoch, ids) }));
        Assert.Contains("e2-deadbeef", CheckTools.CheckTool(_w.Svc, formids: new[] { Artifact("formid", "e2-deadbeef", ids) }));

        var quest = Ids("HcWalkSge");
        var dialogue = new[] { "dialogue" };
        Same(CheckTools.CheckTool(_w.Svc, findings: dialogue, seeds: quest),
                     CheckTools.CheckTool(_w.Svc, findings: dialogue, seeds: new[] { Lines(quest) }));
        Assert.Contains("e2-deadbeef", CheckTools.CheckTool(_w.Svc, findings: dialogue, seeds: new[] { Artifact("formid", "e2-deadbeef", quest) }));
    }

    // ---- lists of names ---------------------------------------------------------------------------------

    /// <summary>Every list of names reads a file of names, one per line, answering as the same list inline.</summary>
    [Fact]
    public void EveryNameListReadsAFileOfNames()
    {
        // check types= and findings=.
        Same(CheckTools.CheckTool(_w.Svc, types: new[] { "NPC_" }, findings: new[] { "errors" }),
                     CheckTools.CheckTool(_w.Svc, types: new[] { Lines("NPC_") }, findings: new[] { Lines("errors") }));

        // records types=, project.fields, walk.seed_paths, walk.through.
        Same(RecordsTools.Records(_w.Svc, types: new[] { "NPC_" }),
                     RecordsTools.Records(_w.Svc, types: new[] { Lines("NPC_") }));
        Same(RecordsTools.Records(_w.Svc, formids: Ids("HcWalkHero"), project: new() { form = "fields", fields = new[] { "EditorID", "Race" } }),
                     RecordsTools.Records(_w.Svc, formids: Ids("HcWalkHero"), project: new() { form = "fields", fields = new[] { Lines("EditorID", "Race") } }));
        Same(RecordsTools.Records(_w.Svc, formids: Ids("HcWalkHero"), walk: new() { seed_paths = new[] { "HeadParts" }, through = new[] { "HeadPart" } }),
                     RecordsTools.Records(_w.Svc, formids: Ids("HcWalkHero"), walk: new() { seed_paths = new[] { Lines("HeadParts") }, through = new[] { Lines("HeadPart") } }));

        // copy exclude_types= and seed_paths=, refused at the severity before anything is written.
        var copyInline = CopyTools.Copy(_w.Svc, _w.Id("HcWalkHero"), seed_paths: new[] { "HeadParts" }, exclude_types: new[] { "Npc:bogus" }, new_editorid: "HcDoorClone");
        Assert.Contains("severity 'bogus'", copyInline);
        Same(copyInline, CopyTools.Copy(_w.Svc, _w.Id("HcWalkHero"), seed_paths: new[] { Lines("HeadParts") }, exclude_types: new[] { Lines("Npc:bogus") }, new_editorid: "HcDoorClone"));

        // copy from_source= and merge_plugins plugins=, each refused naming a plugin the order does not hold.
        var sourceInline = CopyTools.Copy(_w.Svc, _w.Id("HcWalkHero"), from_source: new[] { "HcDoorNoSuch.esp" }, seed_paths: new[] { "HeadParts" }, new_editorid: "HcDoorClone");
        Assert.Contains("HcDoorNoSuch.esp", sourceInline);
        Same(sourceInline, CopyTools.Copy(_w.Svc, _w.Id("HcWalkHero"), from_source: new[] { Lines("HcDoorNoSuch.esp") }, seed_paths: new[] { "HeadParts" }, new_editorid: "HcDoorClone"));
        var mergeInline = WriteTools.MergePlugins(_w.Svc, new[] { "HcDoorNoSuch.esp" }, "HcDoorMerge");
        Assert.Contains("HcDoorNoSuch.esp", mergeInline);
        Same(mergeInline, WriteTools.MergePlugins(_w.Svc, new[] { Lines("HcDoorNoSuch.esp") }, "HcDoorMerge"));
    }

    /// <summary>'@@x' is the literal '@x' on every list: a name, not a file.</summary>
    [Fact]
    public void ADoubledAtIsTheLiteralValueOnEveryList()
    {
        var path = AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { "@@textures/hcwalk/at.dds" });
        Assert.DoesNotContain("not an absolute path", path);
        Assert.Contains("@textures", path);

        var type = RecordsTools.Records(_w.Svc, types: new[] { "@@NPC_" });
        Assert.Contains("'@NPC_'", type);

        var id = RecordsTools.Records(_w.Svc, formids: new[] { "@@000001:Skyrim.esm" });
        Assert.DoesNotContain("not an absolute path", id);
        Assert.Contains("@000001:Skyrim.esm", id);
    }

    // ---- one sentence per mistake ----------------------------------------------------------------------

    /// <summary>Each mistake with a list file is one sentence, the same on a FormID list, a path list, a name list and
    /// a JSON-element list, differing only by the parameter it names.</summary>
    [Fact]
    public void EachListFileMistakeIsOneSentenceOnEveryList()
    {
        var empty = Temp("empty.txt");
        File.WriteAllText(empty, "  \n");
        var missing = Temp("missing.txt");
        var cases = new (string Entry, string Sentence)[]
        {
            ("@", "'@' names a list file but no path follows it"),
            ("@C:relative.txt", "list file 'C:relative.txt' is not an absolute path"),
            ("@" + missing, "could not read {0} list file"),
            ("@" + empty, "list file '" + empty + "' is empty"),
        };
        foreach (var (entry, sentence) in cases)
        {
            Assert.Contains(string.Format(sentence, "formids="), RecordsTools.Records(_w.Svc, formids: new[] { entry }));
            Assert.Contains(string.Format(sentence, "asset_paths="), AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { entry }));
            Assert.Contains(string.Format(sentence, "types="), RecordsTools.Records(_w.Svc, types: new[] { entry }));
            Assert.Contains(string.Format(sentence, "ops="), ApplyTools.Apply(_w.Svc, ops: System.Text.Json.JsonSerializer.SerializeToElement(entry)));
        }

        // A file of separators alone holds no entry either, and says so in the same sentence.
        var separators = Temp("commas.txt");
        File.WriteAllText(separators, ", ,\n,");
        Assert.Contains("list file '" + separators + "' is empty", RecordsTools.Records(_w.Svc, formids: new[] { "@" + separators }));

        var list = Lines(Ids("HcWalkHero"));
        Assert.Contains("formids= mixes an '@file' entry with inline entries", RecordsTools.Records(_w.Svc, formids: new[] { list, "x" }));
        Assert.Contains("types= mixes an '@file' entry with inline entries", RecordsTools.Records(_w.Svc, types: new[] { list, "x" }));
        Assert.Contains("references= mixes an '@file' entry with inline entries",
                        RecordsTools.Records(_w.Svc, types: new[] { "NPC_" }, references: new[] { "!" + list, _w.Id("HcWalkSge") }));
        Assert.Contains("ops= mixes an '@file' entry with inline entries",
                        ApplyTools.Apply(_w.Svc, ops: System.Text.Json.JsonSerializer.SerializeToElement(new[] { list, "x" })));
    }
}
