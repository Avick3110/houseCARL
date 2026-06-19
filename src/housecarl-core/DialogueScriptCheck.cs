using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueScriptCheck — the per-create RESULT-SCRIPT binding check for created dialogue lines
//  (nested-dialogue plan §3.6, Layer B unit C / "per-create structural checks"; sibling of VoiceCheck).
//  A dialogue line (INFO) can carry a RESULT SCRIPT — a Papyrus fragment that runs when the line plays
//  (start a quest stage, give an item). It lives on the INFO's VirtualMachineAdapter (a DialogResponsesAdapter):
//  a ScriptFragments fragment ("End"/"Begin" box, keyed by a FileName script class) and/or attached Scripts.
//  A byte-valid INFO whose script binding is HALF-BUILT or points at an UNCOMPILED script runs NOTHING — the
//  same silent-failure class houseCARL refuses (Q3, plan §3 job 3). This runs as a POST-WRITE step on a
//  successful create: for every CREATED INFO that carries a VMAD it
//    • verifies the binding is structurally usable (a real fragment, or a named attached script), and
//    • checks each bound script class has a compiled `Scripts\<class>.pex` on disk (the VFS — loose + BSA),
//  reporting a loud "WILL NOT FIRE" when the binding is incomplete or its `.pex` is missing, an "OK" for ones
//  fully wired + compiled, or a NAMED undetermined reason when the created INFO can't be located in the patch.
//
//  CORNERSTONE-CLEAN: this is a DIAGNOSTIC over records the engine already wrote — it adds NO per-record write
//  logic and is deliberately SEPARATE from the proven WritePatchBuilder.CreateRecords path (which is untouched).
//  It is driven by BOTH the service (post-create, with the live AssetResolver) and the nested-create CI guard
//  (with a temp AssetResolver over a planted .pex), so the bound/incomplete/not-compiled verdict is end-to-end
//  testable in CI.
//
//  WHY IT NEEDS NO GRAPH RESOLUTION (unlike VoiceCheck): the result-script binding lives ENTIRELY on the INFO
//  itself (VMAD → FileName / OnEnd / Scripts) and the only external fact is the on-disk `.pex`. There is no
//  speaker/voice-type/quest chain to resolve, so this needs the written patch + the AssetResolver only — no
//  LoadOrderResolver, no session.
//
//  KEYED ON VMAD-PRESENT (= the §3.6 "when a result-script op was requested" filter, realized off the written
//  record): a created line with NO VirtualMachineAdapter intends no script and is NOT checked — never nagged.
//  An adapter that exists is validated for completeness; for a freshly-created record the adapter is non-null
//  only because the create spec set it, so VMAD-present and "a result-script was requested" coincide here.
// ======================================================================

/// <summary>The result-script binding verdict for one created dialogue line (INFO) that carries a VMAD.</summary>
public enum ScriptBindingStatus
{
    /// <summary>A fragment or attached script is bound AND every bound class has a compiled `.pex` on disk — it will fire.</summary>
    BoundAndCompiled,
    /// <summary>A VMAD is present but binds no usable fragment/script (no real Begin/End fragment, no named attached
    /// script) — byte-valid, runs NOTHING.</summary>
    BindingIncomplete,
    /// <summary>A script class is bound but its compiled `Scripts\&lt;class&gt;.pex` is absent on disk — runs NOTHING
    /// until compiled.</summary>
    ScriptNotCompiled,
    /// <summary>The created INFO couldn't be located in the written patch to check — surfaced, never silently skipped (Q3).</summary>
    Undetermined,
}

/// <summary>One created INFO's result-script verdict: the <see cref="Status"/>, the bound script class name(s)
/// (<see cref="Scripts"/>), the `Scripts\&lt;class&gt;.pex` path(s) found missing on disk (<see cref="MissingPex"/>,
/// for <see cref="ScriptBindingStatus.ScriptNotCompiled"/>), the <see cref="ReadIncomplete"/> caveat (a BSA failed to
/// read, so a "missing" may merely be unscanned — Q3), and a human-readable <see cref="Detail"/>.</summary>
public sealed record ScriptBindingFinding(
    FormKey Info, string TopicEditorId, ScriptBindingStatus Status,
    IReadOnlyList<string> Scripts, IReadOnlyList<string> MissingPex,
    bool ReadIncomplete, string Detail);

