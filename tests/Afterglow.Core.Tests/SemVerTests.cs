using Afterglow.Core.Services;

namespace Afterglow.Core.Tests;

/// <summary>
/// SemVer 2.0 precedence (semver.org §11) and the concrete F-42/F-65 cases.
/// On the pre-fix code, <c>TryParseTag</c> returned null for any prerelease tag
/// and <c>IsNewer</c> compared only Major.Minor.Build, so every one of the
/// prerelease assertions below failed (a beta compared equal to its release).
/// </summary>
public class SemVerTests
{
    // The ordered chain from semver.org §11, lowest precedence first.
    private static readonly string[] PrecedenceChain =
    [
        "1.0.0-alpha",
        "1.0.0-alpha.1",
        "1.0.0-alpha.beta",
        "1.0.0-beta",
        "1.0.0-beta.2",
        "1.0.0-beta.11",
        "1.0.0-rc.1",
        "1.0.0",
    ];

    [Fact]
    public void Precedence_chain_from_the_spec_is_strictly_increasing()
    {
        for (int i = 0; i + 1 < PrecedenceChain.Length; i++)
        {
            var lower = SemVer.TryParse(PrecedenceChain[i]);
            var higher = SemVer.TryParse(PrecedenceChain[i + 1]);
            Assert.NotNull(lower);
            Assert.NotNull(higher);
            Assert.True(lower!.Value.CompareTo(higher!.Value) < 0,
                $"{PrecedenceChain[i]} should precede {PrecedenceChain[i + 1]}");
            Assert.True(higher.Value.CompareTo(lower.Value) > 0);
        }
    }

    [Theory]
    // A prerelease has lower precedence than its normal version.
    [InlineData("v1.2.0", "1.2.0-beta.3", true)]
    // ...and the normal version is therefore never "newer" than itself-as-release.
    [InlineData("v1.2.0", "1.2.0", false)]
    // A later beta is newer than an earlier one (numeric identifier compare).
    [InlineData("v1.2.0-beta.4", "1.2.0-beta.3", true)]
    [InlineData("v1.2.0-beta.3", "1.2.0-beta.4", false)]
    // beta.11 > beta.2 numerically (not lexically).
    [InlineData("v1.2.0-beta.11", "1.2.0-beta.2", true)]
    // A stable user is correctly offered a newer stable.
    [InlineData("v1.3.0", "1.2.0", true)]
    // A stable user is NOT offered a prerelease of the next version.
    [InlineData("v1.3.0-beta.1", "1.2.0", true)]
    [InlineData("v1.2.0", "1.3.0-beta.1", false)]
    public void IsNewer_follows_semver_precedence(string latestTag, string running, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(latestTag, SemVer.TryParse(running)!.Value));
    }

    [Fact]
    public void Build_metadata_is_ignored_in_precedence()
    {
        var withMeta = SemVer.TryParse("1.2.0-beta.3+51caf3d");
        var without = SemVer.TryParse("1.2.0-beta.3");
        Assert.NotNull(withMeta);
        Assert.NotNull(without);
        Assert.Equal(0, withMeta!.Value.CompareTo(without!.Value));
        // And the suffix survives parsing (this is what the About box shows).
        Assert.Equal("beta.3", withMeta.Value.Prerelease);
    }

    [Theory]
    [InlineData("v1.2.0-beta.3", 1, 2, 0, "beta.3")]
    [InlineData("1.2.0", 1, 2, 0, "")]
    [InlineData("v2.0", 2, 0, 0, "")]       // historic 2-part tag
    [InlineData("v1", 1, 0, 0, "")]
    public void Parse_decomposes_core_and_prerelease(string tag, int major, int minor, int patch, string pre)
    {
        var v = SemVer.TryParse(tag);
        Assert.NotNull(v);
        Assert.Equal((major, minor, patch, pre), (v!.Value.Major, v.Value.Minor, v.Value.Patch, v.Value.Prerelease));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2.0-")]          // dangling prerelease separator
    [InlineData("1.2.0-beta..3")]   // empty identifier
    [InlineData("1.2.3.4")]         // too many core fields
    public void Unparseable_tags_return_null_and_never_claim_an_update(string tag)
    {
        Assert.Null(SemVer.TryParse(tag));
        Assert.False(UpdateChecker.IsNewer(tag, new SemVer(1, 2, 0, "beta.3")));
    }
}
