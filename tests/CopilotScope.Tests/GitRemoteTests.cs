using CopilotScope.Collector.Import;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Repository labels for imported sessions, read from the repository's own config file. The
/// scanner runs this unattended for every project in someone's history, so it runs no
/// <c>git</c>: none may be installed, and on a Mac without the developer tools running it opens
/// an installer dialog.
/// </summary>
public sealed class GitRemoteTests : IDisposable
{
    private readonly string _root = TempDirectory.Create();

    public void Dispose() => TempDirectory.Delete(_root);

    private string Repository(string name, string config)
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, name, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), config);
        return Path.Combine(_root, name);
    }

    private static string? Label(string directory) =>
        GitRemote.RepositoryFor(directory, new Dictionary<string, string?>(StringComparer.Ordinal));

    [Fact]
    public void TheOriginRemoteNamesTheRepositoryFromAnyDirectoryInside()
    {
        var repo = Repository("api", """
            [core]
            	bare = false
            [remote "upstream"]
            	url = git@github.com:someone-else/api.git
            [remote "origin"]
            	url = git@github.com:Acme/API.git
            	fetch = +refs/heads/*:refs/remotes/origin/*
            """);
        var deep = Directory.CreateDirectory(Path.Combine(repo, "src", "deep")).FullName;

        Assert.Equal("acme/api", Label(repo));
        Assert.Equal("acme/api", Label(deep));
    }

    [Fact]
    public void ALinkedWorktreeIsLabelledByTheRepositoryItBelongsTo()
    {
        var main = Repository("main", "[remote \"origin\"]\n\turl = https://github.com/acme/api.git\n");
        var gitDir = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "feature")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "feature")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");

        Assert.Equal("acme/api", Label(worktree));
    }

    [Theory]
    [InlineData("[remote.origin]\n  url = https://github.com/acme/api\n")]
    [InlineData("[remote \"origin\"] url = https://github.com/acme/api\n")]
    [InlineData("[Remote \"origin\"]\n  URL = \"https://github.com/acme/api.git\" ; the canonical one\n")]
    [InlineData("# a comment\n[remote \"origin\"]\n  url = https://github.com/acme/api.git # trailing\n")]
    public void TheSpellingsGitAcceptsAreRead(string config) =>
        Assert.Equal("acme/api", Label(Repository("spelled", config)));

    [Fact]
    public void NoOriginMeansNoLabel()
    {
        // "Origin" differs in case from "origin": git treats a quoted subsection as case-sensitive.
        var repo = Repository("forked", "[remote \"upstream\"]\n\turl = https://github.com/acme/api\n" +
                                        "[remote \"Origin\"]\n\turl = https://github.com/acme/other\n");
        Assert.Null(Label(repo));
    }

    [Fact]
    public void APathThatIsNotADirectoryHereGetsNoLabel()
    {
        // Resolved against this process's own directory, a path from another operating system
        // could land inside some unrelated repository.
        Assert.Null(Label("relative/path"));
        Assert.Null(Label(Path.Combine(_root, "deleted-since")));
    }

    [Fact]
    public void EachDirectoryIsLookedUpOnce()
    {
        var repo = Repository("cached", "[remote \"origin\"]\n\turl = https://github.com/acme/api\n");
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        Assert.Equal("acme/api", GitRemote.RepositoryFor(repo, cache));

        File.Delete(Path.Combine(repo, ".git", "config"));
        Assert.Equal("acme/api", GitRemote.RepositoryFor(repo, cache));
    }
}
