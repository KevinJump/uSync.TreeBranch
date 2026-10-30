using uSync.TreeBranch.Worktrees;

namespace uSync.TreeBranch.Tests;

public class GitDatabaseTests
{
    [Fact]
    public void Sqlite_IsFoundFromTheConnectionString()
    {
        var database = GitDatabase.Create("Data Source=|DataDirectory|/Umbraco.sqlite.db;Cache=Shared", null, "/site/umbraco/Data");

        var sqlite = Assert.IsType<SqliteGitDatabase>(database);
        Assert.Equal("Umbraco", sqlite.Name);
        Assert.False(sqlite.IsSharedBetweenCheckouts);
        Assert.Equal(Path.GetFullPath("/site/umbraco/Data/Umbraco.sqlite.db"), sqlite.PhysicalPath);
    }

    [Fact]
    public void Sqlite_Rename_KeepsTheFolderAndExtension()
    {
        var database = GitDatabase.Create("Data Source=|DataDirectory|/Umbraco.sqlite.db", GitDatabase.SqliteProvider, "/site/umbraco/Data")!;

        var renamed = (SqliteGitDatabase)database.WithName("Umbraco.feature_x");
        Assert.Equal("Umbraco.feature_x", renamed.Name);
        Assert.Equal(Path.GetFullPath("/site/umbraco/Data/Umbraco.feature_x.sqlite.db"), renamed.PhysicalPath);
    }

    [Fact]
    public void SqliteOutsideTheSite_IsShared()
        => Assert.True(GitDatabase.Create("Data Source=/data/Umbraco.sqlite.db", null, "/site/umbraco/Data")!.IsSharedBetweenCheckouts);

    [Fact]
    public void SqlServer_UsesTheCatalog()
    {
        var database = GitDatabase.Create("Server=.;Database=Umbraco;Integrated Security=true", GitDatabase.SqlServerProvider, "/site/umbraco/Data");

        Assert.IsType<SqlServerGitDatabase>(database);
        Assert.Equal("Umbraco", database.Name);
        Assert.True(database.IsSharedBetweenCheckouts);
        Assert.Contains("Umbraco_wt_x", database.WithName("Umbraco_wt_x").ConnectionString);
    }

    [Theory]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;AttachDbFilename=|DataDirectory|\\Umbraco.mdf;Integrated Security=true")]
    [InlineData("Server=.;Integrated Security=true")]
    [InlineData("")]
    public void DatabasesWeCantCopy_AreLeftAlone(string connectionString)
        => Assert.Null(GitDatabase.Create(connectionString, null, "/site/umbraco/Data"));

    [Fact]
    public void Sqlite_CopyFrom_CopiesTheData()
    {
        var folder = Directory.CreateTempSubdirectory("usync-git-db-").FullName;
        try
        {
            var source = (SqliteGitDatabase)GitDatabase.Create("Data Source=|DataDirectory|/source.sqlite.db", null, folder)!;
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={source.PhysicalPath};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('copied');";
                command.ExecuteNonQuery();
            }

            var target = (SqliteGitDatabase)source.WithName("target");
            Assert.False(target.Exists());

            target.CopyFrom(source);

            Assert.True(target.Exists());
            using (var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target.PhysicalPath};Pooling=false"))
            {
                check.Open();
                using var read = check.CreateCommand();
                read.CommandText = "SELECT v FROM t";
                Assert.Equal("copied", read.ExecuteScalar());
            }

            target.Drop();
            Assert.False(target.Exists());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
