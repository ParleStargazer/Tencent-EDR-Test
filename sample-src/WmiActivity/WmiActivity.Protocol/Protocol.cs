using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdrTest.SampleProtocol;

namespace WmiActivity;

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

public sealed record WmiPlan(
    string CapabilityId,
    string Operation,
    string Title,
    string Namespace,
    string ObjectClass,
    string ObjectName,
    string FilterName,
    string ConsumerName,
    string Query,
    string QueryLanguage,
    string EventNamespace,
    string ConsumerClass,
    string LogFilePath,
    string TextTemplate);

public static class WmiPlans
{
    public const string SubscriptionNamespace = @"ROOT\subscription";
    public const string FilterClass = "__EventFilter";
    public const string ConsumerClass = "LogFileEventConsumer";
    public const string BindingClass = "__FilterToConsumerBinding";

    public static WmiPlan Create(string capabilityId, string nonce, string workDirectory)
    {
        ValidateNonce(nonce);
        var operation = capabilityId switch
        {
            "win.wmi.filter" => "filter",
            "win.wmi.consumer" => "consumer",
            "win.wmi.consumer_filter.bind" => "consumer_filter_bind",
            _ => throw new ArgumentException($"不支持的 WMI 能力：{capabilityId}"),
        };
        var filterName = $"EDR_TEST_FILTER_{nonce}";
        var consumerName = $"EDR_TEST_CONSUMER_{nonce}";
        var query = $"SELECT * FROM __InstanceCreationEvent WITHIN 10 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = 'EDR_TEST_NEVER_{nonce}.exe'";
        var logFilePath = Path.GetFullPath(Path.Combine(workDirectory, $"wmi-consumer-{nonce}.log"));
        var textTemplate = $"EDR_TEST_WMI_{nonce}_%TargetInstance.Name%";
        return operation switch
        {
            "filter" => new(capabilityId, operation, "WMI 事件过滤器", SubscriptionNamespace,
                FilterClass, filterName, filterName, consumerName, query, "WQL", @"root\cimv2",
                ConsumerClass, logFilePath, textTemplate),
            "consumer" => new(capabilityId, operation, "WMI 事件消费者", SubscriptionNamespace,
                ConsumerClass, consumerName, filterName, consumerName, query, "WQL", @"root\cimv2",
                ConsumerClass, logFilePath, textTemplate),
            _ => new(capabilityId, operation, "WMI 事件消费者与过滤器绑定", SubscriptionNamespace,
                BindingClass, $"{filterName}->{consumerName}", filterName, consumerName, query, "WQL", @"root\cimv2",
                ConsumerClass, logFilePath, textTemplate),
        };
    }

    public static void ValidateNonce(string nonce)
    {
        if (nonce.Length != 32 || nonce.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("nonce 必须是 32 位十六进制字符串。");
    }
}

public sealed class WmiSnapshot
{
    public required bool Exists { get; init; }
    public required string ObjectClass { get; init; }
    public string? ObjectPath { get; init; }
    public string? Name { get; init; }
    public string? Query { get; init; }
    public string? QueryLanguage { get; init; }
    public string? EventNamespace { get; init; }
    public string? LogFilePath { get; init; }
    public string? TextTemplate { get; init; }
    public string? FilterReference { get; init; }
    public string? ConsumerReference { get; init; }
}

public sealed class WmiReady
{
    public required string CapabilityId { get; init; }
    public required string Operation { get; init; }
    public required int ActorProcessId { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required WmiSnapshot Before { get; init; }
    public required WmiSnapshot After { get; init; }
    public string? FilterPath { get; init; }
    public string? ConsumerPath { get; init; }
    public string? BindingPath { get; init; }
}

public sealed class WmiVerificationGate
{
    public required string CapabilityId { get; init; }
    public required string Operation { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
}

public sealed class WmiBehaviorResult
{
    public required string CapabilityId { get; init; }
    public required string Operation { get; init; }
    public required int ActorProcessId { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required bool ControllerGateObserved { get; init; }
    public required bool ActorVerificationSucceeded { get; init; }
    public required bool CleanupSucceeded { get; init; }
    public required IReadOnlyList<string> CleanupOrder { get; init; }
    public required WmiSnapshot Before { get; init; }
    public required WmiSnapshot After { get; init; }
    public required WmiSnapshot Final { get; init; }
    public string? FilterPath { get; init; }
    public string? ConsumerPath { get; init; }
    public string? BindingPath { get; init; }
    public required bool Succeeded { get; init; }
    public int? HResult { get; init; }
    public string? Error { get; init; }
}

public sealed class WmiCleanupResult
{
    public required bool Succeeded { get; init; }
    public required IReadOnlyList<string> Order { get; init; }
    public required bool BindingExistsAfter { get; init; }
    public required bool ConsumerExistsAfter { get; init; }
    public required bool FilterExistsAfter { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
}

public static class WmiRepository
{
    public static WmiSnapshot CaptureTarget(WmiPlan plan)
    {
        var scope = Connect();
        return plan.Operation switch
        {
            "filter" => CaptureNamed(scope, WmiPlans.FilterClass, plan.FilterName),
            "consumer" => CaptureNamed(scope, WmiPlans.ConsumerClass, plan.ConsumerName),
            "consumer_filter_bind" => CaptureBinding(scope, plan.FilterName, plan.ConsumerName),
            _ => throw new ArgumentException($"不支持的 WMI 动作：{plan.Operation}"),
        };
    }

