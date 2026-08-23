using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using EdrTest.SampleProtocol;

namespace ScheduledTaskActivity;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string path) where T : class => ReliableProtocolFile.Read<T>(path, Options);
    public static void WriteAtomic<T>(string path, T value) => ReliableProtocolFile.WriteAtomic(path, value, Options);
}

public sealed class TaskSnapshot
{
    public required bool Exists { get; init; }
    public string? XmlSha256 { get; init; }
    public string? Principal { get; init; }
    public bool? Enabled { get; init; }
    public string[]? Actions { get; init; }
    public string[]? Triggers { get; init; }
    public string? Marker { get; init; }
    public string? ActionCommand { get; init; }
    public string? ActionArguments { get; init; }
}

public sealed class BehaviorResult
{
    public required string Method { get; init; }
    public required string Operation { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required string TaskPath { get; init; }
    public required string ExpectedMarker { get; init; }
    public required TaskSnapshot Before { get; init; }
    public required TaskSnapshot After { get; init; }
    public int? ClientProcessId { get; init; }
    public string? ClientExecutable { get; init; }
    public string? ClientCommandLine { get; init; }
    public DateTimeOffset? ClientStartedAtUtc { get; init; }
    public DateTimeOffset? ClientEndedAtUtc { get; init; }
    public int? ClientExitCode { get; init; }
    public string? ClientStandardOutput { get; init; }
    public string? ClientStandardError { get; init; }
    public int? SecurityEventId { get; init; }
    public bool? SecurityEventFound { get; init; }
    public DateTimeOffset? SecurityEventOccurredAtUtc { get; init; }
    public long? SecurityEventRecordId { get; init; }
    public bool? SecurityEvent4698Found { get; init; }
    public string? AuditSubcategoryId { get; init; }
    public uint? AuditPolicyBefore { get; init; }
    public uint? AuditPolicyActive { get; init; }
    public bool? AuditSuccessEnabled { get; init; }
    public uint? AuditPolicyRestored { get; init; }
    public bool? AuditPolicyChanged { get; init; }
    public bool? AuditPolicyRestoreSucceeded { get; init; }
    public string? AuditPolicyOutput { get; init; }
    public string? SecurityEventQueryOutput { get; init; }
    public string? DiagnosticError { get; init; }
    public int? HResult { get; init; }
    public string? Error { get; init; }
}

public static class ScheduledTaskClient
{
    private const int TaskCreate = 2;
    private const int TaskUpdate = 4;
    private const int TaskLogonInteractiveToken = 3;
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string CreateDefinition(string taskPath, string principalSid, string marker, string actionArguments,
        bool enabled = false, DateTimeOffset? futureStartUtc = null)
    {
        ValidateTaskPath(taskPath);
        var triggers = new XElement(TaskNamespace + "Triggers");
        if (futureStartUtc is not null)
        {
            triggers.Add(new XElement(TaskNamespace + "TimeTrigger",
                new XElement(TaskNamespace + "StartBoundary", futureStartUtc.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)),
                new XElement(TaskNamespace + "Enabled", true)));
        }
        var task = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(TaskNamespace + "Task", new XAttribute("version", "1.4"),
                new XElement(TaskNamespace + "RegistrationInfo",
                    new XElement(TaskNamespace + "Author", "EDR 自动化验证平台"),
                    new XElement(TaskNamespace + "Description", marker),
                    new XElement(TaskNamespace + "URI", taskPath)),
                triggers,
                new XElement(TaskNamespace + "Principals",
                    new XElement(TaskNamespace + "Principal", new XAttribute("id", "Author"),
                        new XElement(TaskNamespace + "UserId", principalSid),
                        new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(TaskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskNamespace + "Settings",
                    new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNamespace + "DisallowStartIfOnBatteries", false),
                    new XElement(TaskNamespace + "StopIfGoingOnBatteries", false),
                    new XElement(TaskNamespace + "AllowHardTerminate", true),
                    new XElement(TaskNamespace + "StartWhenAvailable", false),
                    new XElement(TaskNamespace + "RunOnlyIfNetworkAvailable", false),
                    new XElement(TaskNamespace + "IdleSettings",
                        new XElement(TaskNamespace + "StopOnIdleEnd", true),
                        new XElement(TaskNamespace + "RestartOnIdle", false)),
                    new XElement(TaskNamespace + "AllowStartOnDemand", false),
                    new XElement(TaskNamespace + "Enabled", enabled),
                    new XElement(TaskNamespace + "Hidden", false),
                    new XElement(TaskNamespace + "RunOnlyIfIdle", false),
                    new XElement(TaskNamespace + "WakeToRun", false),
                    new XElement(TaskNamespace + "ExecutionTimeLimit", "PT1M"),
                    new XElement(TaskNamespace + "Priority", 7)),
                new XElement(TaskNamespace + "Actions", new XAttribute("Context", "Author"),
                    new XElement(TaskNamespace + "Exec",
                        new XElement(TaskNamespace + "Command", Path.Combine(Environment.SystemDirectory, "cmd.exe")),
                        new XElement(TaskNamespace + "Arguments", actionArguments)))));
        return task.ToString(SaveOptions.DisableFormatting);
    }

