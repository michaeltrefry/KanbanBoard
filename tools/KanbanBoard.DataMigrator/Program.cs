using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MySqlConnector;

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    try
    {
        var options = MigrationOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.Command is MigrationCommand.ExportJson)
        {
            var data = await ReadSqliteAsync(options.RequiredSqlitePath());
            await WriteJsonAsync(options.RequiredOutputPath(), data);
            Console.WriteLine($"Exported {data.TotalRows} rows to {options.RequiredOutputPath()}.");
            return 0;
        }

        if (options.Command is MigrationCommand.ImportJson)
        {
            var data = await ReadJsonAsync(options.RequiredInputPath());
            await ImportMariaDbAsync(data, options);
            return 0;
        }

        if (options.Command is MigrationCommand.Copy)
        {
            var sqlitePath = options.RequiredSqlitePath();
            var data = await ReadSqliteAsync(sqlitePath);

            if (!options.SkipBackup)
            {
                var backupPath = options.BackupPath ?? BuildDefaultBackupPath(sqlitePath);
                await BackupSqliteAsync(sqlitePath, backupPath);
                Console.WriteLine($"Backed up SQLite database to {backupPath}.");
            }

            if (options.OutputPath is { } outputPath)
            {
                await WriteJsonAsync(outputPath, data);
                Console.WriteLine($"Wrote migration snapshot to {outputPath}.");
            }

            await ImportMariaDbAsync(data, options);
            return 0;
        }

        PrintUsage();
        return 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<KanbanDataSet> ReadSqliteAsync(string sqlitePath)
{
    if (!File.Exists(sqlitePath))
    {
        throw new FileNotFoundException($"SQLite database was not found: {sqlitePath}", sqlitePath);
    }

    await using var connection = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly;Cache=Shared");
    await connection.OpenAsync();

    var dataSet = new KanbanDataSet();
    foreach (var table in KanbanTables.All)
    {
        var tableData = new TableData(table.Name);
        if (!await SqliteTableExistsAsync(connection, table.Name))
        {
            Console.WriteLine($"SQLite table {table.Name} does not exist; treating it as empty.");
            dataSet.Tables.Add(tableData);
            continue;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(column => QuoteSqlite(column.Name)))} FROM {QuoteSqlite(table.Name)}";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                row[column.Name] = ReadJsonValue(reader, index, column.Kind);
            }

            tableData.Rows.Add(row);
        }

        dataSet.Tables.Add(tableData);
        Console.WriteLine($"Read {tableData.Rows.Count} rows from SQLite table {table.Name}.");
    }

    return dataSet;
}

static JsonElement? ReadJsonValue(SqliteDataReader reader, int index, ColumnKind kind)
{
    if (reader.IsDBNull(index))
    {
        return null;
    }

    object value = kind switch
    {
        ColumnKind.Int => reader.GetInt32(index),
        ColumnKind.Bool => reader.GetInt64(index) != 0,
        _ => reader.GetString(index)
    };

    return JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
}

static async Task ImportMariaDbAsync(KanbanDataSet data, MigrationOptions options)
{
    await using var tunnel = await SshTunnel.OpenAsync(options);
    var connectionString = tunnel.ApplyTo(options.RequiredMariaDbConnectionString());

    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    await EnsureMariaDbTablesExistAsync(connection);

    if (options.ReplaceTarget)
    {
        Console.WriteLine("Replacing target table contents.");
        await ClearTargetTablesAsync(connection);
    }
    else
    {
        await EnsureTargetTablesAreEmptyAsync(connection);
    }

    if (options.DryRun)
    {
        Console.WriteLine("Dry run completed. No MariaDB rows were inserted.");
        return;
    }

    await using var transaction = await connection.BeginTransactionAsync();
    try
    {
        foreach (var table in KanbanTables.All)
        {
            var tableData = data.GetTable(table.Name);
            foreach (var row in tableData.Rows)
            {
                await InsertMariaDbRowAsync(connection, transaction, table, row);
            }

            Console.WriteLine($"Inserted {tableData.Rows.Count} rows into MariaDB table {table.Name}.");
        }

        await transaction.CommitAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }

    await ValidateMariaDbAsync(connection, data);
}

