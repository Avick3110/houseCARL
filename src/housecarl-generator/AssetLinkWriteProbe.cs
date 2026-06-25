using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for AUTHORING ASSET-LINK LIST ELEMENTS (Heisen 2026-06-26:
/// "cannot author SoundDescriptor.SoundFiles (IAssetLink list elements)"). A collection of asset-link paths
/// (<c>List&lt;IAssetLinkGetter&lt;T&gt;&gt;</c> / <c>ExtendedList&lt;IAssetLink&lt;T&gt;&gt;</c>) exposes its element as the
/// getter/setter INTERFACE, not the concrete <c>AssetLink&lt;T&gt;</c> — and the coercion layer recognised only the
/// concrete, so EVERY asset-link list element failed pre-flight ("does not coerce to IAssetLinkGetter`1") and apply.
/// The fix teaches the coercion family-recogniser (<see cref="WriteEngine.IsAssetLinkFamily"/>) the interface arms and
/// builds the mutable <c>AssetLink&lt;T&gt;</c> from a path string — the same getter-interface→concrete map FormLink uses.
///
/// This guard drives the FULL author chain end-to-end for <c>SoundDescriptor.SoundFiles</c> (Heisen's exact case, an
/// <c>ExtendedList&lt;IAssetLink&lt;SkyrimSoundAssetType&gt;&gt;</c>):
///   (G) pre-flight <see cref="CorpusRulebook.Validate"/> ACCEPTS the ReplaceAll + Add the report saw REJECTED
///       ("does not coerce to IAssetLinkGetter`1") — RED if that rejection returns;
///   (A) <see cref="WriteEngine.ApplyVerb"/> coerces+adds each element (builds the mutable AssetLink&lt;T&gt; from a path);
///   (S) the plugin serialises and re-reads with every path round-tripped verbatim in order — proving the built element
///       assigns into the real ExtendedList and survives the binary round-trip.
/// The COERCION's genericity over asset TYPE (sound vs texture, getter vs setter interface) is locked separately by
/// <c>coerce-selftest</c>, and BOTH asset-link list elements are asserted coercible by <c>coerce-audit</c>.
///
/// SCOPE NOTE — the corpus models exactly ONE other asset-link list, <c>Weather.CloudTextures</c>, but its mutable type
/// is a C# ARRAY (<c>IAssetLink&lt;T&gt;[]</c>), not an ExtendedList — so it can't be authored through the list verbs
/// (Clear/Add) at all (a SEPARATE, pre-existing array-backed-collection write gap, shared with <c>Weather.Clouds</c>,
/// independent of asset-link coercion). It is deliberately NOT tested here; the coercion fix is proven by the sound case.
///
/// Self-contained: synthesises the record + plugin in a fresh <c>SkyrimMod</c> in TEMP (no game data). The validator
/// corpus is the SHARED one loaded via <see cref="CorpusRulebook.Load"/> (<c>CorpusRulebook.CorpusPath</c> — the
/// canonicalCorpus the ci-all runner pre-generates, or <c>generated/corpus.json</c> for a standalone run); this probe
/// does NOT generate it. Run: dotnet run --project src/housecarl-generator assetlink-write-guard
/// </summary>
public static class AssetLinkWriteProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" assetlink-write-guard — author an asset-link list element end-to-end (SoundDescriptor.SoundFiles)");
        Console.WriteLine("================================================================");

        var rulebook = CorpusRulebook.Load();
        var tmp = Path.Combine(Path.GetTempPath(), "hc-assetlink-write-guard");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        var results = new List<(string name, bool pass, string detail)>();

        // Heisen's exact paths shape — Sound\fx\... for the SNDR, textures\... for the WTHR. The verbatim round-trip is
        // the proof, so the values are distinctive.
        RunCase(results, tmp, rulebook,
            label: "SoundDescriptor.SoundFiles (AssetLink<SkyrimSoundAssetType>)",
            outName: "HcAssetLinkSndr.esp",
            recordType: "SoundDescriptor",
            field: "SoundFiles",
            addRecord: m => { var r = m.SoundDescriptors.AddNew("HC_SNDR_AssetLink"); return r.FormKey; },
            readBack: (back, fk) => back.SoundDescriptors.First(x => x.FormKey == fk).SoundFiles?.Select(a => a.GivenPath).ToList(),
            replaceAll: new[] { @"Sound\fx\hc\bardsong\one.wav", @"Sound\fx\hc\bardsong\two.wav", @"Sound\fx\hc\bardsong\three.wav" },
            add: @"Sound\fx\hc\bardsong\four.wav");

        // The OTHER asset-link list, Weather.CloudTextures, is ARRAY-backed (IAssetLink<T>[]) — a separate, not-yet-built
        // write mechanism. It must refuse LOUD and NAMED at apply (an "array-backed" ExpectedApplyRejection), NOT the
        // silent NRE it threw before this guard. Asserts the honest boundary, so a future array-write build flips it green.
        RunArrayRefusalCase(results, rulebook);

        Console.WriteLine();
        bool allPass = true;
        foreach (var (name, pass, detail) in results)
        {
            Console.WriteLine($"  [{(pass ? "PASS" : "!! FAIL")}] {name}");
            Console.WriteLine($"            {detail}");
            if (!pass) allPass = false;
        }
        Console.WriteLine();
        Console.WriteLine(allPass
            ? "=== PASS: ExtendedList asset-link element authors end-to-end (round-trips); array-backed one refuses LOUD ==="
            : "=== FAIL: an asset-link list element did not behave as required (read the !! lines) ===");
        return allPass ? 0 : 1;
    }

    /// <summary>Drive one asset-link list element through the full author chain (gate → apply → serialise → re-read),
    /// asserting the ReplaceAll contents + the appended Add survive verbatim and in order.</summary>
    static void RunCase(
        List<(string, bool, string)> results, string tmp, CorpusRulebook rulebook,
        string label, string outName, string recordType, string field,
        Func<SkyrimMod, FormKey> addRecord,
        Func<ISkyrimModGetter, FormKey, List<string?>?> readBack,
        string[] replaceAll, string add)
    {
        var expected = replaceAll.Append(add).ToList();   // ReplaceAll sets the contents; Add appends one more
        try
        {
            var pc = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outName), ModType.Plugin), SkyrimRelease.SkyrimSE);
            var fk = addRecord(pc);
            var rec = pc.EnumerateMajorRecords().First(r => r.FormKey == fk);

            var ops = new[]
            {
                new WriteRequest { RecordType = recordType, Path = new[] { field }, Verb = "ReplaceAll", Values = replaceAll },
                new WriteRequest { RecordType = recordType, Path = new[] { field }, Verb = "Add", Value = add },
            };

            // (G) the gate must ACCEPT both — the report saw "does not coerce to IAssetLinkGetter`1" here.
            foreach (var op in ops)
                if (rulebook.Validate(op) is { } reject)
                {
                    results.Add((label, false, $"GATE REJECTED [{op.Verb} {field}]: {reject}"));
                    return;
                }

            // (A) apply each through the real verb engine (coerce → build mutable AssetLink<T> → add to the list).
            foreach (var op in ops)
                WriteEngine.ApplyVerb(rec, op);

            // (S) serialise the self-contained plugin (no masters — the paths reference no forms) and re-read.
            var outPath = Path.Combine(tmp, outName);
            pc.BeginWrite.ToPath(outPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            var got = readBack(back, fk);

            bool roundTrips = got is not null && got.Count == expected.Count
                && got.Zip(expected, (a, b) => string.Equals(a, b, StringComparison.Ordinal)).All(x => x);
            var gotStr = got is null ? "(null)" : "[" + string.Join(", ", got) + "]";
            results.Add((label, roundTrips,
                $"gate accepted; applied ReplaceAll({replaceAll.Length}) + Add(1); re-read {gotStr} (want [{string.Join(", ", expected)}])"));
        }
        catch (Exception ex)
        {
            var e = ex.InnerException ?? ex;
            results.Add((label, false, $"threw {e.GetType().Name}: {e.Message}"));
        }
    }

    /// <summary>The honest-boundary arm: an ARRAY-backed asset-link list (Weather.CloudTextures, IAssetLink&lt;T&gt;[])
    /// must refuse a list verb with a clean, NAMED <see cref="ExpectedApplyRejectionException"/> ("array-backed"), NOT
    /// the silent NullReferenceException it threw before the guard. Drives the REAL ApplyVerb and asserts the refusal
    /// kind + wording (RED if it NREs again, or wrongly succeeds — which is the signal a future array-write build is in).</summary>
    static void RunArrayRefusalCase(List<(string, bool, string)> results, CorpusRulebook rulebook)
    {
        const string label = "Weather.CloudTextures (array-backed) — refuses LOUD, not NRE";
        try
        {
            var pc = new SkyrimMod(new ModKey("HcAssetLinkWthr", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var wthr = pc.Weathers.AddNew("HC_WTHR_AssetLink");
            var op = new WriteRequest { RecordType = "Weather", Path = new[] { "CloudTextures" }, Verb = "ReplaceAll",
                                        Values = new[] { @"textures\hc\sky\cloud0.dds", @"textures\hc\sky\cloud1.dds" } };
            try
            {
                WriteEngine.ApplyVerb(wthr, op);
                results.Add((label, false, "ApplyVerb SUCCEEDED on an array-backed list — array-write may now be implemented; update this guard."));
            }
            catch (ExpectedApplyRejectionException ex)
            {
                bool named = ex.Message.Contains("array-backed", StringComparison.OrdinalIgnoreCase);
                results.Add((label, named, $"refused as {nameof(ExpectedApplyRejectionException)}: \"{ex.Message}\""));
            }
            catch (Exception ex)
            {
                var e = ex.InnerException ?? ex;
                results.Add((label, false, $"threw {e.GetType().Name} (want a NAMED ExpectedApplyRejectionException, not this): {e.Message}"));
            }
        }
        catch (Exception ex)
        {
            var e = ex.InnerException ?? ex;
            results.Add((label, false, $"setup threw {e.GetType().Name}: {e.Message}"));
        }
    }
}
