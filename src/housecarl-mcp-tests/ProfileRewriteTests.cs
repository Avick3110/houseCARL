using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Something holding an MO2 profile file open while it is rewritten — which is what MO2 does on a re-sort
/// (#794). A call landing in that window used to die with "an internal houseCARL failure". Now the asset lane answers
/// off the build it already holds AND says so, the record lane refuses rather than answer off a superseded index, and
/// a cold call names the transient. Each test owns its world: it locks a profile file, which no shared fixture can
/// survive.</summary>
[Trait("tier", "integration")]
public sealed class ProfileRewriteTests
{
    /// <summary>A small MO2 instance whose profile resolves a REAL plugin path — so a refresh takes the re-index
    /// branch rather than the zero-paths keep — and whose two loose mods both provide one texture, so flipping their
    /// priority in modlist.txt changes the winning answer. That flip is how a kept build is told from a fresh one.</summary>
    sealed class RewriteWorld : IDisposable
    {
        public string Root { get; }
        public string ProfileDir { get; }
        public LoadOrderService Svc { get; }

        public const string Contested = @"textures\hc\contested.dds";
        public const string Higher = "HcRwHigher";
        public const string Lower = "HcRwLower";
        public const string PluginMod = "HcRwPlugins";
        public const string PluginName = "HcRw.esp";

