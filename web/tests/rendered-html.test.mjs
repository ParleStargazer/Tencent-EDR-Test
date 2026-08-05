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
  assert.match(html, /导入结果并验证/);
  assert.match(html, /本地运行 JSON/);
  assert.match(html, /EDR 云端事件/);
  assert.match(html, /开始比较/);
  assert.match(html, /进程创建/);
  assert.doesNotMatch(html, /Your site is taking shape|Starter Project|codex-preview/i);
});

test("模板预览和无关持久化代码已移除", async () => {
  const packageJson = await readFile(new URL("../package.json", import.meta.url), "utf8");
  const hosting = await readFile(new URL("../.openai/hosting.json", import.meta.url), "utf8");
  const page = await readFile(new URL("../app/control-plane.tsx", import.meta.url), "utf8");

  assert.doesNotMatch(packageJson, /react-loading-skeleton|drizzle-orm|tailwindcss/);
  assert.match(hosting, /"d1": null/);
  assert.match(hosting, /"r2": null/);
  assert.match(page, /文件内容未上传/);
  assert.match(page, /win\.process\.create/);

  await assert.rejects(access(new URL("../app/_sites-preview", import.meta.url)));
  await assert.rejects(access(new URL("../db", import.meta.url)));
  await assert.rejects(access(new URL("../examples", import.meta.url)));
});
