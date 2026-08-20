import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { DatabaseSync } from "node:sqlite";
import test from "node:test";

const root = new URL("../../", import.meta.url);

async function readJson(relativePath) {
  return JSON.parse(await readFile(new URL(relativePath, root), "utf8"));
}

const expectedActionCounts = {
  process: 6,
  file: 5,
  account: 5,
  network: 5,
  hash: 3,
  registry: 3,
  scheduled_task: 3,
  service: 3,
  driver: 3,
  device: 3,
  group_policy: 1,
  named_pipe: 2,
  edr_sysops: 6,
  wmi: 3,
  bits: 1,
  powershell: 1,
};

function actionsFromProperty(property) {
  if (Array.isArray(property.enum)) return property.enum;
  if (typeof property.const === "string") return [property.const];
  throw new Error("动作属性没有 enum 或 const");
}

function conditionalByConst(schema, property, value) {
  return schema.allOf.find(
    (entry) => entry.if?.properties?.[property]?.const === value,
  );
}

test("轮次 Schema 与分类事件 Schema 使用 1.1 数据契约", async () => {
  const runSchema = await readJson("schemas/run-export.schema.json");
  const dataSchema = await readJson("schemas/local-event-data.schema.json");

  assert.equal(runSchema.$schema, "https://json-schema.org/draft/2020-12/schema");
  assert.equal(runSchema.properties.schema_version.const, "1.1");
  assert.equal(runSchema.properties.run.properties.database_schema_version.const, 2);
  assert.equal(dataSchema.$schema, "https://json-schema.org/draft/2020-12/schema");
  assert.ok(runSchema.required.includes("cleanup_results"));
  assert.equal(dataSchema.oneOf.length, 16);
  assert.equal(runSchema.properties.programs.minItems, undefined);
});

test("验证结果 Schema 支持逐能力 JSON 对照与多候选高亮", async () => {
  const schema = await readJson("schemas/validation-result.schema.json");
  const baselineSchema = await readJson("schemas/baseline.schema.json");
  const capability = schema.properties.capabilities.items;
  const candidate = capability.properties.edr_candidates.items;

  assert.ok(capability.required.includes("local_export_block"));
  assert.ok(capability.required.includes("local_baseline_matches"));
  assert.ok(capability.required.includes("method_selection"));
  assert.ok(capability.required.includes("method_results"));
  assert.ok(capability.required.includes("stage_flow"));
  assert.ok(capability.required.includes("stage_results"));
  assert.equal(capability.properties.method_selection.oneOf[0].properties.strategy.const, "best");
  assert.ok(baselineSchema.properties.method_selection.properties.strategy.enum.includes("best"));
  assert.equal(baselineSchema.$defs.stage.properties.depends_on.type, "string");
  assert.equal(baselineSchema.$defs.cloud_relationship.properties.type.const, "same_process_continuity");
  assert.ok(candidate.required.includes("baseline_matches"));
  assert.deepEqual(
    candidate.properties.baseline_matches.items.properties.kind.enum,
    ["correlation", "assertion", "custom_filter"],
  );
  assert.ok(candidate.required.includes("anchor_qualified"));
  assert.ok(candidate.required.includes("custom_action_name_matched"));
  assert.ok(candidate.required.includes("custom_child_file_create_op_name_matched"));
  assert.ok(candidate.required.includes("time_offset_ms"));
  assert.ok(candidate.required.includes("local_event_time_utc"));
  assert.deepEqual(candidate.properties.time_offset_ms.type, ["integer", "null"]);
  assert.equal(schema.properties.inputs.properties.action_name_standards.type, "object");
  assert.equal(schema.properties.inputs.properties.child_file_create_op_name_standards.type, "object");
  assert.deepEqual(
    schema.properties.inputs.properties.child_file_create_op_name_standards.propertyNames.enum,
    ["win.file.create", "win.file.open", "win.file.delete", "win.file.modify", "win.file.rename"],
  );
  assert.ok(baselineSchema.properties.correlation.required.includes("max_time_difference_ms"));
  assert.equal(baselineSchema.properties.correlation.properties.max_time_difference_ms.maximum, 60000);
  assert.equal(
    candidate.properties.baseline_matches.items.properties.raw_json_pointer.type.includes("null"),
    true,
  );
});

test("能力包 1.1 模板具备中英名称、Actor 和安全相对 EXE 路径", async () => {
  const schema = await readJson("schemas/capability-manifest.schema.json");
  const template = await readJson("examples/capability-package/capability.json");
  const executablePattern = new RegExp(schema.$defs.program.properties.executable.pattern);

  assert.ok(schema.properties.schema_version.enum.includes("1.1"));
  assert.equal(template.schema_version, "1.1");
  assert.ok(template.display_name_zh.length > 0);
  assert.ok(template.display_name_en.length > 0);
  assert.ok(template.participants.some((participant) => participant.role === "actor"));
  assert.ok(executablePattern.test(template.controller.executable));
  assert.equal(executablePattern.test("..\\outside.exe"), false);
  assert.equal(executablePattern.test("C:\\outside.exe"), false);
});

test("16 个能力域定义了 53 项且 envelope 与 data 动作一致", async () => {
  const runSchema = await readJson("schemas/run-export.schema.json");
  const dataSchema = await readJson("schemas/local-event-data.schema.json");
  const seen = new Set();

  for (const [eventType, expectedCount] of Object.entries(expectedActionCounts)) {
    const envelopeActions = actionsFromProperty(
      runSchema.$defs[`${eventType}_event`].then.properties.event_action,
    );
    const dataActions = actionsFromProperty(
      dataSchema.$defs[eventType].properties.operation,
    );

    assert.deepEqual(envelopeActions, dataActions, `${eventType} 动作定义不一致`);
    assert.equal(dataActions.length, expectedCount, `${eventType} 动作数量错误`);
    dataActions.forEach((action) => {
      const key = `${eventType}/${action}`;
      assert.equal(seen.has(key), false, `动作重复：${key}`);
      seen.add(key);
    });
  }

  assert.equal(seen.size, 53);
});

