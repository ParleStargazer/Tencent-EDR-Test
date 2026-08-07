using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace EdrTest;

public static class ExportService
{
    public static void Export(string databasePath, string outputPath)
    {
        databasePath = Path.GetFullPath(databasePath);
        outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("找不到运行数据库。", databasePath);
        if (string.Equals(databasePath, outputPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("导出路径不能覆盖数据库。");

        using var connection = RunDatabase.OpenReadOnlyConnection(databasePath);
        var run = ReadRun(connection);
        if (run["finalized"]?.GetValue<bool>() != true) throw new InvalidOperationException("数据库尚未封存，不能导出。");
        run.Remove("finalized");

        var capabilities = ReadCapabilities(connection);
        var programs = ReadPrograms(connection);
        var events = ReadEvents(connection);
        var facts = ReadFacts(connection);
        var artifacts = ReadArtifacts(connection);
        var cleanup = ReadCleanup(connection);
        var executionLogs = ReadExecutionLogs(connection);
        var root = new JsonObject
        {
            ["schema_version"] = EdrTestVersion.RunExportSchema,
            ["run"] = run,
            ["capabilities"] = capabilities,
            ["programs"] = programs,
            ["local_events"] = events,
            ["local_facts"] = facts,
            ["artifacts"] = artifacts,
            ["cleanup_results"] = cleanup,
            ["execution_logs"] = executionLogs,
            ["integrity"] = new JsonObject
            {
                ["database_sha256"] = Hashing.FileSha256(databasePath),
                ["database_size_bytes"] = new FileInfo(databasePath).Length,
                ["schema_sha256"] = Hashing.TextSha256(RunDatabase.SchemaSql),
                ["exported_at_utc"] = Values.Utc(DateTimeOffset.UtcNow),
                ["exporter_version"] = EdrTestVersion.Current,
                ["record_counts"] = new JsonObject
                {
                    ["capabilities"] = capabilities.Count,
                    ["programs"] = programs.Count,
                    ["local_events"] = events.Count,
                    ["local_facts"] = facts.Count,
                    ["artifacts"] = artifacts.Count,
                    ["cleanup_results"] = cleanup.Count,
                    ["execution_logs"] = executionLogs.Count,
                },
                ["warnings"] = new JsonArray(),
            },
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + ".tmp-" + Ids.NewUuid7();
        File.WriteAllText(temporary, root.ToJsonString(JsonDefaults.Options) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, outputPath, overwrite: true);
    }

    private static JsonObject ReadRun(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM run WHERE singleton = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("数据库缺少 run 主记录。");
        return new JsonObject
        {
            ["run_id"] = reader.String("run_id"),
            ["database_schema_version"] = reader.Int32("database_schema_version"),
            ["tool_version"] = reader.String("tool_version"),
            ["suite_id"] = reader.NullableString("suite_id"),
            ["environment_id"] = reader.NullableString("environment_id"),
            ["status"] = reader.String("status"),
            ["started_at_utc"] = reader.String("started_at_utc"),
            ["ended_at_utc"] = reader.String("ended_at_utc"),
            ["timezone"] = reader.String("timezone"),
            ["utc_offset_minutes"] = reader.NullableInt32("utc_offset_minutes"),
            ["clock"] = ParseObject(reader.String("clock_json")),
            ["host"] = new JsonObject
            {
                ["hostname"] = reader.String("hostname"),
                ["machine_id"] = reader.NullableString("machine_id"),
                ["os_family"] = reader.String("os_family"),
                ["os_version"] = reader.String("os_version"),
                ["os_build"] = reader.Int32("os_build"),
                ["os_edition"] = reader.NullableString("os_edition"),
                ["architecture"] = reader.String("architecture"),
                ["boot_id"] = reader.NullableString("boot_id"),
                ["boot_time_utc"] = reader.String("boot_time_utc"),
                ["domain"] = reader.NullableString("domain_name"),
                ["primary_user_sid"] = reader.NullableString("primary_user_sid"),
                ["agent_id_hint"] = reader.NullableString("agent_id_hint"),
                ["agent_version_hint"] = reader.NullableString("agent_version_hint"),
            },
            ["finalized"] = reader.Int32("finalized") == 1,
        };
    }

    private static JsonArray ReadCapabilities(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM capability_run ORDER BY sequence_number;";
        using var reader = command.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            result.Add(new JsonObject
            {
                ["case_run_id"] = reader.String("case_run_id"),
                ["capability_id"] = reader.String("capability_id"),
                ["display_name_zh"] = reader.NullableString("display_name_zh"),
                ["display_name_en"] = reader.NullableString("display_name_en"),
                ["category"] = reader.NullableString("category"),
                ["capability_version"] = reader.String("capability_version"),
                ["manifest_sha256"] = reader.String("manifest_sha256"),
                ["baseline_id"] = reader.NullableString("baseline_id"),
                ["baseline_version"] = reader.NullableString("baseline_version"),
                ["sequence"] = reader.Int32("sequence_number"),
                ["nonce"] = reader.String("nonce"),
                ["risk_level"] = reader.String("risk_level"),
                ["required_privilege"] = reader.String("required_privilege"),
                ["status"] = reader.String("status"),
                ["started_at_utc"] = reader.String("started_at_utc"),
                ["ended_at_utc"] = reader.String("ended_at_utc"),
                ["duration_ms"] = reader.NullableInt64("monotonic_duration_ms") ?? 0,
                ["parameters"] = ParseObject(reader.String("parameters_json")),
                ["preconditions"] = ParseArray(reader.String("preconditions_json")),
                ["observation_window"] = new JsonObject
                {
                    ["started_at_utc"] = reader.String("observer_started_at_utc"),
                    ["ended_at_utc"] = reader.String("observer_ended_at_utc"),
                    ["sources"] = ParseArray(reader.String("observer_sources_json")),
                    ["dropped_event_count"] = reader.Int32("observer_dropped_count"),
                    ["warnings"] = ParseArray(reader.String("observer_warnings_json")),
                },
                ["error_code"] = reader.NullableString("error_code"),
                ["error_message"] = reader.NullableString("error_message"),
            });
        }
        return result;
    }

    private static JsonArray ReadPrograms(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM program_instance ORDER BY case_run_id, role, instance_index;";
        using var reader = command.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            var user = reader.IsDBNull("user_sid") && reader.IsDBNull("user_name") && reader.IsDBNull("user_domain")
                ? null
                : new JsonObject
                {
                    ["sid"] = reader.NullableString("user_sid"),
                    ["name"] = reader.NullableString("user_name"),
                    ["domain"] = reader.NullableString("user_domain"),
                };
            result.Add(new JsonObject
            {
                ["program_instance_id"] = reader.String("program_instance_id"),
                ["case_run_id"] = reader.String("case_run_id"),
                ["role"] = reader.String("role"),
                ["instance_name"] = reader.NullableString("instance_name"),
                ["instance_index"] = reader.Int32("instance_index"),
                ["executable"] = reader.String("executable_path"),
                ["file_name"] = reader.NullableString("file_name"),
                ["file_size_bytes"] = reader.NullableInt64("file_size_bytes"),
                ["file_created_at_utc"] = reader.NullableString("file_created_at_utc"),
                ["file_modified_at_utc"] = reader.NullableString("file_modified_at_utc"),
                ["sha256"] = reader.String("sha256"),
                ["sha1"] = reader.NullableString("sha1"),
                ["md5"] = reader.NullableString("md5"),
                ["imphash"] = reader.NullableString("imphash"),
                ["signature"] = reader.IsDBNull("signature_json") ? null : ParseObject(reader.String("signature_json")),
                ["pid"] = reader.Int32("pid"),
                ["parent_pid"] = reader.Int32("parent_pid"),
                ["session_id"] = reader.NullableInt32("session_id"),
                ["architecture"] = reader.String("architecture"),
                ["command_line"] = reader.String("command_line"),
                ["working_directory"] = reader.NullableString("working_directory"),
                ["user"] = user,
                ["integrity_level"] = reader.NullableString("integrity_level"),
                ["elevation_type"] = reader.NullableString("elevation_type"),
                ["started_at_utc"] = reader.String("started_at_utc"),
                ["ended_at_utc"] = reader.NullableString("ended_at_utc"),
                ["exit_code"] = reader.NullableInt32("exit_code"),
                ["startup_result"] = new JsonObject
                {
                    ["attempted"] = reader.Int32("startup_attempted") == 1,
                    ["succeeded"] = reader.Int32("startup_succeeded") == 1,
                    ["win32_error"] = reader.NullableInt32("startup_win32_error"),
                    ["message"] = reader.NullableString("startup_message"),
                },
            });
        }
        return result;
    }

    private static JsonArray ReadEvents(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM local_event ORDER BY case_run_id, sequence_number;";
        using var reader = command.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            result.Add(new JsonObject
            {
                ["local_event_id"] = reader.String("local_event_id"),
                ["case_run_id"] = reader.String("case_run_id"),
                ["sequence"] = reader.Int32("sequence_number"),
                ["event_type"] = reader.String("event_type"),
                ["event_action"] = reader.String("event_action"),
                ["nonce"] = reader.String("nonce"),
                ["occurred_at_utc"] = reader.String("occurred_at_utc"),
                ["observed_at_utc"] = reader.String("observed_at_utc"),
                ["monotonic_offset_ms"] = reader.Int64("monotonic_offset_ms"),
                ["source"] = reader.String("source"),
                ["collection_method"] = reader.String("collection_method"),
                ["collector_version"] = reader.NullableString("collector_version"),
                ["confidence"] = reader.String("confidence"),
                ["actor_program_id"] = reader.NullableString("actor_program_id"),
                ["target_program_id"] = reader.NullableString("target_program_id"),
                ["data"] = ParseObject(reader.String("data_json")),
                ["evidence_refs"] = ParseArray(reader.String("evidence_refs_json")),
            });
        }
        return result;
    }

