using System.Text;
using System.Text.Json.Nodes;

namespace EdrTest;

public sealed record ControllerOutputDisplay(
    string Message,
    string Kind,
    string Level,
    string? Status,
    bool Structured);

public static class ControllerOutputFormatter
{
    private static readonly HashSet<string> SupportedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUBTEST_WAITING",
        "LOCAL_PASS",
        "SAMPLE_ERROR",
        "CLEANUP_ERROR",
        "SKIPPED",
        "ABORTED",
    };

    public static ControllerOutputDisplay FormatLine(string line)
    {
        if (!TryReadStatus(line, out var root, out var status))
        {
            return new ControllerOutputDisplay(line, "controller_stdout", "info", null, false);
        }

        var explicitMessage = ReadString(root, "message");
        var error = ReadString(root, "error");
        var message = !string.IsNullOrWhiteSpace(explicitMessage)
            ? explicitMessage
            : BuildMessage(root, status, error);
        var level = status is "SAMPLE_ERROR" or "CLEANUP_ERROR" or "ABORTED" ? "warning" : "info";
        var kind = status == "SUBTEST_WAITING" ? "subtest_waiting" : "controller_status";
        return new ControllerOutputDisplay(message, kind, level, status, true);
    }

    public static string FormatPersistedOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return output;

        var messages = new List<string>();
        var candidate = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;

        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (candidate.Length == 0 && !line.TrimStart().StartsWith('{'))
            {
                if (!string.IsNullOrWhiteSpace(line)) messages.Add(line);
                continue;
            }

            if (candidate.Length > 0) candidate.AppendLine();
            candidate.Append(line);
            UpdateJsonState(line, ref depth, ref inString, ref escaped);
            if (depth > 0 || inString) continue;

            AddCandidate(messages, candidate.ToString());
            candidate.Clear();
        }

        if (candidate.Length > 0) AddCandidate(messages, candidate.ToString());
        return string.Join(Environment.NewLine, messages);
    }

    private static void AddCandidate(ICollection<string> messages, string candidate)
    {
        var formatted = FormatLine(candidate);
        if (formatted.Structured)
        {
            messages.Add(formatted.Message);
            return;
        }

        foreach (var line in candidate.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line)) messages.Add(line);
        }
    }

    private static bool TryReadStatus(string text, out JsonObject root, out string status)
    {
        root = [];
        status = string.Empty;
        try
        {
            root = JsonNode.Parse(text)?.AsObject() ?? [];
            status = ReadString(root, "status")?.Trim().ToUpperInvariant() ?? string.Empty;
            return SupportedStatuses.Contains(status);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string BuildMessage(JsonObject root, string status, string? error)
    {
        if (status == "SUBTEST_WAITING")
        {
            var completed = ReadString(root, "completed_subtest") ?? "上一项子测试";
            var next = ReadString(root, "next_subtest") ?? "下一项子测试";
            var delay = ReadInteger(root, "delay_ms");
            return delay is null
                ? $"子测试“{completed}”已完成，等待后执行“{next}”。"
                : $"子测试“{completed}”已完成，等待 {delay.Value} ms 后执行“{next}”。";
        }

        var label = status switch
        {
            "LOCAL_PASS" => "本地自验证通过",
            "SAMPLE_ERROR" => "本地自验证失败",
            "CLEANUP_ERROR" => "清理失败",
            "SKIPPED" => "能力已跳过",
            "ABORTED" => "能力已中止",
            _ => status,
        };
        return string.IsNullOrWhiteSpace(error) ? $"Controller 报告：{label}。" : $"Controller 报告：{label}（{error}）。";
    }

    private static string? ReadString(JsonObject root, string propertyName)
    {
        try
        {
            return root[propertyName]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? ReadInteger(JsonObject root, string propertyName)
    {
        try
        {
            return root[propertyName]?.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void UpdateJsonState(string text, ref int depth, ref bool inString, ref bool escaped)
    {
        foreach (var character in text)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"') inString = false;
                continue;
            }

            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}') depth--;
        }
    }
}
