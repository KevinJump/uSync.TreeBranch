using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;

using uSync.BackOffice;
using uSync.GitEdition.Configuration;
using uSync.GitEdition.Notifications;
using uSync.GitEdition.Worktrees;

namespace uSync.GitEdition;

[ComposeAfter(typeof(uSync.BackOffice.uSyncBackOfficeComposer))]
internal class Composer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
        => builder.AdduSyncGit();
}

public static class uSyncGitBootExtensions
{
    /// <summary>
    ///  Add uSync Git Edition. The composer calls this, so you only need it in Program.cs
    ///  if you want the git settings applied before other code reads the configuration.
    /// </summary>
    public static IUmbracoBuilder AdduSyncGit(this IUmbracoBuilder builder)
    {
        if (builder.Services.Any(x => x.ServiceType == typeof(GitSiteState)))
            return builder;

        var state = builder.Config is IConfigurationBuilder configurationBuilder
            ? GitSiteConfigurator.Apply(configurationBuilder, builder.Config, GetContentRoot(builder.Config))
            : new GitSiteState();

        builder.Services.AddSingleton(state);
        builder.Services.Configure<uSyncGitSettings>(builder.Config.GetSection(uSyncGitSettings.Section));
        builder.Services.AddSingleton<uSyncGitService>();

        builder.AddNotificationHandler<UmbracoApplicationStartingNotification, uSyncGitSiteHandler>();
        builder.AddNotificationHandler<UmbracoApplicationStartedNotification, uSyncGitSiteHandler>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartingNotification, uSyncGitApplicationStartingHandler>();
        builder.AddNotificationAsyncHandler<uSyncImportCompletedNotification, uSyncGitApplicationStartingHandler>();

        return builder;
    }

    private static string GetContentRoot(IConfiguration configuration)
    {
        // Umbraco sets DataDirectory to {contentRoot}/umbraco/Data before any composers run.
        if (AppDomain.CurrentDomain.GetData(Constants.System.DataDirectoryName) is string dataDirectory)
            return Path.GetFullPath(Path.Combine(dataDirectory, "..", ".."));

        return configuration[HostDefaults.ContentRootKey] ?? Directory.GetCurrentDirectory();
    }
}