test("关键分类约束保持算法、传输协议与专属证据一致", async () => {
  const dataSchema = await readJson("schemas/local-event-data.schema.json");
  const hash = dataSchema.$defs.hash;
  const network = dataSchema.$defs.network;

  assert.deepEqual(hash.properties.algorithm.enum, [
    "md5",
    "sha1",
    "sha256",
    "sha512",
    "imphash",
  ]);
  assert.equal(
    conditionalByConst(hash, "operation", "md5").then.properties.algorithm.const,
    "md5",
  );
  assert.deepEqual(
    conditionalByConst(hash, "operation", "sha").then.properties.algorithm.enum,
    ["sha1", "sha256", "sha512"],
  );
  assert.equal(
    conditionalByConst(hash, "operation", "imphash").then.properties.algorithm.const,
    "imphash",
  );
  assert.equal(
    conditionalByConst(network, "operation", "tcp_connect")
      .then.properties.connection.properties.transport.const,
    "tcp",
  );
  assert.equal(
    conditionalByConst(network, "operation", "udp_connect")
      .then.properties.connection.properties.transport.const,
    "udp",
  );
  assert.ok(dataSchema.$defs.endpoint.required.includes("family"));
  assert.ok(dataSchema.$defs.file.properties.open.required.includes("desired_access"));
  assert.ok(dataSchema.$defs.wmi.allOf.length >= 3);
});

test("File Manipulation 五项源码清单、Canonical 字段与腾讯路由完整", async () => {
  const normalizedSchema = await readJson("schemas/normalized-event.schema.json");
  const mapping = await readFile(
    new URL("mappings/tencent-edr-proc-events-v1.yaml", root),
    "utf8",
  );
  const capabilities = ["create", "open", "delete", "modify", "rename"];

  for (const operation of capabilities) {
    const manifest = await readJson(
      `sample-src/FileManipulation/manifests/win.file.${operation}/capability.json`,
    );
    const baseline = await readFile(
      new URL(`baselines/windows/file_${operation}.yaml`, root),
      "utf8",
    );
    assert.equal(manifest.capability_id, `win.file.${operation}`);
    assert.equal(manifest.version, "0.2.0");
    assert.equal(manifest.risk_level, "L0");
    assert.deepEqual(manifest.participants.map((item) => item.role), ["actor"]);
    assert.ok(manifest.expected_fact_keys.includes(`file.${operation}_succeeded`));
    assert.ok(manifest.expected_fact_keys.includes(`file.txt.${operation}_succeeded`));
    assert.ok(manifest.expected_fact_keys.includes(`file.json.${operation}_succeeded`));
    assert.ok(manifest.expected_fact_keys.includes("file.json.occurred_at_utc"));
    assert.ok(manifest.expected_fact_keys.includes("file.json.extension"));
    assert.match(baseline, /method_selection: \{ strategy: best \}/);
    assert.match(baseline, /method: \{ id: txt, title: TXT 文件 \}/);
    assert.match(baseline, /method: \{ id: json, title: JSON 文件 \}/);
    assert.match(baseline, new RegExp(`file-${operation}-txt-event`));
    assert.match(baseline, new RegExp(`file-${operation}-json-event`));
    assert.match(mapping, new RegExp(`route_id: file-${operation}`));
  }

  const imageLoadBaseline = await readFile(
    new URL("baselines/windows/process_image_load.yaml", root),
    "utf8",
  );
  assert.match(imageLoadBaseline, /method_selection:\s*\n\s*strategy: best/);
  assert.equal((imageLoadBaseline.match(/^\s+method: /gm) ?? []).length, 4);

  [
    "old_path",
    "created",
    "modified",
    "accessed",
    "operation_name",
    "content_type",
    "format",
    "driver_type",
    "encrypted",
    "io",
  ].forEach((field) => assert.ok(field in normalizedSchema.properties.file.properties));
  assert.match(mapping, /"Child\.FileCreateOpName": 新建文件/);
  assert.match(mapping, /"Child\.FileCreateOpName": 打开文件/);
  assert.match(mapping, /"Child\.FileCreateOpName": 覆盖写文件/);
  assert.match(mapping, /source: "Child\.OldFilePath"/);
  assert.match(mapping, /source: "Child\.FileTotalRead"/);
  assert.match(mapping, /source: "Child\.FileTotalWrite"/);
  assert.match(mapping, /route_id: telemetry-candidate-discovery/);
  assert.match(mapping, /route_id: process-candidate-discovery/);
  assert.match(mapping, /"event\.action": \{ source: "Action\.Name", on_empty: unknown \}/);
});

