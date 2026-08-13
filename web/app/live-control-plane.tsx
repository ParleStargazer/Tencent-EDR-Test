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
  capability_version: string;
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
  local_evidence?: ApiLocalEvidence;
};

type ApiLocalEvidence = {
  capability: {
    case_run_id: string;
    capability_id: string;
    status: string;
    started_at_utc?: string;
    ended_at_utc?: string;
    duration_ms?: number;
    observer_started_at_utc?: string;
    observer_ended_at_utc?: string;
  };
  programs: Array<{
    role: string;
    instance_index: number;
    executable: string;
    pid: number;
    parent_pid: number;
    command_line: string;
    started_at_utc: string;
    ended_at_utc?: string;
    exit_code?: number;
    md5?: string;
    sha1?: string;
    sha256: string;
  }>;
  facts: Array<{ field: string; value: unknown; observed_at_utc: string; source: string; confidence: string }>;
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

type LocalBaselineMatch = {
  requirement_id: string;
  status: RequirementStatus;
  field: string;
  json_pointer?: string;
  expected?: unknown;
  actual?: unknown;
};

type CandidateBaselineMatch = {
  kind: "correlation" | "assertion" | "custom_filter";
  requirement_id: string;
  status: RequirementStatus;
  local_field?: string;
  local_json_pointer?: string;
  canonical_field: string;
  raw_field?: string;
  raw_json_pointer?: string;
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
  local_export_block: Record<string, unknown>;
  local_baseline_matches?: LocalBaselineMatch[];
  edr_candidates?: EdrCandidate[];
  baseline_requirements?: BaselineRequirementResult[];
  method_selection?: MethodSelectionResult | null;
  method_results?: MethodResult[];
  stage_flow?: StageFlowResult | null;
  stage_results?: StageResult[];
  warnings?: string[];
};

type StageFlowResult = {
  strategy: "ordered_all";
  status: Exclude<ValidationStatus, "NOT_COMPARED">;
  stage_count: number;
  notice: string;
};

type StageResult = {
  kind: "event" | "relationship";
  sequence: number;
  title: string;
  expectation_id: string;
  depends_on?: string;
  status: Exclude<ValidationStatus, "NOT_COMPARED">;
  candidate_count: number;
  qualified_candidate_count: number;
  related_expectation_ids: string[];
  relationship?: {
    type: "same_process_continuity";
    left_expectation_id: string;
    right_expectation_id: string;
    same_process_pid: boolean | null;
    same_process_executable: boolean | null;
    ordered: boolean | null;
    local_interval_ms: number | null;
    cloud_interval_ms: number | null;
    interval_difference_ms: number | null;
    maximum_interval_difference_ms: number;
  } | null;
  order_status?: "passed" | "failed" | "not_evaluated" | null;
  order_message?: string | null;
  warnings: string[];
};

type MethodSelectionResult = {
  strategy: "best";
  selected_method_id: string;
  selected_method_title: string;
  selected_method_status: Exclude<ValidationStatus, "NOT_COMPARED">;
  notice: string;
};

type MethodResult = {
  method_id: string;
  method_title: string;
  expectation_id: string;
  status: Exclude<ValidationStatus, "NOT_COMPARED">;
  selected_for_conclusion: boolean;
  candidate_count: number;
  qualified_candidate_count: number;
  passed_requirement_count: number;
  requirement_count: number;
  warnings: string[];
};

type EdrCandidate = {
  expectation_id: string;
  rank: number;
  confidence: "high" | "medium" | "low";
  correlation_score: number;
  anchor_qualified: boolean;
  eligible_for_validation: boolean;
  qualification_reason: string;
  event_type_hint_matched: boolean;
  event_action_hint_matched: boolean;
  custom_action_name_expected: string[];
  custom_action_name_actual?: string;
  custom_action_name_matched: boolean | null;
  custom_child_file_create_op_name_expected: string[];
  custom_child_file_create_op_name_actual?: string;
  custom_child_file_create_op_name_matched: boolean | null;
  maximum_time_difference_ms: number;
  time_difference_matched: boolean;
  time_distance_ms: number;
  time_offset_ms: number | null;
  local_event_time_utc: string;
  event_time_utc?: string;
  raw_ref: string;
  event_id?: string;
  matched_anchors: string[];
  baseline_matches?: CandidateBaselineMatch[];
  canonical_event: Record<string, unknown>;
  raw_event: Record<string, unknown>;
};

type ValidationResult = {
  comparison_id: string;
  compared_at_utc: string;
  inputs?: { strong_correlation_time_ms?: number; candidate_time_limit_ms?: number };
  summary: { pass: number; partial: number; fail: number; inconclusive: number; not_compared: number };
  conclusion: {
    verdict: Exclude<ValidationStatus, "NOT_COMPARED">;
    label_zh: string;
    statement_zh: string;
    pass_rate: number | null;
  };
  capabilities: ValidationEntry[];
};

type ComparisonProgress = {
  comparison_id: string;
  status: "preparing" | "running" | "completed" | "failed";
  completed_capabilities: number;
  total_capabilities: number;
  progress: number;
  capability_id?: string;
  display_name_zh?: string;
  validation_status?: ValidationStatus;
  updated_at_utc?: string;
  error?: string;
};

type FileChoice = { file: File | null; state: "empty" | "ready"; name: string; detail: string };

const emptyFile: FileChoice = { file: null, state: "empty", name: "尚未选择文件", detail: "等待导入" };
const actionNameStorageKey = "edrtest.actionNameStandards.v1";
const childFileCreateOpNameStorageKey = "edrtest.childFileCreateOpNameStandards.v1";
const comparisonTimeStorageKey = "edrtest.comparisonTimeSettings.v1";
const defaultStrongCorrelationTimeMs = 15;
const defaultCandidateTimeLimitMs = 1000;
const defaultActionNameInputs: Record<string, string> = {
  "win.file.rename": "FileRename",
  "win.process.create": "ProcessCreate",
  "win.process.remote_thread": "RemoteThread",
  "win.process.access": "NtOpenProcess",
  "win.process.tampering": "WriteProcessMemory",
  "win.account.local.create": "AccountCreate",
  "win.account.login": "LoginSuccess, LoginFailed, LoginExplicitCredentials",
  "win.file.create": "FileWriteClose",
  "win.file.modify": "FileWriteClose",
  "win.file.open": "FileWriteClose",
  "win.network.tcp": "NetBind",
  "win.network.udp": "NetBind",
  "win.network.url": "NetBind",
  "win.network.dns": "NetBind",
  "win.network.file_download": "NetBind, FileWriteClose",
};
const defaultChildFileCreateOpNameInputs: Record<string, string> = {
  "win.file.create": "新建文件",
  "win.file.modify": "覆盖写文件",
  "win.file.open": "打开文件",
};
const fileCapabilityIds = new Set(["win.file.create", "win.file.open", "win.file.delete", "win.file.modify", "win.file.rename"]);
const allCapabilities = capabilityCatalog.flatMap((category) => category.capabilities.map((capability) => ({ ...capability, categoryId: category.id })));

type CapabilityItemGroup<T> = {
  id: string;
  nameZh: string;
  nameEn: string;
  items: T[];
};

function groupByCapabilityCategory<T extends { capability_id: string }>(items: T[]): CapabilityItemGroup<T>[] {
  const knownIds = new Set(capabilityCatalog.flatMap((category) => category.capabilities.map((capability) => capability.id)));
  const groups = capabilityCatalog.map((category) => ({
    id: category.id,
    nameZh: category.nameZh,
    nameEn: category.nameEn,
    items: category.capabilities.flatMap((capability) => items.filter((item) => item.capability_id === capability.id)),
  })).filter((category) => category.items.length > 0);
  const uncategorized = items.filter((item) => !knownIds.has(item.capability_id));
  return uncategorized.length > 0
    ? [...groups, { id: "uncategorized", nameZh: "未分类能力", nameEn: "Uncategorized", items: uncategorized }]
    : groups;
}
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

async function pollComparisonProgress(comparisonId: string, shouldContinue: () => boolean, onProgress: (progress: ComparisonProgress) => void): Promise<void> {
  while (shouldContinue()) {
    try {
      const progress = await apiRequest<ComparisonProgress>(`/comparisons/${comparisonId}/progress`);
      onProgress(progress);
      if (progress.status !== "running") return;
    } catch {
      // 比较请求完成输入校验前，进度状态可能尚未创建，下一轮继续查询。
    }
    await new Promise((resolve) => window.setTimeout(resolve, 100));
  }
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

function formatDurationMs(value?: number): string {
  if (value === null || value === undefined) return "—";
  if (value < 1000) return `${value} ms`;
  if (value < 60_000) return `${(value / 1000).toFixed(2)} 秒`;
  return `${Math.floor(value / 60_000)} 分 ${Math.round((value % 60_000) / 1000)} 秒`;
}

function formatSignedTimeOffset(value: number | null): string {
  if (value === null || !Number.isFinite(value)) return "方向未知";
  if (value > 0) return `+${formatDurationMs(value)}（EDR 延后）`;
  if (value < 0) return `-${formatDurationMs(Math.abs(value))}（EDR 提前）`;
  return "0 ms（时间一致）";
}

function roleLabel(role: string): string {
  return { controller: "编排程序", actor: "行为执行程序", target: "被测目标", helper: "辅助程序" }[role] ?? role;
}

function confidenceLabel(value: EdrCandidate["confidence"]): string {
  return { high: "高置信度", medium: "中置信度", low: "低置信度" }[value];
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
  return typeof value === "string" ? value : (JSON.stringify(value) ?? String(value));
}

function parseFilterValues(value: string): string[] {
  const seen = new Set<string>();
  return value.split(/[\r\n,，;；]+/).map((item) => item.trim()).filter((item) => {
    const normalized = item.toLocaleLowerCase();
    if (!item || seen.has(normalized)) return false;
    seen.add(normalized);
    return true;
  });
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
  const [actionNameInputs, setActionNameInputs] = useState<Record<string, string>>(defaultActionNameInputs);
  const [childFileCreateOpNameInputs, setChildFileCreateOpNameInputs] = useState<Record<string, string>>(defaultChildFileCreateOpNameInputs);
  const [strongCorrelationTimeMs, setStrongCorrelationTimeMs] = useState(defaultStrongCorrelationTimeMs);
  const [candidateTimeLimitMs, setCandidateTimeLimitMs] = useState(defaultCandidateTimeLimitMs);
  const [comparison, setComparison] = useState<ValidationResult | null>(null);
  const [comparisonProgress, setComparisonProgress] = useState<ComparisonProgress | null>(null);
  const [isComparing, setIsComparing] = useState(false);
  const [notice, setNotice] = useState("正在连接本地 Runner…");

  const availableIds = useMemo(() => new Set(availableCapabilities.map((item) => item.capability_id)), [availableCapabilities]);
  const administratorRequiredIds = useMemo(() => new Set(availableCapabilities
    .filter((item) => item.required_privilege === "administrator")
    .map((item) => item.capability_id)), [availableCapabilities]);
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
    const timer = window.setTimeout(() => {
      const loadStandards = (storageKey: string, defaults: Record<string, string>) => {
        const serialized = window.localStorage.getItem(storageKey);
        if (serialized === null) return { ...defaults };
        try {
          const saved = JSON.parse(serialized) as Record<string, unknown>;
          const values = Object.fromEntries(Object.entries(saved).filter((entry): entry is [string, string] => typeof entry[1] === "string"));
          return { ...defaults, ...values };
        } catch {
          window.localStorage.removeItem(storageKey);
          return { ...defaults };
        }
      };
      setActionNameInputs(loadStandards(actionNameStorageKey, defaultActionNameInputs));
      setChildFileCreateOpNameInputs(loadStandards(childFileCreateOpNameStorageKey, defaultChildFileCreateOpNameInputs));
      const serializedTimeSettings = window.localStorage.getItem(comparisonTimeStorageKey);
      if (serializedTimeSettings !== null) {
        try {
          const saved = JSON.parse(serializedTimeSettings) as { strongCorrelationTimeMs?: unknown; candidateTimeLimitMs?: unknown };
          if (typeof saved.strongCorrelationTimeMs === "number" && saved.strongCorrelationTimeMs >= 1 && saved.strongCorrelationTimeMs <= 60_000) {
            setStrongCorrelationTimeMs(saved.strongCorrelationTimeMs);
          }
          if (typeof saved.candidateTimeLimitMs === "number" && saved.candidateTimeLimitMs >= 1 && saved.candidateTimeLimitMs <= 300_000) {
            setCandidateTimeLimitMs(saved.candidateTimeLimitMs);
          }
        } catch {
          window.localStorage.removeItem(comparisonTimeStorageKey);
        }
      }
    }, 0);
    return () => window.clearTimeout(timer);
  }, []);

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

  function saveEdrFilterStandards() {
    const savedActions = Object.fromEntries(Object.entries(actionNameInputs).map(([capabilityId, value]) => [capabilityId, value.trim()]));
    const savedFileOperations = Object.fromEntries(Object.entries(childFileCreateOpNameInputs).map(([capabilityId, value]) => [capabilityId, value.trim()]));
    window.localStorage.setItem(actionNameStorageKey, JSON.stringify(savedActions));
    window.localStorage.setItem(childFileCreateOpNameStorageKey, JSON.stringify(savedFileOperations));
    setActionNameInputs(savedActions);
    setChildFileCreateOpNameInputs(savedFileOperations);
    const configured = [...Object.values(savedActions), ...Object.values(savedFileOperations)].filter((value) => parseFilterValues(value).length > 0).length;
    setNotice(`已在本机保存 ${configured} 项 EDR 原始字段筛选标准。`);
  }

  function clearEdrFilterStandards() {
    const clearedActions = Object.fromEntries(Object.keys({ ...defaultActionNameInputs, ...actionNameInputs }).map((capabilityId) => [capabilityId, ""]));
    const clearedFileOperations = Object.fromEntries(Object.keys({ ...defaultChildFileCreateOpNameInputs, ...childFileCreateOpNameInputs }).map((capabilityId) => [capabilityId, ""]));
    window.localStorage.setItem(actionNameStorageKey, JSON.stringify(clearedActions));
    window.localStorage.setItem(childFileCreateOpNameStorageKey, JSON.stringify(clearedFileOperations));
    setActionNameInputs(clearedActions);
    setChildFileCreateOpNameInputs(clearedFileOperations);
    setNotice("已清空两类 EDR 原始字段筛选；留空能力将沿用原关联规则。");
  }

  function saveComparisonTimeSettings() {
    if (!validComparisonTimeSettings()) return;
    window.localStorage.setItem(comparisonTimeStorageKey, JSON.stringify({ strongCorrelationTimeMs, candidateTimeLimitMs }));
    setNotice(`已保存时间参数：强关联 ${strongCorrelationTimeMs} ms，候选上限 ${candidateTimeLimitMs} ms。`);
  }

  function resetComparisonTimeSettings() {
    setStrongCorrelationTimeMs(defaultStrongCorrelationTimeMs);
    setCandidateTimeLimitMs(defaultCandidateTimeLimitMs);
    window.localStorage.setItem(comparisonTimeStorageKey, JSON.stringify({
      strongCorrelationTimeMs: defaultStrongCorrelationTimeMs,
      candidateTimeLimitMs: defaultCandidateTimeLimitMs,
    }));
    setNotice("已恢复默认时间参数：强关联 15 ms，候选上限 1000 ms。");
  }

  function validComparisonTimeSettings(): boolean {
    if (!Number.isInteger(strongCorrelationTimeMs) || strongCorrelationTimeMs < 1 || strongCorrelationTimeMs > 60_000) {
      setNotice("强关联时间必须是 1–60000 ms 的整数。");
      return false;
    }
    if (!Number.isInteger(candidateTimeLimitMs) || candidateTimeLimitMs < 1 || candidateTimeLimitMs > 300_000) {
      setNotice("无关联候选事件时间上限必须是 1–300000 ms 的整数。");
      return false;
    }
    if (candidateTimeLimitMs < strongCorrelationTimeMs) {
      setNotice("无关联候选事件时间上限不能小于强关联时间。");
      return false;
    }
    return true;
  }

  async function compare() {
    if (!cloudFile.file) { setNotice("请先选择从 EDR 平台导出的云端事件文件。"); return; }
    if (!localFile.file && !selectedRunId) { setNotice("请选择已完成轮次，或导入本地运行 JSON。"); return; }
    if (!mappingId) { setNotice("本地仓库没有可用的字段映射。"); return; }
    if (!validComparisonTimeSettings()) return;
    const form = new FormData();
    form.append("cloud_file", cloudFile.file);
    form.append("mapping_id", mappingId);
    form.append("strong_correlation_time_ms", String(strongCorrelationTimeMs));
    form.append("candidate_time_limit_ms", String(candidateTimeLimitMs));
    const actionNameStandards = Object.fromEntries(Object.entries(actionNameInputs)
      .map(([capabilityId, value]) => [capabilityId, parseFilterValues(value)])
      .filter(([, values]) => values.length > 0));
    if (Object.keys(actionNameStandards).length) form.append("action_name_standards", JSON.stringify(actionNameStandards));
    const childFileCreateOpNameStandards = Object.fromEntries(Object.entries(childFileCreateOpNameInputs)
      .filter(([capabilityId]) => fileCapabilityIds.has(capabilityId))
      .map(([capabilityId, value]) => [capabilityId, parseFilterValues(value)])
      .filter(([, values]) => values.length > 0));
    if (Object.keys(childFileCreateOpNameStandards).length) form.append("child_file_create_op_name_standards", JSON.stringify(childFileCreateOpNameStandards));
    if (manifestFile.file) form.append("cloud_manifest", manifestFile.file);
    if (localFile.file) form.append("local_file", localFile.file);
    else form.append("operation_id", selectedRunId);
    const comparisonId = window.crypto.randomUUID();
    form.append("comparison_id", comparisonId);
    setComparison(null);
    setComparisonProgress({ comparison_id: comparisonId, status: "preparing", completed_capabilities: 0, total_capabilities: 0, progress: 0 });
    setIsComparing(true);
    let keepPolling = true;
    const progressPolling = pollComparisonProgress(comparisonId, () => keepPolling, setComparisonProgress);
    try {
      const result = await apiRequest<ValidationResult>("/compare", { method: "POST", body: form });
      keepPolling = false;
      await progressPolling;
      setComparisonProgress((current) => ({
        comparison_id: comparisonId,
        status: "completed",
        completed_capabilities: result.capabilities.length,
        total_capabilities: result.capabilities.length,
        progress: 100,
        capability_id: current?.capability_id,
        display_name_zh: current?.display_name_zh,
        validation_status: current?.validation_status,
      }));
      setComparison(result);
      setNotice(`比较完成：${result.summary.pass} 项通过，${result.summary.fail} 项失败。`);
    } catch (error) {
      keepPolling = false;
      await progressPolling;
      setComparisonProgress((current) => current ? { ...current, status: "failed", error: error instanceof Error ? error.message : "比较失败。" } : current);
      setNotice(error instanceof Error ? error.message : "比较失败。");
    } finally {
      keepPolling = false;
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
          apiState={apiState} availableIds={availableIds} administratorRequiredIds={administratorRequiredIds} selectedIds={selectedIds} activeRun={activeRun} recentRuns={recentRuns}
          runName={runName} environment={environment} nextDelay={nextDelay} allowHighRisk={allowHighRisk} selectedRisk={selectedRisk} hasHighRisk={hasHighRisk}
          onRunName={setRunName} onEnvironment={setEnvironment} onNextDelay={setNextDelay} onAllowHighRisk={setAllowHighRisk}
          onToggle={toggleCapability} onSelectAll={() => setSelectedIds([...availableIds])} onClear={() => setSelectedIds([])}
          onStart={() => void startRun()} onCancel={() => void cancelRun()} onDownload={(run) => void downloadLocalExport(run)} onInspect={setActiveRun} onRefresh={() => void refreshRuns()}
        />}
        {view === "compare" && <CompareWorkspace
          apiState={apiState} baselines={baselines} mappings={mappings} mappingId={mappingId} onMappingId={setMappingId}
          completedRuns={completedRuns} selectedRunId={selectedRunId} onSelectedRunId={setSelectedRunId}
          localFile={localFile} cloudFile={cloudFile} manifestFile={manifestFile}
          actionNameInputs={actionNameInputs} onActionNameInput={(capabilityId, value) => setActionNameInputs((current) => ({ ...current, [capabilityId]: value }))}
          childFileCreateOpNameInputs={childFileCreateOpNameInputs} onChildFileCreateOpNameInput={(capabilityId, value) => setChildFileCreateOpNameInputs((current) => ({ ...current, [capabilityId]: value }))}
          onSaveEdrFilters={saveEdrFilterStandards} onClearEdrFilters={clearEdrFilterStandards}
          strongCorrelationTimeMs={strongCorrelationTimeMs} candidateTimeLimitMs={candidateTimeLimitMs}
          onStrongCorrelationTimeMs={setStrongCorrelationTimeMs} onCandidateTimeLimitMs={setCandidateTimeLimitMs}
          onSaveComparisonTimeSettings={saveComparisonTimeSettings} onResetComparisonTimeSettings={resetComparisonTimeSettings}
          onLocalFile={(event) => chooseFile(setLocalFile, event)} onCloudFile={(event) => chooseFile(setCloudFile, event)} onManifestFile={(event) => chooseFile(setManifestFile, event)}
          comparison={comparison} comparisonProgress={comparisonProgress} isComparing={isComparing} onCompare={() => void compare()}
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
  apiState: ApiState; availableIds: Set<string>; administratorRequiredIds: Set<string>; selectedIds: string[]; activeRun: ApiRun | null; recentRuns: ApiRun[];
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
            const selected = props.selectedIds.includes(capability.id); const available = props.availableIds.has(capability.id); const requiresAdministrator = props.administratorRequiredIds.has(capability.id);
            return <label className={`capability-item ${selected ? "selected" : ""} ${available ? "available" : "unavailable"}`} key={capability.id} aria-disabled={!available}><input type="checkbox" checked={selected} disabled={!available} onChange={() => props.onToggle(capability.id)} /><span className="checkbox-ui" aria-hidden="true" /><span className="capability-copy"><span className="capability-title-row"><strong><span className="capability-name-zh">{capability.nameZh}</span><span className="capability-name-en">{capability.nameEn}</span></strong><span className={`risk-badge ${capability.risk.toLowerCase()}`}>{capability.risk}</span></span><span className="program-line">{available ? capability.programs : "样本待实现"}{available && requiresAdministrator ? " · 需要管理员权限" : ""}</span></span></label>;
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
    {selectedStep && <div className="log-modal-backdrop" onClick={() => setSelectedCapabilityId(null)}><section className="log-modal" role="dialog" aria-modal="true" aria-labelledby="capability-log-title" onClick={(event) => event.stopPropagation()}><header className="log-modal-header"><div><p className="section-index">能力详细日志</p><h2 id="capability-log-title">{selectedStep.name_zh}</h2><code>{selectedStep.capability_id}</code></div><div><span className={`queue-status ${selectedStep.status}`}>{selectedStep.status_label}</span><button className="modal-close" type="button" autoFocus aria-label="关闭能力详细日志" onClick={() => setSelectedCapabilityId(null)}>×</button></div></header><div className="capability-log-content"><LocalEvidencePanel evidence={selectedStep.local_evidence} /><section className="runner-log-section"><div className="subsection-heading"><div><span>执行输出</span><h3>Runner 与 Controller 日志</h3></div><em>{selectedLogs.length} 条</em></div><div className="log-console" role="log">{selectedLogs.length ? selectedLogs.map((log, index) => <div className={`log-line ${log.level}`} key={`${log.timestamp_utc}-${index}`}><time>{formatTime(log.timestamp_utc)}</time><span>{log.level.toUpperCase()}</span><code>{log.source}</code><p>{log.message}</p></div>) : <div className="log-empty">该能力没有可用的详细输出。</div>}</div></section></div></section></div>}
  </>;
}

function LocalEvidencePanel({ evidence }: { evidence?: ApiLocalEvidence }) {
  if (!evidence) return <section className="local-evidence-panel"><div className="subsection-heading"><div><span>本地 BASELINE</span><h3>已提取证据</h3></div></div><div className="local-evidence-empty">当前轮次没有可恢复的结构化本地证据。</div></section>;
  const baselineValues: Array<{ field: string; value: unknown }> = evidence.programs.flatMap((program) => [
    { field: `programs.${program.role}.pid`, value: program.pid },
    { field: `programs.${program.role}.parent_pid`, value: program.parent_pid },
    { field: `programs.${program.role}.executable`, value: program.executable },
    { field: `programs.${program.role}.command_line`, value: program.command_line },
    ...(program.md5 ? [{ field: `programs.${program.role}.md5`, value: program.md5 }] : []),
    { field: `programs.${program.role}.sha256`, value: program.sha256 },
  ]);
  baselineValues.push(...evidence.facts.map((fact) => ({ field: fact.field, value: fact.value })));
  return <section className="local-evidence-panel"><div className="subsection-heading"><div><span>本地 BASELINE</span><h3>已提取证据</h3><p>字段名可直接对应本地检验基准；时间用于后续关联 EDR 事件。</p></div><em>{baselineValues.length} 个字段</em></div>
    <div className="evidence-time-grid"><div><span>能力开始</span><strong>{formatTime(evidence.capability.started_at_utc)}</strong></div><div><span>能力结束</span><strong>{formatTime(evidence.capability.ended_at_utc)}</strong></div><div><span>执行耗时</span><strong>{formatDurationMs(evidence.capability.duration_ms)}</strong></div><div><span>观察窗口</span><strong>{formatTime(evidence.capability.observer_started_at_utc)} → {formatTime(evidence.capability.observer_ended_at_utc)}</strong></div></div>
    <div className="baseline-value-list">{baselineValues.map((item, index) => <div key={`${item.field}-${index}`}><code>{item.field}</code><span>{displayValue(item.value)}</span></div>)}</div>
    <div className="program-evidence-grid">{evidence.programs.map((program) => <article key={`${program.role}-${program.instance_index}`}><header><strong>{roleLabel(program.role)}</strong><code>PID {program.pid}</code></header><dl><div><dt>父 PID</dt><dd>{program.parent_pid}</dd></div><div><dt>开始时间</dt><dd>{formatTime(program.started_at_utc)}</dd></div><div><dt>停止时间</dt><dd>{formatTime(program.ended_at_utc)}</dd></div><div><dt>退出码</dt><dd>{program.exit_code ?? "—"}</dd></div></dl></article>)}</div>
  </section>;
}

function RunHistory({ runs, onInspect, onDownload, onRefresh }: { runs: ApiRun[]; onInspect: (run: ApiRun) => void; onDownload: (run: ApiRun) => void; onRefresh: () => void }) {
  return <section className="panel runs-panel"><div className="panel-heading"><div><p className="section-index">F / 运行记录</p><h2>本机最近轮次</h2></div><button className="text-button" type="button" onClick={onRefresh}>刷新</button></div>{runs.length ? <div className="table-wrap"><table><thead><tr><th>轮次</th><th>能力</th><th>开始时间</th><th>本地状态</th><th>操作</th></tr></thead><tbody>{runs.map((run) => <tr key={run.operation_id}><td><strong>{run.name}</strong><span className="table-id">{run.run_id ?? run.operation_id}</span></td><td>{run.capability_ids.length} 项</td><td>{formatTime(run.started_at_utc)}</td><td><span className="table-status"><span className={`status-dot ${statusDot(run.status)}`} />{runStatusLabel(run.status)}</span></td><td><div className="table-actions"><button className="text-button" type="button" onClick={() => onInspect(run)}>查看</button>{run.local_export_available && <button className="text-button" type="button" onClick={() => onDownload(run)}>下载</button>}</div></td></tr>)}</tbody></table></div> : <div className="table-empty">本机还没有测试轮次。</div>}</section>;
}

type CompareWorkspaceProps = {
  apiState: ApiState; baselines: ApiBaseline[]; mappings: ApiMapping[]; mappingId: string; onMappingId: (value: string) => void;
  completedRuns: ApiRun[]; selectedRunId: string; onSelectedRunId: (value: string) => void;
  localFile: FileChoice; cloudFile: FileChoice; manifestFile: FileChoice;
  onLocalFile: (event: ChangeEvent<HTMLInputElement>) => void; onCloudFile: (event: ChangeEvent<HTMLInputElement>) => void; onManifestFile: (event: ChangeEvent<HTMLInputElement>) => void;
  actionNameInputs: Record<string, string>; onActionNameInput: (capabilityId: string, value: string) => void;
  childFileCreateOpNameInputs: Record<string, string>; onChildFileCreateOpNameInput: (capabilityId: string, value: string) => void;
  onSaveEdrFilters: () => void; onClearEdrFilters: () => void;
  strongCorrelationTimeMs: number; candidateTimeLimitMs: number;
  onStrongCorrelationTimeMs: (value: number) => void; onCandidateTimeLimitMs: (value: number) => void;
  onSaveComparisonTimeSettings: () => void; onResetComparisonTimeSettings: () => void;
  comparison: ValidationResult | null; comparisonProgress: ComparisonProgress | null; isComparing: boolean; onCompare: () => void; onDownloadJson: () => void; onDownloadConclusion: () => void;
};

function CompareWorkspace(props: CompareWorkspaceProps) {
  return <>
    <PageHeader index="03" eyebrow="离线比较" title="核对 EDR 日志" description="选择本地运行结果和 EDR 导出日志。系统会按 BASELINE 逐条说明要求是什么、是否满足，以及依据是什么。" apiState={props.apiState} />
    <section className="panel compare-input-panel"><div className="panel-heading compare-heading"><div><p className="section-index">A / 准备输入</p><h2>选择本地结果与 EDR 日志</h2><p className="panel-description">文件只会发送到 127.0.0.1 的本地比较服务。</p></div><button className="primary-button compact" type="button" onClick={props.onCompare} disabled={props.isComparing || props.apiState !== "online"}>{props.isComparing ? "正在逐项核对" : "开始离线比较"}<span aria-hidden="true">→</span></button></div>
      <div className="compare-select-grid"><label className="field-label">使用已完成轮次<select value={props.selectedRunId} onChange={(event) => props.onSelectedRunId(event.target.value)} disabled={Boolean(props.localFile.file)}><option value="">请选择本机轮次</option>{props.completedRuns.map((run) => <option value={run.operation_id} key={run.operation_id}>{run.name} · {formatTime(run.started_at_utc)}</option>)}</select><span className="field-help">导入本地 JSON 后，此选择会被替代。</span></label><label className="field-label">EDR 字段映射<select value={props.mappingId} onChange={(event) => props.onMappingId(event.target.value)}>{props.mappings.map((mapping) => <option value={mapping.profile_id} key={mapping.profile_id}>{mapping.vendor} {mapping.product} · {mapping.profile_id}</option>)}</select><span className="field-help">负责把厂商字段转换成统一字段。</span></label></div>
      <div className="comparison-time-settings"><div className="comparison-time-heading"><div><span>时间关联参数</span><strong>先裁剪候选，再判断强关联</strong><p>候选上限越小，需要评分的无关 EDR 日志越少；两项都以本地行为时间为基准。</p></div><div><button className="text-button" type="button" onClick={props.onResetComparisonTimeSettings}>恢复默认</button><button className="secondary-button" type="button" onClick={props.onSaveComparisonTimeSettings}>保存到本机</button></div></div><div className="comparison-time-grid"><label className="field-label">强关联时间（ms）<input type="number" min="1" max="60000" step="1" value={props.strongCorrelationTimeMs} onChange={(event) => props.onStrongCorrelationTimeMs(Number(event.target.value))} /><span className="field-help">默认 15 ms；命中身份锚点且时间差不超过该值时形成强时间证据。</span></label><label className="field-label">无关联候选事件时间上限（ms）<input type="number" min="1" max="300000" step="1" value={props.candidateTimeLimitMs} onChange={(event) => props.onCandidateTimeLimitMs(Number(event.target.value))} /><span className="field-help">默认 1000 ms（1 秒）；超出范围的 EDR 事件不进入锚点评分和候选展示。</span></label></div></div>
      <div className="upload-grid"><FileSlot id="local-file" step="1" title="本地运行 JSON" hint="可选：用于比较其他机器或历史轮次" choice={props.localFile} onChange={props.onLocalFile} /><FileSlot id="cloud-file" step="2" title="EDR 云端事件" hint="必需：支持 JSON 数组或 JSONL" choice={props.cloudFile} required onChange={props.onCloudFile} /><FileSlot id="manifest-file" step="3" title="云端导出清单" hint="建议：证明主机与时间范围完整" choice={props.manifestFile} onChange={props.onManifestFile} /></div>
      {props.comparisonProgress && <ComparisonProgressBar progress={props.comparisonProgress} isComparing={props.isComparing} />}
    </section>
    <EdrFilterSettings
      baselines={props.baselines}
      actionValues={props.actionNameInputs}
      childFileCreateOpNameValues={props.childFileCreateOpNameInputs}
      onActionChange={props.onActionNameInput}
      onChildFileCreateOpNameChange={props.onChildFileCreateOpNameInput}
      onSave={props.onSaveEdrFilters}
      onClear={props.onClearEdrFilters}
    />
    <BaselineGuide baselines={props.baselines} />
    <ComparisonResultPanel result={props.comparison} onDownloadJson={props.onDownloadJson} onDownloadConclusion={props.onDownloadConclusion} />
  </>;
}

function ComparisonProgressBar({ progress, isComparing }: { progress: ComparisonProgress; isComparing: boolean }) {
  const percentage = Math.min(100, Math.max(0, progress.progress));
  const hasTotal = progress.total_capabilities > 0;
  const statusLabel = progress.validation_status ? validationStatusLabel(progress.validation_status) : null;
  return <section className={`comparison-progress-panel ${isComparing ? "running" : percentage >= 100 ? "complete" : "stopped"}`} aria-live="polite" aria-label="离线比较进度">
    <div className="comparison-progress-heading"><div><span>离线比较进度</span><strong>{hasTotal ? `已完成 ${progress.completed_capabilities} / ${progress.total_capabilities} 项能力` : "正在读取参测能力"}</strong><p>{progress.display_name_zh ? `刚完成：${progress.display_name_zh}${statusLabel ? `（${statusLabel}）` : ""}` : "每完成一项能力，进度将立即更新。"}</p></div><em>{percentage.toFixed(1)}%</em></div>
    <div className="progress-track large comparison-progress-track" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentage} aria-valuetext={hasTotal ? `已完成 ${progress.completed_capabilities} 项，共 ${progress.total_capabilities} 项` : "正在读取参测能力"}><span style={{ width: `${percentage}%` }} /></div>
    <div className="comparison-progress-formula"><span>计算公式</span><code>已完成能力数 ÷ 参测能力总数 × 100%</code><em>{isComparing ? percentage >= 100 ? "能力核对完成，正在生成报告" : "正在逐项核对" : percentage >= 100 ? "离线比较已完成" : "比较未完成"}</em></div>
  </section>;
}

function EdrFilterSettings({ baselines, actionValues, childFileCreateOpNameValues, onActionChange, onChildFileCreateOpNameChange, onSave, onClear }: {
  baselines: ApiBaseline[];
  actionValues: Record<string, string>;
  childFileCreateOpNameValues: Record<string, string>;
  onActionChange: (capabilityId: string, value: string) => void;
  onChildFileCreateOpNameChange: (capabilityId: string, value: string) => void;
  onSave: () => void;
  onClear: () => void;
}) {
  const fileBaselines = baselines.filter((baseline) => fileCapabilityIds.has(baseline.capability_id));
  const configured = [...Object.values(actionValues), ...Object.values(childFileCreateOpNameValues)]
    .filter((value) => parseFilterValues(value).length > 0).length;
  return <details className="panel action-name-settings compare-fold-panel"><summary className="panel-heading compare-fold-heading"><div><p className="section-index">B / 可选消歧</p><h2>EDR 原始字段筛选</h2><p className="panel-description">默认收起；展开后可按能力编辑 EDR 原始字段标准。</p></div><div className="fold-heading-meta"><span className="line-badge">{configured} 项已填写</span></div></summary><div className="compare-fold-body"><div className="action-settings-actions"><span>先按本地路径、程序、PID 和时间建立候选；下列值只做 EDR 候选的进一步筛选。</span><button className="text-button" type="button" onClick={onClear}>全部清空</button><button className="secondary-button" type="button" onClick={onSave}>保存到本机</button></div>
    <div className="edr-filter-group"><header><div><h3>Action.Name</h3><p>适用于全部已有 BASELINE；多个值采用“任选其一”。</p></div><code>EDR ONLY</code></header><div className="action-name-grid">{baselines.map((baseline) => <label className="action-name-field" key={`action-${baseline.baseline_id}`}><span><strong>{baseline.title}</strong><code>{baseline.capability_id}</code></span><input type="text" maxLength={1024} value={actionValues[baseline.capability_id] ?? ""} placeholder="留空：不按 Action.Name 筛选" onChange={(event) => onActionChange(baseline.capability_id, event.target.value)} /><em>精确匹配，不区分大小写；支持逗号、分号或换行</em></label>)}</div></div>
    <div className="edr-filter-group"><header><div><h3>Child.FileCreateOpName</h3><p>仅针对五项文件能力；删除和重命名默认留空。</p></div><code>FILE ONLY</code></header><div className="action-name-grid">{fileBaselines.map((baseline) => <label className="action-name-field" key={`file-operation-${baseline.baseline_id}`}><span><strong>{baseline.title}</strong><code>{baseline.capability_id}</code></span><input type="text" maxLength={1024} value={childFileCreateOpNameValues[baseline.capability_id] ?? ""} placeholder="留空：不按 Child.FileCreateOpName 筛选" onChange={(event) => onChildFileCreateOpNameChange(baseline.capability_id, event.target.value)} /><em>只读取 EDR 原始字段；多个值为任选其一</em></label>)}</div></div>
  </div></details>;
}

function BaselineGuide({ baselines }: { baselines: ApiBaseline[] }) {
  const groups = groupByCapabilityCategory(baselines);
  return <details className="panel baseline-guide compare-fold-panel"><summary className="panel-heading compare-fold-heading"><div><p className="section-index">C / BASELINE 是什么</p><h2>通过判定的要求</h2><p className="panel-description">默认收起；展开后按能力大类查看本地要求和 EDR 要求。</p></div><div className="fold-heading-meta"><span className="line-badge">{groups.length} 类 · {baselines.length} 份基准</span></div></summary><div className="compare-fold-body baseline-category-stack">{groups.map((group) => <details className="baseline-category-card category-fold-card" key={group.id}><summary><div><strong>{group.nameZh}</strong><span>{group.nameEn}</span></div><em>{group.items.length} 份基准</em></summary><div className="baseline-card-grid">{group.items.map((baseline) => {
    const localCount = baseline.requirements.filter((item) => item.scope === "local").length; const cloudCount = baseline.requirements.length - localCount;
    return <details className="baseline-card" key={baseline.baseline_id}><summary><div><strong>{baseline.title}</strong><span>能力 v{baseline.capability_version} · {localCount} 项本地要求 · {cloudCount} 项 EDR 要求</span></div><em>{baseline.risk_level}</em></summary><div className="baseline-preview">{baseline.requirements.map((requirement) => <div key={requirement.requirement_id}><span className={`scope-chip ${requirement.scope}`}>{requirement.scope === "local" ? "本地" : "EDR"}</span><p>{requirement.title_zh}</p><em>{severityLabel(requirement.severity)}</em></div>)}</div></details>;
  })}</div></details>)}</div></details>;
}

function ComparisonResultPanel({ result, onDownloadJson, onDownloadConclusion }: { result: ValidationResult | null; onDownloadJson: () => void; onDownloadConclusion: () => void }) {
  const groups = result ? groupByCapabilityCategory(result.capabilities) : [];
  return <section className="panel comparison-detail-panel"><div className="panel-heading"><div><p className="section-index">D / 比较结果</p><h2>要求满足情况</h2><p className="panel-description">结果先按能力大类折叠；展开大类后再选择具体能力，本地条件默认收起，EDR 条件与关联候选日志默认展开。</p></div></div>{result ? <>
    <div className={`conclusion-card ${result.conclusion.verdict.toLowerCase()}`}><div><span>总体结论</span><strong>{result.conclusion.label_zh}</strong><em>{result.conclusion.pass_rate === null ? "通过率不可计算" : `完整通过率 ${(result.conclusion.pass_rate * 100).toFixed(1)}%`}</em></div><p>{result.conclusion.statement_zh}</p></div>
    <div className="comparison-parameter-summary"><span>本轮时间参数</span><strong>强关联 ≤ {result.inputs?.strong_correlation_time_ms ?? defaultStrongCorrelationTimeMs} ms</strong><strong>候选上限 ≤ {result.inputs?.candidate_time_limit_ms ?? defaultCandidateTimeLimitMs} ms</strong><em>先按候选上限裁剪，再执行锚点评分</em></div>
    <div className="result-summary"><div><span>通过</span><strong className="pass-text">{result.summary.pass}</strong></div><div><span>部分通过</span><strong>{result.summary.partial}</strong></div><div><span>失败</span><strong className="fail-text">{result.summary.fail}</strong></div><div><span>无法判定</span><strong>{result.summary.inconclusive}</strong></div></div>
    <div className="comparison-category-stack">{groups.map((group) => <CapabilityResultCategory group={group} key={group.id} />)}</div>
    <div className="result-actions"><button className="secondary-button" type="button" onClick={onDownloadJson}>下载完整 JSON</button><button className="secondary-button" type="button" onClick={onDownloadConclusion}>下载中文结论</button></div>
  </> : <div className="result-placeholder"><span className="bracket" aria-hidden="true">[ ]</span><div><strong>等待离线比较</strong><p>比较完成后，这里会用简单中文列出每项 BASELINE 要求及满足情况。</p></div></div>}</section>;
}

function CapabilityResultCategory({ group }: { group: CapabilityItemGroup<ValidationEntry> }) {
  const statuses: ValidationStatus[] = ["PASS", "PARTIAL", "FAIL", "INCONCLUSIVE", "NOT_COMPARED"];
  const counts = Object.fromEntries(statuses.map((status) => [status, group.items.filter((item) => item.validation_status === status).length])) as Record<ValidationStatus, number>;
  return <details className="comparison-category-card category-fold-card"><summary><div><strong>{group.nameZh}</strong><span>{group.nameEn}</span></div><div className="category-result-counts">{statuses.filter((status) => counts[status] > 0).map((status) => <span className={status.toLowerCase()} key={status}>{validationStatusLabel(status)} {counts[status]}</span>)}</div><em>{group.items.length} 项能力</em></summary><div className="capability-result-stack">{group.items.map((entry) => <CapabilityComparison key={entry.case_run_id} entry={entry} />)}</div></details>;
}

function CapabilityComparison({ entry }: { entry: ValidationEntry }) {
  const [showJsonComparison, setShowJsonComparison] = useState(false);
  const [jsonComparisonIndex, setJsonComparisonIndex] = useState(0);
  const requirements = entry.baseline_requirements ?? [];
  const passed = requirements.filter((item) => item.status === "passed").length;
  const localRequirements = requirements.filter((item) => item.scope === "local");
  const cloudRequirements = requirements.filter((item) => item.scope === "cloud");
  const methods = entry.method_results ?? [];
  const stages = entry.stage_results ?? [];
  const candidates = [...(entry.edr_candidates ?? [])].sort((left, right) => Number(right.eligible_for_validation) - Number(left.eligible_for_validation)
    || Number(right.anchor_qualified) - Number(left.anchor_qualified)
    || right.correlation_score - left.correlation_score
    || left.time_distance_ms - right.time_distance_ms
    || left.rank - right.rank);
  const openJsonComparison = (candidate?: EdrCandidate) => {
    const candidateIndex = candidate ? candidates.indexOf(candidate) : 0;
    setJsonComparisonIndex(candidateIndex >= 0 ? candidateIndex : 0);
    setShowJsonComparison(true);
  };
  return <><details className="capability-result-card"><summary><div className="capability-summary-main"><span className={`result-status ${entry.validation_status.toLowerCase()}`}>{validationStatusLabel(entry.validation_status)}</span><div><h3>{entry.display_name_zh ?? entry.capability_id}</h3><p>{entry.baseline_title ?? entry.baseline_id ?? "没有匹配的检验基准"}</p></div></div><div className="requirement-count">{entry.method_selection ? <><strong>{validationStatusLabel(entry.method_selection.selected_method_status)}</strong><span>最佳方法 · {entry.method_selection.selected_method_title}</span></> : entry.stage_flow ? <><strong>{validationStatusLabel(entry.stage_flow.status)}</strong><span>三部分证据链</span></> : <><strong>{passed}/{requirements.length}</strong><span>要求已满足</span></>}</div></summary><div className="capability-result-body"><div className="evidence-strip"><span>{coverageLabel(entry.export_coverage)}</span><span>{entry.candidate_count} 条候选事件</span><span>本地状态 {entry.local_status}</span>{entry.method_selection && <span>结论方法：{entry.method_selection.selected_method_title}</span>}{entry.stage_flow && <span>验证流程：连接 → 同进程关联 → 文件写入</span>}<button className="json-compare-open" type="button" onClick={() => openJsonComparison()}>打开 JSON 对照窗</button></div>{entry.warnings?.length ? <div className="plain-warning"><strong>需要注意</strong><p>{entry.warnings.join("；")}</p></div> : null}{methods.length > 0 ? <MethodComparison methods={methods} selection={entry.method_selection ?? null} requirements={requirements} candidates={candidates} onOpenCandidate={openJsonComparison} /> : <><RequirementGroup scope="local" requirements={localRequirements} />{stages.length > 0 ? <StageComparison stages={stages} flow={entry.stage_flow ?? null} requirements={cloudRequirements} candidates={candidates} onOpenCandidate={openJsonComparison} /> : <RequirementGroup scope="cloud" requirements={cloudRequirements} candidates={candidates} onOpenCandidate={openJsonComparison} />}</>}</div></details>{showJsonComparison && <JsonComparisonModal entry={entry} candidates={candidates} initialSelectedIndex={jsonComparisonIndex} onClose={() => setShowJsonComparison(false)} />}</>;
}

function MethodComparison({ methods, selection, requirements, candidates, onOpenCandidate }: { methods: MethodResult[]; selection: MethodSelectionResult | null; requirements: BaselineRequirementResult[]; candidates: EdrCandidate[]; onOpenCandidate: (candidate: EdrCandidate) => void }) {
  const localRequirements = requirements.filter((requirement) => requirement.scope === "local");
  return <section className="method-comparison-section"><header><div><span>EDR 子测试方法</span><h4>不同方法的通过情况</h4><p>每种方法内分别展示本地绝对基准与该方法对应的 EDR 要求；最佳方法默认展开。</p></div><em>{methods.length} 种方法</em></header>{selection && <div className="method-selection-notice"><strong>已采用最佳方法形成结论</strong><p>{selection.notice}</p></div>}<div className="method-result-list">{methods.map((method) => {
    const methodRequirements = requirements.filter((requirement) => requirement.scope === "cloud" && requirement.expectation_id === method.expectation_id);
    const methodCandidates = candidates.filter((candidate) => candidate.expectation_id === method.expectation_id);
    const allMethodRequirements = [...localRequirements, ...methodRequirements];
    const passedMethodRequirements = allMethodRequirements.filter((requirement) => requirement.status === "passed").length;
    return <details className={`method-result-card ${method.selected_for_conclusion ? "selected" : ""}`} open={method.selected_for_conclusion} key={method.method_id}><summary><div><strong>{method.method_title}</strong><code>{method.method_id} · {method.expectation_id}</code></div><span className={`result-status ${method.status.toLowerCase()}`}>{validationStatusLabel(method.status)}</span><div className="method-result-metrics"><strong>{passedMethodRequirements}/{allMethodRequirements.length}</strong><span>要求已满足 · 候选日志 {method.candidate_count} 条，其中合格 {method.qualified_candidate_count} 条</span></div>{method.selected_for_conclusion && <em>结论采用</em>}</summary><div className="method-result-body">{method.warnings.length > 0 && <div className="method-warning"><strong>该方法需要关注</strong><p>{method.warnings.join("；")}</p></div>}<RequirementGroup scope="local" requirements={localRequirements} /><RequirementGroup scope="cloud" requirements={methodRequirements} candidates={methodCandidates} onOpenCandidate={onOpenCandidate} /></div></details>;
  })}</div></section>;
}

function StageComparison({ stages, flow, requirements, candidates, onOpenCandidate }: { stages: StageResult[]; flow: StageFlowResult | null; requirements: BaselineRequirementResult[]; candidates: EdrCandidate[]; onOpenCandidate: (candidate: EdrCandidate) => void }) {
  return <section className="method-comparison-section"><header><div><span>EDR 有序验证</span><h4>文件下载三部分证据链</h4><p>先按 TCP 标准验证网络连接，再证明网络记录与文件记录是同一进程的连续行为，最后验证目标文件写入。</p></div><em>{stages.length} 个部分</em></header>{flow && <div className="method-selection-notice"><strong>三部分必须全部满足</strong><p>{flow.notice}</p></div>}<div className="method-result-list">{stages.map((stage) => {
    const stageRequirements = requirements.filter((requirement) => requirement.expectation_id === stage.expectation_id);
    const relatedIds = stage.related_expectation_ids.length ? stage.related_expectation_ids : [stage.expectation_id];
    const stageCandidates = candidates.filter((candidate) => relatedIds.includes(candidate.expectation_id));
    const relation = stage.relationship;
    const passedStageRequirements = stageRequirements.filter((requirement) => requirement.status === "passed").length;
    return <details className="method-result-card" open key={stage.expectation_id}><summary><div><strong>{stage.title}</strong><code>部分 {stage.sequence} · {stage.kind === "relationship" ? "派生关联" : stage.expectation_id}</code></div><span className={`result-status ${stage.status.toLowerCase()}`}>{validationStatusLabel(stage.status)}</span><div className="method-result-metrics"><strong>{passedStageRequirements}/{stageRequirements.length}</strong><span>要求已满足 · 候选日志 {stage.candidate_count} 条，其中合格 {stage.qualified_candidate_count} 条</span></div>{stage.depends_on && <em>依赖部分 {stage.sequence - 1}</em>}</summary><div className="method-result-body">{relation && <div className="method-selection-notice"><strong>同进程连续行为</strong><p>PID {relation.same_process_pid ? "一致" : relation.same_process_pid === false ? "不一致" : "无法判断"}；程序路径 {relation.same_process_executable ? "一致" : relation.same_process_executable === false ? "不一致" : "无法判断"}；顺序 {relation.ordered ? "连接在前" : relation.ordered === false ? "发生反转" : "无法判断"}；本地间隔 {relation.local_interval_ms ?? "未知"} ms，EDR 间隔 {relation.cloud_interval_ms ?? "未知"} ms，间隔误差 {relation.interval_difference_ms ?? "未知"} / {relation.maximum_interval_difference_ms} ms。</p></div>}{stage.order_message && <div className={`method-warning ${stage.order_status === "failed" ? "failed" : ""}`}><strong>{stage.kind === "relationship" ? "关联结论" : "EDR 阶段顺序"}：{stage.order_status === "passed" ? "符合" : stage.order_status === "failed" ? "不符合" : "无法验证"}</strong><p>{stage.order_message}</p></div>}{stage.warnings.length > 0 && <div className="method-warning"><strong>该部分需要关注</strong><p>{stage.warnings.join("；")}</p></div>}<RequirementGroup scope="cloud" requirements={stageRequirements} candidates={stageCandidates} onOpenCandidate={onOpenCandidate} /></div></details>;
  })}</div></section>;
}

function RequirementGroup({ scope, requirements, candidates = [], onOpenCandidate }: { scope: "local" | "cloud"; requirements: BaselineRequirementResult[]; candidates?: EdrCandidate[]; onOpenCandidate?: (candidate: EdrCandidate) => void }) {
  const passed = requirements.filter((item) => item.status === "passed").length;
  const isCloud = scope === "cloud";
  return <details className={`requirement-group ${scope}`} open={isCloud}><summary><div><span className={`scope-chip ${scope}`}>{isCloud ? "EDR" : "本地"}</span><strong>{isCloud ? "EDR 条件与匹配日志" : "本地绝对基准"}</strong><em>{isCloud ? "左侧核对要求，右侧查看候选日志" : "默认折叠，缺失或不满足时能力不能通过"}</em></div><span>{passed}/{requirements.length} 已满足</span></summary><div className={`requirement-group-body ${isCloud ? "edr-conclusion-layout" : ""}`}><section className="requirement-match-panel">{isCloud && <header className="compact-column-heading"><div><span>结论满足情况</span><h4>匹配与完成情况</h4><p>逐项核对 EDR 要求、实际值与完成状态。</p></div><em>{passed}/{requirements.length}</em></header>}<div className="requirement-table"><div className="requirement-head"><span>来源</span><span>要求</span><span>结果</span><span>依据</span></div>{requirements.length ? requirements.map((requirement) => <RequirementRow key={requirement.requirement_id} requirement={requirement} />) : <div className="table-empty">没有可展示的{isCloud ? " EDR" : "本地"}条件。</div>}</div></section>{isCloud && <EdrCandidateList candidates={candidates} onOpenCandidate={onOpenCandidate} />}</div></details>;
}

function EdrCandidateList({ candidates, onOpenCandidate }: { candidates: EdrCandidate[]; onOpenCandidate?: (candidate: EdrCandidate) => void }) {
  return <section className="edr-candidate-section"><div className="candidate-section-heading compact-column-heading"><div><span>EDR 日志</span><h4>候选 EDR 日志块</h4><p>按关联资格、锚点、得分和时间差排序。</p><div className="time-offset-legend"><code>-</code><span>EDR 早于本地（提前）</span><code>+</code><span>EDR 晚于本地（延后）</span><em>计算基准：EDR 时间 − 本地行为时间</em></div></div><em>{candidates.length} 条</em></div>{candidates.length ? <div className="candidate-list">{candidates.map((candidate, index) => {
    const state = candidate.eligible_for_validation ? "eligible" : candidate.anchor_qualified ? "action-rejected" : "exploratory";
    const label = candidate.eligible_for_validation ? "达到关联阈值" : candidate.anchor_qualified ? "锚点强匹配 · EDR 字段已排除" : "低置信度排查";
    return <button className={`candidate-card ${state}`} type="button" aria-label={`打开候选 #${index + 1} 的 JSON 对照`} onClick={() => onOpenCandidate?.(candidate)} key={`${candidate.expectation_id}-${candidate.raw_ref}-${index}`}><span className="candidate-rank">#{index + 1}</span><span className="candidate-identity"><strong>{candidate.event_id || "无事件 ID"}</strong><code>{candidate.expectation_id} · {label}</code></span><span className={`confidence-badge ${candidate.confidence}`}>{confidenceLabel(candidate.confidence)}</span><span className="candidate-score"><strong>{candidate.correlation_score} 分</strong><span>{formatSignedTimeOffset(candidate.time_offset_ms)}</span></span><span className="candidate-open-action">对照 JSON</span></button>;
  })}</div> : <div className="candidate-empty">候选时间上限内没有可展示的 EDR 日志。</div>}</section>;
}

function JsonComparisonModal({ entry, candidates, initialSelectedIndex, onClose }: { entry: ValidationEntry; candidates: EdrCandidate[]; initialSelectedIndex: number; onClose: () => void }) {
  const [selectedIndex, setSelectedIndex] = useState(() => Math.min(Math.max(initialSelectedIndex, 0), Math.max(candidates.length - 1, 0)));
  const candidate = candidates[selectedIndex];
  const allCandidateMatches = candidate?.baseline_matches ?? [];
  const candidateMatches = allCandidateMatches.filter((match) => match.status === "passed");
  const timestampMatches = allCandidateMatches.filter((match) => match.kind === "correlation" && match.canonical_field === "event.created");
  const localPointers = uniqueStrings([
    ...(entry.local_baseline_matches ?? []).filter((match) => match.status === "passed").map((match) => match.json_pointer),
    ...candidateMatches.map((match) => match.local_json_pointer),
  ]);
  const rawPointers = uniqueStrings(candidateMatches.map((match) => match.raw_json_pointer));
  const localTimestampPointers = uniqueStrings(timestampMatches.map((match) => match.local_json_pointer));
  const rawTimestampPointers = uniqueStrings(timestampMatches.map((match) => match.raw_json_pointer));

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === "Escape") onClose(); };
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [onClose]);

  return <div className="json-compare-backdrop" onClick={onClose}><section className="json-compare-modal" role="dialog" aria-modal="true" aria-labelledby={`json-compare-${entry.case_run_id}`} onClick={(event) => event.stopPropagation()}><header className="json-compare-header"><div><p className="section-index">逐能力 JSON 对照</p><h2 id={`json-compare-${entry.case_run_id}`}>{entry.display_name_zh ?? entry.capability_id}</h2><code>{entry.capability_id} · {entry.baseline_id ?? "无匹配 BASELINE"}</code></div><button className="modal-close" type="button" autoFocus aria-label="关闭 JSON 对照窗" onClick={onClose}>×</button></header>
    <div className="json-candidate-toolbar"><label>选择 EDR 候选 JSON 块<select value={selectedIndex} disabled={!candidates.length} onChange={(event) => setSelectedIndex(Number(event.target.value))}>{candidates.length ? candidates.map((item, index) => <option value={index} key={`${item.expectation_id}-${item.raw_ref}-${index}`}>#{index + 1} · {item.event_id || "无事件 ID"} · {item.expectation_id} · {confidenceLabel(item.confidence)}</option>) : <option value={0}>没有可选候选</option>}</select></label><div className="candidate-switch"><button type="button" disabled={selectedIndex <= 0} onClick={() => setSelectedIndex((value) => Math.max(0, value - 1))}>上一条</button><span>{candidates.length ? `${selectedIndex + 1} / ${candidates.length}` : "0 / 0"}</span><button type="button" disabled={selectedIndex >= candidates.length - 1} onClick={() => setSelectedIndex((value) => Math.min(candidates.length - 1, value + 1))}>下一条</button></div><div className="match-legend"><i /><span>绿色行表示该候选与 BASELINE 比对一致</span><i className="timestamp" /><span>蓝色行表示参与时间差计算的两侧时间戳</span><strong>{rawPointers.length} 个 EDR 字段命中 · {rawTimestampPointers.length} 个时间戳</strong></div></div>
    <div className="selected-candidate-meta">{candidate ? <><span>关联得分 {candidate.correlation_score}</span><span>EDR 相对本地 {formatSignedTimeOffset(candidate.time_offset_ms)} · 绝对差 {candidate.time_offset_ms === null ? "未知" : formatDurationMs(candidate.time_distance_ms)} / 基准 ≤ {candidate.maximum_time_difference_ms} ms</span><span>{confidenceLabel(candidate.confidence)}</span><span>{candidate.eligible_for_validation ? "达到关联阈值" : candidate.anchor_qualified ? "锚点强匹配，但被 EDR 字段筛除" : "低置信度排查块"}</span>{candidate.custom_action_name_expected.length > 0 && <span>Action.Name {candidate.custom_action_name_actual ?? "未读取"} / 标准 {candidate.custom_action_name_expected.join("、")}</span>}{candidate.custom_child_file_create_op_name_expected.length > 0 && <span>Child.FileCreateOpName {candidate.custom_child_file_create_op_name_actual ?? "未读取"} / 标准 {candidate.custom_child_file_create_op_name_expected.join("、")}</span>}<code>{candidate.raw_ref}</code></> : <span>当前能力没有可关联的 EDR 候选 JSON 块</span>}</div>
    <div className="json-side-by-side"><section><header><div><span>原 JSON</span><h3>本地运行导出块</h3></div><em>{localPointers.length} 处 BASELINE 高亮 · {localTimestampPointers.length} 个时间戳</em></header><JsonCodeViewer value={entry.local_export_block ?? {}} highlightPointers={localPointers} timestampPointers={localTimestampPointers} label="本地运行导出 JSON" /></section><section><header><div><span>导出 JSON</span><h3>EDR 平台候选块</h3></div><em>{rawPointers.length} 处 BASELINE 高亮 · {rawTimestampPointers.length} 个时间戳</em></header>{candidate ? <JsonCodeViewer value={candidate.raw_event} highlightPointers={rawPointers} timestampPointers={rawTimestampPointers} label="EDR 平台导出 JSON" /> : <div className="json-candidate-placeholder"><strong>没有可关联的 EDR JSON 块</strong><p>本地 JSON 仍可查看；导入范围内没有候选事件时不会产生错误高亮。</p></div>}</section></div>
    <footer className="json-match-footer"><strong>当前候选命中的 BASELINE 与自定义字段</strong><div>{candidateMatches.length ? candidateMatches.map((match) => <span key={`${match.kind}-${match.requirement_id}`}><i>{match.kind === "correlation" ? "关联" : match.kind === "custom_filter" ? "自定义" : "断言"}</i><code>{match.local_field ?? (match.kind === "custom_filter" ? `${match.raw_field ?? match.canonical_field} 标准` : "固定期望")} ↔ {match.raw_field ?? match.canonical_field}</code></span>) : <em>当前候选没有完全一致的字段。</em>}</div></footer>
  </section></div>;
}

type JsonDisplayLine = { pointer: string; text: string };

function JsonCodeViewer({ value, highlightPointers, timestampPointers, label }: { value: unknown; highlightPointers: string[]; timestampPointers: string[]; label: string }) {
  const highlighted = new Set(highlightPointers);
  const timestampHighlighted = new Set(timestampPointers);
  const lines = buildJsonLines(value);
  return <div className="json-code-viewer" role="region" aria-label={label}>{lines.map((line, index) => {
    const isBaselineMatch = highlighted.has(line.pointer);
    const isTimestamp = timestampHighlighted.has(line.pointer);
    return <div className={`json-code-line ${isBaselineMatch ? "baseline-match" : ""} ${isTimestamp ? "timestamp-match" : ""}`} key={`${line.pointer}-${index}`}><span>{String(index + 1).padStart(3, "0")}</span><code>{line.text}</code>{(isBaselineMatch || isTimestamp) && <em>{isTimestamp ? isBaselineMatch ? "时间戳 · 一致" : "时间戳" : "BASELINE 一致"}</em>}</div>;
  })}</div>;
}

function buildJsonLines(value: unknown, pointer = "", depth = 0, property?: string, trailingComma = false): JsonDisplayLine[] {
  const indent = "  ".repeat(depth);
  const propertyText = property === undefined ? "" : `${JSON.stringify(property)}: `;
  const suffix = trailingComma ? "," : "";
  if (Array.isArray(value)) {
    const lines: JsonDisplayLine[] = [{ pointer, text: `${indent}${propertyText}[` }];
    value.forEach((item, index) => lines.push(...buildJsonLines(item, joinJsonPointer(pointer, String(index)), depth + 1, undefined, index < value.length - 1)));
    lines.push({ pointer, text: `${indent}]${suffix}` });
    return lines;
  }
  if (value !== null && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>);
    const lines: JsonDisplayLine[] = [{ pointer, text: `${indent}${propertyText}{` }];
    entries.forEach(([key, item], index) => lines.push(...buildJsonLines(item, joinJsonPointer(pointer, key), depth + 1, key, index < entries.length - 1)));
    lines.push({ pointer, text: `${indent}}${suffix}` });
    return lines;
  }
  return [{ pointer, text: `${indent}${propertyText}${JSON.stringify(value) ?? "null"}${suffix}` }];
}