static async Task InsertMariaDbRowAsync(
    MySqlConnection connection,
    MySqlTransaction transaction,
    TableSpec table,
    IReadOnlyDictionary<string, JsonElement?> row)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;

    var columnNames = string.Join(", ", table.Columns.Select(column => QuoteMySql(column.Name)));
    var parameterNames = string.Join(", ", table.Columns.Select((_, index) => $"@p{index}"));
    command.CommandText = $"INSERT INTO {QuoteMySql(table.Name)} ({columnNames}) VALUES ({parameterNames});";

    for (var index = 0; index < table.Columns.Count; index++)
    {
        var column = table.Columns[index];
        row.TryGetValue(column.Name, out var value);
        command.Parameters.AddWithValue($"@p{index}", ConvertForMariaDb(value, column.Kind));
    }

    await command.ExecuteNonQueryAsync();
}

static object ConvertForMariaDb(JsonElement? value, ColumnKind kind)
{
    if (value is null || value.Value.ValueKind is JsonValueKind.Null)
    {
        return DBNull.Value;
    }

    var element = value.Value;
    return kind switch
    {
        ColumnKind.Int => element.GetInt32(),
        ColumnKind.Bool => element.GetBoolean(),
        ColumnKind.DateTimeOffset => ParseDateTimeOffset(element.GetString()),
        _ => element.GetString() ?? string.Empty
    };
}

static DateTime ParseDateTimeOffset(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException("A required DateTimeOffset value was empty.");
    }

    return DateTimeOffset
        .Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
        .UtcDateTime;
}

static async Task BackupSqliteAsync(string sqlitePath, string backupPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
    if (File.Exists(backupPath))
    {
        throw new IOException($"Backup file already exists: {backupPath}");
    }

    await using var source = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly;Cache=Shared");
    await using var destination = new SqliteConnection($"Data Source={backupPath}");
    await source.OpenAsync();
    await destination.OpenAsync();
    source.BackupDatabase(destination);
}

static async Task<bool> SqliteTableExistsAsync(SqliteConnection connection, string tableName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
    command.Parameters.AddWithValue("@name", tableName);

    var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    return count != 0;
}

