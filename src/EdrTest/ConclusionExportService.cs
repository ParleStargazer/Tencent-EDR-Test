using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace EdrTest;

public static class ConclusionExportService
{
    public static string DefaultOutputPath(string validationResultPath)
    {
        var fullPath = Path.GetFullPath(validationResultPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        if (stem.EndsWith("-result", StringComparison.OrdinalIgnoreCase)) stem = stem[..^7];
        return Path.Combine(directory, $"{stem}-conclusion.md");
    }

    public static void Export(JsonObject result, string outputPath)
    {
        var conclusion = result["conclusion"]?.AsObject() ?? throw new InvalidDataException("验证结果缺少 conclusion。");
        var summary = result["summary"]?.AsObject() ?? throw new InvalidDataException("验证结果缺少 summary。");
        var capabilities = result["capabilities"]?.AsArray() ?? throw new InvalidDataException("验证结果缺少 capabilities。");
        var builder = new StringBuilder();

        builder.AppendLine("# EDR 能力验证结论");
        builder.AppendLine();
        builder.AppendLine($"- 比较编号：`{Text(result, "comparison_id")}`");
        builder.AppendLine($"- 比较时间（UTC）：`{Text(result, "compared_at_utc")}`");
        builder.AppendLine($"- 总体判定：**{Text(conclusion, "label_zh")}（{Text(conclusion, "verdict")}）**");
        builder.AppendLine($"- 完整通过率：{Percentage(conclusion["pass_rate"])}");
        builder.AppendLine();
        builder.AppendLine("## 总体结论");
        builder.AppendLine();
        builder.AppendLine(Text(conclusion, "statement_zh"));
        builder.AppendLine();
        builder.AppendLine("## 汇总");
        builder.AppendLine();
        builder.AppendLine("| 通过 | 部分通过 | 失败 | 无法判定 | 未比较 |");
        builder.AppendLine("| ---: | ---: | ---: | ---: | ---: |");
        builder.AppendLine($"| {Integer(summary, "pass")} | {Integer(summary, "partial")} | {Integer(summary, "fail")} | {Integer(summary, "inconclusive")} | {Integer(summary, "not_compared")} |");
        builder.AppendLine();
        builder.AppendLine("## 能力明细");
        builder.AppendLine();
        builder.AppendLine("| 能力 | 本地执行 | EDR 验证 | 导出覆盖 | 候选事件 | 判定说明 |");
        builder.AppendLine("| --- | --- | --- | --- | ---: | --- |");
        foreach (var node in capabilities)
        {
            if (node is not JsonObject capability) continue;
            var nameZh = OptionalText(capability, "display_name_zh") ?? Text(capability, "capability_id");
            var nameEn = OptionalText(capability, "display_name_en");
            var name = nameEn is null ? nameZh : $"{nameZh}（{nameEn}）";
            builder.AppendLine($"| {Escape(name)} | {Escape(Text(capability, "local_status"))} | {Escape(StatusLabel(Text(capability, "validation_status")))} | {Escape(CoverageLabel(Text(capability, "export_coverage")))} | {Integer(capability, "candidate_count")} | {Escape(Describe(capability))} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 输入与基准");
        builder.AppendLine();
        var inputs = result["inputs"]?.AsObject();
        var localPath = inputs?["local_export"]?["path"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(localPath)) builder.AppendLine($"- 本地运行结果：`{Path.GetFileName(localPath)}`");
        foreach (var cloud in inputs?["cloud_exports"]?.AsArray() ?? [])
        {
            var path = cloud?["path"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(path)) builder.AppendLine($"- EDR 导出日志：`{Path.GetFileName(path)}`");
        }
        foreach (var mapping in inputs?["mapping_profiles"]?.AsArray() ?? [])
        {
            if (mapping is JsonObject value) builder.AppendLine($"- 字段映射：`{Text(value, "id")}`（版本 {Text(value, "version")}）");
        }
        foreach (var standard in inputs?["action_name_standards"]?.AsObject() ?? [])
        {
            var values = standard.Value?.AsArray()
                .Select(value => value?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray() ?? [];
            if (values.Length > 0) builder.AppendLine($"- 自定义 Action.Name：`{standard.Key}` → `{string.Join("`、`", values)}`");
        }
        foreach (var standard in inputs?["child_file_create_op_name_standards"]?.AsObject() ?? [])
        {
            var values = standard.Value?.AsArray()
                .Select(value => value?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray() ?? [];
            if (values.Length > 0) builder.AppendLine($"- 自定义 Child.FileCreateOpName：`{standard.Key}` → `{string.Join("`、`", values)}`");
        }
        builder.AppendLine($"- 检验基准数量：{inputs?["baselines"]?.AsArray().Count ?? 0}");
        builder.AppendLine();
        builder.AppendLine("> 结论仅适用于本次本地运行窗口、用户导入的 EDR 日志范围以及报告中列出的字段映射和 BASELINE 版本。");

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, builder.ToString(), new UTF8Encoding(false));
    }

    private static string Describe(JsonObject capability)
    {
        var methodNotice = capability["method_selection"]?["notice"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(methodNotice)) return methodNotice;

        if (capability["stage_flow"] is JsonObject stageFlow)
        {
            var stages = capability["stage_results"]?.AsArray()
                .Select(value => value?.AsObject())
                .Where(value => value is not null)
                .Cast<JsonObject>()
                .Select(value => $"{Text(value, "title")}：{StatusLabel(Text(value, "status"))}")
                .ToArray() ?? [];
            var detail = stages.Length == 0 ? string.Empty : $"（{string.Join("；", stages)}）";
            return $"三部分证据链为{StatusLabel(Text(stageFlow, "status"))}{detail}；必须依次验证网络连接、同进程连续关联和文件写入。";
        }

        var warnings = capability["warnings"]?.AsArray()
            .Select(value => value?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];
        if (warnings.Length > 0) return string.Join("；", warnings);

        var failedFields = capability["assertions"]?.AsArray()
            .Select(value => value?.AsObject())
            .Where(value => value?["status"]?.GetValue<string>() is "failed" or "not_evaluated")
            .Select(value => value?["field"]?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];
        if (failedFields.Length == 0) return "满足该能力的检验基准。";
        return Text(capability, "validation_status") == "PASS"
            ? $"必需基准已满足；非必需信息项未匹配：{string.Join("、", failedFields)}"
            : $"未满足字段：{string.Join("、", failedFields)}";
    }

    private static string StatusLabel(string status) => status switch
    {
        "PASS" => "通过",
        "PARTIAL" => "部分通过",
        "FAIL" => "失败",
        "INCONCLUSIVE" => "无法判定",
        "NOT_COMPARED" => "未比较",
        _ => status,
    };

    private static string CoverageLabel(string coverage) => coverage switch
    {
        "verified" => "已由 manifest 验证",
        "inferred" => "由日志时间与主机推断",
        "assumed" => "由用户声明完整",
        "insufficient" => "证据不足",
        _ => coverage,
    };

    private static string Percentage(JsonNode? value) => value is null
        ? "无法计算"
        : value.GetValue<double>().ToString("P1", CultureInfo.GetCultureInfo("zh-CN"));

    private static string Text(JsonObject value, string property) => value[property]?.GetValue<string>() ?? throw new InvalidDataException($"验证结果缺少 {property}。");
    private static string? OptionalText(JsonObject value, string property) => value[property]?.GetValue<string>();
    private static int Integer(JsonObject value, string property) => value[property]?.GetValue<int>() ?? 0;
    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
