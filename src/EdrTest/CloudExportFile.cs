using System.Text.Json;

namespace EdrTest;

public sealed record CloudExportInspection(string Path, string Format, int RecordCount, long SizeBytes, string Sha256);

public static class CloudExportFile
{
    public const long MaximumBytes = 256L * 1024 * 1024;

    public static CloudExportInspection Inspect(string inputPath)
    {
        var path = Path.GetFullPath(inputPath);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 EDR 云端导出文件。", path);
        var size = new FileInfo(path).Length;
        if (size is <= 0 or > MaximumBytes) throw new InvalidDataException("EDR 云端导出文件为空或超过 256 MB。");
        var format = "unknown";
        var count = 0;
        ReadObjectRecords(path, (_, _, detectedFormat) =>
        {
            format = detectedFormat;
            count++;
        });
        if (count == 0) throw new InvalidDataException("EDR 云端导出文件不包含 JSON 事件对象。");
        return new CloudExportInspection(path, format, count, size, Hashing.FileSha256(path));
    }

    internal static void ReadObjectRecords(string inputPath, Action<JsonElement, int, string> consume)
    {
        var path = Path.GetFullPath(inputPath);
        var text = File.ReadAllText(path);
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty) throw new InvalidDataException("EDR 云端导出文件为空。");
        if (trimmed[0] == '[')
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("EDR JSON 顶层必须是数组、对象或 JSONL。");
            var index = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"EDR JSON 数组第 {index + 1} 项不是对象。");
                consume(item, index++, "json_array");
            }
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("EDR JSON 顶层必须是数组、对象或 JSONL。");
            consume(document.RootElement, 0, "json_object");
            return;
        }
        catch (JsonException) when (text.Contains('\n'))
        {
            // 整体不是单个 JSON 对象时，按 JSONL 逐行处理。
        }

        var lineIndex = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"EDR JSONL 第 {lineIndex + 1} 条不是对象。");
            consume(document.RootElement, lineIndex++, "jsonl");
        }
    }
}
