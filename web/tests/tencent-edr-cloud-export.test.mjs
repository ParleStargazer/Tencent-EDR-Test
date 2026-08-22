import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { buildLaunchOptions, runTencentEdrExport, validateRequest } from "../automation/tencent-edr-cloud-export.mjs";

test("云端自动化请求只接受受限的本地 JSON 下载目标", () => {
  const target = join(tmpdir(), "edr-cloud.json");
  const value = validateRequest({
    account: "child-user",
    password: "secret-value",
    device_name: "EDR-TEST-01",
    query_start_local: "2026-08-22 09:10:11",
    download_path: target,
    timeout_ms: 30_000,
  });
  assert.equal(value.download_path, target);
  assert.equal(value.debug_mode, false);
  const debugValue = validateRequest({ ...value, debug_mode: true });
  assert.equal(debugValue.debug_mode, true);
  assert.equal(buildLaunchOptions(debugValue).headless, false);
  assert.equal(buildLaunchOptions(debugValue, { headless: true }).headless, true);
  assert.throws(() => validateRequest({ ...value, debug_mode: "true" }), /debug_mode 必须是布尔值/);
  assert.throws(() => validateRequest({ ...value, download_path: "relative.json" }), /绝对 JSON 文件路径/);
  assert.throws(() => validateRequest({ ...value, query_start_local: "2026/08/22" }), /yyyy-MM-dd HH:mm:ss/);
});

test("调试模式在 Edge 启动失败时保留阶段并脱敏异常", async () => {
  const directory = await mkdtemp(join(tmpdir(), "edr-cloud-launch-failure-"));
  const events = [];
  try {
    await assert.rejects(runTencentEdrExport({
      account: "child-user",
      password: "secret-value",
      device_name: "EDR-TEST-01",
      query_start_local: "2026-08-22 09:10:11",
      download_path: join(directory, "cloud.json"),
      timeout_ms: 30_000,
      debug_mode: true,
    }, {
      launch: async () => { throw new Error("secret-value launch failure for child-user"); },
      onEvent: (event) => events.push(event),
    }), /launch failure/);
    assert.equal(events[0].stage, "launch_browser");
    assert(events.some((event) => event.type === "debug" && event.stage === "automation_exception"));
    assert(events.some((event) => event.type === "progress" && event.stage === "launch_browser" && event.level === "error"));
    assert.doesNotMatch(JSON.stringify(events), /child-user|secret-value/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("本机 Edge 可按腾讯 EDR 页面基准完成筛选并保存下载", async (context) => {
  const directory = await mkdtemp(join(tmpdir(), "edr-cloud-browser-"));
  const output = join(directory, "cloud.json");
  const html = `<!doctype html><html lang="zh-CN"><body>
    <button>子用户</button>
    <input aria-label="子用户名 主账号 ID" placeholder="子用户名" />
    <input type="password" placeholder="请输入登录密码" />
    <button>登录</button>
    <div>请选择域</div><div role="listitem">默认域</div>
    <div>进程事件</div><div title="全部">全部事件</div>
    <div>添加筛选条件</div><button>添加条件</button>
    <div>选择字段</div><div>选择关系</div><div>请指定</div>
    <input aria-label="请输入添加内容" placeholder="请输入添加内容" />
    <input aria-label="选择时间" placeholder="选择时间" />
    <button id="initial-export">导出</button>
    <label>全选<input type="checkbox" /></label><span>xlsx</span>
    <div id="tea-overlay-root">
      <span>主机名称（系统环境信息）</span>
      <div role="listitem">等于</div><div role="listitem">其他信息</div>
      <div role="listitem">采集时间</div><div role="listitem">大于</div>
      <div role="listitem">json</div>
      <button>确定</button><button>检索</button><button id="final-export">导出</button>
    </div>
    <script>
      document.getElementById("final-export").addEventListener("click", () => {
        const blob = new Blob([JSON.stringify([{ "Action.Name": "ProcessCreate", "Host.Name": "EDR-TEST-01" }])], { type: "application/json" });
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = "edr-export.json";
        link.click();
      });
    </script>
  </body></html>`;
  const server = createServer((_request, response) => {
    response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    response.end(html);
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  assert(address && typeof address === "object");
  const events = [];
  try {
    try {
      const result = await runTencentEdrExport({
        account: "child-user",
        password: "secret-value",
        device_name: "EDR-TEST-01",
        query_start_local: "2026-08-22 09:10:11",
        download_path: output,
        timeout_ms: 30_000,
        debug_mode: true,
      }, { loginUrl: `http://127.0.0.1:${address.port}/`, channel: "msedge", headless: true, onEvent: (event) => events.push(event) });
      assert.equal(result.status, "succeeded");
    } catch (error) {
      if (/Executable doesn.t exist|browserType\.launch|msedge/i.test(String(error))) {
        context.skip("当前测试环境没有可用的 Microsoft Edge。");
        return;
      }
      throw error;
    }
    const records = JSON.parse(await readFile(output, "utf8"));
    assert.equal(records.length, 1);
    assert.equal(records[0]["Action.Name"], "ProcessCreate");
    const progressEvents = events.filter((event) => event.type === "progress");
    assert.deepEqual(progressEvents.map((event) => event.stage), [
      "launch_browser", "create_context", "open_login_page", "select_child_user", "fill_credentials",
      "submit_login", "select_domain", "open_event_view", "apply_host_filter", "apply_time_filter",
      "prepare_export", "wait_download", "save_download",
    ]);
    assert(progressEvents.every((event, index) => index === 0 || event.progress >= progressEvents[index - 1].progress));
    assert(events.some((event) => event.type === "debug"));
    assert.doesNotMatch(JSON.stringify(events), /child-user|secret-value/);
  } finally {
    await new Promise((resolve) => server.close(resolve));
    await rm(directory, { recursive: true, force: true });
  }
});
