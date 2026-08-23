using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EdrTest;

public sealed record LocalApiOptions(
    string Host,
    int Port,
    string RepositoryRoot,
    string SamplesRoot,
    string RunsDirectory,
    string ImportDirectory,
    string ReportsDirectory,
    IReadOnlyList<string> AllowedOrigins,
    string? Token,
    string NodePath);

public sealed class ApiRunStartRequest
{
    public List<string> CapabilityIds { get; init; } = [];
    public string? Name { get; init; }
    public string? EnvironmentId { get; init; }
    public bool AllowHighRisk { get; init; }
    public int InterCapabilityDelaySeconds { get; init; } = 3;
    public int InterSubtestDelayMilliseconds { get; init; } = SubtestTiming.DefaultDelayMilliseconds;
    public ApiCloudAutomationStartRequest? CloudAutomation { get; init; }
}

public sealed class ApiCloudAutomationStartRequest
{
    public bool Enabled { get; init; }
    public string? Account { get; init; }
    public string? Password { get; init; }
    public string? DeviceName { get; init; }
    public DateTimeOffset? LogStartTime { get; init; }
    public int DelaySeconds { get; init; } = 30;
    public bool DebugMode { get; init; }
}

public sealed record ApiCloudProgressEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Stage,
    string Message,
    int Progress,
    bool Detailed);

public sealed record ApiCloudAcquisitionSnapshot(
    bool Requested,
    string Status,
    bool DebugMode,
    string? DeviceName,
    DateTimeOffset? QueryStartUtc,
    int? DelaySeconds,
    int? WaitRemainingSeconds,
    int Progress,
    string? Stage,
    string? StageMessage,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<ApiCloudProgressEntry> Logs,
    bool DebugLogAvailable,
    ApiCloudImportRecord? Import,
    string? Error);

public sealed record ApiCapability(
    string CapabilityId,
    string Version,
    string NameZh,
    string NameEn,
    string RiskLevel,
    string RequiredPrivilege,
    IReadOnlyList<string> Programs);

public sealed record ApiBaselineRequirement(
    string RequirementId,
    string Scope,
    string TitleZh,
    string Field,
    string Operator,
    string Severity);

public sealed record ApiBaseline(
    string BaselineId,
    string CapabilityId,
    string CapabilityVersion,
    string Title,
    string RiskLevel,
    string Version,
    IReadOnlyList<ApiBaselineRequirement> Requirements);

public sealed record ApiMapping(string ProfileId, string Vendor, string Product, string Description);

public sealed record ApiRunCapabilityStep(
    string CapabilityId,
    string NameZh,
    int Sequence,
    string Status,
    string StatusLabel,
    JsonObject? LocalEvidence);

public sealed record ApiRunLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Source,
    string Message,
    string? CapabilityId,
    bool Important);

public sealed record ApiRunSnapshot(
    string OperationId,
    string? RunId,
    string Name,
    string Status,
    int Progress,
    string Phase,
    IReadOnlyList<string> CapabilityIds,
    bool AllowHighRisk,
    int InterCapabilityDelaySeconds,
    int InterSubtestDelayMilliseconds,
    int CompletedCapabilities,
    string? CurrentCapabilityId,
    int? WaitRemainingSeconds,
    IReadOnlyList<ApiRunCapabilityStep> Steps,
    IReadOnlyList<ApiRunLogEntry> Logs,
    IReadOnlyList<ApiRunLogEntry> Highlights,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? DatabaseName,
    bool LocalExportAvailable,
    ApiCloudAcquisitionSnapshot CloudAcquisition,
    string? Error);

public sealed record ApiComparisonProgressSnapshot(
    string ComparisonId,
    string Status,
    double Progress,
    int CompletedCapabilities,
    int TotalCapabilities,
    string? CapabilityId,
    string? DisplayNameZh,
    string? ValidationStatus,
    DateTimeOffset UpdatedAtUtc,
    string? Error);

public static class LocalApiService
{
    private const long MaximumUploadBytes = CloudExportFile.MaximumBytes;
    private static readonly JsonSerializerOptions ApiJson = new(JsonDefaults.Options);

