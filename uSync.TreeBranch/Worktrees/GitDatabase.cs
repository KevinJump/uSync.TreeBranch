using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace uSync.TreeBranch.Worktrees;

/// <summary>
///  The site database, with just enough knowledge of the provider to rename, copy and drop it.
/// </summary>
internal abstract class GitDatabase
{
    public const string SqliteProvider = "Microsoft.Data.Sqlite";
    public const string SqlServerProvider = "Microsoft.Data.SqlClient";

    /// <summary>
    ///  Umbraco's load balancing tables. A copy brings the main site's server registration with it, and
    ///  that site is running, so the copy would start as a Subscriber. Clearing them makes it a single server.
    /// </summary>
    protected static readonly string[] ServerTables = ["umbracoServer", "umbracoLastSynced", "umbracoCacheInstruction"];

    public abstract string ProviderName { get; }

    /// <summary>
    ///  The database name (the file name without .sqlite.db for SQLite, the catalog for SQL Server).
    /// </summary>
    public abstract string Name { get; }

    public abstract string ConnectionString { get; }

    /// <summary>
    ///  SQLite files live in the site's own umbraco/Data folder, so each worktree is already
    ///  isolated. SQL Server databases are shared by every checkout that points at the server.
    /// </summary>
    public abstract bool IsSharedBetweenCheckouts { get; }

    public abstract GitDatabase WithName(string name);

    /// <summary>
    ///  Point at the same database as seen from another site folder (SQLite only, SQL Server ignores it).
    /// </summary>
    public abstract GitDatabase WithDataDirectory(string dataDirectory);

    public abstract bool Exists();

    public abstract void CopyFrom(GitDatabase source);

    public abstract void Drop();

    public static GitDatabase? Create(string? connectionString, string? providerName, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        providerName ??= connectionString.Contains(".sqlite", StringComparison.OrdinalIgnoreCase)
            ? SqliteProvider : SqlServerProvider;

        if (providerName.Equals(SqliteProvider, StringComparison.OrdinalIgnoreCase))
            return new SqliteGitDatabase(connectionString, dataDirectory);

        if (providerName.Equals(SqlServerProvider, StringComparison.OrdinalIgnoreCase))
        {
            var builder = new SqlConnectionStringBuilder(connectionString);

            // LocalDB attached files sit in the site folder, so they are already per checkout,
            // but we can't copy them while they are attached.
            if (string.IsNullOrWhiteSpace(builder.AttachDBFilename) is false) return null;
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog)) return null;

            return new SqlServerGitDatabase(builder);
        }

        return null;
    }
}

internal class SqliteGitDatabase : GitDatabase
{
    private const string DataDirectoryPlaceholder = "|DataDirectory|";
    private const string Extension = ".sqlite.db";

    private readonly SqliteConnectionStringBuilder _builder;
    private readonly string _dataDirectory;

    public SqliteGitDatabase(string connectionString, string dataDirectory)
    {
        _builder = new SqliteConnectionStringBuilder(connectionString);
        _dataDirectory = dataDirectory;
    }

    public override string ProviderName => SqliteProvider;

    public override string ConnectionString => _builder.ConnectionString;

    public override bool IsSharedBetweenCheckouts => _builder.DataSource.Contains(DataDirectoryPlaceholder) is false;

    public override string Name
    {
        get
        {
            var fileName = Path.GetFileName(_builder.DataSource);
            return fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^Extension.Length]
                : Path.GetFileNameWithoutExtension(fileName);
        }
    }

    public string PhysicalPath => Path.GetFullPath(_builder.DataSource.Replace(DataDirectoryPlaceholder, _dataDirectory));

    public override GitDatabase WithName(string name)
    {
        var dataSource = _builder.DataSource;
        var fileName = Path.GetFileName(dataSource);
        var extension = fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? Extension : Path.GetExtension(fileName);

        var builder = new SqliteConnectionStringBuilder(_builder.ConnectionString)
        {
            DataSource = dataSource[..^fileName.Length] + name + extension
        };

        return new SqliteGitDatabase(builder.ConnectionString, _dataDirectory);
    }

    public override GitDatabase WithDataDirectory(string dataDirectory)
        => new SqliteGitDatabase(_builder.ConnectionString, dataDirectory);

    public override bool Exists()
    {
        var file = new FileInfo(PhysicalPath);
        return file.Exists && file.Length > 0;
    }

    public override void CopyFrom(GitDatabase source)
    {
        if (source is not SqliteGitDatabase sqliteSource)
            throw new NotSupportedException($"Can't copy a {source.ProviderName} database to SQLite");

        Directory.CreateDirectory(Path.GetDirectoryName(PhysicalPath)!);

        // the backup API gives a consistent copy, even when the main site is running and has a WAL file.
        using var from = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sqliteSource.PhysicalPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = PhysicalPath,
            Pooling = false,
        }.ConnectionString);
        from.Open();
        to.Open();
        from.BackupDatabase(to);

        foreach (var table in ServerTables)
        {
            using var command = to.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
            command.Parameters.AddWithValue("@table", table);
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) continue;

            command.CommandText = $"DELETE FROM \"{table}\"";
            command.Parameters.Clear();
            command.ExecuteNonQuery();
        }
    }

    public override void Drop()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = PhysicalPath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }
}

