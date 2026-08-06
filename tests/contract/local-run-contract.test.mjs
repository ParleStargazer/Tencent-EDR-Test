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
