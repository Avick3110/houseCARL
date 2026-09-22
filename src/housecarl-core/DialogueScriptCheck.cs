using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// DialogueScriptCheck — the per-create result-script binding check for created dialogue lines; sibling of
// VoiceCheck. Every created INFO carrying a VMAD must bind something usable, and each bound class must have a
// compiled `Scripts\<class>.pex` on disk (loose + BSA). It resolves no graph, so it needs the written patch and
// the AssetResolver only.

/// <summary>The result-script binding verdict for one created dialogue line (INFO) that carries a VMAD.</summary>
public enum ScriptBindingStatus
{
    /// <summary>Bound, and every bound class has a compiled `.pex` on disk — it will fire.</summary>
    BoundAndCompiled,
    /// <summary>A VMAD is present but binds nothing usable — byte-valid, runs NOTHING.</summary>
    BindingIncomplete,
    /// <summary>A class is bound but its `Scripts\&lt;class&gt;.pex` is absent — runs NOTHING until compiled.</summary>
    ScriptNotCompiled,
    /// <summary>The created INFO couldn't be located in the written patch to check — surfaced, never silently skipped.</summary>
    Undetermined,
}

/// <summary>One created INFO's result-script verdict, with the bound class names and any missing `.pex`.</summary>
public sealed record ScriptBindingFinding(
    FormKey Info, string TopicEditorId, ScriptBindingStatus Status,
    IReadOnlyList<string> Scripts, IReadOnlyList<string> MissingPex,
    bool ReadIncomplete, string Detail)
{
    /// <summary>A REAL result-script FRAGMENT — distinct from "has a bound script"; an attached class is not one.</summary>
    public bool HasFragment { get; init; }
}

/// <summary>The result-script-coverage report for one create call, one verdict per created VMAD-carrying INFO.</summary>
public sealed record ScriptBindingReport(IReadOnlyList<ScriptBindingFinding> Findings)
{
    /// <summary>The check could not run; the create ALREADY SUCCEEDED, so the binding is merely unverified.</summary>
    public string? CheckError { get; init; }

    /// <summary>The loose roots this scan could not walk or list, each named with the reason, so the "may merely be
    /// unscanned" note says WHICH folder; empty when every root read.</summary>
    public IReadOnlyList<string> RootFailures { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Findings.Count == 0 && CheckError is null;
    public static readonly ScriptBindingReport Empty = new(Array.Empty<ScriptBindingFinding>());
}

public static class DialogueScriptCheck
{
    /// <summary>Run the binding check over the INFOs created by ONE create call; the patch is re-opened read-only
    /// here and disposed. An INFO not located is a NAMED undetermined, a whole-check failure rides CheckError.</summary>
    public static ScriptBindingReport Run(string patchPath, IReadOnlyList<WritePatchBuilder.CreatedRecord> created,
                                          AssetResolver assets)
    {
        // Which created records are dialogue lines (INFOs) — only these get a binding check.
        var infoKeys = new HashSet<FormKey>();
        foreach (var c in created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal))
                infoKeys.Add(c.FormKey);
        if (infoKeys.Count == 0) return ScriptBindingReport.Empty;

        ISkyrimModGetter? patch = null;
        try
        {
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(patchPath));
            return RunOver(patch, infoKeys, assets);
        }
        catch (Exception ex)
        {
            return ScriptBindingReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>The walk over the re-opened patch, split out so <see cref="Run"/> can wrap any throw.</summary>
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

        // A created INFO under no topic is a real inconsistency — surfaced, never dropped.
        foreach (var fk in infoKeys)
            if (!found.Contains(fk))
                findings.Add(new ScriptBindingFinding(fk, "", ScriptBindingStatus.Undetermined,
                    Array.Empty<string>(), Array.Empty<string>(), false,
                    "created but not found under any topic in the written patch — can't check its result-script binding; inspect the patch in xEdit."));

        return new ScriptBindingReport(findings) { RootFailures = av.RootFailures };
    }

    /// <summary>Verdict one created INFO; a line with no VirtualMachineAdapter yields nothing.</summary>
    // internal, not private: DialogueValidate reuses this over every INFO in a topic, so the two cannot drift.
    internal static void CheckInfo(IDialogResponsesGetter info, string topicEdid, AssetResolver.AssetView av,
                          List<ScriptBindingFinding> findings)
    {
        var vmad = info.VirtualMachineAdapter;
        if (vmad is null) return;   // no result script intended — nothing to check

        // Via the single fragment-presence home, so HasFragment and the validator's per-topic tally cannot drift.
        bool hasFragment = HasResultFragment(info);

        // The bound script CLASS names that must each have a compiled .pex to actually fire.
        var names = new List<string>();
        var frag = vmad.ScriptFragments;
        if (hasFragment) names.Add(frag!.FileName!.Trim());
        foreach (var entry in vmad.Scripts)
            if (!string.IsNullOrWhiteSpace(entry.Name)) names.Add(entry.Name.Trim());

        // A VMAD that binds nothing usable is byte-valid but inert: a FileName with no Begin/End is hollow.
        if (names.Count == 0)
        {
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.BindingIncomplete,
                Array.Empty<string>(), Array.Empty<string>(), false,
                "a script adapter (VMAD) is present but binds no usable result-script fragment or attached script — " +
                "byte-valid but it runs NOTHING. Wire the result-script fragment (a FileName AND a Begin/End fragment) " +
                "or remove the empty adapter."));
            return;
        }

        // Each bound class needs Scripts\<class>.pex to fire; distinct so two contributors do not double-report.
        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = new List<string>();
        foreach (var name in distinct)
        {
            // A NAMESPACED class compiles to Scripts\Namespace\Script.pex, so map ':' before probing the VFS.
            var relPex = $@"Scripts\{name.Replace(':', '\\')}.pex";
            if (!av.Resolve(relPex).Exists) missing.Add(relPex);
        }

        if (missing.Count == 0)
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.BoundAndCompiled,
                distinct, Array.Empty<string>(), av.ReadIncomplete,
                $"result script bound + compiled ({string.Join(", ", distinct)}).") { HasFragment = hasFragment });
        else
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.ScriptNotCompiled,
                distinct, missing, av.ReadIncomplete,
                $"result script bound ({string.Join(", ", distinct)}) but the compiled .pex is missing on disk — " +
                "it runs NOTHING until compiled (" + ToolNames.CompileScript + ").") { HasFragment = hasFragment });
    }

    /// <summary>A REAL result-script FRAGMENT: the single presence home, reused by the validator.</summary>
    internal static bool HasResultFragment(IDialogResponsesGetter info)
    {
        var frag = info.VirtualMachineAdapter?.ScriptFragments;
        return frag is not null && !string.IsNullOrWhiteSpace(frag.FileName) && HasRealFragment(frag);
    }

    /// <summary>A REAL fragment: its Begin or End names a script; a FileName alone binds nothing.</summary>
    static bool HasRealFragment(IScriptFragmentsGetter frag)
        => IsReal(frag.OnBegin) || IsReal(frag.OnEnd);

    static bool IsReal(IScriptFragmentGetter? f)
        => f is not null && (!string.IsNullOrWhiteSpace(f.ScriptName) || !string.IsNullOrWhiteSpace(f.FragmentName));
}
