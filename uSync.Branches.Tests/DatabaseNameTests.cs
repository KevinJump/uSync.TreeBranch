using uSync.Branches.Configuration;
using uSync.Branches.Worktrees;

namespace uSync.Branches.Tests;

public class DatabaseNameTests
{
    private static uSyncGitSettings Settings(bool nameByBranch, string template = "{db}.{branch}")
        => new() { Db = new GitDbSettings { NameByBranch = nameByBranch, Template = template } };

    [Fact]
    public void Off_KeepsTheConfiguredName()
        => Assert.Equal("Umbraco", GitSiteConfigurator.DatabaseNameFor("Umbraco", "feature/x", "main", "Development", Settings(false), null));

    [Fact]
    public void NameByBranch_NamesTheBranch()
        => Assert.Equal("Umbraco.feature_x", GitSiteConfigurator.DatabaseNameFor("Umbraco", "feature/x", "main", "Development", Settings(true), null));

    [Fact]
    public void NameByBranch_KeepsTheNameOnTheDefaultBranch()
        => Assert.Equal("Umbraco", GitSiteConfigurator.DatabaseNameFor("Umbraco", "Main", "main", "Development", Settings(true), null));

    [Fact]
    public void NameByBranch_NamesEveryBranchWhenThereIsNoDefault()
        => Assert.Equal("Umbraco.main", GitSiteConfigurator.DatabaseNameFor("Umbraco", "main", null, "Development", Settings(true), null));

    [Fact]
    public void NameByBranch_DetachedHeadKeepsTheName()
        => Assert.Equal("Umbraco", GitSiteConfigurator.DatabaseNameFor("Umbraco", null, "main", "Development", Settings(true), null));

    [Fact]
    public void Template_FillsInTheEnvironment_AndDropsTicks()
        => Assert.Equal("Umbraco_Staging_x", GitSiteConfigurator.DatabaseNameFor("Umbraco", "x", "main", "Staging", Settings(true, "{db}_{env}_{branch}{ticks}"), null));

    [Fact]
    public void ConfiguredName_IsNeverSanitized()
        => Assert.Equal("My Site-db.x", GitSiteConfigurator.DatabaseNameFor("My Site-db", "x", "main", "Development", Settings(true), null));

    [Fact]
    public void Worktree_AddsTheSuffix()
    {
        var suffix = GitSiteConfigurator.WorktreeSuffix("ab12cd34", "my worktree");
        Assert.Equal("_wt_ab12cd34_my_worktree", suffix);
        Assert.Equal("Umbraco_wt_ab12cd34_my_worktree", GitSiteConfigurator.DatabaseNameFor("Umbraco", "main", "main", "Development", Settings(false), suffix));
    }

    [Theory]
    [InlineData("feature/x", "feature_x")]
    [InlineData("v17/main", "v17_main")]
    [InlineData("release-1.2", "release_1.2")]
    [InlineData(".hidden.", "hidden")]
    [InlineData("naïve", "na_ve")]
    public void Sanitize_KeepsLettersDigitsUnderscoreAndDot(string value, string expected)
        => Assert.Equal(expected, GitSiteConfigurator.Sanitize(value));
}