    public static void Register(string taskPath, string xml, bool update)
    {
        ValidateTaskPath(taskPath);
        object? service = null;
        object? folder = null;
        object? registeredTask = null;
        try
        {
            service = Connect();
            dynamic dynamicService = service;
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            registeredTask = dynamicFolder.RegisterTask(taskPath, xml, update ? TaskUpdate : TaskCreate,
                null, null, TaskLogonInteractiveToken, null);
        }
        finally
        {
            Release(registeredTask);
            Release(folder);
            Release(service);
        }
    }

    public static void Delete(string taskPath, bool ignoreMissing = false)
    {
        ValidateTaskPath(taskPath);
        object? service = null;
        object? folder = null;
        try
        {
            service = Connect();
            dynamic dynamicService = service;
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            try { dynamicFolder.DeleteTask(taskPath, 0); }
            catch (Exception exception) when (ignoreMissing && IsNotFound(exception)) { }
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    public static TaskSnapshot Snapshot(string taskPath)
    {
        ValidateTaskPath(taskPath);
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = Connect();
            dynamic dynamicService = service;
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            try { task = dynamicFolder.GetTask(taskPath); }
            catch (Exception exception) when (IsNotFound(exception)) { return MissingSnapshot(); }
            dynamic dynamicTask = task;
            var xml = Convert.ToString(dynamicTask.Xml, System.Globalization.CultureInfo.InvariantCulture)
                ?? throw new InvalidDataException("计划任务服务返回了空 XML。");
            var enabled = Convert.ToBoolean(dynamicTask.Enabled, System.Globalization.CultureInfo.InvariantCulture);
            return ParseSnapshot(xml, enabled);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    public static TaskSnapshot MissingSnapshot() => new() { Exists = false };

    private static TaskSnapshot ParseSnapshot(string xml, bool enabled)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var actions = document.Descendants(TaskNamespace + "Exec").Select(element =>
        {
            var command = element.Element(TaskNamespace + "Command")?.Value;
            var arguments = element.Element(TaskNamespace + "Arguments")?.Value;
            return string.Join(' ', new[] { command, arguments }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var triggers = document.Descendants(TaskNamespace + "Triggers").Elements()
            .Select(element => element.Name.LocalName).ToArray();
        return new TaskSnapshot
        {
            Exists = true,
            XmlSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant(),
            Principal = document.Descendants(TaskNamespace + "UserId").FirstOrDefault()?.Value,
            Enabled = enabled,
            Actions = actions,
            Triggers = triggers,
            Marker = document.Descendants(TaskNamespace + "Description").FirstOrDefault()?.Value,
            ActionCommand = document.Descendants(TaskNamespace + "Command").FirstOrDefault()?.Value,
            ActionArguments = document.Descendants(TaskNamespace + "Arguments").FirstOrDefault()?.Value,
        };
    }

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new PlatformNotSupportedException("当前系统没有 Task Scheduler 2.0 COM 服务。");
        var service = Activator.CreateInstance(type)
            ?? throw new COMException("无法创建 Task Scheduler COM 服务实例。");
        dynamic dynamicService = service;
        dynamicService.Connect();
        return service;
    }

    private static bool IsNotFound(Exception exception) =>
        exception.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F);

    private static void ValidateTaskPath(string taskPath)
    {
        if (!taskPath.StartsWith("\\EdrTest_", StringComparison.Ordinal) || taskPath.Contains("..", StringComparison.Ordinal)
            || taskPath.Count(character => character == '\\') != 1)
            throw new ArgumentException("计划任务路径不在本轮受控根目录命名范围内。", nameof(taskPath));
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

public sealed class ArgumentReader
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    private ArgumentReader(IEnumerable<string> arguments)
    {
        var items = arguments.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!item.StartsWith("--", StringComparison.Ordinal) || item.Length == 2)
                throw new ArgumentException($"无法识别的参数：{item}");
            var name = item[2..];
            if (index + 1 >= items.Length || items[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"参数 --{name} 缺少值。");
            if (!values.TryAdd(name, items[++index])) throw new ArgumentException($"参数 --{name} 重复。");
        }
    }

    public static ArgumentReader Parse(IEnumerable<string> arguments) => new(arguments);
    public string Require(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"缺少参数 --{name}。");
    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum}..{maximum} 范围内。");
        return value;
    }
}