    public static async Task<int> RunAsync(LocalApiOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        Directory.CreateDirectory(options.RunsDirectory);
        Directory.CreateDirectory(options.ImportDirectory);
        Directory.CreateDirectory(options.ReportsDirectory);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
        builder.Services.ConfigureHttpJsonOptions(value =>
        {
            value.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            value.SerializerOptions.DictionaryKeyPolicy = null;
            value.SerializerOptions.PropertyNameCaseInsensitive = true;
            value.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.Configure<FormOptions>(value => value.MultipartBodyLengthLimit = MaximumUploadBytes);

        var catalog = new LocalApiCatalog(options);
        var cloudImportStore = new CloudImportStore(options.RunsDirectory);
        var cloudExporter = new TencentEdrCloudExportService(options, cloudImportStore);
        var coordinator = new ApiRunCoordinator(options, catalog, cloudImportStore, cloudExporter);
        var comparisonCoordinator = new ApiComparisonCoordinator();
        var app = builder.Build();
        ConfigureSecurity(app, options);

        app.MapGet("/api/health", () => Results.Json(new
        {
            status = "ok",
            version = EdrTestVersion.Current,
            server_time_utc = Values.Utc(DateTimeOffset.UtcNow),
            capabilities_available = catalog.Capabilities.Count,
            authentication = string.IsNullOrWhiteSpace(options.Token) ? "origin-only" : "local-token",
            host_name = Environment.MachineName,
            cloud_automation_available = cloudExporter.Available,
        }, ApiJson));

        app.MapGet("/api/capabilities", () => Results.Json(catalog.Capabilities, ApiJson));
        app.MapGet("/api/baselines", () => Results.Json(catalog.Baselines, ApiJson));
        app.MapGet("/api/mappings", () => Results.Json(catalog.Mappings, ApiJson));
        app.MapGet("/api/runs", () => Results.Json(coordinator.List(), ApiJson));

        app.MapPost("/api/runs", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ApiRunStartRequest>(ApiJson, context.RequestAborted);
            if (request is null) return ApiError(400, "请求正文必须是 JSON 对象。");
            try
            {
                var state = coordinator.Start(request);
                return Results.Json(state, ApiJson, statusCode: StatusCodes.Status202Accepted);
            }
            catch (ApiRequestException exception)
            {
                return ApiError(exception.StatusCode, exception.Message);
            }
        });

        app.MapGet("/api/runs/{operationId}", (string operationId) =>
        {
            var state = coordinator.Get(operationId);
            return state is null ? ApiError(404, "找不到测试轮次。") : Results.Json(state, ApiJson);
        });

        app.MapPost("/api/runs/{operationId}/cancel", (string operationId) =>
        {
            var state = coordinator.Cancel(operationId);
            return state is null ? ApiError(409, "轮次不存在或已进入终态。") : Results.Json(state, ApiJson);
        });

        app.MapGet("/api/runs/{operationId}/local-export", (string operationId) =>
        {
            var path = coordinator.ResolveLocalExport(operationId);
            return path is null
                ? ApiError(404, "该轮次还没有可用的本地导出。")
                : Results.File(path, "application/json; charset=utf-8", $"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))}-local-run.json");
        });

        app.MapGet("/api/runs/{operationId}/cloud-imports", (string operationId) =>
        {
            var imports = coordinator.ListCloudImports(operationId);
            return imports is null ? ApiError(404, "找不到测试轮次。") : Results.Json(imports, ApiJson);
        });
        app.MapGet("/api/runs/{operationId}/cloud-imports/{importId}/debug-log", (string operationId, string importId) =>
        {
            var path = coordinator.ResolveCloudDebugLog(operationId, importId);
            return path is null
                ? ApiError(404, "该云端导入没有可用的调试日志。")
                : Results.File(path, "application/x-ndjson; charset=utf-8", $"{importId}-cloud-automation-debug.jsonl");
        });

        app.MapPost("/api/compare", (Func<HttpContext, Task<IResult>>)(context => CompareAsync(context, options, catalog, coordinator, comparisonCoordinator)));
        app.MapGet("/api/comparisons/{comparisonId}/progress", (string comparisonId) =>
        {
            var progress = comparisonCoordinator.Get(comparisonId);
            return progress is null ? ApiError(404, "找不到离线比较进度。") : Results.Json(progress, ApiJson);
        });
        app.MapGet("/api/reports/{comparisonId}/result", (string comparisonId) =>
            DownloadReport(options, comparisonId, "validation-result.json", "application/json; charset=utf-8", $"validation-{comparisonId}.json"));
        app.MapGet("/api/reports/{comparisonId}/conclusion", (string comparisonId) =>
            DownloadReport(options, comparisonId, "validation-conclusion.md", "text/markdown; charset=utf-8", $"validation-{comparisonId}-conclusion.md"));

        Console.WriteLine($"EdrTest 本地 API 已启动：http://{options.Host}:{options.Port}");
        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
        return 0;
    }

    private static void ConfigureSecurity(WebApplication app, LocalApiOptions options)
    {
        var allowedOrigins = options.AllowedOrigins.Select(NormalizeOrigin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        app.Use(async (context, next) =>
        {
            try
            {
                var requestHost = context.Request.Host.Host;
                if (!IsLoopbackHost(requestHost))
                {
                    await WriteErrorAsync(context, 400, "Host 必须是本机回环地址。");
                    return;
                }

                var origin = context.Request.Headers.Origin.ToString();
                if (!string.IsNullOrWhiteSpace(origin))
                {
                    var normalizedOrigin = NormalizeOrigin(origin);
                    if (!allowedOrigins.Contains(normalizedOrigin))
                    {
                        await WriteErrorAsync(context, 403, "请求来源不在本地允许列表中。");
                        return;
                    }
                    context.Response.Headers.AccessControlAllowOrigin = origin;
                    context.Response.Headers.Vary = HeaderNames.Origin;
                    context.Response.Headers.AccessControlAllowMethods = "GET,POST,OPTIONS";
                    context.Response.Headers.AccessControlAllowHeaders = "Content-Type,X-EDRTest-Token";
                }

                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }

                if (!context.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(options.Token)
                    && !string.Equals(context.Request.Headers["X-EDRTest-Token"], options.Token, StringComparison.Ordinal))
                {
                    await WriteErrorAsync(context, 401, "缺少或提供了无效的本地 API 令牌。");
                    return;
                }

                await next();
            }
            catch (BadHttpRequestException exception)
            {
                await WriteErrorAsync(context, 400, exception.Message);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                if (!context.Response.HasStarted) await WriteErrorAsync(context, 500, "本地 API 处理请求失败。");
            }
        });
    }

    private static async Task<IResult> CompareAsync(
        HttpContext context,
        LocalApiOptions options,
        LocalApiCatalog catalog,
        ApiRunCoordinator coordinator,
        ApiComparisonCoordinator comparisonCoordinator)
    {
        if (!context.Request.HasFormContentType) return ApiError(415, "比较接口需要 multipart/form-data。");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var cloudFile = form.Files.GetFile("cloud_file");
        var cloudImportId = form["cloud_import_id"].ToString().Trim();
        if (cloudFile is not null && !string.IsNullOrWhiteSpace(cloudImportId)) return ApiError(400, "cloud_file 与 cloud_import_id 只能提供一个。");
        if (cloudFile is null && string.IsNullOrWhiteSpace(cloudImportId)) return ApiError(400, "必须上传 cloud_file，或选择当前轮次已绑定的 cloud_import_id。");
        if (cloudFile is not null && (!ValidJsonExtension(cloudFile.FileName) || cloudFile.Length is <= 0 or > MaximumUploadBytes))
            return ApiError(400, "cloud_file 必须是 256 MB 以内的非空 .json 或 .jsonl 文件。");

        var localFile = form.Files.GetFile("local_file");
        var operationId = form["operation_id"].ToString();
        if (!string.IsNullOrWhiteSpace(cloudImportId) && (localFile is not null || string.IsNullOrWhiteSpace(operationId)))
            return ApiError(400, "自动绑定的云端日志只能与其 operation_id 对应的本地轮次比较。");
        if (localFile is not null && !string.IsNullOrWhiteSpace(operationId)) return ApiError(400, "local_file 与 operation_id 只能提供一个。");
        if (localFile is null && string.IsNullOrWhiteSpace(operationId)) return ApiError(400, "必须提供 local_file 或 operation_id。");
        if (localFile is not null && (!ValidJsonExtension(localFile.FileName) || localFile.Length is <= 0 or > MaximumUploadBytes))
        {
            return ApiError(400, "local_file 必须是 256 MB 以内的非空 JSON 文件。");
        }

        var mappingId = form["mapping_id"].ToString();
        if (string.IsNullOrWhiteSpace(mappingId)) mappingId = "tencent-edr-proc-events-v1";
        var mappingPath = catalog.ResolveMapping(mappingId);
        if (mappingPath is null) return ApiError(400, "未知 mapping_id。");
        if (!TryParseComparisonTimeParameter(
                form["strong_correlation_time_ms"].ToString(),
                CompareService.DefaultStrongCorrelationTimeMs,
                CompareService.MaximumStrongCorrelationTimeMs,
                "强关联时间",
                out var strongCorrelationTimeMs,
                out var strongCorrelationTimeError))
        {
            return ApiError(400, strongCorrelationTimeError!);
        }
        if (!TryParseComparisonTimeParameter(
                form["candidate_time_limit_ms"].ToString(),
                CompareService.DefaultCandidateTimeLimitMs,
                CompareService.MaximumCandidateTimeLimitMs,
                "无关联候选事件时间上限",
                out var candidateTimeLimitMs,
                out var candidateTimeLimitError))
        {
            return ApiError(400, candidateTimeLimitError!);
        }
        if (candidateTimeLimitMs < strongCorrelationTimeMs)
        {
            return ApiError(400, "无关联候选事件时间上限不能小于强关联时间。");
        }
        IReadOnlyDictionary<string, IReadOnlyList<string>> actionNameStandards;
        IReadOnlyDictionary<string, IReadOnlyList<string>> childFileCreateOpNameStandards;
        try
        {
            actionNameStandards = ParseEdrFieldStandards(
                form["action_name_standards"].ToString(),
                "action_name_standards",
                "Action.Name");
            childFileCreateOpNameStandards = ParseEdrFieldStandards(
                form["child_file_create_op_name_standards"].ToString(),
                "child_file_create_op_name_standards",
                "Child.FileCreateOpName");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return ApiError(400, exception.Message);
        }

        var comparisonId = form["comparison_id"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(comparisonId)) comparisonId = Ids.NewUuid7();
        else if (!Guid.TryParse(comparisonId, out _)) return ApiError(400, "comparison_id 必须是 UUID。");
        var importRoot = Path.Combine(options.ImportDirectory, comparisonId);
        var reportRoot = Path.Combine(options.ReportsDirectory, comparisonId);
        Directory.CreateDirectory(importRoot);
        Directory.CreateDirectory(reportRoot);
        string cloudPath;
        string? manifestPath = null;
        if (cloudFile is not null)
        {
            cloudPath = Path.Combine(importRoot, Path.GetExtension(cloudFile.FileName).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ? "cloud.jsonl" : "cloud.json");
            await SaveUploadAsync(cloudFile, cloudPath, context.RequestAborted);
        }
        else
        {
            var resolvedCloud = coordinator.ResolveCloudImport(operationId, cloudImportId);
            if (resolvedCloud is null) return ApiError(404, "找不到该轮次中可用的云端日志绑定，或文件完整性校验失败。");
            cloudPath = resolvedCloud.CloudPath;
            manifestPath = resolvedCloud.ManifestPath;
        }

        string localPath;
        if (localFile is not null)
        {
            localPath = Path.Combine(importRoot, "local-run.json");
            await SaveUploadAsync(localFile, localPath, context.RequestAborted);
        }
        else
        {
            var resolvedLocalPath = coordinator.ResolveLocalExport(operationId);
            if (resolvedLocalPath is null) return ApiError(404, "找不到 operation_id 对应的本地导出。");
            localPath = resolvedLocalPath;
        }

        var manifestFile = form.Files.GetFile("cloud_manifest");
        if (manifestFile is not null)
        {
            if (!string.IsNullOrWhiteSpace(cloudImportId)) return ApiError(400, "自动绑定日志已包含云端导出清单，不能再上传 cloud_manifest。");
            if (!ValidJsonExtension(manifestFile.FileName) || manifestFile.Length is <= 0 or > MaximumUploadBytes)
            {
                return ApiError(400, "cloud_manifest 必须是 256 MB 以内的非空 JSON 文件。");
            }
            manifestPath = Path.Combine(importRoot, "cloud-manifest.json");
            await SaveUploadAsync(manifestFile, manifestPath, context.RequestAborted);
        }

        var baselineIds = form["baseline_id"].Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
        IReadOnlyList<string> baselinePaths;
        if (baselineIds.Length == 0)
        {
            baselinePaths = catalog.ResolveBaselinesForLocalExport(localPath);
        }
        else
        {
            var resolved = baselineIds.Select(catalog.ResolveBaseline).ToArray();
            if (resolved.Any(path => path is null)) return ApiError(400, "baseline_id 包含未知值。");
            baselinePaths = resolved.Select(path => path!).ToArray();
        }
        if (baselinePaths.Count == 0) return ApiError(400, "本地导出中的能力没有可用 BASELINE。");

        var outputPath = Path.Combine(reportRoot, "validation-result.json");
        var conclusionPath = Path.Combine(reportRoot, "validation-conclusion.md");
        ApiComparisonProgressState progressState;
        try
        {
            progressState = comparisonCoordinator.Start(comparisonId);
        }
        catch (ApiRequestException exception)
        {
            return ApiError(exception.StatusCode, exception.Message);
        }
        try
        {
            var result = CompareService.Compare(new CompareRequest(
                localPath,
                [cloudPath],
                mappingPath,
                baselinePaths,
                outputPath,
                manifestPath,
                conclusionPath,
                comparisonId,
                actionNameStandards,
                childFileCreateOpNameStandards,
                strongCorrelationTimeMs,
                candidateTimeLimitMs,
                progressState.Apply));
            progressState.Complete();
            return Results.Json(result, ApiJson);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
        {
            progressState.Fail(exception.Message);
            return ApiError(400, exception.Message);
        }
        catch (Exception exception)
        {
            progressState.Fail(exception.Message);
            throw;
        }
    }

    private static async Task SaveUploadAsync(IFormFile file, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous);
        await file.CopyToAsync(stream, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseEdrFieldStandards(
        string text,
        string formField,
        string rawField)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (text.Length > 65_536) throw new InvalidDataException($"{formField} 超过 64 KB。");
        var root = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException($"{formField} 必须是 JSON 对象。");
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (capabilityId, node) in root)
        {
            if (node is not JsonArray values || values.Any(value => value is not JsonValue))
            {
                throw new InvalidDataException($"能力 {capabilityId} 的 {rawField} 标准必须是字符串数组。");
            }
            try
            {
                result[capabilityId] = values.Select(value => value!.GetValue<string>()).ToArray();
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                throw new InvalidDataException($"能力 {capabilityId} 的 {rawField} 标准必须是字符串数组。");
            }
        }
        return result;
    }

    private static bool TryParseComparisonTimeParameter(
        string text,
        int defaultValue,
        int maximumValue,
        string displayName,
        out int value,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = defaultValue;
            error = null;
            return true;
        }
        if (!int.TryParse(text, out value) || value is < 1 || value > maximumValue)
        {
            error = $"{displayName}必须是 1..{maximumValue} ms 的整数。";
            return false;
        }
        error = null;
        return true;
    }

