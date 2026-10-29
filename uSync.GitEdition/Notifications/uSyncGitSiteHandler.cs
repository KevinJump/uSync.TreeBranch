using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

using uSync.GitEdition.Configuration;
using uSync.GitEdition.Worktrees;

namespace uSync.GitEdition.Notifications;

/// <summary>
///  Reports what the site configurator did, and tidies up databases left behind by deleted worktrees.
/// </summary>
internal class uSyncGitSiteHandler :
    INotificationHandler<UmbracoApplicationStartingNotification>,
    INotificationHandler<UmbracoApplicationStartedNotification>
{
    private readonly GitSiteState _state;
    private readonly ILogger<uSyncGitSiteHandler> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly GitWorktreeSettings _settings;

    public uSyncGitSiteHandler(
        GitSiteState state,
        ILogger<uSyncGitSiteHandler> logger,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        IOptions<uSyncGitSettings> settings)
    {
        _state = state;
        _logger = logger;
        _configuration = configuration;
        _hostEnvironment = hostEnvironment;
        _settings = settings.Value.Worktree;
    }

    public void Handle(UmbracoApplicationStartingNotification notification)
    {
        foreach (var (level, message, args) in _state.Messages)
        {
#pragma warning disable CA2254 // the templates are constants, they're just stored for later.
            _logger.Log(level, message, args);
#pragma warning restore CA2254
        }
    }

    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        if (_settings.Enabled is false || _settings.RemoveOrphanDatabases is false) return;

        // only the main checkout tidies up, a worktree shouldn't be dropping its siblings' databases.
        if (_state.Git is null || _state.Git.IsLinkedWorktree) return;

        _ = Task.Run(RemoveOrphanDatabases);
    }

    private void RemoveOrphanDatabases()
    {
        try
        {
            var database = GitDatabase.Create(
                _configuration.GetConnectionString("umbracoDbDSN"),
                _configuration["ConnectionStrings:umbracoDbDSN_ProviderName"],
                GitSiteConfigurator.DataDirectory(_hostEnvironment.ContentRootPath));

            // SQLite worktree databases live in the worktree folder, and go when it goes.
            if (database is not SqlServerGitDatabase sqlServer) return;

            var prefix = database.Name + GitSiteConfigurator.WorktreeSuffix;
            var live = _state.Git!.GetLiveWorktreeNames()
                .Select(x => GitSiteConfigurator.Sanitize(prefix + x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var name in sqlServer.FindDatabases(prefix).Where(x => live.Contains(x) is false))
            {
                _logger.LogInformation("uSync Git: dropping {database}, its worktree no longer exists.", name);
                database.WithName(name).Drop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "uSync Git: couldn't remove databases left by deleted worktrees.");
        }
    }
}
