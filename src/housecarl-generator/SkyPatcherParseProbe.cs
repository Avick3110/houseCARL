using HousecarlCore;

namespace HousecarlGenerator;

// ======================================================================
//  SkyPatcherParseProbe — SELF-CONTAINED CI regression guard for the
//  SkyPatcher structural tokenizer (SkyPatcherParse, Wave 0a of the
//  SkyPatcher distributor subsystem — plan
//  dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md).
//
//  Pins the catalog-FREE grammar mechanics: ':'-segment / '='-key-value /
//  ','-list / '~'-compound splitting, the ~…~ rename name-literal, the
//  unambiguous Plugin.esp|FormID address (incl. leading-zero trim), the
//  0a boundary (a bare EditorID stays UN-addressed — that's Wave 1), and
//  the Q3 loud-note paths for malformed segments. Pure in-process, no game
//  data, no MO2 instance, no Mutagen — just string→model asserts.
// ======================================================================
public static class SkyPatcherParseProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[skypatcher-parse-guard] SkyPatcher structural tokenizer (Wave 0a)");
        int failures = 0;

        // -- line classification ---------------------------------------------------------------
        failures += Check("blank line ⇒ Blank", SkyPatcherParse.ParseLine("   ").Kind == SkyPatcherLineKind.Blank);
        failures += Check("';'-led line ⇒ Comment", SkyPatcherParse.ParseLine("  ; a note").Kind == SkyPatcherLineKind.Comment);
        failures += Check("key=value line ⇒ Patch", SkyPatcherParse.ParseLine("attackDamage=99").Kind == SkyPatcherLineKind.Patch);

        // -- the canonical weapon patch (grammar-core §3 worked example) ------------------------
        var w = SkyPatcherParse.ParseLine("filterByWeapons=Skyrim.esm|00012EB7:attackDamage=99:weight=0");
        failures += Check("weapon patch ⇒ 3 segments", w.Segments.Count == 3, $"got {w.Segments.Count}");
        failures += Check("segment[0] key = filterByWeapons", w.Segments[0].Key == "filterByWeapons", w.Segments[0].Key);
        failures += Check("segment[1] key/value = attackDamage/99",
            w.Segments[1].Key == "attackDamage" && w.Segments[1].RawValue == "99",
            $"{w.Segments[1].Key}={w.Segments[1].RawValue}");
        var wAddr = w.Segments[0].Values[0].Address;
        failures += Check("weapon FormID address parsed = Skyrim.esm|0x12EB7",
            wAddr is { IsFormId: true } && wAddr.Plugin == "Skyrim.esm" && wAddr.FormId == 0x12EB7u,
            wAddr is null ? "<null>" : $"{wAddr.Plugin}|{wAddr.FormId:X}");
        failures += Check("no parse note on the clean line", w.Note is null, w.Note ?? "");

        // -- multi-value filter (comma list, each an address) ----------------------------------
        var mv = SkyPatcherParse.ParseLine("filterByWeapons=Skyrim.esm|00012EB7, Skyrim.esm|00013790:attackDamage=50");
        failures += Check("comma list ⇒ 2 values", mv.Segments[0].Values.Count == 2, $"got {mv.Segments[0].Values.Count}");
        failures += Check("both list items address-parsed (leading space trimmed)",
            mv.Segments[0].Values[0].Address?.FormId == 0x12EB7u && mv.Segments[0].Values[1].Address?.FormId == 0x13790u,
            $"{mv.Segments[0].Values[0].Address?.FormId:X}, {mv.Segments[0].Values[1].Address?.FormId:X}");

        // -- leading-zero trim on a modded FormID -----------------------------------------------
        var lz = SkyPatcherParse.ParseLine("keywordsToAdd=myMod.esp|223");
        failures += Check("FormID leading-zero trim (myMod.esp|223 ⇒ 0x223)",
            lz.Segments[0].Values[0].Address is { Plugin: "myMod.esp", FormId: 0x223u },
            $"{lz.Segments[0].Values[0].Address?.Plugin}|{lz.Segments[0].Values[0].Address?.FormId:X}");

        // -- the player special form -------------------------------------------------------------
        var pl = SkyPatcherParse.ParseLine("filterByNpcs=Skyrim.esm|7");
        failures += Check("player form Skyrim.esm|7 ⇒ 0x7", pl.Segments[0].Values[0].Address?.FormId == 0x7u,
            $"{pl.Segments[0].Values[0].Address?.FormId:X}");

        // -- rename name-literal (~…~) -----------------------------------------------------------
        var rn = SkyPatcherParse.ParseLine("filterByWeapons=IronSword:fullName=~Reforged Blade~");
        var nameVal = rn.Segments[1].Values[0];
        failures += Check("~…~ ⇒ name-literal with inner text preserved",
            nameVal.IsNameLiteral && nameVal.NameText == "Reforged Blade", $"lit={nameVal.IsNameLiteral} text=\"{nameVal.NameText}\"");
        failures += Check("a name-literal is NOT address-parsed", nameVal.Address is null);

        // -- 0a boundary: a bare EditorID stays un-addressed (the Wave-1 overlay resolves it) ----
        var eid = rn.Segments[0].Values[0];
        failures += Check("bare EditorID 'IronSword' is left un-addressed at 0a",
            eid.Address is null && eid.Raw == "IronSword", $"addr={(eid.Address is null ? "null" : "SET")} raw={eid.Raw}");

        // -- compound ~-packed value (mgefsToAdd=Form~Mag~Dur~Area) ------------------------------
        var cp = SkyPatcherParse.ParseLine("filterBySpells=Skyrim.esm|12FCD:mgefsToAdd=Skyrim.esm|0001C08A~50~10~0");
        var cpVal = cp.Segments[1].Values[0];
        failures += Check("compound ⇒ 4 sub-args", cpVal.SubArgs.Count == 4, $"got {cpVal.SubArgs.Count}: [{string.Join("|", cpVal.SubArgs)}]");
        failures += Check("compound first sub-arg address-parsed",
            cpVal.Address?.FormId == 0x1C08Au && cpVal.SubArgs[1] == "50" && cpVal.SubArgs[3] == "0",
            $"addr={cpVal.Address?.FormId:X} args=[{string.Join("|", cpVal.SubArgs)}]");

        // -- value legitimately containing '=' (split on FIRST '=' only) -------------------------
        var fr = SkyPatcherParse.ParseLine("filterByNpcs=Skyrim.esm|13BBF:factionsToAdd=Skyrim.esm|1BE1B=5");
        failures += Check("first-'=' split keeps 'form=rank' intact in the value",
            fr.Segments[1].Key == "factionsToAdd" && fr.Segments[1].RawValue == "Skyrim.esm|1BE1B=5",
            $"{fr.Segments[1].Key}={fr.Segments[1].RawValue}");
        // Deliberate 0a boundary, pinned: the '='-packed right side isn't clean hex, so the item carries
        // NO address — Wave 1 re-splits 'form=rank' itself (a change here must be a conscious one).
        failures += Check("'form=rank' item is deliberately un-addressed at 0a",
            fr.Segments[1].Values[0].Address is null,
            $"addr={(fr.Segments[1].Values[0].Address is null ? "null" : "SET")}");

        // -- empty comma-item (a form deleted between commas) is skipped LOUD, not absorbed ------
        var ec = SkyPatcherParse.ParseLine("keywordsToAdd=Skyrim.esm|123,,Skyrim.esm|456");
        failures += Check("doubled ',' ⇒ 2 items + a loud note naming the empty ','-item",
            ec.Segments[0].Values.Count == 2 && ec.Note is not null && ec.Note.Contains("','-item"),
            $"items={ec.Segments[0].Values.Count} note={ec.Note ?? "<none>"}");

        // -- null-clear a form field -------------------------------------------------------------
        var nl = SkyPatcherParse.ParseLine("filterByWeapons=SomeSword:objectEffect=null");
        failures += Check("objectEffect=null ⇒ scalar 'null', no address, not a name-literal",
            nl.Segments[1].Values[0] is { Raw: "null", IsNameLiteral: false, Address: null });

        // -- whitespace tolerance around keys/values --------------------------------------------
        var ws = SkyPatcherParse.ParseLine("  filterByWeapons = Skyrim.esm|12EB7  :  attackDamage = 99 ");
        failures += Check("whitespace around key/value is trimmed",
            ws.Segments[0].Key == "filterByWeapons" && ws.Segments[1].Key == "attackDamage" && ws.Segments[1].RawValue == "99",
            $"[{ws.Segments[0].Key}] [{ws.Segments[1].Key}={ws.Segments[1].RawValue}]");

        // -- Q3: malformed segments surface a loud note, are NOT dropped -------------------------
        var m1 = SkyPatcherParse.ParseLine("filterByWeapons=X:justtext");
        failures += Check("no-'=' segment ⇒ loud note + segment retained",
            m1.Note is not null && m1.Note.Contains("no '='") && m1.Segments.Count == 2,
            $"note={m1.Note ?? "<null>"} segs={m1.Segments.Count}");
        var m2 = SkyPatcherParse.ParseLine("=99");
        failures += Check("empty-key segment ⇒ loud note", m2.Note is not null && m2.Note.Contains("empty key"), m2.Note ?? "<null>");

        // -- TryParseAddress rejects a non-hex right side ----------------------------------------
        failures += Check("TryParseAddress rejects non-hex right side", SkyPatcherParse.TryParseAddress("Skyrim.esm|NotHex") is null);
        failures += Check("TryParseAddress rejects a bare identifier (no '|')", SkyPatcherParse.TryParseAddress("IronSword") is null);

        // -- whole-file parse: counts by kind ----------------------------------------------------
        var file = SkyPatcherParse.ParseFile(
            "; header comment\n" +
            "filterByWeapons=Skyrim.esm|12EB7:attackDamage=99\n" +
            "\n" +
            "filterByArmors=Skyrim.esm|12E49:armorRating=40\n");
        int patches = file.Count(l => l.Kind == SkyPatcherLineKind.Patch);
        int comments = file.Count(l => l.Kind == SkyPatcherLineKind.Comment);
        int blanks = file.Count(l => l.Kind == SkyPatcherLineKind.Blank);
        failures += Check("file parse ⇒ 2 patch, 1 comment, ≥1 blank",
            patches == 2 && comments == 1 && blanks >= 1, $"patch={patches} comment={comments} blank={blanks}");

        Console.WriteLine(failures == 0
            ? "[skypatcher-parse-guard] PASS — the SkyPatcher tokenizer grammar holds."
            : $"[skypatcher-parse-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
    }

    static int Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok && detail.Length > 0) Console.WriteLine($"      got: {detail}");
        return ok ? 0 : 1;
    }
}