    public static (DateTimeOffset OccurredAtUtc, string? FilterPath, string? ConsumerPath, string? BindingPath) Create(WmiPlan plan)
    {
        var scope = Connect();
        string? filterPath = null;
        string? consumerPath = null;
        string? bindingPath = null;
        DateTimeOffset occurredAtUtc;
        switch (plan.Operation)
        {
            case "filter":
                occurredAtUtc = DateTimeOffset.UtcNow;
                filterPath = CreateFilter(scope, plan);
                break;
            case "consumer":
                occurredAtUtc = DateTimeOffset.UtcNow;
                consumerPath = CreateConsumer(scope, plan);
                break;
            case "consumer_filter_bind":
                filterPath = CreateFilter(scope, plan);
                consumerPath = CreateConsumer(scope, plan);
                occurredAtUtc = DateTimeOffset.UtcNow;
                bindingPath = CreateBinding(scope, filterPath, consumerPath);
                break;
            default:
                throw new ArgumentException($"不支持的 WMI 动作：{plan.Operation}");
        }
        return (occurredAtUtc, filterPath, consumerPath, bindingPath);
    }

    public static WmiCleanupResult Cleanup(WmiPlan plan)
    {
        var order = new List<string>();
        var errors = new List<string>();
        var scope = Connect();
        TryDelete(() => DeleteBindings(scope, plan.FilterName, plan.ConsumerName), WmiPlans.BindingClass, order, errors);
        TryDelete(() => DeleteNamed(scope, WmiPlans.ConsumerClass, plan.ConsumerName), WmiPlans.ConsumerClass, order, errors);
        TryDelete(() => DeleteNamed(scope, WmiPlans.FilterClass, plan.FilterName), WmiPlans.FilterClass, order, errors);
        var bindingExists = CaptureBinding(scope, plan.FilterName, plan.ConsumerName).Exists;
        var consumerExists = CaptureNamed(scope, WmiPlans.ConsumerClass, plan.ConsumerName).Exists;
        var filterExists = CaptureNamed(scope, WmiPlans.FilterClass, plan.FilterName).Exists;
        return new WmiCleanupResult
        {
            Succeeded = errors.Count == 0 && !bindingExists && !consumerExists && !filterExists,
            Order = order,
            BindingExistsAfter = bindingExists,
            ConsumerExistsAfter = consumerExists,
            FilterExistsAfter = filterExists,
            Errors = errors,
        };
    }

    public static bool MatchesPlan(WmiPlan plan, WmiSnapshot snapshot)
    {
        if (!snapshot.Exists || !string.Equals(snapshot.ObjectClass, plan.ObjectClass, StringComparison.OrdinalIgnoreCase)) return false;
        return plan.Operation switch
        {
            "filter" => string.Equals(snapshot.Name, plan.FilterName, StringComparison.Ordinal)
                && string.Equals(snapshot.Query, plan.Query, StringComparison.Ordinal)
                && string.Equals(snapshot.QueryLanguage, plan.QueryLanguage, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.EventNamespace, plan.EventNamespace, StringComparison.OrdinalIgnoreCase),
            "consumer" => string.Equals(snapshot.Name, plan.ConsumerName, StringComparison.Ordinal)
                && SamePath(snapshot.LogFilePath, plan.LogFilePath)
                && string.Equals(snapshot.TextTemplate, plan.TextTemplate, StringComparison.Ordinal),
            "consumer_filter_bind" => ReferenceMatches(snapshot.FilterReference, WmiPlans.FilterClass, plan.FilterName)
                && ReferenceMatches(snapshot.ConsumerReference, WmiPlans.ConsumerClass, plan.ConsumerName),
            _ => false,
        };
    }

    private static ManagementScope Connect()
    {
        var options = new ConnectionOptions { EnablePrivileges = true };
        var scope = new ManagementScope(@"\\.\ROOT\subscription", options);
        scope.Connect();
        return scope;
    }

    private static string CreateFilter(ManagementScope scope, WmiPlan plan)
    {
        using var definition = new ManagementClass(scope, new ManagementPath(WmiPlans.FilterClass), null);
        using var instance = definition.CreateInstance() ?? throw new InvalidOperationException("无法创建 __EventFilter 实例。");
        instance["Name"] = plan.FilterName;
        instance["EventNamespace"] = plan.EventNamespace;
        instance["QueryLanguage"] = plan.QueryLanguage;
        instance["Query"] = plan.Query;
        return instance.Put(new PutOptions { Type = PutType.CreateOnly }).Path;
    }

