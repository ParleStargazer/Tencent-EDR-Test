import assert from "node:assert/strict";
import { readFile, stat } from "node:fs/promises";
import test from "node:test";

const root = new URL("../../", import.meta.url);
const readText = (path) => readFile(new URL(path, root), "utf8");
const readJson = async (path) => JSON.parse(await readText(path));

test("驱动三项能力具备 L3 清单、本地绝对基准、直接映射与隔离清理", async () => {
  const capabilityIds = ["win.driver.load", "win.driver.modify", "win.driver.unload"];
  const manifests = await Promise.all(capabilityIds.map((id) =>
    readJson(`sample-src/DriverActivity/manifests/${id}/capability.json`)));
  const baselines = await Promise.all(["load", "modify", "unload"].map((operation) =>
    readText(`baselines/windows/driver_${operation}.yaml`)));
  const controller = await readText("sample-src/DriverActivity/DriverActivity.Controller/Program.cs");
  const behavior = await readText("sample-src/DriverActivity/DriverActivity.Behavior/Program.cs");
  const protocol = await readText("sample-src/DriverActivity/DriverActivity.Protocol/Protocol.cs");
  const nativeDriver = await readText("drivers/EdrTestDriver/src/driver.c");
  const mapping = await readText("mappings/tencent-edr-proc-events-v1.yaml");
  const genericMapping = await readText("mappings/generic-driver-activity-v1.yaml");
  const normalized = await readJson("schemas/normalized-event.schema.json");
  const start = await readText("scripts/Start-EdrTest.ps1");
  const front = await readText("web/app/control-plane.tsx");
  const liveFront = await readText("web/app/live-control-plane.tsx");

  assert.deepEqual(manifests.map((value) => value.capability_id), capabilityIds);
  for (const manifest of manifests) {
    assert.equal(manifest.version, "0.1.0");
    assert.equal(manifest.risk_level, "L3");
    assert.equal(manifest.required_privilege, "administrator");
    assert.equal(manifest.participants[0].role, "actor");
  }
  for (const [index, operation] of ["load", "modify", "unload"].entries()) {
    assert.match(baselines[index], new RegExp(`baseline_id: win\\.driver\\.${operation}`));
    assert.match(baselines[index], /risk_level: L3/);
    assert.match(baselines[index], /max_time_difference_ms: 15/);
    assert.match(baselines[index], /cardinality: \{ min: 1/);
  }
  assert.match(baselines[0], /event_actions: \[load\]/);
  assert.match(baselines[1], /event_actions: \[modify\]/);
  assert.match(baselines[2], /event_actions: \[unload\]/);
  assert.match(controller, /Thread\.Sleep\(Math\.Max\(2_000, isolationMs\)\)/);
  assert.match(controller, /InstanceIndex = instanceIndex/);
  assert.match(controller, /ENVIRONMENT_NOT_READY/);
  assert.match(controller, /stop_delete_exact_driver_service_and_work_copy/);
  const packageFactKeys = [...controller.matchAll(/"(driver\.package\.[^"]+)"/g)]
    .map((match) => match[1]);
  assert.equal(packageFactKeys.length, new Set(packageFactKeys).size,
    "同一 case_run 内的驱动包事实键只能写入一次");
  assert.match(behavior, /CreateServiceW\+StartServiceW/);
  assert.match(behavior, /ControlService\(STOP\)/);
  assert.match(controller, /EDRTEST_DRIVER_MODIFY\|\{invocation\.Nonce\}/);
  assert.match(behavior, /options\.Require\("marker"\)/);
  assert.match(behavior, /AppendMarker\(imagePath, marker\)/);
  assert.match(protocol, /ServiceKernelDriver/);
  assert.match(protocol, /K32EnumDeviceDrivers/);
  assert.match(protocol, /DllImport\("kernel32\.dll", SetLastError = true\)/);
  assert.doesNotMatch(protocol, /DllImport\("psapi\.dll"/);
  assert.match(protocol, /ValidateDriverPath/);
  assert.match(nativeDriver, /DriverEntry/);
  assert.match(nativeDriver, /DriverUnload/);
  assert.doesNotMatch(nativeDriver, /IoCreateDevice|IRP_MJ_|IOCTL|PsSet|ObRegister|CmRegister/);
  assert.ok(normalized.properties.driver.properties.base_address);
  assert.ok(normalized.properties.driver.properties.module_size);
  assert.ok(normalized.properties.driver.properties.hash.properties.md5);
  assert.match(mapping, /route_id: driver-load-kernelmon/);
  assert.match(mapping, /"Action.Name": LoadDriver/);
  assert.match(mapping, /transform: \[signed_int64_to_hex\]/);
  assert.match(mapping, /"driver\.module_size": \{ source: "Child\.ModuleSize"/);
  assert.match(mapping, /route_id: driver-modify-direct-planned/);
  assert.match(mapping, /route_id: driver-unload-direct-planned/);
  assert.match(genericMapping, /route_id: driver-direct/);
  assert.match(start, /Build-DriverActivitySamples\.ps1/);
  assert.match(front, /"win\.driver\.load", "驱动加载", "Driver Loaded", "L3"/);
  assert.match(liveFront, /"win\.driver\.load": "LoadDriver"/);
});

test("平台启动检测驱动环境，证书导入需确认且不修改 testsigning", async () => {
  const start = await readText("scripts/Start-EdrTest.ps1");
  const buildSamples = await readText("scripts/Build-DriverActivitySamples.ps1");
  const theory = await readText("scripts/Test-DriverActivitySamples.ps1");
  const environment = await readText("script/driver/Test-DriverEnvironment.ps1");
  const certificate = await readText("script/driver/New-DriverTestCertificate.ps1");
  const build = await readText("script/driver/Build-DriverPackage.ps1");
  const metadata = await readJson("drivers/EdrTestDriver/prebuilt/x64/driver-package.json");
  const [driverFile, publicCertificate] = await Promise.all([
    stat(new URL("drivers/EdrTestDriver/prebuilt/x64/EdrTestDriver.sys", root)),
    stat(new URL("drivers/cert/EdrTestDriverTest.cer", root)),
  ]);

  assert.match(start, /DriverCertificateImportMode = "Prompt"/);
  assert.match(start, /EwdkRoot = "F:\\EWDK"/);
  assert.match(start, /WindowsBuiltInRole\]::Administrator/);
  assert.match(start, /Test-CurrentBootTestSigning/);
  assert.match(start, /Resolve-DriverTestPackage/);
  assert.match(start, /repository-prebuilt/);
  assert.match(start, /ewdk-fallback/);
  assert.ok(start.indexOf("repository-prebuilt") < start.indexOf("ewdk-fallback"));
  assert.match(start, /Remove-DriverActivitySamplePackages/);
  assert.match(start, /平台其他能力继续启动/);
  assert.match(start, /是否导入测试用证书到 LocalMachine\\Root 和 LocalMachine\\TrustedPublisher/);
  assert.match(start, /Import-DriverTestCertificate/);
  assert.match(start, /certificateMatchesPackage/);
  assert.match(start, /不开启会导致驱动加载与卸载能力不可用/);
  assert.doesNotMatch(start, /\/set testsigning on/);
  assert.doesNotMatch(start, /pnputil\.exe/);
  assert.match(environment, /ready_for_load/);
  assert.match(certificate, /KeyExportPolicy NonExportable/);
  assert.match(certificate, /PrivateKeyExported = \$false/);
  assert.match(build, /\[string\]\$EwdkRoot = "F:\\EWDK"/);
  assert.match(build, /certificate_thumbprint/);
  assert.match(build, /private_key_in_package = \$false/);
  assert.match(buildSamples, /Get-AuthenticodeSignature/);
  assert.match(buildSamples, /DriverCertificatePath/);
  assert.match(buildSamples, /拒绝使用未签名的 SYS\/CAT/);
  assert.match(theory, /-OutputPath \$theoryPackage/);
  assert.doesNotMatch(theory, /-UpdatePrebuilt/);
  assert.equal(metadata.signature_valid, true);
  assert.equal(metadata.private_key_in_package, false);
  assert.match(metadata.certificate_thumbprint, /^[0-9A-F]{40}$/);
  assert.ok(driverFile.size > 0);
  assert.ok(publicCertificate.size > 0);
});
