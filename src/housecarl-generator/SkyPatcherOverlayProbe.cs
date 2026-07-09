using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

// ======================================================================
//  SkyPatcherOverlayProbe — CI regression guard for the Wave-1 overlay
//  engine (SkyPatcherOverlay; plan dev/plans/SKYPATCHER_DISTRIBUTOR_
//  TOOL_PLAN_2026-07-08.md §5.3 apply-order replay + §2.3 tiered honesty).
//
//  Self-contained: an in-memory SkyrimMod weapon, the embedded catalog +
//  field map, a stub resolver — no game data, no MO2 instance.
//
//  THE RED-PROOF ARM (plan §7): the final damage asserts the exact value
//  ONLY the ordered, stateful, running-value replay produces:
//      a.ini  set 40   →  z.ini  ×2.5 = 100   →  z.ini  +11 = 111
//  Every wrong model lands elsewhere: last-write-wins ⇒ 40; mult/add off
//  the ORIGINAL value ⇒ 10×2.5=25 / 51; unordered set-after-mult ⇒ 100 or
//  125. Same for weight: a.ini sets 9, m.ini sets 2 ⇒ later file wins ⇒ 2.
// ======================================================================
public static class SkyPatcherOverlayProbe
{
    sealed class StubResolver : SkyPatcherOverlay.IFormResolver
    {
        public Dictionary<string, FormKey> EditorIds = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Plugins = new(StringComparer.OrdinalIgnoreCase);
        public FormKey? ResolveEditorId(string editorId, string? mutagenType)
            => EditorIds.TryGetValue(editorId, out var fk) ? fk : null;
        public string? ReadWinnerLeaf(FormKey donor, string path) => null;
        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record) => null;
        public bool PluginPresent(string pluginName) => Plugins.Contains(pluginName);
    }

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[skypatcher-overlay-guard] SkyPatcher overlay replay (Wave 1)");
        int failures = 0;

        var catalog = SkyPatcherCatalog.Load();
        var fieldMap = SkyPatcherFieldMap.Load();
        var weapCat = catalog.ForSubfolder("weapon")!;
        var weapMap = fieldMap.For("weapon", "Weapon");
        failures += Check("weapon field map present", weapMap is not null);
        if (weapMap is null) return Done(failures);

        // ---- fixture record: a weapon with known stats + one keyword + a sound to null-clear ----
        var mod = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var weap = mod.Weapons.AddNew();                    // 000800:HcSpOv.esp (mod default floor)
        var fk = weap.FormKey;
        weap.EditorID = "HcTestSword";
        weap.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 5, Value = 100 };
        weap.Critical = new CriticalData { Damage = 3 };
        var k1 = new FormKey(new ModKey("HcKw", ModType.Plugin), 0x801);
        var k2 = new FormKey(new ModKey("HcKw", ModType.Plugin), 0x802);
        var kEid = new FormKey(new ModKey("HcKw", ModType.Plugin), 0x803);
        weap.Keywords = new() { k1.ToLink<IKeywordGetter>() };
        weap.EquipSound.SetTo(new FormKey(new ModKey("HcSnd", ModType.Plugin), 0x900));

        var resolver = new StubResolver();
        resolver.Plugins.Add("HcSpOv.esp");
        resolver.EditorIds["HcNamedKeyword"] = kEid;

        var me = $"HcSpOv.esp|800";
        SkyPatcherOverlay.OrderedLine L(string file, int n, string text)
            => new(file, n, SkyPatcherParse.ParseLine(text));

        var lines = new[]
        {
            // a.ini — earliest in filename sort.
            L("a.ini", 1, $"filterByWeapons={me}:attackDamage=40:weight=9:keywordsToAdd=HcKw.esp|802"),
            // m.ini — a foreign-record line that must NOT apply, then the later weight set.
            L("m.ini", 1, "filterByWeapons=Other.esp|123:attackDamage=1"),
            L("m.ini", 2, $"filterByWeapons={me}:weight=2"),
            // z.ini — the stateful chain + the rest of the surface.
            L("z.ini", 1, $"filterByWeapons={me}:attackDamageMult=2.5"),
            L("z.ini", 2, "filterByWeapons=HcTestSword:attackDamageToAdd=11"),
            L("z.ini", 3, $"filterByWeapons={me}:critDamageSetToBase=true"),
            L("z.ini", 4, $"filterByWeapons={me}:mirrorWeapon=Skyrim.esm|139B9"),
            L("z.ini", 5, $"filterByWeapons={me}:restrictToSkills=twohanded:speed=9"),
            L("z.ini", 6, $"filterByWeapons={me}:notAnOp=1"),
            L("z.ini", 7, $"filterByWeapons={me}:fullName=~Reforged Blade~"),
            L("z.ini", 8, $"filterByWeapons={me}:animationType=bow:weaponHitType=no:soundLevel=silent"),
            L("z.ini", 9, $"filterByWeapons={me}:equipSound=null"),
            L("z.ini", 10, $"filterByWeapons={me}:minX=-7"),
            L("z.ini", 11, $"filterByWeapons={me}:keywordsToRemove=HcKw.esp|777"),      // absent → visible no-op
            L("z.ini", 12, $"filterByWeapons={me}:keywordsToRemove=HcKw.esp|801"),      // present → removed
            L("z.ini", 13, $"filterByKeywords=HcKw.esp|802:stagger=1.5"),               // keyword added EARLIER in the replay
            L("z.ini", 14, "filterByEditorIdContains=TestSw:reach=1.25"),
            L("z.ini", 15, "rangeMax=99"),                                              // no filter ⇒ applies to all of the type
            L("z.ini", 16, $"filterByWeaponsExcluded={me}:value=1"),                    // excluded ⇒ NOT applied
            L("z.ini", 17, $"hasPlugins=Missing.esp:filterByWeapons={me}:value=7"),     // gate fails ⇒ NOT applied
            L("z.ini", 18, $"hasPlugins=HcSpOv.esp:filterByWeapons={me}:value=777"),    // gate passes
            L("z.ini", 19, $"filterByWeapons={me}:keywordsToAdd=HcNamedKeyword"),       // EditorID value → resolver
            L("z.ini", 20, "bogusFilter=1:critPercentMult=3"),                          // unknown ONLY-filter must NOT become apply-all
        };

        var result = SkyPatcherOverlay.Apply(weap, fk, weap.EditorID, catalog, weapCat, weapMap, lines, resolver);

        // ---- THE RED-PROOF: ordered stateful replay (see header) ----
        failures += Check("damage replays 40 ×2.5 +11 = 111 (ordered, stateful, running-value)",
            weap.BasicStats!.Damage == 111, $"got {weap.BasicStats.Damage}");
        failures += Check("weight: later-sorted set wins (9 then 2 ⇒ 2)",
            Math.Abs(weap.BasicStats.Weight - 2) < 0.001, $"got {weap.BasicStats.Weight}");

        // ---- selection ----
        failures += Check("foreign-record line did NOT apply (damage untouched by Other.esp line)",
            result.Applied.All(a => a.File != "m.ini" || a.LineNumber != 1));
        failures += Check("EditorID primary filter matched (attackDamageToAdd applied)",
            result.Applied.Any(a => a.Op == "attackDamageToAdd"));
        failures += Check("Excluded connective skips the record (value never 1)",
            weap.BasicStats.Value == 777, $"got {weap.BasicStats.Value}");
        failures += Check("hasPlugins gates the line (7 skipped, 777 applied)",
            result.Applied.Count(a => a.Op == "value") == 1);
        failures += Check("no-filter line applies to the type (rangeMax=99)",
            Math.Abs(weap.Data!.RangeMax - 99) < 0.001, $"got {weap.Data.RangeMax}");
        failures += Check("keyword filter sees the RUNNING copy (stagger applied after keywordsToAdd)",
            Math.Abs(weap.Data.Stagger - 1.5) < 0.001, $"got {weap.Data.Stagger}");
        failures += Check("editorIdContains filter matched (reach=1.25)",
            Math.Abs(weap.Data.Reach - 1.25) < 0.001, $"got {weap.Data.Reach}");

        // ---- tiered honesty ----
        failures += Check("HARD op ⇒ a directive, not a value (mirrorWeapon)",
            result.Directives.Any(d => d.Op == "mirrorWeapon" && d.Reason.Contains("copy-from-form")),
            string.Join(" | ", result.Directives.Select(d => $"{d.Op}:{d.Reason}")));
        failures += Check("unevaluated filter ⇒ line skipped LOUD (restrictToSkills), speed untouched",
            result.LinesSkippedUnresolvedFilter >= 1
            && result.Warnings.Any(w => w.Contains("restrictToSkills") && w.Contains("UNRESOLVED"))
            && Math.Abs(weap.Data.Speed - 0) < 0.001, $"speed={weap.Data.Speed}");
        failures += Check("an unknown key poisons the WHOLE line, loud (notAnOp)",
            result.Warnings.Any(w => w.Contains("notAnOp") && w.Contains("UNRESOLVED")));
        failures += Check("unknown ONLY-filter line did NOT become apply-all (critPercentMult untouched)",
            weap.Critical!.PercentMult == 0
            && result.Warnings.Any(w => w.Contains("bogusFilter") && w.Contains("UNRESOLVED")),
            $"PercentMult={weap.Critical.PercentMult}");

        // ---- op surface ----
        failures += Check("rename strips the ~…~ wrapper", weap.Name?.String == "Reforged Blade", weap.Name?.String ?? "<null>");
        failures += Check("enum coerces ignore-case (animationType=bow)",
            weap.Data.AnimationType == WeaponAnimationType.Bow, weap.Data.AnimationType.ToString());
        failures += Check("valueMap translates the documented token (weaponHitType=no)",
            weap.Data.OnHit.ToString().StartsWith("No"), weap.Data.OnHit.ToString());
        failures += Check("enum without valueMap (soundLevel=silent)",
            weap.DetectionSoundLevel == SoundLevel.Silent, weap.DetectionSoundLevel.ToString());
        failures += Check("null clears the form field (equipSound)", weap.EquipSound.IsNull);
        failures += Check("vec component set (minX=-7, others untouched)",
            weap.ObjectBounds.First.X == -7 && weap.ObjectBounds.First.Y == 0, weap.ObjectBounds.First.ToString());
        failures += Check("critDamageSetToBase self-copies the RUNNING damage (111)",
            weap.Critical!.Damage == 111, $"got {weap.Critical.Damage}");
        failures += Check("keywordsToAdd accumulated + keywordsToRemove removed + EditorID keyword resolved",
            weap.Keywords is not null
            && !weap.Keywords.Any(k => k.FormKey == k1)
            && weap.Keywords.Any(k => k.FormKey == k2)
            && weap.Keywords.Any(k => k.FormKey == kEid),
            string.Join(",", weap.Keywords?.Select(k => k.FormKey.ToString()) ?? Array.Empty<string>()));
        failures += Check("absent-keyword remove is a VISIBLE no-op (named note)",
            result.Applied.Any(a => a.Op == "keywordsToRemove" && a.Note is { } n && n.Contains("not present")));

        // ---- accounting ----
        failures += Check("before/after tokens carried on the stateful ops",
            result.Applied.Any(a => a.Op == "attackDamageMult" && a.Before == "40" && a.After == "100"),
            string.Join(" | ", result.Applied.Where(a => a.Op == "attackDamageMult").Select(a => $"{a.Before}→{a.After}")));

        failures += NpcEntryOpsArm(catalog, fieldMap, mod);

        return Done(failures);
    }

    /// <summary>The struct-ENTRY collection semantics (addEntry/'='-pack, addEntryOnce, removeEntry,
    /// removeEntryByCount, replaceEntry, clearList, flagBool) exercised on an NPC's inventory + factions —
    /// the shapes the weapon arm can't reach. Includes the conditional-removal LOUD-skip guard (a qualified
    /// remove must NOT replay as an unconditional one — subagent review finding).</summary>
    static int NpcEntryOpsArm(SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyrimMod mod)
    {
        Console.WriteLine("  --- NPC entry-op arm (struct-entry collections) ---");
        int failures = 0;
        var npcCat = catalog.ForSubfolder("npc")!;
        var npcMap = fieldMap.For("npc", "Npc");
        failures += Check("npc field map present", npcMap is not null);
        if (npcMap is null) return failures;

        var itemA = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x900);
        var itemB = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x901);
        var itemC = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x902);
        var facA = new FormKey(new ModKey("HcFac", ModType.Plugin), 0xA01);

        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcTestNpc";
        npc.Items = new()
        {
            new ContainerEntry { Item = new ContainerItem { Item = itemA.ToLink<IItemGetter>(), Count = 2 } },
        };

        var me = $"HcSpOv.esp|{npc.FormKey.ID:X}";
        SkyPatcherOverlay.OrderedLine L(string file, int n, string text)
            => new(file, n, SkyPatcherParse.ParseLine(text));

        var resolver = new StubResolver();
        var lines = new[]
        {
            L("a.ini", 1, $"filterByNpcs={me}:objectsToAdd=HcItm.esp|901=3"),                    // '='-packed addEntry
            L("a.ini", 2, $"filterByNpcs={me}:factionsToAdd=HcFac.esp|A01=2"),                   // '='-packed, rank sub-field
            L("a.ini", 3, $"filterByNpcs={me}:addOnceToInventory=HcItm.esp|901~5"),              // '~'-packed (unlike objectsToAdd!); already present ⇒ no-op
            L("z.ini", 1, $"filterByNpcs={me}:removeInventoryObjectsByCount=HcItm.esp|901~1"),   // count 3 → 2
            L("z.ini", 2, $"filterByNpcs={me}:objectsToReplace=HcItm.esp|900~HcItm.esp|902"),    // retarget A → C
            L("z.ini", 3, $"filterByNpcs={me}:objectsToRemove=HcItm.esp|902~5"),                 // QUALIFIED remove ⇒ loud skip
            L("z.ini", 4, $"filterByNpcs={me}:setEssential=true"),                               // flagBool
        };
        var r = SkyPatcherOverlay.Apply(npc, npc.FormKey, npc.EditorID, catalog, npcCat, npcMap, lines, resolver);

        var entries = npc.Items!.Select(e => (fk: e.Item.Item.FormKey, count: e.Item.Count)).ToList();
        failures += Check("'='-packed addEntry landed (itemB count 3 → byCount → 2)",
            entries.Any(e => e.fk == itemB && e.count == 2),
            string.Join(" | ", entries.Select(e => $"{e.fk}×{e.count}")));
        failures += Check("addOnce on a present entry is a visible no-op",
            r.Applied.Any(a => a.Op == "addOnceToInventory" && a.Note is { } n && n.Contains("no-op"))
            && entries.Count(e => e.fk == itemB) == 1);
        failures += Check("replaceEntry retargeted itemA → itemC, count preserved",
            !entries.Any(e => e.fk == itemA) && entries.Any(e => e.fk == itemC && e.count == 2),
            string.Join(" | ", entries.Select(e => $"{e.fk}×{e.count}")));
        failures += Check("QUALIFIED removal skipped LOUD (itemC still present, warning names the gap)",
            entries.Any(e => e.fk == itemC)
            && r.Warnings.Any(w => w.Contains("objectsToRemove") && w.Contains("NOT applied")),
            string.Join(" ; ", r.Warnings));
        failures += Check("factionsToAdd entry with rank sub-field",
            npc.Factions.Any(f => f.Faction.FormKey == facA && f.Rank == 2),
            string.Join(" | ", npc.Factions.Select(f => $"{f.Faction.FormKey}@{f.Rank}")));
        failures += Check("flagBool set (setEssential=true)",
            npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential));
        return failures;
    }

    static int Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"  [{detail}]")}");
        return ok ? 0 : 1;
    }

    static int Done(int failures)
    {
        Console.WriteLine(failures == 0
            ? "[skypatcher-overlay-guard] PASS — the ordered stateful replay holds."
            : $"[skypatcher-overlay-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
    }
}
