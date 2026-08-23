using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdrTest;

public static class SubtestTiming
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonDefaults.Options) { WriteIndented = false };

    public const int DefaultDelayMilliseconds = 1_000;
    public const int MaximumDelayMilliseconds = 10_000;
    public const int MaximumSubtestsPerCapability = 16;

    public static void WaitBetween(
        ControllerInvocation invocation,
        int completedIndex,
        int totalSubtests,
        string completedSubtest,
        string? nextSubtest)
    {
        if (completedIndex < 0 || totalSubtests < 1 || totalSubtests > MaximumSubtestsPerCapability
            || completedIndex >= totalSubtests)
            throw new ArgumentOutOfRangeException(nameof(completedIndex), "子测试序号超出范围。");
        if (completedIndex >= totalSubtests - 1 || invocation.InterSubtestDelayMs == 0) return;

        Console.WriteLine(new JsonObject
        {
            ["schema_version"] = "1.0",
            ["status"] = "SUBTEST_WAITING",
            ["completed_subtest"] = completedSubtest,
            ["next_subtest"] = nextSubtest,
            ["completed_index"] = completedIndex + 1,
            ["total_subtests"] = totalSubtests,
            ["delay_ms"] = invocation.InterSubtestDelayMs,
            ["message"] = $"子测试“{completedSubtest}”已完成，等待 {invocation.InterSubtestDelayMs} ms 后执行“{nextSubtest ?? "下一子测试"}”。",
        }.ToJsonString(CompactJson));
        Console.Out.Flush();
        Thread.Sleep(invocation.InterSubtestDelayMs);
    }
}
