using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The synthetic MO2 instance the <c>exclude=</c> tests run over: a base master named
/// <c>Skyrim.esm</c> (the filename Mutagen's Implicits set matches on) carrying three dangling refs, plus a
/// plugin mastering it carrying two more — five in the order. <c>Skyrim.esm</c> is in <c>loadorder.txt</c> and
/// absent from <c>plugins.txt</c>, which is what makes it implicit (force-loaded).</summary>
public sealed class CheckWorld : IDisposable
{
    public string Root { get; }
    public string Instance { get; }
    public string ProfileDir { get; }
    /// <summary>The profile's plugin list — the file the composition-read tests hold open.</summary>
    public string PluginsTxt => Path.Combine(ProfileDir, "plugins.txt");

    /// <summary>The base master, excluded by name in the tests that name a plugin.</summary>
    public string BaseMasterName => "Skyrim.esm";

    public LoadOrderService Svc { get; }

    public CheckWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-check-exclude-tests-" + Guid.NewGuid().ToString("N"));
        Instance = Path.Combine(Root, "instance");
        ProfileDir = Path.Combine(Instance, "profiles", "Default");
        var mods = Path.Combine(Instance, "mods");
        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var baseModDir = Path.Combine(mods, "VanillaStub");
        var wireModDir = Path.Combine(mods, "WireMod");
        Directory.CreateDirectory(baseModDir);
        Directory.CreateDirectory(wireModDir);

