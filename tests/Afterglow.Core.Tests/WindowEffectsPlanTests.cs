using Afterglow.App.Shell;

namespace Afterglow.Core.Tests;

/// <summary>
/// The Windows 11 window-integration version gate (src/Afterglow.App/Shell/WindowEffectsPlan.cs).
/// The test project does not reference the WPF app, so the csproj compiles that one
/// WPF-free file in. Only Windows 11 25H2 (build 26200) is available to run the real
/// window on; these pin the decisions for every other build, including the Windows 10
/// fallback that cannot be exercised here.
/// </summary>
public sealed class WindowEffectsPlanTests
{
    [Theory]
    [InlineData(10240)] // Windows 10 1507
    [InlineData(17763)] // Windows 10 1809 (attribute 20 was 19 back then)
    [InlineData(19045)] // Windows 10 22H2, the last Windows 10
    [InlineData(21999)] // one below the first Windows 11 build
    public void Windows_10_keeps_the_classic_opaque_window_and_sets_no_dwm_attributes(int build)
    {
        var plan = WindowEffectsPlan.For(build, highContrast: false, transparencyEffects: true, backdropOptOut: false);

        Assert.Equal(WindowEffectsPlan.Classic, plan);
        Assert.False(plan.DarkFrame);
        Assert.False(plan.RoundedCorners);
        Assert.False(plan.BorderColor);
        Assert.False(plan.SnapLayouts);
        Assert.Equal(SystemBackdrop.None, plan.Backdrop);
        Assert.False(plan.UsesBackdrop);
    }

    [Theory]
    [InlineData(22000)] // Windows 11 21H2
    [InlineData(22620)] // one below 22H2
    public void Windows_11_21H2_gets_the_frame_and_snap_layouts_but_stays_opaque(int build)
    {
        var plan = WindowEffectsPlan.For(build, highContrast: false, transparencyEffects: true, backdropOptOut: false);

        Assert.True(plan.DarkFrame);
        Assert.True(plan.RoundedCorners);
        Assert.True(plan.BorderColor);
        Assert.True(plan.SnapLayouts);
        Assert.Equal(SystemBackdrop.None, plan.Backdrop);
        Assert.False(plan.UsesBackdrop);
    }

    [Theory]
    [InlineData(22621)] // Windows 11 22H2: DWMWA_SYSTEMBACKDROP_TYPE documented from here
    [InlineData(22631)] // 23H2
    [InlineData(26100)] // 24H2
    [InlineData(26200)] // 25H2, the development machine
    public void Windows_11_22H2_and_later_add_the_mica_backdrop(int build)
    {
        var plan = WindowEffectsPlan.For(build, highContrast: false, transparencyEffects: true, backdropOptOut: false);

        Assert.True(plan.DarkFrame);
        Assert.True(plan.RoundedCorners);
        Assert.True(plan.BorderColor);
        Assert.True(plan.SnapLayouts);
        Assert.Equal(SystemBackdrop.Mica, plan.Backdrop);
        Assert.True(plan.UsesBackdrop);
    }

    [Theory]
    [InlineData(true, true, false)]  // high contrast
    [InlineData(false, false, false)] // Settings > Personalization > Colors > Transparency effects off
    [InlineData(false, true, true)]  // no --mica (the default), or --screenshot
    public void Preferences_against_translucency_keep_the_window_opaque_but_keep_the_frame(
        bool highContrast, bool transparencyEffects, bool backdropOptOut)
    {
        var plan = WindowEffectsPlan.For(26200, highContrast, transparencyEffects, backdropOptOut);

        Assert.Equal(SystemBackdrop.None, plan.Backdrop);
        Assert.False(plan.UsesBackdrop);
        Assert.True(plan.DarkFrame);
        Assert.True(plan.RoundedCorners);
        Assert.True(plan.SnapLayouts);
    }

    [Fact]
    public void Backdrop_values_are_the_documented_dwm_constants()
    {
        // DWM_SYSTEMBACKDROP_TYPE: DWMSBT_NONE = 1, DWMSBT_MAINWINDOW = 2, DWMSBT_TABBEDWINDOW = 4.
        Assert.Equal(1, (int)SystemBackdrop.None);
        Assert.Equal(2, (int)SystemBackdrop.Mica);
        Assert.Equal(4, (int)SystemBackdrop.MicaAlt);
        Assert.Equal(22000, WindowEffectsPlan.Windows11Build);
        Assert.Equal(22621, WindowEffectsPlan.SystemBackdropBuild);
    }
}