test("User Account Activity 五项清单、BASELINE、Canonical 字段和权限提示完整", async () => {
  const normalizedSchema = await readJson("schemas/normalized-event.schema.json");
  const mapping = await readFile(
    new URL("mappings/tencent-edr-proc-events-v1.yaml", root),
    "utf8",
  );
  const startScript = await readFile(
    new URL("scripts/Start-EdrTest.ps1", root),
    "utf8",
  );
  const buildScript = await readFile(
    new URL("scripts/Build-UserAccountActivitySamples.ps1", root),
    "utf8",
  );
  const accountController = await readFile(
    new URL("sample-src/UserAccountActivity/UserAccountActivity.Controller/Program.cs", root),
    "utf8",
  );
  const capabilities = [
    ["win.account.local.create", "account_local_create.yaml", "local_create", 4720],
    ["win.account.local.modify", "account_local_modify.yaml", "local_modify", 4738],
    ["win.account.local.delete", "account_local_delete.yaml", "local_delete", 4726],
    ["win.account.login", "account_login.yaml", "login", 4624],
    ["win.account.logoff", "account_logoff.yaml", "logoff", 4634],
  ];

  for (const [capabilityId, baselineName, operation, eventId] of capabilities) {
    const manifest = await readJson(
      `sample-src/UserAccountActivity/manifests/${capabilityId}/capability.json`,
    );
    const baseline = await readFile(
      new URL(`baselines/windows/${baselineName}`, root),
      "utf8",
    );
    assert.equal(manifest.capability_id, capabilityId);
    assert.equal(manifest.version, "0.1.0");
    assert.equal(manifest.risk_level, "L1");
    assert.equal(manifest.required_privilege, "administrator");
    assert.deepEqual(manifest.participants.map((item) => item.role), ["actor"]);
    assert.ok(manifest.expected_fact_keys.includes(`account.${operation}_succeeded`));
    assert.ok(manifest.expected_fact_keys.includes("account.occurred_at_utc"));
    assert.ok(manifest.expected_fact_keys.includes("account.name"));
    assert.ok(manifest.expected_fact_keys.includes("account.sid"));
    assert.match(baseline, /max_time_difference_ms: 15/);
    assert.match(baseline, new RegExp(`expected: ${eventId}|expected: \\[4634, 4647\\]`));
    if (capabilityId === "win.account.local.create") {
      assert.match(baseline, /accepted_values:/);
      assert.match(baseline, /C:\\Windows\\System32\\lsass\.exe/);
      assert.match(baseline, /缺少上级调用链，需要优化/);
    }
  }

  assert.ok("target" in normalizedSchema.properties.user.properties);
  assert.ok("winlog" in normalizedSchema.properties);
  assert.match(mapping, /route_id: account-local-create/);
  assert.match(mapping, /route_id: account-local-modify/);
  assert.match(mapping, /route_id: account-local-delete/);
  assert.match(mapping, /route_id: account-login/);
  assert.match(mapping, /route_id: account-logoff/);
  [4720, 4738, 4726, 4624, 4634, 4647].forEach((eventId) =>
    assert.match(mapping, new RegExp(`"Action\\.EventLogId": .*${eventId}|"Action\\.EventLogId": ${eventId}`)),
  );
  assert.match(startScript, /建议关闭后使用管理员权限重新运行/);
  assert.match(startScript, /Build-UserAccountActivitySamples\.ps1/);
  assert.match(buildScript, /当前 PowerShell 未以管理员身份运行/);
  assert.doesNotMatch(accountController, /"--password"/);
  assert.match(accountController, /缺少本轮 nonce 所有权标记，拒绝删除/);
  assert.match(accountController, /contains_password.*false/s);
});

