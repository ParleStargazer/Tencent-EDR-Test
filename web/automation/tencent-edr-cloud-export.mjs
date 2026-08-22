import { mkdir } from "node:fs/promises";
import { dirname, isAbsolute, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { chromium } from "playwright-core";

const LOGIN_URL = "https://cloud.tencent.com/login?s_url=https%3A%2F%2Fconsole.cloud.tencent.com%2Fioa%2Fv1%2Fedr%2Fprotect%2Fthreatalarm";
const MAX_REQUEST_BYTES = 32 * 1024;
const DEFAULT_STEP_TIMEOUT_MS = 60_000;
const DEFAULT_POST_LOGIN_TIMEOUT_MS = 180_000;
const DEFAULT_STEP_SETTLE_MS = 800;
const DEFAULT_DOMAIN_SELECTION_SETTLE_MS = 3_000;
const DEFAULT_QUERY_RESULT_SETTLE_MS = 3_000;

class AutomationError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "AutomationError";
    this.code = code;
  }
}

function timingValue(value, fallback, minimum, maximum) {
  const parsed = Number(value ?? fallback);
  return Number.isFinite(parsed) ? Math.min(maximum, Math.max(minimum, Math.round(parsed))) : fallback;
}

export function buildAutomationTiming(request, runtime = {}) {
  const maximum = request.timeout_ms;
  return {
    stepTimeoutMs: timingValue(runtime.stepTimeoutMs, Math.min(DEFAULT_STEP_TIMEOUT_MS, maximum), 1_000, maximum),
    postLoginTimeoutMs: timingValue(runtime.postLoginTimeoutMs, Math.min(DEFAULT_POST_LOGIN_TIMEOUT_MS, maximum), 1_000, maximum),
    stepSettleMs: timingValue(runtime.stepSettleMs, DEFAULT_STEP_SETTLE_MS, 0, 5_000),
    domainSelectionSettleMs: timingValue(runtime.domainSelectionSettleMs, DEFAULT_DOMAIN_SELECTION_SETTLE_MS, 0, 30_000),
    queryResultSettleMs: timingValue(runtime.queryResultSettleMs, DEFAULT_QUERY_RESULT_SETTLE_MS, 0, 30_000),
  };
}

async function firstVisibleNow(locators) {
  for (const locator of locators) {
    const count = await locator.count().catch(() => 0);
    for (let index = 0; index < count; index++) {
      const candidate = locator.nth(index);
      if (await candidate.isVisible().catch(() => false)) return candidate;
    }
  }
  return null;
}

async function firstVisible(locators, timeoutMs = 10_000, errorCode = "CONTROL_NOT_FOUND", errorMessage = "腾讯云控制台页面结构与自动化基准不一致，未找到所需控件。") {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const candidate = await firstVisibleNow(locators);
    if (candidate) return candidate;
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
  }
  throw new AutomationError(errorCode, errorMessage);
}

async function firstVisibleState(states, timeoutMs, errorCode, errorMessage) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    for (const state of states) {
      const locator = await firstVisibleNow(state.locators);
      if (locator) return { name: state.name, locator };
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
  }
  throw new AutomationError(errorCode, errorMessage);
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

async function settleStep(page, timing, events, stage) {
  if (timing.stepSettleMs <= 0) return;
  events.debug("step_settle_wait", `${stage} 操作完成，等待页面稳定 ${timing.stepSettleMs} ms。`);
  await page.waitForTimeout(timing.stepSettleMs);
}

function childUserCandidates(page) {
  return [
    page.getByRole("button", { name: "子用户", exact: true }),
    page.getByText("子用户", { exact: true }),
  ];
}

function accountInputCandidates(page) {
  return [
    page.getByRole("textbox", { name: /子用户名.*主账号 ID/ }),
    page.locator('input[placeholder*="子用户名"]'),
    page.locator('input[type="text"]'),
  ];
}

function domainChooserCandidates(page) {
  return [
    page.locator("#ioa-v1 #chevron-down"),
    page.locator("div").filter({ hasText: /^请选择域$/ }),
    page.getByText("请选择域", { exact: true }),
  ];
}

function eventViewCandidates(page) {
  return [
    page.locator("div").filter({ hasText: /^进程事件$/ }),
    page.getByText("进程事件", { exact: true }),
  ];
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
  if (request.debug_mode !== undefined && typeof request.debug_mode !== "boolean")
    throw new AutomationError("INVALID_REQUEST", "debug_mode 必须是布尔值。");
  return {
    ...request,
    timeout_ms: timeoutMs,
    debug_mode: request.debug_mode === true,
    download_path: resolve(request.download_path),
  };
}

