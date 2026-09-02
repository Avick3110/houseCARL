using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Guard for the polymorphic-element validator surface (#35 — the VMAD write gap): the corpus models
/// <c>ScriptEntry.Properties</c> as a list of the base <c>ScriptProperty</c> (Name/Flags only), but every real
/// element is one of its ARMS (a <c>ScriptObjectProperty</c> carries Object/Alias) — so before this surface the
/// pre-flight rejected every write that touched an arm-only field ("No field 'Object' on 'ScriptProperty'") and
/// every compose of a concrete arm ("spec type does not match element type"), even though the runtime engine
/// (runtime-typed navigation; BuildStruct-by-name; covariant list Add) could already perform them.
///
/// Corpus-only checks A–E run with no plugin (CI-safe). With <c>--source &lt;Skyrim.esm path&gt;</c> the guard
/// also proves the surface END-TO-END (F): DeepCopy a real scripted record, drive the SAME ApplyVerb the patch
/// cleave uses for an arm-field Set and an arm-element compose Add, and assert the typed results.
///
/// Run: dotnet run --project src/housecarl-generator vmad-poly-guard [--source "&lt;path&gt;\Skyrim.esm"]
/// </summary>
public static class VmadPolyProbe
{
    [CiProbe("vmad-poly-guard")]
    public static int RunGuard(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);

        // CI-safe: corpus.json is GENERATED, not tracked — on a fresh checkout (the CI runner) build it into a
        // UNIQUE temp dir (no cross-run sharing/races; PR review) and point the rulebook there, leaving the
        // working tree untouched; cleaned up on exit. A repo with generated/ already present (local dev) is
        // used as-is.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-vmad-poly-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Console.WriteLine($"corpus.json absent — generating into {tmp} (CI / fresh checkout)…");
            var rc = CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), Path.Combine(tmp, "refs"));
            if (rc != 0) { Console.Error.WriteLine("error: corpus generation failed"); return rc; }
            CorpusRulebook.CorpusPath = Path.Combine(tmp, "generated", "corpus.json");
        }
        try
        {
            return RunChecks(f);
        }
        finally
        {
            if (tmp is not null) { try { Directory.Delete(tmp, recursive: true); } catch { /* temp cleanup is best-effort */ } }
        }
    }

    static int RunChecks(Dictionary<string, string> f)
    {
        var rb = CorpusRulebook.Load();
        int failures = 0;

        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        Console.WriteLine("vmad-poly-guard — polymorphic-element validator surface (#35)");
        Console.WriteLine();

        // ---- A: a leaf Set through a polymorphic element's ARM field passes pre-flight. (THE filed repro:
        //         'No field Object on ScriptProperty'.) ----
        var setArmLeaf = new WriteRequest
        {
            RecordType = "Activator",
            Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties[0]", "Object" },
            Verb = "Set", Value = "018C91:Skyrim.esm",
        };
        var aErr = rb.Validate(setArmLeaf);
        Check("A: Set …Properties[0].Object (arm-only field) passes pre-flight", aErr is null, aErr);

        // ---- B: composing a concrete ARM element into the base-typed list passes pre-flight. ----
        var addArm = new WriteRequest
        {
            RecordType = "Activator",
            Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties" },
            Verb = "Add",
            Struct = new StructSpec
            {
                Type = "ScriptObjectProperty",
                Fields = new()
                {
                    ["Name"] = "VmadGuardProp", ["Flags"] = "Edited",
                    ["Object"] = "00308D:Update.esm", ["Alias"] = "-1",
                },
            },
        };
        var bErr = rb.Validate(addArm);
        Check("B: Add compose ScriptObjectProperty into Properties passes pre-flight", bErr is null, bErr);

        // ---- C: a field on NO arm still rejects — and the message says the arms were searched (Q3). ----
        var bogus = new WriteRequest
        {
            RecordType = "Activator",
            Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties[0]", "Bogus" },
            Verb = "Set", Value = "1",
        };
        var cErr = rb.Validate(bogus);
        Check("C: a field on no arm still rejects", cErr is not null);
        Check("C2: …and the rejection names the searched arms", cErr?.Contains("arms", StringComparison.OrdinalIgnoreCase) == true, cErr);

        // ---- D: a spec type that is NEITHER the element type NOR one of its arms still rejects, and the
        //         message lists the legal arm set. ----
        var wrongSpec = new WriteRequest
        {
            RecordType = "Activator",
            Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties" },
            Verb = "Add",
            Struct = new StructSpec { Type = "LeveledItemEntry", Fields = new() },
        };
        var dErr = rb.Validate(wrongSpec);
        Check("D: a non-arm spec type still rejects", dErr is not null);
        Check("D2: …and the rejection lists the legal element types", dErr?.Contains("ScriptObjectProperty", StringComparison.Ordinal) == true, dErr);

        // ---- E: the QUST alias-script path (QuestAdapter) validates through the same surface. ----
        var questAlias = new WriteRequest
        {
            RecordType = "Quest",
            Path = new[] { "VirtualMachineAdapter", "Aliases[0]", "Scripts[0]", "Properties[0]", "Object" },
            Verb = "Set", Value = "00308D:Update.esm",
        };
        var eErr = rb.Validate(questAlias);
        Check("E: QUST alias-script arm-field path passes pre-flight", eErr is null, eErr);

        // ---- G: a DICT of polymorphic elements (Package.Data = Dictionary<sbyte,APackageData>) ACCEPTS a compose-Add
        //         — the dict analog of VMAD's list-of-arm compose (Gap 3 / PR-B: dict-element composition). ApplyDictVerb
        //         now BuildStructs the entry value and the gate validates the spec via the SAME StructElementLegality the
        //         list Add uses. RED before Gap 3: rejected 'dict-element composition is a later surface'. ----
        var dictCompose = new WriteRequest
        {
            RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
            Struct = new StructSpec { Type = "PackageDataBool", Fields = new() },
        };
        var gErr = rb.Validate(dictCompose);
        Check("G: dict arm-element compose-Add accepted (Gap 3)", gErr is null, gErr);

        // ---- G2: a Package.Data compose-Add whose type is NOT an APackageData arm still rejects, naming the legal arms
        //          — the dict compose validates its spec exactly like the list path (no blind accept). ----
        var dictBadArm = new WriteRequest
        {
            RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
            Struct = new StructSpec { Type = "Weapon", Fields = new() },
        };
        var g2Err = rb.Validate(dictBadArm);
        Check("G2: a non-arm dict compose type rejects, naming the legal arms",
            g2Err?.Contains("does not match", StringComparison.OrdinalIgnoreCase) == true
            && g2Err?.Contains("Legal element types", StringComparison.OrdinalIgnoreCase) == true, g2Err);

        // ---- H: a polymorphic-base whose arms are RECORDS (GameSetting → GameSettingBool/Float/Int/String)
        //         classifies Record, never Arm — the composition surface must not admit record-group families
        //         (PR review). ----
        var corpus = CorpusRulebook.LoadCorpus();
        var modType = corpus.Types.GetValueOrDefault("SkyrimMod");
        var gsField = modType?.Fields.FirstOrDefault(x => x.Name == "GameSettings");
        Check("H: record-family polymorphic base classifies Record (not composable)",
            gsField is not null && SchemaClassifier.ClassifyElement(gsField, corpus) == ElementKind.Record,
            gsField is null ? "SkyrimMod.GameSettings not found in corpus"
                : SchemaClassifier.ClassifyElement(gsField, corpus).ToString());

        // ---- F (optional, --source): END-TO-END through the engine's own ApplyVerb on a DeepCopy of a real
        //      scripted record — proving the pre-flight now admits exactly what the runtime can already do. ----
        var source = f.GetValueOrDefault("source");
        if (source is not null)
        {
            if (!File.Exists(source)) { Console.Error.WriteLine($"error: --source not found: {source}"); return 1; }
            var mod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE);
            var fk = FormKey.Factory("10EE3F:Skyrim.esm");      // ACTI MineOreBlackreach04 — the filed repro record
            var getter = mod.EnumerateMajorRecords().OfType<IActivatorGetter>().FirstOrDefault(r => r.FormKey == fk);
            if (getter?.VirtualMachineAdapter is null)
            {
                Check("F0: repro record 10EE3F with VMAD present in --source", false,
                    "record absent or unscripted — is --source a Skyrim SE Skyrim.esm?");
            }
            else
            {
                var copy = getter.DeepCopy();
                int before = copy.VirtualMachineAdapter!.Scripts[0].Properties.Count;

                WriteEngine.ApplyVerb(copy, setArmLeaf);
                var p0 = copy.VirtualMachineAdapter.Scripts[0].Properties[0] as ScriptObjectProperty;
                Check("F1: runtime Set of the arm field landed",
                    p0 is not null && p0.Object.FormKey.ToString() == "018C91:Skyrim.esm",
                    p0 is null ? "Properties[0] is not a ScriptObjectProperty" : p0.Object.FormKey.ToString());

                WriteEngine.ApplyVerb(copy, addArm);
                var added = copy.VirtualMachineAdapter.Scripts[0].Properties.LastOrDefault() as ScriptObjectProperty;
                Check("F2: runtime compose-Add of the arm element landed",
                    copy.VirtualMachineAdapter.Scripts[0].Properties.Count == before + 1
                        && added is { Name: "VmadGuardProp", Alias: -1 }
                        && added.Object.FormKey.ToString() == "00308D:Update.esm"
                        && added.Flags == ScriptProperty.Flag.Edited,
                    added is null ? "last element is not a ScriptObjectProperty"
                        : $"count {copy.VirtualMachineAdapter.Scripts[0].Properties.Count} (was {before}), Name={added.Name}, Alias={added.Alias}, Object={added.Object.FormKey}, Flags={added.Flags}");
            }
        }
        else
        {
            Console.WriteLine("  (skipped F: pass --source <path>\\Skyrim.esm for the end-to-end engine proof)");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "vmad-poly-guard: ALL PASS" : $"vmad-poly-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
