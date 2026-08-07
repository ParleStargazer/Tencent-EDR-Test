"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import type { ChangeEvent } from "react";
import Link from "next/link";
import { capabilityCatalog } from "./control-plane";

export type ControlPlaneView = "overview" | "test" | "compare";
type ApiState = "checking" | "online" | "offline";
type RunStatus = "queued" | "running" | "cancelling" | "completed" | "completed_with_errors" | "cancelled" | "failed";
type ValidationStatus = "PASS" | "PARTIAL" | "FAIL" | "INCONCLUSIVE" | "NOT_COMPARED";
type RequirementStatus = "passed" | "failed" | "not_evaluated";

type ApiCapability = {
  capability_id: string;
  version: string;
  name_zh: string;
  name_en: string;
  risk_level: string;
  required_privilege: string;
  programs: string[];
};

type ApiBaselineRequirement = {
  requirement_id: string;
  scope: "local" | "cloud";
  title_zh: string;
  field: string;
  operator: string;
  severity: "required" | "recommended" | "informational";
};

type ApiBaseline = {
  baseline_id: string;
  capability_id: string;
  title: string;
  risk_level: string;
  version: string;
  requirements: ApiBaselineRequirement[];
};

type ApiMapping = {
  profile_id: string;
  vendor: string;
  product: string;
  description: string;
};

type ApiRunStep = {
  capability_id: string;
  name_zh: string;
  sequence: number;
  status: "pending" | "running" | "passed" | "error" | "skipped" | "cancelled";
  status_label: string;
};

type ApiRunLog = {
  timestamp_utc: string;
  level: string;
  source: string;
  message: string;
  capability_id?: string;
  important: boolean;
};

type ApiRun = {
  operation_id: string;
  run_id?: string;
  name: string;
  status: RunStatus;
  progress: number;
  phase: string;
  capability_ids: string[];
  allow_high_risk: boolean;
  inter_capability_delay_seconds: number;
  completed_capabilities: number;
  current_capability_id?: string;
  wait_remaining_seconds?: number;
  steps: ApiRunStep[];
  logs: ApiRunLog[];
  highlights: ApiRunLog[];
  started_at_utc: string;
  ended_at_utc?: string;
  database_name?: string;
  local_export_available: boolean;
  error?: string;
};

type BaselineRequirementResult = ApiBaselineRequirement & {
  expectation_id?: string;
  status: RequirementStatus;
  expected?: unknown;
  actual?: unknown;
  message?: string;
};

type ValidationEntry = {
  case_run_id: string;
  capability_id: string;
  display_name_zh?: string;
  display_name_en?: string;
  baseline_id?: string;
  baseline_version?: string;
  baseline_title?: string;
  local_status: string;
  validation_status: ValidationStatus;
  export_coverage: string;
  candidate_count: number;
  baseline_requirements?: BaselineRequirementResult[];
  warnings?: string[];
};

type ValidationResult = {
  comparison_id: string;
  compared_at_utc: string;
  summary: { pass: number; partial: number; fail: number; inconclusive: number; not_compared: number };
  conclusion: {
    verdict: Exclude<ValidationStatus, "NOT_COMPARED">;
    label_zh: string;
    statement_zh: string;
    pass_rate: number | null;
  };
  capabilities: ValidationEntry[];
};

type FileChoice = { file: File | null; state: "empty" | "ready"; name: string; detail: string };

const emptyFile: FileChoice = { file: null, state: "empty", name: "尚未选择文件", detail: "等待导入" };
const allCapabilities = capabilityCatalog.flatMap((category) => category.capabilities.map((capability) => ({ ...capability, categoryId: category.id })));
const viteEnvironment = import.meta.env as Record<string, string | undefined>;
const apiRoot = (viteEnvironment.VITE_EDR_API_URL ?? "http://127.0.0.1:4317/api").replace(/\/$/, "");
const apiToken = viteEnvironment.VITE_EDR_API_TOKEN;

function apiHeaders(extra?: HeadersInit): Headers {
  const headers = new Headers(extra);
  if (apiToken) headers.set("X-EDRTest-Token", apiToken);
  return headers;
}

