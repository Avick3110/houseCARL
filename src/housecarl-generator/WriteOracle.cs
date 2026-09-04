using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// The write oracle — per-kind byte-identical cells.
///
/// Each cell performs the same logical mutation two ways: <b>Path A</b> through the generic
/// reflection engine (<see cref="WriteEngine"/>), <b>Path B</b> via a hand-written typed Mutagen
/// setter. The two output plugins must be <b>byte-identical</b>. Because the engine is blind to which
/// record it edits (pure reflection), proving each KIND proves every record carrying that kind — this
/// is how full coverage is validated without hand-authoring ~9,000 per-field oracles. Source-unchanged
/// is asserted by SHA across the whole run.
///
/// Run: <c>dotnet run --project src/housecarl-generator oracle</c>
/// </summary>
public static class WriteOracle
{
    const string DefaultSourcePath =
        @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";
    static readonly ModKey PatchKey = new("HousecarlWriteProof", ModType.Plugin);

    public static int Run(string[] args)
    {
        var src = args.Length > 0 ? args[0] : DefaultSourcePath;
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source not found: {src}"); return 1; }

        var outDir = Path.GetFullPath(Path.Combine("write-output", "oracle"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        var shaBefore = Sha(src);

        var ironBoots = FormKey.Factory("012E4B:Skyrim.esm");
        const string kwIron = "06BBE2:Skyrim.esm";
        const string kwIron2 = "06BBE3:Skyrim.esm";

        // dynamically locate an NPC carrying SkillValues[OneHanded]
        var probe = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        FormKey npcFk = default;
        foreach (var n in probe.Npcs)
            if (n.PlayerSkills?.SkillValues?.ContainsKey(Skill.OneHanded) == true) { npcFk = n.FormKey; break; }
        if (npcFk == default) { Console.Error.WriteLine("error: no NPC with SkillValues[OneHanded]"); return 1; }

        FormKey mgefFk = default;
        foreach (var m in probe.MagicEffects) { mgefFk = m.FormKey; break; }
        if (mgefFk == default) { Console.Error.WriteLine("error: no MagicEffect found"); return 1; }

        var armorType = Enum.GetValues<ArmorType>()[^1];
        var actorValue = Enum.GetValues<ActorValue>()[0];

        var cells = new List<Cell>
        {
            Cell.Armor("scalar float   Armor.Weight=50", ironBoots,
                new() { RecordType = "Armor", Path = ["Weight"], Verb = "Set", Value = "50" },
                a => a.Weight = 50f),
            Cell.Armor("scalar uint    Armor.Value=999", ironBoots,
                new() { RecordType = "Armor", Path = ["Value"], Verb = "Set", Value = "999" },
                a => a.Value = 999),
            Cell.Armor("scalar string  Armor.EditorID", ironBoots,
                new() { RecordType = "Armor", Path = ["EditorID"], Verb = "Set", Value = "HC_OracleTest" },
                a => a.EditorID = "HC_OracleTest"),
            Cell.Armor($"enum-in-substruct  BodyTemplate.ArmorType={armorType}", ironBoots,
                new() { RecordType = "Armor", Path = ["BodyTemplate", "ArmorType"], Verb = "Set", Value = armorType.ToString() },
                a => a.BodyTemplate!.ArmorType = armorType),
            Cell.Armor("formlink       Armor.Race", ironBoots,
                new() { RecordType = "Armor", Path = ["Race"], Verb = "Set", Value = "013746:Skyrim.esm" },
                a => a.Race = new FormLinkNullable<IRaceGetter>(FormKey.Factory("013746:Skyrim.esm"))),
            Cell.Armor("formlink-list Add   Armor.Keywords", ironBoots,
                new() { RecordType = "Armor", Path = ["Keywords"], Verb = "Add", Value = kwIron },
                a => a.Keywords!.Add(new FormLink<IKeywordGetter>(FormKey.Factory(kwIron)))),
            Cell.Armor("list SetAtIndex     Armor.Keywords[0]", ironBoots,
                new() { RecordType = "Armor", Path = ["Keywords"], Verb = "SetAtIndex", Key = "0", Value = kwIron },
                a => a.Keywords![0] = new FormLink<IKeywordGetter>(FormKey.Factory(kwIron))),
            Cell.Armor("list InsertAtIndex  Armor.Keywords[0]", ironBoots,
                new() { RecordType = "Armor", Path = ["Keywords"], Verb = "InsertAtIndex", Key = "0", Value = kwIron },
                a => a.Keywords!.Insert(0, new FormLink<IKeywordGetter>(FormKey.Factory(kwIron)))),
            Cell.Armor("list Remove         Armor.Keywords[0]", ironBoots,
                new() { RecordType = "Armor", Path = ["Keywords"], Verb = "Remove", Key = "0" },
                a => a.Keywords!.RemoveAt(0)),
            Cell.Armor("list ReplaceAll     Armor.Keywords", ironBoots,
                new() { RecordType = "Armor", Path = ["Keywords"], Verb = "ReplaceAll", Values = [kwIron, kwIron2] },
                a => { a.Keywords!.Clear(); a.Keywords.Add(new FormLink<IKeywordGetter>(FormKey.Factory(kwIron))); a.Keywords.Add(new FormLink<IKeywordGetter>(FormKey.Factory(kwIron2))); }),
            Cell.Npc("dict Set     Npc.SkillValues[OneHanded]=50", npcFk,
                new() { RecordType = "Npc", Path = ["PlayerSkills", "SkillValues"], Verb = "Set", Key = "OneHanded", Value = "50" },
                n => n.PlayerSkills!.SkillValues[Skill.OneHanded] = 50),
            Cell.Npc("dict Remove  Npc.SkillValues[OneHanded]", npcFk,
                new() { RecordType = "Npc", Path = ["PlayerSkills", "SkillValues"], Verb = "Remove", Key = "OneHanded" },
                n => n.PlayerSkills!.SkillValues.Remove(Skill.OneHanded)),
            Cell.Npc("dict Merge   Npc.SkillValues +Block,Smithing", npcFk,
                new() { RecordType = "Npc", Path = ["PlayerSkills", "SkillValues"], Verb = "Merge", Entries = new() { ["Block"] = "80", ["Smithing"] = "90" } },
                n => { n.PlayerSkills!.SkillValues[Skill.Block] = 80; n.PlayerSkills.SkillValues[Skill.Smithing] = 90; }),
            Cell.Mgef("polymorphic arm-swap MagicEffect.Archetype->Light", mgefFk,
                new()
                {
                    RecordType = "MagicEffect", Path = ["Archetype"], Verb = "Set",
                    Struct = new StructSpec
                    {
                        Type = "MagicEffectLightArchetype",
                        Fields = new() { ["ActorValue"] = actorValue.ToString(), ["Association"] = "00C2A8:Skyrim.esm" },
                    },
                },
                m => m.Archetype = new MagicEffectLightArchetype
                {
                    ActorValue = actorValue,
                    Association = new FormLink<ILightGetter>(FormKey.Factory("00C2A8:Skyrim.esm")),
                }),
            // --- value-type coercion kinds ---
            Cell.Generic("value Color    Activator.MarkerColor=255,128,0",
                m => m.Activators.FirstOrDefault(),
                new() { RecordType = "Activator", Path = ["MarkerColor"], Verb = "Set", Value = "255,128,0" },
                r => ((IActivator)r).MarkerColor = System.Drawing.Color.FromArgb(255, 128, 0)),
            Cell.Generic("value Percent  LeveledItem.ChanceNone=0.25",
                m => m.LeveledItems.FirstOrDefault(),
                new() { RecordType = "LeveledItem", Path = ["ChanceNone"], Verb = "Set", Value = "0.25" },
                r => ((ILeveledItem)r).ChanceNone = new Noggog.Percent(0.25)),
            Cell.Generic("value MemSlice ActorValueInformation.CNAM=DEADBEEF",
                m => m.ActorValueInformation.FirstOrDefault(),
                new() { RecordType = "ActorValueInformation", Path = ["CNAM"], Verb = "Set", Value = "DEADBEEF" },
                r => ((IActorValueInformation)r).CNAM = Convert.FromHexString("DEADBEEF")),
            Cell.Armor("substruct-whole TranslatedString Armor.Name", ironBoots,
                new() { RecordType = "Armor", Path = ["Name"], Verb = "Set", Value = "HC Test Boots" },
                a => a.Name = "HC Test Boots"),
            // --- collection-nav: edit a sub-field INSIDE a list element (Spell.Effects[0]) ---
            // Proves list-element → substruct → scalar and list-element → formlink are byte-identical to a
            // hand-typed Mutagen edit, exactly as the other cells prove their kinds.
            Cell.Generic("collnav scalar   Spell.Effects[0].Data.Magnitude=25",
                m => m.Spells.FirstOrDefault(s => s.Effects.Count > 0 && s.Effects[0].Data is not null),
                new() { RecordType = "Spell", Path = ["Effects[0]", "Data", "Magnitude"], Verb = "Set", Value = "25" },
                r => ((ISpell)r).Effects[0].Data!.Magnitude = 25f),
            Cell.Generic("collnav formlink Spell.Effects[0].BaseEffect",
                m => m.Spells.FirstOrDefault(s => s.Effects.Count > 0),
                new() { RecordType = "Spell", Path = ["Effects[0]", "BaseEffect"], Verb = "Set", Value = "013CA9:Skyrim.esm" },
                r => ((ISpell)r).Effects[0].BaseEffect = new FormLinkNullable<IMagicEffectGetter>(FormKey.Factory("013CA9:Skyrim.esm"))),
            // --- composition: ADD a NEW modeled struct element to a list, built FROM PARTS ---
            // Proves the build-from-parts path (StructSpec -> recursive BuildStruct, including the nested Data
            // substruct materialized on first write) is byte-identical to a hand-typed Mutagen Add.
            Cell.Generic("struct-elem Add  LeveledItem.Entries+=Entry{Data}",
                m => m.LeveledItems.FirstOrDefault(l => l.Entries is { Count: > 0 }),
                new()
                {
                    RecordType = "LeveledItem", Path = ["Entries"], Verb = "Add",
                    Struct = new StructSpec
                    {
                        Type = "LeveledItemEntry",
                        Sets = new()
                        {
                            new() { RecordType = "LeveledItemEntry", Path = ["Data", "Level"], Verb = "Set", Value = "5" },
                            new() { RecordType = "LeveledItemEntry", Path = ["Data", "Reference"], Verb = "Set", Value = "012E4B:Skyrim.esm" },
                            new() { RecordType = "LeveledItemEntry", Path = ["Data", "Count"], Verb = "Set", Value = "1" },
                        },
                    },
                },
                r => ((ILeveledItem)r).Entries!.Add(new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData
                    {
                        Level = 5,
                        Reference = new FormLink<IItemGetter>(FormKey.Factory("012E4B:Skyrim.esm")),
                        Count = 1,
                    },
                })),
        };

        int pass = 0;
        foreach (var c in cells)
        {
            var dir = Path.Combine(outDir, Slug(c.Name));
            var pa = Path.Combine(dir, "a", "HousecarlWriteProof.esp");
            var pb = Path.Combine(dir, "b", "HousecarlWriteProof.esp");
            Console.Write($"  {c.Name,-44} ");
            try
            {
                c.RunA(src, pa);
                c.RunB(src, pb);
                if (Compare(pa, pb)) { Console.WriteLine("PASS"); pass++; }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException is { } ie ? $" / {ie.GetType().Name}: {ie.Message}" : "";
                Console.WriteLine($"EXCEPTION {ex.GetType().Name}: {ex.Message}{inner}");
            }
        }

        var shaAfter = Sha(src);
        Console.WriteLine();
        Console.WriteLine($"Source unchanged: {(shaBefore == shaAfter ? "YES" : "NO")}");
        Console.WriteLine($"=== Oracle: {pass}/{cells.Count} cells byte-identical ===");
        return pass == cells.Count && shaBefore == shaAfter ? 0 : 1;
    }

    sealed class Cell
    {
        public required string Name { get; init; }
        public required Action<string, string> RunA { get; init; }  // (source, outPath)
        public required Action<string, string> RunB { get; init; }

        public static Cell Armor(string name, FormKey fk, WriteRequest req, Action<Mutagen.Bethesda.Skyrim.Armor> edit) => new()
        {
            Name = name,
            RunA = (src, o) =>
            {
                var m = Open(src);
                if (!m.Armors.TryGetValue(fk, out var r)) throw new InvalidOperationException($"Armor {fk} not found");
                var p = NewPatch();
                WriteEngine.ApplyVerb(WriteEngine.GenericGetOrAddAsOverride(p, r), req);
                WriteEngine.WritePatch(p, m, o);
            },
            RunB = (src, o) =>
            {
                var m = Open(src);
                if (!m.Armors.TryGetValue(fk, out var r)) throw new InvalidOperationException($"Armor {fk} not found");
                var p = NewPatch();
                edit(p.Armors.GetOrAddAsOverride(r));
                WriteEngine.WritePatch(p, m, o);
            },
        };

        public static Cell Npc(string name, FormKey fk, WriteRequest req, Action<Mutagen.Bethesda.Skyrim.Npc> edit) => new()
        {
            Name = name,
            RunA = (src, o) =>
            {
                var m = Open(src);
                if (!m.Npcs.TryGetValue(fk, out var r)) throw new InvalidOperationException($"Npc {fk} not found");
                var p = NewPatch();
                WriteEngine.ApplyVerb(WriteEngine.GenericGetOrAddAsOverride(p, r), req);
                WriteEngine.WritePatch(p, m, o);
            },
            RunB = (src, o) =>
            {
                var m = Open(src);
                if (!m.Npcs.TryGetValue(fk, out var r)) throw new InvalidOperationException($"Npc {fk} not found");
                var p = NewPatch();
                edit(p.Npcs.GetOrAddAsOverride(r));
                WriteEngine.WritePatch(p, m, o);
            },
        };

        public static Cell Mgef(string name, FormKey fk, WriteRequest req, Action<Mutagen.Bethesda.Skyrim.MagicEffect> edit) => new()
        {
            Name = name,
            RunA = (src, o) =>
            {
                var m = Open(src);
                if (!m.MagicEffects.TryGetValue(fk, out var r)) throw new InvalidOperationException($"MagicEffect {fk} not found");
                var p = NewPatch();
                WriteEngine.ApplyVerb(WriteEngine.GenericGetOrAddAsOverride(p, r), req);
                WriteEngine.WritePatch(p, m, o);
            },
            RunB = (src, o) =>
            {
                var m = Open(src);
                if (!m.MagicEffects.TryGetValue(fk, out var r)) throw new InvalidOperationException($"MagicEffect {fk} not found");
                var p = NewPatch();
                edit(p.MagicEffects.GetOrAddAsOverride(r));
                WriteEngine.WritePatch(p, m, o);
            },
        };

        /// <summary>Type-blind cell for proving a coercion KIND on whatever record carries it. Path B still does an
        /// independent HAND-WRITTEN typed assignment (the value under test); both paths share the already-proven
        /// generic override mechanism, which is not what these cells exercise.</summary>
        public static Cell Generic(string name, Func<ISkyrimModGetter, IMajorRecordGetter?> find, WriteRequest req, Action<IMajorRecord> edit) => new()
        {
            Name = name,
            RunA = (src, o) =>
            {
                var m = Open(src);
                var r = find(m) ?? throw new InvalidOperationException($"{name}: no source record found");
                var p = NewPatch();
                WriteEngine.ApplyVerb(WriteEngine.GenericGetOrAddAsOverride(p, r), req);
                WriteEngine.WritePatch(p, m, o);
            },
            RunB = (src, o) =>
            {
                var m = Open(src);
                var r = find(m) ?? throw new InvalidOperationException($"{name}: no source record found");
                var p = NewPatch();
                edit(WriteEngine.GenericGetOrAddAsOverride(p, r));
                WriteEngine.WritePatch(p, m, o);
            },
        };
    }

    static ISkyrimModGetter Open(string src) => SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
    static SkyrimMod NewPatch() => new(PatchKey, SkyrimRelease.SkyrimSE);

    static bool Compare(string a, string b)
    {
        var ba = File.ReadAllBytes(a);
        var bb = File.ReadAllBytes(b);
        if (ba.Length != bb.Length)
        {
            Console.Write($"SIZE {ba.Length}!={bb.Length} ");
            return false;
        }
        for (int i = 0; i < ba.Length; i++)
            if (ba[i] != bb[i])
            {
                Console.Write($"DIVERGE@0x{i:X} A=0x{ba[i]:X2} B=0x{bb[i]:X2} ");
                return false;
            }
        return true;
    }

    static string Sha(string path)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    static string Slug(string s) =>
        new string(s.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray());
}