    private static JsonArray ReadFacts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM local_fact ORDER BY case_run_id, fact_key;";
        using var reader = command.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            result.Add(new JsonObject
            {
                ["local_fact_id"] = reader.String("local_fact_id"),
                ["case_run_id"] = reader.String("case_run_id"),
                ["local_event_id"] = reader.NullableString("local_event_id"),
                ["key"] = reader.String("fact_key"),
                ["value"] = JsonNode.Parse(reader.String("value_json")),
                ["value_type"] = reader.String("value_type"),
                ["observed_at_utc"] = reader.String("observed_at_utc"),
                ["source"] = reader.String("source"),
                ["confidence"] = reader.String("confidence"),
            });
        }
        return result;
    }

    private static JsonArray ReadArtifacts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM artifact ORDER BY case_run_id, relative_path;";
        using var reader = command.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            result.Add(new JsonObject
            {
                ["artifact_id"] = reader.String("artifact_id"),
                ["case_run_id"] = reader.String("case_run_id"),
                ["kind"] = reader.String("kind"),
                ["relative_path"] = reader.String("relative_path"),
                ["media_type"] = reader.NullableString("media_type"),
                ["sha256"] = reader.String("sha256"),
                ["size_bytes"] = reader.Int64("size_bytes"),
                ["created_at_utc"] = reader.String("created_at_utc"),
                ["sensitive"] = reader.Int32("sensitive") == 1,
                ["metadata"] = ParseObject(reader.String("metadata_json")),
            });
        }
        return result;
    }

    private static JsonArray ReadCleanup(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM cleanup_result ORDER BY case_run_id, sequence_number;";
        using var reader = command.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            result.Add(new JsonObject
            {
                ["cleanup_result_id"] = reader.String("cleanup_result_id"),
                ["case_run_id"] = reader.String("case_run_id"),
                ["sequence"] = reader.Int32("sequence_number"),
                ["action"] = reader.String("action"),
                ["status"] = reader.String("status"),
                ["started_at_utc"] = reader.String("started_at_utc"),
                ["ended_at_utc"] = reader.String("ended_at_utc"),
                ["before"] = ParseObject(reader.String("before_json")),
                ["after"] = ParseObject(reader.String("after_json")),
                ["error_message"] = reader.NullableString("error_message"),
            });
        }
        return result;
    }

    private static JsonArray ReadExecutionLogs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM execution_log ORDER BY log_id;";
        using var reader = command.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            result.Add(new JsonObject
            {
                ["log_id"] = reader.Int64("log_id"),
                ["case_run_id"] = reader.NullableString("case_run_id"),
                ["timestamp_utc"] = reader.String("timestamp_utc"),
                ["level"] = reader.String("level"),
                ["phase"] = reader.String("phase"),
                ["code"] = reader.NullableString("code"),
                ["message"] = reader.String("message"),
                ["properties"] = ParseObject(reader.String("properties_json")),
            });
        }
        return result;
    }

    private static JsonObject ParseObject(string json) => JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("数据库 JSON 应为对象。");
    private static JsonArray ParseArray(string json) => JsonNode.Parse(json) as JsonArray ?? throw new InvalidDataException("数据库 JSON 应为数组。");
}

internal static class ReaderExtensions
{
    public static string String(this SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    public static string? NullableString(this SqliteDataReader reader, string name) => reader.IsDBNull(name) ? null : reader.String(name);
    public static int Int32(this SqliteDataReader reader, string name) => reader.GetInt32(reader.GetOrdinal(name));
    public static int? NullableInt32(this SqliteDataReader reader, string name) => reader.IsDBNull(name) ? null : reader.Int32(name);
    public static long Int64(this SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name));
    public static long? NullableInt64(this SqliteDataReader reader, string name) => reader.IsDBNull(name) ? null : reader.Int64(name);
    public static bool IsDBNull(this SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name));
}
