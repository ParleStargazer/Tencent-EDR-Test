using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdrTest;

public sealed record ApiCloudImportRecord(
    string SchemaVersion,
    string ImportId,
    string RunId,
    string Status,
    string Source,
    string DeviceName,
    DateTimeOffset QueryStartUtc,
    DateTimeOffset? QueryEndUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ImportedAtUtc,
    string? FileName,
    string? ManifestFileName,
    string? Format,
    int? RecordCount,
    long? SizeBytes,
    string? Sha256,
    string? ErrorCode,
    string? Error);

internal sealed record ResolvedCloudImport(ApiCloudImportRecord Record, string CloudPath, string ManifestPath);

internal sealed class CloudExportAutomationConfig : IDisposable
{
    public required string Account { get; set; }
    public required string Password { get; set; }
    public required string DeviceName { get; init; }
    public required DateTimeOffset? RequestedStartTime { get; init; }
    public required int DelaySeconds { get; init; }

    public void Dispose()
    {
        Account = string.Empty;
        Password = string.Empty;
    }
}

internal sealed class CloudImportStore
{
    private const string RecordFileName = "cloud-import.json";
    private readonly string runsRoot;

    public CloudImportStore(string runsRoot) => this.runsRoot = Path.GetFullPath(runsRoot);

    public IReadOnlyList<ApiCloudImportRecord> List(string runDirectory)
    {
        runDirectory = EnsureRunDirectory(runDirectory);
        var root = Path.Combine(runDirectory, "import", "cloud");
        if (!Directory.Exists(root)) return [];
        var records = new List<ApiCloudImportRecord>();
        foreach (var path in Directory.EnumerateFiles(root, RecordFileName, SearchOption.AllDirectories))
        {
            try
            {
                var value = JsonSerializer.Deserialize<ApiCloudImportRecord>(File.ReadAllText(path), JsonDefaults.Options);
                if (value is null || !Guid.TryParse(value.ImportId, out _) || !string.Equals(value.SchemaVersion, "1.0", StringComparison.Ordinal)
                    || !string.Equals(value.RunId, new DirectoryInfo(runDirectory).Name, StringComparison.Ordinal)) continue;
                if (value.Status == "succeeded")
                {
                    var resolved = ResolveSuccessful(runDirectory, value.ImportId);
                    if (resolved is not null) records.Add(resolved.Record);
                }
                else if (value.Status == "failed")
                {
                    records.Add(value);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"忽略无法读取的云端导入记录 {path}：{exception.Message}");
            }
        }
        return records.OrderByDescending(value => value.ImportedAtUtc ?? value.CreatedAtUtc).ToArray();
    }

