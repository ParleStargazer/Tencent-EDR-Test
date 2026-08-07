"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import type { ChangeEvent } from "react";
import { capabilityCatalog } from "./control-plane";

type ApiState = "checking" | "online" | "offline";
type RunStatus = "queued" | "running" | "cancelling" | "completed" | "completed_with_errors" | "cancelled" | "failed";
type ValidationStatus = "PASS" | "PARTIAL" | "FAIL" | "INCONCLUSIVE" | "NOT_COMPARED";

type ApiCapability = {
  capability_id: string;
  version: string;
  name_zh: string;
  name_en: string;
  risk_level: string;
  required_privilege: string;
  programs: string[];
};

type ApiMapping = {
  profile_id: string;
  vendor: string;
  product: string;
  description: string;
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
  started_at_utc: string;
  ended_at_utc?: string;
  database_name?: string;
  local_export_available: boolean;
  error?: string;
};

type ValidationEntry = {
  case_run_id: string;
  capability_id: string;
  local_status: string;
  validation_status: ValidationStatus;
  export_coverage: string;
  candidate_count: number;
  warnings?: string[];
};

type ValidationResult = {
  comparison_id: string;
  compared_at_utc: string;
  summary: Record<Lowercase<ValidationStatus>, number>;
  conclusion: {
    verdict: Exclude<ValidationStatus, "NOT_COMPARED">;
    label_zh: string;
    statement_zh: string;
    total_capabilities: number;
    compared_capabilities: number;
    pass_rate: number | null;
    passed_capability_ids: string[];
    gap_capability_ids: string[];
    uncertain_capability_ids: string[];
  };
  capabilities: ValidationEntry[];
};

type FileChoice = {
  file: File | null;
  state: "empty" | "ready";
  name: string;
  detail: string;
};

const emptyFile: FileChoice = { file: null, state: "empty", name: "尚未选择文件", detail: "等待导入" };
const allCapabilities = capabilityCatalog.flatMap((category) =>
  category.capabilities.map((capability) => ({ ...capability, categoryId: category.id })),
);
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
    let message = `本地 API 返回 HTTP ${response.status}`;
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
  if (!response.ok) {
    let message = `报告下载失败（HTTP ${response.status}）`;
    try {
      const payload = (await response.json()) as { error?: string };
      if (payload.error) message = payload.error;
    } catch {
      // 非 JSON 错误响应保留状态码。
    }
    throw new Error(message);
  }
  return response.blob();
}

function isActive(status: RunStatus): boolean {
  return status === "queued" || status === "running" || status === "cancelling";
}

