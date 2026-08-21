using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EdrTest;

namespace VirtualDiskActivity;

internal static class Program
{
    private const string CapabilityId = "win.device.virtual_disk.mount";

    public static int Main(string[] args)
    {
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        var states = new List<ExecutionState>();
        var plannedImages = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!string.Equals(package.Manifest.CapabilityId, CapabilityId, StringComparison.Ordinal))
                throw new InvalidDataException($"VirtualDiskActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);

            var localSucceeded = true;
            string? firstError = null;
            foreach (var (method, instanceIndex) in VirtualDiskPlans.Methods.Select((value, index) => (value, index)))
            {
                var state = Execute(invocation, package, parameters, method, instanceIndex, plannedImages);
                states.Add(state);
                var actor = CreateActorProgram(invocation, state);
                var initiator = CreateInitiatorProgram(invocation, state, actor);
                database.AddProgram(actor);
                database.AddProgram(initiator);
                var verified = Verify(state);
                localSucceeded &= verified;
                firstError ??= verified ? null : state.Result.Error ?? $"{state.Plan.Title}没有通过本地独立验证。";
                var artifact = CreateArtifact(invocation, state);
                database.AddArtifact(artifact);
                var localEvent = CreateEvent(invocation, stopwatch, state, initiator, artifact.ArtifactId, verified);
                database.AddEvent(localEvent);
                AddFacts(database, invocation, state, actor, initiator, localEvent.LocalEventId, verified);
            }

            AddFact(database, invocation, "virtual_disk.mount_succeeded", JsonValue.Create(localSucceeded), null);
            AddFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce), null);
            var cleanups = states.Select(state => Cleanup(invocation, state)).ToArray();
            foreach (var cleanup in cleanups) database.AddCleanup(cleanup);
            var cleanupFailure = cleanups.FirstOrDefault(value => value.Status != "succeeded");
            if (cleanupFailure is not null)
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "VIRTUAL_DISK_CLEANUP_FAILED", cleanupFailure.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", cleanupFailure.ErrorMessage);
                return 30;
            }

            database.CompleteCapability(invocation.CaseRunId, localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR",
                DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, localSucceeded ? null : "VIRTUAL_DISK_SUBTEST_FAILED",
                localSucceeded ? null : firstError);
            WriteStatus(localSucceeded ? "LOCAL_PASS" : "SAMPLE_ERROR", firstError);
            return localSucceeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var cleanups = EmergencyCleanup(invocation, states, plannedImages);
                    foreach (var cleanup in cleanups) database.AddCleanup(cleanup);
                    var cleanupSucceeded = cleanups.All(value => value.Status == "succeeded");
                    database.CompleteCapability(invocation.CaseRunId, cleanupSucceeded ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, "VIRTUAL_DISK_CONTROLLER_ERROR", exception.Message);
                    return cleanupSucceeded ? 20 : 30;
                }
                catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            }
            return 20;
        }
        finally
        {
            foreach (var state in states) state.Dispose();
            database?.Dispose();
        }
    }

    private static ExecutionState Execute(ControllerInvocation invocation, CapabilityPackage package, JsonObject parameters,
        string method, int instanceIndex, ICollection<string> plannedImages)
    {
        var plan = VirtualDiskPlans.Create(method, invocation.Nonce);
        var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
        var actorPath = package.ResolveProgram(actorDefinition.Executable);
        var methodWorkDir = Path.GetFullPath(Path.Combine(invocation.WorkDir, $"virtual-disk-{plan.FactKey}"));
        Directory.CreateDirectory(methodWorkDir);
        var imagePath = Path.GetFullPath(Path.Combine(methodWorkDir, plan.ImageFileName));
        EnsureScopedPath(invocation.WorkDir, imagePath);
        plannedImages.Add(imagePath);
        VirtualDiskNative.CreateDynamicVhd(imagePath, VirtualDiskPlans.VirtualSizeBytes);
        var imageSha256 = Hashing.FileSha256(imagePath);
        var before = VirtualDiskNative.Inspect(imagePath);
        if (!before.ImageExists || before.Attached) throw new InvalidDataException("Controller 创建后的 VHD 初始状态不正确。");

        var readyPath = Path.Combine(methodWorkDir, "virtual-disk-ready.json");
        var gatePath = Path.Combine(methodWorkDir, "virtual-disk-controller-verified.json");
        var resultPath = Path.Combine(methodWorkDir, "virtual-disk-actor-result.json");
        var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_000;
        var roleTimeout = Math.Min(invocation.TimeoutMs, 180_000);
        var arguments = new[]
        {
            "--method", method,
            "--nonce", invocation.Nonce,
            "--image-path", imagePath,
            "--image-sha256", imageSha256,
            "--ready", readyPath,
            "--gate", gatePath,
            "--result", resultPath,
            "--work-dir", methodWorkDir,
            "--timeout-ms", roleTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--hold-ms", holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var actor = Start(actorPath, arguments, invocation.WorkDir);
        try
        {
            var ready = WaitAndRead<VirtualDiskReady>(readyPath, invocation.TimeoutMs, actor, "虚拟磁盘挂载就绪");
            ValidateReady(plan, imagePath, imageSha256, ready, actor.Id);
            var independent = VirtualDiskNative.Inspect(imagePath);
            if (!independent.Attached || string.IsNullOrWhiteSpace(independent.PhysicalPath)
                || !string.Equals(independent.PhysicalPath, ready.After.PhysicalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Controller 独立句柄没有复核到相同的物理磁盘路径。");
            ProtocolJson.WriteAtomic(gatePath, new VirtualDiskVerificationGate
            {
                Method = method,
                VerifiedAtUtc = DateTimeOffset.UtcNow,
                PhysicalPath = independent.PhysicalPath,
            });
            var result = WaitAndRead<VirtualDiskBehaviorResult>(resultPath, invocation.TimeoutMs, actor, "虚拟磁盘 Actor 结果");
            WaitForExit(actor, invocation.TimeoutMs, "虚拟磁盘 Actor");
            var finalIndependent = VirtualDiskNative.Inspect(imagePath);
            return new ExecutionState(instanceIndex, plan, actorPath, arguments, methodWorkDir, imagePath, imageSha256,
                readyPath, gatePath, resultPath, actor, before, ready, independent, result, finalIndependent);
        }
        catch
        {
            Stop(actor, []);
            actor.Dispose();
            try { VirtualDiskNative.DetachIfAttached(imagePath); } catch { }
            throw;
        }
    }

    private static void ValidateReady(VirtualDiskPlan plan, string imagePath, string imageSha256,
        VirtualDiskReady ready, int actorPid)
    {
        if (!string.Equals(ready.Method, plan.Method, StringComparison.Ordinal)
            || !string.Equals(ready.InvocationKind, plan.InvocationKind, StringComparison.Ordinal)
            || ready.ActorProcessId != actorPid
            || !string.Equals(Path.GetFullPath(ready.ImagePath), imagePath, StringComparison.OrdinalIgnoreCase)
            || ready.VirtualSizeBytes != VirtualDiskPlans.VirtualSizeBytes
            || !string.Equals(ready.ImageSha256, imageSha256, StringComparison.OrdinalIgnoreCase)
            || !ready.Before.ImageExists || ready.Before.Attached
            || !ready.After.ImageExists || !ready.After.Attached
            || string.IsNullOrWhiteSpace(ready.After.PhysicalPath))
            throw new InvalidDataException("虚拟磁盘就绪协议与本地计划不一致。");
        if (plan.Method == VirtualDiskPlans.PowerShell && (ready.InitiatorProcess is null
            || !string.Equals(Path.GetFileName(ready.InitiatorProcess.Executable), "powershell.exe", StringComparison.OrdinalIgnoreCase)
            || ready.InitiatorProcess.ExitCode != 0
            || ready.InitiatorProcess.OperationStartedAtUtc < ready.InitiatorProcess.StartedAtUtc
            || ready.InitiatorProcess.OperationStartedAtUtc > ready.InitiatorProcess.EndedAtUtc
            || !ready.InitiatorProcess.CommandLine.Contains("Mount-DiskImage", StringComparison.Ordinal)
            || !ready.InitiatorProcess.CommandLine.Contains(imagePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("VDISK_POWERSHELL 缺少可信的 Mount-DiskImage 进程观测。");
    }

    private static bool Verify(ExecutionState state)
    {
        var value = state.Result;
        return value.Succeeded
            && value.ControllerGateObserved
            && value.ActorAttachVerified
            && value.ActorDetachVerified
            && value.ReadOnly
            && value.NoDriveLetter
            && value.ActorProcessId == state.Actor.Id
            && string.Equals(value.Method, state.Plan.Method, StringComparison.Ordinal)
            && string.Equals(value.InvocationKind, state.Plan.InvocationKind, StringComparison.Ordinal)
            && string.Equals(Path.GetFullPath(value.ImagePath), state.ImagePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.ImageSha256, state.ImageSha256, StringComparison.OrdinalIgnoreCase)
            && value.VirtualSizeBytes == VirtualDiskPlans.VirtualSizeBytes
            && !value.Before.Attached
            && value.After.Attached
            && !value.Final.Attached
            && state.Independent.Attached
            && !state.FinalIndependent.Attached
            && string.Equals(value.After.PhysicalPath, state.Independent.PhysicalPath, StringComparison.OrdinalIgnoreCase)
            && value.CompletedAtUtc >= value.OccurredAtUtc
            && File.Exists(value.ImagePath)
            && Hashing.FileSha256(value.ImagePath).Equals(value.ImageSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static ProgramObservation CreateActorProgram(ControllerInvocation invocation, ExecutionState state)
    {
        DateTimeOffset startedAt;
        DateTimeOffset? endedAt;
        int? exitCode;
        try { startedAt = state.Actor.StartTime.ToUniversalTime(); } catch { startedAt = state.Result.OccurredAtUtc; }
        try { endedAt = state.Actor.ExitTime.ToUniversalTime(); exitCode = state.Actor.ExitCode; } catch { endedAt = null; exitCode = null; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
            InstanceName = state.Plan.Method,
            InstanceIndex = state.InstanceIndex,
            ExecutablePath = state.ActorPath,
            Sha256 = Hashing.FileSha256(state.ActorPath),
            Sha1 = Hashing.FileSha1(state.ActorPath),
            Md5 = Hashing.FileMd5(state.ActorPath),
            Pid = state.Actor.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(state.Actor),
            Architecture = Architecture(),
            CommandLine = FormatCommandLine(state.ActorPath, state.ActorArguments),
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            ExitCode = exitCode,
            Metadata = new JsonObject { ["method"] = state.Plan.Method, ["role"] = "virtual_disk_lifecycle_actor" },
        };
    }

    private static ProgramObservation CreateInitiatorProgram(ControllerInvocation invocation, ExecutionState state, ProgramObservation actor)
    {
        if (state.Result.InitiatorProcess is not { } process)
        {
            return new ProgramObservation
            {
                CaseRunId = invocation.CaseRunId,
                Role = "target",
                InstanceName = state.Plan.Method,
                InstanceIndex = state.InstanceIndex,
                ExecutablePath = actor.ExecutablePath,
                Sha256 = actor.Sha256,
                Sha1 = actor.Sha1,
                Md5 = actor.Md5,
                Pid = actor.Pid,
                ParentPid = actor.ParentPid,
                SessionId = actor.SessionId,
                Architecture = actor.Architecture,
                CommandLine = actor.CommandLine,
                WorkingDirectory = actor.WorkingDirectory,
                StartedAtUtc = actor.StartedAtUtc,
                EndedAtUtc = actor.EndedAtUtc,
                ExitCode = actor.ExitCode,
                Metadata = new JsonObject { ["method"] = state.Plan.Method, ["invocation_kind"] = state.Plan.InvocationKind },
            };
        }

        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "target",
            InstanceName = state.Plan.Method,
            InstanceIndex = state.InstanceIndex,
            ExecutablePath = process.Executable,
            Sha256 = Hashing.FileSha256(process.Executable),
            Sha1 = Hashing.FileSha1(process.Executable),
            Md5 = Hashing.FileMd5(process.Executable),
            Pid = process.ProcessId,
            ParentPid = actor.Pid,
            SessionId = null,
            Architecture = Architecture(),
            CommandLine = process.CommandLine,
            WorkingDirectory = state.MethodWorkDir,
            StartedAtUtc = process.StartedAtUtc,
            EndedAtUtc = process.EndedAtUtc,
            ExitCode = process.ExitCode,
            Metadata = new JsonObject { ["method"] = state.Plan.Method, ["invocation_kind"] = state.Plan.InvocationKind },
        };
    }

    private static ArtifactObservation CreateArtifact(ControllerInvocation invocation, ExecutionState state)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "virtual_disk_result_protocol",
            RelativePath = Path.GetRelativePath(runDirectory, state.ResultPath).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = Hashing.FileSha256(state.ResultPath),
            SizeBytes = new FileInfo(state.ResultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(state.ResultPath),
            Sensitive = false,
            Metadata = new JsonObject { ["method"] = state.Plan.Method, ["image_sha256"] = state.ImageSha256 },
        };
    }

    private static LocalEventObservation CreateEvent(ControllerInvocation invocation, Stopwatch stopwatch,
        ExecutionState state, ProgramObservation initiator, string artifactId, bool succeeded) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = state.InstanceIndex + 1,
        EventType = "device",
        EventAction = "virtual_disk_mount",
        Nonce = invocation.Nonce,
        OccurredAtUtc = state.Result.OccurredAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "virtual_disk_activity_controller",
        CollectionMethod = $"{state.Plan.InvocationKind}_independent_physical_path_verification",
        Confidence = "high",
        ActorProgramId = initiator.ProgramInstanceId,
        EvidenceRefs = [artifactId],
        Data = new JsonObject
        {
            ["kind"] = "device",
            ["operation"] = "virtual_disk_mount",
            ["method"] = state.Plan.Method,
            ["actor"] = ProcessReference(initiator),
            ["device"] = new JsonObject
            {
                ["instance_id"] = state.Result.After.PhysicalPath,
                ["class_guid"] = null,
                ["vendor_id"] = "Microsoft",
                ["product_id"] = "VHD",
                ["serial_number"] = null,
                ["volume_guid"] = null,
                ["drive_letter"] = null,
                ["mount_point"] = null,
                ["image_path"] = state.ImagePath,
                ["physical_path"] = state.Result.After.PhysicalPath,
                ["device_type"] = "virtual_disk",
                ["virtual_size_bytes"] = state.Result.VirtualSizeBytes,
                ["read_only"] = state.Result.ReadOnly,
                ["no_drive_letter"] = state.Result.NoDriveLetter,
                ["provider"] = "Microsoft Virtual Disk Service",
            },
            ["before"] = SnapshotJson(state.Result.Before),
            ["after"] = SnapshotJson(state.Result.After),
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = succeeded,
                ["win32_error"] = state.Result.Win32Error,
                ["message"] = state.Result.Error,
            },
        },
    };

    private static void AddFacts(RunDatabase database, ControllerInvocation invocation, ExecutionState state,
        ProgramObservation actor, ProgramObservation initiator, string eventId, bool succeeded)
    {
        var prefix = $"virtual_disk.{state.Plan.FactKey}";
        var values = new Dictionary<string, JsonNode?>
        {
            [$"{prefix}.succeeded"] = JsonValue.Create(succeeded),
            [$"{prefix}.occurred_at_utc"] = JsonValue.Create(Values.Utc(state.Result.OccurredAtUtc)),
            [$"{prefix}.completed_at_utc"] = JsonValue.Create(Values.Utc(state.Result.CompletedAtUtc)),
            [$"{prefix}.method"] = JsonValue.Create(state.Plan.Method),
            [$"{prefix}.invocation_kind"] = JsonValue.Create(state.Plan.InvocationKind),
            [$"{prefix}.image_path"] = JsonValue.Create(state.ImagePath),
            [$"{prefix}.image_sha256"] = JsonValue.Create(state.ImageSha256),
            [$"{prefix}.virtual_size_bytes"] = JsonValue.Create(state.Result.VirtualSizeBytes),
            [$"{prefix}.physical_path"] = JsonValue.Create(state.Result.After.PhysicalPath),
            [$"{prefix}.read_only"] = JsonValue.Create(state.Result.ReadOnly),
            [$"{prefix}.no_drive_letter"] = JsonValue.Create(state.Result.NoDriveLetter),
            [$"{prefix}.actor_attach_verified"] = JsonValue.Create(state.Result.ActorAttachVerified),
            [$"{prefix}.controller_attach_verified"] = JsonValue.Create(state.Independent.Attached),
            [$"{prefix}.actor_detach_verified"] = JsonValue.Create(state.Result.ActorDetachVerified),
            [$"{prefix}.controller_detach_verified"] = JsonValue.Create(!state.FinalIndependent.Attached),
            [$"{prefix}.actor_pid"] = JsonValue.Create(actor.Pid),
            [$"{prefix}.actor_executable"] = JsonValue.Create(actor.ExecutablePath),
            [$"{prefix}.initiator_pid"] = JsonValue.Create(initiator.Pid),
            [$"{prefix}.initiator_executable"] = JsonValue.Create(initiator.ExecutablePath),
            [$"{prefix}.initiator_command_line"] = JsonValue.Create(initiator.CommandLine),
        };
        foreach (var (key, value) in values) AddFact(database, invocation, key, value, eventId);
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, ExecutionState state) =>
        CleanupImage(invocation, state.InstanceIndex + 1, state.Actor, state.ImagePath, state.Plan.FactKey);

    private static IReadOnlyList<CleanupObservation> EmergencyCleanup(ControllerInvocation invocation,
        IReadOnlyList<ExecutionState> states, IReadOnlyList<string> plannedImages)
    {
        var actorByImage = states.ToDictionary(value => value.ImagePath, value => value.Actor, StringComparer.OrdinalIgnoreCase);
        return plannedImages.Distinct(StringComparer.OrdinalIgnoreCase).Select((path, index) =>
            CleanupImage(invocation, index + 1, actorByImage.GetValueOrDefault(path), path, $"emergency_{index}")).ToArray();
    }

    private static CleanupObservation CleanupImage(ControllerInvocation invocation, int sequence, Process? actor,
        string imagePath, string label)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        Stop(actor, errors);
        var attachedBefore = false;
        try
        {
            EnsureScopedPath(invocation.WorkDir, imagePath);
            if (File.Exists(imagePath))
            {
                try
                {
                    attachedBefore = VirtualDiskNative.Inspect(imagePath).Attached;
                    if (attachedBefore && Path.GetFileName(imagePath).StartsWith("edr-test-ps-", StringComparison.OrdinalIgnoreCase))
                        DismountPowerShell(imagePath, 30_000);
                    else if (!VirtualDiskNative.DetachIfAttached(imagePath))
                        errors.Add("VHD 仍处于附加状态。");
                }
                catch (Exception exception) { errors.Add($"卸载检查失败：{exception.Message}"); }

                var canDelete = false;
                try { canDelete = !VirtualDiskNative.Inspect(imagePath).Attached; }
                catch
                {
                    // 无效或未完成创建的本轮文件无法由 VirtDisk provider 打开；仍尝试精确删除，
                    // 操作系统会拒绝删除任何仍被设备栈占用的镜像。
                    canDelete = true;
                }
                if (canDelete) DeleteImageWithRetry(imagePath, 5_000);
                else errors.Add("VHD 仍处于附加状态，拒绝删除镜像。");
            }
        }
        catch (Exception exception) { errors.Add(exception.Message); }
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = sequence,
            Action = $"detach_and_delete_virtual_disk_{label}",
            Status = errors.Count == 0 && !IsAlive(actor) && !File.Exists(imagePath) ? "succeeded" : "failed",
            StartedAtUtc = startedAt,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["actor_pid"] = actor?.Id, ["image_path"] = imagePath, ["attached"] = attachedBefore },
            After = new JsonObject { ["actor_alive"] = IsAlive(actor), ["image_exists"] = File.Exists(imagePath) },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static Process Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动虚拟磁盘 Actor：{executable}");
    }

    private static T WaitAndRead<T>(string path, int timeoutMs, Process process, string stage) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"{stage}前 Actor 已退出：{process.ExitCode}");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待{stage}超时。");
            Thread.Sleep(5);
        }
        return ProtocolJson.Read<T>(path);
    }

    private static void WaitForExit(Process process, int timeoutMs, string label)
    {
        if (process.WaitForExit(timeoutMs)) return;
        process.Kill(entireProcessTree: true);
        throw new TimeoutException($"等待{label}退出超时：PID {process.Id}");
    }

    private static void Stop(Process? process, ICollection<string> errors)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5_000)) errors.Add($"Actor PID {process.Id} 未退出。");
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception exception) { errors.Add(exception.Message); }
    }

    private static void EnsureScopedPath(string workDir, string path)
    {
        var root = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("虚拟磁盘路径越出本轮工作目录。");
    }

    private static void DeleteImageWithRetry(string imagePath, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Delete(imagePath);
                if (!File.Exists(imagePath)) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw;
            }
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                throw new IOException($"等待虚拟磁盘镜像释放超时：{imagePath}");
            Thread.Sleep(25);
        }
    }

    private static void DismountPowerShell(string imagePath, int timeoutMs)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var escapedPath = imagePath.Replace("'", "''", StringComparison.Ordinal);
        var script = $"$ErrorActionPreference='Stop'; Dismount-DiskImage -ImagePath '{escapedPath}' -Confirm:$false -ErrorAction Stop | Out-Null";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(imagePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Dismount-DiskImage 兜底清理。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待 Dismount-DiskImage 兜底清理超时：PID {process.Id}");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Dismount-DiskImage 兜底清理失败（退出码 {process.ExitCode}）：{error.Result}");
    }

    private static JsonObject SnapshotJson(VirtualDiskSnapshot value) => new()
    {
        ["exists"] = value.ImageExists,
        ["attached"] = value.Attached,
        ["image_path"] = value.ImagePath,
        ["physical_path"] = value.PhysicalPath,
        ["physical_path_error"] = value.PhysicalPathError,
    };

    private static bool IsAlive(Process? process) { if (process is null) return false; try { return !process.HasExited; } catch { return false; } }
    private static int? TrySessionId(Process process) { try { return process.SessionId; } catch { return null; } }
    private static string Architecture() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x86" => "x86", "arm64" => "arm64", _ => "x64" };
    private static string FormatCommandLine(string executable, IEnumerable<string> arguments) => string.Join(" ", new[] { executable }.Concat(arguments).Select(value => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value));
    private static JsonObject ProcessReference(ProgramObservation value) => new() { ["program_instance_id"] = value.ProgramInstanceId, ["pid"] = value.Pid, ["parent_pid"] = value.ParentPid, ["started_at_utc"] = Values.Utc(value.StartedAtUtc), ["executable"] = value.ExecutablePath, ["command_line"] = value.CommandLine };
    private static void AddFact(RunDatabase database, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) => database.AddFact(new LocalFactObservation { CaseRunId = invocation.CaseRunId, LocalEventId = eventId, Key = key, Value = value, ObservedAtUtc = DateTimeOffset.UtcNow, Source = "virtual_disk_activity_controller", Confidence = "high" });
    private static void WriteStatus(string status, string? error) => Console.WriteLine(new JsonObject { ["schema_version"] = "1.0", ["status"] = status, ["capability_id"] = CapabilityId, ["operation"] = "virtual_disk_mount", ["methods"] = 2, ["error"] = error }.ToJsonString(JsonDefaults.Options));

    private sealed class ExecutionState(
        int instanceIndex, VirtualDiskPlan plan, string actorPath, IReadOnlyList<string> actorArguments,
        string methodWorkDir, string imagePath, string imageSha256, string readyPath, string gatePath,
        string resultPath, Process actor, VirtualDiskSnapshot before, VirtualDiskReady ready,
        VirtualDiskSnapshot independent, VirtualDiskBehaviorResult result, VirtualDiskSnapshot finalIndependent) : IDisposable
    {
        public int InstanceIndex { get; } = instanceIndex;
        public VirtualDiskPlan Plan { get; } = plan;
        public string ActorPath { get; } = actorPath;
        public IReadOnlyList<string> ActorArguments { get; } = actorArguments;
        public string MethodWorkDir { get; } = methodWorkDir;
        public string ImagePath { get; } = imagePath;
        public string ImageSha256 { get; } = imageSha256;
        public string ReadyPath { get; } = readyPath;
        public string GatePath { get; } = gatePath;
        public string ResultPath { get; } = resultPath;
        public Process Actor { get; } = actor;
        public VirtualDiskSnapshot Before { get; } = before;
        public VirtualDiskReady Ready { get; } = ready;
        public VirtualDiskSnapshot Independent { get; } = independent;
        public VirtualDiskBehaviorResult Result { get; } = result;
        public VirtualDiskSnapshot FinalIndependent { get; } = finalIndependent;
        public void Dispose() => Actor.Dispose();
    }
}
