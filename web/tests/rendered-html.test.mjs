import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

async function render(path = "/") {
  const workerUrl = new URL("../dist/server/index.js", import.meta.url);
  workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
  const { default: worker } = await import(workerUrl.href);

  return worker.fetch(
    new Request(`http://localhost${path}`, {
      headers: { accept: "text/html" },
    }),
    {
      ASSETS: {
        fetch: async () => new Response("Not found", { status: 404 }),
      },
    },
    {
      waitUntil() {},
      passThroughOnException() {},
    },
  );
}

test("工作台提供测试与离线比较两个独立入口", async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html\b/i);

  const html = await response.text();
  assert.match(html, /<html[^>]*lang="zh-CN"/i);
  assert.match(html, /<title>EDR 能力验证控制台<\/title>/i);
  assert.match(html, /EDR 能力验证/);
  assert.match(html, /进行能力测试/);
  assert.match(html, /执行离线比较/);
  assert.match(html, /href="\/test"/);
  assert.match(html, /href="\/compare"/);
  assert.doesNotMatch(html, /Your site is taking shape|Starter Project|codex-preview/i);
});

test("测试子页面展示串行设置、逐项进度与两级日志", async () => {
  const response = await render("/test");
  assert.equal(response.status, 200);
  const html = await response.text();
  assert.match(html, /串行能力测试/);
  assert.match(html, /下一项能力前等待（秒）/);
  assert.match(html, /默认 3 秒/);
  assert.match(html, /测试进度/);
  assert.match(html, /重点日志/);
  assert.match(html, /详细日志/);
  assert.match(html, /Runner 与 Controller 输出/);
  assert.match(html, /进程创建/);
  assert.match(html, /Process Creation/);
});

test("离线比较子页面解释 BASELINE 并展示逐项满足情况", async () => {
  const response = await render("/compare");
  assert.equal(response.status, 200);
  const html = await response.text();
  assert.match(html, /核对 EDR 日志/);
  assert.match(html, /BASELINE 是什么/);
  assert.match(html, /通过判定的要求/);
  assert.match(html, /要求满足情况/);
  assert.match(html, /本地运行 JSON/);
  assert.match(html, /EDR 云端事件/);
  assert.match(html, /开始离线比较/);
});

test("能力目录包含 16 个中英活动域和 53 项唯一能力", async () => {
  const page = await readFile(new URL("../app/control-plane.tsx", import.meta.url), "utf8");
  const catalog = page.match(/const capabilityCatalog:[\s\S]+?const capabilities:/)?.[0] ?? "";
  const capabilityIds = [...catalog.matchAll(/defineCapability\("([^"]+)"/g)].map(
    (match) => match[1],
  );
  const categoryNames = [
    "Process Activity",
    "File Manipulation",
    "User Account Activity",
    "Network Activity",
    "Hash Algorithms",
    "Registry Activity",
    "Schedule Task Activity",
    "Service Activity",
    "Driver/Module Activity",
    "Device Operations",
    "Other Relevant Events",
    "Named Pipe Activity",
    "EDR SysOps",
    "WMI Activity",
    "BIT JOBS Activity",
    "PowerShell Activity",
  ];

  assert.equal(capabilityIds.length, 53);
  assert.equal(new Set(capabilityIds).size, 53);
  categoryNames.forEach((name) => assert.match(catalog, new RegExp(name.replace("/", "\\/"))));
  assert.match(catalog, /"进程篡改活动", "Process Tampering Activity"/);
  assert.match(catalog, /"WMI 事件消费者与过滤器绑定", "WmiEventConsumerToFilter"/);
  assert.match(catalog, /"脚本块活动", "Script-Block Activity"/);
});

test("模板预览已移除且页面接入本地 Runner API", async () => {
  const packageJson = await readFile(new URL("../package.json", import.meta.url), "utf8");
  const hosting = await readFile(new URL("../.openai/hosting.json", import.meta.url), "utf8");
  const page = await readFile(new URL("../app/control-plane.tsx", import.meta.url), "utf8");
  const livePage = await readFile(new URL("../app/live-control-plane.tsx", import.meta.url), "utf8");
  const styles = await readFile(new URL("../app/globals.css", import.meta.url), "utf8");
  const route = await readFile(new URL("../app/page.tsx", import.meta.url), "utf8");
  const testRoute = await readFile(new URL("../app/test/page.tsx", import.meta.url), "utf8");
  const compareRoute = await readFile(new URL("../app/compare/page.tsx", import.meta.url), "utf8");

  assert.doesNotMatch(packageJson, /react-loading-skeleton|drizzle-orm|tailwindcss/);
  assert.match(hosting, /"d1": null/);
  assert.match(hosting, /"r2": null/);
  assert.match(page, /win\.process\.create/);
  assert.match(route, /LiveControlPlane/);
  assert.match(testRoute, /view="test"/);
  assert.match(compareRoute, /view="compare"/);
  assert.match(livePage, /127\.0\.0\.1:4317\/api/);
  assert.match(livePage, /apiRequest<ApiRun>\("\/runs"/);
  assert.match(livePage, /apiRequest<ValidationResult>\("\/compare"/);
  assert.match(livePage, /inter_capability_delay_seconds: nextDelay/);
  assert.match(livePage, /run\.highlights/);
  assert.match(livePage, /E \/ 已完成队列/);
  assert.match(livePage, /role="dialog"/);
  assert.match(livePage, /本地 BASELINE/);
  assert.match(livePage, /programs\.\$\{program\.role\}\.pid/);
  assert.match(livePage, /能力开始/);
  assert.match(livePage, /baseline_requirements/);
  assert.match(livePage, /<details className="capability-result-card">/);
  assert.match(livePage, /open=\{isCloud\}/);
  assert.match(livePage, /EDR 原始完整日志/);
  assert.match(livePage, /打开 JSON 对照窗/);
  assert.match(livePage, /逐能力 JSON 对照/);
  assert.match(livePage, /选择 EDR 候选 JSON 块/);
  assert.match(livePage, /BASELINE 一致/);
  assert.match(livePage, /baseline_matches/);
  assert.match(livePage, /JsonCodeViewer/);
  assert.match(livePage, /correlation_score/);
  assert.match(livePage, /time_distance_ms/);
  assert.match(livePage, /低置信度排查/);
  assert.match(livePage, /事件类型与 Action 只作提示/);
  assert.match(livePage, /eligible_for_validation/);
  assert.match(livePage, /应包含的测试标记/);
  assert.match(livePage, /读取到的 PID/);
  assert.match(livePage, /总体结论/);
  assert.match(livePage, /\/reports\/\$\{comparison\.comparison_id\}\/conclusion/);
  assert.match(livePage, /下载中文结论/);
  assert.doesNotMatch(livePage, /slice\(0,\s*120\)/);
  assert.match(styles, /\.log-line p \{[^}]*white-space:\s*pre-wrap;/);
  assert.doesNotMatch(styles, /\.log-line code \{[^}]*text-overflow:\s*ellipsis;/);
  assert.doesNotMatch(styles, /\.selected-candidate-meta code \{[^}]*text-overflow:\s*ellipsis;/);
  assert.doesNotMatch(livePage, /当前为控制面阶段|真实 EXE 调度将在/);

  await assert.rejects(access(new URL("../app/_sites-preview", import.meta.url)));
  await assert.rejects(access(new URL("../db", import.meta.url)));
  await assert.rejects(access(new URL("../examples", import.meta.url)));
});
