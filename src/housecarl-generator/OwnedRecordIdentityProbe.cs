using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the depth-2 element-identity render of an OWNED-RECORD element (#252) —
/// the next member of the #198 family after <c>element-identity-guard</c> (which handles lone-FormLink structs). A
/// list element that is itself an owned <see cref="Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter"/> — a DIAL's
/// Responses hold DialogResponses/INFO records, each owning its own FormKey but having NO Name/EditorID/Title and
/// NOT being a lone FormLink — now renders its OWN FormKey (+ EditorID when present) as its identity line
/// (<c>[DialogResponses 000800:hc_owned.esp editorid=…]</c>) instead of a bare opaque <c>[DialogResponses]</c>, so a
/// caller reading a Responses list at the documented depth=2 sees WHICH INFO each element is — without the second
/// per-<c>[i].FormKey</c> call the #252 report had to fall back to.
///
/// Arms, all driving the PRODUCT read path (<see cref="ReadEngine.ReadFields"/>) at depth=2:
///   * FORMKEY      — an INFO WITH an EditorID renders <c>[DialogResponses &lt;fk&gt; editorid=&lt;edid&gt;]</c>.
///     RED before the fix (bare <c>[DialogResponses]</c>), GREEN after.
///   * NO-EDITORID  — an INFO with NO EditorID renders <c>[DialogResponses &lt;fk&gt;]</c> — FormKey still surfaced,
///     and no dangling <c>editorid=</c> (EditorID rides along ONLY when present).
///   * TYPE-KEPT    — the <c>[Type]</c> marker stays (the identity is additive, exactly like #198).
///
/// Run: dotnet run --project src/housecarl-generator -- owned-record-identity-guard
/// </summary>
public static class OwnedRecordIdentityProbe
{
    [CiProbe("owned-record-identity-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — depth-2 element identity: owned-record element (#252)  ################");
        Console.WriteLine();

        var mod = new SkyrimMod(new ModKey("hc_owned", ModType.Plugin), SkyrimRelease.SkyrimSE);

        // A DIAL whose Responses list holds two owned INFO records: one WITH an EditorID, one without. Each owns its
        // own FormKey — the identity #252 wants surfaced at depth=2 (an INFO has no Name and usually no EditorID, the
        // exact case the #198 lone-FormLink path can't reach).
        var withEdid = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcInfoEdid" };
        var noEdid = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        var dial = mod.DialogTopics.AddNew();
        dial.Responses.Add(withEdid);
        dial.Responses.Add(noEdid);

        var rf = ReadEngine.ReadFields(dial, new[] { "Responses" }, 2);
        var e0 = rf.Fields.FirstOrDefault(f => f.Path.EndsWith("Responses[0]", StringComparison.Ordinal) && !f.HasValue);
        var e1 = rf.Fields.FirstOrDefault(f => f.Path.EndsWith("Responses[1]", StringComparison.Ordinal) && !f.HasValue);

        Console.WriteLine($"   Responses[0] : {Show(e0)}");
        Console.WriteLine($"   Responses[1] : {Show(e1)}");
        Console.WriteLine();

        var n0 = e0?.Note ?? "";
        var n1 = e1?.Note ?? "";
        bool formKeyShown = n0.Contains(withEdid.FormKey.ToString(), StringComparison.Ordinal);    // the #252 fix
        bool edidCarried = n0.Contains("editorid=HcInfoEdid", StringComparison.Ordinal);
        bool noEdidClean = n1.Contains(noEdid.FormKey.ToString(), StringComparison.Ordinal)
                        && !n1.Contains("editorid=", StringComparison.Ordinal);                    // no dangling editorid=
        bool typeKept = n0.Contains("DialogResponses", StringComparison.Ordinal)
                     && n1.Contains("DialogResponses", StringComparison.Ordinal);

        bool pass = formKeyShown && edidCarried && noEdidClean && typeKept;

        Console.WriteLine($"   FORMKEY      (owned record shows its FormKey)        : {(formKeyShown ? "PASS" : "FAIL")}");
        Console.WriteLine($"   EDITORID     (EditorID carried when present)         : {(edidCarried ? "PASS" : "FAIL")}");
        Console.WriteLine($"   NO-EDITORID  (absent EditorID → FormKey only, clean) : {(noEdidClean ? "PASS" : "FAIL")}");
        Console.WriteLine($"   TYPE-KEPT    (identity is additive, [Type] stays)    : {(typeKept ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"=== owned-record-identity-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    static string Show(FieldValue? f) =>
        f is null ? "(missing)" : $"{f.Path} = {(f.HasValue ? f.Token : f.Note)}";
}
