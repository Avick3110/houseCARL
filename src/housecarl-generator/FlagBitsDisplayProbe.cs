using System.Globalization;
using System.Runtime.InteropServices;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the unknown-bits flag decode (#255). When a <c>[Flags]</c> enum leaf
/// carries bits the catalog does NOT name, <c>[Flags].ToString()</c> abandons the name list and renders a bare
/// decimal (the real case: <c>Configuration.Flags = 2490402</c> on Dawnguard vampire NPCs), silently losing even the
/// KNOWN bits (Female / Unique / IsGhost). The read now hangs a DISPLAY-ONLY decode off such a leaf —
/// <c>Female, … (+unknown bits 0x…)</c> — so the common bits stay directly consumable and the unnamed remainder is
/// stated, not hidden. The round-trip Token (the bare decimal, which <c>Enum.Parse</c> re-accepts) is untouched.
///
/// The fixture is enum-AGNOSTIC: it reflects the NPC <c>Configuration.Flags</c> enum, picks a real named single-bit
/// member and a genuinely-unnamed bit position, and sets that combination — so it stays correct if Mutagen's flag
/// set changes. Arms, all driving the PRODUCT read path (<see cref="ReadEngine.ReadFields"/>):
///   * DECODE       — a named-bit | unnamed-bit value surfaces a Display of "&lt;name&gt; (+unknown bits 0x&lt;bit&gt;)".
///     RED before the fix (no Display / decimal only), GREEN after.
///   * TOKEN-INTACT — the leaf's round-trip Token is still the bare decimal, DISTINCT from the Display (the decode
///     rides Display only; write / read-proof / diff never see it).
///   * ALL-NAMED-NULL — a value with ONLY named bits gets NO decode Display (ToString already names it — nothing to
///     recover), so the annotation fires exactly when it adds information.
///   * BIPED-ROUTED — a <c>BodyTemplate.FirstPersonFlags</c> leaf still gets the SLOT decode ("slot 32"), NOT the
///     unknown-bits decode — the dispatcher routes biped flags to their own annotation, unchanged.
///
/// Run: dotnet run --project src/housecarl-generator -- flag-bits-display-guard
/// </summary>
public static class FlagBitsDisplayProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — unknown-bits flag decode (#255)  ################");
        Console.WriteLine();

        var mod = new SkyrimMod(new ModKey("hc_flagbits", ModType.Plugin), SkyrimRelease.SkyrimSE);

        // Reflect the NPC Configuration.Flags enum (the reported field) without hardcoding its type.
        var npc = mod.Npcs.AddNew();
        var cfg = npc.Configuration!;
        var flagsProp = cfg.GetType().GetProperty("Flags")!;
        var enumType = flagsProp.PropertyType;

        // A real NAMED single-bit (power of two) member → its name is the decode we expect.
        object? namedMember = null; ulong namedBit = 0;
        foreach (var m in Enum.GetValues(enumType))
        {
            var b = Bits(m, enumType);
            if (b != 0 && (b & (b - 1)) == 0) { namedMember = m; namedBit = b; break; }
        }
        // Union of every member's bits = the NAMED mask; the lowest CLEAR bit in-width is a genuinely-unnamed bit.
        ulong known = 0;
        foreach (var m in Enum.GetValues(enumType)) known |= Bits(m, enumType);
        int width = Marshal.SizeOf(Enum.GetUnderlyingType(enumType)) * 8;
        ulong unknownBit = 0;
        for (int i = 0; i < width; i++) if ((known & (1UL << i)) == 0) { unknownBit = 1UL << i; break; }

        if (namedMember is null || unknownBit == 0)
        {
            Console.WriteLine($"   FIXTURE UNAVAILABLE — {enumType.Name} lacks a named single-bit and/or a free bit; cannot build the case.");
            Console.WriteLine("=== flag-bits-display-guard: FAIL ===");
            return 1;
        }
        var namedName = namedMember.ToString();

        // --- DECODE + TOKEN-INTACT: named bit | unnamed bit → ToString goes decimal, Display recovers the name. ---
        flagsProp.SetValue(cfg, Enum.ToObject(enumType, namedBit | unknownBit));
        var mixed = Leaf(ReadEngine.ReadFields(npc, new[] { "Configuration.Flags" }), "Configuration.Flags");

        // --- ALL-NAMED-NULL: only the named bit set → ToString names it, no decode Display needed. ---
        flagsProp.SetValue(cfg, Enum.ToObject(enumType, namedBit));
        var named = Leaf(ReadEngine.ReadFields(npc, new[] { "Configuration.Flags" }), "Configuration.Flags");

        // --- BIPED-ROUTED: a biped-slot flags leaf still gets the slot decode, not the unknown-bits one. ---
        var armo = mod.Armors.AddNew();
        armo.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body };
        var biped = Leaf(ReadEngine.ReadFields(armo, new[] { "BodyTemplate.FirstPersonFlags" }), "BodyTemplate.FirstPersonFlags");

        Console.WriteLine($"   fixture: {enumType.Name}  named={namedName}(0x{namedBit:X})  unknown=0x{unknownBit:X}");
        Console.WriteLine($"   mixed  : token={Tok(mixed)}  display={Disp(mixed)}");
        Console.WriteLine($"   named  : token={Tok(named)}  display={Disp(named)}");
        Console.WriteLine($"   biped  : token={Tok(biped)}  display={Disp(biped)}");
        Console.WriteLine();

        var mixedDisp = mixed?.Display ?? "";
        bool decode = mixed is { HasValue: true }
                   && mixedDisp.Contains(namedName!, StringComparison.Ordinal)
                   && mixedDisp.Contains("unknown bits", StringComparison.Ordinal)
                   && mixedDisp.Contains($"0x{unknownBit:X}", StringComparison.Ordinal);
        // Token is the bare decimal ToString of the raw value — unchanged by the decode, and NOT the display text.
        bool tokenIntact = mixed is { HasValue: true }
                   && mixed.Token == Enum.ToObject(enumType, namedBit | unknownBit).ToString()
                   && mixed.Token != mixed.Display;
        bool allNamedNull = named is { HasValue: true } && named.Display is null
                   && named.Token == Enum.ToObject(enumType, namedBit).ToString();   // ToString gave the name
        bool bipedRouted = biped is { HasValue: true }
                   && (biped.Display ?? "").Contains("slot", StringComparison.Ordinal)
                   && !(biped.Display ?? "").Contains("unknown bits", StringComparison.Ordinal);

        bool pass = decode && tokenIntact && allNamedNull && bipedRouted;

        Console.WriteLine($"   DECODE        (known name + unnamed hex remainder)   : {(decode ? "PASS" : "FAIL")}");
        Console.WriteLine($"   TOKEN-INTACT  (round-trip token = bare decimal)      : {(tokenIntact ? "PASS" : "FAIL")}");
        Console.WriteLine($"   ALL-NAMED-NULL(no decode when every bit is named)    : {(allNamedNull ? "PASS" : "FAIL")}");
        Console.WriteLine($"   BIPED-ROUTED  (biped leaf keeps its slot decode)     : {(bipedRouted ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"=== flag-bits-display-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    /// <summary>The unsigned bit pattern of a boxed enum member, robust across signed/unsigned underlying types —
    /// mirrors ReadEngine.TryEnumBits so the fixture reads bits the same way the product does.</summary>
    static ulong Bits(object member, Type enumType)
    {
        var u = Convert.ChangeType(member, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture);
        return u switch
        {
            ulong x => x, long x => unchecked((ulong)x), uint x => x, int x => unchecked((ulong)(long)x),
            ushort x => x, short x => unchecked((ulong)(long)x), byte x => x, sbyte x => unchecked((ulong)(long)x),
            _ => Convert.ToUInt64(u, CultureInfo.InvariantCulture),
        };
    }

    static FieldValue? Leaf(RecordFields rf, string suffix) =>
        rf.Fields.FirstOrDefault(f => f.Path.EndsWith(suffix, StringComparison.Ordinal));

    static string Tok(FieldValue? f) => f is null ? "(missing)" : (f.HasValue ? f.Token ?? "(null)" : "(no-value)");
    static string Disp(FieldValue? f) => f?.Display ?? "(none)";
}
