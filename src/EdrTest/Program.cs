using System.Text;

namespace EdrTest;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var options = CliOptions.Parse(args.Skip(1));
            return args[0].ToLowerInvariant() switch
            {
                "capabilities" => ListCapabilities(options),
                "run" => await Run(options),
                "export" => Export(options),
                "compare" => Compare(options),
                "inspect" => Inspect(options),
                "serve" => await Serve(options),
                "version" => Version(),
                _ => throw new ArgumentException($"未知命令：{args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return 2;
        }
    }

    private static int ListCapabilities(CliOptions options)
    {
        var root = Path.GetFullPath(options.Get("root") ?? "samples");
        var packages = CapabilityCatalog.Discover(root);
        if (packages.Count == 0)
        {
            Console.WriteLine($"未在 {root} 中发现 capability.json。");
            return 0;
        }
        foreach (var package in packages)
        {
            var manifest = package.Manifest;
            Console.WriteLine($"{manifest.CapabilityId,-32} {manifest.Version,-10} {manifest.RiskLevel,-2} {manifest.DisplayNameZh ?? manifest.DisplayName ?? manifest.CapabilityId} / {manifest.DisplayNameEn ?? "-"}");
        }
        return 0;
    }

    private static async Task<int> Run(CliOptions options)
    {
        var manifests = options.GetMany("manifest").Select(Path.GetFullPath).ToList();
        var capabilityIds = options.GetMany("capability");
        if (capabilityIds.Count > 0)
        {
            var root = options.Get("samples-root") ?? "samples";
            var discovered = CapabilityCatalog.Discover(root).ToDictionary(x => x.Manifest.CapabilityId, StringComparer.Ordinal);
            foreach (var id in capabilityIds)
            {
                if (!discovered.TryGetValue(id, out var package)) throw new ArgumentException($"未发现能力：{id}");
                manifests.Add(package.ManifestPath);
            }
        }
        if (manifests.Count == 0) throw new ArgumentException("请使用 --manifest 或 --capability 选择能力。");
        var parameters = ReadInlineOrFile(options.Get("parameters"));
        var request = new RunRequest(
            manifests,
            options.Get("runs-dir") ?? "runs",
            parameters,
            options.HasFlag("allow-high-risk"),
            options.Get("suite-id"),
            options.Get("environment-id"));
        var result = await new RunnerService().RunAsync(request);
        Console.WriteLine($"轮次：{result.RunId}");
        Console.WriteLine($"状态：{result.Status}");
        Console.WriteLine($"数据库：{result.DatabasePath}");
        Console.WriteLine($"本地导出：{result.LocalExportPath}");
        return result.Status == "COMPLETED" ? 0 : 1;
    }

    private static int Export(CliOptions options)
    {
        var database = options.Require("db");
        var output = options.Require("out");
        ExportService.Export(database, output);
        Console.WriteLine($"已导出：{Path.GetFullPath(output)}");
        return 0;
    }

    private static int Compare(CliOptions options)
    {
        var baselines = options.GetMany("baseline").Select(Path.GetFullPath).ToList();
        if (options.Get("baselines") is { } baselineDirectory)
        {
            baselines.AddRange(Directory.EnumerateFiles(baselineDirectory, "*.yaml", SearchOption.AllDirectories).Select(Path.GetFullPath));
        }
        var request = new CompareRequest(
            options.Require("local"),
            options.GetMany("cloud"),
            options.Require("mapping"),
            baselines,
            options.Require("out"),
            options.Get("cloud-manifest"));
        var result = CompareService.Compare(request);
        var summary = result["summary"]!.AsObject();
        Console.WriteLine($"比较完成：PASS={summary["pass"]} PARTIAL={summary["partial"]} FAIL={summary["fail"]} INCONCLUSIVE={summary["inconclusive"]}");
        Console.WriteLine($"结果：{Path.GetFullPath(request.OutputPath)}");
        return summary["fail"]?.GetValue<int>() > 0 ? 1 : 0;
    }

    private static int Inspect(CliOptions options)
    {
        Console.WriteLine(InspectService.Inspect(options.Require("db")).ToJsonString(JsonDefaults.Options));
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine(EdrTestVersion.Current);
        return 0;
    }

    private static Task<int> Serve(CliOptions options)
    {
        var port = ParsePort(options.Get("port") ?? "4317");
        var repositoryRoot = Path.GetFullPath(options.Get("repo-root") ?? Environment.CurrentDirectory);
        var allowedOrigins = options.GetMany("allowed-origin");
        return LocalApiService.RunAsync(new LocalApiOptions(
            options.Get("host") ?? "127.0.0.1",
            port,
            repositoryRoot,
            Path.GetFullPath(options.Get("samples-root") ?? Path.Combine(repositoryRoot, "samples")),
            Path.GetFullPath(options.Get("runs-dir") ?? Path.Combine(repositoryRoot, "runs")),
            Path.GetFullPath(options.Get("import-dir") ?? Path.Combine(repositoryRoot, "import")),
            Path.GetFullPath(options.Get("reports-dir") ?? Path.Combine(repositoryRoot, "reports")),
            allowedOrigins.Count == 0 ? ["http://127.0.0.1:3000", "http://localhost:3000"] : allowedOrigins,
            options.Get("token")));
    }

    private static int ParsePort(string value) => int.TryParse(value, out var port) && port is >= 1024 and <= 65535
        ? port
        : throw new ArgumentException("--port 必须在 1024..65535 内。");

    private static string? ReadInlineOrFile(string? value)
    {
        if (value is null) return null;
        return value.StartsWith('@') ? File.ReadAllText(value[1..]) : value;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            EdrTest — EDR 能力离线验证框架

            命令：
              capabilities --root samples
              run --manifest <capability.json> [--manifest <...>] [--runs-dir runs]
                  [--parameters <json|@file>] [--allow-high-risk]
              run --capability <id> [--samples-root samples]
              export --db <run.db> --out <local-run.json>
              compare --local <local-run.json> --cloud <cloud.json> [--cloud <...>]
                  --mapping <mapping.yaml> --baseline <baseline.yaml> [--baseline <...>]
                  [--cloud-manifest <manifest.json>] --out <validation-result.json>
              inspect --db <run.db>
              serve [--host 127.0.0.1] [--port 4317] [--repo-root <path>]
                  [--samples-root samples] [--runs-dir runs]
                  [--allowed-origin http://localhost:3000] [--token <local-token>]
              version

            samples/、runs/ 和云端导出均为本地文件，不纳入版本控制。
            """);
    }
}
