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
  assert.match(html, /测试后自动下载并导入云端日志/);
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
  assert.match(html, /当前轮次已绑定的云端日志/);
  assert.match(html, /有多份时默认选择最新一份/);
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
  const localApi = await readFile(new URL("../../src/EdrTest/LocalApiService.cs", import.meta.url), "utf8");
  const cloudService = await readFile(new URL("../../src/EdrTest/TencentEdrCloudExportService.cs", import.meta.url), "utf8");
  const cloudAutomation = await readFile(new URL("../automation/tencent-edr-cloud-export.mjs", import.meta.url), "utf8");
  const startScript = await readFile(new URL("../../scripts/Start-EdrTest.ps1", import.meta.url), "utf8");

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
  assert.match(livePage, /cloud_automation:/);
  assert.match(livePage, /腾讯云子账号/);
  assert.match(livePage, /日志起始时间（可选）/);
  assert.match(livePage, /调试模式（浏览器界面可见）/);
  assert.match(livePage, /debug_mode: cloudDebugMode/);
  assert.match(livePage, /aria-label="云端日志导入进度"/);
  assert.match(livePage, /云端导入详细调试日志/);
  assert.match(livePage, /cloud-automation-debug\.jsonl/);
  assert.match(livePage, /\/cloud-imports\/\$\{cloudImport\.import_id\}\/debug-log/);
  assert.match(livePage, /cloud_import_id/);
  assert.match(livePage, /\/runs\/\$\{selectedRunId\}\/cloud-imports/);
  assert.match(localApi, /MapGet\("\/api\/runs\/\{operationId\}\/cloud-imports"/);
  assert.match(localApi, /MapGet\("\/api\/runs\/\{operationId\}\/cloud-imports\/\{importId\}\/debug-log"/);
  assert.match(localApi, /ApiCloudProgressEntry/);
  assert.match(localApi, /DebugLogAvailable/);
  assert.match(localApi, /cloud_import_id/);
  assert.match(cloudService, /RedirectStandardInput = true/);
  assert.match(cloudService, /cloud-automation-debug\.jsonl/);
  assert.match(cloudService, /CloudAutomationJournal/);
  assert.match(cloudService, /\[REDACTED\]/);
  assert.match(cloudService, /process\.StandardInput\.WriteAsync/);
  assert.doesNotMatch(cloudService, /ArgumentList\.Add\(config\.(?:Account|Password)\)/);
  assert.match(cloudAutomation, /readRequestFromStdin/);
  assert.match(cloudAutomation, /headless: runtime\.headless \?\? !request\.debug_mode/);
  assert.match(cloudAutomation, /runtime\.onEvent/);
  assert.match(cloudAutomation, /page\.on\("requestfailed"/);
  assert.match(cloudAutomation, /DEFAULT_STEP_TIMEOUT_MS = 60_000/);
  assert.match(cloudAutomation, /DEFAULT_POST_LOGIN_TIMEOUT_MS = 180_000/);
  assert.match(cloudAutomation, /DEFAULT_STEP_SETTLE_MS = 1_600/);
  assert.match(cloudAutomation, /DEFAULT_DOMAIN_LOOKUP_DELAY_MS = 15_000/);
  assert.match(cloudAutomation, /DEFAULT_FILTER_ACTION_DELAY_MS = 150/);
  assert.match(cloudAutomation, /DEFAULT_QUERY_RESULT_SETTLE_MS = 6_000/);
  assert.match(cloudAutomation, /#ioa-v1 #chevron-down/);
  assert.match(cloudAutomation, /app-ioa-dropdown__header\.app-ioa-dropdown-btn/);
  assert.match(cloudAutomation, /trial: true/);
  assert.match(cloudAutomation, /domainLookupDelayMs[\s\S]*firstVisibleState/);
  assert.match(cloudAutomation, /runFilterAction[\s\S]*waitForTimeout[\s\S]*action\(\)[\s\S]*waitForTimeout/);
  assert.equal((cloudAutomation.match(/await runFilterAction\(page, timing/g) ?? []).length, 20);
  assert.match(cloudAutomation, /timeInput\.press\("Enter"\)/);
  assert.match(cloudAutomation, /firstVisibleState/);
  assert.doesNotMatch(cloudAutomation, /selectDefaultDomain\(page, 30_000\)/);
  assert.match(cloudService, /TimeSpan\.FromMinutes\(10\)/);
  assert.match(styles, /\.cloud-progress-track \{[^}]*height:/);
  assert.match(styles, /\.cloud-progress-log\.debug \{[^}]*max-height:/);
  assert.match(startScript, /function ConvertTo-NativeArgument/);
  assert.match(startScript, /\$backendCommandLine = Join-NativeArguments \$backendArguments/);
  assert.match(startScript, /-ArgumentList \$backendCommandLine/);
  assert.match(startScript, /\$frontendCommandLine = Join-NativeArguments \$frontendArguments/);
  assert.match(startScript, /-ArgumentList \$frontendCommandLine/);
  assert.doesNotMatch(startScript, /-ArgumentList \$backendArguments/);
  assert.match(livePage, /action_name_standards/);
  assert.match(livePage, /edrtest\.actionNameStandards\.v1/);
  assert.match(livePage, /child_file_create_op_name_standards/);
  assert.match(livePage, /edrtest\.childFileCreateOpNameStandards\.v1/);
  assert.match(livePage, /edrtest\.comparisonTimeSettings\.v1/);
  assert.match(livePage, /strong_correlation_time_ms/);
  assert.match(livePage, /candidate_time_limit_ms/);
  assert.match(livePage, /强关联时间（ms）/);
  assert.match(livePage, /无关联候选事件时间上限（ms）/);
  assert.match(livePage, /本轮时间参数/);
  assert.match(livePage, /先按候选上限裁剪，再执行锚点评分/);
  assert.match(livePage, /form\.append\("comparison_id", comparisonId\)/);
  assert.match(livePage, /pollComparisonProgress\(comparisonId/);
  assert.match(livePage, /\/comparisons\/\$\{comparisonId\}\/progress/);
  assert.doesNotMatch(livePage, /response\.body\.getReader\(\)|stream_progress|pendingEventJson/);
  assert.match(localApi, /MapGet\("\/api\/comparisons\/\{comparisonId\}\/progress"/);
  assert.match(localApi, /progressState\.Apply/);
  assert.match(localApi, /return Results\.Json\(result, ApiJson\)/);
  assert.doesNotMatch(localApi, /WriteComparisonStreamEventAsync/);
  assert.match(localApi, /Results\.File\(path, "application\/x-ndjson; charset=utf-8"/);
  assert.match(livePage, /已完成能力数 ÷ 参测能力总数 × 100%/);
  assert.match(livePage, /role="progressbar"/);
  assert.match(livePage, /已完成 \$\{progress\.completed_capabilities\} \/ \$\{progress\.total_capabilities\} 项能力/);
  assert.match(styles, /\.comparison-progress-panel \{[^}]*border-top:/);
  assert.match(styles, /\.comparison-progress-track \{[^}]*margin:\s*0 20px/);
  assert.match(livePage, /defaultStrongCorrelationTimeMs = 15/);
  assert.match(livePage, /defaultCandidateTimeLimitMs = 1000/);
  assert.match(styles, /\.comparison-time-grid \{[^}]*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(livePage, /EDR 原始字段筛选/);
  assert.match(livePage, /Child\.FileCreateOpName/);
  assert.match(livePage, /"win\.file\.rename": "FileRename"/);
  assert.match(livePage, /"win\.process\.create": "ProcessCreate"/);
  assert.match(livePage, /"win\.process\.remote_thread": "RemoteThread"/);
  assert.match(livePage, /"win\.process\.access": "NtOpenProcess"/);
  assert.match(livePage, /"win\.process\.tampering": "WriteProcessMemory"/);
  assert.match(livePage, /"win\.account\.local\.create": "AccountCreate"/);
  assert.match(livePage, /"win\.account\.login": "LoginSuccess, LoginFailed, LoginExplicitCredentials"/);
  assert.match(livePage, /"win\.network\.tcp": "NetBind"/);
  assert.match(livePage, /"win\.network\.udp": "NetBind"/);
  assert.match(livePage, /"win\.network\.url": "NetBind"/);
  assert.match(livePage, /"win\.network\.dns": "NetBind"/);
  assert.match(livePage, /"win\.network\.file_download": "NetBind, FileWriteClose"/);
  assert.match(livePage, /"win\.file\.create": "FileWriteClose"/);
  assert.match(livePage, /"win\.file\.create": "新建文件"/);
  assert.match(livePage, /"win\.file\.modify": "覆盖写文件"/);
  assert.match(livePage, /"win\.file\.open": "打开文件"/);
  assert.match(livePage, /fileCapabilityIds\.has\(baseline\.capability_id\)/);
  assert.match(livePage, /保存到本机/);
  assert.match(livePage, /inter_capability_delay_seconds: nextDelay/);
  assert.match(livePage, /inter_subtest_delay_milliseconds: subtestDelayMs/);
  assert.match(livePage, /子测试间等待（毫秒）/);
  assert.match(livePage, /默认 1000 ms/);
  assert.match(livePage, /run\.highlights/);
  assert.match(livePage, /E \/ 已完成队列/);
  assert.match(livePage, /role="dialog"/);
  assert.match(livePage, /本地 BASELINE/);
  assert.match(livePage, /programs\.\$\{program\.role\}\.pid/);
  assert.match(livePage, /能力开始/);
  assert.match(livePage, /baseline_requirements/);
  assert.match(livePage, /<details className="capability-result-card">/);
  assert.match(livePage, /panel action-name-settings compare-fold-panel/);
  assert.match(livePage, /panel baseline-guide compare-fold-panel/);
  assert.match(livePage, /groupByCapabilityCategory/);
  assert.match(livePage, /baseline-category-card category-fold-card/);
  assert.match(livePage, /comparison-category-card category-fold-card/);
  assert.match(livePage, /open=\{isCloud\}/);
  assert.match(livePage, /EDR 平台导出 JSON/);
  assert.match(livePage, /edr-conclusion-layout/);
  assert.match(livePage, /匹配与完成情况/);
  assert.match(livePage, /候选 EDR 日志块/);
  assert.match(styles, /\.edr-conclusion-layout \{[^}]*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(styles, /\.edr-conclusion-layout \{[^}]*align-items:\s*start[^}]*overflow:\s*visible/);
  assert.match(styles, /\.edr-conclusion-layout > \.requirement-match-panel,[\s\S]*?\.edr-conclusion-layout > \.edr-candidate-section \{[^}]*grid-template-rows:\s*auto auto[^}]*min-height:\s*0/);
  assert.match(styles, /\.edr-conclusion-layout \.requirement-table,[\s\S]*?\.edr-conclusion-layout \.candidate-list \{[^}]*max-height:\s*clamp\(320px, calc\(68vh - 110px\), 570px\)[^}]*overflow:\s*auto[^}]*scrollbar-gutter:\s*stable/);
  assert.match(styles, /@media \(max-width: 900px\)[\s\S]*?\.edr-conclusion-layout \{ grid-template-columns: 1fr; max-height: none; overflow: visible; \}/);
  assert.match(styles, /@media \(max-width: 900px\)[\s\S]*?\.edr-conclusion-layout \.requirement-table,[\s\S]*?\.edr-conclusion-layout \.candidate-list \{[^}]*max-height:\s*none[^}]*overflow:\s*visible/);
  assert.match(livePage, /initialSelectedIndex=\{jsonComparisonIndex\}/);
  assert.match(livePage, /candidates\.indexOf\(candidate\)/);
  assert.match(livePage, /打开候选 #\$\{index \+ 1\} 的 JSON 对照/);
  assert.match(livePage, /onClick=\{\(\) => onOpenCandidate\?\.\(candidate\)\}/);
  assert.match(livePage, /对照 JSON/);
  assert.doesNotMatch(livePage, /className="candidate-detail"|className="candidate-json-grid"/);
  assert.match(livePage, /打开 JSON 对照窗/);
  assert.match(livePage, /逐能力 JSON 对照/);
  assert.match(livePage, /选择 EDR 候选 JSON 块/);
  assert.match(livePage, /BASELINE 一致/);
  assert.match(livePage, /baseline_matches/);
  assert.match(livePage, /JsonCodeViewer/);
  assert.match(livePage, /correlation_score/);
  assert.match(livePage, /time_distance_ms/);
  assert.match(livePage, /time_offset_ms/);
  assert.match(livePage, /formatSignedTimeOffset/);
  assert.match(livePage, /EDR 时间 − 本地行为时间/);
  assert.match(livePage, /EDR 早于本地（提前）/);
  assert.match(livePage, /EDR 晚于本地（延后）/);
  assert.match(livePage, /timestampPointers/);
  assert.match(livePage, /蓝色行表示参与时间差计算的两侧时间戳/);
  assert.match(livePage, /canonical_field === "event\.created"/);
  assert.match(livePage, /低置信度排查/);
  assert.match(livePage, /Action\.Name \{candidate\.custom_action_name_actual \?\? "未读取"\} \/ 标准/);
  assert.match(livePage, /锚点强匹配 · EDR 字段已排除/);
  assert.match(livePage, /EDR 相对本地/);
  assert.match(livePage, /maximum_time_difference_ms/);
  assert.match(livePage, /administratorRequiredIds/);
  assert.match(livePage, /需要管理员权限/);
  assert.match(livePage, /eligible_for_validation/);
  assert.match(livePage, /method_selection/);
  assert.match(livePage, /method_results/);
  assert.match(livePage, /不同方法的通过情况/);
  assert.match(livePage, /已采用最佳方法形成结论/);
  assert.match(livePage, /selected_for_conclusion/);
  assert.match(livePage, /结论采用/);
  assert.match(livePage, /open=\{method\.selected_for_conclusion\}/);
  assert.match(livePage, /每种方法内分别展示本地绝对基准与该方法对应的 EDR 要求；最佳方法默认展开/);
  assert.match(livePage, /methods\.length > 0 \? <MethodComparison[^>]+requirements=\{requirements\}/);
  assert.match(livePage, /<RequirementGroup scope="local" requirements=\{localRequirements\} \/><RequirementGroup scope="cloud" requirements=\{methodRequirements\}/);
  assert.match(livePage, /requirement\.scope === "cloud" && requirement\.expectation_id === method\.expectation_id/);
  assert.doesNotMatch(livePage, /<RequirementGroup scope="local" requirements=\{localRequirements\} \/>\{methods\.length > 0/);
  assert.match(livePage, /const allMethodRequirements = \[\.\.\.localRequirements, \.\.\.methodRequirements\]/);
  assert.match(livePage, /\{passedMethodRequirements\}\/\{allMethodRequirements\.length\}/);
  assert.match(livePage, /\{passedStageRequirements\}\/\{stageRequirements\.length\}/);
  assert.match(livePage, /要求已满足 · 候选日志 \{method\.candidate_count\} 条，其中合格 \{method\.qualified_candidate_count\} 条/);
  assert.doesNotMatch(livePage, /<strong>\{stage\.qualified_candidate_count\}\/\{stage\.candidate_count\}<\/strong>/);
  assert.match(styles, /\.method-result-body > \.requirement-group \+ \.requirement-group/);
  assert.match(livePage, /应包含的测试标记/);
  assert.match(livePage, /读取到的 PID/);
  assert.match(livePage, /总体结论/);
  assert.match(livePage, /\/reports\/\$\{comparison\.comparison_id\}\/conclusion/);
  assert.match(livePage, /下载中文结论/);
  assert.doesNotMatch(livePage, /slice\(0,\s*120\)/);
  assert.match(styles, /\.log-line p \{[^}]*white-space:\s*pre-wrap;/);
  assert.match(styles, /\.json-code-line\.timestamp-match/);
  assert.doesNotMatch(styles, /\.log-line code \{[^}]*text-overflow:\s*ellipsis;/);
  assert.doesNotMatch(styles, /\.selected-candidate-meta code \{[^}]*text-overflow:\s*ellipsis;/);
  assert.doesNotMatch(livePage, /当前为控制面阶段|真实 EXE 调度将在/);

  await assert.rejects(access(new URL("../app/_sites-preview", import.meta.url)));
  await assert.rejects(access(new URL("../db", import.meta.url)));
  await assert.rejects(access(new URL("../examples", import.meta.url)));
});
