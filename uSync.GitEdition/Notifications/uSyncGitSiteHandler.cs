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
///  Tidies up databases left behind by deleted worktrees.
/// </summary>
internal class uSyncGitSiteHandler :
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

            // only this clone's worktree databases carry its id, so another clone (or another site)
            // on the same server is never touched, whatever its databases are called.
            var marker = GitSiteConfigurator.WorktreeMarker(_state.Git!.GetRepositoryId());
            var worktrees = _state.Git.GetWorktreeNames()
                .Select(GitSiteConfigurator.Sanitize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var name in sqlServer.FindDatabasesContaining(marker))
            {
                var worktree = name[(name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..];
                if (worktrees.Contains(worktree)) continue;

                _logger.LogInformation("uSync Git: dropping {database}, git no longer has its worktree {worktree}.", name, worktree);
                database.WithName(name).Drop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "uSync Git: couldn't remove databases left by deleted worktrees.");
        }
    }
}