        var deadFk = FormKey.Factory("0E0E0E:Skyrim.esm");   // defined by nothing — every ref to it dangles
        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 3; i++) { var n = sky.Npcs.AddNew(); n.EditorID = $"HcCeWireVanilla{i}"; n.Race.SetTo(deadFk); }
        var skyPath = Path.Combine(baseModDir, "Skyrim.esm");
        sky.BeginWrite.ToPath(skyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using (var skyOv = SkyrimMod.CreateFromBinaryOverlay(skyPath, SkyrimRelease.SkyrimSE))
        {
            var mod = new SkyrimMod(new ModKey("HcCeWireMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
            for (int i = 0; i < 2; i++) { var n = mod.Npcs.AddNew(); n.EditorID = $"HcCeWireMod{i}"; n.Race.SetTo(deadFk); }
            mod.BeginWrite.ToPath(Path.Combine(wireModDir, "HcCeWireMod.esp"))
               .WithLoadOrder(new ISkyrimModGetter[] { skyOv }).Write();
        }

        File.WriteAllText(Path.Combine(ProfileDir, "loadorder.txt"), "# header\r\nSkyrim.esm\r\nHcCeWireMod.esp\r\n");
        File.WriteAllText(PluginsTxt, "*HcCeWireMod.esp\r\n");
        File.WriteAllText(Path.Combine(ProfileDir, "modlist.txt"), "# header\r\n+WireMod\r\n+VanillaStub\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);

        // One sweep before any test runs, and it is load-bearing. Two tests hold plugins.txt open exclusively
        // to fault the composition read; they are about a read the service cannot make NOW, not one it never
        // made. The composition cache is keyed on mtime, which holding a handle does not move, so warming it
        // here makes those tests independent of the order xUnit runs methods in.
        CheckTools.CheckTool(Svc);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The world, built once for the class. Every test below is read-only over it.</summary>
public sealed class CheckWorldFixture : IDisposable
{
    public CheckWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// <c>exclude=</c>'s grammar, driven END TO END through the surface a caller reaches:
/// <see cref="CheckTools.CheckTool"/> -> <see cref="LoadOrderService.CheckErrors"/> -> <c>ErrorCheck.Run</c>,
/// over a synthetic MO2 instance. Driven at the tool rather than the core, because that is the only lane in
/// which the parameter's journey across the two layers is observable — a core call takes an already-resolved
/// exclusion.
///
/// <para>Every test asserts the sweep's own totals as numbers off the errors family's head line, since they are
/// fixture-known: five dangling refs across two plugins, three in the base master. An exclusion that does not
/// reach the core returns five.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class CheckExcludeGrammarTests : IClassFixture<CheckWorldFixture>
{
    readonly CheckWorld W;
    public CheckExcludeGrammarTests(CheckWorldFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    // ---- reading the totals as values ------------------------------------------------------------------

    static readonly Regex ScannedRx = new(@"^scanned (\d+) plugins? ", RegexOptions.Compiled);
    static readonly Regex DanglingRx = new(@"(\d+) dangling ref\(s\)", RegexOptions.Compiled);

    /// <summary>The errors family's head line — where the sweep states what it swept and what it found. Read
    /// as a LINE rather than matched over the whole response, because a listed dangling entry prints its own
    /// counts too and a whole-text match would take whichever came first.</summary>
    static string HeadLine(string response) =>
        response.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("scanned ", StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"no errors-family head line in the {ToolNames.Check} response: {Head(response)}");

    static int Scanned(string response) => Num(ScannedRx, HeadLine(response), "scanned plugins");
    static int Dangling(string response) => Num(DanglingRx, HeadLine(response), "dangling refs");

    static int Num(Regex rx, string line, string what)
    {
        var m = rx.Match(line);
        if (!m.Success) throw new InvalidOperationException($"no {what} count in the head line: {line}");
        return int.Parse(m.Groups[1].Value);
    }

    /// <summary>The first two lines of a response — enough to read a failure by without printing a sweep.</summary>
    static string Head(string response)
    {
        var lines = response.Split('\n');
        return lines.Length > 1 ? lines[0] + " | " + lines[1] : lines[0];
    }

    // ---- the tests -------------------------------------------------------------------------------------

    [Fact]
    public void TheSyntheticInstanceSweepsBothPluginsAndFindsAllFiveDanglingRefs_TheBaselineEveryExclusionIsMeasuredAgainst()
    {
        var whole = CheckTools.CheckTool(Svc);
        Assert.Equal(2, Scanned(whole));
        Assert.Equal(5, Dangling(whole));
    }

    [Fact]
    public void ExcludeNamingAPluginRemovesItFromTheSweepTheCoreRuns_OnePluginScannedTwoDanglingRefs()
    {
        var byName = CheckTools.CheckTool(Svc, exclude: new[] { W.BaseMasterName });
        Assert.Equal(1, Scanned(byName));
        Assert.Equal(2, Dangling(byName));
        Assert.DoesNotContain("[ERROR] " + W.BaseMasterName, byName);
    }

    [Fact]
    public void TheBaseMastersGroupTokenSurvivesTheSameJourney_ExpandedAtTheServiceAndSweptByTheCore()
    {
        var byGroup = CheckTools.CheckTool(Svc, exclude: new[] { SweepExclusion.BaseMastersToken });
        Assert.Equal(1, Scanned(byGroup));
        Assert.Equal(2, Dangling(byGroup));
    }

    /// <summary>
    /// <c>implicit</c> is the one token whose members are read from the MO2 COMPOSITION, at the service layer —
    /// the profile's own plugins.txt/loadorder.txt split.
    /// </summary>
    [Fact]
    public void TheImplicitTokenResolvesFromTheProfilesOwnCompositionAndDropsTheForceLoadedMaster()
    {
        var byImplicit = CheckTools.CheckTool(Svc, exclude: new[] { SweepExclusion.ImplicitToken });
        Assert.Equal(1, Scanned(byImplicit));
        Assert.Equal(2, Dangling(byImplicit));
    }

    /// <summary>
    /// json is a second render over the same service call — the exclusion must carry there too, or one
    /// transport silently answers a different question.
    ///
    /// <para>The MERGED response nests a family's own counts under <c>families.&lt;family&gt;</c>; only the
    /// call-level scope facts (<c>families_ran</c>, <c>findings_scope</c>, <c>excluded_plugins</c>) stay flat at
    /// the root. A shape mismatch reports the keys it actually saw.</para>
    /// </summary>
    [Fact]
    public void TheJsonTransportOfTheSameExcludedSweepReportsTheSameNarrowedTotals()
    {
        var jsonExcl = CheckTools.CheckTool(Svc, exclude: new[] { W.BaseMasterName }, format: "json");
        JsonElement errors;
        try
        {
            errors = JsonDocument.Parse(jsonExcl).RootElement
                .GetProperty("families").GetProperty(SweepFamilySelection.Token(SweepFamily.Errors));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"the errors family's counts are not where they are read from ({ex.GetType().Name}: {ex.Message}) " +
                $"| keys: {TopKeys(jsonExcl)}");
        }
        Assert.Equal(1, errors.GetProperty("scanned_plugins").GetInt32());
        Assert.Equal(2, errors.GetProperty("dangling").GetInt32());
    }

    /// <summary>The response's own key names, for a shape mismatch's message — one level deep, so a nesting
    /// change reads as a nesting change rather than as a missing property.</summary>
    static string TopKeys(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.ValueKind != JsonValueKind.Object) return $"(root is {root.ValueKind})";
            return string.Join(",", root.EnumerateObject().Select(x =>
                x.Value.ValueKind == JsonValueKind.Object
                    ? $"{x.Name}{{{string.Join(",", x.Value.EnumerateObject().Select(y => y.Name))}}}"
                    : x.Name));
        }
        catch (Exception ex) { return $"(unparseable: {ex.GetType().Name})"; }
    }

    /// <summary>The <c>implicit</c> group is the only value whose members come from a read that can fail, so the
    /// fault is real rather than mocked: plugins.txt held open exclusively. Asserted as prose because a refusal
    /// exists only as its sentence; the group name comes off the product's constant.</summary>
    [Fact]
    public void WhenTheProfileCompositionCannotBeRead_TheCallerWhoAskedForTheImplicitGroupIsRefusedAndToldWhy()
    {
        string locked;
        using (var hold = new FileStream(W.PluginsTxt, FileMode.Open, FileAccess.Read, FileShare.None))
            locked = CheckTools.CheckTool(Svc, exclude: new[] { SweepExclusion.ImplicitToken });

        Assert.Contains("exclude= could not be resolved", locked);
        Assert.Contains(SweepExclusion.ImplicitToken, locked);
    }

    /// <summary>A refusal naming a group they never wrote is one the caller cannot act on — so the same fault
    /// must leave a caller who named a plugin alone.</summary>
    [Fact]
    public void TheSameCompositionReadFailureDoesNotRefuseACallerWhoNamedAPlugin()
    {
        string locked;
        using (var hold = new FileStream(W.PluginsTxt, FileMode.Open, FileAccess.Read, FileShare.None))
            locked = CheckTools.CheckTool(Svc, exclude: new[] { W.BaseMasterName });

        Assert.DoesNotContain("could not be resolved", locked);
        Assert.Equal(1, Scanned(locked));
        Assert.Equal(2, Dangling(locked));
    }

    /// <summary>A bad value refuses through the TOOL, not just in the resolver's own unit test, and names the
    /// value alongside the real token set rather than sweeping with the exclusion quietly dropped. The token set
    /// is taken from <see cref="SweepExclusion.Tokens"/> so a token added to the product arrives here by
    /// itself.</summary>
    [Fact]
    public void AnUnknownGroupValueRefusesAtTheToolSurface_NamingTheValueAndTheRealTokenSet()
    {
        var refused = CheckTools.CheckTool(Svc, exclude: new[] { "vanilla" });

        Assert.StartsWith("error:", refused);
        Assert.Contains("'vanilla'", refused);
        foreach (var t in SweepExclusion.Tokens) Assert.Contains(t, refused);
        Assert.DoesNotContain("scanned ", refused);
    }
}

/// <summary>The <c>exclude=</c> parameter's <c>[Description]</c> against the vocabulary the parameter accepts:
/// a token added to the set and not to the description is undiscoverable. The converse — a word in the
/// description that is not a token — is deliberately not checked, since the description is prose; the
/// unknown-group refusal above covers that direction.</summary>
[Trait("tier", "unit")]
public sealed class CheckExcludeGrammarDescriptionTests
{
    /// <summary>The accepted tokens, off the product's own list. A theory with no rows is an xUnit failure, so
    /// an emptied list fails here rather than passing vacuously.</summary>
    public static IEnumerable<object[]> AcceptedTokens() =>
        SweepExclusion.Tokens.Select(t => new object[] { t });

    static string ExcludeDescription()
    {
        var param = typeof(CheckTools).GetMethod(nameof(CheckTools.CheckTool), BindingFlags.Public | BindingFlags.Static)?
            .GetParameters().FirstOrDefault(x => x.Name == "exclude");
        Assert.NotNull(param);
        var text = param!.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.NotNull(text);
        return text!;
    }

    [Theory]
    [MemberData(nameof(AcceptedTokens))]
    public void TheExcludeDescriptionNamesEveryTokenTheParameterAccepts(string token) =>
        Assert.Contains(token, ExcludeDescription());

    static string LimitDescription()
    {
        var param = typeof(CheckTools).GetMethod(nameof(CheckTools.CheckTool), BindingFlags.Public | BindingFlags.Static)?
            .GetParameters().FirstOrDefault(x => x.Name == "limit");
        Assert.NotNull(param);
        var text = param!.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.NotNull(text);
        return text!;
    }

    /// <summary>An agent plans against the tool description, so the knob may not promise a listing the sweep no
    /// longer produces: unverifiable notes are outside limit=, but repeats for one script class are collapsed.</summary>
    [Fact]
    public void TheLimitDescriptionStatesTheUnverifiableCollapseRatherThanAFullListing()
    {
        var text = LimitDescription();

        Assert.Contains("collapsed", text);
        Assert.Contains("script class", text);
        // The claim the collapse made untrue must be gone, not merely qualified further down.
        Assert.DoesNotContain("always listed in full", text);
    }
}
