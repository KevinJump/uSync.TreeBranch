using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Extensions;

using uSync.TreeBranch.Configuration;
using uSync.TreeBranch.Git;

namespace uSync.TreeBranch.Worktrees;

/// <summary>
///  What the configurator did. Messages are kept and logged by the caller, the configurator runs before it has a logger.
/// </summary>
public class GitSiteState
{
    public GitContext? Git { get; init; }

    /// <summary>
    ///  False when the site isn't a development site (or Git:Enabled is off), and nothing should happen.
    /// </summary>
    public bool Active => Git is not null;

    public string? DatabaseName { get; set; }

    public string? Urls { get; set; }

    internal List<(LogLevel Level, Exception? Exception, string Message, object?[] Args)> Messages { get; } = [];

    internal void Log(LogLevel level, string message, params object?[] args) => Messages.Add((level, null, message, args));

    internal void Log(LogLevel level, Exception exception, string message, params object?[] args) => Messages.Add((level, exception, message, args));
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
        try
        {
            return Configure(configurationBuilder, configuration, contentRoot);
        }
        catch (Exception ex)
        {
            // this is a development helper, it shouldn't stop the site booting.
            var failed = new GitSiteState();
            failed.Log(LogLevel.Error, ex, "uSync Git: off, setting up the site for git failed");
            return failed;
        }
    }

    private static GitSiteState Configure(IConfigurationBuilder configurationBuilder, IConfiguration configuration, string contentRoot)
    {
        if (IsEnabled(configuration, out var reason) is false)
        {
            var disabled = new GitSiteState();
            disabled.Log(LogLevel.Information, "uSync Git: off, {reason}", reason);
            return disabled;
        }

        var git = GitContext.Discover(contentRoot);
        var state = new GitSiteState { Git = git };

        if (git is null)
        {
            state.Log(LogLevel.Information, "uSync Git: no git repository found above {contentRoot}", contentRoot);
            return state;
        }

        var settings = configuration.GetSection(uSyncGitSettings.Section).Get<uSyncGitSettings>() ?? new();
        var isWorktree = git.IsLinkedWorktree && settings.Worktree.Enabled;
        var mainSiteRoot = isWorktree ? git.MapToMainCheckout(contentRoot) : null;
        var overrides = new Dictionary<string, string?>();

        state.Log(LogLevel.Information, "uSync Git: branch {branch}, worktree {worktree}", git.Branch ?? "(detached)", git.WorktreeName ?? "(main checkout)");

        // first, so the connection string from a gitignored settings file is there to read.
        if (settings.Worktree.ShareLocalSettings && mainSiteRoot is not null)
            ShareLocalSettings(configurationBuilder, contentRoot, mainSiteRoot, state);

        if (settings.uSyncDefaults)
            AddDefault(configuration, overrides, state, "uSync:Settings:ImportOnFirstBoot", "true");

        ConfigureDatabase(configuration, contentRoot, git, settings, isWorktree, overrides, state);

        if (isWorktree)
        {
            if (settings.Worktree.ShareMedia && mainSiteRoot is not null
                && string.IsNullOrWhiteSpace(configuration["Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath"]))
            {
                var media = Path.Combine(mainSiteRoot, "wwwroot", "media");
                overrides["Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath"] = media;
                state.Log(LogLevel.Information, "uSync Git: serving media from {media}", media);
            }

            if (settings.Worktree.Ports)
            {
                try
                {
                    var (https, http) = PortPicker.Pick(git.WorktreeName!, settings.Worktree.PortRangeStart);
                    state.Urls = $"https://localhost:{https};http://localhost:{http}";
                    overrides["urls"] = state.Urls;
                    state.Log(LogLevel.Information, "uSync Git: worktree site will run on {urls}", state.Urls);
                }
                catch (Exception ex)
                {
                    // the database is already set up for the worktree, so carry on with the site's own urls.
                    state.Log(LogLevel.Warning, ex, "uSync Git: couldn't pick ports for the worktree, using the site's urls");
                }
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

        var defaultBranch = string.IsNullOrWhiteSpace(settings.DefaultBranch) ? git.FindDefaultBranch() : settings.DefaultBranch;
        if (settings.Db.NameByBranch)
        {
            if (defaultBranch is null)
                state.Log(LogLevel.Warning, "uSync Git: can't work out the default branch, so every branch gets its own database name. Set Git:DefaultBranch to keep the configured name on it.");
            else
                state.Log(LogLevel.Information, "uSync Git: default branch is {branch}", defaultBranch);
        }

        // a SQLite file in the worktree's umbraco/Data is already separate, a server database isn't.
        var worktreeSuffix = isWorktree && database.IsSharedBetweenCheckouts
            ? WorktreeSuffix(git.GetRepositoryId(), git.WorktreeName!)
            : null;

        var target = database.WithName(DatabaseNameFor(database.Name, git.Branch, defaultBranch, environment, settings, worktreeSuffix));
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

        if (seed == WorktreeSeed.Copy && TryCopyFromMainCheckout(database, target, contentRoot, git, defaultBranch, environment, settings, state))
            return;

        // no database yet, so let Umbraco install one and uSync's ImportOnFirstBoot fill it.
        // without a user to create, the unattended install would stop the site booting.
        if (HasUnattendedUser(configuration) is false)
        {
            state.Log(LogLevel.Warning, "uSync Git: {database} doesn't exist and there is no Unattended user configured, so Umbraco will show the installer", target.Name);
            return;
        }

        overrides["Umbraco:CMS:Unattended:InstallUnattended"] = "true";
        state.Log(LogLevel.Information, "uSync Git: {database} doesn't exist, it will be created by an unattended install", target.Name);
    }

    /// <summary>
    ///  Only development sites: a site deployed from a git clone shouldn't import on commit change,
    ///  and a production main checkout certainly shouldn't drop databases.
    /// </summary>
    internal static bool IsEnabled(IConfiguration configuration, out string reason)
    {
        var enabled = configuration.GetValue<bool?>($"{uSyncGitSettings.Section}:Enabled");
        if (enabled is not null)
        {
            reason = "Git:Enabled is false";
            return enabled.Value;
        }

        if (configuration.GetRuntimeMode() == RuntimeMode.Production)
        {
            reason = "Umbraco's runtime mode is Production (set Git:Enabled to override)";
            return false;
        }

        var debug = configuration.GetValue<bool>("Umbraco:CMS:Hosting:Debug");
        var development = string.Equals(configuration[HostDefaults.EnvironmentKey], Environments.Development, StringComparison.OrdinalIgnoreCase);

        reason = "the site isn't in debug or the Development environment (set Git:Enabled to override)";
        return debug || development;
    }

    private static bool HasUnattendedUser(IConfiguration configuration)
        => new[] { "UnattendedUserName", "UnattendedUserEmail", "UnattendedUserPassword" }
            .All(x => string.IsNullOrWhiteSpace(configuration[$"Umbraco:CMS:Unattended:{x}"]) is false);

    /// <summary>
    ///  Set a value only when the site's own configuration doesn't, so the site's choices always win.
    /// </summary>
    private static void AddDefault(IConfiguration configuration, Dictionary<string, string?> overrides, GitSiteState state, string key, string value)
    {
        if (configuration[key] is not null) return;
        overrides[key] = value;
        state.Log(LogLevel.Information, "uSync Git: {key} isn't set, using {value}", key, value);
    }

    /// <summary>
    ///  Point json settings files the site loads, but that the worktree doesn't have, at the main checkout's copy.
    /// </summary>
    /// <remarks>
    ///  Only files the site already registered are touched, in the same place in the list, so the
    ///  order (and so which value wins) is the same as it is in the main checkout.
    /// </remarks>
    private static void ShareLocalSettings(IConfigurationBuilder configurationBuilder, string contentRoot, string mainSiteRoot, GitSiteState state)
    {
        var sources = configurationBuilder.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is not JsonConfigurationSource json || string.IsNullOrWhiteSpace(json.Path)) continue;
            if (Path.IsPathRooted(json.Path)) continue; // e.g. user secrets, already shared.

            var root = (json.FileProvider as PhysicalFileProvider)?.Root ?? contentRoot;
            if (File.Exists(Path.Combine(root, json.Path))) continue;

            var mainFile = Path.Combine(mainSiteRoot, json.Path);
            if (File.Exists(mainFile) is false) continue;

            // replacing the item (rather than changing it) makes the configuration reload.
            sources[i] = new JsonConfigurationSource
            {
                Path = json.Path,
                Optional = json.Optional,
                ReloadOnChange = false,
                FileProvider = new PhysicalFileProvider(mainSiteRoot),
            };

            state.Log(LogLevel.Information, "uSync Git: reading {file} from the main checkout", mainFile);
        }
    }

    private static bool TryCopyFromMainCheckout(GitDatabase database, GitDatabase target, string contentRoot,
        GitContext git, string? defaultBranch, string environment, uSyncGitSettings settings, GitSiteState state)
    {
        var mainSiteRoot = git.MapToMainCheckout(contentRoot);
        if (mainSiteRoot is null)
        {
            state.Log(LogLevel.Warning, "uSync Git: can't find the main checkout to copy the database from");
            return false;
        }

        // the default branch's database first, then whatever the main checkout is on now.
        var mainBranch = GitContext.Discover(mainSiteRoot)?.Branch;
        var candidates = new[] { defaultBranch, mainBranch }
            .Select(x => DatabaseNameFor(database.Name, x, defaultBranch, environment, settings, null))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => database.WithName(x).WithDataDirectory(DataDirectory(mainSiteRoot)))
            .ToList();

        GitDatabase? source = null;
        try
        {
            source = candidates.FirstOrDefault(x => x.Exists());
            if (source is null)
            {
                state.Log(LogLevel.Warning, "uSync Git: the main checkout's database {database} doesn't exist, nothing to copy", candidates[0].Location);
                return false;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            target.CopyFrom(source);
            state.Log(LogLevel.Information, "uSync Git: copied {source} to {target} in {elapsed}ms", source.Location, target.Location, timer.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            state.Log(LogLevel.Error, "uSync Git: copying {source} to {target} failed: {error}", source?.Location, target.Location, ex.Message);
            try { target.Drop(); } catch { /* leave whatever is there, the install will tell us */ }
            return false;
        }
    }

    /// <summary>
    ///  The database name for a branch (and worktree). The configured name is never changed for the
    ///  default branch, and only the parts we add are cleaned up, never the site's own name.
    /// </summary>
    internal static string DatabaseNameFor(string name, string? branch, string? defaultBranch, string environment,
        uSyncGitSettings settings, string? worktreeSuffix)
    {
        if (settings.Db.NameByBranch && branch is not null
            && string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase) is false)
        {
            name = settings.Db.Template
                .Replace("{db}", name, StringComparison.OrdinalIgnoreCase)
                .Replace("{branch}", Sanitize(branch), StringComparison.OrdinalIgnoreCase)
                .Replace("{env}", Sanitize(environment), StringComparison.OrdinalIgnoreCase)
                .Replace("{ticks}", string.Empty, StringComparison.OrdinalIgnoreCase); // gave a new database every install.
        }

        return name + worktreeSuffix;
    }

    /// <summary>
    ///  The part of a database name that marks it as belonging to a worktree of this clone.
    /// </summary>
    internal static string WorktreeMarker(string repositoryId) => $"_wt_{repositoryId}_";

    /// <summary>
    ///  Added to a worktree's database name on a shared server: {db}_wt_{repository id}_{worktree}.
    /// </summary>
    internal static string WorktreeSuffix(string repositoryId, string worktree) => WorktreeMarker(repositoryId) + Sanitize(worktree);

    internal static string Sanitize(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var c in name)
            result.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_');
        return result.ToString().Trim('.');
    }

    internal static string DataDirectory(string siteRoot) => Path.Combine(siteRoot, "umbraco", "Data");
}
