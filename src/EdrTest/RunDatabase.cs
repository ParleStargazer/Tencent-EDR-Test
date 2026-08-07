using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace EdrTest;

public sealed record RunSeed(string RunId, string? SuiteId, string? EnvironmentId, DateTimeOffset StartedAtUtc);

public sealed class ProgramObservation
{
    public string ProgramInstanceId { get; init; } = Ids.NewUuid7();
    public required string CaseRunId { get; init; }
    public required string Role { get; init; }
    public string? InstanceName { get; init; }
    public int InstanceIndex { get; init; }
    public required string ExecutablePath { get; init; }
    public required string Sha256 { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Imphash { get; init; }
    public required int Pid { get; init; }
    public required int ParentPid { get; init; }
    public int? SessionId { get; init; }
    public required string Architecture { get; init; }
    public required string CommandLine { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? UserSid { get; init; }
    public string? UserName { get; init; }
    public string? UserDomain { get; init; }
    public string? IntegrityLevel { get; init; }
    public string? ElevationType { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public int? ExitCode { get; init; }
    public bool StartupAttempted { get; init; } = true;
    public bool StartupSucceeded { get; init; } = true;
    public int? StartupWin32Error { get; init; }
    public string? StartupMessage { get; init; }
    public JsonObject Metadata { get; init; } = [];

    public static ProgramObservation CaptureCurrent(string caseRunId, string role, int instanceIndex = 0, string? instanceName = null)
    {
        var process = Process.GetCurrentProcess();
        var path = Environment.ProcessPath ?? process.MainModule?.FileName ?? throw new InvalidOperationException("无法取得当前 EXE 路径。");
        var file = new FileInfo(path);
        string? sid = null;
        string? userName = null;
        string? userDomain = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value;
            var account = identity.Name.Split('\\', 2);
            userDomain = account.Length == 2 ? account[0] : null;
            userName = account[^1];
        }
        catch (PlatformNotSupportedException)
        {
            userName = Environment.UserName;
        }

        return new ProgramObservation
        {
            CaseRunId = caseRunId,
            Role = role,
            InstanceName = instanceName,
            InstanceIndex = instanceIndex,
            ExecutablePath = path,
            Sha256 = Hashing.FileSha256(path),
            Sha1 = Hashing.FileSha1(path),
            Md5 = Hashing.FileMd5(path),
            Pid = Environment.ProcessId,
            ParentPid = 0,
            SessionId = process.SessionId,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
            {
                "x86" => "x86",
                "arm64" => "arm64",
                _ => "x64",
            },
            CommandLine = Environment.CommandLine,
            WorkingDirectory = Environment.CurrentDirectory,
            UserSid = sid,
            UserName = userName,
            UserDomain = userDomain,
            StartedAtUtc = process.StartTime.ToUniversalTime(),
            StartupAttempted = true,
            StartupSucceeded = true,
            Metadata = new JsonObject { ["captured_by"] = "EdrTest SDK" },
        };
    }

}

public sealed class LocalEventObservation
{
    public string LocalEventId { get; init; } = Ids.NewUuid7();
    public required string CaseRunId { get; init; }
    public int Sequence { get; init; } = 1;
    public required string EventType { get; init; }
    public required string EventAction { get; init; }
    public required string Nonce { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public long MonotonicOffsetMs { get; init; }
    public required string Source { get; init; }
    public required string CollectionMethod { get; init; }
    public string? CollectorVersion { get; init; } = EdrTestVersion.Current;
    public string Confidence { get; init; } = "high";
    public string? ActorProgramId { get; init; }
    public string? TargetProgramId { get; init; }
    public required JsonObject Data { get; init; }
    public List<string> EvidenceRefs { get; init; } = [];
}

public sealed class LocalFactObservation
{
    public string LocalFactId { get; init; } = Ids.NewUuid7();
    public required string CaseRunId { get; init; }
    public string? LocalEventId { get; init; }
    public required string Key { get; init; }
    public required JsonNode? Value { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required string Source { get; init; }
    public string Confidence { get; init; } = "high";
}

public sealed class ArtifactObservation
{
    public string ArtifactId { get; init; } = Ids.NewUuid7();
    public required string CaseRunId { get; init; }
    public required string Kind { get; init; }
    public required string RelativePath { get; init; }
    public string? MediaType { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public bool Sensitive { get; init; }
    public JsonObject Metadata { get; init; } = [];
}

public sealed class CleanupObservation
{
    public string CleanupResultId { get; init; } = Ids.NewUuid7();
    public required string CaseRunId { get; init; }
    public int Sequence { get; init; } = 1;
    public required string Action { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public JsonObject Before { get; init; } = [];
    public JsonObject After { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

public sealed class RunDatabase : IDisposable
{
    private static readonly string Ddl = LoadDdl();
    private readonly SqliteConnection connection;

    private RunDatabase(SqliteConnection connection) => this.connection = connection;

    public static string SchemaSql => Ddl;

    public static RunDatabase Create(string path, RunSeed seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path)) throw new IOException($"运行数据库已存在：{path}");
        var database = Open(path, SqliteOpenMode.ReadWriteCreate);
        database.connection.ExecuteNonQuery(Ddl);
        database.connection.ExecuteNonQuery("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;");

        var now = seed.StartedAtUtc;
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var zone = TimeZoneInfo.Local;
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() switch
        {
            "x86" => "x86",
            "arm64" => "arm64",
            _ => "x64",
        };
        using var command = database.connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run (
              singleton, run_id, database_schema_version, tool_version, suite_id, environment_id, status,
              started_at_utc, timezone, utc_offset_minutes, hostname, os_family, os_version, os_build,
              architecture, boot_time_utc, clock_json, environment_json, finalized
            ) VALUES (
              1, $run_id, 2, $tool_version, $suite_id, $environment_id, 'RUNNING',
              $started_at, $timezone, $offset, $hostname, 'windows', $os_version, $os_build,
              $architecture, $boot_time, $clock, '{}', 0
            );
            """;
        command.Parameters.AddWithValue("$run_id", seed.RunId);
        command.Parameters.AddWithValue("$tool_version", EdrTestVersion.Current);
        command.Parameters.AddNullable("$suite_id", seed.SuiteId);
        command.Parameters.AddNullable("$environment_id", seed.EnvironmentId);
        command.Parameters.AddWithValue("$started_at", Values.Utc(now));
        command.Parameters.AddWithValue("$timezone", zone.Id);
        command.Parameters.AddWithValue("$offset", (int)zone.GetUtcOffset(now).TotalMinutes);
        command.Parameters.AddWithValue("$hostname", Environment.MachineName);
        command.Parameters.AddWithValue("$os_version", Environment.OSVersion.VersionString);
        command.Parameters.AddWithValue("$os_build", Math.Max(1, Environment.OSVersion.Version.Build));
        command.Parameters.AddWithValue("$architecture", architecture);
        command.Parameters.AddWithValue("$boot_time", Values.Utc(boot));
        command.Parameters.AddWithValue("$clock", "{\"time_source\":\"system_utc_and_stopwatch\",\"synchronized\":null}");
        command.ExecuteNonQuery();
        return database;
    }

    public static RunDatabase OpenReadWrite(string path)
    {
        var database = Open(path, SqliteOpenMode.ReadWrite);
        using var command = database.connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar()) != EdrTestVersion.DatabaseSchema)
        {
            database.Dispose();
            throw new InvalidDataException($"运行数据库版本不是 {EdrTestVersion.DatabaseSchema}。");
        }
        return database;
    }

    internal static SqliteConnection OpenReadOnlyConnection(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        connection.ExecuteNonQuery("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public void AddCapability(string runId, string caseRunId, int sequence, string nonce, CapabilityPackage package, JsonElement parameters)
    {
        var manifest = package.Manifest;
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO capability_run (
              case_run_id, run_id, sequence_number, capability_id, display_name_zh, display_name_en, category,
              capability_version, manifest_sha256, baseline_id, baseline_version, nonce, risk_level,
              required_privilege, status, parameters_json, preconditions_json, observer_sources_json,
              observer_dropped_count, observer_warnings_json
            ) VALUES (
              $case_id, $run_id, $sequence, $capability_id, $name_zh, $name_en, $category,
              $version, $manifest_sha, $baseline_id, $baseline_version, $nonce, $risk,
              $privilege, 'PLANNED', $parameters, '[]', '[]', 0, '[]'
            );
            """;
        command.Parameters.AddWithValue("$case_id", caseRunId);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$capability_id", manifest.CapabilityId);
        command.Parameters.AddNullable("$name_zh", manifest.DisplayNameZh ?? manifest.DisplayName);
        command.Parameters.AddNullable("$name_en", manifest.DisplayNameEn);
        command.Parameters.AddNullable("$category", CategoryFromId(manifest.CapabilityId));
        command.Parameters.AddWithValue("$version", manifest.Version);
        command.Parameters.AddWithValue("$manifest_sha", package.ManifestSha256);
        command.Parameters.AddWithValue("$baseline_id", manifest.CapabilityId);
        command.Parameters.AddWithValue("$baseline_version", manifest.Version);
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$risk", manifest.RiskLevel);
        command.Parameters.AddWithValue("$privilege", manifest.RequiredPrivilege);
        command.Parameters.AddWithValue("$parameters", parameters.GetRawText());
        command.ExecuteNonQuery();
    }

    public void StartCapability(string caseRunId, DateTimeOffset startedAt, IReadOnlyList<string> sources)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capability_run SET status = 'EXECUTING', started_at_utc = $time,
              observer_started_at_utc = $time, observer_sources_json = $sources
            WHERE case_run_id = $case_id;
            """;
        command.Parameters.AddWithValue("$time", Values.Utc(startedAt));
        command.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(sources, JsonDefaults.Options));
        command.Parameters.AddWithValue("$case_id", caseRunId);
        EnsureOne(command.ExecuteNonQuery(), caseRunId);
    }

    public void CompleteCapability(string caseRunId, string status, DateTimeOffset endedAt, long durationMs, string? errorCode = null, string? errorMessage = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capability_run SET status = $status, ended_at_utc = $ended,
              observer_ended_at_utc = COALESCE(observer_ended_at_utc, $ended),
              observer_started_at_utc = COALESCE(observer_started_at_utc, started_at_utc, $ended),
              observer_sources_json = CASE WHEN observer_sources_json = '[]' THEN '[\"controller\"]' ELSE observer_sources_json END,
              monotonic_duration_ms = $duration, error_code = $error_code, error_message = $error_message
            WHERE case_run_id = $case_id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$ended", Values.Utc(endedAt));
        command.Parameters.AddWithValue("$duration", Math.Max(0, durationMs));
        command.Parameters.AddNullable("$error_code", errorCode);
        command.Parameters.AddNullable("$error_message", errorMessage);
        command.Parameters.AddWithValue("$case_id", caseRunId);
        EnsureOne(command.ExecuteNonQuery(), caseRunId);
    }

    public string GetCapabilityStatus(string caseRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM capability_run WHERE case_run_id = $case_id;";
        command.Parameters.AddWithValue("$case_id", caseRunId);
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException($"找不到能力轮次：{caseRunId}");
    }

    public void AddProgram(ProgramObservation value)
    {
        var file = new FileInfo(value.ExecutablePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO program_instance (
              program_instance_id, case_run_id, role, instance_name, instance_index, executable_path,
              file_name, file_size_bytes, file_created_at_utc, file_modified_at_utc, sha256, sha1, md5,
              imphash, pid, parent_pid, session_id, architecture, command_line, working_directory,
              user_sid, user_name, user_domain, integrity_level, elevation_type, started_at_utc,
              ended_at_utc, exit_code, startup_attempted, startup_succeeded, startup_win32_error,
              startup_message, metadata_json
            ) VALUES (
              $id, $case_id, $role, $name, $index, $path,
              $file_name, $size, $created, $modified, $sha256, $sha1, $md5,
              $imphash, $pid, $parent_pid, $session_id, $architecture, $command_line, $cwd,
              $user_sid, $user_name, $user_domain, $integrity, $elevation, $started,
              $ended, $exit_code, $attempted, $succeeded, $win32_error, $message, $metadata
            );
            """;
        command.Parameters.AddWithValue("$id", value.ProgramInstanceId);
        command.Parameters.AddWithValue("$case_id", value.CaseRunId);
        command.Parameters.AddWithValue("$role", value.Role);
        command.Parameters.AddNullable("$name", value.InstanceName);
        command.Parameters.AddWithValue("$index", value.InstanceIndex);
        command.Parameters.AddWithValue("$path", Path.GetFullPath(value.ExecutablePath));
        command.Parameters.AddWithValue("$file_name", file.Name);
        command.Parameters.AddNullable("$size", file.Exists ? file.Length : null);
        command.Parameters.AddNullable("$created", file.Exists ? Values.Utc(file.CreationTimeUtc) : null);
        command.Parameters.AddNullable("$modified", file.Exists ? Values.Utc(file.LastWriteTimeUtc) : null);
        command.Parameters.AddWithValue("$sha256", value.Sha256);
        command.Parameters.AddNullable("$sha1", value.Sha1);
        command.Parameters.AddNullable("$md5", value.Md5);
        command.Parameters.AddNullable("$imphash", value.Imphash);
        command.Parameters.AddWithValue("$pid", value.Pid);
        command.Parameters.AddWithValue("$parent_pid", value.ParentPid);
        command.Parameters.AddNullable("$session_id", value.SessionId);
        command.Parameters.AddWithValue("$architecture", value.Architecture);
        command.Parameters.AddWithValue("$command_line", value.CommandLine);
        command.Parameters.AddNullable("$cwd", value.WorkingDirectory);
        command.Parameters.AddNullable("$user_sid", value.UserSid);
        command.Parameters.AddNullable("$user_name", value.UserName);
        command.Parameters.AddNullable("$user_domain", value.UserDomain);
        command.Parameters.AddNullable("$integrity", value.IntegrityLevel);
        command.Parameters.AddNullable("$elevation", value.ElevationType);
        command.Parameters.AddWithValue("$started", Values.Utc(value.StartedAtUtc));
        command.Parameters.AddNullable("$ended", value.EndedAtUtc is { } ended ? Values.Utc(ended) : null);
        command.Parameters.AddNullable("$exit_code", value.ExitCode);
        command.Parameters.AddWithValue("$attempted", value.StartupAttempted ? 1 : 0);
        command.Parameters.AddWithValue("$succeeded", value.StartupSucceeded ? 1 : 0);
        command.Parameters.AddNullable("$win32_error", value.StartupWin32Error);
        command.Parameters.AddNullable("$message", value.StartupMessage);
        command.Parameters.AddWithValue("$metadata", value.Metadata.ToJsonString());
        command.ExecuteNonQuery();
    }

    public void AddEvent(LocalEventObservation value)
    {
        if (value.Data["kind"]?.GetValue<string>() != value.EventType || value.Data["operation"]?.GetValue<string>() != value.EventAction)
        {
            throw new InvalidDataException("事件 data.kind/operation 必须与 event_type/event_action 一致。");
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_event (
              local_event_id, case_run_id, sequence_number, event_type, event_action, nonce,
              occurred_at_utc, observed_at_utc, monotonic_offset_ms, source, collection_method,
              collector_version, confidence, actor_program_id, target_program_id, data_json, evidence_refs_json
            ) VALUES (
              $id, $case_id, $sequence, $type, $action, $nonce, $occurred, $observed, $offset,
              $source, $method, $collector, $confidence, $actor, $target, $data, $evidence
            );
            """;
        command.Parameters.AddWithValue("$id", value.LocalEventId);
        command.Parameters.AddWithValue("$case_id", value.CaseRunId);
        command.Parameters.AddWithValue("$sequence", value.Sequence);
        command.Parameters.AddWithValue("$type", value.EventType);
        command.Parameters.AddWithValue("$action", value.EventAction);
        command.Parameters.AddWithValue("$nonce", value.Nonce);
        command.Parameters.AddWithValue("$occurred", Values.Utc(value.OccurredAtUtc));
        command.Parameters.AddWithValue("$observed", Values.Utc(value.ObservedAtUtc));
        command.Parameters.AddWithValue("$offset", value.MonotonicOffsetMs);
        command.Parameters.AddWithValue("$source", value.Source);
        command.Parameters.AddWithValue("$method", value.CollectionMethod);
        command.Parameters.AddNullable("$collector", value.CollectorVersion);
        command.Parameters.AddWithValue("$confidence", value.Confidence);
        command.Parameters.AddNullable("$actor", value.ActorProgramId);
        command.Parameters.AddNullable("$target", value.TargetProgramId);
        command.Parameters.AddWithValue("$data", value.Data.ToJsonString());
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(value.EvidenceRefs, JsonDefaults.Options));
        command.ExecuteNonQuery();
    }

    public void AddFact(LocalFactObservation value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_fact (
              local_fact_id, case_run_id, local_event_id, fact_key, value_json, value_type,
              observed_at_utc, source, confidence
            ) VALUES ($id, $case_id, $event_id, $key, $value, $type, $time, $source, $confidence);
            """;
        command.Parameters.AddWithValue("$id", value.LocalFactId);
        command.Parameters.AddWithValue("$case_id", value.CaseRunId);
        command.Parameters.AddNullable("$event_id", value.LocalEventId);
        command.Parameters.AddWithValue("$key", value.Key);
        command.Parameters.AddWithValue("$value", value.Value?.ToJsonString() ?? "null");
        command.Parameters.AddWithValue("$type", JsonType(value.Value));
        command.Parameters.AddWithValue("$time", Values.Utc(value.ObservedAtUtc));
        command.Parameters.AddWithValue("$source", value.Source);
        command.Parameters.AddWithValue("$confidence", value.Confidence);
        command.ExecuteNonQuery();
    }

    public void AddArtifact(ArtifactObservation value)
    {
        if (Path.IsPathRooted(value.RelativePath)
            || value.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("artifact.relative_path 必须是不能越过轮次目录的相对路径。");
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifact (
              artifact_id, case_run_id, kind, relative_path, media_type, sha256, size_bytes,
              created_at_utc, sensitive, metadata_json
            ) VALUES ($id, $case_id, $kind, $path, $media, $sha, $size, $created, $sensitive, $metadata);
            """;
        command.Parameters.AddWithValue("$id", value.ArtifactId);
        command.Parameters.AddWithValue("$case_id", value.CaseRunId);
        command.Parameters.AddWithValue("$kind", value.Kind);
        command.Parameters.AddWithValue("$path", value.RelativePath);
        command.Parameters.AddNullable("$media", value.MediaType);
        command.Parameters.AddWithValue("$sha", value.Sha256);
        command.Parameters.AddWithValue("$size", value.SizeBytes);
        command.Parameters.AddWithValue("$created", Values.Utc(value.CreatedAtUtc));
        command.Parameters.AddWithValue("$sensitive", value.Sensitive ? 1 : 0);
        command.Parameters.AddWithValue("$metadata", value.Metadata.ToJsonString());
        command.ExecuteNonQuery();
    }

    public void AddCleanup(CleanupObservation value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cleanup_result (
              cleanup_result_id, case_run_id, sequence_number, action, status, started_at_utc,
              ended_at_utc, before_json, after_json, error_message
            ) VALUES ($id, $case_id, $sequence, $action, $status, $started, $ended, $before, $after, $error);
            """;
        command.Parameters.AddWithValue("$id", value.CleanupResultId);
        command.Parameters.AddWithValue("$case_id", value.CaseRunId);
        command.Parameters.AddWithValue("$sequence", value.Sequence);
        command.Parameters.AddWithValue("$action", value.Action);
        command.Parameters.AddWithValue("$status", value.Status);
        command.Parameters.AddWithValue("$started", Values.Utc(value.StartedAtUtc));
        command.Parameters.AddWithValue("$ended", Values.Utc(value.EndedAtUtc));
        command.Parameters.AddWithValue("$before", value.Before.ToJsonString());
        command.Parameters.AddWithValue("$after", value.After.ToJsonString());
        command.Parameters.AddNullable("$error", value.ErrorMessage);
        command.ExecuteNonQuery();
    }

    public void AddLog(string? caseRunId, string level, string phase, string message, string? code = null, JsonObject? properties = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO execution_log (case_run_id, timestamp_utc, level, phase, code, message, properties_json)
            VALUES ($case_id, $time, $level, $phase, $code, $message, $properties);
            """;
        command.Parameters.AddNullable("$case_id", caseRunId);
        command.Parameters.AddWithValue("$time", Values.Utc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$phase", phase);
        command.Parameters.AddNullable("$code", code);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$properties", (properties ?? []).ToJsonString());
        command.ExecuteNonQuery();
    }

    public void Seal(string status, DateTimeOffset endedAt)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE run SET status = $status, ended_at_utc = $ended, finalized = 1 WHERE singleton = 1;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$ended", Values.Utc(endedAt));
            EnsureOne(command.ExecuteNonQuery(), "run");
        }
        connection.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
        connection.ExecuteNonQuery("PRAGMA journal_mode = DELETE;");
    }

    public void Dispose() => connection.Dispose();

    private static RunDatabase Open(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        connection.ExecuteNonQuery("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return new RunDatabase(connection);
    }

    private static string LoadDdl()
    {
        var assembly = typeof(RunDatabase).Assembly;
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("run-db.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("缺少内嵌 run-db.sql。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? CategoryFromId(string capabilityId)
    {
        var parts = capabilityId.Split('.');
        return parts.Length >= 3 ? parts[1] : null;
    }

    private static string JsonType(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonObject) return "object";
        if (node is JsonArray) return "array";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out _)) return "boolean";
            if (value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _)) return "integer";
            if (value.TryGetValue<double>(out _) || value.TryGetValue<decimal>(out _)) return "number";
        }
        return "string";
    }

    private static void EnsureOne(int count, string id)
    {
        if (count != 1) throw new InvalidOperationException($"数据库更新对象不存在或不唯一：{id}");
    }
}

public sealed record ControllerInvocation(
    string RunId,
    string CaseRunId,
    string Nonce,
    string RunDb,
    string WorkDir,
    string ManifestPath,
    string PackageDirectory,
    string ParametersPath,
    int TimeoutMs)
{
    public static ControllerInvocation Parse(IEnumerable<string> arguments)
    {
        var options = CliOptions.Parse(arguments);
        var invocation = new ControllerInvocation(
            options.Require("run-id"),
            options.Require("case-run-id"),
            options.Require("nonce"),
            Path.GetFullPath(options.Require("run-db")),
            Path.GetFullPath(options.Require("work-dir")),
            Path.GetFullPath(options.Require("manifest")),
            Path.GetFullPath(options.Require("package-dir")),
            Path.GetFullPath(options.Require("parameters")),
            options.RequireInt("timeout-ms", 1, 3_600_000));
        if (!Guid.TryParse(invocation.RunId, out _) || !Guid.TryParse(invocation.CaseRunId, out _)) throw new ArgumentException("run-id/case-run-id 必须是 UUID。");
        if (invocation.Nonce.Length != 32 || invocation.Nonce.Any(character => !Uri.IsHexDigit(character))) throw new ArgumentException("nonce 必须是 128-bit 十六进制字符串。");
        if (!File.Exists(invocation.RunDb)) throw new FileNotFoundException("找不到运行数据库。", invocation.RunDb);
        if (!File.Exists(invocation.ManifestPath)) throw new FileNotFoundException("找不到能力清单。", invocation.ManifestPath);
        if (!Directory.Exists(invocation.PackageDirectory)) throw new DirectoryNotFoundException($"找不到能力包目录：{invocation.PackageDirectory}");
        if (!File.Exists(invocation.ParametersPath)) throw new FileNotFoundException("找不到参数文件。", invocation.ParametersPath);
        return invocation;
    }
}

internal static class SqliteExtensions
{
    public static void ExecuteNonQuery(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static void AddNullable(this SqliteParameterCollection parameters, string name, object? value) =>
        parameters.AddWithValue(name, value ?? DBNull.Value);
}
