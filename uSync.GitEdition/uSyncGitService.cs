using LibGit2Sharp;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Umbraco.Cms.Core.Services;

using uSync.GitEdition.Git;
using uSync.GitEdition.Worktrees;

namespace uSync.GitEdition;

internal class uSyncGitService
{
    // kept in the database (not the uSync folder), because it describes what has been
    // imported into this database, and a copied database brings it along.
    private const string LastImportKey = "uSync.Git.LastImportCommit";

    private readonly ILogger<uSyncGitService> _logger;
    private readonly IKeyValueService _keyValueService;
    private readonly string _contentRoot;
    private readonly GitContext? _git;

    public uSyncGitService(ILogger<uSyncGitService> logger, IKeyValueService keyValueService, IHostEnvironment hostEnvironment, GitSiteState siteState)
    {
        _logger = logger;
        _keyValueService = keyValueService;
        _contentRoot = hostEnvironment.ContentRootPath;
        _git = siteState.Git;
    }

    public bool HasRepo()
    {
        try
        {
            var path = Repository.Discover(_contentRoot);
            return string.IsNullOrEmpty(path) is false && Repository.IsValid(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "uSync Git: can't open a git repository from {path}", _contentRoot);
            return false;
        }
    }

    private Repository GetRepo()
        => new(Repository.Discover(_contentRoot));

    public string GetLastCommit()
    {
        using var repo = GetRepo();
        return repo.Head.Tip?.Sha ?? string.Empty;
    }

    public string? GetLastImportedCommit()
        => _keyValueService.GetValue(LastImportKey);

    public void SaveLastImportedCommit()
    {
        var commit = GetLastCommit();
        if (string.IsNullOrEmpty(commit)) return;
        _keyValueService.SetValue(LastImportKey, commit);
    }

    /// <summary>
    ///  Are there uncommitted changes in any of the folders (paths relative to the site)?
    /// </summary>
    public bool HasChanges(IEnumerable<string> folders)
    {
        using var repo = GetRepo();

        // not repo.Info.WorkingDirectory: libgit2 resolves junctions (C:\Source -> D:\Source),
        // the content root doesn't, and paths across the two don't make a relative pathspec.
        var workingDirectory = _git?.WorkingDirectory ?? repo.Info.WorkingDirectory;

        var paths = folders
            .Select(x => Path.GetFullPath(Path.Combine(_contentRoot, x.TrimStart('~', '/', '\\'))))
            .Select(x => Path.GetRelativePath(workingDirectory, x).Replace('\\', '/').TrimEnd('/'))
            .ToArray();

        _logger.LogDebug("uSync Git: checking {paths} in {workingDirectory} for changes", paths, workingDirectory);

        var status = repo.RetrieveStatus(new StatusOptions
        {
            PathSpec = paths,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });

        if (status.IsDirty && _logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            foreach (var entry in status)
                _logger.LogDebug("uSync Git: {state} {file}", entry.State, entry.FilePath);
        }

        return status.IsDirty;
    }
}