async function selectChildUser(page, timing) {
  const state = await firstVisibleState([
    { name: "selector", locators: childUserCandidates(page) },
    { name: "form_ready", locators: accountInputCandidates(page) },
  ], timing.stepTimeoutMs, "LOGIN_FORM_NOT_READY", "登录页在步骤等待时间内未显示子用户入口或账号输入框。");
  if (state.name === "form_ready") return "already_ready";
  await state.locator.click();
  await firstVisible(
    accountInputCandidates(page),
    timing.stepTimeoutMs,
    "LOGIN_FORM_NOT_READY",
    "选择子用户后，账号输入框在步骤等待时间内仍未就绪。",
  );
  return "selected";
}

async function selectDefaultDomain(page, timing, events) {
  const state = await firstVisibleState([
    { name: "domain_chooser", locators: domainChooserCandidates(page) },
    { name: "event_view_ready", locators: eventViewCandidates(page) },
  ], timing.postLoginTimeoutMs, "POST_LOGIN_STATE_TIMEOUT", "登录提交后，域选择器和 EDR 事件页在等待时间内均未出现；可能仍在登录验证或页面加载中。");
  if (state.name === "event_view_ready") return "not_required";
  if (timing.domainSelectionSettleMs > 0) {
    events.debug("domain_selection_wait", `域选择器已出现，等待页面稳定 ${timing.domainSelectionSettleMs} ms 后再选择默认域。`);
    await page.waitForTimeout(timing.domainSelectionSettleMs);
  }
  await state.locator.click();
  await clickFirst([
    page.getByRole("listitem", { name: "默认域" }),
    page.getByText("默认域", { exact: true }),
  ], timing.stepTimeoutMs);
  await clickFirst([
    page.getByRole("button", { name: "确定", exact: true }),
    page.locator("#tea-overlay-root").getByRole("button", { name: "确定", exact: true }),
  ], timing.stepTimeoutMs);
  await firstVisible(
    eventViewCandidates(page),
    timing.postLoginTimeoutMs,
    "EVENT_VIEW_NOT_READY",
    "默认域已确认，但 EDR 事件页在等待时间内仍未就绪。",
  );
  return "selected";
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
  const timeInput = await firstVisible([
    page.getByRole("textbox", { name: "选择时间" }),
    page.locator('input[placeholder*="选择时间"]'),
  ], timeoutMs);
  await timeInput.click();
  await timeInput.fill(queryStartLocal);
  await timeInput.press("Enter");
  await clickFirst([
    page.locator("#tea-overlay-root").getByRole("button", { name: "检索", exact: true }),
  ], timeoutMs);
}

function redactText(value, secrets) {
  let result = String(value ?? "").replace(/[\r\n]+/g, " ");
  for (const secret of secrets) {
    if (secret) result = result.split(secret).join("[REDACTED]");
  }
  result = result.replace(/\b(password|authorization|cookie|token)(\s*[:=]\s*)[^\s,;]+/gi, "$1$2[REDACTED]");
  return result.length <= 4_096 ? result : result.slice(0, 4_096) + "…";
}

function createEventEmitter(runtime, request) {
  const secrets = [request.account, request.password];
  const sink = runtime.onEvent ?? ((event) => process.stderr.write(JSON.stringify(event) + "\n"));
  let currentProgress = 0;
  let currentStage = "initializing";
  const emit = (stage, message, progress, level = "info", detailed = false) => {
    currentProgress = Math.max(currentProgress, Math.min(100, Math.max(0, progress)));
    if (!detailed) currentStage = stage;
    const event = {
      schema_version: "1.0",
      type: detailed ? "debug" : "progress",
      timestamp_utc: new Date().toISOString(),
      level,
      stage,
      message: redactText(message, secrets),
      progress: currentProgress,
    };
    try { sink(event); } catch {}
  };
  return {
    emit,
    debug(stage, message, level = "debug") {
      if (request.debug_mode) emit(stage, message, currentProgress, level, true);
    },
    get progress() { return currentProgress; },
    get stage() { return currentStage; },
  };
}

