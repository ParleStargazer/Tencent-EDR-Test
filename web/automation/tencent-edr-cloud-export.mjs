import { mkdir } from "node:fs/promises";
import { dirname, isAbsolute, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { chromium } from "playwright-core";

const LOGIN_URL = "https://cloud.tencent.com/login?s_url=https%3A%2F%2Fconsole.cloud.tencent.com%2Fioa%2Fv1%2Fedr%2Fprotect%2Fthreatalarm";
const MAX_REQUEST_BYTES = 32 * 1024;

class AutomationError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "AutomationError";
    this.code = code;
  }
}

async function firstVisible(locators, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    for (const locator of locators) {
      const count = await locator.count().catch(() => 0);
      for (let index = 0; index < count; index++) {
        const candidate = locator.nth(index);
        if (await candidate.isVisible().catch(() => false)) return candidate;
      }
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
  }
  throw new AutomationError("CONTROL_NOT_FOUND", "腾讯云控制台页面结构与自动化基准不一致，未找到所需控件。");
}

async function clickFirst(locators, timeoutMs) {
  const locator = await firstVisible(locators, timeoutMs);
  await locator.click();
  return locator;
}

async function fillFirst(locators, value, timeoutMs) {
  const locator = await firstVisible(locators, timeoutMs);
  await locator.fill(value);
  return locator;
}

export function validateRequest(request) {
  if (!request || typeof request !== "object" || Array.isArray(request))
    throw new AutomationError("INVALID_REQUEST", "自动化请求必须是 JSON 对象。");
  for (const field of ["account", "password", "device_name", "query_start_local", "download_path"]) {
    if (typeof request[field] !== "string" || !request[field].trim())
      throw new AutomationError("INVALID_REQUEST", `自动化请求缺少 ${field}。`);
  }
  if (request.account.length > 512 || request.password.length > 4096 || request.device_name.length > 255)
    throw new AutomationError("INVALID_REQUEST", "自动化请求字段长度超过限制。");
  if (!/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(request.query_start_local))
    throw new AutomationError("INVALID_REQUEST", "日志起始时间格式必须为 yyyy-MM-dd HH:mm:ss。");
  if (!isAbsolute(request.download_path) || !request.download_path.toLowerCase().endsWith(".json"))
    throw new AutomationError("INVALID_REQUEST", "下载目标必须是绝对 JSON 文件路径。");
  const timeoutMs = Number(request.timeout_ms ?? 300_000);
  if (!Number.isInteger(timeoutMs) || timeoutMs < 30_000 || timeoutMs > 900_000)
    throw new AutomationError("INVALID_REQUEST", "timeout_ms 必须在 30000..900000 范围内。");
  return { ...request, timeout_ms: timeoutMs, download_path: resolve(request.download_path) };
}