        public RewriteWorld()
        {
            Root = Path.Combine(Path.GetTempPath(), "hc-profile-rewrite-" + Guid.NewGuid().ToString("N"));
            var instance = Path.Combine(Root, "instance");
            ProfileDir = Path.Combine(instance, "profiles", "Default");
            var mods = Path.Combine(instance, "mods");
            var data = Path.Combine(Root, "game", "Data");
            foreach (var d in new[] { ProfileDir, data, Path.Combine(mods, Higher), Path.Combine(mods, Lower), Path.Combine(mods, PluginMod) })
                Directory.CreateDirectory(d);

            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

            Loose(Path.Combine(mods, Higher), Contested);
            Loose(Path.Combine(mods, Lower), Contested);

            // A real plugin, so Mo2LoadOrder.Build resolves one ordered path and a refresh does real work.
            var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = "HcRwWeapon";
            mod.BeginWrite.ToPath(Path.Combine(mods, PluginMod, PluginName))
               .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            File.WriteAllText(Path.Combine(ProfileDir, "loadorder.txt"), "# header\r\n" + PluginName + "\r\n");
            File.WriteAllText(Path.Combine(ProfileDir, "plugins.txt"), "*" + PluginName + "\r\n");
            WriteModlist(Higher, Lower);
            File.WriteAllText(Path.Combine(ProfileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

            Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        }

        /// <summary>Rewrite modlist.txt with <paramref name="first"/> at the top — MO2's own priority order, so the
        /// first-listed mod wins the contested file. The flip keeps the byte count identical, so the mtime carries the
        /// whole signal the service's FileStamp gate reads; two writes landing inside one filesystem timestamp tick
        /// would leave it unmoved and the profile change invisible (#812). So the mtime is SET here, from a counter,
        /// rather than taken from the clock: the change is staged synchronously, before the call under test.</summary>
        public void WriteModlist(string first, string second)
        {
            var path = Path.Combine(ProfileDir, "modlist.txt");
            File.WriteAllText(path, $"# header\r\n+{PluginMod}\r\n+{first}\r\n+{second}\r\n");
            File.SetLastWriteTimeUtc(path, ModlistEpoch.AddSeconds(++_modlistWrites));
        }

        static readonly DateTime ModlistEpoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int _modlistWrites;

        public string LoadOrderPath => Path.Combine(ProfileDir, "loadorder.txt");

        /// <summary>Who wins the contested texture right now, as the asset lane answers it.</summary>
        public AssetStatusData Contest() => Svc.AssetStatus(new[] { Contested });

        static void Loose(string modDir, string rel)
        {
            var p = Path.Combine(modDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
        }

        public void Dispose()
        {
            Svc.Dispose();
            try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>Nothing is built yet, so there is no answer to serve: the call says the profile file is held and to
    /// run it again, in place of the internal-failure sentence.</summary>
    [Fact]
    public void AColdAssetCallOnAHeldProfileNamesTheHoldInsteadOfAnInternalFailure()
    {
        using var w = new RewriteWorld();
        using var hold = HeldOpen.Hold(w.LoadOrderPath);

        var response = AssetTools.AssetStatus(w.Svc, new[] { RewriteWorld.Contested });

        Assert.Contains("held open by another process", response, StringComparison.Ordinal);
        Assert.DoesNotContain("internal houseCARL failure", response, StringComparison.Ordinal);
    }

    /// <summary>The kept build, both halves: while the profile cannot be re-read the asset answer is the one the old
    /// build gives AND says it is, and because the baseline was never advanced the very next call after the hold is
    /// released follows the new profile. The second assertion is what catches a keep that freezes for good.</summary>
    [Fact]
    public void AWarmAssetCallAnswersOffTheKeptBuildSaysSoAndFollowsTheProfileOnceItIsFree()
    {
        using var w = new RewriteWorld();
        Assert.Equal(RewriteWorld.Higher, w.Contest().Results.Single().Hit!.Winner!.Source);

        // The flip changes both the stamp (so a refresh is attempted) and the winner (so a kept build is visible).
        w.WriteModlist(RewriteWorld.Lower, RewriteWorld.Higher);
        var hold = HeldOpen.Hold(w.LoadOrderPath);
        try
        {
            var during = w.Contest();

            Assert.Equal(RewriteWorld.Higher, during.Results.Single().Hit!.Winner!.Source);
            Assert.Contains(during.Warnings, s => s.Contains("could not be re-read", StringComparison.Ordinal));
        }
        finally { hold.Dispose(); }

        var after = w.Contest();

        Assert.Equal(RewriteWorld.Lower, after.Results.Single().Hit!.Winner!.Source);
        Assert.DoesNotContain(after.Warnings, s => s.Contains("could not be re-read", StringComparison.Ordinal));
    }

    /// <summary>The first record call after the hold is released reads the profile for itself, and that read is a
    /// refresh for the asset lane too: the kept asset build is dropped there, or the baseline it advances would leave
    /// the next asset call matching, the pending flag cleared, and the old answer served with nothing said.</summary>
    [Fact]
    public void AColdRecordBuildAfterAHoldDoesNotStrandTheKeptAssetBuild()
    {
        using var w = new RewriteWorld();
        Assert.Equal(RewriteWorld.Higher, w.Contest().Results.Single().Hit!.Winner!.Source);

        w.WriteModlist(RewriteWorld.Lower, RewriteWorld.Higher);
        var hold = HeldOpen.Hold(w.LoadOrderPath);
        try { Assert.Equal(RewriteWorld.Higher, w.Contest().Results.Single().Hit!.Winner!.Source); }
        finally { hold.Dispose(); }

        w.Svc.CaptureView();               // the first record call: builds the index off the NEW profile

        var after = w.Contest();

        Assert.Equal(RewriteWorld.Lower, after.Results.Single().Hit!.Winner!.Source);
        Assert.DoesNotContain(after.Warnings, s => s.Contains("could not be re-read", StringComparison.Ordinal));
    }

    /// <summary>The record lane has no channel to say a refresh is pending and its answer IS the order, so it refuses
    /// instead of answering off an index the profile has moved on from. Driven on the index accessor every record
    /// tool goes through rather than on a tool: a tool call would also need this world to generate the whole write
    /// rulebook, which has nothing to do with the behaviour under test. The sentence a caller reads off this
    /// exception is the cold asset test above, through the same tool guard.</summary>
    [Fact]
    public void TheRecordIndexRefusesRatherThanAnswerOffASupersededBuild()
    {
        using var w = new RewriteWorld();
        var built = w.Svc.CaptureView();           // the warm build, taken before anything holds the profile
        Assert.NotNull(built.Epoch);

        w.WriteModlist(RewriteWorld.Lower, RewriteWorld.Higher);
        using var hold = HeldOpen.Hold(w.LoadOrderPath);

        var ex = Assert.Throws<ProfileUnreadableException>(() => w.Svc.CaptureView());

        Assert.Equal(w.LoadOrderPath, ex.ProfilePath);
    }

    /// <summary>The vacuity check on the tests above: with the hold released the same cold call resolves normally, so
    /// none of them is asserting against a world that was broken to begin with.</summary>
    [Fact]
    public void OnceTheHoldIsReleasedTheSameColdCallResolvesNormally()
    {
        using var w = new RewriteWorld();
        using (HeldOpen.Hold(w.LoadOrderPath))
            Assert.Contains("held open by another process",
                            AssetTools.AssetStatus(w.Svc, new[] { RewriteWorld.Contested }), StringComparison.Ordinal);

        Assert.Equal(RewriteWorld.Higher, w.Contest().Results.Single().Hit!.Winner!.Source);
    }

    /// <summary>Only the LOCK is the transient. A profile file that is missing is a different fault and keeps its own
    /// answer, rather than being reported as a re-sort to wait for.</summary>
    [Fact]
    public void AProfileFileThatIsMissingIsNotReportedAsAHold()
    {
        using var w = new RewriteWorld();
        File.Delete(w.LoadOrderPath);

        var comp = Mo2LoadOrder.ReadComposition(w.ProfileDir, new List<string>());

        Assert.Empty(comp.OrderedPluginNames);
    }

    /// <summary>The read itself names the hold, so every lane that reads the profile gets the same sentence rather
    /// than each tool catching a raw IOException of its own.</summary>
    [Fact]
    public void ReadingAProfileWhoseFileIsHeldThrowsTheNamedTransient()
    {
        using var w = new RewriteWorld();
        using var hold = HeldOpen.Hold(w.LoadOrderPath);

        var ex = Assert.Throws<ProfileUnreadableException>(() => Mo2LoadOrder.ReadComposition(w.ProfileDir));

        Assert.Equal(w.LoadOrderPath, ex.ProfilePath);
    }
}
