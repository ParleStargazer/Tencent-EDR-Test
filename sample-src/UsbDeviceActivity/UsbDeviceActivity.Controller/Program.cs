using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using EdrTest;

namespace UsbDeviceActivity;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Operations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UsbTestConstants.MountCapabilityId] = "mount",
            [UsbTestConstants.UnmountCapabilityId] = "unmount",
        };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        ControllerInvocation? invocation = null;
        RunDatabase? database = null;
        UsbDriverPackageLease? lease = null;
        var actors = new List<Process>();
        var cleanupRecorded = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            invocation = ControllerInvocation.Parse(args);
            var package = CapabilityCatalog.Load(invocation.ManifestPath);
            if (!Operations.TryGetValue(package.Manifest.CapabilityId, out var operation))
                throw new InvalidDataException($"UsbDeviceActivity Controller 不支持能力：{package.Manifest.CapabilityId}");
            database = RunDatabase.OpenReadWrite(invocation.RunDb);
            database.AddProgram(ProgramObservation.CaptureCurrent(invocation.CaseRunId, "controller"));
            var parameters = JsonNode.Parse(File.ReadAllText(invocation.ParametersPath))?.AsObject()
                ?? throw new InvalidDataException("参数文件不是 JSON 对象。");
            Directory.CreateDirectory(invocation.WorkDir);

            var packageDirectory = Path.GetDirectoryName(Path.GetFullPath(invocation.ManifestPath))
                ?? throw new InvalidDataException("无法定位 USB 能力包目录。");
            var driverPath = Path.Combine(packageDirectory, "UsbUdeTest.sys");
            var infPath = Path.Combine(packageDirectory, "UsbUdeTest.inf");
            var catalogPath = Path.Combine(packageDirectory, "UsbUdeTest.cat");
            var certificatePath = Path.Combine(packageDirectory, "EdrTestDriverTest.cer");
            var metadataPath = Path.Combine(packageDirectory, "usb-driver-package.json");
            var environment = EvaluateEnvironment(driverPath, infPath, catalogPath, certificatePath, metadataPath);
            AddEnvironmentFacts(database, invocation, environment);
            if (!environment.Ready)
            {
                database.AddCleanup(EmptyCleanup(invocation, "usb_environment_not_ready"));
                cleanupRecorded = true;
                database.CompleteCapability(invocation.CaseRunId, "SKIPPED", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "ENVIRONMENT_NOT_READY", environment.Reason);
                WriteStatus("SKIPPED", package.Manifest.CapabilityId, environment.Reason);
                return 10;
            }

            try
            {
                lease = UsbDriverInstaller.Install(infPath);
                AddInstallDiagnostic(database, invocation, lease.Diagnostic);
            }
            catch (UsbDriverInstallException exception)
            {
                AddInstallDiagnostic(database, invocation, exception.Diagnostic);
                WriteInstallDiagnosticArtifact(database, invocation, exception.Diagnostic);
                throw;
            }
            var serial = UsbTestConstants.CreateSerial(invocation.Nonce);
            UsbUdeClient.Detach(ignoreMissing: true);
            var before = UsbDeviceDiscovery.WaitFor(serial, present: false, 5_000);
            if (before.Present) throw new InvalidOperationException("本轮 USB 测试设备在操作前已经存在。");

            var actorDefinition = package.Manifest.Participants.Single(value => value.Role == "actor");
            var actorPath = package.ResolveProgram(actorDefinition.Executable);
            var holdMs = parameters["post_operation_hold_ms"]?.GetValue<int>() ?? 1_500;
            var isolationMs = parameters["setup_isolation_ms"]?.GetValue<int>() ?? 1_000;
            ActorExecution? setup = null;
            ActorExecution operationExecution;
            UsbDeviceSnapshot operationBefore;

            if (operation == "unmount")
            {
                setup = ExecuteActor(invocation, actorPath, "attach", serial, "setup-attach", 0, actors);
                database.AddProgram(setup.Observation);
                database.AddArtifact(CreateArtifact(invocation, setup, "setup_attach", serial));
                operationBefore = UsbDeviceDiscovery.WaitFor(serial, present: true, 15_000);
                if (!setup.Result.Succeeded || !operationBefore.Present)
                    throw new InvalidOperationException(setup.Result.Error ?? "卸载测试的 USB Attach 准备阶段未成功。");
                Thread.Sleep(Math.Max(500, isolationMs));
                operationExecution = ExecuteActor(invocation, actorPath, "detach", serial, "operation-unmount", 1, actors);
            }
            else
            {
                operationBefore = before;
                operationExecution = ExecuteActor(invocation, actorPath, "attach", serial, "operation-mount", 0, actors);
            }

            database.AddProgram(operationExecution.Observation);
            var after = UsbDeviceDiscovery.WaitFor(serial, present: operation == "mount", 15_000);
            var controllerVerified = operation == "mount" ? after.Present : !after.Present;
            var succeeded = operationExecution.Result.Succeeded && controllerVerified
                && string.Equals(operationExecution.Result.SerialNumber, serial, StringComparison.Ordinal)
                && string.Equals(operationExecution.Result.ExpectedInstanceId,
                    UsbTestConstants.ExpectedInstanceId(serial), StringComparison.OrdinalIgnoreCase);
            if (holdMs > 0) Thread.Sleep(holdMs);

            var artifact = CreateArtifact(invocation, operationExecution, operation, serial);
            database.AddArtifact(artifact);
            var localEvent = CreateEvent(invocation, stopwatch, operation, operationBefore, after,
                operationExecution, artifact.ArtifactId, succeeded);
            database.AddEvent(localEvent);
            AddOperationFacts(database, invocation, operation, operationBefore, after, operationExecution,
                setup, lease, localEvent.LocalEventId, succeeded, controllerVerified);
            AddFact(database, invocation, "correlation.nonce", JsonValue.Create(invocation.Nonce), null);

            var cleanup = Cleanup(invocation, lease, serial, actors);
            database.AddCleanup(cleanup);
            cleanupRecorded = true;
            lease = null;
            if (cleanup.Status != "succeeded")
            {
                database.CompleteCapability(invocation.CaseRunId, "CLEANUP_ERROR", DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, "USB_CLEANUP_FAILED", cleanup.ErrorMessage);
                WriteStatus("CLEANUP_ERROR", package.Manifest.CapabilityId, cleanup.ErrorMessage);
                return 30;
            }

            var status = succeeded ? "LOCAL_PASS" : "SAMPLE_ERROR";
            var error = succeeded ? null : operationExecution.Result.Error ?? "Controller 未确认预期 USB PnP 状态。";
            database.CompleteCapability(invocation.CaseRunId, status, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, succeeded ? null : "USB_OUTCOME_MISMATCH", error);
            WriteStatus(status, package.Manifest.CapabilityId, error);
            return succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (invocation is not null && database is not null)
            {
                try
                {
                    var serial = SafeSerial(invocation.Nonce);
                    var cleanup = Cleanup(invocation, lease, serial, actors);
                    if (!cleanupRecorded)
                    {
                        database.AddCleanup(cleanup);
                        cleanupRecorded = true;
                    }
                    database.CompleteCapability(invocation.CaseRunId,
                        cleanup.Status == "succeeded" ? "SAMPLE_ERROR" : "CLEANUP_ERROR",
                        DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                        "USB_CONTROLLER_ERROR", exception.Message);
                    return cleanup.Status == "succeeded" ? 20 : 30;
                }
                catch (Exception cleanupException) { Console.Error.WriteLine(cleanupException); }
            }
            return 20;
        }
        finally
        {
            foreach (var actor in actors) actor.Dispose();
            database?.Dispose();
        }
    }

    private static UsbEnvironmentCheck EvaluateEnvironment(string driverPath, string infPath, string catalogPath,
        string certificatePath, string metadataPath)
    {
        if (!OperatingSystem.IsWindows()) return UsbEnvironmentCheck.NotReady("仅支持 Windows。", driverPath);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return UsbEnvironmentCheck.NotReady("USB UDE 样本仅支持 x64 进程。", driverPath);
        if (!IsAdministrator()) return UsbEnvironmentCheck.NotReady("USB UDE 能力需要管理员权限。", driverPath);
        if (!File.Exists(driverPath) || !File.Exists(infPath) || !File.Exists(catalogPath)
            || !File.Exists(certificatePath) || !File.Exists(metadataPath))
            return UsbEnvironmentCheck.NotReady("能力包缺少 UsbUdeTest SYS、INF、CAT、公开 CER 或元数据。", driverPath);
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject()
            ?? throw new InvalidDataException("usb-driver-package.json 不是 JSON 对象。");
        var actualHash = UsbDeviceActivity.Hashing.Sha256(driverPath);
        var actualInfHash = UsbDeviceActivity.Hashing.Sha256(infPath);
        var actualCatalogHash = UsbDeviceActivity.Hashing.Sha256(catalogPath);
        using var certificate = new X509Certificate2(certificatePath);
        var actualCertificateHash = certificate.GetCertHashString(
            System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant();
        var expectedHash = metadata["sha256"]?.GetValue<string>();
        var expectedInfHash = metadata["inf_sha256"]?.GetValue<string>();
        var expectedCatalogHash = metadata["catalog_sha256"]?.GetValue<string>();
        var expectedCertificateHash = metadata["certificate_sha256"]?.GetValue<string>();
        var signatureValid = metadata["signature_valid"]?.GetValue<bool>() ?? false;
        var catalogMembershipVerified = metadata["catalog_membership_verified"]?.GetValue<bool>() ?? false;
        var signer = metadata["signer"]?.GetValue<string>();
        var thumbprint = metadata["certificate_thumbprint"]?.GetValue<string>()?.Replace(" ", "").ToUpperInvariant();
        var requiresTestSigning = metadata["requires_test_signing"]?.GetValue<bool>() ?? true;
        var privateKeyInPackage = metadata["private_key_in_package"]?.GetValue<bool>() ?? true;
        var hardwareId = metadata["hardware_id"]?.GetValue<string>();
        var vendorId = metadata["emulated_vendor_id"]?.GetValue<string>();
        var productId = metadata["emulated_product_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            return UsbEnvironmentCheck.NotReady("UsbUdeTest.sys 与元数据 SHA256 不一致。", driverPath, actualHash,
                signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash, actualCatalogHash,
                actualCertificateHash, catalogMembershipVerified);
        if (string.IsNullOrWhiteSpace(expectedInfHash)
            || !string.Equals(actualInfHash, expectedInfHash, StringComparison.OrdinalIgnoreCase))
            return UsbEnvironmentCheck.NotReady("UsbUdeTest.inf 与元数据 SHA256 不一致；文件可能被 Git 换行转换。",
                driverPath, actualHash, signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash,
                actualCatalogHash, actualCertificateHash, catalogMembershipVerified);
        if (string.IsNullOrWhiteSpace(expectedCatalogHash)
            || !string.Equals(actualCatalogHash, expectedCatalogHash, StringComparison.OrdinalIgnoreCase))
            return UsbEnvironmentCheck.NotReady("UsbUdeTest.cat 与元数据 SHA256 不一致。", driverPath, actualHash,
                signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash, actualCatalogHash,
                actualCertificateHash, catalogMembershipVerified);
        if (string.IsNullOrWhiteSpace(expectedCertificateHash)
            || !string.Equals(actualCertificateHash, expectedCertificateHash, StringComparison.OrdinalIgnoreCase)
            || certificate.HasPrivateKey
            || !string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            return UsbEnvironmentCheck.NotReady("USB UDE 公开证书哈希、指纹或私钥边界与元数据不一致。",
                driverPath, actualHash, signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash,
                actualCatalogHash, actualCertificateHash, catalogMembershipVerified);
        if (!signatureValid || string.IsNullOrWhiteSpace(thumbprint))
            return UsbEnvironmentCheck.NotReady("UsbUdeTest.sys 未完成可验证的测试签名。", driverPath, actualHash,
                signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash, actualCatalogHash,
                actualCertificateHash, catalogMembershipVerified);
        if (!catalogMembershipVerified)
            return UsbEnvironmentCheck.NotReady("USB UDE 包未声明 SYS/INF 已纳入 CAT。", driverPath, actualHash,
                signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash, actualCatalogHash,
                actualCertificateHash, catalogMembershipVerified);
        if (privateKeyInPackage
            || !string.Equals(hardwareId, UsbTestConstants.HardwareId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(vendorId, UsbTestConstants.VendorId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(productId, UsbTestConstants.ProductId, StringComparison.OrdinalIgnoreCase))
            return UsbEnvironmentCheck.NotReady("USB UDE 包元数据的私钥边界、Hardware ID 或 VID/PID 与样本协议不一致。",
                driverPath, actualHash, signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash,
                actualCatalogHash, actualCertificateHash, catalogMembershipVerified);
        if (!CertificateTrusted(thumbprint))
            return UsbEnvironmentCheck.NotReady("USB UDE 测试证书尚未同时导入 LocalMachine\\Root 与 TrustedPublisher。",
                driverPath, actualHash, signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash,
                actualCatalogHash, actualCertificateHash, catalogMembershipVerified);
        if (requiresTestSigning && !TestSigningEnabled())
            return UsbEnvironmentCheck.NotReady("当前启动项未启用 testsigning；USB 挂载和卸载能力不可用。",
                driverPath, actualHash, signer, signatureValid, thumbprint, requiresTestSigning, actualInfHash,
                actualCatalogHash, actualCertificateHash, catalogMembershipVerified);
        return new UsbEnvironmentCheck(true, null, driverPath, actualHash, actualInfHash, actualCatalogHash,
            actualCertificateHash, signer, signatureValid, thumbprint, requiresTestSigning, catalogMembershipVerified);
    }

    private static ActorExecution ExecuteActor(ControllerInvocation invocation, string actorPath, string operation,
        string serial, string instanceName, int instanceIndex, ICollection<Process> actors)
    {
        var resultPath = Path.Combine(invocation.WorkDir, $"usb-actor-{instanceIndex}-{operation}.json");
        var arguments = new[] { "--operation", operation, "--serial", serial, "--result", resultPath };
        var info = new ProcessStartInfo { FileName = actorPath, WorkingDirectory = invocation.WorkDir, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 USB Actor：{actorPath}");
        actors.Add(process);
        var result = WaitForResult(resultPath, invocation.TimeoutMs, process);
        if (!process.WaitForExit(invocation.TimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"等待 USB Actor 退出超时：PID {process.Id}");
        }
        var observation = CreateActorObservation(invocation, actorPath, arguments, process, result, instanceName, instanceIndex);
        return new ActorExecution(process, observation, result, resultPath);
    }

    private static UsbBehaviorResult WaitForResult(string path, int timeoutMs, Process process)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException($"USB Actor 写入结果前已退出：{process.ExitCode}");
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"等待 USB Actor 结果超时：{path}");
            Thread.Sleep(10);
        }
        return ProtocolJson.Read<UsbBehaviorResult>(path);
    }

    private static ProgramObservation CreateActorObservation(ControllerInvocation invocation, string actorPath,
        IReadOnlyList<string> arguments, Process process, UsbBehaviorResult result, string instanceName, int instanceIndex)
    {
        DateTimeOffset started;
        DateTimeOffset ended;
        try { started = process.StartTime.ToUniversalTime(); } catch { started = result.OccurredAtUtc; }
        try { ended = process.ExitTime.ToUniversalTime(); } catch { ended = result.CompletedAtUtc; }
        return new ProgramObservation
        {
            CaseRunId = invocation.CaseRunId,
            Role = "actor",
            InstanceName = instanceName,
            InstanceIndex = instanceIndex,
            ExecutablePath = actorPath,
            Sha256 = EdrTest.Hashing.FileSha256(actorPath),
            Sha1 = EdrTest.Hashing.FileSha1(actorPath),
            Md5 = EdrTest.Hashing.FileMd5(actorPath),
            Pid = process.Id,
            ParentPid = Environment.ProcessId,
            SessionId = TrySessionId(process),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            CommandLine = $"{Quote(actorPath)} {string.Join(' ', arguments.Select(Quote))}",
            WorkingDirectory = invocation.WorkDir,
            StartedAtUtc = started,
            EndedAtUtc = ended,
            ExitCode = process.ExitCode,
            Metadata = new JsonObject { ["method"] = UsbTestConstants.Method, ["operation"] = result.Operation },
        };
    }

    private static ArtifactObservation CreateArtifact(ControllerInvocation invocation, ActorExecution execution,
        string operation, string serial)
    {
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        return new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "usb_ude_actor_result",
            RelativePath = Path.GetRelativePath(runDirectory, execution.ResultPath).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = EdrTest.Hashing.FileSha256(execution.ResultPath),
            SizeBytes = new FileInfo(execution.ResultPath).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(execution.ResultPath),
            Sensitive = false,
            Metadata = new JsonObject { ["operation"] = operation, ["serial_number"] = serial, ["method"] = UsbTestConstants.Method },
        };
    }

    private static LocalEventObservation CreateEvent(ControllerInvocation invocation, Stopwatch stopwatch, string operation,
        UsbDeviceSnapshot before, UsbDeviceSnapshot after, ActorExecution execution, string artifactId, bool succeeded) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        EventType = "device",
        EventAction = operation == "mount" ? "usb_mount" : "usb_unmount",
        Nonce = invocation.Nonce,
        OccurredAtUtc = execution.Result.OccurredAtUtc,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        MonotonicOffsetMs = stopwatch.ElapsedMilliseconds,
        Source = "usb_device_activity_controller",
        CollectionMethod = "ude_ioctl_with_setupapi_pnp_verification",
        Confidence = "high",
        ActorProgramId = execution.Observation.ProgramInstanceId,
        EvidenceRefs = [artifactId],
        Data = new JsonObject
        {
            ["kind"] = "device",
            ["operation"] = operation == "mount" ? "usb_mount" : "usb_unmount",
            ["method"] = UsbTestConstants.Method,
            ["actor"] = ProcessReference(execution.Observation),
            ["device"] = DeviceJson(operation == "mount" ? after : before),
            ["before"] = SnapshotJson(before),
            ["after"] = SnapshotJson(after),
            ["result"] = new JsonObject
            {
                ["attempted"] = true,
                ["succeeded"] = succeeded,
                ["win32_error"] = execution.Result.Win32Error,
                ["message"] = execution.Result.Error,
            },
        },
    };

    private static void AddEnvironmentFacts(RunDatabase database, ControllerInvocation invocation, UsbEnvironmentCheck environment)
    {
        AddFact(database, invocation, "usb.environment.ready", JsonValue.Create(environment.Ready), null);
        AddFact(database, invocation, "usb.environment.reason", JsonValue.Create(environment.Reason), null);
        AddFact(database, invocation, "usb.package.sha256", JsonValue.Create(environment.Sha256), null);
        AddFact(database, invocation, "usb.package.inf_sha256", JsonValue.Create(environment.InfSha256), null);
        AddFact(database, invocation, "usb.package.catalog_sha256", JsonValue.Create(environment.CatalogSha256), null);
        AddFact(database, invocation, "usb.package.certificate_sha256", JsonValue.Create(environment.CertificateSha256), null);
        AddFact(database, invocation, "usb.package.catalog_membership_verified",
            JsonValue.Create(environment.CatalogMembershipVerified), null);
        AddFact(database, invocation, "usb.package.signer", JsonValue.Create(environment.Signer), null);
        AddFact(database, invocation, "usb.package.signature_valid", JsonValue.Create(environment.SignatureValid), null);
        AddFact(database, invocation, "usb.package.certificate_thumbprint", JsonValue.Create(environment.CertificateThumbprint), null);
        AddFact(database, invocation, "usb.package.requires_test_signing", JsonValue.Create(environment.RequiresTestSigning), null);
    }

    private static void AddInstallDiagnostic(RunDatabase database, ControllerInvocation invocation,
        UsbDriverInstallDiagnostic diagnostic)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["usb.install.stage"] = JsonValue.Create(diagnostic.Stage),
            ["usb.install.win32_error"] = JsonValue.Create(diagnostic.Win32Error),
            ["usb.install.driver_store_present"] = JsonValue.Create(diagnostic.DriverStorePresent),
            ["usb.install.published_inf_path"] = JsonValue.Create(diagnostic.PublishedInfPath),
            ["usb.install.root_devnode_present"] = JsonValue.Create(diagnostic.RootDevNodePresent),
            ["usb.install.root_instance_id"] = JsonValue.Create(diagnostic.RootInstanceId),
            ["usb.install.bound_service"] = JsonValue.Create(diagnostic.BoundService),
            ["usb.install.bound_driver_key"] = JsonValue.Create(diagnostic.BoundDriverKey),
            ["usb.install.bound_inf_name"] = JsonValue.Create(diagnostic.BoundInfName),
            ["usb.install.config_manager_result"] = JsonValue.Create(diagnostic.ConfigManagerResult),
            ["usb.install.devnode_status"] = JsonValue.Create(diagnostic.DevNodeStatus),
            ["usb.install.devnode_problem_code"] = JsonValue.Create(diagnostic.DevNodeProblemCode),
            ["usb.install.devnode_started"] = JsonValue.Create(diagnostic.DevNodeStarted),
            ["usb.install.reboot_required"] = JsonValue.Create(diagnostic.RebootRequired),
            ["usb.install.driver_initialization_stage"] = JsonValue.Create(diagnostic.DriverInitializationStage),
            ["usb.install.driver_initialization_status"] = JsonValue.Create(diagnostic.DriverInitializationStatus),
            ["usb.install.driver_interface_guid"] = JsonValue.Create(diagnostic.DriverInterfaceGuid),
            ["usb.install.expected_interface_guid"] = JsonValue.Create(diagnostic.ExpectedInterfaceGuid),
            ["usb.install.interface_query_win32_error"] = JsonValue.Create(diagnostic.InterfaceQueryWin32Error),
            ["usb.install.interface_present"] = JsonValue.Create(diagnostic.InterfacePresent),
            ["usb.install.interface_path"] = JsonValue.Create(diagnostic.InterfacePath),
        };
        foreach (var (key, value) in values) AddFact(database, invocation, key, value, null);
    }

    private static void WriteInstallDiagnosticArtifact(RunDatabase database, ControllerInvocation invocation,
        UsbDriverInstallDiagnostic diagnostic)
    {
        var path = Path.Combine(invocation.WorkDir, "usb-driver-install-diagnostic.json");
        ProtocolJson.WriteAtomic(path, diagnostic);
        var runDirectory = Directory.GetParent(Directory.GetParent(invocation.WorkDir)!.FullName)!.FullName;
        database.AddArtifact(new ArtifactObservation
        {
            CaseRunId = invocation.CaseRunId,
            Kind = "usb_driver_install_diagnostic",
            RelativePath = Path.GetRelativePath(runDirectory, path).Replace('\\', '/'),
            MediaType = "application/json",
            Sha256 = EdrTest.Hashing.FileSha256(path),
            SizeBytes = new FileInfo(path).Length,
            CreatedAtUtc = File.GetCreationTimeUtc(path),
            Sensitive = false,
            Metadata = new JsonObject
            {
                ["stage"] = diagnostic.Stage,
                ["problem_code"] = diagnostic.DevNodeProblemCode,
                ["driver_initialization_stage"] = diagnostic.DriverInitializationStage,
            },
        });
    }

    private static void AddOperationFacts(RunDatabase database, ControllerInvocation invocation, string operation,
        UsbDeviceSnapshot before, UsbDeviceSnapshot after, ActorExecution execution, ActorExecution? setup,
        UsbDriverPackageLease lease, string eventId, bool succeeded, bool controllerVerified)
    {
        var device = operation == "mount" ? after : before;
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["usb.operation_succeeded"] = JsonValue.Create(succeeded),
            ["usb.operation"] = JsonValue.Create(operation),
            ["usb.method"] = JsonValue.Create(UsbTestConstants.Method),
            ["usb.occurred_at_utc"] = JsonValue.Create(Values.Utc(execution.Result.OccurredAtUtc)),
            ["usb.completed_at_utc"] = JsonValue.Create(Values.Utc(execution.Result.CompletedAtUtc)),
            ["usb.instance_id"] = JsonValue.Create(device.InstanceId),
            ["usb.class_guid"] = JsonValue.Create(device.ClassGuid),
            ["usb.vendor_id"] = JsonValue.Create(device.VendorId),
            ["usb.product_id"] = JsonValue.Create(device.ProductId),
            ["usb.serial_number"] = JsonValue.Create(device.SerialNumber),
            ["usb.description"] = JsonValue.Create(device.Description),
            ["usb.manufacturer"] = JsonValue.Create(device.Manufacturer),
            ["usb.service"] = JsonValue.Create(device.Service),
            ["usb.driver_key"] = JsonValue.Create(device.DriverKey),
            ["usb.volume_guid"] = null,
            ["usb.drive_letter"] = null,
            ["usb.mount_point"] = null,
            ["usb.before_present"] = JsonValue.Create(before.Present),
            ["usb.after_present"] = JsonValue.Create(after.Present),
            ["usb.ioctl_succeeded"] = JsonValue.Create(execution.Result.IoctlSucceeded),
            ["usb.actor_pid"] = JsonValue.Create(execution.Observation.Pid),
            ["usb.actor_executable"] = JsonValue.Create(execution.Observation.ExecutablePath),
            ["usb.actor_command_line"] = JsonValue.Create(execution.Observation.CommandLine),
            ["usb.controller_pnp_verified"] = JsonValue.Create(controllerVerified),
            ["usb.setup_attach_succeeded"] = JsonValue.Create(setup?.Result.Succeeded),
            ["usb.setup_actor_pid"] = JsonValue.Create(setup?.Observation.Pid),
            ["usb.root_instance_id"] = JsonValue.Create(lease.RootInstanceId),
            ["usb.published_inf_path"] = JsonValue.Create(lease.PublishedInfPath),
        };
        foreach (var (key, value) in values) AddFact(database, invocation, key, value, eventId);
    }

    private static CleanupObservation Cleanup(ControllerInvocation invocation, UsbDriverPackageLease? lease,
        string serial, IReadOnlyList<Process> actors)
    {
        var started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        foreach (var actor in actors)
        {
            try
            {
                if (!actor.HasExited) actor.Kill(entireProcessTree: true);
            }
            catch (Exception exception) { errors.Add($"停止 Actor PID {actor.Id} 失败：{exception.Message}"); }
        }
        var presentBefore = false;
        try { presentBefore = UsbDeviceDiscovery.Snapshot(serial).Present; } catch { }
        try { UsbDriverInstaller.Uninstall(lease); }
        catch (Exception exception) { errors.Add(exception.Message); }
        var final = UsbDeviceDiscovery.WaitFor(serial, present: false, 5_000);
        if (final.Present) errors.Add("清理后本轮 USB PnP Instance 仍然存在。");
        if (UsbDriverInstaller.IsRootDevicePresent()) errors.Add("清理后 USB UDE 根设备仍然存在。");
        if (UsbUdeClient.TryGetInterfacePath() is not null) errors.Add("清理后 USB UDE 控制接口仍然存在。");
        return new CleanupObservation
        {
            CaseRunId = invocation.CaseRunId,
            Sequence = 1,
            Action = "detach_usb_remove_ude_root_and_driver_package",
            Status = errors.Count == 0 ? "succeeded" : "failed",
            StartedAtUtc = started,
            EndedAtUtc = DateTimeOffset.UtcNow,
            Before = new JsonObject { ["usb_present"] = presentBefore, ["root_instance_id"] = lease?.RootInstanceId, ["published_inf"] = lease?.PublishedInfPath },
            After = new JsonObject { ["usb_present"] = final.Present, ["root_present"] = UsbDriverInstaller.IsRootDevicePresent(), ["interface_present"] = UsbUdeClient.TryGetInterfacePath() is not null },
            ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors),
        };
    }

    private static CleanupObservation EmptyCleanup(ControllerInvocation invocation, string action) => new()
    {
        CaseRunId = invocation.CaseRunId,
        Sequence = 1,
        Action = action,
        Status = "succeeded",
        StartedAtUtc = DateTimeOffset.UtcNow,
        EndedAtUtc = DateTimeOffset.UtcNow,
    };

    private static JsonObject DeviceJson(UsbDeviceSnapshot value) => new()
    {
        ["instance_id"] = value.InstanceId,
        ["class_guid"] = value.ClassGuid,
        ["vendor_id"] = value.VendorId,
        ["product_id"] = value.ProductId,
            ["serial_number"] = value.SerialNumber,
            ["description"] = value.Description,
            ["manufacturer"] = value.Manufacturer,
            ["service"] = value.Service,
            ["driver_key"] = value.DriverKey,
        ["volume_guid"] = null,
        ["drive_letter"] = null,
        ["mount_point"] = null,
        ["image_path"] = null,
        ["physical_path"] = null,
        ["device_type"] = "usb_virtual",
        ["virtual_size_bytes"] = null,
        ["read_only"] = null,
        ["no_drive_letter"] = null,
        ["provider"] = "UsbUdeTest/UdeCx",
    };

    private static JsonObject SnapshotJson(UsbDeviceSnapshot value) => new()
    {
        ["exists"] = value.Present,
        ["present"] = value.Present,
        ["instance_id"] = value.InstanceId,
        ["observed_at_utc"] = Values.Utc(value.ObservedAtUtc),
    };

    private static JsonObject ProcessReference(ProgramObservation value) => new()
    {
        ["program_instance_id"] = value.ProgramInstanceId,
        ["pid"] = value.Pid,
        ["parent_pid"] = value.ParentPid,
        ["started_at_utc"] = Values.Utc(value.StartedAtUtc),
        ["executable"] = value.ExecutablePath,
        ["command_line"] = value.CommandLine,
    };

    private static void AddFact(RunDatabase database, ControllerInvocation invocation, string key, JsonNode? value, string? eventId) =>
        database.AddFact(new LocalFactObservation
        {
            CaseRunId = invocation.CaseRunId,
            LocalEventId = eventId,
            Key = key,
            Value = value,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Source = "usb_device_activity_controller",
            Confidence = "high",
        });

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool CertificateTrusted(string thumbprint) =>
        StoreContains(StoreName.Root, thumbprint) && StoreContains(StoreName.TrustedPublisher, thumbprint);

    private static bool StoreContains(StoreName storeName, string thumbprint)
    {
        using var store = new X509Store(storeName, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
    }

    private static bool TestSigningEnabled()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/enum");
        info.ArgumentList.Add("{current}");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 bcdedit.exe。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("bcdedit 环境检查超时。");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"bcdedit 环境检查失败：{error}");
        return System.Text.RegularExpressions.Regex.IsMatch(output, @"(?im)^\s*testsigning\s+(Yes|On|是|开启)\s*$");
    }

    private static int? TrySessionId(Process process) { try { return process.SessionId; } catch { return null; } }
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    private static string SafeSerial(string nonce) { try { return UsbTestConstants.CreateSerial(nonce); } catch { return "EDR_USB_UNKNOWN"; } }
    private static void WriteStatus(string status, string capabilityId, string? message) =>
        Console.WriteLine(new JsonObject { ["status"] = status, ["capability_id"] = capabilityId, ["message"] = message }.ToJsonString());

    private sealed record ActorExecution(Process Process, ProgramObservation Observation, UsbBehaviorResult Result, string ResultPath);
    private sealed record UsbEnvironmentCheck(bool Ready, string? Reason, string DriverPath, string? Sha256,
        string? InfSha256, string? CatalogSha256, string? CertificateSha256, string? Signer,
        bool SignatureValid, string? CertificateThumbprint, bool RequiresTestSigning,
        bool CatalogMembershipVerified)
    {
        public static UsbEnvironmentCheck NotReady(string reason, string driverPath, string? sha256 = null,
            string? signer = null, bool signatureValid = false, string? thumbprint = null,
            bool requiresTestSigning = true, string? infSha256 = null, string? catalogSha256 = null,
            string? certificateSha256 = null, bool catalogMembershipVerified = false) =>
            new(false, reason, driverPath, sha256, infSha256, catalogSha256, certificateSha256, signer,
                signatureValid, thumbprint, requiresTestSigning, catalogMembershipVerified);
    }
}