static async Task EnsureMariaDbTablesExistAsync(MySqlConnection connection)
{
    foreach (var table in KanbanTables.All)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = @tableName;
            """;
        command.Parameters.AddWithValue("@tableName", table.Name);

        var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        if (count == 0)
        {
            throw new InvalidOperationException(
                $"MariaDB table {table.Name} does not exist. Run the MariaDB schema migration before importing data.");
        }
    }
}

static async Task EnsureTargetTablesAreEmptyAsync(MySqlConnection connection)
{
    var nonEmptyTables = new List<string>();
    foreach (var table in KanbanTables.All)
    {
        var count = await CountMariaDbRowsAsync(connection, table.Name);
        if (count > 0)
        {
            nonEmptyTables.Add($"{table.Name} ({count})");
        }
    }

    if (nonEmptyTables.Count > 0)
    {
        throw new InvalidOperationException(
            "MariaDB target is not empty. Re-run with --replace-target to delete existing rows first. Non-empty tables: " +
            string.Join(", ", nonEmptyTables));
    }
}

static async Task ClearTargetTablesAsync(MySqlConnection connection)
{
    await ExecuteMariaDbAsync(connection, "SET FOREIGN_KEY_CHECKS = 0;");
    try
    {
        foreach (var table in KanbanTables.All.Reverse())
        {
            await ExecuteMariaDbAsync(connection, $"DELETE FROM {QuoteMySql(table.Name)};");
        }
    }
    finally
    {
        await ExecuteMariaDbAsync(connection, "SET FOREIGN_KEY_CHECKS = 1;");
    }
}

static async Task ValidateMariaDbAsync(MySqlConnection connection, KanbanDataSet expected)
{
    Console.WriteLine("Validation:");
    foreach (var table in KanbanTables.All)
    {
        var expectedCount = expected.GetTable(table.Name).Rows.Count;
        var actualCount = await CountMariaDbRowsAsync(connection, table.Name);
        var marker = expectedCount == actualCount ? "ok" : "mismatch";
        Console.WriteLine($"  {marker}: {table.Name}: expected {expectedCount}, found {actualCount}");

        if (expectedCount != actualCount)
        {
            throw new InvalidOperationException($"MariaDB row count mismatch for table {table.Name}.");
        }
    }

    await ValidateNoOrphansAsync(connection, "Epics", "ProjectId", "Projects", "Id");
    await ValidateNoOrphansAsync(connection, "EpicDocuments", "EpicId", "Epics", "Id");
    await ValidateNoOrphansAsync(connection, "WorkItems", "ProjectId", "Projects", "Id");
    await ValidateNoOrphansAsync(connection, "WorkItems", "EpicId", "Epics", "Id", nullable: true);
    await ValidateNoOrphansAsync(connection, "WorkItemComments", "WorkItemId", "WorkItems", "Id");
    await ValidateNoOrphansAsync(connection, "PersonalAccessTokens", "AppUserId", "AppUsers", "Id");
    await PrintRepresentativeBoardReadAsync(connection);
}

static async Task ValidateNoOrphansAsync(
    MySqlConnection connection,
    string childTable,
    string childColumn,
    string parentTable,
    string parentColumn,
    bool nullable = false)
{
    await using var command = connection.CreateCommand();
    var nullableFilter = nullable ? $"AND child.{QuoteMySql(childColumn)} IS NOT NULL" : string.Empty;
    command.CommandText = $"""
        SELECT COUNT(*)
        FROM {QuoteMySql(childTable)} child
        LEFT JOIN {QuoteMySql(parentTable)} parent
            ON child.{QuoteMySql(childColumn)} = parent.{QuoteMySql(parentColumn)}
        WHERE parent.{QuoteMySql(parentColumn)} IS NULL {nullableFilter};
        """;

    var orphanCount = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    if (orphanCount != 0)
    {
        throw new InvalidOperationException(
            $"Found {orphanCount} orphan rows from {childTable}.{childColumn} to {parentTable}.{parentColumn}.");
    }
}

static async Task PrintRepresentativeBoardReadAsync(MySqlConnection connection)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT p.Id, p.Name, COUNT(w.Id) AS WorkItemCount
        FROM Projects p
        LEFT JOIN WorkItems w ON w.ProjectId = p.Id
        GROUP BY p.Id, p.Name
        ORDER BY p.CreatedAtUtc
        LIMIT 1;
        """;

    await using var reader = await command.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        Console.WriteLine(
            $"  ok: representative board read: {reader.GetString(1)} ({reader.GetString(0)}) has {Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)} work items");
    }
    else
    {
        Console.WriteLine("  ok: representative board read skipped because there are no projects");
    }
}

static async Task<int> CountMariaDbRowsAsync(MySqlConnection connection, string tableName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM {QuoteMySql(tableName)};";
    return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

static async Task ExecuteMariaDbAsync(MySqlConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

static async Task WriteJsonAsync(string outputPath, KanbanDataSet data)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    await using var stream = File.Create(outputPath);
    await JsonSerializer.SerializeAsync(stream, data, JsonDefaults.Options);
}

static async Task<KanbanDataSet> ReadJsonAsync(string inputPath)
{
    await using var stream = File.OpenRead(inputPath);
    return await JsonSerializer.DeserializeAsync<KanbanDataSet>(stream, JsonDefaults.Options)
        ?? throw new InvalidOperationException($"Could not read migration snapshot: {inputPath}");
}

static string BuildDefaultBackupPath(string sqlitePath)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(sqlitePath)) ?? ".";
    var fileName = Path.GetFileNameWithoutExtension(sqlitePath);
    var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    return Path.Combine(directory, $"{fileName}.{timestamp}.backup.db");
}