test("Registry Activity 双方法样本、真实 RegEvents 字段和 BASSLINE 校准完整", async () => {
  const normalizedSchema = await readJson("schemas/normalized-event.schema.json");
  const profile = await readJson("docs/reference/tencent-edr-reg-events-field-profile.json");
  const mapping = await readFile(new URL("mappings/tencent-edr-proc-events-v1.yaml", root), "utf8");
  const controller = await readFile(new URL("sample-src/RegistryActivity/RegistryActivity.Controller/Program.cs", root), "utf8");
  const behavior = await readFile(new URL("sample-src/RegistryActivity/RegistryActivity.Behavior/Program.cs", root), "utf8");

  for (const operation of ["create", "modify", "delete"]) {
    const manifest = await readJson(`sample-src/RegistryActivity/manifests/win.registry.${operation}/capability.json`);
    const baseline = await readFile(new URL(`baselines/windows/registry_${operation}.yaml`, root), "utf8");
    assert.equal(manifest.version, "0.2.0");
    assert.ok(manifest.expected_fact_keys.includes(`registry.isolated_key.${operation}_succeeded`));
    assert.ok(manifest.expected_fact_keys.includes(`registry.run_key_native.${operation}_succeeded`));
    assert.match(baseline, /method_selection: \{ strategy: best \}/);
    assert.match(baseline, /method: \{ id: isolated_key,/);
    assert.match(baseline, /method: \{ id: run_key_native,/);
    assert.match(baseline, /max_time_difference_ms: 15/);
  }

  ["old_value_data", "value_type", "old_value_type", "group_name"].forEach((field) =>
    assert.ok(field in normalizedSchema.properties.registry.properties));
  assert.equal(profile.source.event_count, 2168);
  assert.equal(profile.event_identity.action_names.RegSetValue, 2168);
  assert.equal(profile.registry_groups["启动项"], 26);
  assert.equal(profile.old_value_consistency.old_type_zero_and_old_data_present, 0);
  assert.match(controller, /Methods = \["isolated_key", "run_key_native"\]/);
  assert.match(controller, /remove_exact_run_value_only/);
  assert.match(behavior, /RegSetValueExW/);
  assert.match(behavior, /RegDeleteValueW/);
  assert.match(mapping, /route_id: registry-set-value/);
  assert.match(mapping, /source: "Child\.RegOldValData"/);
  assert.match(mapping, /source: "Child\.RegGroupName"/);
  const compareService = await readFile(new URL("src/EdrTest/CompareService.cs", root), "utf8");
  assert.match(compareService, /registry_hive_path/);
  assert.match(compareService, /hkey_users\\\\s-1-5-21/);
});

test("Scheduled Task Activity 三项样本、真实字段清单、映射和 BASSLINE 完整", async () => {
  const normalizedSchema = await readJson("schemas/normalized-event.schema.json");
  const profile = await readJson("docs/reference/tencent-edr-scheduled-task-events-field-profile.json");
  const mapping = await readFile(new URL("mappings/tencent-edr-proc-events-v1.yaml", root), "utf8");
  const protocol = await readFile(new URL("sample-src/ScheduledTaskActivity/ScheduledTaskActivity.Protocol/Protocol.cs", root), "utf8");
  const behavior = await readFile(new URL("sample-src/ScheduledTaskActivity/ScheduledTaskActivity.Behavior/Program.cs", root), "utf8");
  const controller = await readFile(new URL("sample-src/ScheduledTaskActivity/ScheduledTaskActivity.Controller/Program.cs", root), "utf8");
  const startScript = await readFile(new URL("scripts/Start-EdrTest.ps1", root), "utf8");
  const liveControlPlane = await readFile(new URL("web/app/live-control-plane.tsx", root), "utf8");
  const definitions = [
    ["create", 4698, "SchedTaskCreate"],
    ["modify", 4702, "SchedTaskUpdate"],
    ["delete", 4699, "SchedTaskDelete"],
  ];

  for (const [operation, eventId] of definitions) {
    const manifest = await readJson(`sample-src/ScheduledTaskActivity/manifests/win.scheduled_task.${operation}/capability.json`);
    const baseline = await readFile(new URL(`baselines/windows/scheduled_task_${operation}.yaml`, root), "utf8");
    assert.equal(manifest.version, "0.2.0");
    assert.equal(manifest.risk_level, "L1");
    assert.equal(manifest.required_privilege, "standard_user");
    assert.ok(manifest.expected_fact_keys.includes(`scheduled_task.${operation}_succeeded`));
    for (const method of ["task_scheduler_com", "schtasks_cli"]) {
      const factPrefix = `scheduled_task.${method}`;
      assert.ok(manifest.expected_fact_keys.includes(`${factPrefix}.${operation}_succeeded`));
      assert.ok(manifest.expected_fact_keys.includes(`${factPrefix}.task_path`));
      assert.ok(manifest.expected_fact_keys.includes(`${factPrefix}.actor_pid`));
      assert.match(baseline, new RegExp(`method: \\{ id: ${method},`));
    }
    assert.match(baseline, /method_selection: \{ strategy: best \}/);
    assert.match(baseline, /max_time_difference_ms: 15/);
    assert.match(baseline, new RegExp(`expected: ${eventId}`));
    assert.match(baseline, /cloud_field: scheduled_task\.name/);
  }

  assert.ok("scheduled_task" in normalizedSchema.properties);
  assert.deepEqual(Object.keys(normalizedSchema.properties.scheduled_task.properties), ["name", "content", "command", "arguments"]);
  assert.equal(profile.sources.length, 4);
  assert.equal(profile.sources[0].action_name, "SchedTaskCreate");
  assert.equal(profile.sources[1].action_name, "SchedTaskUpdate");
  assert.equal(profile.sources[2].action_name, "RpcSchedTaskCreate");
  assert.equal(profile.sources[2].filtered_event_count, 5);
  assert.equal(profile.sources[3].event_count, 560);
  assert.equal(profile.union_field_count, 107);
  assert.equal(profile.all_exported_fields.length, 107);
  assert.equal(profile.rpc_hook_all_exported_fields.length, 130);
  assert.equal(profile.rpc_hook_baseline_fields.length, 8);
  assert.match(mapping, /route_id: scheduled-task-rpc-register/);
  assert.match(mapping, /"event\.action": \{ constant: register \}/);
  assert.match(mapping, /route_id: scheduled-task-create/);
  assert.match(mapping, /route_id: scheduled-task-modify/);
  assert.match(mapping, /route_id: scheduled-task-delete/);
  assert.match(mapping, /route_id: scheduled-task-candidate-discovery/);
  assert.match(mapping, /source: "Child\.TaskContent"/);
  assert.match(mapping, /sources: \["Child\.TaskContentNew", "Child\.TaskContent"\]/);
  assert.match(mapping, /source: "Child\.TaskArg"/);
  assert.match(protocol, /TaskLogonInteractiveToken/);
  assert.match(protocol, /new XElement\(TaskNamespace \+ "Enabled", enabled\)/);
  assert.match(protocol, /TimeTrigger/);
  assert.match(protocol, /AllowStartOnDemand", false/);
  assert.match(behavior, /schtasks\.exe/);
  assert.match(behavior, /0CCE9227-69AE-11D9-BED3-505054503030/);
  assert.match(behavior, /dateCandidates/);
  assert.match(behavior, /"\/Change"/);
  assert.match(behavior, /"\/Delete"/);
  assert.match(behavior, /"modify" => 4702/);
  assert.match(behavior, /"delete" => 4699/);
  assert.match(controller, /task_scheduler_com/);
  assert.match(controller, /schtasks_cli/);
  assert.match(controller, /delete_exact_test_task_/);
  assert.match(controller, /task_was_never_started/);
  assert.match(startScript, /Build-ScheduledTaskActivitySamples\.ps1/);
  assert.match(liveControlPlane, /"win\.scheduled_task\.create": "SchedTaskCreate, RpcSchedTaskCreate"/);
  assert.match(liveControlPlane, /"win\.scheduled_task\.modify": "SchedTaskUpdate, RpcSchedTaskCreate"/);
  assert.match(liveControlPlane, /"win\.scheduled_task\.delete": "SchedTaskDelete"/);
});

test("Service Activity 三项安全样本、SCM 本地基准、映射和 BASSLINE 完整", async () => {
  const normalizedSchema = await readJson("schemas/normalized-event.schema.json");
  const profile = await readJson("docs/reference/tencent-edr-service-events-field-profile.json");
  const mapping = await readFile(new URL("mappings/tencent-edr-proc-events-v1.yaml", root), "utf8");
  const genericMapping = await readFile(new URL("mappings/generic-service-activity-v1.yaml", root), "utf8");
  const protocol = await readFile(new URL("sample-src/ServiceActivity/ServiceActivity.Protocol/Protocol.cs", root), "utf8");
  const behavior = await readFile(new URL("sample-src/ServiceActivity/ServiceActivity.Behavior/Program.cs", root), "utf8");
  const controller = await readFile(new URL("sample-src/ServiceActivity/ServiceActivity.Controller/Program.cs", root), "utf8");
  const buildScript = await readFile(new URL("scripts/Build-ServiceActivitySamples.ps1", root), "utf8");
  const startScript = await readFile(new URL("scripts/Start-EdrTest.ps1", root), "utf8");
  const liveControlPlane = await readFile(new URL("web/app/live-control-plane.tsx", root), "utf8");
  const definitions = [
    ["create", "CreateServiceW", 7045, 2],
    ["modify", "ChangeServiceConfigW", 7040, 2],
    ["delete", "DeleteService", null, 1],
  ];

  for (const [operation, nativeApi, eventId, methodCount] of definitions) {
    const manifest = await readJson(`sample-src/ServiceActivity/manifests/win.service.${operation}/capability.json`);
    const baseline = await readFile(new URL(`baselines/windows/service_${operation}.yaml`, root), "utf8");
    assert.equal(manifest.capability_id, `win.service.${operation}`);
    assert.equal(manifest.version, "0.1.0");
    assert.equal(manifest.risk_level, "L1");
    assert.equal(manifest.required_privilege, "administrator");
    assert.deepEqual(manifest.participants.map((item) => item.role), ["actor"]);
    assert.ok(manifest.expected_fact_keys.includes(`service.${operation}_succeeded`));
    assert.ok(manifest.expected_fact_keys.includes("service.name"));
    assert.ok(manifest.expected_fact_keys.includes("service.native_api"));
    assert.ok(manifest.expected_fact_keys.includes("service.actor_pid"));
    assert.ok(manifest.expected_fact_keys.includes("service.before.exists"));
    assert.ok(manifest.expected_fact_keys.includes("service.after.exists"));
    assert.match(baseline, /max_time_difference_ms: 25/);
    assert.match(baseline, new RegExp(`expected: ${nativeApi}`));
    assert.match(baseline, /cloud_field: service\.name/);
    assert.match(baseline, /method_selection: \{ strategy: best \}/);
    assert.equal((baseline.match(/^\s+method: /gm) ?? []).length, methodCount);
    if (eventId === null) {
      assert.match(baseline, /method: \{ id: scm_api_hook, title: SCM DeleteService API Hook \}/);
    } else {
      assert.match(baseline, new RegExp(`expected: ${eventId}`));
    }
  }

  assert.ok("service" in normalizedSchema.properties);
  assert.deepEqual(Object.keys(normalizedSchema.properties.service.properties), [
    "name", "display_name", "binary_path", "start_type", "old_start_type", "account", "service_type", "state",
  ]);
  assert.equal(profile.source.confirmed_action_name, "StartService");
  assert.equal(profile.source.confirmed_event_count, 8);
  assert.equal(profile.source.union_field_count, 129);
  assert.equal(profile.target_activity_coverage.CreateService, 0);
  assert.match(genericMapping, /"event\.type": \{ source: event_type \}/);
  assert.match(mapping, /route_id: service-create-hook/);
  assert.match(mapping, /route_id: service-create-system-event/);
  assert.match(mapping, /route_id: service-modify-hook/);
  assert.match(mapping, /route_id: service-modify-system-event/);
  assert.match(mapping, /route_id: service-delete-hook/);
  assert.match(mapping, /route_id: service-candidate-discovery/);
  assert.match(mapping, /sources: \["Child\.ServiceName", "Child\.ServiceKeyName", "Child\.NodeName"\]/);
  assert.match(protocol, /CreateServiceW/);
  assert.match(protocol, /ChangeServiceConfigW/);
  assert.match(protocol, /DeleteService/);
  assert.match(protocol, /QueryServiceConfigW/);
  assert.match(protocol, /QueryServiceStatusEx/);
  assert.match(protocol, /EdrTestSvc_/);
  assert.match(protocol, /rem EDRTEST_/);
  assert.match(behavior, /"create" => 7045/);
  assert.match(behavior, /"modify" => 7040/);
  assert.match(behavior, /"delete" => null/);
  assert.match(controller, /delete_exact_test_service/);
  assert.match(controller, /service_was_never_started/);
  assert.match(buildScript, /当前 PowerShell 未以管理员身份运行/);
  assert.match(startScript, /Build-ServiceActivitySamples\.ps1/);
  assert.match(liveControlPlane, /"win\.service\.create": "CreateService, CreateServiceW, RpcCreateService, ServiceInstall"/);
  assert.match(liveControlPlane, /"win\.service\.modify": "ChangeServiceConfig, ChangeServiceConfigW, ServiceConfigChange"/);
  assert.match(liveControlPlane, /"win\.service\.delete": "DeleteService, DeleteServiceW"/);
});

test("Network Activity 五项清单、BASELINE、回环编排和腾讯路由完整", async () => {
  const normalizedSchema = await readJson("schemas/normalized-event.schema.json");
  const localEventDataSchema = await readJson("schemas/local-event-data.schema.json");
  const mapping = await readFile(
    new URL("mappings/tencent-edr-proc-events-v1.yaml", root),
    "utf8",
  );
  const genericMapping = await readFile(
    new URL("mappings/generic-network-activity-v1.yaml", root),
    "utf8",
  );
  const controller = await readFile(
    new URL("sample-src/NetworkActivity/NetworkActivity.Controller/Program.cs", root),
    "utf8",
  );
  const behavior = await readFile(
    new URL("sample-src/NetworkActivity/NetworkActivity.Behavior/Program.cs", root),
    "utf8",
  );
  const startScript = await readFile(new URL("scripts/Start-EdrTest.ps1", root), "utf8");
  const capabilities = [
    ["win.network.tcp", "network_tcp.yaml", "tcp_connect", "0.1.0"],
    ["win.network.udp", "network_udp.yaml", "udp_connect", "0.1.0"],
    ["win.network.url", "network_url.yaml", "url_access", "0.2.0"],
    ["win.network.dns", "network_dns.yaml", "dns_query", "0.2.0"],
    ["win.network.file_download", "network_file_download.yaml", "file_download", "0.3.0"],
  ];

  for (const [capabilityId, baselineName, operation, version] of capabilities) {
    const manifest = await readJson(
      `sample-src/NetworkActivity/manifests/${capabilityId}/capability.json`,
    );
    const baseline = await readFile(
      new URL(`baselines/windows/${baselineName}`, root),
      "utf8",
    );
    assert.equal(manifest.capability_id, capabilityId);
    assert.equal(manifest.version, version);
    assert.equal(manifest.risk_level, "L0");
    assert.equal(manifest.required_privilege, "standard_user");
    assert.equal(manifest.network.required, capabilityId === "win.network.dns");
    assert.deepEqual(manifest.participants.map((item) => item.role), ["actor", "helper"]);
    assert.ok(manifest.expected_fact_keys.includes(`network.${operation}_succeeded`));
    if (capabilityId === "win.network.tcp" || capabilityId === "win.network.udp") {
      assert.ok(manifest.expected_fact_keys.includes("network.occurred_at_utc"));
      assert.ok(manifest.expected_fact_keys.includes("network.actor_pid"));
      assert.ok(manifest.expected_fact_keys.includes("network.remote.port"));
    }
    assert.match(baseline, /max_time_difference_ms: 15/);
    assert.match(baseline, new RegExp(`baseline_id: ${capabilityId.replaceAll(".", "\\.")}`));
  }

  assert.ok("url" in normalizedSchema.properties);
  assert.ok("http" in normalizedSchema.properties);
  const networkData = localEventDataSchema.$defs.network.properties;
  assert.ok("subtest" in networkData);
  assert.ok("stage" in networkData);
  assert.ok("dns_client_service" in networkData);
  assert.ok("native_api" in networkData.dns.properties);
  assert.ok("write_started_at_utc" in networkData.download.properties);
  assert.match(mapping, /route_id: network-tcp-socket-request/);
  assert.match(mapping, /route_id: network-udp-socket-request/);
  assert.match(mapping, /route_id: network-tcp-net-bind/);
  assert.match(mapping, /route_id: network-udp-net-bind/);
  assert.match(mapping, /route_id: network-dns-socket-request/);
  assert.match(mapping, /route_id: network-http-url-access/);
  assert.match(mapping, /route_id: network-candidate-discovery/);
  assert.match(mapping, /transform: \[network_direction\]/);
  assert.match(mapping, /transform: \[http_method\]/);
  assert.match(mapping, /"Child\.DstPort": 53/);
  assert.match(mapping, /"url\.full": \{ source: "Child\.Url" \}/);
  assert.match(genericMapping, /route_id: downloaded-file/);
  const downloadBaseline = await readFile(
    new URL("baselines/windows/network_file_download.yaml", root),
    "utf8",
  );
  assert.match(downloadBaseline, /id: download-connection-stage[\s\S]*stage: \{ sequence: 1, title: 第一部分：网络连接 \}/);
  assert.match(downloadBaseline, /network\.source_ip[\s\S]*expected: 0\.0\.0\.0[\s\S]*network\.source_port[\s\S]*expected: 0/);
  assert.match(downloadBaseline, /id: download-process-continuity[\s\S]*sequence: 2[\s\S]*type: same_process_continuity/);
  assert.match(downloadBaseline, /left_expectation: download-connection-stage[\s\S]*right_expectation: download-file-write-stage[\s\S]*max_interval_difference_ms: 30/);
  assert.match(downloadBaseline, /id: download-file-write-stage[\s\S]*sequence: 3[\s\S]*depends_on: download-process-continuity/);
  assert.match(downloadBaseline, /event_actions: \[create, modify\]/);
  assert.doesNotMatch(downloadBaseline, /download-file-write-stage[\s\S]*field: process\.pid/);
  assert.match(controller, /actor_helper_protocol_and_endpoint_cross_check/);
  assert.match(controller, /DnsClientServicePid/);
  assert.match(controller, /must_not_stop_during_cleanup/);
  assert.match(controller, /Hashing\.FileSha256\(state\.Destination\)/);
  assert.match(controller, /EventAction = "file_download"/);
  assert.doesNotMatch(controller, /file_download_connection|file_download_write/);
  assert.match(behavior, /new IPEndPoint\(IPAddress\.Loopback, 53\)/);
  assert.match(behavior, /192\.0\.2\.123/);
  assert.match(behavior, /InternetOpenUrlW/);
  assert.match(behavior, /DnsQuery_W/);
  assert.doesNotMatch(behavior, /8\.8\.8\.8|1\.1\.1\.1/);
  assert.match(startScript, /Build-NetworkActivitySamples\.ps1/);
});

test("哈希算法三项使用 JSON/EXE 真实样本并接入 BASELINE 与字段映射", async () => {
  const controller = await readFile(
    new URL("sample-src/HashAlgorithms/HashAlgorithms.Controller/Program.cs", root),
    "utf8",
  );
  const behavior = await readFile(
    new URL("sample-src/HashAlgorithms/HashAlgorithms.Behavior/Program.cs", root),
    "utf8",
  );
  const importHash = await readFile(
    new URL("sample-src/HashAlgorithms/HashAlgorithms.Protocol/ImportHashCalculator.cs", root),
    "utf8",
  );
  const genericMapping = await readFile(
    new URL("mappings/generic-hash-algorithms-v1.yaml", root),
    "utf8",
  );
  const tencentMapping = await readFile(
    new URL("mappings/tencent-edr-proc-events-v1.yaml", root),
    "utf8",
  );
  const startScript = await readFile(new URL("scripts/Start-EdrTest.ps1", root), "utf8");
  const definitions = [
    ["win.hash.md5", "md5", ".json"],
    ["win.hash.sha", "sha", ".json"],
    ["win.hash.imphash", "imphash", ".exe"],
  ];

  for (const [capabilityId, operation, extension] of definitions) {
    const manifest = await readJson(
      `sample-src/HashAlgorithms/manifests/${capabilityId}/capability.json`,
    );
    const baseline = await readFile(
      new URL(`baselines/windows/hash_${operation}.yaml`, root),
      "utf8",
    );
    assert.equal(manifest.capability_id, capabilityId);
    assert.equal(manifest.version, "0.1.0");
    assert.equal(manifest.participants.length, 1);
    assert.equal(manifest.participants[0].role, "actor");
    assert.ok(manifest.expected_fact_keys.includes(`hash.${operation === "sha" ? "sha256" : operation}`));
    assert.match(baseline, new RegExp(`baseline_id: ${capabilityId.replaceAll(".", "\\.")}`));
    assert.match(baseline, /max_time_difference_ms: 15/);
    assert.match(baseline, new RegExp(`expected: "?${extension.replace(".", "\\.")}"?`));
    assert.match(baseline, /method_selection: \{ strategy: best \}/);
  }

  assert.match(behavior, /operation == "imphash" \? "\.exe" : "\.json"/);
  assert.match(behavior, /FileMode\.CreateNew/);
  assert.match(behavior, /JsonDocument\.Parse/);
  assert.match(behavior, /Environment\.ProcessPath/);
  assert.match(controller, /copy_pe_and_parse_import_table/);
  assert.match(controller, /Hashing\.FileSha256\(source\) == current\.Sha256/);
  assert.match(importHash, /PE 文件没有导入表/);
  assert.match(importHash, /MD5\.HashData\(Encoding\.UTF8\.GetBytes\(source\)\)/);
  assert.match(genericMapping, /"file\.hash\.sha512"/);
  assert.match(genericMapping, /"file\.hash\.imphash"/);
  assert.match(tencentMapping, /sources: \["Child\.FileSha", "Child\.FileSha256"\]/);
  assert.match(tencentMapping, /sources: \["Child\.FileImpHash", "Child\.FileImphash", "Child\.ImpHash"\]/);
  assert.match(startScript, /Build-HashAlgorithmsSamples\.ps1/);
});

test("260808 腾讯 EDR 全字段目录完整、脱敏且可复现", async () => {
  const catalog = await readJson(
    "docs/reference/tencent-edr-260808-field-catalog.json",
  );
  assert.equal(catalog.schema_version, "1.0");
  assert.equal(catalog.source.event_count, 834);
  assert.equal(catalog.all_fields.length, 228);
  assert.equal(catalog.sanitization.applied, true);
  assert.match(catalog.source.sha256, /^[a-f0-9]{64}$/);

  const allFields = new Map(catalog.all_fields.map((item) => [item.field, item]));
  [
    "Common.EventTime",
    "Common.EventUUId",
    "Parent.ProcPid",
    "Parent.ProcCmdline",
    "Child.FilePath",
    "Child.OldFilePath",
    "Child.FileCreateOpName",
    "Child.FileTotalRead",
    "Child.FileTotalWrite",
  ].forEach((field) => assert.ok(allFields.has(field), `字段目录缺少 ${field}`));
  assert.deepEqual(allFields.get("Child.DstIp").examples, ["203.0.113.10"]);
  assert.ok(
    allFields.get("Child.FilePath").examples.every((value) =>
      value.startsWith("C:\\EDR-Test\\example\\"),
    ),
  );

  const fileWriteClose = catalog.event_kinds.find(
    (item) => item.action_type === "File" && item.action_name === "FileWriteClose",
  );
  assert.equal(fileWriteClose.event_count, 465);
  assert.ok(fileWriteClose.field_names.includes("Child.FileCreateOpName"));
});

test("进程创建示例的引用、时间、nonce、计数和进程身份一致", async () => {
  const example = await readJson("examples/local-run.process-create.example.json");
  const [capability] = example.capabilities;
  const [event] = example.local_events;
  const programs = new Map(example.programs.map((program) => [program.program_instance_id, program]));
  const facts = new Set(example.local_facts.map((fact) => fact.local_fact_id));
  const artifacts = new Set(example.artifacts.map((artifact) => artifact.artifact_id));

  assert.equal(example.schema_version, "1.1");
  assert.equal(example.run.database_schema_version, 2);
  assert.equal(capability.status, "LOCAL_PASS");
  assert.equal(event.case_run_id, capability.case_run_id);
  assert.equal(event.nonce, capability.nonce);
  assert.equal(event.event_type, event.data.kind);
  assert.equal(event.event_action, event.data.operation);
  assert.ok(programs.has(event.actor_program_id));
  assert.ok(programs.has(event.target_program_id));
  assert.equal(programs.get(event.actor_program_id).pid, event.data.actor.pid);
  assert.equal(programs.get(event.target_program_id).pid, event.data.target.pid);
  assert.equal(event.data.target.parent_pid, event.data.actor.pid);

  const occurred = Date.parse(event.occurred_at_utc);
  assert.ok(occurred >= Date.parse(capability.observation_window.started_at_utc));
  assert.ok(occurred <= Date.parse(capability.observation_window.ended_at_utc));

  example.local_facts.forEach((fact) => {
    assert.equal(fact.case_run_id, capability.case_run_id);
    assert.equal(fact.local_event_id, event.local_event_id);
  });
  assert.equal(facts.size, example.local_facts.length);
  event.evidence_refs.forEach((id) => assert.ok(artifacts.has(id)));

  const expectedCounts = {
    capabilities: example.capabilities.length,
    programs: example.programs.length,
    local_events: example.local_events.length,
    local_facts: example.local_facts.length,
    artifacts: example.artifacts.length,
    cleanup_results: example.cleanup_results.length,
  };
  assert.deepEqual(example.integrity.record_counts, expectedCounts);
});

test("SQLite v2 可以初始化且具备 JSON 导出所需的关键采集列", async () => {
  const ddl = await readFile(new URL("schemas/run-db.sql", root), "utf8");
  const requiredFragments = [
    "PRAGMA user_version = 2",
    "boot_time_utc",
    "observer_dropped_count",
    "parent_pid",
    "architecture",
    "occurred_at_utc",
    "collection_method",
    "evidence_refs_json",
    "json_extract(data_json, '$.kind') = event_type",
    "json_extract(data_json, '$.operation') = event_action",
    "sensitive",
  ];

  requiredFragments.forEach((fragment) => {
    assert.ok(ddl.includes(fragment), `SQLite DDL 缺少：${fragment}`);
  });

  const database = new DatabaseSync(":memory:");
  try {
    database.exec(ddl);
    assert.equal(database.prepare("PRAGMA user_version").get().user_version, 2);
    const tables = new Set(
      database
        .prepare("SELECT name FROM sqlite_master WHERE type = 'table'")
        .all()
        .map((row) => row.name),
    );
    [
      "run",
      "capability_run",
      "program_instance",
      "local_event",
      "local_fact",
      "artifact",
      "execution_log",
      "cleanup_result",
    ].forEach((name) => assert.ok(tables.has(name), `SQLite 缺少表：${name}`));
  } finally {
    database.close();
  }
});

test("组策略与命名管道三项能力具备完整样本、BASELINE、映射和启动接入", async () => {
  const definitions = [
    ["win.group_policy.modify", "group_policy_modify.yaml", 1, "administrator", "0.2.0", "L2"],
    ["win.named_pipe.create", "named_pipe_create.yaml", 2, "standard_user", "0.1.0", "L0"],
    ["win.named_pipe.connect", "named_pipe_connect.yaml", 2, "standard_user", "0.1.0", "L0"],
  ];
  for (const [capabilityId, baselineName, participantCount, privilege, version, risk] of definitions) {
    const domain = capabilityId.startsWith("win.group_policy") ? "GroupPolicyActivity" : "NamedPipeActivity";
    const manifest = await readJson(`sample-src/${domain}/manifests/${capabilityId}/capability.json`);
    const baseline = await readFile(new URL(`baselines/windows/${baselineName}`, root), "utf8");
    assert.equal(manifest.capability_id, capabilityId);
    assert.equal(manifest.version, version);
    assert.equal(manifest.risk_level, risk);
    assert.equal(manifest.required_privilege, privilege);
    assert.equal(manifest.participants.length, participantCount);
    assert.match(baseline, /max_time_difference_ms: 15/);
    assert.match(baseline, new RegExp(`baseline_id: ${capabilityId.replaceAll(".", "\\.")}`));
  }

  const groupBehavior = await readFile(new URL("sample-src/GroupPolicyActivity/GroupPolicyActivity.Behavior/Program.cs", root), "utf8");
  const groupProtocol = await readFile(new URL("sample-src/GroupPolicyActivity/GroupPolicyActivity.Protocol/Protocol.cs", root), "utf8");
  const groupManifest = await readJson("sample-src/GroupPolicyActivity/manifests/win.group_policy.modify/capability.json");
  const groupBaseline = await readFile(new URL("baselines/windows/group_policy_modify.yaml", root), "utf8");
  const pipeBehavior = await readFile(new URL("sample-src/NamedPipeActivity/NamedPipeActivity.Behavior/Program.cs", root), "utf8");
  const pipeController = await readFile(new URL("sample-src/NamedPipeActivity/NamedPipeActivity.Controller/Program.cs", root), "utf8");
  const tencentMapping = await readFile(new URL("mappings/tencent-edr-proc-events-v1.yaml", root), "utf8");
  const front = await readFile(new URL("web/app/live-control-plane.tsx", root), "utf8");
  const start = await readFile(new URL("scripts/Start-EdrTest.ps1", root), "utf8");
  assert.match(groupBehavior, /RegSetValueExW/);
  assert.match(groupBehavior, /SOFTWARE\\\\Policies\\\\EdrTest\\\\Runs/);
  assert.match(groupBehavior, /known_policy_same_value/);
  assert.match(groupProtocol, /RewriteSameValue/);
  assert.match(groupProtocol, /SHA256\.HashData/);
  assert.match(groupProtocol, /class KnownPolicyTargetCatalog/);
  assert.match(groupProtocol, /ResolveCandidates/);
  assert.ok(groupManifest.parameters.known_policy_target.allowed_values.includes("auto"));
  assert.match(groupBaseline, /method: \{ id: isolated_policy_key/);
  assert.match(groupBaseline, /method: \{ id: known_policy_same_value/);
  assert.match(pipeBehavior, /CreateNamedPipeW/);
  assert.match(pipeBehavior, /CreateFileW/);
  assert.match(pipeController, /serverIsActor = operation == "create"/);
  assert.match(pipeController, /named_pipe\.nonce_verified/);
  assert.match(tencentMapping, /"Child\.PipeOpName": 创建管道/);
  assert.match(tencentMapping, /"Child\.PipeOpName": 打开管道/);
  assert.match(front, /"win\.group_policy\.modify": "RegSetValue"/);
  assert.match(front, /"win\.named_pipe\.create": "NamedPipe"/);
  assert.match(start, /Build-GroupPolicyActivitySamples\.ps1/);
  assert.match(start, /Build-NamedPipeActivitySamples\.ps1/);
});
