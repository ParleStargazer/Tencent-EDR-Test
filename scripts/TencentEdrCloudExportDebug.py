"""腾讯 EDR 云端日志导出的独立浏览器自动化调试脚本。

本文件与平台当前使用的 Node.js 自动化链路隔离，便于单独修改、运行和观察。
默认启动可见的 Microsoft Edge。运行前只需修改本文件顶部的用户配置区，
执行过程中不会再从终端读取账号、密码或其他参数。

首次运行前，创建项目专用 Conda 环境并安装 Python Playwright：

    conda create --prefix .\\.conda python=3.13 -y
    .\\.conda\\python.exe -m pip install playwright

本脚本直接使用系统已安装的 Edge，不需要执行 ``playwright install``。修改配置后运行：

    .\\.conda\\python.exe scripts\\TencentEdrCloudExportDebug.py

选择器、等待参数和执行步骤全部保留在本文件中，调试完成后可直接对照修改
``web/automation/tencent-edr-cloud-export.mjs``。
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

try:
    from playwright.sync_api import (
        Browser,
        BrowserContext,
        Locator,
        Page,
        Playwright,
        sync_playwright,
    )
    from playwright.sync_api import (
        Error as PlaywrightError,
    )
except ModuleNotFoundError:
    print(
        "缺少 Python Playwright。请在项目专用 Conda 环境中执行："
        "python -m pip install playwright",
        file=sys.stderr,
    )
    raise SystemExit(3)

if os.name == "nt":
    # 保证直接运行、重定向到文件或由 PowerShell 调用时都使用一致的中文编码。
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


# ---------------------------------------------------------------------------
# 用户配置区：运行前只修改本段，不需要传入任何命令行参数
# ---------------------------------------------------------------------------

# 警告：真实密码只用于本机临时调试，不要提交填写真实凭据后的文件。
TENCENT_CLOUD_ACCOUNT = "请填写腾讯云子账号"
TENCENT_CLOUD_PASSWORD = "请填写腾讯云登录密码"
EDR_DEVICE_NAME = "请填写 EDR 设备名称"

# 留空时自动使用脚本启动时间前 10 秒；也可填写 "2026-08-22 16:00:00"。
QUERY_START_LOCAL = ""

OUTPUT_PATH = Path(r"C:\Temp\tencent-edr-cloud.json")
DEBUG_LOG_PATH = Path(r"C:\Temp\tencent-edr-cloud-debug.jsonl")
FAILURE_SCREENSHOT_PATH = Path(r"C:\Temp\tencent-edr-cloud-error.png")
TRACE_OUTPUT_PATH = Path(r"C:\Temp\tencent-edr-cloud-trace.zip")

LOGIN_URL = (
    "https://cloud.tencent.com/login?"
    "s_url=https%3A%2F%2Fconsole.cloud.tencent.com%2Fioa%2Fv1%2Fedr%2Fprotect%2Fthreatalarm"
)
DEFAULT_STEP_TIMEOUT_MS = 60_000
DEFAULT_POST_LOGIN_TIMEOUT_MS = 180_000
DEFAULT_NAVIGATION_TIMEOUT_MS = 300_000
DEFAULT_ACTION_SETTLE_MS = 1_000
DEFAULT_POLL_INTERVAL_MS = 200
DEFAULT_SLOW_MO_MS = 150

HEADLESS = False
PAUSE_ON_ERROR = False
KEEP_BROWSER_OPEN_AFTER_SUCCESS = False
SAVE_SCREENSHOT_ON_ERROR = False
ENABLE_TRACE = False


class AutomationFailure(RuntimeError):
    """带稳定错误码的自动化失败。"""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class Timing:
    step_timeout_ms: int
    post_login_timeout_ms: int
    navigation_timeout_ms: int
    action_settle_ms: int
    poll_interval_ms: int


@dataclass(frozen=True)
class Candidate:
    """一个可读名称及其对应的 Playwright 定位器。"""

    description: str
    locator: Locator


class DebugLogger:
    """同时向终端和 JSONL 文件输出脱敏调试日志。"""

    def __init__(self, path: Path, secrets: Iterable[str]) -> None:
        self.path = path
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._secrets = tuple(value for value in secrets if value)
        self._stream = self.path.open("a", encoding="utf-8", newline="\n")

    def close(self) -> None:
        self._stream.close()

    def _redact(self, value: object) -> str:
        text = str(value).replace("\r", " ").replace("\n", " ")
        for secret in self._secrets:
            text = text.replace(secret, "[REDACTED]")
        return re.sub(
            r"\b(password|authorization|cookie|token)(\s*[:=]\s*)[^\s,;]+",
            r"\1\2[REDACTED]",
            text,
            flags=re.IGNORECASE,
        )[:4096]

    def write(self, level: str, stage: str, message: str, **details: object) -> None:
        safe_message = self._redact(message)
        safe_details = {key: self._redact(value) for key, value in details.items()}
        event = {
            "timestamp_utc": datetime.now(timezone.utc).isoformat(),
            "level": level,
            "stage": stage,
            "message": safe_message,
            "details": safe_details,
        }
        self._stream.write(json.dumps(event, ensure_ascii=False) + "\n")
        self._stream.flush()
        suffix = ""
        if safe_details:
            suffix = " · " + " · ".join(
                f"{key}={value}" for key, value in safe_details.items()
            )
        print(
            f"[{event['timestamp_utc']}] [{level.upper()}] {stage}: {safe_message}{suffix}",
            flush=True,
        )


# ---------------------------------------------------------------------------
# 页面定位器集中区。用户调试成功后，主要从这里回填 Node.js 实现。
# ---------------------------------------------------------------------------


def child_user_candidates(page: Page) -> list[Candidate]:
    return [
        Candidate(
            "role=button, name=子用户, exact",
            page.get_by_role("button", name="子用户", exact=True),
        ),
        Candidate("text=子用户, exact", page.get_by_text("子用户", exact=True)),
    ]


def account_input_candidates(page: Page) -> list[Candidate]:
    return [
        Candidate(
            "role=textbox, name=/子用户名.*主账号 ID/",
            page.get_by_role("textbox", name=re.compile(r"子用户名.*主账号 ID")),
        ),
        Candidate(
            'css=input[placeholder*="子用户名"]',
            page.locator('input[placeholder*="子用户名"]'),
        ),
        Candidate('css=input[type="text"]', page.locator('input[type="text"]')),
    ]


def password_input_candidates(page: Page) -> list[Candidate]:
    return [
        Candidate(
            "role=textbox, name=请输入登录密码",
            page.get_by_role("textbox", name="请输入登录密码"),
        ),
        Candidate(
            'css=input[placeholder="请输入登录密码"]',
            page.locator('input[placeholder="请输入登录密码"]'),
        ),
        Candidate('css=input[type="password"]', page.locator('input[type="password"]')),
    ]


def domain_chooser_candidates(page: Page) -> list[Candidate]:
    return [
        Candidate(
            "div has_text=/^请选择域$/",
            page.locator("div").filter(has_text=re.compile(r"^请选择域$")),
        ),
        Candidate("text=请选择域, exact", page.get_by_text("请选择域", exact=True)),
    ]


def event_view_candidates(page: Page) -> list[Candidate]:
    return [
        Candidate(
            "div has_text=/^进程事件$/",
            page.locator("div").filter(has_text=re.compile(r"^进程事件$")),
        ),
        Candidate("text=进程事件, exact", page.get_by_text("进程事件", exact=True)),
    ]


def exact_text_candidates(
    page: Page, text: str, *, role: str | None = None
) -> list[Candidate]:
    candidates: list[Candidate] = []
    if role:
        candidates.append(
            Candidate(
                f"role={role}, name={text}, exact",
                page.get_by_role(role, name=text, exact=True),
            )
        )
    candidates.append(
        Candidate(f"text={text}, exact", page.get_by_text(text, exact=True))
    )
    return candidates


def overlay_button_candidates(page: Page, name: str) -> list[Candidate]:
    return [
        Candidate(
            f"#tea-overlay-root role=button, name={name}, exact",
            page.locator("#tea-overlay-root").get_by_role(
                "button", name=name, exact=True
            ),
        ),
        Candidate(
            f"role=button, name={name}, exact",
            page.get_by_role("button", name=name, exact=True),
        ),
    ]


# ---------------------------------------------------------------------------
# 可观察的等待与动作封装
# ---------------------------------------------------------------------------


def page_state(page: Page) -> dict[str, str]:
    try:
        title = page.title()
    except PlaywrightError:  # 页面可能正在切换或已经关闭。
        title = "<unavailable>"
    url = page.url
    return {"url": url, "title": title}


def first_visible_now(candidates: Sequence[Candidate]) -> Candidate | None:
    for candidate in candidates:
        try:
            count = candidate.locator.count()
        except PlaywrightError:
            continue
        for index in range(count):
            item = candidate.locator.nth(index)
            try:
                if item.is_visible():
                    return Candidate(f"{candidate.description} [index={index}]", item)
            except PlaywrightError:
                continue
    return None


def first_visible(
    page: Page,
    candidates: Sequence[Candidate],
    timeout_ms: int,
    logger: DebugLogger,
    label: str,
    *,
    error_code: str = "CONTROL_NOT_FOUND",
    error_message: str = "未找到所需控件。",
) -> Candidate:
    started = time.monotonic()
    candidate_names = " | ".join(candidate.description for candidate in candidates)
    logger.write(
        "debug",
        "wait_element",
        f"开始等待：{label}",
        timeout_ms=timeout_ms,
        candidates=candidate_names,
    )
    deadline = started + timeout_ms / 1000
    while time.monotonic() < deadline:
        found = first_visible_now(candidates)
        if found:
            elapsed_ms = round((time.monotonic() - started) * 1000)
            logger.write(
                "debug",
                "element_found",
                f"已找到：{label}",
                candidate=found.description,
                elapsed_ms=elapsed_ms,
            )
            return found
        time.sleep(DEFAULT_POLL_INTERVAL_MS / 1000)
    state = page_state(page)
    raise AutomationFailure(
        error_code,
        f"{error_message} 步骤={label}；候选={candidate_names}；"
        f"等待={timeout_ms}ms；URL={state['url']}；标题={state['title']}",
    )


def first_visible_state(
    page: Page,
    states: Sequence[tuple[str, Sequence[Candidate]]],
    timeout_ms: int,
    logger: DebugLogger,
    label: str,
    *,
    error_code: str,
    error_message: str,
) -> tuple[str, Candidate]:
    started = time.monotonic()
    state_names = " | ".join(name for name, _ in states)
    logger.write(
        "debug",
        "wait_state",
        f"开始等待页面状态：{label}",
        timeout_ms=timeout_ms,
        states=state_names,
    )
    deadline = started + timeout_ms / 1000
    while time.monotonic() < deadline:
        for state_name, candidates in states:
            found = first_visible_now(candidates)
            if found:
                elapsed_ms = round((time.monotonic() - started) * 1000)
                logger.write(
                    "debug",
                    "state_found",
                    f"页面已进入状态：{state_name}",
                    candidate=found.description,
                    elapsed_ms=elapsed_ms,
                )
                return state_name, found
        time.sleep(DEFAULT_POLL_INTERVAL_MS / 1000)
    state = page_state(page)
    raise AutomationFailure(
        error_code,
        f"{error_message} 步骤={label}；状态={state_names}；等待={timeout_ms}ms；"
        f"URL={state['url']}；标题={state['title']}",
    )


def settle_after_action(
    page: Page, timing: Timing, logger: DebugLogger, action: str
) -> None:
    if timing.action_settle_ms <= 0:
        return
    logger.write(
        "debug",
        "action_settle",
        f"动作完成，等待页面稳定：{action}",
        wait_ms=timing.action_settle_ms,
    )
    page.wait_for_timeout(timing.action_settle_ms)


def click_candidate(
    page: Page, candidate: Candidate, timing: Timing, logger: DebugLogger, label: str
) -> None:
    logger.write("debug", "click", f"点击：{label}", candidate=candidate.description)
    candidate.locator.click(timeout=timing.step_timeout_ms)
    settle_after_action(page, timing, logger, label)


def click_first(
    page: Page,
    candidates: Sequence[Candidate],
    timing: Timing,
    logger: DebugLogger,
    label: str,
) -> Candidate:
    candidate = first_visible(page, candidates, timing.step_timeout_ms, logger, label)
    click_candidate(page, candidate, timing, logger, label)
    return candidate


def fill_first(
    page: Page,
    candidates: Sequence[Candidate],
    value: str,
    timing: Timing,
    logger: DebugLogger,
    label: str,
) -> Candidate:
    candidate = first_visible(page, candidates, timing.step_timeout_ms, logger, label)
    logger.write("debug", "fill", f"填写：{label}", candidate=candidate.description)
    candidate.locator.fill(value, timeout=timing.step_timeout_ms)
    settle_after_action(page, timing, logger, label)
    return candidate


def press_key(
    page: Page,
    candidate: Candidate,
    key: str,
    timing: Timing,
    logger: DebugLogger,
    label: str,
) -> None:
    logger.write(
        "debug", "press", f"按键：{label}", key=key, candidate=candidate.description
    )
    candidate.locator.press(key, timeout=timing.step_timeout_ms)
    settle_after_action(page, timing, logger, label)


# ---------------------------------------------------------------------------
# 业务步骤
# ---------------------------------------------------------------------------


def select_child_user(page: Page, timing: Timing, logger: DebugLogger) -> str:
    state_name, candidate = first_visible_state(
        page,
        [
            ("child_user_selector", child_user_candidates(page)),
            ("account_form_ready", account_input_candidates(page)),
        ],
        timing.step_timeout_ms,
        logger,
        "子用户入口或账号输入框",
        error_code="LOGIN_FORM_NOT_READY",
        error_message="登录页未显示子用户入口或账号输入框。",
    )
    if state_name == "account_form_ready":
        return "already_ready"
    click_candidate(page, candidate, timing, logger, "子用户登录方式")
    first_visible(
        page,
        account_input_candidates(page),
        timing.step_timeout_ms,
        logger,
        "选择子用户后的账号输入框",
        error_code="LOGIN_FORM_NOT_READY",
        error_message="选择子用户后账号输入框仍未就绪。",
    )
    return "selected"


def select_default_domain(page: Page, timing: Timing, logger: DebugLogger) -> str:
    state_name, candidate = first_visible_state(
        page,
        [
            ("domain_chooser", domain_chooser_candidates(page)),
            ("event_view_ready", event_view_candidates(page)),
        ],
        timing.post_login_timeout_ms,
        logger,
        "登录后的域选择器或 EDR 事件页",
        error_code="POST_LOGIN_STATE_TIMEOUT",
        error_message="登录后域选择器和 EDR 事件页均未出现。",
    )
    if state_name == "event_view_ready":
        return "not_required"
    click_candidate(page, candidate, timing, logger, "打开域选择器")
    click_first(
        page,
        exact_text_candidates(page, "默认域", role="listitem"),
        timing,
        logger,
        "选择默认域",
    )
    click_first(
        page, overlay_button_candidates(page, "确定"), timing, logger, "确认默认域"
    )
    first_visible(
        page,
        event_view_candidates(page),
        timing.post_login_timeout_ms,
        logger,
        "域确认后的 EDR 事件页",
        error_code="EVENT_VIEW_NOT_READY",
        error_message="默认域已确认，但 EDR 事件页仍未就绪。",
    )
    return "selected"


def add_host_filter(
    page: Page, device_name: str, timing: Timing, logger: DebugLogger
) -> None:
    click_first(
        page,
        [
            Candidate(
                "div has_text=/^添加筛选条件$/",
                page.locator("div").filter(has_text=re.compile(r"^添加筛选条件$")),
            ),
            Candidate(
                "text=添加筛选条件, exact", page.get_by_text("添加筛选条件", exact=True)
            ),
        ],
        timing,
        logger,
        "打开筛选条件",
    )
    click_first(
        page,
        [
            Candidate(
                "div has_text=/^选择字段$/",
                page.locator("div").filter(has_text=re.compile(r"^选择字段$")),
            ),
            Candidate("text=选择字段, exact", page.get_by_text("选择字段", exact=True)),
        ],
        timing,
        logger,
        "打开主机字段选择器",
    )
    click_first(
        page,
        [
            Candidate(
                "#tea-overlay-root text=主机名称（系统环境信息）",
                page.locator("#tea-overlay-root").get_by_text(
                    "主机名称（系统环境信息）", exact=True
                ),
            ),
            Candidate(
                "text=主机名称（系统环境信息）, exact",
                page.get_by_text("主机名称（系统环境信息）", exact=True),
            ),
        ],
        timing,
        logger,
        "选择主机名称字段",
    )
    click_first(
        page,
        [
            Candidate(
                "div has_text=/^选择关系$/",
                page.locator("div").filter(has_text=re.compile(r"^选择关系$")),
            ),
            Candidate("text=选择关系, exact", page.get_by_text("选择关系", exact=True)),
        ],
        timing,
        logger,
        "打开主机筛选关系",
    )
    click_first(
        page,
        exact_text_candidates(page, "等于", role="listitem"),
        timing,
        logger,
        "选择等于关系",
    )
    click_first(
        page,
        [
            Candidate(
                "div has_text=/^请指定$/",
                page.locator("div").filter(has_text=re.compile(r"^请指定$")),
            ),
            Candidate("text=请指定, exact", page.get_by_text("请指定", exact=True)),
        ],
        timing,
        logger,
        "打开主机值输入器",
    )
    host_input = fill_first(
        page,
        [
            Candidate(
                "role=textbox, name=请输入添加内容",
                page.get_by_role("textbox", name="请输入添加内容"),
            ),
            Candidate(
                'css=input[placeholder*="请输入添加内容"]',
                page.locator('input[placeholder*="请输入添加内容"]'),
            ),
        ],
        device_name,
        timing,
        logger,
        "填写设备名称",
    )
    press_key(page, host_input, "Enter", timing, logger, "确认设备名称输入")
    click_first(
        page,
        overlay_button_candidates(page, "确定"),
        timing,
        logger,
        "确认主机筛选条件",
    )


def add_start_time_filter(
    page: Page, query_start: str, timing: Timing, logger: DebugLogger
) -> None:
    click_first(
        page,
        [
            Candidate(
                "role=button, name=/添加条件/",
                page.get_by_role("button", name=re.compile("添加条件")),
            ),
            Candidate(
                'css=button:has-text("添加条件")',
                page.locator('button:has-text("添加条件")'),
            ),
        ],
        timing,
        logger,
        "添加第二个筛选条件",
    )
    click_first(
        page,
        [
            Candidate(
                "div has_text=/^选择字段$/",
                page.locator("div").filter(has_text=re.compile(r"^选择字段$")),
            ),
            Candidate("text=选择字段, exact", page.get_by_text("选择字段", exact=True)),
        ],
        timing,
        logger,
        "打开时间字段选择器",
    )
    click_first(
        page,
        [
            Candidate(
                "role=listitem has_text=其他信息",
                page.get_by_role("listitem").filter(has_text="其他信息"),
            ),
            Candidate("text=其他信息, exact", page.get_by_text("其他信息", exact=True)),
        ],
        timing,
        logger,
        "选择其他信息分类",
    )
    click_first(
        page,
        [
            Candidate(
                "role=listitem has_text=采集时间",
                page.get_by_role("listitem").filter(has_text="采集时间"),
            ),
            Candidate("text=采集时间, exact", page.get_by_text("采集时间", exact=True)),
        ],
        timing,
        logger,
        "选择采集时间字段",
    )
    click_first(
        page,
        [
            Candidate(
                "div has_text=/^选择关系$/",
                page.locator("div").filter(has_text=re.compile(r"^选择关系$")),
            ),
            Candidate("text=选择关系, exact", page.get_by_text("选择关系", exact=True)),
        ],
        timing,
        logger,
        "打开时间筛选关系",
    )
    click_first(
        page,
        exact_text_candidates(page, "大于", role="listitem"),
        timing,
        logger,
        "选择大于关系",
    )
    fill_first(
        page,
        [
            Candidate(
                "role=textbox, name=选择时间",
                page.get_by_role("textbox", name="选择时间"),
            ),
            Candidate(
                'css=input[placeholder*="选择时间"]',
                page.locator('input[placeholder*="选择时间"]'),
            ),
        ],
        query_start,
        timing,
        logger,
        "填写日志起始时间",
    )
    click_first(
        page,
        overlay_button_candidates(page, "确定"),
        timing,
        logger,
        "确认时间筛选条件",
    )
    click_first(
        page, overlay_button_candidates(page, "检索"), timing, logger, "执行检索"
    )


def prepare_export(page: Page, timing: Timing, logger: DebugLogger) -> None:
    click_first(
        page,
        [
            Candidate(
                "role=button, name=/导出/",
                page.get_by_role("button", name=re.compile("导出")),
            ),
            Candidate(
                'css=button:has-text("导出")', page.locator('button:has-text("导出")')
            ),
        ],
        timing,
        logger,
        "打开导出对话框",
    )
    select_all = first_visible(
        page,
        [
            Candidate(
                "label has_text=/^全选$/",
                page.locator("label").filter(has_text=re.compile(r"^全选$")),
            ),
            Candidate("text=全选, exact", page.get_by_text("全选", exact=True)),
        ],
        timing.step_timeout_ms,
        logger,
        "全选导出字段",
    )
    # 保留现有页面基准中的双击行为：第一次清空默认项，第二次选中全部字段。
    click_candidate(page, select_all, timing, logger, "全选导出字段（第 1 次）")
    click_candidate(page, select_all, timing, logger, "全选导出字段（第 2 次）")
    click_first(
        page, exact_text_candidates(page, "xlsx"), timing, logger, "打开导出格式选择器"
    )
    click_first(
        page,
        exact_text_candidates(page, "json", role="listitem"),
        timing,
        logger,
        "选择 JSON 导出格式",
    )


def run_automation(
    playwright: Playwright,
    account: str,
    password: str,
    device_name: str,
    query_start: str,
    output: Path,
    timing: Timing,
    logger: DebugLogger,
    *,
    headless: bool,
    slow_mo_ms: int,
    trace_path: Path | None,
    screenshot_on_error: Path | None,
    pause_on_error: bool,
    keep_open: bool,
) -> None:
    browser: Browser | None = None
    context: BrowserContext | None = None
    page: Page | None = None
    trace_started = False
    try:
        logger.write(
            "info",
            "launch_browser",
            "启动 Microsoft Edge。",
            headless=headless,
            slow_mo_ms=slow_mo_ms,
        )
        browser = playwright.chromium.launch(
            channel="msedge",
            headless=headless,
            slow_mo=slow_mo_ms if not headless else 0,
            downloads_path=str(output.parent),
        )
        context = browser.new_context(accept_downloads=True, locale="zh-CN")
        if trace_path:
            context.tracing.start(screenshots=True, snapshots=True, sources=True)
            trace_started = True
            logger.write(
                "warning",
                "trace_started",
                "已启用 Playwright trace；其中可能包含页面敏感信息。",
                path=trace_path,
            )
        page = context.new_page()
        page.set_default_timeout(timing.step_timeout_ms)
        page.on(
            "console",
            lambda message: logger.write(
                "debug", "browser_console", message.text, kind=message.type
            ),
        )
        page.on(
            "pageerror", lambda error: logger.write("error", "page_error", str(error))
        )
        page.on(
            "requestfailed",
            lambda request: logger.write(
                "warning",
                "request_failed",
                "浏览器请求失败。",
                method=request.method,
                resource_type=request.resource_type,
                url=request.url,
                failure=request.failure,
            ),
        )

        logger.write(
            "info",
            "wait_policy",
            "本轮逐动作等待策略。",
            step_timeout_ms=timing.step_timeout_ms,
            post_login_timeout_ms=timing.post_login_timeout_ms,
            navigation_timeout_ms=timing.navigation_timeout_ms,
            action_settle_ms=timing.action_settle_ms,
            poll_interval_ms=timing.poll_interval_ms,
        )

        logger.write("info", "open_login_page", "打开腾讯云登录页。")
        page.goto(
            LOGIN_URL,
            wait_until="domcontentloaded",
            timeout=timing.navigation_timeout_ms,
        )
        settle_after_action(page, timing, logger, "登录页 DOM 加载")

        logger.write("info", "select_child_user", "选择子用户登录方式。")
        child_user_result = select_child_user(page, timing, logger)
        logger.write(
            "info",
            "select_child_user_completed",
            "子用户登录步骤完成。",
            result=child_user_result,
        )

        logger.write("info", "fill_credentials", "填写登录凭据；日志不会记录字段内容。")
        fill_first(
            page, account_input_candidates(page), account, timing, logger, "填写子账号"
        )
        fill_first(
            page,
            password_input_candidates(page),
            password,
            timing,
            logger,
            "填写登录密码",
        )
        click_first(
            page,
            exact_text_candidates(page, "登录", role="button"),
            timing,
            logger,
            "提交登录",
        )

        logger.write("info", "select_domain", "等待域选择器或 EDR 事件页。")
        domain_result = select_default_domain(page, timing, logger)
        logger.write(
            "info", "select_domain_completed", "域选择步骤完成。", result=domain_result
        )

        logger.write("info", "open_event_view", "切换到全部事件视图。")
        click_first(
            page, event_view_candidates(page), timing, logger, "打开进程事件菜单"
        )
        click_first(
            page,
            [
                Candidate("title=全部", page.get_by_title("全部")),
                Candidate(
                    "text=全部事件, exact", page.get_by_text("全部事件", exact=True)
                ),
            ],
            timing,
            logger,
            "选择全部事件",
        )
        click_first(
            page,
            overlay_button_candidates(page, "确定"),
            timing,
            logger,
            "确认全部事件视图",
        )

        logger.write("info", "apply_host_filter", "添加主机名称筛选。")
        add_host_filter(page, device_name, timing, logger)

        logger.write("info", "apply_time_filter", "添加采集时间筛选并检索。")
        add_start_time_filter(page, query_start, timing, logger)

        logger.write("info", "prepare_export", "选择全部字段和 JSON 导出格式。")
        prepare_export(page, timing, logger)

        logger.write("info", "wait_download", "提交导出并等待浏览器下载事件。")
        with page.expect_download(
            timeout=timing.navigation_timeout_ms
        ) as download_info:
            click_first(
                page,
                overlay_button_candidates(page, "导出"),
                timing,
                logger,
                "确认导出",
            )
        download = download_info.value
        failure = download.failure()
        if failure:
            raise AutomationFailure("DOWNLOAD_FAILED", f"浏览器下载失败：{failure}")
        download.save_as(str(output))
        logger.write("info", "completed", "EDR 云端日志已保存。", output=output)

        if keep_open and not headless:
            input("自动化已完成。按 Enter 关闭浏览器……")
    except Exception:
        if page is not None and screenshot_on_error is not None:
            try:
                screenshot_on_error.parent.mkdir(parents=True, exist_ok=True)
                page.screenshot(path=str(screenshot_on_error), full_page=True)
                logger.write(
                    "warning",
                    "failure_screenshot",
                    "已保存失败页面截图；截图可能包含敏感信息。",
                    path=screenshot_on_error,
                )
            except (OSError, PlaywrightError) as screenshot_error:
                logger.write(
                    "warning",
                    "failure_screenshot_error",
                    "失败截图保存失败。",
                    error=screenshot_error,
                )
        if pause_on_error and page is not None and not headless:
            try:
                input("自动化失败，浏览器将保持打开。检查页面后按 Enter 退出……")
            except EOFError:
                pass
        raise
    finally:
        if context is not None and trace_started and trace_path is not None:
            try:
                trace_path.parent.mkdir(parents=True, exist_ok=True)
                context.tracing.stop(path=str(trace_path))
                logger.write(
                    "info", "trace_saved", "Playwright trace 已保存。", path=trace_path
                )
            except (OSError, PlaywrightError) as trace_error:
                logger.write(
                    "warning",
                    "trace_save_error",
                    "Playwright trace 保存失败。",
                    error=trace_error,
                )
        if context is not None:
            context.close()
        if browser is not None:
            browser.close()


def validate_configuration() -> tuple[str, str, str, str, Path, Path]:
    account = TENCENT_CLOUD_ACCOUNT.strip()
    password = TENCENT_CLOUD_PASSWORD
    device_name = EDR_DEVICE_NAME.strip()
    required_values = (
        ("TENCENT_CLOUD_ACCOUNT", account),
        ("TENCENT_CLOUD_PASSWORD", password),
        ("EDR_DEVICE_NAME", device_name),
    )
    for name, value in required_values:
        if not value or value.startswith("请填写"):
            raise AutomationFailure(
                "INVALID_CONFIGURATION", f"请先在文件顶部配置 {name}。"
            )

    query_start = QUERY_START_LOCAL.strip()
    if not query_start:
        query_start = (datetime.now().astimezone() - timedelta(seconds=10)).strftime(
            "%Y-%m-%d %H:%M:%S"
        )
    try:
        time.strptime(query_start, "%Y-%m-%d %H:%M:%S")
    except ValueError as error:
        raise AutomationFailure(
            "INVALID_CONFIGURATION",
            "QUERY_START_LOCAL 必须留空或使用 yyyy-MM-dd HH:mm:ss 格式。",
        ) from error

    numeric_values = (
        ("DEFAULT_STEP_TIMEOUT_MS", DEFAULT_STEP_TIMEOUT_MS, 1),
        ("DEFAULT_POST_LOGIN_TIMEOUT_MS", DEFAULT_POST_LOGIN_TIMEOUT_MS, 1),
        ("DEFAULT_NAVIGATION_TIMEOUT_MS", DEFAULT_NAVIGATION_TIMEOUT_MS, 1),
        ("DEFAULT_ACTION_SETTLE_MS", DEFAULT_ACTION_SETTLE_MS, 0),
        ("DEFAULT_POLL_INTERVAL_MS", DEFAULT_POLL_INTERVAL_MS, 1),
        ("DEFAULT_SLOW_MO_MS", DEFAULT_SLOW_MO_MS, 0),
    )
    for name, value, minimum in numeric_values:
        if not isinstance(value, int) or isinstance(value, bool) or value < minimum:
            raise AutomationFailure(
                "INVALID_CONFIGURATION", f"{name} 必须是不小于 {minimum} 的整数。"
            )

    output = OUTPUT_PATH.expanduser().resolve()
    log_file = DEBUG_LOG_PATH.expanduser().resolve()
    if output.suffix.lower() != ".json":
        raise AutomationFailure(
            "INVALID_CONFIGURATION", "OUTPUT_PATH 必须是 .json 文件。"
        )
    if log_file.suffix.lower() != ".jsonl":
        raise AutomationFailure(
            "INVALID_CONFIGURATION", "DEBUG_LOG_PATH 必须是 .jsonl 文件。"
        )
    output.parent.mkdir(parents=True, exist_ok=True)
    log_file.parent.mkdir(parents=True, exist_ok=True)
    return account, password, device_name, query_start, output, log_file


def main() -> int:
    logger: DebugLogger | None = None
    try:
        account, password, device_name, query_start, output, log_file = (
            validate_configuration()
        )
        logger = DebugLogger(log_file, (account, password))
        timing = Timing(
            step_timeout_ms=DEFAULT_STEP_TIMEOUT_MS,
            post_login_timeout_ms=DEFAULT_POST_LOGIN_TIMEOUT_MS,
            navigation_timeout_ms=DEFAULT_NAVIGATION_TIMEOUT_MS,
            action_settle_ms=DEFAULT_ACTION_SETTLE_MS,
            poll_interval_ms=DEFAULT_POLL_INTERVAL_MS,
        )
        trace_path = TRACE_OUTPUT_PATH.expanduser().resolve() if ENABLE_TRACE else None
        screenshot_path = (
            FAILURE_SCREENSHOT_PATH.expanduser().resolve()
            if SAVE_SCREENSHOT_ON_ERROR
            else None
        )
        with sync_playwright() as playwright:
            run_automation(
                playwright,
                account,
                password,
                device_name,
                query_start,
                output,
                timing,
                logger,
                headless=HEADLESS,
                slow_mo_ms=DEFAULT_SLOW_MO_MS,
                trace_path=trace_path,
                screenshot_on_error=screenshot_path,
                pause_on_error=PAUSE_ON_ERROR,
                keep_open=KEEP_BROWSER_OPEN_AFTER_SUCCESS,
            )
        print(
            json.dumps(
                {
                    "status": "succeeded",
                    "output": str(output),
                    "debug_log": str(log_file),
                },
                ensure_ascii=False,
            )
        )
        return 0
    except AutomationFailure as error:
        if logger:
            logger.write("error", error.code, str(error))
        else:
            print(f"[{error.code}] {error}", file=sys.stderr)
        return 20
    except Exception as error:  # noqa: BLE001 - 命令行入口必须转换未知浏览器异常。
        if logger:
            logger.write(
                "error",
                "BROWSER_AUTOMATION_FAILED",
                str(error),
                error_type=type(error).__name__,
            )
        else:
            print(
                f"[BROWSER_AUTOMATION_FAILED] {type(error).__name__}: {error}",
                file=sys.stderr,
            )
        return 21
    finally:
        if logger:
            logger.close()


if __name__ == "__main__":
    raise SystemExit(main())
