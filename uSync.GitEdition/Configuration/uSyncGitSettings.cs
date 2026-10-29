namespace uSync.GitEdition.Configuration;

/// <summary>
///  Settings for uSync Git Edition, read from the "Git" section of appsettings.
/// </summary>
public class uSyncGitSettings
{
    public const string Section = "Git";

    public GitDbSettings Db { get; set; } = new();

    public GitWorktreeSettings Worktree { get; set; } = new();

    public GitImportSettings Import { get; set; } = new();
}

public class GitDbSettings
{
    /// <summary>
    ///  Give each branch its own database, named with <see cref="Template"/>.
    /// </summary>
    public bool NameByBranch { get; set; } = false;

    /// <summary>
    ///  Database name when <see cref="NameByBranch"/> is on.
    ///  Placeholders: {db} (the configured database name), {branch}, {env}.
    /// </summary>
    public string Template { get; set; } = "{db}.{branch}";
}

public enum WorktreeSeed
{
    /// <summary>
    ///  Copy the main checkout's database into the worktree.
    /// </summary>
    Copy,

    /// <summary>
    ///  Start with an empty database: an unattended install, then uSync's ImportOnFirstBoot.
    /// </summary>
    Empty,

    /// <summary>
    ///  Leave the database alone.
    /// </summary>
    None
}

public class GitWorktreeSettings
{
    /// <summary>
    ///  Set the site up for the worktree when it runs from a linked git worktree.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///  How to create the worktree's database when it doesn't exist yet.
    /// </summary>
    public WorktreeSeed Seed { get; set; } = WorktreeSeed.Copy;

    /// <summary>
    ///  Serve media from the main checkout's wwwroot/media folder (it's gitignored, so worktrees don't have one).
    /// </summary>
    public bool ShareMedia { get; set; } = true;

    /// <summary>
    ///  Give each worktree its own https/http port pair, so sites can run side by side.
    /// </summary>
    public bool Ports { get; set; } = true;

    /// <summary>
    ///  Start of the range worktree ports are picked from.
    /// </summary>
    public int PortRangeStart { get; set; } = 45000;

    /// <summary>
    ///  When the main checkout starts, drop SQL Server databases left by worktrees that no longer exist.
    /// </summary>
    public bool RemoveOrphanDatabases { get; set; } = true;
}

public class GitImportSettings
{
    /// <summary>
    ///  Import at startup when the git commit has changed since the last uSync import into this database.
    /// </summary>
    public bool OnCommitChange { get; set; } = true;

    /// <summary>
    ///  The uSync handler group to import.
    /// </summary>
    public string Group { get; set; } = "All";

    /// <summary>
    ///  Don't import while the uSync folder has uncommitted changes. They are usually
    ///  exports from this site, so importing them again does nothing useful.
    /// </summary>
    public bool SkipWhenDirty { get; set; } = true;
}
