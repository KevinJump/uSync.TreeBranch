namespace uSync.TreeBranch.Git;

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
    ///  Work out the repository's default branch: the branch origin's HEAD points at (set by git clone),
    ///  otherwise main or master if there is a local branch with that name.
    /// </summary>
    public string? FindDefaultBranch()
    {
        // symbolic refs are never packed, so this is always a file when it exists.
        var originHead = Path.Combine(CommonGitDirectory, "refs", "remotes", "origin", "HEAD");
        if (File.Exists(originHead))
        {
            const string prefix = "ref: refs/remotes/origin/";
            var head = File.ReadAllText(originHead).Trim();
            if (head.StartsWith(prefix, StringComparison.Ordinal))
                return head[prefix.Length..];
        }

        return new[] { "main", "master" }.FirstOrDefault(HasLocalBranch);
    }

    private bool HasLocalBranch(string branch)
    {
        if (File.Exists(Path.Combine(CommonGitDirectory, "refs", "heads", branch)))
            return true;

        var packedRefs = Path.Combine(CommonGitDirectory, "packed-refs");
        return File.Exists(packedRefs)
            && File.ReadLines(packedRefs).Any(x => x.EndsWith($" refs/heads/{branch}", StringComparison.Ordinal));
    }

    /// <summary>
    ///  The names of the worktrees git still knows about.
    /// </summary>
    /// <remarks>
    ///  A worktree counts until git removes its folder under .git/worktrees (git worktree remove, or prune),
    ///  not when its own folder can't be seen: a moved worktree, or one on a drive that isn't
    ///  connected, still has work in it.
    /// </remarks>
    public IReadOnlyCollection<string> GetWorktreeNames()
    {
        var worktreesFolder = Path.Combine(CommonGitDirectory, "worktrees");
        if (Directory.Exists(worktreesFolder) is false) return [];

        return Directory.GetDirectories(worktreesFolder).Select(x => Path.GetFileName(x)).ToList();
    }

    /// <summary>
    ///  A short id for this clone, shared by its main checkout and worktrees, so databases on a shared
    ///  server can be told apart from another clone's.
    /// </summary>
    /// <remarks>
    ///  Kept in a file in the common .git folder (git ignores files it doesn't know), rather than worked
    ///  out from the path, because the same folder can be reached by more than one path (junctions).
    /// </remarks>
    public string GetRepositoryId()
    {
        var idFile = Path.Combine(CommonGitDirectory, "usync-git-id");

        if (File.Exists(idFile) is false)
        {
            try
            {
                using var stream = new FileStream(idFile, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(Guid.NewGuid().ToString("N")[..8]);
            }
            catch (IOException) when (File.Exists(idFile))
            {
                // another site (a worktree starting at the same time) got there first.
            }
        }

        return File.ReadAllText(idFile).Trim();
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
