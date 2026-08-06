import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

async function render() {
  const workerUrl = new URL("../dist/server/index.js", import.meta.url);
  workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
  const { default: worker } = await import(workerUrl.href);

  return worker.fetch(
    new Request("http://localhost/", {
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

test("服务端渲染中文 EDR 控制面核心内容", async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html\b/i);

  const html = await response.text();
  assert.match(html, /<html[^>]*lang="zh-CN"/i);
  assert.match(html, /<title>EDR 能力验证控制台<\/title>/i);
  assert.match(html, /验证工作台/);
  assert.match(html, /选择本轮能力/);
  assert.match(html, /启动本轮测试/);
  assert.match(html, /启动真实 Runner/);
  assert.match(html, /导入结果并验证/);
  assert.match(html, /本地运行 JSON/);
  assert.match(html, /EDR 云端事件/);
  assert.match(html, /开始比较/);
  assert.match(html, /进程创建/);
  assert.match(html, /Process Creation/);
  assert.match(html, /BITS 后台传输任务活动/);
  assert.match(html, /PowerShell Activity/);
  assert.doesNotMatch(html, /Your site is taking shape|Starter Project|codex-preview/i);
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
  const route = await readFile(new URL("../app/page.tsx", import.meta.url), "utf8");

  assert.doesNotMatch(packageJson, /react-loading-skeleton|drizzle-orm|tailwindcss/);
  assert.match(hosting, /"d1": null/);
  assert.match(hosting, /"r2": null/);
  assert.match(page, /win\.process\.create/);
  assert.match(route, /LiveControlPlane/);
  assert.match(livePage, /127\.0\.0\.1:4317\/api/);
  assert.match(livePage, /apiRequest<ApiRun>\("\/runs"/);
  assert.match(livePage, /apiRequest<ValidationResult>\("\/compare"/);
  assert.doesNotMatch(livePage, /当前为控制面阶段|真实 EXE 调度将在/);

  await assert.rejects(access(new URL("../app/_sites-preview", import.meta.url)));
  await assert.rejects(access(new URL("../db", import.meta.url)));
  await assert.rejects(access(new URL("../examples", import.meta.url)));
});
