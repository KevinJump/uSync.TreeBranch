using System.Diagnostics;

using uSync.Branches.Git;

namespace uSync.Branches.Tests;

/// <summary>
///  Runs real git against a throwaway repository with a linked worktree.
/// </summary>
public sealed class GitContextTests : IDisposable
{
    private readonly string _root;
    private readonly string _main;
    private readonly string _worktree;

    public GitContextTests()
    {
        _root = Directory.CreateTempSubdirectory("usync-git-").FullName;
        _main = Path.Combine(_root, "main");
        _worktree = Path.Combine(_root, "wt-one");

        Directory.CreateDirectory(Path.Combine(_main, "site"));
        File.WriteAllText(Path.Combine(_main, "site", "readme.txt"), "hello");

        Git(_root, "init", "-b", "main", _main);
        Git(_main, "add", ".");
        Git(_main, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-m", "first");
        Git(_main, "worktree", "add", "-b", "feature/one", _worktree);
    }

    public void Dispose()
    {
        // git makes its object files read only, which stops Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void MainCheckout_IsFoundFromASubFolder()
    {
        var git = GitContext.Discover(Path.Combine(_main, "site"))!;

        Assert.False(git.IsLinkedWorktree);
        Assert.Equal("main", git.Branch);
        Assert.Equal(Path.GetFullPath(_main), git.WorkingDirectory);
    }

    [Fact]
    public void Worktree_KnowsItsNameBranchAndMainCheckout()
    {
        var git = GitContext.Discover(Path.Combine(_worktree, "site"))!;

        Assert.True(git.IsLinkedWorktree);
        Assert.Equal("wt-one", git.WorktreeName);
        Assert.Equal("feature/one", git.Branch);
        Assert.Equal(Path.GetFullPath(Path.Combine(_main, "site")), git.MapToMainCheckout(Path.Combine(_worktree, "site")));
        Assert.Equal("main", git.FindDefaultBranch());
    }

    [Fact]
    public void WorktreeNames_ComeFromGit_UntilItRemovesThem()
    {
        var main = GitContext.Discover(_main)!;
        Assert.Equal(["wt-one"], main.GetWorktreeNames());

        Git(_main, "worktree", "remove", _worktree);
        Assert.Empty(main.GetWorktreeNames());
    }

    [Fact]
    public void RepositoryId_IsSharedByTheMainCheckoutAndItsWorktrees()
    {
        var id = GitContext.Discover(_main)!.GetRepositoryId();

        Assert.Matches("^[0-9a-f]{8}$", id);
        Assert.Equal(id, GitContext.Discover(_worktree)!.GetRepositoryId());
        Assert.Equal(id, GitContext.Discover(_main)!.GetRepositoryId());
    }

    [Fact]
    public void NoRepository_IsNull()
    {
        var folder = Directory.CreateTempSubdirectory("usync-nogit-").FullName;
        try
        {
            // the temp folder could itself be inside a checkout on a dev machine, so only check
            // that whatever is found isn't claiming this folder as its root.
            var git = GitContext.Discover(folder);
            Assert.True(git is null || git.WorkingDirectory != folder);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Result}");
    }
}
