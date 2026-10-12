using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Core.Tests;

/// <summary>
/// Which DRS profiles `drs --cleanup` and `drs --selftest` may delete. The
/// leftover being targeted is the one 1.2.0-beta.4 and earlier created by
/// truncating every name to its first character: profile "A", application
/// "a", holding up to the three settings Afterglow writes. Anything else —
/// predefined, a full name anywhere, a foreign setting, an unreadable entry —
/// must be left alone. Pure: no test here opens a driver session.
/// </summary>
public class DrsProfileSignatureTests
{
    private static readonly DrsSettingSnapshot Frl = new(DrsApi.FrameRateLimiterId, 0, 58, false);
    private static readonly DrsSettingSnapshot Vsync = new(DrsApi.VsyncModeId, 0, DrsApi.VsyncForceOff, false);
    private static readonly DrsSettingSnapshot PreRender = new(DrsApi.PreRenderedFramesId, 0, 1, false);

    /// <summary>The store's own counts match what was enumerated unless a test says otherwise.</summary>
    private static DrsProfileSnapshot Profile(
        string name, bool predefined, DrsAppSnapshot[] apps, DrsSettingSnapshot[] settings,
        int? declaredApps = null, int? declaredSettings = null) =>
        new(name, predefined, apps, settings, declaredApps ?? apps.Length, declaredSettings ?? settings.Length);

    private static DrsAppSnapshot App(string name, bool predefined = false) => new(name, predefined);

    [Fact]
    public void The_exact_leftover_matches()
    {
        // What the maintainer's store holds: "A", user-created, app "a", 3 settings.
        var junk = Profile("A", false, [App("a")], [PreRender, Vsync, Frl]);

        var verdict = DrsProfileSignatures.EvaluateTruncationLeftover(junk);

        Assert.True(verdict.Matches, verdict.Reason);
    }

    [Fact]
    public void Several_one_letter_apps_still_match()
    {
        // Every later create collided with "A" and attached its own truncated exe to it.
        var junk = Profile("A", false, [App("a"), App("c"), App("r")], [Frl]);

        Assert.True(DrsProfileSignatures.EvaluateTruncationLeftover(junk).Matches);
    }

    [Fact]
    public void A_leftover_whose_settings_were_cleared_still_matches()
    {
        // The old --clear only removed the three settings from a non-"Afterglow - "
        // profile, so "A" + "a" with nothing on it is a real leftover too.
        var junk = Profile("A", false, [App("a")], []);

        Assert.True(DrsProfileSignatures.EvaluateTruncationLeftover(junk).Matches);
    }