    private static string CreateConsumer(ManagementScope scope, WmiPlan plan)
    {
        using var definition = new ManagementClass(scope, new ManagementPath(WmiPlans.ConsumerClass), null);
        using var instance = definition.CreateInstance() ?? throw new InvalidOperationException("无法创建 LogFileEventConsumer 实例。");
        instance["Name"] = plan.ConsumerName;
        instance["FileName"] = plan.LogFilePath;
        instance["Text"] = plan.TextTemplate;
        instance["MaximumFileSize"] = 65_536U;
        return instance.Put(new PutOptions { Type = PutType.CreateOnly }).Path;
    }

    private static string CreateBinding(ManagementScope scope, string filterPath, string consumerPath)
    {
        using var definition = new ManagementClass(scope, new ManagementPath(WmiPlans.BindingClass), null);
        using var instance = definition.CreateInstance() ?? throw new InvalidOperationException("无法创建 __FilterToConsumerBinding 实例。");
        instance["Filter"] = new ManagementPath(filterPath).RelativePath;
        instance["Consumer"] = new ManagementPath(consumerPath).RelativePath;
        return instance.Put(new PutOptions { Type = PutType.CreateOnly }).Path;
    }

    private static WmiSnapshot CaptureNamed(ManagementScope scope, string className, string name)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className} WHERE Name = '{EscapeWql(name)}'"));
        using var results = searcher.Get();
        foreach (ManagementObject value in results)
        {
            using (value)
            {
                return new WmiSnapshot
                {
                    Exists = true,
                    ObjectClass = className,
                    ObjectPath = value.Path.Path,
                    Name = Property(value, "Name"),
                    Query = Property(value, "Query"),
                    QueryLanguage = Property(value, "QueryLanguage"),
                    EventNamespace = Property(value, "EventNamespace"),
                    LogFilePath = Property(value, "FileName"),
                    TextTemplate = Property(value, "Text"),
                };
            }
        }
        return Missing(className);
    }

    private static WmiSnapshot CaptureBinding(ManagementScope scope, string filterName, string consumerName)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {WmiPlans.BindingClass}"));
        using var results = searcher.Get();
        foreach (ManagementObject value in results)
        {
            using (value)
            {
                var filter = Property(value, "Filter");
                var consumer = Property(value, "Consumer");
                if (!ReferenceMatches(filter, WmiPlans.FilterClass, filterName)
                    || !ReferenceMatches(consumer, WmiPlans.ConsumerClass, consumerName)) continue;
                return new WmiSnapshot
                {
                    Exists = true,
                    ObjectClass = WmiPlans.BindingClass,
                    ObjectPath = value.Path.Path,
                    FilterReference = filter,
                    ConsumerReference = consumer,
                };
            }
        }
        return Missing(WmiPlans.BindingClass);
    }

    private static int DeleteNamed(ManagementScope scope, string className, string name)
    {
        var count = 0;
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className} WHERE Name = '{EscapeWql(name)}'"));
        using var results = searcher.Get();
        foreach (ManagementObject value in results)
        {
            using (value) { value.Delete(); count++; }
        }
        return count;
    }

    private static int DeleteBindings(ManagementScope scope, string filterName, string consumerName)
    {
        var count = 0;
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {WmiPlans.BindingClass}"));
        using var results = searcher.Get();
        foreach (ManagementObject value in results)
        {
            using (value)
            {
                if (!ReferenceMatches(Property(value, "Filter"), WmiPlans.FilterClass, filterName)
                    || !ReferenceMatches(Property(value, "Consumer"), WmiPlans.ConsumerClass, consumerName)) continue;
                value.Delete();
                count++;
            }
        }
        return count;
    }

    private static void TryDelete(Func<int> action, string className, ICollection<string> order, ICollection<string> errors)
    {
        try
        {
            _ = action();
            order.Add(className);
        }
        catch (Exception exception)
        {
            errors.Add($"{className}: {exception.Message}");
        }
    }

    private static bool ReferenceMatches(string? reference, string className, string name)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        try
        {
            var path = new ManagementPath(reference);
            return string.Equals(path.ClassName, className, StringComparison.OrdinalIgnoreCase)
                && path.RelativePath.Contains(name, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or ManagementException)
        {
            return reference.Contains(className, StringComparison.OrdinalIgnoreCase)
                && reference.Contains(name, StringComparison.Ordinal);
        }
    }

    private static string? Property(ManagementBaseObject value, string propertyName)
    {
        var property = value.Properties.Cast<PropertyData>()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        return property?.Value?.ToString();
    }

    private static WmiSnapshot Missing(string objectClass) => new() { Exists = false, ObjectClass = objectClass };
    private static string EscapeWql(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static bool SamePath(string? left, string right) => !string.IsNullOrWhiteSpace(left)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
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

    public static ArgumentReader Parse(IEnumerable<string> values) => new(values);
    public string Require(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"缺少参数 --{name}。");
    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum}..{maximum} 范围内。");
        return value;
    }
}
