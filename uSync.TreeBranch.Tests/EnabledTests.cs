using Microsoft.Extensions.Configuration;

using uSync.TreeBranch.Worktrees;

namespace uSync.TreeBranch.Tests;

public class EnabledTests
{
    private static bool IsEnabled(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();
        return GitSiteConfigurator.IsEnabled(configuration, out _);
    }

    [Fact]
    public void DevelopmentEnvironment_IsOn()
        => Assert.True(IsEnabled(("environment", "Development")));

    [Fact]
    public void Debug_IsOn()
        => Assert.True(IsEnabled(("environment", "Staging"), ("Umbraco:CMS:Hosting:Debug", "true")));

    [Fact]
    public void NotDebugOrDevelopment_IsOff()
        => Assert.False(IsEnabled(("environment", "Production")));

    [Fact]
    public void ProductionRuntimeMode_IsOff_EvenInDevelopment()
        => Assert.False(IsEnabled(("environment", "Development"), ("Umbraco:CMS:Runtime:Mode", "Production")));

    [Fact]
    public void GitEnabled_OverridesEitherWay()
    {
        Assert.True(IsEnabled(("environment", "Production"), ("Umbraco:CMS:Runtime:Mode", "Production"), ("Git:Enabled", "true")));
        Assert.False(IsEnabled(("environment", "Development"), ("Git:Enabled", "false")));
    }
}
