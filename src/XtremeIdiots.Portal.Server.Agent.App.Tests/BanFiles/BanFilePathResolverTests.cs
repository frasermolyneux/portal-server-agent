using XtremeIdiots.Portal.Server.Agent.App.BanFiles;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.BanFiles;

public class BanFilePathResolverTests
{
    private readonly BanFilePathResolver _sut = new();

    [Theory]
    [InlineData("/")]
    [InlineData("/cod2/")]
    [InlineData("/cod2")]
    public void Resolve_CallOfDuty2_AlwaysUsesRoot_NeverIncludesMod(string root)
    {
        // CoD2's ban file is always at the server root — the mod folder convention used
        // by CoD4/5 does not apply. This is the bug-fix that the legacy heuristic missed.
        var result = _sut.Resolve("CallOfDuty2", root, liveMod: "anything");

        Assert.EndsWith("ban.txt", result.Path);
        Assert.DoesNotContain("/mods/", result.Path);
        Assert.Null(result.ResolvedForMod);
    }

    [Theory]
    [InlineData("CallOfDuty4")]
    [InlineData("CallOfDuty5")]
    public void Resolve_CodWithMod_PutsBanFileUnderModsFolder(string gameType)
    {
        var result = _sut.Resolve(gameType, "/cod4/", liveMod: "xi_sniper");

        Assert.Equal("/cod4/mods/xi_sniper/ban.txt", result.Path);
        Assert.Equal("xi_sniper", result.ResolvedForMod);
    }

    [Theory]
    [InlineData("CallOfDuty4")]
    [InlineData("CallOfDuty5")]
    public void Resolve_CodWithoutMod_FallsBackToMain(string gameType)
    {
        var result = _sut.Resolve(gameType, "/", liveMod: null);

        Assert.Equal("/main/ban.txt", result.Path);
        Assert.Equal("main", result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_StripsLeadingModsPrefixIfPresent()
    {
        // Defensive: some parsers may surface the mod with a "mods/" prefix already.
        // We must not produce a doubled "mods/mods/..." path.
        var result = _sut.Resolve("CallOfDuty4", "/", liveMod: "mods/xi_sniper");

        Assert.Equal("/mods/xi_sniper/ban.txt", result.Path);
        Assert.Equal("xi_sniper", result.ResolvedForMod);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_CodWithEmptyMod_FallsBackToMain(string mod)
    {
        var result = _sut.Resolve("CallOfDuty5", "/", liveMod: mod);

        Assert.Equal("/main/ban.txt", result.Path);
        Assert.Equal("main", result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_NormalisesBackslashesToForwardSlashes()
    {
        var result = _sut.Resolve("CallOfDuty4", @"\cod4\", "xi_sniper");

        Assert.Equal("/cod4/mods/xi_sniper/ban.txt", result.Path);
    }

    [Fact]
    public void Resolve_AppendsTrailingSlashIfMissing()
    {
        var result = _sut.Resolve("CallOfDuty2", "/cod2", liveMod: null);

        Assert.Equal("/cod2/ban.txt", result.Path);
    }

    [Fact]
    public void Resolve_UnknownGameType_FallsBackToRootBanTxt()
    {
        // New game types should not crash the watcher — they get a sensible default
        // until an explicit rule is added.
        var result = _sut.Resolve("SomeNewGame", "/", "anything");

        Assert.Equal("/ban.txt", result.Path);
        Assert.Null(result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_Cod4x_WithMod_UsesRootBanlistV2Path()
    {
        // CoD4x banlist_v2.dat is configured relative to the server's ban-file root
        // path, not under mods/<mod>/.
        var result = _sut.Resolve("CallOfDuty4x", "/cod4x/", liveMod: "xi_promod");

        Assert.Equal("/cod4x/banlist_v2.dat", result.Path);
        Assert.Null(result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_Cod4x_WithoutMod_UsesRootBanlistV2Path()
    {
        var result = _sut.Resolve("CallOfDuty4x", "/", liveMod: null);

        Assert.Equal("/banlist_v2.dat", result.Path);
        Assert.Null(result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_Cod4x_IgnoresLeadingModsPrefixWhenResolvingPath()
    {
        var result = _sut.Resolve("CallOfDuty4x", "/", liveMod: "mods/xi_promod");

        Assert.Equal("/banlist_v2.dat", result.Path);
        Assert.Null(result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_Cod4x_DoesNotDependOnServerReportedModsTokenCase()
    {
        var result = _sut.Resolve("CallOfDuty4x", "/home/cod4xserver/.callofduty4", liveMod: "Mods/xi_mw2_old");

        Assert.Equal("/home/cod4xserver/.callofduty4/banlist_v2.dat", result.Path);
        Assert.Null(result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_NormalisesBackslashesInLiveModAndPreservesModsTokenCase()
    {
        var result = _sut.Resolve("CallOfDuty4", "/home/cod4server/.callofduty4", liveMod: "MODS\\xi_sniper");

        Assert.Equal("/home/cod4server/.callofduty4/MODS/xi_sniper/ban.txt", result.Path);
        Assert.Equal("xi_sniper", result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_LiveModTokenOnly_FallsBackToMain()
    {
        var result = _sut.Resolve("CallOfDuty4x", "/home/cod4xserver/.callofduty4", liveMod: "mods/");

        Assert.Equal("/home/cod4xserver/.callofduty4/banlist_v2.dat", result.Path);
        Assert.Null(result.ResolvedForMod);
    }

    [Fact]
    public void Resolve_Cod4x_LeadingSlashBeforeModsToken_DoesNotAffectPath()
    {
        var result = _sut.Resolve("CallOfDuty4x", "/home/cod4xserver/.callofduty4", liveMod: "/Mods/xi_mw2_old");

        Assert.Equal("/home/cod4xserver/.callofduty4/banlist_v2.dat", result.Path);
        Assert.Null(result.ResolvedForMod);
    }
}
