using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;

using uSync.BackOffice;
using uSync.Branches.Configuration;
using uSync.Branches.Notifications;
using uSync.Branches.Worktrees;

namespace uSync.Branches;

[ComposeAfter(typeof(uSync.BackOffice.uSyncBackOfficeComposer))]
internal class Composer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
        => builder.AdduSyncGit();
}

public static class uSyncGitBootExtensions
{
    /// <summary>
    ///  Add uSync Branches. The composer calls this, so you only need it in Program.cs
    ///  if you want the git settings applied before other code reads the configuration.
    /// </summary>
    public static IUmbracoBuilder AdduSyncGit(this IUmbracoBuilder builder)
    {
        if (builder.Services.Any(x => x.ServiceType == typeof(GitSiteState)))
            return builder;

        var state = builder.Config is IConfigurationBuilder configurationBuilder
            ? GitSiteConfigurator.Apply(configurationBuilder, builder.Config, GetContentRoot(builder.Config))
            : new GitSiteState();

        // logged now, not at startup, so a site that fails to boot (e.g. a bad connection string) still says why.
        var logger = builder.BuilderLoggerFactory.CreateLogger(typeof(GitSiteConfigurator));
        foreach (var (level, exception, message, args) in state.Messages)
        {
#pragma warning disable CA2254 // the templates are constants, they're just stored until logging is up.
            logger.Log(level, exception, message, args);
#pragma warning restore CA2254
        }

        builder.Services.AddSingleton(state);
        builder.Services.Configure<uSyncGitSettings>(builder.Config.GetSection(uSyncGitSettings.Section));
        builder.Services.AddSingleton<uSyncGitService>();

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