internal class SqlServerGitDatabase : GitDatabase
{
    private readonly SqlConnectionStringBuilder _builder;

    public SqlServerGitDatabase(SqlConnectionStringBuilder builder)
    {
        _builder = builder;
    }

    public override string ProviderName => SqlServerProvider;

    public override string ConnectionString => _builder.ConnectionString;

    public override string Name => _builder.InitialCatalog;

    public override bool IsSharedBetweenCheckouts => true;

    public override GitDatabase WithName(string name)
        => new SqlServerGitDatabase(new SqlConnectionStringBuilder(_builder.ConnectionString) { InitialCatalog = name });

    public override GitDatabase WithDataDirectory(string dataDirectory) => this;

    public override bool Exists()
    {
        using var connection = OpenMaster();
        using var command = new SqlCommand("SELECT DB_ID(@name)", connection);
        command.Parameters.AddWithValue("@name", Name);
        return command.ExecuteScalar() is not (null or DBNull);
    }

    /// <summary>
    ///  Copy with BACKUP ... COPY_ONLY and RESTORE ... WITH MOVE, so the source database's
    ///  backup chain isn't touched. The files go in the server's default data and log folders.
    /// </summary>
    public override void CopyFrom(GitDatabase source)
    {
        if (source is not SqlServerGitDatabase)
            throw new NotSupportedException($"Can't copy a {source.ProviderName} database to SQL Server");

        using var connection = OpenMaster();

        var dataPath = ServerProperty(connection, "InstanceDefaultDataPath");
        var logPath = ServerProperty(connection, "InstanceDefaultLogPath") ?? dataPath;
        if (dataPath is null)
            throw new InvalidOperationException("Can't find the SQL Server default data path");

        var backupFile = Path.Combine(dataPath, $"{Name}.seed.bak");

        Execute(connection, $"BACKUP DATABASE {Quote(source.Name)} TO DISK = @file WITH COPY_ONLY, INIT, FORMAT", ("@file", backupFile));

        var moves = new List<string>();
        var parameters = new List<(string, object)> { ("@file", backupFile) };
        using (var fileList = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @file", connection))
        {
            fileList.Parameters.AddWithValue("@file", backupFile);
            using var reader = fileList.ExecuteReader();
            int index = 0, dataFiles = 0, logFiles = 0;
            while (reader.Read())
            {
                var logicalName = reader.GetString(reader.GetOrdinal("LogicalName"));
                var isLog = reader.GetString(reader.GetOrdinal("Type")) == "L";
                var target = isLog
                    ? Path.Combine(logPath!, $"{Name}_log{Suffix(logFiles++)}.ldf")
                    : Path.Combine(dataPath, $"{Name}{Suffix(dataFiles++)}.mdf");

                moves.Add($"MOVE @logical{index} TO @physical{index}");
                parameters.Add(($"@logical{index}", logicalName));
                parameters.Add(($"@physical{index}", target));
                index++;
            }
        }

        Execute(connection,
            $"RESTORE DATABASE {Quote(Name)} FROM DISK = @file WITH {string.Join(", ", moves)}, REPLACE, RECOVERY",
            [.. parameters]);

        // the backup file is on the server, we can only tidy it up when the server is this machine.
        try { if (File.Exists(backupFile)) File.Delete(backupFile); } catch (IOException) { }

        foreach (var table in ServerTables)
            Execute(connection, $"IF OBJECT_ID(@table) IS NOT NULL DELETE FROM {Quote(Name)}.dbo.{Quote(table)}", ("@table", $"{Quote(Name)}.dbo.{Quote(table)}"));
    }

    public override void Drop()
    {
        using var connection = OpenMaster();
        Execute(connection, $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {Quote(Name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Quote(Name)}; END", ("@name", Name));
    }

    /// <summary>
    ///  Names of the databases on the server that contain the text.
    /// </summary>
    public IReadOnlyCollection<string> FindDatabasesContaining(string text)
    {
        using var connection = OpenMaster();
        using var command = new SqlCommand(@"SELECT name FROM sys.databases WHERE name LIKE @pattern ESCAPE '\'", connection);
        command.Parameters.AddWithValue("@pattern", "%" + EscapeLike(text) + "%");

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private SqlConnection OpenMaster()
    {
        var builder = new SqlConnectionStringBuilder(_builder.ConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };

        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string? ServerProperty(SqlConnection connection, string property)
    {
        using var command = new SqlCommand($"SELECT CONVERT(nvarchar(512), SERVERPROPERTY('{property}'))", connection);
        return command.ExecuteScalar() as string;
    }

    private static void Execute(SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    // _, % and [ are all wildcards in a SQL Server LIKE.
    private static string EscapeLike(string text)
        => text.Replace(@"\", @"\\").Replace("_", @"\_").Replace("%", @"\%").Replace("[", @"\[");

    private static string Quote(string name) => $"[{name.Replace("]", "]]")}]";

    private static string Suffix(int index) => index == 0 ? string.Empty : $"_{index}";
}
