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
  const capability = schema.properties.capabilities.items;
  const candidate = capability.properties.edr_candidates.items;

  assert.ok(capability.required.includes("local_export_block"));
  assert.ok(capability.required.includes("local_baseline_matches"));
  assert.ok(candidate.required.includes("baseline_matches"));
  assert.deepEqual(
    candidate.properties.baseline_matches.items.properties.kind.enum,
    ["correlation", "assertion"],
  );
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
    assert.equal(manifest.capability_id, `win.file.${operation}`);
    assert.equal(manifest.version, "0.1.0");
    assert.equal(manifest.risk_level, "L0");
    assert.deepEqual(manifest.participants.map((item) => item.role), ["actor"]);
    assert.ok(manifest.expected_fact_keys.includes(`file.${operation}_succeeded`));
    assert.ok(manifest.expected_fact_keys.includes("file.occurred_at_utc"));
    assert.match(mapping, new RegExp(`route_id: file-${operation}`));
  }

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
