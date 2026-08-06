namespace EdrTest;

public sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);

    private CliOptions()
    {
    }

    public static CliOptions Parse(IEnumerable<string> arguments)
    {
        var result = new CliOptions();
        var items = arguments.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!item.StartsWith("--", StringComparison.Ordinal) || item.Length == 2)
            {
                throw new ArgumentException($"无法识别的参数：{item}");
            }

            var name = item[2..];
            if (index + 1 >= items.Length || items[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result.flags.Add(name);
                continue;
            }

            if (!result.values.TryGetValue(name, out var list))
            {
                list = [];
                result.values[name] = list;
            }
            list.Add(items[++index]);
        }
        return result;
    }

    public bool HasFlag(string name) => flags.Contains(name);

    public string? Get(string name) => values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> GetMany(string name) => values.TryGetValue(name, out var list) ? list : [];

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"缺少参数 --{name}。");

    public int RequireInt(string name, int minimum, int maximum)
    {
        var text = Require(name);
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"--{name} 必须在 {minimum}..{maximum} 内。");
        }
        return value;
    }
}