function joinJsonPointer(pointer: string, segment: string): string {
  const escaped = segment.replaceAll("~", "~0").replaceAll("/", "~1");
  return `${pointer}/${escaped}`;
}

function uniqueStrings(values: Array<string | undefined>): string[] {
  return [...new Set(values.filter((value): value is string => Boolean(value)))];
}

function RequirementRow({ requirement }: { requirement: BaselineRequirementResult }) {
  const evidence = requirementEvidence(requirement);
  return <div className={`requirement-row ${requirement.status}`}><span><i className={`scope-chip ${requirement.scope}`}>{requirement.scope === "local" ? "本地" : "EDR"}</i><em>{severityLabel(requirement.severity)}</em></span><div><strong>{requirement.title_zh}</strong><code>{requirement.field}</code></div><span className={`requirement-status ${requirement.status}`}>{requirementStatusLabel(requirement.status)}</span><div className="requirement-values"><p><b>{evidence.expectedLabel}：</b>{evidence.expectedValue}</p><p><b>{evidence.actualLabel}：</b>{evidence.actualValue}</p>{requirement.message && <p className="requirement-message">{requirement.message}</p>}</div></div>;
}

function FileSlot({ id, step, title, hint, choice, required = false, onChange }: { id: string; step: string; title: string; hint: string; choice: FileChoice; required?: boolean; onChange: (event: ChangeEvent<HTMLInputElement>) => void }) {
  return <label className={`file-slot ${choice.state}`} htmlFor={id}><input id={id} type="file" accept=".json,.jsonl,application/json" onChange={onChange} /><span className="file-step">{step}</span><span className="file-copy"><span className="file-title-row"><strong>{title}</strong><em>{required ? "必需" : "可选"}</em></span><span className="file-hint">{hint}</span><span className="file-name">{choice.name}</span><span className="file-summary">{choice.detail}</span></span><span className="file-action">选择文件</span></label>;
}