/// <summary>The result-script-coverage report for one create call: a per-INFO binding verdict for each created
/// dialogue line that carries a VMAD. <see cref="IsEmpty"/> when the call created no scripted dialogue lines.</summary>
public sealed record ScriptBindingReport(IReadOnlyList<ScriptBindingFinding> Findings)
{
    /// <summary>The check itself could not run (the patch wouldn't re-open, the walk threw) — surfaced, never a silent
    /// skip (Q3). The create ALREADY SUCCEEDED when this is set; it just means "I couldn't verify the script binding",
    /// not "the write failed". Null on a clean run.</summary>
    public string? CheckError { get; init; }

    public bool IsEmpty => Findings.Count == 0 && CheckError is null;
    public static readonly ScriptBindingReport Empty = new(Array.Empty<ScriptBindingFinding>());
}

public static class DialogueScriptCheck
{
    /// <summary>Run the result-script binding check over the INFOs created by ONE create call. <paramref name="patchPath"/>
    /// is the just-written patch file (re-opened read-only here, then disposed — the overlay lifetime lives in core, so
    /// the service needs no Mutagen.Skyrim dependency); <paramref name="created"/> is the call's CreatedRecord list
    /// (filtered here to INFOs); <paramref name="assets"/> answers on-disk `.pex` presence (loose + BSA). Returns
    /// <see cref="ScriptBindingReport.Empty"/> when the call created no INFOs. A created INFO not located in the patch is
    /// a NAMED undetermined (Q3); a whole-check failure (the patch won't re-open, the walk throws) is surfaced on
    /// <see cref="ScriptBindingReport.CheckError"/> — NEVER thrown (the create already succeeded; this is a verify step).</summary>
    public static ScriptBindingReport Run(string patchPath, IReadOnlyList<WritePatchBuilder.CreatedRecord> created,
                                          AssetResolver assets)
    {
        // Which created records are dialogue lines (INFOs) — only these get a script-binding check.
        var infoKeys = new HashSet<FormKey>();
        foreach (var c in created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal))
                infoKeys.Add(c.FormKey);
        if (infoKeys.Count == 0) return ScriptBindingReport.Empty;