function formatTime(value?: string): string {
  if (!value) return "—";
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(new Date(value));
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function runStatusLabel(status: RunStatus): string {
  return {
    queued: "等待执行",
    running: "正在执行",
    cancelling: "正在取消",
    completed: "本地通过",
    completed_with_errors: "带错误完成",
    cancelled: "已取消",
    failed: "执行失败",
  }[status];
}

function validationStatusLabel(status: ValidationStatus): string {
  return {
    PASS: "通过",
    PARTIAL: "部分通过",
    FAIL: "失败",
    INCONCLUSIVE: "无法判定",
    NOT_COMPARED: "未比较",
  }[status];
}

function statusDot(status: RunStatus): string {
  if (status === "completed") return "green";
  if (status === "queued" || status === "running" || status === "cancelling") return "blue";
  return "red";
}

function downloadJson(fileName: string, value: unknown) {
  const blob = new Blob([JSON.stringify(value, null, 2)], { type: "application/json;charset=utf-8" });
  downloadBlob(fileName, blob);
}

function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

export function LiveControlPlane() {
  const [apiState, setApiState] = useState<ApiState>("checking");
  const [apiVersion, setApiVersion] = useState("—");
  const [availableCapabilities, setAvailableCapabilities] = useState<ApiCapability[]>([]);
  const [mappings, setMappings] = useState<ApiMapping[]>([]);
  const [mappingId, setMappingId] = useState("");
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [runName, setRunName] = useState("基础遥测验证 #001");
  const [environment, setEnvironment] = useState("Windows 11 · 实验室");
  const [allowHighRisk, setAllowHighRisk] = useState(false);
  const [activeRun, setActiveRun] = useState<ApiRun | null>(null);
  const [recentRuns, setRecentRuns] = useState<ApiRun[]>([]);
  const [cloudFile, setCloudFile] = useState<FileChoice>(emptyFile);
  const [manifestFile, setManifestFile] = useState<FileChoice>(emptyFile);
  const [localFile, setLocalFile] = useState<FileChoice>(emptyFile);
  const [comparison, setComparison] = useState<ValidationResult | null>(null);
  const [isComparing, setIsComparing] = useState(false);
  const [notice, setNotice] = useState("正在连接本地 Runner API…");

  const availableIds = useMemo(
    () => new Set(availableCapabilities.map((capability) => capability.capability_id)),
    [availableCapabilities],
  );
  const selectedCapabilities = useMemo(
    () => allCapabilities.filter((capability) => selectedIds.includes(capability.id)),
    [selectedIds],
  );
  const hasHighRisk = selectedCapabilities.some((capability) => capability.risk === "L2" || capability.risk === "L3");
  const selectedRisk = selectedCapabilities.reduce((maximum, capability) =>
    Number(capability.risk.slice(1)) > Number(maximum.slice(1)) ? capability.risk : maximum, "L0");

  const refreshRuns = useCallback(async () => {
    const runs = await apiRequest<ApiRun[]>("/runs");
    setRecentRuns(runs);
    return runs;
  }, []);

  const connect = useCallback(async () => {
    try {
      const [health, discovered, mappingList, runs] = await Promise.all([
        apiRequest<{ version: string }>("/health"),
        apiRequest<ApiCapability[]>("/capabilities"),
        apiRequest<ApiMapping[]>("/mappings"),
        apiRequest<ApiRun[]>("/runs"),
      ]);
      setApiVersion(health.version);
      setAvailableCapabilities(discovered);
      setMappings(mappingList);
      setMappingId((current) => current || mappingList[0]?.profile_id || "");
      setRecentRuns(runs);
      setSelectedIds((current) => current.length ? current.filter((id) => discovered.some((item) => item.capability_id === id)) : discovered.map((item) => item.capability_id));
      setApiState("online");
      setNotice(`本地 Runner API 已连接，发现 ${discovered.length} 项可执行样本。`);
    } catch (error) {
      setApiState("offline");
      setNotice(error instanceof Error ? `无法连接本地 API：${error.message}` : "无法连接本地 API。");
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
      void apiRequest<ApiRun>(`/runs/${operationId}`)
        .then((run) => {
          setActiveRun(run);
          if (!isActive(run.status)) {
            setNotice(`轮次 ${run.run_id ?? run.operation_id}：${run.phase}`);
            void refreshRuns();
          }
        })
        .catch((error: unknown) => setNotice(error instanceof Error ? error.message : "轮次状态刷新失败。"));
    }, 750);
    return () => window.clearInterval(timer);
  }, [activeRun, refreshRuns]);

  function toggleCapability(capabilityId: string) {
    if (!availableIds.has(capabilityId)) return;
    setSelectedIds((ids) => ids.includes(capabilityId) ? ids.filter((id) => id !== capabilityId) : [...ids, capabilityId]);
  }

  async function startRun() {
    if (apiState !== "online") { setNotice("请先启动并连接本地 Runner API。"); return; }
    if (!selectedIds.length) { setNotice("请至少选择一项已有样本的能力。"); return; }
    if (hasHighRisk && !allowHighRisk) { setNotice("所选能力包含 L2 项，请先确认高风险执行。"); return; }
    try {
      setComparison(null);
      const run = await apiRequest<ApiRun>("/runs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          capability_ids: selectedIds,
          name: runName.trim() || "未命名验证轮次",
          environment_id: environment,
          allow_high_risk: allowHighRisk,
        }),
      });
      setActiveRun(run);
      setNotice(`已提交轮次 ${run.operation_id}，Runner 正在执行。`);
      void refreshRuns();
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "轮次创建失败。");
    }
  }

  async function cancelRun() {
    if (!activeRun || !isActive(activeRun.status)) return;
    try {
      const run = await apiRequest<ApiRun>(`/runs/${activeRun.operation_id}/cancel`, { method: "POST" });
      setActiveRun(run);
      setNotice("已请求取消，Runner 正在清理子进程并封存数据库。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "取消轮次失败。");
    }
  }

  async function downloadLocalExport(run: ApiRun) {
    try {
      const response = await fetch(`${apiRoot}/runs/${run.operation_id}/local-export`, { headers: apiHeaders() });
      if (!response.ok) throw new Error(`下载失败：HTTP ${response.status}`);
      downloadBlob(`${run.run_id ?? run.operation_id}-local-run.json`, await response.blob());
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "下载本地结果失败。");
    }
  }

  function chooseFile(setter: (value: FileChoice) => void, event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (file) setter({ file, state: "ready", name: file.name, detail: `${formatBytes(file.size)} · 将发送至本机比较 API` });
    event.target.value = "";
  }

  async function compare() {
    if (!cloudFile.file) { setNotice("请先选择从 EDR 平台导出的云端事件 JSON/JSONL。"); return; }
    if (!localFile.file && !activeRun?.local_export_available) { setNotice("请先完成一轮本地测试，或选择本地运行 JSON。"); return; }
    if (!mappingId) { setNotice("本地仓库没有可用的字段映射配置。"); return; }

    const form = new FormData();
    form.append("cloud_file", cloudFile.file);
    form.append("mapping_id", mappingId);
    if (manifestFile.file) form.append("cloud_manifest", manifestFile.file);
    if (localFile.file) form.append("local_file", localFile.file);
    else if (activeRun) form.append("operation_id", activeRun.operation_id);

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
      const blob = await apiDownload(`/reports/${comparison.comparison_id}/conclusion`);
      downloadBlob(`validation-${comparison.comparison_id}-conclusion.md`, blob);
      setNotice("中文验证结论已导出。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "下载中文结论失败。");
    }
  }

  return (
    <div className="app-shell">
      <aside className="sidebar" aria-label="主导航">
        <div className="brand-block"><div className="brand-mark" aria-hidden="true">E</div><div><p className="brand-name">EDR 验证平台</p><p className="brand-subtitle">LOCAL CONTROL PLANE · {apiVersion}</p></div></div>
        <nav className="side-nav">
          <a className="nav-item active" href="#overview"><span>01</span>工作台</a>
          <a className="nav-item" href="#capabilities"><span>02</span>能力编排</a>
          <a className="nav-item" href="#compare"><span>03</span>离线比较</a>
          <a className="nav-item" href="#runs"><span>04</span>运行记录</a>
        </nav>
        <div className="sidebar-note">
          <div className="status-line"><span className={`status-dot ${apiState === "online" ? "green" : apiState === "checking" ? "amber" : "red"}`} />{apiState === "online" ? "Runner API 已连接" : apiState === "checking" ? "正在连接 Runner" : "Runner API 未连接"}</div>
          <p>页面只连接 127.0.0.1。用户导入的日志保存在本机忽略目录，不接入 EDR 云端接口。</p>
          {apiState === "offline" && <button className="sidebar-button" type="button" onClick={() => void connect()}>重新连接</button>}
        </div>
      </aside>

      <main className="main-content">
        <header className="topbar" id="overview">
          <div><p className="eyebrow">本地能力验证 / 项目入口</p><h1>验证工作台</h1><p className="page-description">编排 Windows 能力程序，管理每轮 SQLite 制品，并比较用户导入的 EDR 日志。</p></div>
          <div className="topbar-actions"><span className="mode-chip"><span className={`status-dot ${apiState === "online" ? "green" : "red"}`} />{apiState === "online" ? "本地服务在线" : "本地服务离线"}</span></div>
        </header>

        <section className="metric-row" aria-label="平台摘要">
          <article className="metric-card"><p>能力目录</p><strong>{allCapabilities.length}</strong><span>{availableCapabilities.length} 项已有可执行样本</span></article>
          <article className="metric-card"><p>本轮已选</p><strong>{selectedIds.length}</strong><span>最高风险 {selectedRisk}</span></article>
          <article className="metric-card"><p>运行数据库</p><strong className="metric-text">{activeRun?.database_name ?? "待创建"}</strong><span>一轮一个 SQLite</span></article>
          <article className="metric-card"><p>比较状态</p><strong className="metric-text">{comparison ? "已完成" : "待导入"}</strong><span>{comparison ? `${comparison.summary.pass} 通过 · ${comparison.summary.fail} 失败` : "本地事实 + 云端日志"}</span></article>
        </section>

        <div className="workspace-grid">
          <section className="panel capability-panel" id="capabilities">
            <div className="panel-heading"><div><p className="section-index">01 / 能力编排</p><h2>选择本轮能力</h2><p className="panel-description">完整目录共 53 项；只有已发现 capability.json 和程序包的能力可执行。</p></div><div className="inline-actions"><button type="button" className="text-button" onClick={() => setSelectedIds([...availableIds])}>全选可用</button><button type="button" className="text-button" onClick={() => setSelectedIds([])}>清空</button></div></div>
            <div className="capability-groups">
              {capabilityCatalog.map((category) => (
                <fieldset className="capability-group" key={category.id}>
                  <legend><span className="category-name-zh">{category.nameZh}</span><span className="category-name-en" lang="en">{category.nameEn}</span></legend>
                  <div className="capability-list">
                    {category.capabilities.map((capability) => {
                      const selected = selectedIds.includes(capability.id);
                      const available = availableIds.has(capability.id);
                      return (
                        <label className={`capability-item ${selected ? "selected" : ""} ${available ? "available" : "unavailable"}`} key={capability.id} aria-disabled={!available}>
                          <input type="checkbox" checked={selected} disabled={!available} onChange={() => toggleCapability(capability.id)} />
                          <span className="checkbox-ui" aria-hidden="true" />
                          <span className="capability-copy">
                            <span className="capability-title-row"><strong><span className="capability-name-zh">{capability.nameZh}</span><span className="capability-name-en" lang="en">{capability.nameEn}</span></strong><span className={`risk-badge ${capability.risk.toLowerCase()}`}>{capability.risk}</span></span>
                            <span className="capability-description">{capability.id}</span>
                            <span className="program-line">{available ? capability.programs : "样本待实现"}</span>
                          </span>
                        </label>
                      );
                    })}
                  </div>
                </fieldset>
              ))}
            </div>
          </section>

          <section className="panel run-panel">
            <div className="panel-heading"><div><p className="section-index">02 / 测试轮次</p><h2>启动真实 Runner</h2></div><span className="line-badge">串行执行</span></div>
            <div className="form-stack">
              <label className="field-label">轮次名称<input value={runName} onChange={(event) => setRunName(event.target.value)} /></label>
              <label className="field-label">测试环境<select value={environment} onChange={(event) => setEnvironment(event.target.value)}><option>Windows 11 · 实验室</option><option>Windows Server 2022 · 实验室</option></select></label>
              <div className="plan-summary"><div><span>执行模式</span><strong>顺序执行</strong></div><div><span>能力数量</span><strong>{selectedIds.length} 项</strong></div><div><span>风险上限</span><strong>{selectedRisk}</strong></div><div><span>数据文件</span><strong>独立 .db</strong></div></div>
              {hasHighRisk && <label className="risk-confirm"><input type="checkbox" checked={allowHighRisk} onChange={(event) => setAllowHighRisk(event.target.checked)} /><span>我确认在隔离测试机中执行 L2 高风险样本</span></label>}
              <button className="primary-button" type="button" onClick={() => void startRun()} disabled={apiState !== "online" || Boolean(activeRun && isActive(activeRun.status))}>{activeRun && isActive(activeRun.status) ? "轮次执行中" : "启动本轮测试"}<span aria-hidden="true">→</span></button>
            </div>
            <div className="run-status" aria-live="polite">
              {activeRun ? <>
                <div className="run-status-head"><div><span className={`status-dot ${statusDot(activeRun.status)}`} /><strong>{activeRun.phase}</strong></div><span>{activeRun.progress}%</span></div>
                <div className="progress-track"><span style={{ width: `${activeRun.progress}%` }} /></div>
                <dl className="run-details"><div><dt>Operation ID</dt><dd>{activeRun.operation_id}</dd></div><div><dt>Run ID</dt><dd>{activeRun.run_id ?? "执行后生成"}</dd></div><div><dt>数据库</dt><dd>{activeRun.database_name ?? "执行后生成"}</dd></div></dl>
                <div className="run-actions">{isActive(activeRun.status) && <button className="danger-button" type="button" onClick={() => void cancelRun()}>取消并清理</button>}{activeRun.local_export_available && <button className="secondary-button" type="button" onClick={() => void downloadLocalExport(activeRun)}>下载本地结果</button>}</div>
                {activeRun.error && <p className="api-error">{activeRun.error}</p>}
              </> : <div className="empty-state"><span className="empty-glyph" aria-hidden="true">＋</span><p>选择已有样本的能力并创建测试轮次</p><span>Runner 将真实调度 EXE，并写入独立 SQLite 文件</span></div>}
            </div>
          </section>
        </div>

        <section className="panel compare-panel" id="compare">
          <div className="panel-heading compare-heading"><div><p className="section-index">03 / 离线比较</p><h2>导入结果并验证</h2><p className="panel-description">云端日志由用户从 EDR 平台导出后导入；本机后端按映射配置和 BASELINE 统一比较。</p></div><button className="primary-button compact" type="button" onClick={() => void compare()} disabled={isComparing || apiState !== "online"}>{isComparing ? "正在比较" : "开始比较"}<span aria-hidden="true">→</span></button></div>
          <label className="field-label mapping-field">字段映射<select value={mappingId} onChange={(event) => setMappingId(event.target.value)}>{mappings.map((mapping) => <option value={mapping.profile_id} key={mapping.profile_id}>{mapping.vendor} {mapping.product} · {mapping.profile_id}</option>)}</select></label>
          <div className="upload-grid">
            <FileSlot id="local-file" step="A" title="本地运行 JSON" hint={activeRun?.local_export_available ? `可选；默认使用轮次 ${activeRun.run_id ?? activeRun.operation_id}` : "可选择历史 local-run.json"} choice={localFile} onChange={(event) => chooseFile(setLocalFile, event)} />
            <FileSlot id="cloud-file" step="B" title="EDR 云端事件" hint="支持 JSON 数组或 JSONL" choice={cloudFile} required onChange={(event) => chooseFile(setCloudFile, event)} />
            <FileSlot id="manifest-file" step="C" title="云端导出清单" hint="用于证明主机与时间范围完整" choice={manifestFile} onChange={(event) => chooseFile(setManifestFile, event)} />
          </div>
          <div className="comparison-result" aria-live="polite">
            {comparison ? <>
              <div className={`conclusion-card ${comparison.conclusion.verdict.toLowerCase()}`}>
                <div><span>总体结论</span><strong>{comparison.conclusion.label_zh}</strong><em>{comparison.conclusion.pass_rate === null ? "通过率不可计算" : `完整通过率 ${(comparison.conclusion.pass_rate * 100).toFixed(1)}%`}</em></div>
                <p>{comparison.conclusion.statement_zh}</p>
              </div>
              <div className="result-summary"><div><span>通过</span><strong className="pass-text">{comparison.summary.pass}</strong></div><div><span>部分通过</span><strong>{comparison.summary.partial}</strong></div><div><span>失败</span><strong className="fail-text">{comparison.summary.fail}</strong></div><div><span>无法判定</span><strong>{comparison.summary.inconclusive}</strong></div></div>
              <div className="result-list">{comparison.capabilities.map((entry) => { const definition = allCapabilities.find((item) => item.id === entry.capability_id); return <div className="result-row" key={entry.case_run_id}><span className={`result-status ${entry.validation_status.toLowerCase()}`}>{validationStatusLabel(entry.validation_status)}</span><div><strong>{definition?.nameZh ?? entry.capability_id}</strong><p>{entry.warnings?.join("；") || `导出覆盖：${entry.export_coverage}`}</p></div><span className="candidate-count">{entry.candidate_count} 候选</span></div>; })}</div>
              <div className="result-actions"><button className="secondary-button" type="button" onClick={() => downloadJson(`validation-${comparison.comparison_id}.json`, comparison)}>下载验证结果 JSON</button><button className="secondary-button" type="button" onClick={() => void downloadConclusion()}>下载中文结论 Markdown</button></div>
            </> : <div className="result-placeholder"><span className="bracket" aria-hidden="true">[ ]</span><div><strong>等待比较</strong><p>完成本地轮次并导入云端 JSON 后，系统将按 BASELINE 关联本地事实与云端事件。</p></div></div>}
          </div>
        </section>

        <section className="panel runs-panel" id="runs">
          <div className="panel-heading"><div><p className="section-index">04 / 运行记录</p><h2>本机最近轮次</h2></div><button className="text-button" type="button" onClick={() => void refreshRuns()}>刷新</button></div>
          {recentRuns.length ? <div className="table-wrap"><table><thead><tr><th>轮次</th><th>能力</th><th>开始时间</th><th>本地状态</th><th>SQLite / 操作</th></tr></thead><tbody>{recentRuns.map((run) => <tr key={run.operation_id}><td><strong>{run.name}</strong><span className="table-id">{run.run_id ?? run.operation_id}</span></td><td>{run.capability_ids.length} 项</td><td>{formatTime(run.started_at_utc)}</td><td><span className="table-status"><span className={`status-dot ${statusDot(run.status)}`} />{runStatusLabel(run.status)}</span></td><td>{run.local_export_available ? <button className="text-button" type="button" onClick={() => setActiveRun(run)}>用于比较</button> : <span className="mono-cell">{run.database_name ?? "—"}</span>}</td></tr>)}</tbody></table></div> : <div className="table-empty">本机还没有测试轮次。</div>}
        </section>

        <footer className="footer-line"><span>EDR CAPABILITY VALIDATION</span><span>本地优先 · 离线比较 · 证据可追溯</span></footer>
      </main>
      <div className="toast" role="status" aria-live="polite">{notice}</div>
    </div>
  );
}

function FileSlot({ id, step, title, hint, choice, required = false, onChange }: { id: string; step: string; title: string; hint: string; choice: FileChoice; required?: boolean; onChange: (event: ChangeEvent<HTMLInputElement>) => void }) {
  return <label className={`file-slot ${choice.state}`} htmlFor={id}><input id={id} type="file" accept=".json,.jsonl,application/json" onChange={onChange} /><span className="file-step">{step}</span><span className="file-copy"><span className="file-title-row"><strong>{title}</strong><em>{required ? "必需" : "可选"}</em></span><span className="file-hint">{hint}</span><span className="file-name">{choice.name}</span><span className="file-summary">{choice.detail}</span></span><span className="file-action">选择文件</span></label>;
}
