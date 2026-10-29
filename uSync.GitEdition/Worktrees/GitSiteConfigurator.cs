using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using uSync.GitEdition.Configuration;
using uSync.GitEdition.Git;

namespace uSync.GitEdition.Worktrees;

/// <summary>
///  What the configurator did, kept so it can be logged once logging is up.
/// </summary>
public class GitSiteState
{
    public GitContext? Git { get; init; }

    public string? DatabaseName { get; set; }

    public string? Urls { get; set; }

    internal List<(LogLevel Level, string Message, object?[] Args)> Messages { get; } = [];

    internal void Log(LogLevel level, string message, params object?[] args) => Messages.Add((level, message, args));
}

/// <summary>
///  Adjusts the site's configuration for the git checkout it is running from: database name,
///  seeding a worktree's database, shared media and ports.
/// </summary>
/// <remarks>
///  Runs while the site is being configured, before anything reads the connection string.
///  Nothing is written to disk, the values go into an in-memory configuration source that
///  sits on top of appsettings and environment variables.
/// </remarks>
internal static class GitSiteConfigurator
{
    private const string ConnectionStringKey = "ConnectionStrings:umbracoDbDSN";
    private const string ProviderNameKey = "ConnectionStrings:umbracoDbDSN_ProviderName";

    public static GitSiteState Apply(IConfigurationBuilder configurationBuilder, IConfiguration configuration, string contentRoot)
    {
        var git = GitContext.Discover(contentRoot);
        var state = new GitSiteState { Git = git };

        if (git is null)
        {
            state.Log(LogLevel.Information, "uSync Git: no git repository found above {contentRoot}", contentRoot);
            return state;
        }

        var settings = configuration.GetSection(uSyncGitSettings.Section).Get<uSyncGitSettings>() ?? new();
        var isWorktree = git.IsLinkedWorktree && settings.Worktree.Enabled;
        var overrides = new Dictionary<string, string?>();

        state.Log(LogLevel.Information, "uSync Git: branch {branch}, worktree {worktree}", git.Branch ?? "(detached)", git.WorktreeName ?? "(main checkout)");

        ConfigureDatabase(configuration, contentRoot, git, settings, isWorktree, overrides, state);

        if (isWorktree)
        {
            var mainSiteRoot = git.MapToMainCheckout(contentRoot);

            if (settings.Worktree.ShareMedia && mainSiteRoot is not null
                && string.IsNullOrWhiteSpace(configuration["Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath"]))
            {
                var media = Path.Combine(mainSiteRoot, "wwwroot", "media");
                overrides["Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath"] = media;
                state.Log(LogLevel.Information, "uSync Git: serving media from {media}", media);
            }

            if (settings.Worktree.Ports)
            {
                var (https, http) = PortPicker.Pick(git.WorktreeName!, settings.Worktree.PortRangeStart);
                state.Urls = $"https://localhost:{https};http://localhost:{http}";
                overrides["urls"] = state.Urls;
                state.Log(LogLevel.Information, "uSync Git: worktree site will run on {urls}", state.Urls);
            }
        }

        if (overrides.Count > 0)
            configurationBuilder.AddInMemoryCollection(overrides);

        return state;
    }

    private static void ConfigureDatabase(IConfiguration configuration, string contentRoot, GitContext git,
        uSyncGitSettings settings, bool isWorktree, Dictionary<string, string?> overrides, GitSiteState state)
    {
        var environment = configuration["environment"] ?? "Production";

        var database = GitDatabase.Create(
            configuration[ConnectionStringKey], configuration[ProviderNameKey], DataDirectory(contentRoot));

        if (database is null)
        {
            // not installed yet (or a database we don't know how to handle), leave it to Umbraco.
            return;
        }

        var target = database.WithName(DatabaseNameFor(database.Name, git.Branch, environment, settings, isWorktree ? git.WorktreeName : null, database.IsSharedBetweenCheckouts));
        state.DatabaseName = target.Name;

        if (target.ConnectionString != database.ConnectionString)
        {
            overrides[ConnectionStringKey] = target.ConnectionString;
            overrides[ProviderNameKey] = target.ProviderName;
        }

        state.Log(LogLevel.Information, "uSync Git: using database {database}", target.Name);

        // a missing database is only ours to create when we picked its name.
        if (isWorktree is false && settings.Db.NameByBranch is false) return;

        var seed = isWorktree ? settings.Worktree.Seed : WorktreeSeed.Empty;
        if (seed == WorktreeSeed.None) return;

        try
        {
            if (target.Exists()) return;
        }
        catch (Exception ex)
        {
            state.Log(LogLevel.Error, "uSync Git: can't check if {database} exists: {error}", target.Name, ex.Message);
            return;
        }

        if (seed == WorktreeSeed.Copy && TryCopyFromMainCheckout(database, target, contentRoot, git, environment, settings, state))
            return;

        // no database yet, so let Umbraco install one and uSync's ImportOnFirstBoot fill it.
        overrides["Umbraco:CMS:Unattended:InstallUnattended"] = "true";
        state.Log(LogLevel.Information, "uSync Git: {database} doesn't exist, it will be created by an unattended install", target.Name);
    }

    private static bool TryCopyFromMainCheckout(GitDatabase database, GitDatabase target, string contentRoot,
        GitContext git, string environment, uSyncGitSettings settings, GitSiteState state)
    {
        var mainSiteRoot = git.MapToMainCheckout(contentRoot);
        if (mainSiteRoot is null)
        {
            state.Log(LogLevel.Warning, "uSync Git: can't find the main checkout to copy the database from");
            return false;
        }

        var mainBranch = GitContext.Discover(mainSiteRoot)?.Branch;
        var source = database
            .WithName(DatabaseNameFor(database.Name, mainBranch, environment, settings, null, database.IsSharedBetweenCheckouts))
            .WithDataDirectory(DataDirectory(mainSiteRoot));

        try
        {
            if (source.Exists() is false)
            {
                state.Log(LogLevel.Warning, "uSync Git: the main checkout's database {database} doesn't exist, nothing to copy", source.Name);
                return false;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            target.CopyFrom(source);
            state.Log(LogLevel.Information, "uSync Git: copied {source} to {target} in {elapsed}ms", source.Name, target.Name, timer.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            state.Log(LogLevel.Error, "uSync Git: copying {source} to {target} failed: {error}", source.Name, target.Name, ex.Message);
            try { target.Drop(); } catch { /* leave whatever is there, the install will tell us */ }
            return false;
        }
    }

    internal static string DatabaseNameFor(string name, string? branch, string environment, uSyncGitSettings settings, string? worktree, bool isShared)
    {
        if (settings.Db.NameByBranch && branch is not null)
        {
            name = settings.Db.Template
                .Replace("{db}", name, StringComparison.OrdinalIgnoreCase)
                .Replace("{branch}", branch, StringComparison.OrdinalIgnoreCase)
                .Replace("{env}", environment, StringComparison.OrdinalIgnoreCase)
                .Replace("{ticks}", string.Empty, StringComparison.OrdinalIgnoreCase); // gave a new database every install.
        }

        // a SQLite file in the worktree's umbraco/Data is already separate, a server database isn't.
        if (worktree is not null && isShared)
            name = $"{name}{WorktreeSuffix}{worktree}";

        return Sanitize(name);
    }

    internal const string WorktreeSuffix = "_wt_";

    internal static string Sanitize(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var c in name)
            result.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_');
        return result.ToString().Trim('.');
    }

    internal static string DataDirectory(string siteRoot) => Path.Combine(siteRoot, "umbraco", "Data");
}