static string QuoteSqlite(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

static string QuoteMySql(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

static void PrintUsage()
{
    Console.WriteLine("""
        KanbanBoard.DataMigrator

        Commands:
          copy        Back up SQLite, read data, and import it into MariaDB.
          export-json Export SQLite data to a JSON migration snapshot.
          import-json Import a JSON migration snapshot into MariaDB.

        Examples:
          dotnet run --project tools/KanbanBoard.DataMigrator -- copy \
            --sqlite ./KanbanBoard.Api/App_Data/kanban.db \
            --mariadb "Server=localhost;Database=kanban;User ID=kanban;Password=secret;" \
            --replace-target

          dotnet run --project tools/KanbanBoard.DataMigrator -- export-json \
            --sqlite ./KanbanBoard.Api/App_Data/kanban.db \
            --output ./kanban-migration.json

          dotnet run --project tools/KanbanBoard.DataMigrator -- import-json \
            --input ./kanban-migration.json \
            --mariadb "Server=localhost;Database=kanban;User ID=kanban;Password=secret;"

        Options:
          --sqlite PATH       Source SQLite kanban.db path.
          --mariadb STRING    Target MariaDB connection string.
          --ssh-host HOST     Open a temporary SSH tunnel through this host.
          --ssh-user USER     SSH user for --ssh-host.
          --ssh-port PORT     SSH port. Defaults to 22.
          --ssh-key PATH      Optional SSH private key path. Otherwise ssh-agent/default keys are used.
          --ssh-remote-host   MariaDB host from the SSH server. Defaults to 127.0.0.1.
          --ssh-remote-port   MariaDB port from the SSH server. Defaults to 3306.
          --ssh-local-port    Local tunnel port. Defaults to a free ephemeral port.
          --backup PATH       SQLite backup path for copy.
          --skip-backup       Do not create a SQLite backup before copy.
          --output PATH       JSON snapshot path for export-json or optional copy snapshot.
          --input PATH        JSON snapshot path for import-json.
          --replace-target    Delete target MariaDB rows before import.
          --dry-run           Validate sources/target, but do not insert rows.
          --help              Show this help.
        """);
}

public sealed record KanbanDataSet
{
    public List<TableData> Tables { get; init; } = [];

    public int TotalRows => Tables.Sum(table => table.Rows.Count);

    public TableData GetTable(string name) =>
        Tables.FirstOrDefault(table => string.Equals(table.Name, name, StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"Migration snapshot does not contain table {name}.");
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

public sealed record TableData(string Name)
{
    public List<Dictionary<string, JsonElement?>> Rows { get; init; } = [];
}

public sealed record TableSpec(string Name, IReadOnlyList<ColumnSpec> Columns);

public sealed record ColumnSpec(string Name, ColumnKind Kind);

public enum ColumnKind
{
    Text,
    Int,
    Bool,
    DateTimeOffset
}

public enum MigrationCommand
{
    None,
    Copy,
    ExportJson,
    ImportJson
}

public sealed record MigrationOptions
{
    public MigrationCommand Command { get; init; }
    public string? SqlitePath { get; init; }
    public string? MariaDbConnectionString { get; init; }
    public string? BackupPath { get; init; }
    public string? OutputPath { get; init; }
    public string? InputPath { get; init; }
    public string? SshHost { get; init; }
    public string? SshUser { get; init; }
    public string? SshKeyPath { get; init; }
    public int SshPort { get; init; } = 22;
    public string SshRemoteHost { get; init; } = "127.0.0.1";
    public int SshRemotePort { get; init; } = 3306;
    public int? SshLocalPort { get; init; }
    public bool SkipBackup { get; init; }
    public bool ReplaceTarget { get; init; }
    public bool DryRun { get; init; }
    public bool ShowHelp { get; init; }

    public static MigrationOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            return new MigrationOptions { ShowHelp = true };
        }

        var command = args[0] switch
        {
            "copy" => MigrationCommand.Copy,
            "export-json" => MigrationCommand.ExportJson,
            "import-json" => MigrationCommand.ImportJson,
            _ => MigrationCommand.None
        };

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {arg}");
            }

            if (arg is "--skip-backup" or "--replace-target" or "--dry-run")
            {
                flags.Add(arg);
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {arg}.");
            }

            values[arg] = args[++index];
        }

        return new MigrationOptions
        {
            Command = command,
            SqlitePath = values.GetValueOrDefault("--sqlite"),
            MariaDbConnectionString = values.GetValueOrDefault("--mariadb"),
            BackupPath = values.GetValueOrDefault("--backup"),
            OutputPath = values.GetValueOrDefault("--output"),
            InputPath = values.GetValueOrDefault("--input"),
            SshHost = values.GetValueOrDefault("--ssh-host"),
            SshUser = values.GetValueOrDefault("--ssh-user"),
            SshKeyPath = values.GetValueOrDefault("--ssh-key"),
            SshPort = ParseInt(values.GetValueOrDefault("--ssh-port"), 22, "--ssh-port"),
            SshRemoteHost = values.GetValueOrDefault("--ssh-remote-host") ?? "127.0.0.1",
            SshRemotePort = ParseInt(values.GetValueOrDefault("--ssh-remote-port"), 3306, "--ssh-remote-port"),
            SshLocalPort = values.TryGetValue("--ssh-local-port", out var localPort)
                ? ParseInt(localPort, 0, "--ssh-local-port")
                : null,
            SkipBackup = flags.Contains("--skip-backup"),
            ReplaceTarget = flags.Contains("--replace-target"),
            DryRun = flags.Contains("--dry-run")
        };
    }

    public string RequiredSqlitePath() =>
        !string.IsNullOrWhiteSpace(SqlitePath)
            ? SqlitePath
            : throw new ArgumentException("--sqlite is required.");

    public string RequiredMariaDbConnectionString() =>
        !string.IsNullOrWhiteSpace(MariaDbConnectionString)
            ? MariaDbConnectionString
            : throw new ArgumentException("--mariadb is required.");

    public string RequiredOutputPath() =>
        !string.IsNullOrWhiteSpace(OutputPath)
            ? OutputPath
            : throw new ArgumentException("--output is required.");

    public string RequiredInputPath() =>
        !string.IsNullOrWhiteSpace(InputPath)
            ? InputPath
            : throw new ArgumentException("--input is required.");

    private static int ParseInt(string? value, int defaultValue, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{optionName} must be a non-negative integer.");
    }
}