async function selectDefaultDomain(page, timeoutMs) {
  const chooserCandidates = [
    page.locator("div").filter({ hasText: /^请选择域$/ }),
    page.getByText("请选择域", { exact: true }),
  ];
  const chooser = await firstVisible(chooserCandidates, 5_000).catch(() => null);
  if (!chooser) return;
  await chooser.click();
  await clickFirst([
    page.getByRole("listitem", { name: "默认域" }),
    page.getByText("默认域", { exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.getByRole("button", { name: "确定", exact: true }),
    page.locator("#tea-overlay-root").getByRole("button", { name: "确定", exact: true }),
  ], timeoutMs);
}

async function addHostFilter(page, deviceName, timeoutMs) {
  await clickFirst([
    page.locator("div").filter({ hasText: /^添加筛选条件$/ }),
    page.getByText("添加筛选条件", { exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.locator("div").filter({ hasText: /^选择字段$/ }),
    page.getByText("选择字段", { exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.locator("#tea-overlay-root").getByText("主机名称（系统环境信息）", { exact: true }),
    page.getByText("主机名称（系统环境信息）", { exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.locator("div").filter({ hasText: /^选择关系$/ }),
    page.getByText("选择关系", { exact: true }),
  ], timeoutMs);
  await clickFirst([page.getByRole("listitem", { name: "等于", exact: true }), page.getByText("等于", { exact: true })], timeoutMs);
  await clickFirst([
    page.locator("div").filter({ hasText: /^请指定$/ }),
    page.getByText("请指定", { exact: true }),
  ], timeoutMs);
  const input = await fillFirst([
    page.getByRole("textbox", { name: "请输入添加内容" }),
    page.locator('input[placeholder*="请输入添加内容"]'),
  ], deviceName, timeoutMs);
  await input.press("Enter");
  await clickFirst([
    page.locator("#tea-overlay-root").getByRole("button", { name: "确定", exact: true }),
    page.getByRole("button", { name: "确定", exact: true }),
  ], timeoutMs);
}

async function addStartTimeFilter(page, queryStartLocal, timeoutMs) {
  await clickFirst([
    page.getByRole("button", { name: /添加条件/ }),
    page.locator('button:has-text("添加条件")'),
  ], timeoutMs);
  await clickFirst([
    page.locator("div").filter({ hasText: /^选择字段$/ }),
    page.getByText("选择字段", { exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.getByRole("listitem").filter({ hasText: "其他信息" }),
    page.getByText("其他信息", { exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.getByRole("listitem").filter({ hasText: "采集时间" }),
    page.getByText("采集时间", { exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.locator("div").filter({ hasText: /^选择关系$/ }),
    page.getByText("选择关系", { exact: true }),
  ], timeoutMs);
  await clickFirst([page.getByRole("listitem", { name: "大于", exact: true }), page.getByText("大于", { exact: true })], timeoutMs);
  await fillFirst([
    page.getByRole("textbox", { name: "选择时间" }),
    page.locator('input[placeholder*="选择时间"]'),
  ], queryStartLocal, timeoutMs);
  await clickFirst([
    page.locator("#tea-overlay-root").getByRole("button", { name: "确定", exact: true }),
    page.getByRole("button", { name: "确定", exact: true }),
  ], timeoutMs);
  await clickFirst([
    page.locator("#tea-overlay-root").getByRole("button", { name: "检索" }),
    page.getByRole("button", { name: "检索", exact: true }),
  ], timeoutMs);
}

export async function runTencentEdrExport(rawRequest, runtime = {}) {
  const request = validateRequest(rawRequest);
  await mkdir(dirname(request.download_path), { recursive: true });
  const launch = runtime.launch ?? ((options) => chromium.launch(options));
  const browser = await launch({
    channel: runtime.channel ?? "msedge",
    headless: runtime.headless ?? true,
    downloadsPath: dirname(request.download_path),
  });
  try {
    const context = await browser.newContext({ acceptDownloads: true, locale: "zh-CN" });
    const page = await context.newPage();
    page.setDefaultTimeout(Math.min(request.timeout_ms, 60_000));
    await page.goto(runtime.loginUrl ?? LOGIN_URL, { waitUntil: "domcontentloaded", timeout: request.timeout_ms });
    process.stderr.write("[cloud-export] login_page_opened\n");

    const childUserButton = await firstVisible([
      page.getByRole("button", { name: "子用户", exact: true }),
      page.getByText("子用户", { exact: true }),
    ], 8_000).catch(() => null);
    if (childUserButton) await childUserButton.click();
    await fillFirst([
      page.getByRole("textbox", { name: /子用户名.*主账号 ID/ }),
      page.locator('input[placeholder*="子用户名"]'),
      page.locator('input[type="text"]'),
    ], request.account, 30_000);
    await fillFirst([
      page.getByRole("textbox", { name: "请输入登录密码" }),
      page.locator('input[placeholder="请输入登录密码"]'),
      page.locator('input[type="password"]'),
    ], request.password, 30_000);
    await clickFirst([page.getByRole("button", { name: "登录", exact: true })], 30_000);
    process.stderr.write("[cloud-export] credentials_submitted\n");

    await selectDefaultDomain(page, 30_000);
    await clickFirst([
      page.locator("div").filter({ hasText: /^进程事件$/ }),
      page.getByText("进程事件", { exact: true }),
    ], request.timeout_ms);
    await clickFirst([
      page.getByTitle("全部"),
      page.getByText("全部事件", { exact: true }),
    ], 30_000);
    await clickFirst([
      page.locator("#tea-overlay-root").getByRole("button", { name: "确定", exact: true }),
      page.getByRole("button", { name: "确定", exact: true }),
    ], 30_000);
    await addHostFilter(page, request.device_name, 30_000);
    await addStartTimeFilter(page, request.query_start_local, 30_000);
    process.stderr.write("[cloud-export] filters_applied\n");

    await clickFirst([
      page.getByRole("button", { name: /导出/ }),
      page.locator('button:has-text("导出")'),
    ], 30_000);
    const selectAll = await firstVisible([
      page.locator("label").filter({ hasText: /^全选$/ }),
      page.getByText("全选", { exact: true }),
    ], 30_000);
    await selectAll.click();
    await selectAll.click();
    await clickFirst([page.getByText("xlsx", { exact: true })], 30_000);
    await clickFirst([
      page.getByRole("listitem", { name: "json", exact: true }),
      page.getByText("json", { exact: true }),
    ], 30_000);

    const downloadPromise = page.waitForEvent("download", { timeout: request.timeout_ms });
    await clickFirst([
      page.locator("#tea-overlay-root").getByRole("button", { name: "导出", exact: true }),
      page.getByRole("button", { name: "导出", exact: true }),
    ], 30_000);
    const download = await downloadPromise;
    const failure = await download.failure();
    if (failure) throw new AutomationError("DOWNLOAD_FAILED", "腾讯云控制台生成了下载任务，但文件下载失败。");
    await download.saveAs(request.download_path);
    process.stderr.write("[cloud-export] download_saved\n");
    await context.close();
    return { status: "succeeded", download_path: request.download_path };
  } finally {
    await browser.close();
  }
}

async function readRequestFromStdin() {
  let text = "";
  process.stdin.setEncoding("utf8");
  for await (const chunk of process.stdin) {
    text += chunk;
    if (Buffer.byteLength(text, "utf8") > MAX_REQUEST_BYTES)
      throw new AutomationError("INVALID_REQUEST", "自动化请求超过 32 KB。");
  }
  try {
    return JSON.parse(text);
  } catch {
    throw new AutomationError("INVALID_REQUEST", "自动化请求不是有效 JSON。");
  }
}

async function main() {
  try {
    const request = await readRequestFromStdin();
    const result = await runTencentEdrExport(request);
    process.stdout.write(`${JSON.stringify(result)}\n`);
  } catch (error) {
    const code = error instanceof AutomationError ? error.code : "BROWSER_AUTOMATION_FAILED";
    const message = error instanceof AutomationError
      ? error.message
      : "浏览器自动化未完成；请检查网络、腾讯云登录验证或控制台页面是否变更。";
    process.stdout.write(`${JSON.stringify({ status: "failed", code, message })}\n`);
    process.exitCode = 20;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) await main();
