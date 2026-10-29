using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

using uSync.BackOffice;
using uSync.BackOffice.Configuration;
using uSync.BackOffice.Services;
using uSync.BackOffice.SyncHandlers.Models;
using uSync.GitEdition.Configuration;
using uSync.GitEdition.Worktrees;

namespace uSync.GitEdition.Notifications;

/// <summary>
///  Imports at startup when the git commit has moved on since the last import into this database.
/// </summary>
/// <remarks>
///  This replaces uSync's ImportAtStartup for a git based workflow: switching branch, pulling,
///  or starting a worktree from a copied database imports, a plain restart doesn't.
/// </remarks>
internal class uSyncGitApplicationStartingHandler :
    INotificationAsyncHandler<UmbracoApplicationStartingNotification>,
    INotificationAsyncHandler<uSyncImportCompletedNotification>
{
    private readonly uSyncGitService _uSyncGitService;
    private readonly ILogger<uSyncGitApplicationStartingHandler> _logger;
    private readonly IRuntimeState _runtimeState;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly ISyncService _syncService;
    private readonly ISyncConfigService _syncConfigService;
    private readonly GitImportSettings _settings;
    private readonly GitSiteState _siteState;

    public uSyncGitApplicationStartingHandler(
        uSyncGitService uSyncGitService,
        ILogger<uSyncGitApplicationStartingHandler> logger,
        IRuntimeState runtimeState,
        IUmbracoContextFactory umbracoContextFactory,
        ISyncService syncService,
        ISyncConfigService syncConfigService,
        IOptions<uSyncGitSettings> settings,
        GitSiteState siteState)
    {
        _siteState = siteState;
        _uSyncGitService = uSyncGitService;
        _logger = logger;
        _runtimeState = runtimeState;
        _umbracoContextFactory = umbracoContextFactory;
        _syncService = syncService;
        _syncConfigService = syncConfigService;
        _settings = settings.Value.Import;
    }

    public async Task HandleAsync(UmbracoApplicationStartingNotification notification, CancellationToken cancellationToken)
    {
        if (_siteState.Active is false || _settings.OnCommitChange is false || _runtimeState.Level < RuntimeLevel.Run) return;

        if (_uSyncGitService.HasRepo() is false)
        {
            _logger.LogWarning("uSync Git: no git repository found, skipping the startup import.");
            return;
        }

        var importAtStartup = _syncConfigService.Settings.ImportAtStartup;
        if (string.IsNullOrWhiteSpace(importAtStartup) is false && importAtStartup.InvariantEquals("none") is false)
        {
            _logger.LogInformation("uSync Git: uSync's ImportAtStartup is {group}, so it does the startup import, not us.", importAtStartup);
            return;
        }

        var folders = _syncConfigService.GetFolders();

        if (_settings.SkipWhenDirty && _uSyncGitService.HasChanges(folders))
        {
            _logger.LogWarning("uSync Git: the uSync folder has uncommitted changes, skipping the startup import.");
            return;
        }

        var commit = _uSyncGitService.GetLastCommit();
        var lastImport = _uSyncGitService.GetLastImportedCommit();

        if (commit == lastImport)
        {
            _logger.LogInformation("uSync Git: already imported {commit}.", commit);
            return;
        }

        _logger.LogInformation("uSync Git: commit is {commit}, last import was {lastImport}, importing {group}.",
            commit, lastImport ?? "(never)", _settings.Group);

        using (_umbracoContextFactory.EnsureUmbracoContext())
        {
            await _syncService.StartupImportAsync(folders, false, new SyncHandlerOptions
            {
                Group = _settings.Group,
            });
        }
    }

    public Task HandleAsync(uSyncImportCompletedNotification notification, CancellationToken cancellationToken)
    {
        if (_siteState.Active is false) return Task.CompletedTask;

        // only a full, clean import brings the database up to the commit. Importing one group
        // (or one handler) from the dashboard, or an import with errors, leaves things to do.
        if (IsFullImport(notification.Group) is false || notification.Actions.ContainsErrors())
        {
            _logger.LogInformation("uSync Git: {group} import wasn't a full, clean import, not saving the commit.", notification.Group ?? "(unknown)");
            return Task.CompletedTask;
        }

        try
        {
            if (_uSyncGitService.HasRepo())
                _uSyncGitService.SaveLastImportedCommit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "uSync Git: couldn't save the imported commit.");
        }

        return Task.CompletedTask;
    }

    private bool IsFullImport(string? group)
        => group is not null
            && (group.InvariantEquals(global::uSync.BackOffice.uSync.EverythingGroupName) || group.InvariantEquals(_settings.Group));
}