public sealed class SshTunnel : IAsyncDisposable
{
    private readonly Process? process;
    private readonly Task<string>? stderrTask;

    private SshTunnel(Process? process, Task<string>? stderrTask, int? localPort)
    {
        this.process = process;
        this.stderrTask = stderrTask;
        LocalPort = localPort;
    }

    public int? LocalPort { get; }

    public static async Task<SshTunnel> OpenAsync(MigrationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SshHost))
        {
            return new SshTunnel(null, null, null);
        }

        if (string.IsNullOrWhiteSpace(options.SshUser))
        {
            throw new ArgumentException("--ssh-user is required when --ssh-host is provided.");
        }

        var localPort = options.SshLocalPort is > 0
            ? options.SshLocalPort.Value
            : GetFreeLocalPort();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        processStartInfo.ArgumentList.Add("-N");
        processStartInfo.ArgumentList.Add("-L");
        processStartInfo.ArgumentList.Add($"{localPort}:{options.SshRemoteHost}:{options.SshRemotePort}");
        processStartInfo.ArgumentList.Add("-p");
        processStartInfo.ArgumentList.Add(options.SshPort.ToString(CultureInfo.InvariantCulture));
        processStartInfo.ArgumentList.Add("-o");
        processStartInfo.ArgumentList.Add("BatchMode=yes");
        processStartInfo.ArgumentList.Add("-o");
        processStartInfo.ArgumentList.Add("ExitOnForwardFailure=yes");
        processStartInfo.ArgumentList.Add("-o");
        processStartInfo.ArgumentList.Add("ServerAliveInterval=30");

        if (!string.IsNullOrWhiteSpace(options.SshKeyPath))
        {
            processStartInfo.ArgumentList.Add("-i");
            processStartInfo.ArgumentList.Add(options.SshKeyPath);
        }

        processStartInfo.ArgumentList.Add($"{options.SshUser}@{options.SshHost}");

        var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Failed to start ssh.");
        var stderrTask = process.StandardError.ReadToEndAsync();

        var tunnel = new SshTunnel(process, stderrTask, localPort);
        try
        {
            await WaitForTunnelAsync(process, stderrTask, localPort);
            Console.WriteLine(
                $"Opened SSH tunnel 127.0.0.1:{localPort} -> {options.SshRemoteHost}:{options.SshRemotePort} via {options.SshUser}@{options.SshHost}:{options.SshPort}.");
            return tunnel;
        }
        catch
        {
            await tunnel.DisposeAsync();
            throw;
        }
    }

    public string ApplyTo(string connectionString)
    {
        if (LocalPort is null)
        {
            return connectionString;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Server = "127.0.0.1",
            Port = (uint)LocalPort.Value
        };

        return builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task WaitForTunnelAsync(Process process, Task<string> stderrTask, int localPort)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stderr = await stderrTask;
                throw new InvalidOperationException($"ssh tunnel exited early with code {process.ExitCode}. {stderr}".Trim());
            }

            if (await CanConnectAsync(localPort))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for ssh tunnel on 127.0.0.1:{localPort}.");
    }

    private static async Task<bool> CanConnectAsync(int localPort)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, localPort).WaitAsync(TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetFreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public static class KanbanTables
{
    public static readonly IReadOnlyList<TableSpec> All =
    [
        new("Projects",
        [
            Text("Id"),
            Text("Name"),
            Text("Key"),
            Text("Description"),
            Bool("IsArchived"),
            DateTimeOffset("CreatedAtUtc")
        ]),
        new("AppUsers",
        [
            Text("Id"),
            Text("Issuer"),
            Text("Subject"),
            Text("DisplayName"),
            Text("Email"),
            DateTimeOffset("CreatedAtUtc"),
            DateTimeOffset("UpdatedAtUtc"),
            DateTimeOffset("LastSeenAtUtc")
        ]),
        new("Epics",
        [
            Text("Id"),
            Text("ProjectId"),
            Text("Name"),
            Text("Description"),
            Bool("IsArchived"),
            DateTimeOffset("CreatedAtUtc"),
            DateTimeOffset("UpdatedAtUtc")
        ]),
        new("EpicDocuments",
        [
            Text("Id"),
            Text("EpicId"),
            Text("Title"),
            Text("Body"),
            DateTimeOffset("CreatedAtUtc"),
            DateTimeOffset("UpdatedAtUtc")
        ]),
        new("WorkItems",
        [
            Text("Id"),
            Text("ProjectId"),
            Text("EpicId"),
            Text("Title"),
            Text("Description"),
            Int("Type"),
            Int("Status"),
            Int("Priority"),
            Int("Order"),
            Int("Estimate"),
            Text("Labels"),
            DateTimeOffset("CreatedAtUtc"),
            DateTimeOffset("UpdatedAtUtc")
        ]),
        new("WorkItemComments",
        [
            Text("Id"),
            Text("WorkItemId"),
            Text("Author"),
            Text("Body"),
            DateTimeOffset("CreatedAtUtc")
        ]),
        new("PersonalAccessTokens",
        [
            Text("Id"),
            Text("AppUserId"),
            Text("Name"),
            Text("TokenPrefix"),
            Text("TokenHash"),
            Text("EncryptedSecret"),
            DateTimeOffset("CreatedAtUtc"),
            DateTimeOffset("ExpiresAtUtc"),
            DateTimeOffset("LastUsedAtUtc"),
            DateTimeOffset("RevokedAtUtc")
        ])
    ];

    private static ColumnSpec Text(string name) => new(name, ColumnKind.Text);

    private static ColumnSpec Int(string name) => new(name, ColumnKind.Int);

    private static ColumnSpec Bool(string name) => new(name, ColumnKind.Bool);

    private static ColumnSpec DateTimeOffset(string name) => new(name, ColumnKind.DateTimeOffset);
}
