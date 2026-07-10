using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
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
        public Dictionary<FormKey, string> WinnerLeafs = new();       // (donor) → the one leaf the test reads
        public Dictionary<FormKey, IReadOnlyList<FormKey>> Keywords = new();
        public Dictionary<FormKey, string> Winners = new();
        public Dictionary<FormKey, string> Eids = new();
        public string? ReadWinnerLeaf(FormKey donor, string path) => WinnerLeafs.GetValueOrDefault(donor);
        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record) => Keywords.GetValueOrDefault(record);
        public bool PluginPresent(string pluginName) => Plugins.Contains(pluginName);
        public string? WinnerPluginOf(FormKey record) => Winners.GetValueOrDefault(record);
        public string? EditorIdOf(FormKey record) => Eids.GetValueOrDefault(record);
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
            L("a.ini", 1, $"filterByWeapons={me}:attackDamage=40:weight=9:keywordsToAdd=HcKw.esp|802:skillType=onehanded"),
            // m.ini — a foreign-record line that must NOT apply, then the later weight set.
            L("m.ini", 1, "filterByWeapons=Other.esp|123:attackDamage=1"),
            L("m.ini", 2, $"filterByWeapons={me}:weight=2"),
            // z.ini — the stateful chain + the rest of the surface.
            L("z.ini", 1, $"filterByWeapons={me}:attackDamageMult=2.5"),
            L("z.ini", 2, "filterByWeapons=HcTestSword:attackDamageToAdd=11"),
            L("z.ini", 3, $"filterByWeapons={me}:critDamageSetToBase=true"),
            L("z.ini", 4, $"filterByWeapons={me}:mirrorWeapon=Skyrim.esm|139B9"),
            L("z.ini", 5, $"filterByWeapons={me}:restrictToSkills=twohanded:speed=9"),   // Wave 2: EVALUATES (no-match on a onehanded sword)
            L("z.ini", 25, $"filterByWeapons={me}:restrictToSkills=onehanded:speed=6"), // Wave 2: matches
            L("z.ini", 26, $"filterByWeapons={me}:filterByHasAmmoFromWeaponList=1:stagger=7"),  // explicitly unmapped filter ⇒ loud skip
            L("z.ini", 27, $"filterByModNames=HcSpOv.esp:rangeMin=11"),                 // origin-plugin match (built-in)
            L("z.ini", 28, $"filterByModNamesExcluded=HcSpOv.esp:rangeMin=99"),         // excluded ⇒ NOT applied
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
            L("z.ini", 20, "bogusFilter=1:stagger=9"),                                  // unknown ONLY-filter must NOT become apply-all
            L("z.ini", 21, "filterByWeapons=HcSpOv.esp|FE000800:enchantAmount=77"),     // full load-indexed ESL FormID (FExxxYYY)
            L("z.ini", 22, $"filterByWeapons={me}:reach="),                             // EMPTY value must warn, not silently no-op
            L("z.ini", 23, $"filterByWeapons={me}:model=NotARealDonor"),                // unresolvable dot-less donor must NOT become a "model path"
            L("z.ini", 24, $"filterByWeapons={me}:critPercentMult=3"),                  // SET of the CRDT multiplier field (0 → 3; a multiply model yields 0)
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
        failures += Check("restrictToSkills EVALUATES (Wave 2): twohanded no-match, onehanded applies (speed=6)",
            Math.Abs(weap.Data.Speed - 6) < 0.001
            && !result.Warnings.Any(w => w.Contains("restrictToSkills") && w.Contains("UNRESOLVED")),
            $"speed={weap.Data.Speed}");
        failures += Check("explicitly-unmapped filter ⇒ line skipped LOUD (filterByHasAmmoFromWeaponList), stagger untouched",
            result.LinesSkippedUnresolvedFilter >= 1
            && result.Warnings.Any(w => w.Contains("filterByHasAmmoFromWeaponList") && w.Contains("no static evaluation"))
            && Math.Abs(weap.Data.Stagger - 1.5) < 0.001, $"stagger={weap.Data.Stagger}");
        failures += Check("filterByModNames matches the defining master; Excluded skips (rangeMin=11)",
            Math.Abs(weap.Data.RangeMin - 11) < 0.001, $"rangeMin={weap.Data.RangeMin}");
        failures += Check("an unknown key poisons the WHOLE line, loud (notAnOp)",
            result.Warnings.Any(w => w.Contains("notAnOp") && w.Contains("UNRESOLVED")));
        failures += Check("unknown ONLY-filter line did NOT become apply-all (stagger untouched by z:20)",
            Math.Abs(weap.Data!.Stagger - 1.5) < 0.001
            && result.Warnings.Any(w => w.Contains("bogusFilter") && w.Contains("UNRESOLVED")),
            $"stagger={weap.Data.Stagger}");
        failures += Check("full load-indexed ESL FormID (FE000800) matches the record (review #4)",
            weap.EnchantmentAmount == 77, $"got {weap.EnchantmentAmount}");
        failures += Check("empty set value warns loud, does not silently no-op (review #8)",
            Math.Abs(weap.Data.Reach - 1.25) < 0.001
            && result.Warnings.Any(w => w.Contains("'reach='") && w.Contains("no value")),
            $"reach={weap.Data.Reach}");
        failures += Check("unresolvable dot-less model donor warns, never written verbatim as a path (review #6)",
            (weap.Model?.File.GivenPath ?? "") != "NotARealDonor"
            && result.Warnings.Any(w => w.Contains("NotARealDonor") && w.Contains("not resolvable")),
            weap.Model?.File.GivenPath ?? "<no model>");
        failures += Check("critPercentMult is a SET of the CRDT multiplier field (0 → 3, not 0×3)",
            Math.Abs(weap.Critical!.PercentMult - 3) < 0.001, $"got {weap.Critical.PercentMult}");

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
        failures += LeveledListScopeArm(catalog, fieldMap, mod);
        failures += FormListArm(catalog, fieldMap, mod);
        failures += Wave2FilterArm(catalog, fieldMap, mod);
        failures += Wave2OpClosuresArm(catalog, fieldMap, mod);

        return Done(failures);
    }

    /// <summary>The Wave-2 unmapped-op CLOSURES — the five semantics that turn 47 explicitly-unmapped
    /// entries into resolved post-state: dict-keyed race stats (Set + stateful Mult), Cell per-channel
    /// colours (token splice, alpha preserved), armor biped-slot INDEX bit math, Book's polymorphic
    /// Teaches compose-Set, and the COBJ set-entry-count (incl. the null=ALL form).</summary>
    static int Wave2OpClosuresArm(SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyrimMod mod)
    {
        Console.WriteLine("  --- Wave-2 op-closure arm (dict / colour / biped bits / Teaches / entry count) ---");
        int failures = 0;
        var resolver = new StubResolver();
        SkyPatcherOverlay.OrderedLine L(int n, string text) => new("c.ini", n, SkyPatcherParse.ParseLine(text));

        // ---- race: dictSet + dictMult on Starting/Regen (stateful chain: set 100 then ×1.5 = 150) ----
        {
            var race = mod.Races.AddNew();
            race.EditorID = "HcStatRace";
            race.Starting[BasicStat.Health] = 50;
            race.Regen[BasicStat.Magicka] = 3;
            var r = SkyPatcherOverlay.Apply(race, race.FormKey, race.EditorID, catalog, catalog.ForSubfolder("race")!,
                fieldMap.For("race", "Race"), new[]
                {
                    L(1, "startingHealth=100"),
                    L(2, "startingHealthMult=1.5"),      // runs on the RUNNING 100, not the original 50
                    L(3, "regenMagickaMult=2"),
                    L(4, "startingStaminaMult=2"),        // ABSENT key ⇒ loud skip, never invented
                }, resolver);
            failures += Check("race dict stats: set 100 then ×1.5 = 150 (ordered, stateful, running-value)",
                Math.Abs(race.Starting[BasicStat.Health] - 150) < 0.001, $"got {race.Starting[BasicStat.Health]}");
            failures += Check("race regen dict mult (3 × 2 = 6)",
                Math.Abs(race.Regen[BasicStat.Magicka] - 6) < 0.001, $"got {race.Regen[BasicStat.Magicka]}");
            failures += Check("dict mult on an ABSENT key skips LOUD, never invents a base value",
                !race.Starting.ContainsKey(BasicStat.Stamina)
                && r.Warnings.Any(w => w.Contains("startingStaminaMult") && w.Contains("no current value")));
        }

        // ---- cell: per-channel colour splice (alpha + sibling channels preserved) ----
        {
            var cell = new Cell(new FormKey(new ModKey("HcSpOv", ModType.Plugin), 0xCE2), SkyrimRelease.SkyrimSE);
            cell.EditorID = "HcColorCell";
            cell.Lighting = new CellLighting { AmbientColor = System.Drawing.Color.FromArgb(255, 10, 20, 30) };
            var me = "HcSpOv.esp|CE2";
            SkyPatcherOverlay.Apply(cell, cell.FormKey, cell.EditorID, catalog, catalog.ForSubfolder("cell")!,
                fieldMap.For("cell", "Cell"), new[]
                {
                    L(1, $"filterByCells={me}:ambientRed=64"),
                    L(2, $"filterByCells={me}:ambientBlue=128"),
                    L(3, $"filterByCells={me}:directionalAmbientXMinGreen=99"),   // nested AmbientColors struct
                }, resolver);
            var c = cell.Lighting!.AmbientColor;
            failures += Check("colour channels spliced independently (R=64, G kept 20, B=128, A kept 255)",
                c.R == 64 && c.G == 20 && c.B == 128 && c.A == 255, $"got {c.R},{c.G},{c.B},{c.A}");
            failures += Check("nested directional-ambient channel landed (XMinus.G=99)",
                cell.Lighting.AmbientColors?.DirectionalXMinus.G == 99,
                cell.Lighting.AmbientColors?.DirectionalXMinus.ToString() ?? "<null>");
        }

        // ---- armor: biped-slot INDEX bit math on the ops side (slot 41 = index 11; 42 = 12) ----
        {
            var ar = mod.Armors.AddNew();
            ar.EditorID = "HcSlotArmor";
            ar.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.LongHair };   // index 11 (slot 41)
            SkyPatcherOverlay.Apply(ar, ar.FormKey, ar.EditorID, catalog, catalog.ForSubfolder("armor")!,
                fieldMap.For("armor", "Armor"), new[]
                {
                    L(1, "bipedSlotsToRemove=11:bipedSlotsToAdd=12"),   // the armor.md example line
                }, resolver);
            failures += Check("bipedSlots ops: index 11 cleared, 12 set (the documented example)",
                !ar.BodyTemplate!.FirstPersonFlags.HasFlag(BipedObjectFlag.LongHair)
                && ar.BodyTemplate.FirstPersonFlags.HasFlag(BipedObjectFlag.Circlet),
                ar.BodyTemplate.FirstPersonFlags.ToString());
        }

        // ---- book: the polymorphic Teaches compose-Set (spell arm, then skill arm overwrites) ----
        {
            var spellFk = new FormKey(new ModKey("HcSpl", ModType.Plugin), 0xF10);
            var book = mod.Books.AddNew();
            book.EditorID = "HcTome";
            var r = SkyPatcherOverlay.Apply(book, book.FormKey, book.EditorID, catalog, catalog.ForSubfolder("book")!,
                fieldMap.For("book", "Book"), new[]
                {
                    L(1, "teachSpell=HcSpl.esp|F10"),
                    L(2, "teachSkill=marksman"),          // valueMap marksman→Archery; later line wins
                }, resolver);
            failures += Check("teachSpell composed the BookSpell arm (visible in the applied note)",
                r.Applied.Any(a => a.Op == "teachSpell" && a.Note == "Teaches → BookSpell"));
            failures += Check("teachSkill overwrote with the BookSkill arm (marksman→Archery)",
                book.Teaches is BookSkill bs && bs.Skill == Skill.Archery,
                book.Teaches?.GetType().Name ?? "<null>");
        }

        // ---- cobj: set-entry-count (specific form, then null = ALL) ----
        {
            var ingotA = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x930);
            var ingotB = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x931);
            var c = mod.ConstructibleObjects.AddNew();
            c.EditorID = "HcCountRecipe";
            c.Items = new()
            {
                new ContainerEntry { Item = new ContainerItem { Item = ingotA.ToLink<IItemGetter>(), Count = 5 } },
                new ContainerEntry { Item = new ContainerItem { Item = ingotB.ToLink<IItemGetter>(), Count = 5 } },
            };
            SkyPatcherOverlay.Apply(c, c.FormKey, c.EditorID, catalog, catalog.ForSubfolder("constructibleObject")!,
                fieldMap.For("constructibleObject", "ConstructibleObject"), new[]
                {
                    L(1, "changeCobjsCount=HcItm.esp|930~2"),   // one ingredient → 2
                }, resolver);
            var counts = c.Items!.Select(e => (fk: e.Item.Item.FormKey, n: e.Item.Count)).ToList();
            failures += Check("setEntryCount: the matching ingredient set to 2, the other untouched",
                counts.Any(e => e.fk == ingotA && e.n == 2) && counts.Any(e => e.fk == ingotB && e.n == 5),
                string.Join(" | ", counts.Select(e => $"{e.fk}×{e.n}")));

            SkyPatcherOverlay.Apply(c, c.FormKey, c.EditorID, catalog, catalog.ForSubfolder("constructibleObject")!,
                fieldMap.For("constructibleObject", "ConstructibleObject"), new[]
                {
                    L(1, "changeCobjsCount=null~0"),            // the documented null form: ALL entries
                }, resolver);
            failures += Check("setEntryCount null form hits EVERY entry (all counts 0)",
                c.Items!.All(e => e.Item.Count == 0),
                string.Join(" | ", c.Items!.Select(e => $"{e.Item.Item.FormKey}×{e.Item.Count}")));
        }

        return failures;
    }

    /// <summary>The Wave-2 filter surface — one representative per evaluation kind plus the danger
    /// cases where a wrong model lands elsewhere: the yield/skip filters (match means the line does
    /// NOT apply — an un-inverted model applies exactly the lines the author asked to suppress), the
    /// player rule (an apply-all model patches the player the grammar excludes), enum valueMaps
    /// (marksman→Archery), donor reads, and the explicit unmapped-filter loud skip.</summary>
    static int Wave2FilterArm(SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyrimMod mod)
    {
        Console.WriteLine("  --- Wave-2 filter arm (map-driven + override-aware + player rule) ---");
        int failures = 0;
        var resolver = new StubResolver();
        SkyPatcherOverlay.OrderedLine L(int n, string text) => new("f.ini", n, SkyPatcherParse.ParseLine(text));

        // ---- weapon: enumEquals + valueMap (marksman→Archery), flagAnyOf (boundweapon) ----
        {
            var w = mod.Weapons.AddNew();
            w.EditorID = "HcBow";
            w.Data = new WeaponData { Skill = Skill.Archery, AnimationType = WeaponAnimationType.Bow, Flags = WeaponData.Flag.BoundWeapon };
            w.BasicStats = new WeaponBasicStats { Damage = 5, Weight = 1, Value = 1 };
            var r = SkyPatcherOverlay.Apply(w, w.FormKey, w.EditorID, catalog, catalog.ForSubfolder("weapon")!,
                fieldMap.For("weapon", "Weapon"), new[]
                {
                    L(1, "filterBySkills=marksman:attackDamage=21"),           // valueMap marksman→Archery
                    L(2, "filterByAnimationTypeOr=crossbow:attackDamage=1"),   // enum no-match
                    L(3, "restrictToFlags=boundweapon:weight=3"),              // flagAnyOf, ignore-case member
                }, resolver);
            failures += Check("enumEquals + valueMap: marksman matched the Archery-skill bow (damage=21)",
                w.BasicStats!.Damage == 21, $"got {w.BasicStats.Damage}");
            failures += Check("enum no-match leaves the record alone (crossbow line)", w.BasicStats.Damage != 1);
            failures += Check("flagAnyOf: boundweapon flag matched (weight=3)",
                Math.Abs(w.BasicStats.Weight - 3) < 0.001, $"got {w.BasicStats.Weight}");
        }

        // ---- npc: gender / flagBool / pcLevelMult / formEquals / formInList(keyPath) / class-Exclude /
        //      donorSubstring / explicit-unmapped restrictToSkill ----
        {
            var raceFk = new FormKey(new ModKey("HcRace", ModType.Plugin), 0xC01);
            var clsFk = new FormKey(new ModKey("HcCls", ModType.Plugin), 0xD01);
            var facFk = new FormKey(new ModKey("HcFac", ModType.Plugin), 0xA02);
            var n = mod.Npcs.AddNew();
            n.EditorID = "HcFilterNpc";
            n.Configuration.Flags |= NpcConfiguration.Flag.Female | NpcConfiguration.Flag.Essential;
            n.Configuration.Level = new PcLevelMult { LevelMult = 1.15f };
            n.Race.SetTo(raceFk);
            n.Class.SetTo(clsFk);
            n.Factions.Add(new RankPlacement { Faction = facFk.ToLink<IFactionGetter>(), Rank = 0 });
            resolver.WinnerLeafs[raceFk] = "Actors\\Character\\Character Assets\\skeleton.nif";
            var r = SkyPatcherOverlay.Apply(n, n.FormKey, n.EditorID, catalog, catalog.ForSubfolder("npc")!,
                fieldMap.For("npc", "Npc"), new[]
                {
                    L(1, "filterByGender=female:weight=44"),
                    L(2, "filterByGender=male:weight=1"),
                    L(3, "filterByEssential=true:height=1.1"),
                    L(4, "filterByProtected=true:height=9"),
                    L(5, "filterByPCLevelMult=true:fullName=~Scaled~"),
                    L(6, "filterByFactionsOr=HcFac.esp|A02,HcFac.esp|FFF:shortName=~Fac~"),
                    L(7, "filterByRaces=HcRace.esp|C01:weight=55"),
                    L(8, "filterByClassExclude=HcCls.esp|D01:height=7"),        // its OWN class ⇒ excluded ⇒ no
                    L(9, "restrictToMaleModelContains=skeleton:deathItem=HcItm.esp|900"),   // donor read off the race
                    L(10, "restrictToSkill=onehanded~20~60:calcLevelMax=99"),   // explicitly unmapped ⇒ loud skip
                }, resolver);
            failures += Check("gender=female matched, male didn't; race formEquals matched last (weight 44→55)",
                Math.Abs(n.Weight - 55) < 0.001, $"got {n.Weight}");
            failures += Check("flagBool: essential matched, protected didn't; class-Exclude skipped (height=1.1)",
                Math.Abs(n.Height - 1.1) < 0.001, $"got {n.Height}");
            failures += Check("pcLevelMult arm detected (fullName applied)", n.Name?.String == "Scaled", n.Name?.String ?? "<null>");
            failures += Check("formInList keyPath (factions, Or) matched", n.ShortName?.String == "Fac", n.ShortName?.String ?? "<null>");
            failures += Check("donorSubstring: NPC skeleton read off its RACE's winner (deathItem set)",
                n.DeathItem?.FormKey == new FormKey(new ModKey("HcItm", ModType.Plugin), 0x900),
                n.DeathItem?.FormKey.ToString() ?? "<null>");
            failures += Check("restrictToSkill is explicitly unmapped ⇒ loud skip, calcLevelMax untouched",
                n.Configuration.CalcMaxLevel != 99
                && r.Warnings.Any(w2 => w2.Contains("restrictToSkill") && w2.Contains("no static evaluation")));
        }

        // ---- the player rule: only a LONE bare primary that names the player applies — but a
        //      hasPlugins LINE gate is not a record filter and must not break "alone" (review fix) ----
        {
            resolver.Plugins.Add("HcGate.esp");
            var player = new Npc(new FormKey(new ModKey("Skyrim", ModType.Master), 0x7), SkyrimRelease.SkyrimSE);
            var r = SkyPatcherOverlay.Apply(player, player.FormKey, "Player", catalog, catalog.ForSubfolder("npc")!,
                fieldMap.For("npc", "Npc"), new[]
                {
                    L(1, "weight=9"),                                            // apply-all EXCLUDES the player
                    L(2, "filterByGender=male:weight=3"),                        // filtered lines exclude the player
                    L(3, "filterByNpcs=Skyrim.esm|7:filterByEssential=false:weight=5"),   // primary + extra RECORD filter ⇒ still excluded
                    L(4, "filterByNpcs=Skyrim.esm|7:weight=7"),                  // the ONE documented way in
                    // NOTE: the player rule deliberately ignores hasPlugins LINE gates when counting
                    // "alone" (a gate isn't a record filter) — but that path is unreachable through the
                    // closed catalog today: hasPlugins is documented on weapon/armor/ammo only, so an
                    // npc line carrying it classifies Unknown and skips loud BEFORE the player rule.
                    L(5, "hasPlugins=HcGate.esp:filterByNpcs=Skyrim.esm|7:height=2"),
                }, resolver);
            failures += Check("player rule: only the lone bare primary applied (weight=7, not 9/3/5)",
                Math.Abs(player.Weight - 7) < 0.001, $"got {player.Weight}");
            failures += Check("hasPlugins on an npc line is not in the reference — unknown-key LOUD skip, player untouched",
                Math.Abs(player.Height - 0) < 0.001
                && r.Warnings.Any(x => x.Contains("hasPlugins") && x.Contains("not in the SkyPatcher reference")),
                $"height={player.Height}");
        }

        // ---- review fixes: EnumEquals unknown-token loud, gender templated-NPC honesty,
        //      filterByModNames defining-vs-winner disagreement ----
        {
            var w = mod.Weapons.AddNew();
            w.EditorID = "HcEnumWarnBow";
            w.Data = new WeaponData { Skill = Skill.Archery };
            w.BasicStats = new WeaponBasicStats { Damage = 5 };
            resolver.Winners[w.FormKey] = "WinOverride.esp";   // ≠ the defining master HcSpOv.esp
            var r = SkyPatcherOverlay.Apply(w, w.FormKey, w.EditorID, catalog, catalog.ForSubfolder("weapon")!,
                fieldMap.For("weapon", "Weapon"), new[]
                {
                    L(1, "filterBySkills=notaskill:attackDamage=50"),            // no Skill member ⇒ loud UNRESOLVED, never a silent no-match
                    L(2, "filterByModNames=WinOverride.esp:attackDamage=60"),    // winner says yes, master says no ⇒ DISAGREE ⇒ UNRESOLVED
                    L(3, "filterByModNames=Unrelated.esp:attackDamage=70"),      // both readings say no ⇒ agree ⇒ NoMatch (silent, honest)
                }, resolver);
            failures += Check("EnumEquals: an unknown enum token is a LOUD unresolved skip, never a silent verdict",
                w.BasicStats!.Damage == 5
                && r.Warnings.Any(x => x.Contains("notaskill") && x.Contains("UNRESOLVED")),
                string.Join(" ; ", r.Warnings));
            failures += Check("filterByModNames: defining-master vs winning-override DISAGREEMENT is unresolved loud; agreement answers",
                w.BasicStats.Damage == 5
                && r.Warnings.Any(x => x.Contains("disagree on membership"))
                && r.LinesSkippedUnresolvedFilter >= 2,
                string.Join(" ; ", r.Warnings));

            var templated = mod.Npcs.AddNew();
            templated.EditorID = "HcTemplatedNpc";
            templated.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;   // gender comes from the template
            var r2 = SkyPatcherOverlay.Apply(templated, templated.FormKey, templated.EditorID, catalog, catalog.ForSubfolder("npc")!,
                fieldMap.For("npc", "Npc"), new[] { L(1, "filterByGender=female:weight=44") }, resolver);
            failures += Check("gender on a TRAITS-templated NPC is unresolved loud (own Female bit not authoritative)",
                Math.Abs(templated.Weight - 44) > 0.001
                && r2.Warnings.Any(x => x.Contains("templates its TRAITS")),
                string.Join(" ; ", r2.Warnings));
        }

        // ---- ammo: numericLess + flagBool-invert (restrictToBolts) ----
        {
            var a = mod.Ammunitions.AddNew();
            a.EditorID = "HcArrow";
            a.Weight = 0.1f;
            a.Flags = Ammunition.Flag.NonBolt;   // an arrow
            SkyPatcherOverlay.Apply(a, a.FormKey, a.EditorID, catalog, catalog.ForSubfolder("ammo")!,
                fieldMap.For("ammo", "Ammunition"), new[]
                {
                    L(1, "filterByWeightLessThan=0.5:value=20"),   // 0.1 < 0.5 ⇒ applies
                    L(2, "filterByWeightLessThan=0.05:value=1"),   // no
                    L(3, "restrictToBolts=true:weight=9"),          // arrow ⇒ no
                    L(4, "restrictToBolts=false:weight=0.75"),      // non-bolt ⇒ applies
                }, resolver);
            failures += Check("numericLess (weightLessThan 0.5 yes, 0.05 no): value=20", a.Value == 20, $"got {a.Value}");
            failures += Check("restrictToBolts inverts the NonBolt flag (arrow: true no, false yes): weight=0.75",
                Math.Abs(a.Weight - 0.75) < 0.001, $"got {a.Weight}");
        }

        // ---- armor: bipedSlots (INDEX = slot−30) + enum valueMap (heavy) + armorAddons EditorID substring ----
        {
            var aaFk = new FormKey(new ModKey("HcAA", ModType.Plugin), 0xE01);
            var ar = mod.Armors.AddNew();
            ar.EditorID = "HcCuirass";
            ar.BodyTemplate = new BodyTemplate { ArmorType = ArmorType.HeavyArmor, FirstPersonFlags = BipedObjectFlag.Body };
            ar.Armature.Add(aaFk.ToLink<IArmorAddonGetter>());
            resolver.Eids[aaFk] = "HcTestAAHeavyBody";
            SkyPatcherOverlay.Apply(ar, ar.FormKey, ar.EditorID, catalog, catalog.ForSubfolder("armor")!,
                fieldMap.For("armor", "Armor"), new[]
                {
                    L(1, "filterByBipedSlots=2:weight=4"),          // Body = index 2 (slot 32) ⇒ applies
                    L(2, "filterByBipedSlotsOr=0,9:weight=9"),      // head/shield ⇒ no
                    L(3, "filterByArmorTypes=heavy:damageResist=30"),   // valueMap heavy→HeavyArmor
                    L(4, "filterByArmorAddons=TestAA:weight=6"),    // EditorID SUBSTRING via the resolver
                }, resolver);
            failures += Check("bipedSlots: index 2 (Body) matched, 0/9 didn't; addon EID substring matched (weight 4→6)",
                Math.Abs(ar.Weight - 6) < 0.001, $"got {ar.Weight}");
            failures += Check("filterByArmorTypes heavy→HeavyArmor (damageResist=30)",
                Math.Abs(ar.ArmorRating - 30) < 0.001, $"got {ar.ArmorRating}");
        }

        // ---- magicEffect: modNamesLastOverriddenExcluded yields to the WINNING plugin; effectShaders
        //      reads both shader slots; detrimental flagBool ----
        {
            var shdFk = new FormKey(new ModKey("HcShd", ModType.Plugin), 0xE01);
            var g = mod.MagicEffects.AddNew();
            g.EditorID = "HcMgef";
            g.Flags |= MagicEffect.Flag.Detrimental;
            g.HitShader.SetTo(shdFk);
            resolver.Winners[g.FormKey] = "WinPatch.esp";
            var r = SkyPatcherOverlay.Apply(g, g.FormKey, g.EditorID, catalog, catalog.ForSubfolder("magicEffect")!,
                fieldMap.For("magicEffect", "MagicEffect"), new[]
                {
                    L(1, "modNamesLastOverriddenExcluded=WinPatch.esp:baseCost=9"),    // last override IS listed ⇒ YIELD
                    L(2, "modNamesLastOverriddenExcluded=Other.esp:baseCost=12"),      // not listed ⇒ applies
                    L(3, "effectShadersExcluded=HcShd.esp|E01:spellmakingArea=3"),     // shader attached ⇒ excluded ⇒ no
                    L(4, "effectShadersExcluded=HcShd.esp|E02:spellmakingArea=7"),     // not attached ⇒ applies
                    L(5, "restrictToDetrimentalEffects=true:spellmakingCastingTime=2"),
                }, resolver);
            failures += Check("modNamesLastOverriddenExcluded YIELDS to the winning plugin (baseCost=12, never 9)",
                Math.Abs(g.BaseCost - 12) < 0.001, $"got {g.BaseCost}");
            failures += Check("effectShadersExcluded reads the attached shader (area=7, never 3)",
                g.SpellmakingArea == 7, $"got {g.SpellmakingArea}");
            failures += Check("restrictToDetrimentalEffects flagBool (castingTime=2)",
                Math.Abs(g.SpellmakingCastingTime - 2) < 0.001, $"got {g.SpellmakingCastingTime}");
        }

        // ---- constructibleObject: donorKeywords (the CREATED object's keywords) + workbench formEquals
        //      + ingredient formInList(keyPath) ----
        {
            var createdFk = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x910);
            var kwFk = new FormKey(new ModKey("HcKw", ModType.Plugin), 0x820);
            var forgeFk = new FormKey(new ModKey("HcKw", ModType.Plugin), 0xF01);
            var ingotFk = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x920);
            var c = mod.ConstructibleObjects.AddNew();
            c.EditorID = "HcRecipe";
            c.CreatedObject.SetTo(createdFk);
            c.WorkbenchKeyword.SetTo(forgeFk);
            c.Items = new() { new ContainerEntry { Item = new ContainerItem { Item = ingotFk.ToLink<IItemGetter>(), Count = 2 } } };
            resolver.Keywords[createdFk] = new[] { kwFk };
            var r = SkyPatcherOverlay.Apply(c, c.FormKey, c.EditorID, catalog, catalog.ForSubfolder("constructibleObject")!,
                fieldMap.For("constructibleObject", "ConstructibleObject"), new[]
                {
                    L(1, "filterByIngredients=HcItm.esp|920:count=7"),
                    L(2, "filterByWorkBenchKeywords=HcKw.esp|F01:count=5"),
                    L(3, "filterByKeywords=HcKw.esp|820:count=3"),          // the CREATED object's keywords (map override)
                    L(4, "filterByKeywordsExcluded=HcKw.esp|820:count=1"),  // excluded on the donor's keywords ⇒ no
                }, resolver);
            failures += Check("cobj: ingredient + workbench + donor-keyword filters all matched (3 count sets, final 3)",
                r.Applied.Count(x => x.Op == "count") == 3 && c.CreatedObjectCount == 3,
                $"applied={r.Applied.Count(x => x.Op == "count")}, count={c.CreatedObjectCount?.ToString() ?? "<null>"}");
        }

        // ---- cell: the two skip filters (linked template's origin + record origin substring) ----
        {
            var luxLgtm = new FormKey(new ModKey("Lux", ModType.Plugin), 0xC01);
            var cell = new Cell(new FormKey(new ModKey("HcSpOv", ModType.Plugin), 0xCE1), SkyrimRelease.SkyrimSE);
            cell.EditorID = "HcCell";
            cell.LightingTemplate.SetTo(luxLgtm);
            var r = SkyPatcherOverlay.Apply(cell, cell.FormKey, cell.EditorID, catalog, catalog.ForSubfolder("cell")!,
                fieldMap.For("cell", "Cell"), new[]
                {
                    L(1, "skipRecordByLightingTemplateFromMod=Lux.esp:fogNear=100"),    // template IS from Lux ⇒ SKIP
                    L(2, "skipRecordByLightingTemplateFromMod=ELFX.esp:fogNear=200"),   // not ⇒ applies
                    L(3, "skipRecordByModNameContains=HcSp:fogNear=300"),               // origin contains ⇒ SKIP
                }, resolver);
            failures += Check("cell skip filters: Lux-template line yielded, ELFX one applied, origin-substring skipped (fogNear=200)",
                Math.Abs((cell.Lighting?.FogNear ?? 0) - 200) < 0.001, $"got {cell.Lighting?.FogNear}");
        }

        // ---- race: substringLeaf on the gendered skeleton path ----
        {
            var race = mod.Races.AddNew();
            race.EditorID = "HcRace";
            race.SkeletalModel = new GenderedItem<SimpleModel?>(
                new SimpleModel { File = "Actors\\Character\\Character Assets\\skeleton.nif" }, null);
            SkyPatcherOverlay.Apply(race, race.FormKey, race.EditorID, catalog, catalog.ForSubfolder("race")!,
                fieldMap.For("race", "Race"), new[]
                {
                    L(1, "filterByMaleModelContains=skeleton:baseCarryweight=150"),
                    L(2, "filterByFemaleModelContains=skeleton:baseCarryweight=9"),   // female model absent ⇒ no
                }, resolver);
            failures += Check("race substringLeaf: male skeleton path matched, absent female didn't (carryweight=150)",
                Math.Abs(race.BaseCarryWeight - 150) < 0.001, $"got {race.BaseCarryWeight}");
        }

        return failures;
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

        var itemD = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x903);

        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcTestNpc";
        npc.Items = new()
        {
            new ContainerEntry { Item = new ContainerItem { Item = itemA.ToLink<IItemGetter>(), Count = 2 } },
            new ContainerEntry { Item = new ContainerItem { Item = itemD.ToLink<IItemGetter>(), Count = 1 } },
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
            L("z.ini", 5, $"filterByNpcs={me}:removeInventoryObjectsByCount=HcItm.esp|903~2"),   // count 1−2 ≤ 0 ⇒ ENTRY REMOVED (engine RemoveAt path)
            L("z.ini", 6, $"filterByNpcs={me}:setFlags=unique"),                                 // flagsSet (multi-token bit math → engine Set)
            L("z.ini", 7, $"filterByNpcs={me}:setProtected=none"),                               // legal 'none' ⇒ VISIBLE leave-unchanged no-op
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
        failures += Check("removeByCount to ≤0 REMOVES the entry (engine RemoveAt path)",
            !entries.Any(e => e.fk == itemD),
            string.Join(" | ", entries.Select(e => $"{e.fk}×{e.count}")));
        failures += Check("flagsSet lands via the engine (setFlags=unique; essential kept)",
            npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique)
            && npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential),
            npc.Configuration.Flags.ToString());
        failures += Check("'none' on a flagBool is a VISIBLE leave-unchanged no-op, not a warning",
            !npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Protected)
            && r.Applied.Any(a => a.Op == "setProtected" && a.Note is { } n && n.Contains("leave unchanged"))
            && !r.Warnings.Any(w => w.Contains("setProtected")),
            string.Join(" ; ", r.Warnings));
        return failures;
    }

    /// <summary>The plain-formlink-list surgery the NPC arm can't reach: replaceForm re-points elements
    /// through the engine's SetAtIndex, clearList empties through the engine's ReplaceAll (review fold —
    /// these paths were re-plumbed onto WriteEngine and previously had no overlay-level pin).</summary>
    static int FormListArm(SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyrimMod mod)
    {
        Console.WriteLine("  --- formList arm (replaceForm → SetAtIndex, clearList → ReplaceAll) ---");
        int failures = 0;
        var flCat = catalog.ForSubfolder("formList")!;
        var flMap = fieldMap.For("formList", "FormList");
        failures += Check("formList field map present", flMap is not null);
        if (flMap is null) return failures;

        var x = new FormKey(new ModKey("HcItm", ModType.Plugin), 0xB01);
        var y = new FormKey(new ModKey("HcItm", ModType.Plugin), 0xB02);
        var z = new FormKey(new ModKey("HcItm", ModType.Plugin), 0xB03);
        var resolver = new StubResolver();
        SkyPatcherOverlay.OrderedLine L(int n, string text) => new("fl.ini", n, SkyPatcherParse.ParseLine(text));

        // replaceForm: X,Y,X → Z,Y,Z ('='-packed pair per the formList docs) — every X re-pointed in place.
        var fl = mod.FormLists.AddNew();
        fl.Items.Add(x.ToLink<ISkyrimMajorRecordGetter>());
        fl.Items.Add(y.ToLink<ISkyrimMajorRecordGetter>());
        fl.Items.Add(x.ToLink<ISkyrimMajorRecordGetter>());
        var me1 = $"HcSpOv.esp|{fl.FormKey.ID:X}";
        var r1 = SkyPatcherOverlay.Apply(fl, fl.FormKey, null, catalog, flCat, flMap, new[]
        { L(1, $"filterByFormLists={me1}:formsToReplace=HcItm.esp|B01=HcItm.esp|B03") }, resolver);
        failures += Check("replaceForm re-points BOTH occurrences in place (X,Y,X → Z,Y,Z)",
            fl.Items.Select(i => i.FormKey).SequenceEqual(new[] { z, y, z })
            && r1.Applied.Any(a => a.Op == "formsToReplace" && a.Note is { } n && n.Contains("replaced 2")),
            string.Join(",", fl.Items.Select(i => i.FormKey.ToString())));

        // clearList: a populated list empties; the applied entry carries the honest before-count.
        var fl2 = mod.FormLists.AddNew();
        fl2.Items.Add(x.ToLink<ISkyrimMajorRecordGetter>());
        fl2.Items.Add(y.ToLink<ISkyrimMajorRecordGetter>());
        var me2 = $"HcSpOv.esp|{fl2.FormKey.ID:X}";
        var r2 = SkyPatcherOverlay.Apply(fl2, fl2.FormKey, null, catalog, flCat, flMap, new[]
        { L(1, $"filterByFormLists={me2}:clear=true") }, resolver);
        failures += Check("clearList empties the list through the engine (2 → 0, honest before-count)",
            fl2.Items.Count == 0
            && r2.Applied.Any(a => a.Op == "clear" && a.Before == "2 entr(ies)" && a.Note == "cleared"),
            $"count={fl2.Items.Count}");
        return failures;
    }

    /// <summary>The shared leveledList folder serves TWO record classes — noFilterLL is an ITEM-list
    /// apply-all, noFilterLLNPC a CHARACTER-list apply-all (review finding #3: without class scoping,
    /// an LLNPC-only line silently patched item lists too).</summary>
    static int LeveledListScopeArm(SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyrimMod mod)
    {
        Console.WriteLine("  --- leveledList class-scope arm (noFilterLL vs noFilterLLNPC) ---");
        int failures = 0;
        var llCat = catalog.ForSubfolder("leveledList")!;
        var liMap = fieldMap.For("leveledList", "LeveledItem")!;
        var lnMap = fieldMap.For("leveledList", "LeveledNpc")!;
        var resolver = new StubResolver();

        SkyPatcherOverlay.OrderedLine L(int n, string text) => new("ll.ini", n, SkyPatcherParse.ParseLine(text));
        var lines = new[]
        {
            L(1, "noFilterLLNPC=true:calcForLevel=true"),   // character lists ONLY
            L(2, "noFilterLL=true:calcEachItem=true"),      // item lists ONLY
        };

        var lvli = mod.LeveledItems.AddNew();
        SkyPatcherOverlay.Apply(lvli, lvli.FormKey, null, catalog, llCat, liMap, lines, resolver);
        failures += Check("LVLI: the LLNPC apply-all did NOT touch it; the LL one did",
            !lvli.Flags.HasFlag(LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer)
            && lvli.Flags.HasFlag(LeveledItem.Flag.CalculateForEachItemInCount),
            lvli.Flags.ToString());

        var lvln = mod.LeveledNpcs.AddNew();
        SkyPatcherOverlay.Apply(lvln, lvln.FormKey, null, catalog, llCat, lnMap, lines, resolver);
        failures += Check("LVLN: the LL apply-all did NOT touch it; the LLNPC one did",
            lvln.Flags.HasFlag(LeveledNpc.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer)
            && !lvln.Flags.HasFlag(LeveledNpc.Flag.CalculateForEachItemInCount),
            lvln.Flags.ToString());
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