async function apiRequest<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiRoot}${path}`, { ...init, headers: apiHeaders(init?.headers) });
  if (!response.ok) {
    let message = `本地服务返回 HTTP ${response.status}`;
    try {
      const payload = (await response.json()) as { error?: string };
      if (payload.error) message = payload.error;
    } catch {
      // 非 JSON 错误响应保留状态码。
    }
    throw new Error(message);
  }
  return (await response.json()) as T;
}

async function apiDownload(path: string): Promise<Blob> {
  const response = await fetch(`${apiRoot}${path}`, { headers: apiHeaders() });
  if (!response.ok) throw new Error(`报告下载失败（HTTP ${response.status}）`);
  return response.blob();
}

function isActive(status: RunStatus): boolean {
  return status === "queued" || status === "running" || status === "cancelling";
}

function formatTime(value?: string): string {
  if (!value) return "—";
  return new Intl.DateTimeFormat("zh-CN", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false }).format(new Date(value));
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function runStatusLabel(status: RunStatus): string {
  return { queued: "等待执行", running: "正在执行", cancelling: "正在取消", completed: "本地通过", completed_with_errors: "带错误完成", cancelled: "已取消", failed: "执行失败" }[status];
}

function validationStatusLabel(status: ValidationStatus): string {
  return { PASS: "通过", PARTIAL: "部分通过", FAIL: "失败", INCONCLUSIVE: "无法判定", NOT_COMPARED: "未比较" }[status];
}

function requirementStatusLabel(status: RequirementStatus): string {
  return { passed: "已满足", failed: "未满足", not_evaluated: "未检查" }[status];
}

function severityLabel(severity: string): string {
  return { required: "必需", recommended: "建议", informational: "信息" }[severity] ?? severity;
}

function coverageLabel(value: string): string {
  return { verified: "范围已由清单验证", inferred: "范围由日志时间推断", assumed: "用户声明范围完整", insufficient: "日志范围证据不足" }[value] ?? value;
}

function statusDot(status: RunStatus): string {
  if (status === "completed") return "green";
  if (isActive(status)) return "blue";
  return "red";
}

function displayValue(value: unknown): string {
  if (value === null || value === undefined || value === "") return "未采集到值";
  if (value === true) return "是";
  if (value === false) return "否";
  if (typeof value === "object" && value !== null && "min" in value) {
    const range = value as { min?: unknown; max?: unknown };
    return range.max === null || range.max === undefined ? `至少 ${range.min} 条` : `${range.min}–${range.max} 条`;
  }
  const text = typeof value === "string" ? value : JSON.stringify(value);
  return text.length > 120 ? `${text.slice(0, 120)}…` : text;
}

function requirementEvidence(requirement: BaselineRequirementResult): { expectedLabel: string; expectedValue: string; actualLabel: string; actualValue: string } {
  const isPid = requirement.field.endsWith(".pid");
  const isCommandLine = requirement.field.endsWith(".command_line");
  const isMd5 = requirement.field.endsWith(".md5");

  if (requirement.operator === "present") {
    return {
      expectedLabel: "校验条件",
      expectedValue: "必须采集到值",
      actualLabel: isPid ? "读取到的 PID" : "读取结果",
      actualValue: displayValue(requirement.actual),
    };
  }
  if (requirement.operator === "absent") {
    return { expectedLabel: "校验条件", expectedValue: "必须为空", actualLabel: "读取结果", actualValue: displayValue(requirement.actual) };
  }
  if (requirement.operator === "contains") {
    return {
      expectedLabel: isCommandLine ? "应包含的测试标记" : "应包含内容",
      expectedValue: displayValue(requirement.expected),
      actualLabel: isCommandLine ? "实际命令行" : "实际内容",
      actualValue: displayValue(requirement.actual),
    };
  }
  if (requirement.field === "event.count") {
    return { expectedLabel: "要求数量", expectedValue: displayValue(requirement.expected), actualLabel: "找到数量", actualValue: displayValue(requirement.actual) };
  }
  return {
    expectedLabel: isPid ? "本地期望 PID" : isMd5 ? "本地期望 MD5" : "期望值",
    expectedValue: displayValue(requirement.expected),
    actualLabel: isPid ? "EDR 实际 PID" : isMd5 ? "EDR 实际 MD5" : "实际值",
    actualValue: displayValue(requirement.actual),
  };
}

function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

function downloadJson(fileName: string, value: unknown) {
  downloadBlob(fileName, new Blob([JSON.stringify(value, null, 2)], { type: "application/json;charset=utf-8" }));
}

export function LiveControlPlane({ view = "overview" }: { view?: ControlPlaneView }) {
  const [apiState, setApiState] = useState<ApiState>("checking");
  const [apiVersion, setApiVersion] = useState("—");
  const [availableCapabilities, setAvailableCapabilities] = useState<ApiCapability[]>([]);
  const [baselines, setBaselines] = useState<ApiBaseline[]>([]);
  const [mappings, setMappings] = useState<ApiMapping[]>([]);
  const [mappingId, setMappingId] = useState("");
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [runName, setRunName] = useState("基础遥测验证 #001");
  const [environment, setEnvironment] = useState("Windows 11 · 实验室");
  const [nextDelay, setNextDelay] = useState(3);
  const [allowHighRisk, setAllowHighRisk] = useState(false);
  const [activeRun, setActiveRun] = useState<ApiRun | null>(null);
  const [recentRuns, setRecentRuns] = useState<ApiRun[]>([]);
  const [selectedRunId, setSelectedRunId] = useState("");
  const [cloudFile, setCloudFile] = useState<FileChoice>(emptyFile);
  const [manifestFile, setManifestFile] = useState<FileChoice>(emptyFile);
  const [localFile, setLocalFile] = useState<FileChoice>(emptyFile);
  const [comparison, setComparison] = useState<ValidationResult | null>(null);
  const [isComparing, setIsComparing] = useState(false);
  const [notice, setNotice] = useState("正在连接本地 Runner…");

  const availableIds = useMemo(() => new Set(availableCapabilities.map((item) => item.capability_id)), [availableCapabilities]);
  const selectedCapabilities = useMemo(() => allCapabilities.filter((item) => selectedIds.includes(item.id)), [selectedIds]);
  const hasHighRisk = selectedCapabilities.some((item) => item.risk === "L2" || item.risk === "L3");
  const selectedRisk = selectedCapabilities.reduce((maximum, item) => Number(item.risk.slice(1)) > Number(maximum.slice(1)) ? item.risk : maximum, "L0");
  const completedRuns = recentRuns.filter((run) => run.local_export_available);

  const refreshRuns = useCallback(async () => {
    const runs = await apiRequest<ApiRun[]>("/runs");
    setRecentRuns(runs);
    setSelectedRunId((current) => current || runs.find((run) => run.local_export_available)?.operation_id || "");
    return runs;
  }, []);

  const connect = useCallback(async () => {
    try {
      const [health, capabilities, baselineList, mappingList, runs] = await Promise.all([
        apiRequest<{ version: string }>("/health"),
        apiRequest<ApiCapability[]>("/capabilities"),
        apiRequest<ApiBaseline[]>("/baselines"),
        apiRequest<ApiMapping[]>("/mappings"),
        apiRequest<ApiRun[]>("/runs"),
      ]);
      setApiVersion(health.version);
      setAvailableCapabilities(capabilities);
      setBaselines(baselineList);
      setMappings(mappingList);
      setMappingId((current) => current || mappingList[0]?.profile_id || "");
      setRecentRuns(runs);
      setSelectedRunId((current) => current || runs.find((run) => run.local_export_available)?.operation_id || "");
      setSelectedIds((current) => current.length ? current.filter((id) => capabilities.some((item) => item.capability_id === id)) : capabilities.map((item) => item.capability_id));
      setApiState("online");
      setNotice(`本地 Runner 已连接，发现 ${capabilities.length} 项可执行样本。`);
    } catch (error) {
      setApiState("offline");
      setNotice(error instanceof Error ? `无法连接本地服务：${error.message}` : "无法连接本地服务。");
    }
  }, []);

  useEffect(() => {
    const timer = window.setTimeout(() => void connect(), 0);
    return () => window.clearTimeout(timer);
  }, [connect]);

  useEffect(() => {
    if (!activeRun || !isActive(activeRun.status)) return;
    const operationId = activeRun.operation_id;
    const timer = window.setInterval(() => {
      void apiRequest<ApiRun>(`/runs/${operationId}`).then((run) => {
        setActiveRun(run);
        if (!isActive(run.status)) {
          setNotice(`轮次已结束：${run.phase}`);
          void refreshRuns();
        }
      }).catch((error: unknown) => setNotice(error instanceof Error ? error.message : "刷新进度失败。"));
    }, 500);
    return () => window.clearInterval(timer);
  }, [activeRun, refreshRuns]);

  function toggleCapability(capabilityId: string) {
    if (!availableIds.has(capabilityId)) return;
    setSelectedIds((ids) => ids.includes(capabilityId) ? ids.filter((id) => id !== capabilityId) : [...ids, capabilityId]);
  }

  async function startRun() {
    if (!selectedIds.length) { setNotice("请至少选择一项已有样本的能力。"); return; }
    if (hasHighRisk && !allowHighRisk) { setNotice("所选能力包含高风险样本，请先确认隔离环境。"); return; }
    try {
      const run = await apiRequest<ApiRun>("/runs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          capability_ids: selectedIds,
          name: runName.trim() || "未命名验证轮次",
          environment_id: environment,
          allow_high_risk: allowHighRisk,
          inter_capability_delay_seconds: nextDelay,
        }),
      });
      setActiveRun(run);
      setNotice(`已提交 ${selectedIds.length} 项能力，Runner 将严格串行执行。`);
      void refreshRuns();
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "轮次创建失败。");
    }
  }

  async function cancelRun() {
    if (!activeRun || !isActive(activeRun.status)) return;
    try {
      setActiveRun(await apiRequest<ApiRun>(`/runs/${activeRun.operation_id}/cancel`, { method: "POST" }));
      setNotice("已请求取消，Runner 正在清理并保留已产生的证据。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "取消失败。");
    }
  }

  async function downloadLocalExport(run: ApiRun) {
    try {
      downloadBlob(`${run.run_id ?? run.operation_id}-local-run.json`, await apiDownload(`/runs/${run.operation_id}/local-export`));
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "下载本地结果失败。");
    }
  }

  function chooseFile(setter: (value: FileChoice) => void, event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (file) setter({ file, state: "ready", name: file.name, detail: `${formatBytes(file.size)} · 仅发送到本机比较服务` });
    event.target.value = "";
  }

  async function compare() {
    if (!cloudFile.file) { setNotice("请先选择从 EDR 平台导出的云端事件文件。"); return; }
    if (!localFile.file && !selectedRunId) { setNotice("请选择已完成轮次，或导入本地运行 JSON。"); return; }
    if (!mappingId) { setNotice("本地仓库没有可用的字段映射。"); return; }
    const form = new FormData();
    form.append("cloud_file", cloudFile.file);
    form.append("mapping_id", mappingId);
    if (manifestFile.file) form.append("cloud_manifest", manifestFile.file);
    if (localFile.file) form.append("local_file", localFile.file);
    else form.append("operation_id", selectedRunId);
    setIsComparing(true);
    try {
      const result = await apiRequest<ValidationResult>("/compare", { method: "POST", body: form });
      setComparison(result);
      setNotice(`比较完成：${result.summary.pass} 项通过，${result.summary.fail} 项失败。`);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "比较失败。");
    } finally {
      setIsComparing(false);
    }
  }

  async function downloadConclusion() {
    if (!comparison) return;
    try {
      downloadBlob(`validation-${comparison.comparison_id}-conclusion.md`, await apiDownload(`/reports/${comparison.comparison_id}/conclusion`));
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "下载中文结论失败。");
    }
  }

  return (
    <div className="app-shell">
      <Sidebar view={view} apiState={apiState} apiVersion={apiVersion} onReconnect={() => void connect()} />
      <main className="main-content">
        {view === "overview" && <Overview apiState={apiState} capabilities={availableCapabilities} baselines={baselines} recentRuns={recentRuns} />}
        {view === "test" && <TestWorkspace
          apiState={apiState} availableIds={availableIds} selectedIds={selectedIds} activeRun={activeRun} recentRuns={recentRuns}
          runName={runName} environment={environment} nextDelay={nextDelay} allowHighRisk={allowHighRisk} selectedRisk={selectedRisk} hasHighRisk={hasHighRisk}
          onRunName={setRunName} onEnvironment={setEnvironment} onNextDelay={setNextDelay} onAllowHighRisk={setAllowHighRisk}
          onToggle={toggleCapability} onSelectAll={() => setSelectedIds([...availableIds])} onClear={() => setSelectedIds([])}
          onStart={() => void startRun()} onCancel={() => void cancelRun()} onDownload={(run) => void downloadLocalExport(run)} onInspect={setActiveRun} onRefresh={() => void refreshRuns()}
        />}
        {view === "compare" && <CompareWorkspace
          apiState={apiState} baselines={baselines} mappings={mappings} mappingId={mappingId} onMappingId={setMappingId}
          completedRuns={completedRuns} selectedRunId={selectedRunId} onSelectedRunId={setSelectedRunId}
          localFile={localFile} cloudFile={cloudFile} manifestFile={manifestFile}
          onLocalFile={(event) => chooseFile(setLocalFile, event)} onCloudFile={(event) => chooseFile(setCloudFile, event)} onManifestFile={(event) => chooseFile(setManifestFile, event)}
          comparison={comparison} isComparing={isComparing} onCompare={() => void compare()}
          onDownloadJson={() => comparison && downloadJson(`validation-${comparison.comparison_id}.json`, comparison)} onDownloadConclusion={() => void downloadConclusion()}
        />}
        <footer className="footer-line"><span>EDR CAPABILITY VALIDATION</span><span>本地优先 · 串行执行 · 离线比较 · 证据可追溯</span></footer>
      </main>
      <div className="toast" role="status" aria-live="polite">{notice}</div>
    </div>
  );
}

function Sidebar({ view, apiState, apiVersion, onReconnect }: { view: ControlPlaneView; apiState: ApiState; apiVersion: string; onReconnect: () => void }) {
  return <aside className="sidebar" aria-label="主导航">
    <div className="brand-block"><div className="brand-mark" aria-hidden="true">E</div><div><p className="brand-name">EDR 验证平台</p><p className="brand-subtitle">LOCAL CONTROL PLANE · {apiVersion}</p></div></div>
    <nav className="side-nav">
      <Link className={`nav-item ${view === "overview" ? "active" : ""}`} href="/"><span>01</span>工作台</Link>
      <Link className={`nav-item ${view === "test" ? "active" : ""}`} href="/test"><span>02</span>进行测试</Link>
      <Link className={`nav-item ${view === "compare" ? "active" : ""}`} href="/compare"><span>03</span>离线比较</Link>
    </nav>
    <div className="sidebar-note">
      <div className="status-line"><span className={`status-dot ${apiState === "online" ? "green" : apiState === "checking" ? "amber" : "red"}`} />{apiState === "online" ? "本地 Runner 已连接" : apiState === "checking" ? "正在连接 Runner" : "Runner 未连接"}</div>
      <p>所有测试、数据库和导入日志都保存在本机，不直接连接 EDR 云端接口。</p>
      {apiState === "offline" && <button className="sidebar-button" type="button" onClick={onReconnect}>重新连接</button>}
    </div>
  </aside>;
}

function PageHeader({ index, eyebrow, title, description, apiState }: { index: string; eyebrow: string; title: string; description: string; apiState: ApiState }) {
  return <header className="topbar"><div><p className="eyebrow">{index} / {eyebrow}</p><h1>{title}</h1><p className="page-description">{description}</p></div><div className="topbar-actions"><span className="mode-chip"><span className={`status-dot ${apiState === "online" ? "green" : "red"}`} />{apiState === "online" ? "本地服务在线" : "本地服务离线"}</span></div></header>;
}

function Overview({ apiState, capabilities, baselines, recentRuns }: { apiState: ApiState; capabilities: ApiCapability[]; baselines: ApiBaseline[]; recentRuns: ApiRun[] }) {
  const latest = recentRuns[0];
  return <>
    <PageHeader index="01" eyebrow="工作台" title="EDR 能力验证" description="先在本机依次产生可验证行为，再导入 EDR 日志进行离线核对。两个步骤相互独立，结果都可追溯。" apiState={apiState} />
    <section className="metric-row" aria-label="平台摘要">
      <article className="metric-card"><p>可执行能力</p><strong>{capabilities.length}</strong><span>已发现完整程序包</span></article>
      <article className="metric-card"><p>检验基准</p><strong>{baselines.length}</strong><span>包含本地与云端要求</span></article>
      <article className="metric-card"><p>最近轮次</p><strong className="metric-text">{latest ? runStatusLabel(latest.status) : "暂无"}</strong><span>{latest ? latest.name : "等待首次测试"}</span></article>
      <article className="metric-card"><p>工作方式</p><strong className="metric-text">本地离线</strong><span>不保存 EDR 账号凭据</span></article>
    </section>
    <section className="journey-grid" aria-label="验证流程入口">
      <Link className="journey-card" href="/test"><span className="journey-number">步骤 1</span><h2>进行能力测试</h2><p>选择能力并严格串行运行，查看每一步进度、等待倒计时和控制程序日志。</p><strong>进入测试页面 <span aria-hidden="true">→</span></strong></Link>
      <Link className="journey-card" href="/compare"><span className="journey-number">步骤 2</span><h2>执行离线比较</h2><p>导入 EDR 平台日志，逐条查看 BASELINE 的要求、实际值以及是否满足。</p><strong>进入比较页面 <span aria-hidden="true">→</span></strong></Link>
    </section>
    <section className="panel simple-guide"><div className="panel-heading"><div><p className="section-index">简单说明</p><h2>怎样得到可信结论</h2></div></div><div className="guide-steps"><div><span>1</span><strong>本地行为确实发生</strong><p>Controller 会自验证行为和清理结果。</p></div><div><span>2</span><strong>导出正确时间范围</strong><p>EDR 日志需要覆盖同一主机和测试时间。</p></div><div><span>3</span><strong>逐项满足基准</strong><p>必需要求全部通过，能力才判定为通过。</p></div></div></section>
  </>;
}

type TestWorkspaceProps = {
  apiState: ApiState; availableIds: Set<string>; selectedIds: string[]; activeRun: ApiRun | null; recentRuns: ApiRun[];
  runName: string; environment: string; nextDelay: number; allowHighRisk: boolean; selectedRisk: string; hasHighRisk: boolean;
  onRunName: (value: string) => void; onEnvironment: (value: string) => void; onNextDelay: (value: number) => void; onAllowHighRisk: (value: boolean) => void;
  onToggle: (id: string) => void; onSelectAll: () => void; onClear: () => void; onStart: () => void; onCancel: () => void;
  onDownload: (run: ApiRun) => void; onInspect: (run: ApiRun) => void; onRefresh: () => void;
};

function TestWorkspace(props: TestWorkspaceProps) {
  const { activeRun } = props;
  return <>
    <PageHeader index="02" eyebrow="进行测试" title="串行能力测试" description="每次只运行一项能力。当前能力结束后，按设定时间等待，再开始下一项，减少行为互相干扰。" apiState={props.apiState} />
    <section className="metric-row test-metrics" aria-label="测试摘要">
      <article className="metric-card"><p>本轮已选</p><strong>{props.selectedIds.length}</strong><span>最高风险 {props.selectedRisk}</span></article>
      <article className="metric-card"><p>整体进度</p><strong>{activeRun?.progress ?? 0}%</strong><span>{activeRun?.phase ?? "尚未开始"}</span></article>
      <article className="metric-card"><p>已完成</p><strong>{activeRun?.completed_capabilities ?? 0}</strong><span>共 {activeRun?.capability_ids.length ?? props.selectedIds.length} 项</span></article>
      <article className="metric-card"><p>下一项等待</p><strong>{activeRun?.wait_remaining_seconds ?? props.nextDelay}s</strong><span>{activeRun?.wait_remaining_seconds ? "正在倒计时" : "默认间隔"}</span></article>
    </section>
    <div className="workspace-grid test-setup-grid">
      <section className="panel capability-panel">
        <div className="panel-heading"><div><p className="section-index">A / 选择能力</p><h2>本轮测试内容</h2><p className="panel-description">灰色项目还没有可执行程序包；勾选顺序就是实际执行顺序。</p></div><div className="inline-actions"><button type="button" className="text-button" onClick={props.onSelectAll}>全选可用</button><button type="button" className="text-button" onClick={props.onClear}>清空</button></div></div>
        <div className="capability-groups compact-catalog">
          {capabilityCatalog.map((category) => <fieldset className="capability-group" key={category.id}><legend><span className="category-name-zh">{category.nameZh}</span><span className="category-name-en">{category.nameEn}</span></legend><div className="capability-list">{category.capabilities.map((capability) => {
            const selected = props.selectedIds.includes(capability.id); const available = props.availableIds.has(capability.id);
            return <label className={`capability-item ${selected ? "selected" : ""} ${available ? "available" : "unavailable"}`} key={capability.id} aria-disabled={!available}><input type="checkbox" checked={selected} disabled={!available} onChange={() => props.onToggle(capability.id)} /><span className="checkbox-ui" aria-hidden="true" /><span className="capability-copy"><span className="capability-title-row"><strong><span className="capability-name-zh">{capability.nameZh}</span><span className="capability-name-en">{capability.nameEn}</span></strong><span className={`risk-badge ${capability.risk.toLowerCase()}`}>{capability.risk}</span></span><span className="program-line">{available ? capability.programs : "样本待实现"}</span></span></label>;
          })}</div></fieldset>)}
        </div>
      </section>
      <section className="panel run-panel sticky-panel">
        <div className="panel-heading"><div><p className="section-index">B / 运行设置</p><h2>启动本轮测试</h2></div><span className="line-badge">严格串行</span></div>
        <div className="form-stack">
          <label className="field-label">轮次名称<input value={props.runName} onChange={(event) => props.onRunName(event.target.value)} /></label>
          <label className="field-label">测试环境<select value={props.environment} onChange={(event) => props.onEnvironment(event.target.value)}><option>Windows 11 · 实验室</option><option>Windows Server 2022 · 实验室</option></select></label>
          <label className="field-label">下一项能力前等待（秒）<input type="number" min="0" max="300" value={props.nextDelay} onChange={(event) => props.onNextDelay(Math.min(300, Math.max(0, Number(event.target.value) || 0)))} /><span className="field-help">默认 3 秒；设置为 0 可取消等待。</span></label>
          <div className="serial-note"><strong>执行规则</strong><p>前一项完成并写入 SQLite 后，才会开始等待倒计时；倒计时结束后再启动下一项。</p></div>
          {props.hasHighRisk && <label className="risk-confirm"><input type="checkbox" checked={props.allowHighRisk} onChange={(event) => props.onAllowHighRisk(event.target.checked)} /><span>我确认在隔离测试机执行 L2/L3 高风险样本</span></label>}
          <button className="primary-button" type="button" onClick={props.onStart} disabled={props.apiState !== "online" || Boolean(activeRun && isActive(activeRun.status))}>{activeRun && isActive(activeRun.status) ? "测试执行中" : "启动本轮测试"}<span aria-hidden="true">→</span></button>
        </div>
      </section>
    </div>
    <RunProgressPanel run={activeRun} onCancel={props.onCancel} onDownload={props.onDownload} />
    <RunLogPanel key={activeRun?.operation_id ?? "empty-run"} run={activeRun} />
    <RunHistory runs={props.recentRuns} onInspect={props.onInspect} onDownload={props.onDownload} onRefresh={props.onRefresh} />
  </>;
}

function RunProgressPanel({ run, onCancel, onDownload }: { run: ApiRun | null; onCancel: () => void; onDownload: (run: ApiRun) => void }) {
  return <section className="panel progress-panel"><div className="panel-heading"><div><p className="section-index">C / 测试进度</p><h2>{run ? run.phase : "等待测试开始"}</h2></div>{run && <span className="line-badge">{run.progress}%</span>}</div>
    {run ? <><div className="progress-track large"><span style={{ width: `${run.progress}%` }} /></div><div className="run-progress-meta"><span>已完成 {run.completed_capabilities}/{run.capability_ids.length}</span><span>{run.wait_remaining_seconds ? `下一项将在 ${run.wait_remaining_seconds} 秒后开始` : "能力严格串行执行"}</span><span>{run.database_name ?? "正在创建独立 SQLite"}</span></div>
      <ol className="step-timeline">{run.steps.map((step) => <li className={step.status} key={step.capability_id}><span className="step-marker">{step.sequence}</span><div><strong>{step.name_zh}</strong><span>{step.status_label}</span></div>{run.current_capability_id === step.capability_id && <em>{run.wait_remaining_seconds ? "等待中" : "当前"}</em>}</li>)}</ol>
      <div className="run-actions">{isActive(run.status) && <button className="danger-button" type="button" onClick={onCancel}>取消并清理</button>}{run.local_export_available && <button className="secondary-button" type="button" onClick={() => onDownload(run)}>下载本地结果</button>}</div>{run.error && <p className="api-error">{run.error}</p>}</>
      : <div className="empty-state"><span className="empty-glyph" aria-hidden="true">＋</span><p>启动轮次后，这里会逐项显示等待、执行和完成状态</p><span>进度不会再从 15% 直接跳到 100%</span></div>}
  </section>;
}

function RunLogPanel({ run }: { run: ApiRun | null }) {
  const [selectedCapabilityId, setSelectedCapabilityId] = useState<string | null>(null);
  const completedSteps = run?.steps.filter((step) => step.status !== "pending" && step.status !== "running") ?? [];
  const selectedStep = completedSteps.find((step) => step.capability_id === selectedCapabilityId);
  const selectedLogs = selectedStep ? run?.logs.filter((log) => log.capability_id === selectedStep.capability_id) ?? [] : [];

  useEffect(() => {
    if (!selectedStep) return;
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === "Escape") setSelectedCapabilityId(null); };
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [selectedStep]);

  return <><section className="log-workspace"><article className="panel highlight-panel"><div className="panel-heading"><div><p className="section-index">D / 重点日志</p><h2>需要关注的事件</h2><p className="panel-description">只保留开始、完成、跳过、警告和错误，便于快速判断。</p></div></div><div className="highlight-list">{run?.highlights.length ? run.highlights.map((log, index) => <div className={`highlight-item ${log.level}`} key={`${log.timestamp_utc}-${index}`}><span>{formatTime(log.timestamp_utc)}</span><strong>{log.message}</strong></div>) : <div className="table-empty">暂时没有重点日志。</div>}</div></article>
    <article className="panel completed-queue-panel"><div className="panel-heading"><div><p className="section-index">E / 已完成队列</p><h2>按能力查看详细日志</h2><p className="panel-description">能力完成后进入队列；点击具体能力查看其 Runner 与 Controller 输出。</p></div><span className="line-badge">{completedSteps.length} 项</span></div><div className="completed-queue-list">{completedSteps.length ? completedSteps.map((step) => {
      const logCount = run?.logs.filter((log) => log.capability_id === step.capability_id).length ?? 0;
      return <button className={`completed-queue-item ${step.status}`} type="button" key={step.capability_id} onClick={() => setSelectedCapabilityId(step.capability_id)}><span className="queue-sequence">{step.sequence}</span><span className="queue-copy"><strong>{step.name_zh}</strong><code>{step.capability_id}</code></span><span className="queue-meta"><em>{step.status_label}</em><small>{logCount} 条日志</small></span><span className="queue-open" aria-hidden="true">查看 →</span></button>;
    }) : <div className="table-empty">能力完成后会按执行顺序显示在这里。</div>}</div></article></section>
    {selectedStep && <div className="log-modal-backdrop" onClick={() => setSelectedCapabilityId(null)}><section className="log-modal" role="dialog" aria-modal="true" aria-labelledby="capability-log-title" onClick={(event) => event.stopPropagation()}><header className="log-modal-header"><div><p className="section-index">能力详细日志</p><h2 id="capability-log-title">{selectedStep.name_zh}</h2><code>{selectedStep.capability_id}</code></div><div><span className={`queue-status ${selectedStep.status}`}>{selectedStep.status_label}</span><button className="modal-close" type="button" autoFocus aria-label="关闭能力详细日志" onClick={() => setSelectedCapabilityId(null)}>×</button></div></header><div className="log-console" role="log">{selectedLogs.length ? selectedLogs.map((log, index) => <div className={`log-line ${log.level}`} key={`${log.timestamp_utc}-${index}`}><time>{formatTime(log.timestamp_utc)}</time><span>{log.level.toUpperCase()}</span><code>{log.source}</code><p>{log.message}</p></div>) : <div className="log-empty">该能力没有可用的详细输出。</div>}</div></section></div>}
  </>;
}

function RunHistory({ runs, onInspect, onDownload, onRefresh }: { runs: ApiRun[]; onInspect: (run: ApiRun) => void; onDownload: (run: ApiRun) => void; onRefresh: () => void }) {
  return <section className="panel runs-panel"><div className="panel-heading"><div><p className="section-index">F / 运行记录</p><h2>本机最近轮次</h2></div><button className="text-button" type="button" onClick={onRefresh}>刷新</button></div>{runs.length ? <div className="table-wrap"><table><thead><tr><th>轮次</th><th>能力</th><th>开始时间</th><th>本地状态</th><th>操作</th></tr></thead><tbody>{runs.map((run) => <tr key={run.operation_id}><td><strong>{run.name}</strong><span className="table-id">{run.run_id ?? run.operation_id}</span></td><td>{run.capability_ids.length} 项</td><td>{formatTime(run.started_at_utc)}</td><td><span className="table-status"><span className={`status-dot ${statusDot(run.status)}`} />{runStatusLabel(run.status)}</span></td><td><div className="table-actions"><button className="text-button" type="button" onClick={() => onInspect(run)}>查看</button>{run.local_export_available && <button className="text-button" type="button" onClick={() => onDownload(run)}>下载</button>}</div></td></tr>)}</tbody></table></div> : <div className="table-empty">本机还没有测试轮次。</div>}</section>;
}

type CompareWorkspaceProps = {
  apiState: ApiState; baselines: ApiBaseline[]; mappings: ApiMapping[]; mappingId: string; onMappingId: (value: string) => void;
  completedRuns: ApiRun[]; selectedRunId: string; onSelectedRunId: (value: string) => void;
  localFile: FileChoice; cloudFile: FileChoice; manifestFile: FileChoice;
  onLocalFile: (event: ChangeEvent<HTMLInputElement>) => void; onCloudFile: (event: ChangeEvent<HTMLInputElement>) => void; onManifestFile: (event: ChangeEvent<HTMLInputElement>) => void;
  comparison: ValidationResult | null; isComparing: boolean; onCompare: () => void; onDownloadJson: () => void; onDownloadConclusion: () => void;
};

function CompareWorkspace(props: CompareWorkspaceProps) {
  return <>
    <PageHeader index="03" eyebrow="离线比较" title="核对 EDR 日志" description="选择本地运行结果和 EDR 导出日志。系统会按 BASELINE 逐条说明要求是什么、是否满足，以及依据是什么。" apiState={props.apiState} />
    <section className="panel compare-input-panel"><div className="panel-heading compare-heading"><div><p className="section-index">A / 准备输入</p><h2>选择本地结果与 EDR 日志</h2><p className="panel-description">文件只会发送到 127.0.0.1 的本地比较服务。</p></div><button className="primary-button compact" type="button" onClick={props.onCompare} disabled={props.isComparing || props.apiState !== "online"}>{props.isComparing ? "正在逐项核对" : "开始离线比较"}<span aria-hidden="true">→</span></button></div>
      <div className="compare-select-grid"><label className="field-label">使用已完成轮次<select value={props.selectedRunId} onChange={(event) => props.onSelectedRunId(event.target.value)} disabled={Boolean(props.localFile.file)}><option value="">请选择本机轮次</option>{props.completedRuns.map((run) => <option value={run.operation_id} key={run.operation_id}>{run.name} · {formatTime(run.started_at_utc)}</option>)}</select><span className="field-help">导入本地 JSON 后，此选择会被替代。</span></label><label className="field-label">EDR 字段映射<select value={props.mappingId} onChange={(event) => props.onMappingId(event.target.value)}>{props.mappings.map((mapping) => <option value={mapping.profile_id} key={mapping.profile_id}>{mapping.vendor} {mapping.product} · {mapping.profile_id}</option>)}</select><span className="field-help">负责把厂商字段转换成统一字段。</span></label></div>
      <div className="upload-grid"><FileSlot id="local-file" step="1" title="本地运行 JSON" hint="可选：用于比较其他机器或历史轮次" choice={props.localFile} onChange={props.onLocalFile} /><FileSlot id="cloud-file" step="2" title="EDR 云端事件" hint="必需：支持 JSON 数组或 JSONL" choice={props.cloudFile} required onChange={props.onCloudFile} /><FileSlot id="manifest-file" step="3" title="云端导出清单" hint="建议：证明主机与时间范围完整" choice={props.manifestFile} onChange={props.onManifestFile} /></div>
    </section>
    <BaselineGuide baselines={props.baselines} />
    <ComparisonResultPanel result={props.comparison} onDownloadJson={props.onDownloadJson} onDownloadConclusion={props.onDownloadConclusion} />
  </>;
}

function BaselineGuide({ baselines }: { baselines: ApiBaseline[] }) {
  return <section className="panel baseline-guide"><div className="panel-heading"><div><p className="section-index">B / BASELINE 是什么</p><h2>通过判定的要求</h2><p className="panel-description">“本地要求”证明测试行为真的发生；“EDR 要求”检查云端事件是否记录了正确内容。必需要求全部满足才算通过。</p></div><span className="line-badge">{baselines.length} 份基准</span></div><div className="baseline-card-grid">{baselines.map((baseline) => {
    const localCount = baseline.requirements.filter((item) => item.scope === "local").length; const cloudCount = baseline.requirements.length - localCount;
    return <details className="baseline-card" key={baseline.baseline_id}><summary><div><strong>{baseline.title}</strong><span>{localCount} 项本地要求 · {cloudCount} 项 EDR 要求</span></div><em>{baseline.risk_level}</em></summary><div className="baseline-preview">{baseline.requirements.map((requirement) => <div key={requirement.requirement_id}><span className={`scope-chip ${requirement.scope}`}>{requirement.scope === "local" ? "本地" : "EDR"}</span><p>{requirement.title_zh}</p><em>{severityLabel(requirement.severity)}</em></div>)}</div></details>;
  })}</div></section>;
}

function ComparisonResultPanel({ result, onDownloadJson, onDownloadConclusion }: { result: ValidationResult | null; onDownloadJson: () => void; onDownloadConclusion: () => void }) {
  return <section className="panel comparison-detail-panel"><div className="panel-heading"><div><p className="section-index">C / 比较结果</p><h2>要求满足情况</h2><p className="panel-description">结果先按能力折叠；展开后查看每项要求的状态、校验条件和实际观测。</p></div></div>{result ? <>
    <div className={`conclusion-card ${result.conclusion.verdict.toLowerCase()}`}><div><span>总体结论</span><strong>{result.conclusion.label_zh}</strong><em>{result.conclusion.pass_rate === null ? "通过率不可计算" : `完整通过率 ${(result.conclusion.pass_rate * 100).toFixed(1)}%`}</em></div><p>{result.conclusion.statement_zh}</p></div>
    <div className="result-summary"><div><span>通过</span><strong className="pass-text">{result.summary.pass}</strong></div><div><span>部分通过</span><strong>{result.summary.partial}</strong></div><div><span>失败</span><strong className="fail-text">{result.summary.fail}</strong></div><div><span>无法判定</span><strong>{result.summary.inconclusive}</strong></div></div>
    <div className="capability-result-stack">{result.capabilities.map((entry) => <CapabilityComparison key={entry.case_run_id} entry={entry} />)}</div>
    <div className="result-actions"><button className="secondary-button" type="button" onClick={onDownloadJson}>下载完整 JSON</button><button className="secondary-button" type="button" onClick={onDownloadConclusion}>下载中文结论</button></div>
  </> : <div className="result-placeholder"><span className="bracket" aria-hidden="true">[ ]</span><div><strong>等待离线比较</strong><p>比较完成后，这里会用简单中文列出每项 BASELINE 要求及满足情况。</p></div></div>}</section>;
}

function CapabilityComparison({ entry }: { entry: ValidationEntry }) {
  const requirements = entry.baseline_requirements ?? [];
  const passed = requirements.filter((item) => item.status === "passed").length;
  return <details className="capability-result-card"><summary><div className="capability-summary-main"><span className={`result-status ${entry.validation_status.toLowerCase()}`}>{validationStatusLabel(entry.validation_status)}</span><div><h3>{entry.display_name_zh ?? entry.capability_id}</h3><p>{entry.baseline_title ?? entry.baseline_id ?? "没有匹配的检验基准"}</p></div></div><div className="requirement-count"><strong>{passed}/{requirements.length}</strong><span>要求已满足</span></div></summary><div className="capability-result-body"><div className="evidence-strip"><span>{coverageLabel(entry.export_coverage)}</span><span>{entry.candidate_count} 条候选事件</span><span>本地状态 {entry.local_status}</span></div>{entry.warnings?.length ? <div className="plain-warning"><strong>需要注意</strong><p>{entry.warnings.join("；")}</p></div> : null}<div className="requirement-table"><div className="requirement-head"><span>来源</span><span>要求</span><span>结果</span><span>依据</span></div>{requirements.map((requirement) => <RequirementRow key={requirement.requirement_id} requirement={requirement} />)}</div></div></details>;
}

function RequirementRow({ requirement }: { requirement: BaselineRequirementResult }) {
  const evidence = requirementEvidence(requirement);
  return <div className={`requirement-row ${requirement.status}`}><span><i className={`scope-chip ${requirement.scope}`}>{requirement.scope === "local" ? "本地" : "EDR"}</i><em>{severityLabel(requirement.severity)}</em></span><div><strong>{requirement.title_zh}</strong><code>{requirement.field}</code></div><span className={`requirement-status ${requirement.status}`}>{requirementStatusLabel(requirement.status)}</span><div className="requirement-values"><p><b>{evidence.expectedLabel}：</b>{evidence.expectedValue}</p><p><b>{evidence.actualLabel}：</b>{evidence.actualValue}</p>{requirement.message && <p className="requirement-message">{requirement.message}</p>}</div></div>;
}

function FileSlot({ id, step, title, hint, choice, required = false, onChange }: { id: string; step: string; title: string; hint: string; choice: FileChoice; required?: boolean; onChange: (event: ChangeEvent<HTMLInputElement>) => void }) {
  return <label className={`file-slot ${choice.state}`} htmlFor={id}><input id={id} type="file" accept=".json,.jsonl,application/json" onChange={onChange} /><span className="file-step">{step}</span><span className="file-copy"><span className="file-title-row"><strong>{title}</strong><em>{required ? "必需" : "可选"}</em></span><span className="file-hint">{hint}</span><span className="file-name">{choice.name}</span><span className="file-summary">{choice.detail}</span></span><span className="file-action">选择文件</span></label>;
}