    [Fact]
    public void A_predefined_profile_named_A_is_never_a_match()
    {
        var shipped = Profile("A", true, [App("a")], [Frl]);

        var verdict = DrsProfileSignatures.EvaluateTruncationLeftover(shipped);

        Assert.False(verdict.Matches);
        Assert.Contains("predefined", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("apex.exe")]
    [InlineData("ab")]
    public void A_user_profile_A_with_a_full_application_name_is_left_alone(string appName)
    {
        var mine = Profile("A", false, [App("a"), App(appName)], [Frl]);

        var verdict = DrsProfileSignatures.EvaluateTruncationLeftover(mine);

        Assert.False(verdict.Matches);
        Assert.Contains(appName, verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setting_Afterglow_never_writes_disqualifies_the_profile()
    {
        const uint foreign = 0x1057EB71;   // any ID outside the three Afterglow writes
        var mine = Profile("A", false, [App("a")], [Frl, new DrsSettingSnapshot(foreign, 0, 1, false)]);

        var verdict = DrsProfileSignatures.EvaluateTruncationLeftover(mine);

        Assert.False(verdict.Matches);
        Assert.Contains("0x1057EB71", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_with_no_applications_is_left_alone()
    {
        var bare = Profile("A", false, [], [Frl]);

        Assert.False(DrsProfileSignatures.EvaluateTruncationLeftover(bare).Matches);
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("Afterglow - apex.exe")]
    [InlineData("")]
    public void A_name_that_is_not_one_character_is_left_alone(string name)
    {
        var profile = Profile(name, false, [App("a")], [Frl]);

        Assert.False(DrsProfileSignatures.EvaluateTruncationLeftover(profile).Matches);
        Assert.Equal(name.Length == 1, DrsProfileSignatures.IsCleanupCandidate(name));
    }

    [Theory]
    [InlineData("B")]
    [InlineData("a")]   // case matters: the bug wrote "A"
    public void A_one_character_name_other_than_A_is_left_alone(string name)
    {
        // Every name the bug produced began with "Afterglow…", so it was always "A".
        var profile = Profile(name, false, [App("b")], [Frl]);

        Assert.True(DrsProfileSignatures.IsCleanupCandidate(name));   // reported on...
        Assert.False(DrsProfileSignatures.EvaluateTruncationLeftover(profile).Matches);   // ...never deleted
    }

    [Fact]
    public void A_predefined_application_disqualifies_the_profile()
    {
        var profile = Profile("A", false, [App("a", predefined: true)], [Frl]);

        Assert.False(DrsProfileSignatures.EvaluateTruncationLeftover(profile).Matches);
    }

    [Theory]
    [InlineData(2, 3)]   // the store says 2 apps, 1 was read
    [InlineData(1, 4)]   // the store says 4 settings, 3 were read: the 4th could be anything
    [InlineData(1, 2)]   // more settings came back than the store declared
    public void A_partial_or_inconsistent_read_is_never_judged(int declaredApps, int declaredSettings)
    {
        var profile = Profile("A", false, [App("a")], [Frl, Vsync, PreRender], declaredApps, declaredSettings);

        var verdict = DrsProfileSignatures.EvaluateTruncationLeftover(profile);

        Assert.False(verdict.Matches);
        Assert.Contains("partial or inconsistent read", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_three_managed_setting_ids_count_as_Afterglows()
    {
        Assert.True(DrsProfileSignatures.IsAfterglowSetting(DrsApi.FrameRateLimiterId));
        Assert.True(DrsProfileSignatures.IsAfterglowSetting(DrsApi.VsyncModeId));
        Assert.True(DrsProfileSignatures.IsAfterglowSetting(DrsApi.PreRenderedFramesId));
        Assert.False(DrsProfileSignatures.IsAfterglowSetting(DrsApi.VsyncForceOff));   // a value, not an ID
        Assert.False(DrsProfileSignatures.IsAfterglowSetting(0));
    }

    // --- drs --selftest: delete exactly the run's own profile -----------------

    private const string SelfTestExe = "afterglow-drs-selftest-0a1b2c3d.exe";

    [Fact]
    public void The_self_test_profile_name_is_the_full_prefixed_lower_case_exe()
    {
        Assert.Equal("Afterglow - afterglow-drs-selftest-0a1b2c3d.exe",
            DrsApi.ProfileNameFor("Afterglow-DRS-Selftest-0A1B2C3D.exe"));
    }

    [Fact]
    public void The_self_test_recognises_its_own_profile_with_or_without_its_app()
    {
        string name = DrsApi.ProfileNameFor(SelfTestExe);

        Assert.True(DrsProfileSignatures.EvaluateSelfTestProfile(
            Profile(name, false, [App(SelfTestExe)], [Frl]), SelfTestExe).Matches);

        // CreateApplication failed after CreateProfile: the bare profile is still ours.
        Assert.True(DrsProfileSignatures.EvaluateSelfTestProfile(
            Profile(name, false, [], []), SelfTestExe).Matches);
    }

    [Fact]
    public void The_self_test_never_deletes_anything_but_its_own_profile()
    {
        string name = DrsApi.ProfileNameFor(SelfTestExe);

        // Another run's profile, the truncation leftover, a predefined profile,
        // a profile holding someone else's app, a partial read: all refused.
        DrsProfileSnapshot[] notOurs =
        [
            Profile(DrsApi.ProfileNameFor("afterglow-drs-selftest-ffffffff.exe"), false, [App("afterglow-drs-selftest-ffffffff.exe")], [Frl]),
            Profile("A", false, [App("a")], [Frl]),
            Profile(name, true, [App(SelfTestExe)], [Frl]),
            Profile(name, false, [App(SelfTestExe), App("game.exe")], [Frl]),
            Profile(name, false, [App(SelfTestExe, predefined: true)], [Frl]),
            Profile(name, false, [App(SelfTestExe)], [Frl], declaredApps: 2),
        ];

        Assert.All(notOurs, p => Assert.False(DrsProfileSignatures.EvaluateSelfTestProfile(p, SelfTestExe).Matches, p.Name));
    }
}