    private static IResult DownloadReport(LocalApiOptions options, string comparisonId, string fileName, string contentType, string downloadName)
    {
        if (!Guid.TryParse(comparisonId, out _)) return ApiError(400, "comparison_id 必须是 UUID。");
        var path = Path.Combine(options.ReportsDirectory, comparisonId, fileName);
        return File.Exists(path) ? Results.File(path, contentType, downloadName) : ApiError(404, "找不到比较报告。");
    }

    private static IResult ApiError(int statusCode, string message) => Results.Json(new { error = message }, ApiJson, statusCode: statusCode);

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }, ApiJson));
    }

    private static bool ValidJsonExtension(string fileName) => Path.GetExtension(fileName) is ".json" or ".jsonl" or ".JSON" or ".JSONL";

    private static string NormalizeOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"无效的 allowed-origin：{value}");
        }
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1"
        || host == "[::1]";

    private static void ValidateOptions(LocalApiOptions options)
    {
        if (!IsLoopbackHost(options.Host)) throw new ArgumentException("本地 API 只允许绑定 localhost、127.0.0.1 或 ::1。");
        if (options.Port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "API 端口必须在 1024..65535 内。");
        if (!Directory.Exists(options.RepositoryRoot)) throw new DirectoryNotFoundException($"找不到仓库目录：{options.RepositoryRoot}");
        if (!Directory.Exists(options.SamplesRoot)) Directory.CreateDirectory(options.SamplesRoot);
        foreach (var origin in options.AllowedOrigins) _ = NormalizeOrigin(origin);
        if (options.Token is { Length: > 0 and < 32 }) throw new ArgumentException("本地 API 令牌至少需要 32 个字符。");
    }
}

