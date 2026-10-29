namespace uSync.GitEdition.Git;

/// <summary>
///  What we know about the git checkout a site is running from.
/// </summary>
/// <remarks>
///  This is read straight from the files in .git, so it can be used while the site
///  is still being configured, before Umbraco (or LibGit2Sharp) is loaded.
/// </remarks>
public class GitContext
{
    /// <summary>
    ///  Root folder of this checkout (the worktree folder when in a linked worktree).
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    ///  Root folder of the main checkout. Same as <see cref="WorkingDirectory"/> unless this is
    ///  a linked worktree, null when the main repo is bare.
    /// </summary>
    public string? MainWorkingDirectory { get; init; }

    /// <summary>
    ///  The .git folder shared by the main checkout and all its worktrees.
    /// </summary>
    public required string CommonGitDirectory { get; init; }

    /// <summary>
    ///  The name git uses for this worktree (the folder under .git/worktrees), null when this is the main checkout.
    /// </summary>
    public string? WorktreeName { get; init; }

    /// <summary>
    ///  The checked out branch, null when HEAD is detached.
    /// </summary>
    public string? Branch { get; init; }

    public bool IsLinkedWorktree => WorktreeName is not null;

    /// <summary>
    ///  Find the git checkout that contains the given path (walking up the folders).
    /// </summary>
    public static GitContext? Discover(string path)
    {
        var folder = new DirectoryInfo(path);
        while (folder is not null)
        {
            var dotGit = Path.Combine(folder.FullName, ".git");

            if (Directory.Exists(dotGit))
            {
                return new GitContext
                {
                    WorkingDirectory = folder.FullName,
                    MainWorkingDirectory = folder.FullName,
                    CommonGitDirectory = dotGit,
                    Branch = ReadBranch(dotGit)
                };
            }

            if (File.Exists(dotGit))
                return FromGitFile(folder.FullName, dotGit);

            folder = folder.Parent;
        }

        return null;
    }

    /// <summary>
    ///  Map a path in this checkout to the same path in the main checkout.
    /// </summary>
    public string? MapToMainCheckout(string path)
    {
        if (MainWorkingDirectory is null) return null;
        var relative = Path.GetRelativePath(WorkingDirectory, path);
        return Path.GetFullPath(Path.Combine(MainWorkingDirectory, relative));
    }

    /// <summary>
    ///  The names of the worktrees git knows about whose folders still exist.
    /// </summary>
    public IReadOnlyCollection<string> GetLiveWorktreeNames()
    {
        var worktreesFolder = Path.Combine(CommonGitDirectory, "worktrees");
        if (Directory.Exists(worktreesFolder) is false) return [];

        var names = new List<string>();
        foreach (var adminFolder in Directory.GetDirectories(worktreesFolder))
        {
            // the gitdir file points at the .git file inside the worktree folder.
            var gitdirFile = Path.Combine(adminFolder, "gitdir");
            if (File.Exists(gitdirFile) is false) continue;

            var worktreeGitFile = File.ReadAllText(gitdirFile).Trim();
            if (File.Exists(Path.GetFullPath(worktreeGitFile, adminFolder)))
                names.Add(Path.GetFileName(adminFolder));
        }

        return names;
    }

    private static GitContext? FromGitFile(string workingDirectory, string gitFile)
    {
        // a linked worktree (or a submodule) has a .git file pointing at its real git folder
        //   gitdir: C:/Source/MySite/.git/worktrees/my-feature
        var line = File.ReadLines(gitFile).FirstOrDefault(x => x.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;

        var gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), workingDirectory);

        // only worktrees have a commondir file, submodules are treated as a normal checkout.
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile) is false)
        {
            return new GitContext
            {
                WorkingDirectory = workingDirectory,
                MainWorkingDirectory = workingDirectory,
                CommonGitDirectory = gitDir,
                Branch = ReadBranch(gitDir)
            };
        }

        var commonDir = Path.GetFullPath(File.ReadAllText(commonDirFile).Trim(), gitDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return new GitContext
        {
            WorkingDirectory = workingDirectory,
            MainWorkingDirectory = Path.GetFileName(commonDir).Equals(".git", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(commonDir)
                : null,
            CommonGitDirectory = commonDir,
            WorktreeName = Path.GetFileName(gitDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Branch = ReadBranch(gitDir)
        };
    }

    private static string? ReadBranch(string gitDir)
    {
        var headFile = Path.Combine(gitDir, "HEAD");
        if (File.Exists(headFile) is false) return null;

        const string prefix = "ref: refs/heads/";
        var head = File.ReadAllText(headFile).Trim();
        return head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : null;
    }
}