export function buildLaunchOptions(request, runtime = {}) {
  return {
    channel: runtime.channel ?? "msedge",
    headless: runtime.headless ?? !request.debug_mode,
    downloadsPath: dirname(request.download_path),
    ...(request.debug_mode ? { slowMo: runtime.slowMo ?? 120 } : {}),
  };
}

export async function runTencentEdrExport(rawRequest, runtime = {}) {
  const request = validateRequest(rawRequest);
  const events = createEventEmitter(runtime, request);
  const timing = buildAutomationTiming(request, runtime);
  await mkdir(dirname(request.download_path), { recursive: true });
  const launch = runtime.launch ?? ((options) => chromium.launch(options));
  events.emit("launch_browser", request.debug_mode
    ? "正在启动可见的 Microsoft Edge 调试窗口。"
    : "正在启动 Microsoft Edge 自动化。", 10);
  let browser;
  try {
    browser = await launch(buildLaunchOptions(request, runtime));
    events.emit("create_context", "浏览器已启动，正在创建隔离下载上下文。", 15);
    const context = await browser.newContext({ acceptDownloads: true, locale: "zh-CN" });
    const page = await context.newPage();
    page.setDefaultTimeout(timing.stepTimeoutMs);

    if (request.debug_mode) {
      page.on("console", (message) => {
        if (message.type() === "warning" || message.type() === "error")
          events.debug("browser_console", "浏览器控制台 " + message.type() + "：" + message.text(), message.type() === "error" ? "error" : "warning");
      });
      page.on("pageerror", (error) => events.debug("page_error", "页面脚本异常：" + error.message, "error"));
      page.on("requestfailed", (failedRequest) => {
        let origin = "未知来源";
        try { origin = new URL(failedRequest.url()).origin; } catch {}
        const failure = failedRequest.failure()?.errorText ?? "unknown";
        events.debug("request_failed", "请求失败：" + failedRequest.method() + " " + origin + " · " + failedRequest.resourceType() + " · " + failure, "warning");
      });
      page.on("response", (response) => {
        if (response.status() < 400) return;
        let origin = "未知来源";
        try { origin = new URL(response.url()).origin; } catch {}
        events.debug("http_error", "HTTP " + response.status() + " · " + origin, "warning");
      });
    }

    events.debug("wait_policy", `等待策略：常规控件最长 ${timing.stepTimeoutMs} ms，登录后状态最长 ${timing.postLoginTimeoutMs} ms，每步稳定等待 ${timing.stepSettleMs} ms，域选择前等待 ${timing.domainSelectionSettleMs} ms，时间检索结果等待 ${timing.queryResultSettleMs} ms。`);
    await settleStep(page, timing, events, "create_context");

    events.emit("open_login_page", "正在打开腾讯云登录页面。", 20);
    await page.goto(runtime.loginUrl ?? LOGIN_URL, { waitUntil: "domcontentloaded", timeout: request.timeout_ms });
    await settleStep(page, timing, events, "open_login_page");
    events.debug("login_page_ready", "登录页 DOM 已加载并完成稳定等待。");

    events.emit("select_child_user", "正在等待子用户入口或登录表单就绪。", 27);
    const childUserResult = await selectChildUser(page, timing);
    await settleStep(page, timing, events, "select_child_user");
    events.debug(childUserResult === "selected" ? "child_user_selected" : "child_user_not_required", childUserResult === "selected" ? "已选择子用户登录方式，账号输入框已就绪。" : "账号输入框已就绪，无需切换子用户登录方式。");

    events.emit("fill_credentials", "正在定位并填写子账号与密码输入框。", 34);
    await fillFirst(accountInputCandidates(page), request.account, timing.stepTimeoutMs);
    events.debug("account_filled", "子账号输入框已填写；字段内容不会写入日志。");
    await fillFirst([
      page.getByRole("textbox", { name: "请输入登录密码" }),
      page.locator('input[placeholder="请输入登录密码"]'),
      page.locator('input[type="password"]'),
    ], request.password, timing.stepTimeoutMs);
    await settleStep(page, timing, events, "fill_credentials");
    events.debug("password_filled", "密码输入框已填写；字段内容不会写入日志。");

    events.emit("submit_login", "正在提交腾讯云登录表单；如需 MFA，请在可见浏览器中完成。", 42);
    await clickFirst([page.getByRole("button", { name: "登录", exact: true })], timing.stepTimeoutMs);
    await settleStep(page, timing, events, "submit_login");
    events.debug("login_submitted", "登录表单已提交，正在等待域选择器或 EDR 事件页出现。");

    events.emit("select_domain", "正在等待域选择器或 EDR 事件页就绪。", 48);
    const domainResult = await selectDefaultDomain(page, timing, events);
    await settleStep(page, timing, events, "select_domain");
    events.debug("domain_step_completed", domainResult === "selected" ? "默认域已选择，EDR 事件页已就绪。" : "EDR 事件页已就绪，确认本次登录无需域选择。");

    events.emit("open_event_view", "正在进入进程事件并切换到全部事件。", 55);
    await clickFirst(eventViewCandidates(page), timing.stepTimeoutMs);
    await clickFirst([
      page.getByTitle("全部"),
      page.getByText("全部事件", { exact: true }),
    ], timing.stepTimeoutMs);
    await clickFirst([
      page.locator("#tea-overlay-root").getByRole("button", { name: "确定", exact: true }),
      page.getByRole("button", { name: "确定", exact: true }),
    ], timing.stepTimeoutMs);
    await settleStep(page, timing, events, "open_event_view");
    events.debug("event_view_ready", "全部事件视图已确认并完成稳定等待。");

    events.emit("apply_host_filter", "正在添加主机名称筛选条件。", 63);
    await addHostFilter(page, request.device_name, timing.stepTimeoutMs);
    await settleStep(page, timing, events, "apply_host_filter");
    events.debug("host_filter_applied", "主机名称筛选已应用并完成稳定等待。");

    events.emit("apply_time_filter", "正在添加采集时间筛选并执行检索。", 71);
    await addStartTimeFilter(page, request.query_start_local, timing.stepTimeoutMs);
    if (timing.queryResultSettleMs > 0) {
      events.debug("query_result_wait", `时间筛选已提交，等待检索结果加载 ${timing.queryResultSettleMs} ms。`);
      await page.waitForTimeout(timing.queryResultSettleMs);
    }
    await settleStep(page, timing, events, "apply_time_filter");
    events.debug("time_filter_applied", "采集时间筛选已应用，检索结果已完成稳定等待。");

    events.emit("prepare_export", "正在打开导出对话框并选择全部字段与 JSON 格式。", 79);
    await clickFirst([
      page.getByRole("button", { name: /导出/ }),
      page.locator('button:has-text("导出")'),
    ], timing.stepTimeoutMs);
    const selectAll = await firstVisible([
      page.locator("label").filter({ hasText: /^全选$/ }),
      page.getByText("全选", { exact: true }),
    ], timing.stepTimeoutMs);
    await selectAll.click();
    await selectAll.click();
    await clickFirst([page.getByText("xlsx", { exact: true })], timing.stepTimeoutMs);
    await clickFirst([
      page.getByRole("listitem", { name: "json", exact: true }),
      page.getByText("json", { exact: true }),
    ], timing.stepTimeoutMs);
    await settleStep(page, timing, events, "prepare_export");
    events.debug("export_options_ready", "已选择全部字段和 JSON 导出格式，并完成稳定等待。");

    events.emit("wait_download", "已提交导出请求，正在等待腾讯云生成下载文件。", 87);
    const downloadPromise = page.waitForEvent("download", { timeout: request.timeout_ms });
    await clickFirst([
      page.locator("#tea-overlay-root").getByRole("button", { name: "导出", exact: true }),
      page.getByRole("button", { name: "导出", exact: true }),
    ], timing.stepTimeoutMs);
    await settleStep(page, timing, events, "wait_download");
    const download = await downloadPromise;
    const failure = await download.failure();
    if (failure) throw new AutomationError("DOWNLOAD_FAILED", "腾讯云控制台生成了下载任务，但文件下载失败。");

    events.emit("save_download", "浏览器已收到下载事件，正在保存到当前轮次目录。", 91);
    await download.saveAs(request.download_path);
    await settleStep(page, timing, events, "save_download");
    events.debug("download_saved", "下载文件已保存到受限的当前轮次目录。");
    await context.close();
    return { status: "succeeded", download_path: request.download_path };
  } catch (error) {
    events.debug("automation_exception", error instanceof Error ? error.message : String(error), "error");
    events.emit(events.stage, "浏览器自动化在当前阶段失败，请查看该阶段附近的详细日志。", events.progress, "error");
    throw error;
  } finally {
    if (browser) await browser.close();
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
