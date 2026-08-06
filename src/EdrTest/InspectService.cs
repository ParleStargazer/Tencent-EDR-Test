using System.Text.Json.Nodes;

namespace EdrTest;

public static class InspectService
{
    public static JsonObject Inspect(string databasePath)
    {
        using var connection = RunDatabase.OpenReadOnlyConnection(Path.GetFullPath(databasePath));
        using var runCommand = connection.CreateCommand();
        runCommand.CommandText = "SELECT run_id, status, started_at_utc, ended_at_utc, finalized FROM run WHERE singleton = 1;";
        using var run = runCommand.ExecuteReader();
        if (!run.Read()) throw new InvalidDataException("数据库缺少 run 主记录。");
        var root = new JsonObject
        {
            ["run_id"] = run.String("run_id"),
            ["status"] = run.String("status"),
            ["started_at_utc"] = run.String("started_at_utc"),
            ["ended_at_utc"] = run.NullableString("ended_at_utc"),
            ["finalized"] = run.Int32("finalized") == 1,
        };
        run.Close();

        using var capabilityCommand = connection.CreateCommand();
        capabilityCommand.CommandText = "SELECT capability_id, case_run_id, status, error_code FROM capability_run ORDER BY sequence_number;";
        using var reader = capabilityCommand.ExecuteReader();
        var capabilities = new JsonArray();
        while (reader.Read())
        {
            capabilities.Add(new JsonObject
            {
                ["capability_id"] = reader.String("capability_id"),
                ["case_run_id"] = reader.String("case_run_id"),
                ["status"] = reader.String("status"),
                ["error_code"] = reader.NullableString("error_code"),
            });
        }
        root["capabilities"] = capabilities;
        return root;
    }
}