    public ResolvedCloudImport? ResolveSuccessful(string runDirectory, string importId)
    {
        if (!Guid.TryParse(importId, out _)) return null;
        runDirectory = EnsureRunDirectory(runDirectory);
        var directory = Path.GetFullPath(Path.Combine(runDirectory, "import", "cloud", importId));
        if (!IsWithin(directory, runDirectory)) return null;
        var recordPath = Path.Combine(directory, RecordFileName);
        if (!File.Exists(recordPath)) return null;
        ApiCloudImportRecord? record;
        try { record = JsonSerializer.Deserialize<ApiCloudImportRecord>(File.ReadAllText(recordPath), JsonDefaults.Options); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
        if (record is null || record.Status != "succeeded" || record.ImportId != importId
            || !string.Equals(record.RunId, new DirectoryInfo(runDirectory).Name, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(record.FileName) || string.IsNullOrWhiteSpace(record.ManifestFileName)) return null;
        string cloudPath;
        string manifestPath;
        try
        {
            cloudPath = Path.GetFullPath(Path.Combine(directory, record.FileName));
            manifestPath = Path.GetFullPath(Path.Combine(directory, record.ManifestFileName));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!IsWithin(cloudPath, directory) || !IsWithin(manifestPath, directory)
            || !File.Exists(cloudPath) || !File.Exists(manifestPath)) return null;
        try
        {
            var inspection = CloudExportFile.Inspect(cloudPath);
            if (inspection.RecordCount != record.RecordCount || inspection.SizeBytes != record.SizeBytes
                || !string.Equals(inspection.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
        return new ResolvedCloudImport(record, cloudPath, manifestPath);
    }

    public string CreateDirectory(string runDirectory, string importId)
    {
        if (!Guid.TryParse(importId, out _)) throw new ArgumentException("import_id 必须是 UUID。");
        runDirectory = EnsureRunDirectory(runDirectory);
        var directory = Path.GetFullPath(Path.Combine(runDirectory, "import", "cloud", importId));
        if (!IsWithin(directory, runDirectory)) throw new InvalidDataException("云端导入目录越出当前 run。");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Write(string importDirectory, ApiCloudImportRecord record)
    {
        importDirectory = Path.GetFullPath(importDirectory);
        if (!IsWithin(importDirectory, runsRoot)) throw new InvalidDataException("云端导入记录越出 runs 目录。");
        var path = Path.Combine(importDirectory, RecordFileName);
        var temporary = Path.Combine(importDirectory, $".{RecordFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonDefaults.Options), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string EnsureRunDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) || !IsWithin(fullPath, runsRoot))
            throw new InvalidDataException("无法在 runs 根目录中解析该轮次。");
        return fullPath;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}

internal sealed class TencentEdrCloudExportService
{
    private readonly LocalApiOptions options;
    private readonly CloudImportStore store;
    private readonly string automationScript;

    public TencentEdrCloudExportService(LocalApiOptions options, CloudImportStore store)
    {
        this.options = options;
        this.store = store;
        automationScript = Path.Combine(options.RepositoryRoot, "web", "automation", "tencent-edr-cloud-export.mjs");
    }

    public bool Available => File.Exists(options.NodePath) && File.Exists(automationScript)
        && File.Exists(Path.Combine(options.RepositoryRoot, "web", "node_modules", "playwright-core", "package.json"));

    public async Task<ApiCloudImportRecord> ExportAndImportAsync(
        RunResult run,
        CloudExportAutomationConfig config,
        DateTimeOffset queryStartUtc,
        CancellationToken cancellationToken)
    {
        var importId = Ids.NewUuid7();
        var importDirectory = store.CreateDirectory(run.RunDirectory, importId);
        var cloudPath = Path.Combine(importDirectory, "cloud.json");
        var manifestPath = Path.Combine(importDirectory, "cloud-manifest.json");
        var createdAt = DateTimeOffset.UtcNow;
        try
        {
            if (!Available)
                throw new CloudAutomationException("AUTOMATION_RUNTIME_UNAVAILABLE", "未找到 Node.js、Playwright Core 或自动化脚本；可继续手动导入。");
            await RunBrowserAutomationAsync(config, queryStartUtc, cloudPath, cancellationToken);
            var inspection = CloudExportFile.Inspect(cloudPath);
            var queryEndUtc = DateTimeOffset.UtcNow;
            WriteManifest(manifestPath, run.RunId, config.DeviceName, queryStartUtc, queryEndUtc, inspection);
            var succeeded = new ApiCloudImportRecord(
                "1.0", importId, run.RunId, "succeeded", "tencent_edr_browser_automation", config.DeviceName,
                queryStartUtc, queryEndUtc, createdAt, DateTimeOffset.UtcNow, Path.GetFileName(cloudPath),
                Path.GetFileName(manifestPath), inspection.Format, inspection.RecordCount, inspection.SizeBytes,
                inspection.Sha256, null, null);
            store.Write(importDirectory, succeeded);
            return succeeded;
        }
        catch (Exception exception)
        {
            var (code, message) = exception is CloudAutomationException automation
                ? (automation.Code, automation.Message)
                : exception is JsonException or InvalidDataException
                    ? ("CLOUD_LOG_PARSE_FAILED", "已下载文件，但不是可导入的 JSON 云端日志；可继续手动导入。")
                    : ("CLOUD_LOG_ACQUISITION_FAILED", "云端日志获取失败；请检查网络、登录验证或控制台页面变更，仍可手动导入。");
            var failed = new ApiCloudImportRecord(
                "1.0", importId, run.RunId, "failed", "tencent_edr_browser_automation", config.DeviceName,
                queryStartUtc, null, createdAt, null, File.Exists(cloudPath) ? Path.GetFileName(cloudPath) : null,
                null, null, null, File.Exists(cloudPath) ? new FileInfo(cloudPath).Length : null, null, code, message);
            try { store.Write(importDirectory, failed); } catch (Exception writeException) { Console.Error.WriteLine($"无法写入云端导入失败记录：{writeException.Message}"); }
            return failed;
        }
    }

    private async Task RunBrowserAutomationAsync(
        CloudExportAutomationConfig config,
        DateTimeOffset queryStartUtc,
        string cloudPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.NodePath,
            WorkingDirectory = Path.Combine(options.RepositoryRoot, "web"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(automationScript);
        using var process = Process.Start(startInfo) ?? throw new CloudAutomationException("BROWSER_START_FAILED", "无法启动云端导出自动化进程。");
        var payload = new JsonObject
        {
            ["account"] = config.Account,
            ["password"] = config.Password,
            ["device_name"] = config.DeviceName,
            ["query_start_local"] = queryStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["download_path"] = Path.GetFullPath(cloudPath),
            ["timeout_ms"] = 300_000,
        }.ToJsonString(JsonDefaults.Options);
        await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        payload = string.Empty;
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new CloudAutomationException("BROWSER_AUTOMATION_TIMEOUT", "云端导出自动化超过 6 分钟。");
        }
        var output = await outputTask;
        _ = await errorTask; // 只用于排空管道；不写入 run 日志，避免泄露页面内容。
        JsonObject? result = null;
        try { result = JsonNode.Parse(output)?.AsObject(); } catch (JsonException) { }
        if (process.ExitCode != 0 || result?["status"]?.GetValue<string>() != "succeeded")
        {
            var code = result?["code"]?.GetValue<string>() ?? "BROWSER_AUTOMATION_FAILED";
            var message = result?["message"]?.GetValue<string>() ?? "浏览器自动化未完成，可继续手动导入。";
            throw new CloudAutomationException(code, message);
        }
        var reportedPath = result["download_path"]?.GetValue<string>();
        if (!string.Equals(Path.GetFullPath(reportedPath ?? string.Empty), Path.GetFullPath(cloudPath), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(cloudPath))
            throw new CloudAutomationException("DOWNLOAD_NOT_FOUND", "浏览器报告导出完成，但当前 run 目录中没有下载文件。");
    }

    private static void WriteManifest(
        string path,
        string runId,
        string deviceName,
        DateTimeOffset queryStartUtc,
        DateTimeOffset queryEndUtc,
        CloudExportInspection inspection)
    {
        var manifest = new JsonObject
        {
            ["schema_version"] = "1.0",
            ["product"] = new JsonObject { ["vendor"] = "Tencent", ["name"] = "Tencent EDR", ["version"] = null, ["export_format_version"] = null },
            ["exported_at_utc"] = Values.Utc(DateTimeOffset.UtcNow),
            ["query_window"] = new JsonObject { ["start_utc"] = Values.Utc(queryStartUtc), ["end_utc"] = Values.Utc(queryEndUtc) },
            ["host_filter"] = new JsonObject { ["hostname"] = deviceName, ["host_id"] = null, ["description"] = $"自动化绑定本地 run {runId}" },
            ["event_filters"] = new JsonArray("全部事件"),
            ["source_files"] = new JsonArray(new JsonObject
            {
                ["path"] = Path.GetFileName(inspection.Path),
                ["sha256"] = inspection.Sha256,
                ["size_bytes"] = inspection.SizeBytes,
            }),
            ["notes"] = "由本地 Playwright 自动化从腾讯 EDR 控制台下载；帐号和密码未写入此文件。",
        };
        File.WriteAllText(path, manifest.ToJsonString(JsonDefaults.Options), new UTF8Encoding(false));
    }
}

internal sealed class CloudAutomationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