internal sealed class LocalApiCatalog
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
    private readonly Dictionary<string, CapabilityPackage> packages;
    private readonly Dictionary<string, (ApiBaseline Value, string Path)> baselines;
    private readonly Dictionary<string, (ApiMapping Value, string Path)> mappings;

    public LocalApiCatalog(LocalApiOptions options)
    {
        packages = CapabilityCatalog.Discover(options.SamplesRoot).ToDictionary(value => value.Manifest.CapabilityId, StringComparer.Ordinal);
        Capabilities = packages.Values.Select(package => new ApiCapability(
                package.Manifest.CapabilityId,
                package.Manifest.Version,
                package.Manifest.DisplayNameZh ?? package.Manifest.DisplayName ?? package.Manifest.CapabilityId,
                package.Manifest.DisplayNameEn ?? package.Manifest.CapabilityId,
                package.Manifest.RiskLevel,
                package.Manifest.RequiredPrivilege,
                new[] { package.Manifest.Controller.Executable }.Concat(package.Manifest.Participants.Select(value => value.Executable)).ToArray()))
            .OrderBy(value => value.CapabilityId, StringComparer.Ordinal)
            .ToArray();

        var baselineRoot = Path.Combine(options.RepositoryRoot, "baselines");
        baselines = Directory.Exists(baselineRoot)
            ? Directory.EnumerateFiles(baselineRoot, "*.yaml", SearchOption.AllDirectories)
                .Select(path => (Path: Path.GetFullPath(path), Value: Yaml.Deserialize<BaselineDefinition>(File.ReadAllText(path))))
                .Where(value => value.Value is not null)
                .ToDictionary(
                    value => value.Value.BaselineId,
                    value => (new ApiBaseline(
                        value.Value.BaselineId,
                        value.Value.Capability.Id,
                        value.Value.Capability.Version,
                        value.Value.Title ?? value.Value.BaselineId,
                        value.Value.RiskLevel ?? "L0",
                        value.Value.Version,
                        BuildRequirements(value.Value)), value.Path),
                    StringComparer.Ordinal)
            : new Dictionary<string, (ApiBaseline, string)>(StringComparer.Ordinal);
        Baselines = baselines.Values.Select(value => value.Value).OrderBy(value => value.BaselineId, StringComparer.Ordinal).ToArray();

        var mappingRoot = Path.Combine(options.RepositoryRoot, "mappings");
        mappings = Directory.Exists(mappingRoot)
            ? Directory.EnumerateFiles(mappingRoot, "*.yaml", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: Path.GetFullPath(path), Value: Yaml.Deserialize<MappingProfile>(File.ReadAllText(path))))
                .Where(value => value.Value is not null)
                .ToDictionary(
                    value => value.Value.ProfileId,
                    value => (new ApiMapping(value.Value.ProfileId, value.Value.Vendor ?? "Unknown", value.Value.Product ?? "Unknown", value.Value.Description ?? string.Empty), value.Path),
                    StringComparer.Ordinal)
            : new Dictionary<string, (ApiMapping, string)>(StringComparer.Ordinal);
        Mappings = mappings.Values.Select(value => value.Value).OrderBy(value => value.ProfileId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ApiCapability> Capabilities { get; }
    public IReadOnlyList<ApiBaseline> Baselines { get; }
    public IReadOnlyList<ApiMapping> Mappings { get; }
    public CapabilityPackage? ResolvePackage(string capabilityId) => packages.GetValueOrDefault(capabilityId);
    public string? ResolveBaseline(string baselineId) => baselines.TryGetValue(baselineId, out var value) ? value.Path : null;
    public string? ResolveMapping(string mappingId) => mappings.TryGetValue(mappingId, out var value) ? value.Path : null;

    public IReadOnlyList<string> ResolveBaselinesForLocalExport(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("本地导出必须是 JSON 对象。");
        var capabilityIds = root["capabilities"]?.AsArray()
            .Select(value => value?["capability_id"]?.GetValue<string>())
            .Where(value => value is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal) ?? [];
        return baselines.Values
            .Where(value => capabilityIds.Contains(value.Value.CapabilityId))
            .Select(value => value.Path)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ApiBaselineRequirement> BuildRequirements(BaselineDefinition baseline)
    {
        var requirements = baseline.LocalRequirements.Select((item, index) => new ApiBaselineRequirement(
            $"local-{index + 1}",
            "local",
            CompareService.RequirementTitle(item.Field, item.Operator),
            item.Field,
            item.Operator,
            item.Severity));
        var cloud = baseline.CloudExpectations.SelectMany(expectation =>
            new[]
            {
                new ApiBaselineRequirement(
                    $"{expectation.Id}-cardinality",
                    "cloud",
                    $"必须找到至少 {expectation.Cardinality.Min} 条 {CompareService.EventActionTitle(expectation.EventType, expectation.EventActions)} EDR 事件",
                    "event.count",
                    "range",
                    "required"),
                new ApiBaselineRequirement(
                    $"{expectation.Id}-time-difference",
                    "cloud",
                    $"EDR 事件与本地行为时间差必须不超过 {expectation.Correlation?.MaxTimeDifferenceMs ?? baseline.Correlation.MaxTimeDifferenceMs} ms",
                    "event.time_difference_ms",
                    "range",
                    "required"),
            }.Concat(expectation.Assertions.Select((item, index) => new ApiBaselineRequirement(
                $"{expectation.Id}-{index + 1}",
                "cloud",
                CompareService.RequirementTitle(item.Field, item.Operator),
                item.Field,
                item.Operator,
                item.Severity))));
        return requirements.Concat(cloud).ToArray();
    }
}

internal sealed class ApiRunCoordinator
{
    private readonly LocalApiOptions options;
    private readonly LocalApiCatalog catalog;
    private readonly CloudImportStore cloudImportStore;
    private readonly TencentEdrCloudExportService cloudExporter;
    private readonly ConcurrentDictionary<string, ApiRunState> states = new(StringComparer.Ordinal);

    public ApiRunCoordinator(
        LocalApiOptions options,
        LocalApiCatalog catalog,
        CloudImportStore cloudImportStore,
        TencentEdrCloudExportService cloudExporter)
    {
        this.options = options;
        this.catalog = catalog;
        this.cloudImportStore = cloudImportStore;
        this.cloudExporter = cloudExporter;
    }

    public ApiRunSnapshot Start(ApiRunStartRequest request)
    {
        var capabilityIds = request.CapabilityIds.Distinct(StringComparer.Ordinal).ToArray();
        if (capabilityIds.Length == 0) throw new ApiRequestException(400, "至少选择一个能力。");
        if (capabilityIds.Length != request.CapabilityIds.Count) throw new ApiRequestException(400, "capability_ids 不能重复。");
        if (request.InterCapabilityDelaySeconds is < 0 or > 300) throw new ApiRequestException(400, "能力间等待时间必须在 0..300 秒内。");
        if (request.InterSubtestDelayMilliseconds is < 0 or > SubtestTiming.MaximumDelayMilliseconds)
            throw new ApiRequestException(400, $"子测试间等待时间必须在 0..{SubtestTiming.MaximumDelayMilliseconds} 毫秒内。");
        var packages = capabilityIds.Select(id => catalog.ResolvePackage(id) ?? throw new ApiRequestException(400, $"能力样本不可用：{id}")).ToArray();
        if (!request.AllowHighRisk && packages.Any(value => value.Manifest.RiskLevel is "L2" or "L3"))
        {
            throw new ApiRequestException(409, "所选能力包含 L2/L3 项，请显式确认高风险执行。");
        }

        var cloudConfig = BuildCloudAutomationConfig(request.CloudAutomation);
        var state = new ApiRunState(
            Ids.NewUuid7(),
            request.Name?.Trim() is { Length: > 0 } name ? name : "未命名验证轮次",
            packages,
            request.AllowHighRisk,
            request.InterCapabilityDelaySeconds,
            request.InterSubtestDelayMilliseconds,
            DateTimeOffset.UtcNow,
            cloudConfig);
        if (!states.TryAdd(state.OperationId, state))
        {
            cloudConfig?.Dispose();
            throw new InvalidOperationException("无法创建唯一操作 ID。");
        }
        _ = Task.Run(() => ExecuteAsync(state, packages, request.EnvironmentId, cloudConfig));
        return state.Snapshot();
    }

    public ApiRunSnapshot? Get(string operationId)
    {
        if (states.TryGetValue(operationId, out var state)) return state.Snapshot();
        return ReadHistoricalRuns().FirstOrDefault(value => value.OperationId == operationId || value.RunId == operationId);
    }

    public ApiRunSnapshot? Cancel(string operationId)
    {
        if (!states.TryGetValue(operationId, out var state) || !state.Cancel()) return null;
        return state.Snapshot();
    }

    public IReadOnlyList<ApiRunSnapshot> List()
    {
        var current = states.Values.Select(value => value.Snapshot()).ToArray();
        var currentRunIds = current.Select(value => value.RunId).Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        return current.Concat(ReadHistoricalRuns().Where(value => value.RunId is null || !currentRunIds.Contains(value.RunId)))
            .OrderByDescending(value => value.StartedAtUtc)
            .Take(50)
            .ToArray();
    }

    public string? ResolveLocalExport(string operationId)
    {
        if (states.TryGetValue(operationId, out var state) && state.LocalExportPath is { } current && File.Exists(current)) return current;
        var historical = ReadHistoricalRunFiles().FirstOrDefault(value => value.RunId == operationId);
        return historical?.ExportPath;
    }

    public IReadOnlyList<ApiCloudImportRecord>? ListCloudImports(string operationId)
    {
        var runDirectory = ResolveRunDirectory(operationId);
        return runDirectory is null ? null : cloudImportStore.List(runDirectory);
    }

    public ResolvedCloudImport? ResolveCloudImport(string operationId, string importId)
    {
        var runDirectory = ResolveRunDirectory(operationId);
        return runDirectory is null ? null : cloudImportStore.ResolveSuccessful(runDirectory, importId);
    }

    public string? ResolveCloudDebugLog(string operationId, string importId)
    {
        var runDirectory = ResolveRunDirectory(operationId);
        return runDirectory is null ? null : cloudImportStore.ResolveDebugLog(runDirectory, importId);
    }

    private string? ResolveRunDirectory(string operationId)
    {
        if (states.TryGetValue(operationId, out var state) && state.RunDirectory is { } current && Directory.Exists(current)) return current;
        var historical = ReadHistoricalRunFiles().FirstOrDefault(value => value.RunId == operationId);
        return historical?.RunDirectory;
    }

    private async Task ExecuteAsync(
        ApiRunState state,
        IReadOnlyList<CapabilityPackage> packages,
        string? environmentId,
        CloudExportAutomationConfig? cloudConfig)
    {
        state.MarkRunning();
        try
        {
            RunResult result;
            try
            {
                result = await new RunnerService().RunAsync(new RunRequest(
                    packages.Select(value => value.ManifestPath).ToArray(),
                    options.RunsDirectory,
                    null,
                    state.AllowHighRisk,
                    state.Name,
                    environmentId,
                    state.InterCapabilityDelaySeconds,
                    state.ApplyProgress,
                    state.InterSubtestDelayMilliseconds), state.CancellationToken);
                state.Complete(result);
            }
            catch (OperationCanceledException)
            {
                state.MarkCancelled();
                if (cloudConfig is not null) state.FailCloudAcquisition("本地轮次已取消，未启动云端日志获取。");
                return;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                state.Fail(exception.Message);
                if (cloudConfig is not null) state.FailCloudAcquisition("本地轮次未正常完成，未启动云端日志获取。");
                return;
            }

            if (cloudConfig is null) return;
            try
            {
                var queryStartUtc = cloudConfig.RequestedStartTime ?? state.StartedAt.AddSeconds(-10);
                state.BeginCloudWait(queryStartUtc);
                for (var remaining = cloudConfig.DelaySeconds; remaining > 0; remaining--)
                {
                    state.UpdateCloudWaitRemaining(remaining);
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                state.BeginCloudAcquisition();
                var import = await cloudExporter.ExportAndImportAsync(result, cloudConfig, queryStartUtc, state.ApplyCloudProgress, CancellationToken.None);
                state.CompleteCloudAcquisition(import, cloudImportStore.ResolveDebugLog(result.RunDirectory, import.ImportId) is not null);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                state.FailCloudAcquisition("云端日志获取失败；本地测试结果不受影响，可稍后手动导入。");
            }
        }
        finally
        {
            cloudConfig?.Dispose();
        }
    }

    private static CloudExportAutomationConfig? BuildCloudAutomationConfig(ApiCloudAutomationStartRequest? request)
    {
        if (request is null || !request.Enabled) return null;
        var account = request.Account?.Trim();
        var password = request.Password;
        var deviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? Environment.MachineName : request.DeviceName.Trim();
        if (string.IsNullOrWhiteSpace(account) || account.Length > 512 || account.Any(char.IsControl))
            throw new ApiRequestException(400, "启用云端自动获取时必须提供有效的子账号。");
        if (string.IsNullOrEmpty(password) || password.Length > 4096 || password.Any(char.IsControl))
            throw new ApiRequestException(400, "启用云端自动获取时必须提供有效的密码。");
        if (deviceName.Length > 255 || deviceName.Any(char.IsControl))
            throw new ApiRequestException(400, "设备名称无效。");
        if (request.DelaySeconds is < 0 or > 3600)
            throw new ApiRequestException(400, "云端日志等待时间必须在 0..3600 秒内。");
        return new CloudExportAutomationConfig
        {
            Account = account,
            Password = password,
            DeviceName = deviceName,
            RequestedStartTime = request.LogStartTime,
            DelaySeconds = request.DelaySeconds,
            DebugMode = request.DebugMode,
        };
    }

    private IReadOnlyList<ApiRunSnapshot> ReadHistoricalRuns() => ReadHistoricalRunFiles().Select(value => value.Snapshot).ToArray();

    private IReadOnlyList<HistoricalRun> ReadHistoricalRunFiles()
    {
        if (!Directory.Exists(options.RunsDirectory)) return [];
        var results = new List<HistoricalRun>();
        foreach (var path in Directory.EnumerateFiles(options.RunsDirectory, "local-run.json", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                var run = root?["run"]?.AsObject();
                var runId = run?["run_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(runId)) continue;
                var status = run?["status"]?.GetValue<string>() ?? "UNKNOWN";
                var capabilityNodes = root?["capabilities"]?.AsArray().Select(value => value?.AsObject()).Where(value => value is not null).Cast<JsonObject>().ToArray() ?? [];
                var capabilityIds = capabilityNodes.Select(value => value["capability_id"]?.GetValue<string>()).Where(value => value is not null).Cast<string>().ToArray();
                var capabilityByCaseRunId = capabilityNodes
                    .Where(value => value["case_run_id"] is not null && value["capability_id"] is not null)
                    .ToDictionary(
                        value => value["case_run_id"]!.GetValue<string>(),
                        value => value["capability_id"]!.GetValue<string>(),
                        StringComparer.Ordinal);
                var logs = root?["execution_logs"]?.AsArray()
                    .Select(value => value?.AsObject())
                    .Where(value => value is not null)
                    .Cast<JsonObject>()
                    .Select(value =>
                    {
                        var caseRunId = value["case_run_id"]?.GetValue<string>();
                        var level = value["level"]?.GetValue<string>() ?? "info";
                        var phase = value["phase"]?.GetValue<string>() ?? "runner";
                        var capabilityId = caseRunId is not null && capabilityByCaseRunId.TryGetValue(caseRunId, out var mappedCapabilityId)
                            ? mappedCapabilityId
                            : null;
                        var rawMessage = value["message"]?.GetValue<string>() ?? string.Empty;
                        var displayMessage = phase == "controller.stdout"
                            ? ControllerOutputFormatter.FormatPersistedOutput(rawMessage)
                            : rawMessage;
                        return new ApiRunLogEntry(
                            DateTimeOffset.Parse(value["timestamp_utc"]?.GetValue<string>() ?? throw new InvalidDataException()),
                            level,
                            phase,
                            displayMessage,
                            capabilityId,
                            level is "warning" or "error" or "critical" || phase is "precheck");
                    })
                    .ToArray() ?? [];
                var steps = capabilityNodes.Select((value, index) =>
                {
                    var capabilityStatus = value["status"]?.GetValue<string>() ?? "UNKNOWN";
                    return new ApiRunCapabilityStep(
                        value["capability_id"]?.GetValue<string>() ?? $"unknown-{index + 1}",
                        value["display_name_zh"]?.GetValue<string>() ?? value["capability_id"]?.GetValue<string>() ?? "未知能力",
                        index + 1,
                        StepStatus(capabilityStatus),
                        StepStatusLabel(capabilityStatus),
                        BuildLocalEvidence(root!, value));
                }).ToArray();
                var started = DateTimeOffset.Parse(run?["started_at_utc"]?.GetValue<string>() ?? throw new InvalidDataException());
                var endedText = run?["ended_at_utc"]?.GetValue<string>();
                var runDirectory = Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName;
                var databasePath = Path.Combine(runDirectory, $"{runId}.db");
                var latestCloudImport = cloudImportStore.List(runDirectory).FirstOrDefault();
                var cloudProgressLogs = latestCloudImport is null
                    ? []
                    : cloudImportStore.ReadDebugProgressEntries(runDirectory, latestCloudImport.ImportId);
                var latestCloudProgress = cloudProgressLogs.LastOrDefault();
                var debugLogAvailable = latestCloudImport is not null
                    && cloudImportStore.ResolveDebugLog(runDirectory, latestCloudImport.ImportId) is not null;
                var cloudAcquisition = latestCloudImport is null
                    ? new ApiCloudAcquisitionSnapshot(
                        false, "not_requested", false, null, null, null, null, 0, null, null, null, [], false, null, null)
                    : new ApiCloudAcquisitionSnapshot(
                        true,
                        latestCloudImport.Status,
                        debugLogAvailable,
                        latestCloudImport.DeviceName,
                        latestCloudImport.QueryStartUtc,
                        null,
                        null,
                        latestCloudImport.Status == "succeeded" ? 100 : latestCloudProgress?.Progress ?? 0,
                        latestCloudProgress?.Stage ?? (latestCloudImport.Status == "succeeded" ? "completed" : null),
                        latestCloudProgress?.Message ?? latestCloudImport.Error,
                        latestCloudProgress?.TimestampUtc ?? latestCloudImport.ImportedAtUtc ?? latestCloudImport.CreatedAtUtc,
                        cloudProgressLogs,
                        debugLogAvailable,
                        latestCloudImport,
                        latestCloudImport.Error);
                var interSubtestDelayMilliseconds = root?["local_facts"]?.AsArray()
                    .Select(value => value?.AsObject())
                    .FirstOrDefault(value => value?["key"]?.GetValue<string>() == "execution.inter_subtest_delay_ms")?["value"]
                    ?.GetValue<int>() ?? SubtestTiming.DefaultDelayMilliseconds;
                var snapshot = new ApiRunSnapshot(
                    runId,
                    runId,
                    run?["suite_id"]?.GetValue<string>() ?? $"历史轮次 {runId[..8]}",
                    status == "COMPLETED" ? "completed" : "completed_with_errors",
                    100,
                    status == "COMPLETED" ? "本地轮次已完成" : "本地轮次带错误完成",
                    capabilityIds,
                    false,
                    3,
                    interSubtestDelayMilliseconds,
                    steps.Count(value => value.Status is "passed" or "error" or "skipped" or "cancelled"),
                    null,
                    null,
                    steps,
                    logs,
                    logs.Where(value => value.Important).TakeLast(12).ToArray(),
                    started,
                    endedText is null ? null : DateTimeOffset.Parse(endedText),
                    File.Exists(databasePath) ? Path.GetFileName(databasePath) : null,
                    true,
                    cloudAcquisition,
                    status == "COMPLETED" ? null : status);
                results.Add(new HistoricalRun(runId, runDirectory, path, snapshot));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or InvalidDataException)
            {
                Console.Error.WriteLine($"忽略无法读取的历史轮次 {path}：{exception.Message}");
            }
        }
        return results;
    }

    private static string StepStatus(string capabilityStatus) => capabilityStatus switch
    {
        "LOCAL_PASS" => "passed",
        "SKIPPED" => "skipped",
        "ABORTED" => "cancelled",
        "SAMPLE_ERROR" or "CLEANUP_ERROR" => "error",
        _ => "pending",
    };

    private static string StepStatusLabel(string capabilityStatus) => capabilityStatus switch
    {
        "LOCAL_PASS" => "本地验证通过",
        "SKIPPED" => "已跳过",
        "ABORTED" => "已取消",
        "SAMPLE_ERROR" => "本地验证失败",
        "CLEANUP_ERROR" => "清理失败",
        _ => "状态未知",
    };

    private static JsonObject BuildLocalEvidence(JsonObject root, JsonObject capability)
    {
        var caseRunId = capability["case_run_id"]?.GetValue<string>() ?? throw new InvalidDataException("能力缺少 case_run_id。");
        var capabilityEvidence = new JsonObject
        {
            ["case_run_id"] = caseRunId,
            ["capability_id"] = capability["capability_id"]?.DeepClone(),
            ["status"] = capability["status"]?.DeepClone(),
            ["started_at_utc"] = capability["started_at_utc"]?.DeepClone(),
            ["ended_at_utc"] = capability["ended_at_utc"]?.DeepClone(),
            ["duration_ms"] = capability["duration_ms"]?.DeepClone(),
            ["observer_started_at_utc"] = capability["observation_window"]?["started_at_utc"]?.DeepClone(),
            ["observer_ended_at_utc"] = capability["observation_window"]?["ended_at_utc"]?.DeepClone(),
        };
        var programs = new JsonArray((root["programs"]?.AsArray()
            .Select(value => value?.AsObject())
            .Where(value => value?["case_run_id"]?.GetValue<string>() == caseRunId)
            .Select(value => (JsonNode)new JsonObject
            {
                ["role"] = value!["role"]?.DeepClone(),
                ["instance_index"] = value["instance_index"]?.DeepClone(),
                ["executable"] = value["executable"]?.DeepClone(),
                ["pid"] = value["pid"]?.DeepClone(),
                ["parent_pid"] = value["parent_pid"]?.DeepClone(),
                ["command_line"] = value["command_line"]?.DeepClone(),
                ["started_at_utc"] = value["started_at_utc"]?.DeepClone(),
                ["ended_at_utc"] = value["ended_at_utc"]?.DeepClone(),
                ["exit_code"] = value["exit_code"]?.DeepClone(),
                ["md5"] = value["md5"]?.DeepClone(),
                ["sha1"] = value["sha1"]?.DeepClone(),
                ["sha256"] = value["sha256"]?.DeepClone(),
            }).ToArray() ?? []));
        var facts = new JsonArray((root["local_facts"]?.AsArray()
            .Select(value => value?.AsObject())
            .Where(value => value?["case_run_id"]?.GetValue<string>() == caseRunId)
            .Select(value => (JsonNode)new JsonObject
            {
                ["field"] = $"facts.{value!["key"]?.GetValue<string>()}",
                ["value"] = value["value"]?.DeepClone(),
                ["observed_at_utc"] = value["observed_at_utc"]?.DeepClone(),
                ["source"] = value["source"]?.DeepClone(),
                ["confidence"] = value["confidence"]?.DeepClone(),
            }).ToArray() ?? []));
        return new JsonObject { ["capability"] = capabilityEvidence, ["programs"] = programs, ["facts"] = facts };
    }

    private sealed record HistoricalRun(string RunId, string RunDirectory, string ExportPath, ApiRunSnapshot Snapshot);
}

internal sealed class ApiRunState
{
    private readonly object sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<ApiRunCapabilityStep> steps;
    private readonly List<ApiRunLogEntry> logs = [];
    private string? runId;
    private string status = "queued";
    private int progress = 5;
    private string phase = "等待 Runner 调度";
    private int completedCapabilities;
    private string? currentCapabilityId;
    private int? waitRemainingSeconds;
    private DateTimeOffset? endedAt;
    private string? databaseName;
    private string? localExportPath;
    private string? runDirectory;
    private string? error;
    private readonly bool cloudRequested;
    private readonly string? cloudDeviceName;
    private readonly int? cloudDelaySeconds;
    private DateTimeOffset? cloudQueryStartUtc;
    private string cloudStatus;
    private int? cloudWaitRemainingSeconds;
    private ApiCloudImportRecord? cloudImport;
    private string? cloudError;
    private readonly bool cloudDebugMode;
    private int cloudProgress;
    private string? cloudStage;
    private string? cloudStageMessage;
    private DateTimeOffset? cloudUpdatedAtUtc;
    private readonly List<ApiCloudProgressEntry> cloudProgressLogs = [];
    private bool cloudDebugLogAvailable;

    public ApiRunState(
        string operationId,
        string name,
        IReadOnlyList<CapabilityPackage> packages,
        bool allowHighRisk,
        int interCapabilityDelaySeconds,
        int interSubtestDelayMilliseconds,
        DateTimeOffset startedAt,
        CloudExportAutomationConfig? cloudConfig)
    {
        OperationId = operationId;
        Name = name;
        CapabilityIds = packages.Select(value => value.Manifest.CapabilityId).ToArray();
        AllowHighRisk = allowHighRisk;
        InterCapabilityDelaySeconds = interCapabilityDelaySeconds;
        InterSubtestDelayMilliseconds = interSubtestDelayMilliseconds;
        StartedAt = startedAt;
        cloudRequested = cloudConfig is not null;
        cloudDeviceName = cloudConfig?.DeviceName;
        cloudDelaySeconds = cloudConfig?.DelaySeconds;
        cloudQueryStartUtc = cloudConfig?.RequestedStartTime;
        cloudDebugMode = cloudConfig?.DebugMode ?? false;
        cloudStatus = cloudRequested ? "pending" : "not_requested";
        cloudStage = cloudRequested ? "pending" : null;
        cloudStageMessage = cloudRequested ? "等待本地测试完成后启动云端日志获取。" : null;
        cloudUpdatedAtUtc = cloudRequested ? startedAt : null;
        steps = packages.Select((value, index) => new ApiRunCapabilityStep(
            value.Manifest.CapabilityId,
            value.Manifest.DisplayNameZh ?? value.Manifest.DisplayName ?? value.Manifest.CapabilityId,
            index + 1,
            "pending",
            "等待执行",
            null)).ToList();
        AddLog(new ApiRunLogEntry(startedAt, "info", "queue", $"轮次已进入队列，共 {steps.Count} 项能力。", null, true));
    }

    public string OperationId { get; }
    public string Name { get; }
    public IReadOnlyList<string> CapabilityIds { get; }
    public bool AllowHighRisk { get; }
    public int InterCapabilityDelaySeconds { get; }
    public int InterSubtestDelayMilliseconds { get; }
    public DateTimeOffset StartedAt { get; }
    public CancellationToken CancellationToken => cancellation.Token;
    public string? LocalExportPath { get { lock (sync) return localExportPath; } }
    public string? RunDirectory { get { lock (sync) return runDirectory; } }

    public void MarkRunning()
    {
        lock (sync)
        {
            status = "running";
            progress = 3;
            phase = "Runner 已启动，准备串行执行";
            AddLog(new ApiRunLogEntry(DateTimeOffset.UtcNow, "info", "runner",
                $"Runner 已启动：能力之间不会并行；同一能力的子测试间隔为 {InterSubtestDelayMilliseconds} ms。", null, true));
        }
    }

    public void ApplyProgress(RunProgressUpdate update)
    {
        lock (sync)
        {
            progress = Math.Clamp(update.Progress, progress, 100);
            phase = update.Message;
            currentCapabilityId = update.Kind is "run_completed" or "export_completed" ? null : update.CapabilityId ?? currentCapabilityId;
            waitRemainingSeconds = update.Kind == "waiting_next" ? update.WaitRemainingSeconds : null;
            if (update.CapabilityId is { } capabilityId)
            {
                var index = steps.FindIndex(value => value.CapabilityId == capabilityId);
                if (index >= 0)
                {
                    var step = steps[index];
                    if (update.Kind == "capability_started") steps[index] = step with { Status = "running", StatusLabel = "正在执行" };
                    else if (update.Kind == "capability_completed")
                    {
                        var mapped = update.CapabilityStatus switch
                        {
                            "LOCAL_PASS" => ("passed", "本地验证通过"),
                            "SKIPPED" => ("skipped", "已跳过"),
                            "ABORTED" => ("cancelled", "已取消"),
                            "CLEANUP_ERROR" => ("error", "清理失败"),
                            _ => ("error", "本地验证失败"),
                        };
                        steps[index] = step with { Status = mapped.Item1, StatusLabel = mapped.Item2, LocalEvidence = update.LocalEvidence?.DeepClone().AsObject() };
                        completedCapabilities = steps.Count(value => value.Status is "passed" or "error" or "skipped" or "cancelled");
                    }
                }
            }
            AddLog(new ApiRunLogEntry(update.TimestampUtc, update.Level, update.Kind, update.Message, update.CapabilityId, update.Important));
        }
    }

    public void Complete(RunResult result)
    {
        lock (sync)
        {
            runId = result.RunId;
            status = result.Status == "COMPLETED" ? "completed" : "completed_with_errors";
            progress = 100;
            phase = result.Status == "COMPLETED" ? "SQLite 已封存并导出本地结果" : "轮次完成，但存在能力错误";
            endedAt = DateTimeOffset.UtcNow;
            databaseName = Path.GetFileName(result.DatabasePath);
            localExportPath = result.LocalExportPath;
            runDirectory = result.RunDirectory;
            error = result.Status == "COMPLETED" ? null : result.Status;
            currentCapabilityId = null;
            waitRemainingSeconds = null;
        }
    }

    public void BeginCloudWait(DateTimeOffset queryStartUtc)
    {
        lock (sync)
        {
            if (!cloudRequested) return;
            cloudQueryStartUtc = queryStartUtc;
            cloudStatus = "waiting";
            cloudWaitRemainingSeconds = cloudDelaySeconds;
            AddCloudProgressCore(new ApiCloudProgressEntry(
                DateTimeOffset.UtcNow,
                "info",
                "waiting_ingestion",
                $"本地测试已完成，等待 {cloudDelaySeconds ?? 0} 秒后获取云端日志。",
                0,
                false));
        }
    }

    public void UpdateCloudWaitRemaining(int remainingSeconds)
    {
        lock (sync)
        {
            if (cloudStatus != "waiting") return;
            cloudWaitRemainingSeconds = Math.Max(0, remainingSeconds);
            cloudStageMessage = $"剩余 {cloudWaitRemainingSeconds} 秒后启动浏览器导出。";
            cloudUpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void BeginCloudAcquisition()
    {
        lock (sync)
        {
            if (!cloudRequested) return;
            cloudStatus = "running";
            cloudWaitRemainingSeconds = null;
            AddCloudProgressCore(new ApiCloudProgressEntry(
                DateTimeOffset.UtcNow,
                "info",
                "starting",
                cloudDebugMode ? "正在启动可见浏览器并准备详细日志。" : "正在启动浏览器自动下载云端日志。",
                1,
                false));
        }
    }

    public void ApplyCloudProgress(ApiCloudProgressEntry update)
    {
        lock (sync)
        {
            if (!cloudRequested || cloudStatus is "succeeded" or "failed") return;
            AddCloudProgressCore(update);
        }
    }

    public void CompleteCloudAcquisition(ApiCloudImportRecord import, bool debugLogAvailable)
    {
        lock (sync)
        {
            cloudImport = import;
            cloudStatus = import.Status;
            cloudError = import.Error;
            cloudWaitRemainingSeconds = null;
            cloudDebugLogAvailable = debugLogAvailable;
            var succeeded = import.Status == "succeeded";
            if (succeeded && cloudStage != "completed")
            {
                AddCloudProgressCore(new ApiCloudProgressEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    "completed",
                    $"云端日志已下载并绑定，共 {import.RecordCount ?? 0} 条事件。",
                    100,
                    false));
            }
            else if (!succeeded && (cloudProgressLogs.Count == 0 || cloudProgressLogs[^1].Level != "error"))
            {
                AddCloudProgressCore(new ApiCloudProgressEntry(
                    DateTimeOffset.UtcNow,
                    "error",
                    cloudStage ?? "acquisition_error",
                    import.Error ?? "云端日志获取失败，可稍后手动导入。",
                    cloudProgress,
                    false));
            }
        }
    }

    public void FailCloudAcquisition(string message)
    {
        lock (sync)
        {
            if (!cloudRequested || cloudStatus is "succeeded" or "failed") return;
            cloudStatus = "failed";
            cloudError = message;
            cloudWaitRemainingSeconds = null;
            AddCloudProgressCore(new ApiCloudProgressEntry(
                DateTimeOffset.UtcNow,
                "error",
                cloudStage ?? "coordinator_error",
                message,
                cloudProgress,
                false));
        }
    }

    public bool Cancel()
    {
        lock (sync)
        {
            if (status is not ("queued" or "running")) return false;
            status = "cancelling";
            phase = "正在取消并清理进程树";
            AddLog(new ApiRunLogEntry(DateTimeOffset.UtcNow, "warning", "cancel", "已收到取消请求，正在终止进程树并保留证据。", currentCapabilityId, true));
            cancellation.Cancel();
            return true;
        }
    }

    public void MarkCancelled()
    {
        lock (sync)
        {
            status = "cancelled";
            phase = "轮次已取消，数据库已封存为 ABORTED";
            endedAt = DateTimeOffset.UtcNow;
            error = "RUN_CANCELLED";
            if (currentCapabilityId is { } capabilityId)
            {
                var index = steps.FindIndex(value => value.CapabilityId == capabilityId && value.Status == "running");
                if (index >= 0) steps[index] = steps[index] with { Status = "cancelled", StatusLabel = "已取消" };
            }
            completedCapabilities = steps.Count(value => value.Status is "passed" or "error" or "skipped" or "cancelled");
            currentCapabilityId = null;
            waitRemainingSeconds = null;
        }
    }

    public void Fail(string message)
    {
        lock (sync)
        {
            status = "failed";
            phase = "Runner 执行失败";
            endedAt = DateTimeOffset.UtcNow;
            error = message;
            if (currentCapabilityId is { } capabilityId)
            {
                var index = steps.FindIndex(value => value.CapabilityId == capabilityId && value.Status == "running");
                if (index >= 0) steps[index] = steps[index] with { Status = "error", StatusLabel = "Runner 异常" };
            }
            completedCapabilities = steps.Count(value => value.Status is "passed" or "error" or "skipped" or "cancelled");
            AddLog(new ApiRunLogEntry(DateTimeOffset.UtcNow, "error", "runner", message, currentCapabilityId, true));
        }
    }

    public ApiRunSnapshot Snapshot()
    {
        lock (sync)
        {
            return new ApiRunSnapshot(
                OperationId,
                runId,
                Name,
                status,
                progress,
                phase,
                CapabilityIds,
                AllowHighRisk,
                InterCapabilityDelaySeconds,
                InterSubtestDelayMilliseconds,
                completedCapabilities,
                currentCapabilityId,
                waitRemainingSeconds,
                steps.ToArray(),
                logs.ToArray(),
                logs.Where(value => value.Important || value.Level is "warning" or "error").TakeLast(12).ToArray(),
                StartedAt,
                endedAt,
                databaseName,
                localExportPath is not null && File.Exists(localExportPath),
                new ApiCloudAcquisitionSnapshot(
                    cloudRequested,
                    cloudStatus,
                    cloudDebugMode,
                    cloudDeviceName,
                    cloudQueryStartUtc,
                    cloudDelaySeconds,
                    cloudWaitRemainingSeconds,
                    cloudProgress,
                    cloudStage,
                    cloudStageMessage,
                    cloudUpdatedAtUtc,
                    cloudProgressLogs.ToArray(),
                    cloudDebugLogAvailable,
                    cloudImport,
                    cloudError),
                error);
        }
    }

    private void AddCloudProgressCore(ApiCloudProgressEntry entry)
    {
        cloudProgress = Math.Clamp(Math.Max(cloudProgress, entry.Progress), 0, 100);
        var normalized = entry with { Progress = cloudProgress };
        if (!normalized.Detailed)
        {
            cloudStage = normalized.Stage;
            cloudStageMessage = normalized.Message;
        }
        cloudUpdatedAtUtc = normalized.TimestampUtc;
        cloudProgressLogs.Add(normalized);
        if (cloudProgressLogs.Count > 250) cloudProgressLogs.RemoveRange(0, cloudProgressLogs.Count - 250);
        if (!normalized.Detailed)
        {
            AddLog(new ApiRunLogEntry(
                normalized.TimestampUtc,
                normalized.Level,
                $"cloud_export:{normalized.Stage}",
                normalized.Message,
                null,
                true));
        }
    }

    private void AddLog(ApiRunLogEntry entry)
    {
        logs.Add(entry);
    }
}

internal sealed class ApiComparisonCoordinator
{
    private readonly ConcurrentDictionary<string, ApiComparisonProgressState> states = new(StringComparer.Ordinal);

    public ApiComparisonProgressState Start(string comparisonId)
    {
        var state = new ApiComparisonProgressState(comparisonId);
        if (!states.TryAdd(comparisonId, state))
        {
            throw new ApiRequestException(409, "comparison_id 已存在，请重新发起比较。");
        }
        return state;
    }

    public ApiComparisonProgressSnapshot? Get(string comparisonId)
    {
        if (!Guid.TryParse(comparisonId, out _) || !states.TryGetValue(comparisonId, out var state)) return null;
        return state.Snapshot();
    }
}

internal sealed class ApiComparisonProgressState
{
    private readonly object sync = new();
    private string status = "running";
    private double progress;
    private int completedCapabilities;
    private int totalCapabilities;
    private string? capabilityId;
    private string? displayNameZh;
    private string? validationStatus;
    private DateTimeOffset updatedAtUtc = DateTimeOffset.UtcNow;
    private string? error;

    public ApiComparisonProgressState(string comparisonId)
    {
        ComparisonId = comparisonId;
    }

    public string ComparisonId { get; }

    public void Apply(CompareProgressUpdate update)
    {
        lock (sync)
        {
            progress = update.Progress;
            completedCapabilities = update.CompletedCapabilities;
            totalCapabilities = update.TotalCapabilities;
            capabilityId = update.CapabilityId;
            displayNameZh = update.DisplayNameZh;
            validationStatus = update.ValidationStatus;
            updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Complete()
    {
        lock (sync)
        {
            status = "completed";
            progress = totalCapabilities == 0 ? 100 : progress;
            updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(string message)
    {
        lock (sync)
        {
            status = "failed";
            error = message;
            updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public ApiComparisonProgressSnapshot Snapshot()
    {
        lock (sync)
        {
            return new ApiComparisonProgressSnapshot(
                ComparisonId,
                status,
                progress,
                completedCapabilities,
                totalCapabilities,
                capabilityId,
                displayNameZh,
                validationStatus,
                updatedAtUtc,
                error);
        }
    }
}

internal sealed class ApiRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