        ISkyrimModGetter? patch = null;
        try
        {
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return RunOver(patch, infoKeys, assets);
        }
        catch (Exception ex)
        {
            return ScriptBindingReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>The walk over the re-opened patch (split out so <see cref="Run"/> can wrap the overlay open + any
    /// walk-level throw into <see cref="ScriptBindingReport.CheckError"/>, while a per-INFO not-found stays a NAMED
    /// undetermined). Mirrors <see cref="VoiceCheck"/>'s topic walk: each created INFO lives in exactly one topic's
    /// Responses (its structural parent), which also yields the topic EditorID for a friendly label.</summary>
    static ScriptBindingReport RunOver(ISkyrimModGetter writtenPatch, HashSet<FormKey> infoKeys, AssetResolver assets)
    {
        var findings = new List<ScriptBindingFinding>();
        var av = assets.Capture();                           // ONE asset build, so presence + ReadIncomplete agree

        var found = new HashSet<FormKey>();
        foreach (var topic in writtenPatch.DialogTopics)
        {
            foreach (var info in topic.Responses)
            {
                if (!infoKeys.Contains(info.FormKey)) continue;   // a pre-existing INFO the patch carried, or not ours
                found.Add(info.FormKey);
                CheckInfo(info, topic.EditorID ?? "", av, findings);
            }
        }

        // A created INFO not found under any topic is a real inconsistency — surfaced, never silently dropped (Q3).
        foreach (var fk in infoKeys)
            if (!found.Contains(fk))
                findings.Add(new ScriptBindingFinding(fk, "", ScriptBindingStatus.Undetermined,
                    Array.Empty<string>(), Array.Empty<string>(), false,
                    "created but not found under any topic in the written patch — can't check its result-script binding; inspect the patch in xEdit."));

        return new ScriptBindingReport(findings);
    }

    /// <summary>Verdict one created INFO. A line with NO VirtualMachineAdapter intends no script and yields nothing
    /// (correct — never nag a script-free line). An adapter that IS present is validated: collect every bound script
    /// CLASS that must have a compiled `.pex` to run (a ScriptFragments fragment's FileName, counted only when a real
    /// Begin/End fragment is wired; each attached Scripts[] entry's class name), then either flag the hollow binding,
    /// or check each class's `Scripts\&lt;class&gt;.pex` on disk.</summary>
    // internal (not private): DialogueValidate (unit C2) reuses this exact per-INFO binding check over EVERY INFO
    // in a topic, so the per-create result-script teeth and the on-demand validator can never drift.
    internal static void CheckInfo(IDialogResponsesGetter info, string topicEdid, AssetResolver.AssetView av,
                          List<ScriptBindingFinding> findings)
    {
        var vmad = info.VirtualMachineAdapter;
        if (vmad is null) return;   // no result script intended — nothing to check

        // The bound script CLASS names that must each have a compiled .pex to actually fire.
        var names = new List<string>();
        var frag = vmad.ScriptFragments;
        if (frag is not null && !string.IsNullOrWhiteSpace(frag.FileName) && HasRealFragment(frag))
            names.Add(frag.FileName.Trim());
        foreach (var entry in vmad.Scripts)
            if (!string.IsNullOrWhiteSpace(entry.Name)) names.Add(entry.Name.Trim());

        // A VMAD that binds nothing usable (no real fragment, no named attached script) is byte-valid but inert — a
        // FileName WITHOUT a Begin/End fragment is a hollow declaration, not a binding, and is caught here (Q3).
        if (names.Count == 0)
        {
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.BindingIncomplete,
                Array.Empty<string>(), Array.Empty<string>(), false,
                "a script adapter (VMAD) is present but binds no usable result-script fragment or attached script — " +
                "byte-valid but it runs NOTHING. Wire the result-script fragment (a FileName AND a Begin/End fragment) " +
                "or remove the empty adapter."));
            return;
        }

        // Each bound class needs Scripts\<class>.pex on disk to fire. Distinct (case-insensitive) so two contributors
        // naming the same class don't double-report.
        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = new List<string>();
        foreach (var name in distinct)
        {
            // A NAMESPACED Papyrus class (Namespace:Script) compiles to Scripts\Namespace\Script.pex — the ':' is a
            // folder separator on disk (and not a legal filename char), so map it before probing the VFS. Without this a
            // valid namespaced script reads as a false "not compiled" (Q3 — a loud-wrong answer; the check must not cry
            // wolf). CK-generated fragments (TIF__/QF_) are always flat, so this only bites namespaced attached Scripts[].
            var relPex = $@"Scripts\{name.Replace(':', '\\')}.pex";
            if (!av.Resolve(relPex).Exists) missing.Add(relPex);
        }

        if (missing.Count == 0)
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.BoundAndCompiled,
                distinct, Array.Empty<string>(), av.ReadIncomplete,
                $"result script bound + compiled ({string.Join(", ", distinct)})."));
        else
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.ScriptNotCompiled,
                distinct, missing, av.ReadIncomplete,
                $"result script bound ({string.Join(", ", distinct)}) but the compiled .pex is missing on disk — " +
                "it runs NOTHING until compiled (housecarl_compile_script)."));
    }

    /// <summary>A ScriptFragments carries a REAL fragment when its Begin or End fragment names a script/fragment — a
    /// FileName alone (no Begin/End) is a hollow declaration that binds nothing.</summary>
    static bool HasRealFragment(IScriptFragmentsGetter frag)
        => IsReal(frag.OnBegin) || IsReal(frag.OnEnd);

    static bool IsReal(IScriptFragmentGetter? f)
        => f is not null && (!string.IsNullOrWhiteSpace(f.ScriptName) || !string.IsNullOrWhiteSpace(f.FragmentName));
}
