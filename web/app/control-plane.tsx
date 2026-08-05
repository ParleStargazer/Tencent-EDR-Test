"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import type { ChangeEvent } from "react";

type RiskLevel = "L0" | "L1";
type CapabilityCategory = "进程" | "文件" | "注册表" | "网络";

type Capability = {
  id: string;
  name: string;
  description: string;
  category: CapabilityCategory;
  risk: RiskLevel;
  programs: string;
};

type ActiveRun = {
  id: string;
  name: string;
  capabilityIds: string[];
  nonce: string;
  status: "running" | "completed";
  progress: number;
  phase: string;
  startedAt: string;
  dbName: string;
};

type UploadState = {
  name: string;
  size: string;
  summary: string;
  state: "empty" | "ready" | "error";
};

type ValidationStatus = "PASS" | "PARTIAL" | "FAIL" | "INCONCLUSIVE" | "NOT_COMPARED";

type ValidationEntry = {
  capabilityId: string;
  capabilityName: string;
  status: ValidationStatus;
  message: string;
  candidateCount: number;
  score: number | null;
};

type ComparisonResult = {
  comparedAt: string;
  runId: string;
  entries: ValidationEntry[];
  counts: Record<ValidationStatus, number>;
};

type JsonRecord = Record<string, unknown>;

const capabilities: Capability[] = [
  {
    id: "win.process.create",
    name: "进程创建",
    description: "验证 Actor 创建 Target 的父子进程遥测。",
    category: "进程",
    risk: "L0",
    programs: "Controller · Actor · Target",
  },
  {
    id: "win.process.terminate",
    name: "进程退出",
    description: "记录目标进程退出、退出码与时间。",
    category: "进程",
    risk: "L0",
    programs: "Controller · Actor",
  },
  {
    id: "win.process.parent-child",
    name: "父子关系",
    description: "校验三层进程链和命令行关联。",
    category: "进程",
    risk: "L0",
    programs: "Controller · Actor · Target",
  },
  {
    id: "win.file.create",
    name: "文件创建",
    description: "在专用工作区创建带唯一标记的文件。",
    category: "文件",
    risk: "L0",
    programs: "Controller · Actor",
  },
  {
    id: "win.file.modify",
    name: "文件修改",
    description: "变更内容并记录修改前后 Hash。",
    category: "文件",
    risk: "L0",
    programs: "Controller · Actor",
  },
  {
    id: "win.file.rename",
    name: "文件重命名",
    description: "记录源路径、目标路径和操作进程。",
    category: "文件",
    risk: "L0",
    programs: "Controller · Actor",
  },
  {
    id: "win.file.delete",
    name: "文件删除",
    description: "删除测试工件并验证清理状态。",
    category: "文件",
    risk: "L0",
    programs: "Controller · Actor",
  },
  {
    id: "win.registry.create",
    name: "注册表创建",
    description: "在 HKCU 专用命名空间创建 Key/Value。",
    category: "注册表",
    risk: "L1",
    programs: "Controller · Actor",
  },
  {
    id: "win.registry.modify",
    name: "注册表修改",
    description: "修改测试 Value 并保存前后值。",
    category: "注册表",
    risk: "L1",
    programs: "Controller · Actor",
  },
  {
    id: "win.registry.delete",
    name: "注册表删除",
    description: "删除测试 Key 并确认无残留。",
    category: "注册表",
    risk: "L1",
    programs: "Controller · Actor",
  },
  {
    id: "win.network.dns",
    name: "DNS 查询",
    description: "解析内部测试域名并记录查询信息。",
    category: "网络",
    risk: "L0",
    programs: "Controller · Actor",
  },
  {
    id: "win.network.tcp",
    name: "TCP 连接",
    description: "连接回环测试服务并记录四元组。",
    category: "网络",
    risk: "L0",
    programs: "Controller · Actor · Helper",
  },
  {
    id: "win.network.http",
    name: "HTTP 请求",
    description: "向本地服务发送带 nonce 的请求。",
    category: "网络",
    risk: "L0",
    programs: "Controller · Actor · Helper",
  },
];

const categoryOrder: CapabilityCategory[] = ["进程", "文件", "注册表", "网络"];

const initialUploads: UploadState = {
  name: "尚未选择文件",
  size: "—",
  summary: "等待导入",
  state: "empty",
};

const emptyCounts: Record<ValidationStatus, number> = {
  PASS: 0,
  PARTIAL: 0,
  FAIL: 0,
  INCONCLUSIVE: 0,
  NOT_COMPARED: 0,
};

function isRecord(value: unknown): value is JsonRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function asString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

function asNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatLocalTime(date: Date): string {
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(date);
}

function normalizePath(value: unknown): string {
  return typeof value === "string"
    ? value.trim().replaceAll("/", "\\").replace(/^"|"$/g, "").toLowerCase()
    : "";
}

function parseJsonContainer(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    const lines = text
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean);
    if (lines.length === 0) throw new Error("文件内容为空");
    return lines.map((line) => JSON.parse(line) as unknown);
  }
}

function downloadJson(fileName: string, value: unknown) {
  const blob = new Blob([JSON.stringify(value, null, 2)], {
    type: "application/json;charset=utf-8",
  });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

function statusLabel(status: ValidationStatus): string {
  const labels: Record<ValidationStatus, string> = {
    PASS: "通过",
    PARTIAL: "部分通过",
    FAIL: "失败",
    INCONCLUSIVE: "无法判定",
    NOT_COMPARED: "未比较",
  };
  return labels[status];
}

export function ControlPlane() {
  const [selectedIds, setSelectedIds] = useState<string[]>([
    "win.process.create",
    "win.file.create",
    "win.registry.modify",
    "win.network.tcp",
  ]);
  const [runName, setRunName] = useState("基础遥测验证 #001");
  const [environment, setEnvironment] = useState("Windows 11 · 实验室");
  const [activeRun, setActiveRun] = useState<ActiveRun | null>(null);
  const [notice, setNotice] = useState("前端控制面已就绪，Runner 接口待接入。");
  const [localUpload, setLocalUpload] = useState<UploadState>(initialUploads);
  const [cloudUpload, setCloudUpload] = useState<UploadState>(initialUploads);
  const [manifestUpload, setManifestUpload] = useState<UploadState>(initialUploads);
  const [comparison, setComparison] = useState<ComparisonResult | null>(null);
  const [isComparing, setIsComparing] = useState(false);
  const [recentRuns, setRecentRuns] = useState<ActiveRun[]>([]);

  const localDataRef = useRef<unknown>(null);
  const cloudDataRef = useRef<unknown>(null);
  const manifestDataRef = useRef<unknown>(null);
  const recordedRunsRef = useRef<Set<string>>(new Set());

  const selectedCapabilities = useMemo(
    () => capabilities.filter((capability) => selectedIds.includes(capability.id)),
    [selectedIds],
  );

  const selectedRiskCount = selectedCapabilities.filter(
    (capability) => capability.risk === "L1",
  ).length;
  const activeRunId = activeRun?.id;
  const activeRunStatus = activeRun?.status;

  useEffect(() => {
    if (!activeRunId || activeRunStatus !== "running") return;

    const timer = window.setInterval(() => {
      setActiveRun((current) => {
        if (!current || current.status !== "running") return current;
        const nextProgress = Math.min(current.progress + 20, 100);
        const phase =
          nextProgress < 25
            ? "创建 SQLite 数据库"
            : nextProgress < 50
              ? "执行能力 Controller"
              : nextProgress < 75
                ? "收集本地事实"
                : nextProgress < 100
                  ? "清理并封存数据库"
                  : "本地轮次完成";

        return {
          ...current,
          progress: nextProgress,
          phase,
          status: nextProgress === 100 ? "completed" : "running",
        };
      });
    }, 650);

    return () => window.clearInterval(timer);
  }, [activeRunId, activeRunStatus]);

  useEffect(() => {
    if (!activeRun || activeRun.status !== "completed") return;
    if (recordedRunsRef.current.has(activeRun.id)) return;
    recordedRunsRef.current.add(activeRun.id);
    setRecentRuns((runs) => [activeRun, ...runs].slice(0, 5));
    setNotice(`轮次 ${activeRun.id} 已在控制面完成，等待真实 Runner 回传本地 JSON。`);
  }, [activeRun]);

  function toggleCapability(capabilityId: string) {
    setSelectedIds((ids) =>
      ids.includes(capabilityId)
        ? ids.filter((id) => id !== capabilityId)
        : [...ids, capabilityId],
    );
  }

  function startRun() {
    if (selectedIds.length === 0) {
      setNotice("请至少选择一项能力后再创建测试轮次。");
      return;
    }

    const timestamp = Date.now().toString(36).toUpperCase();
    const id = `RUN-${timestamp}`;
    const nonce = crypto.randomUUID().replaceAll("-", "").slice(0, 20).toUpperCase();
    setComparison(null);
    setActiveRun({
      id,
      name: runName.trim() || "未命名验证轮次",
      capabilityIds: [...selectedIds],
      nonce,
      status: "running",
      progress: 0,
      phase: "准备执行计划",
      startedAt: formatLocalTime(new Date()),
      dbName: `${id.toLowerCase()}.db`,
    });
    setNotice("已创建前端轮次。当前为控制面阶段，真实 EXE 调度将在 Runner 接口接入后启用。");
  }

  function downloadPlan() {
    if (!activeRun) return;
    downloadJson(`${activeRun.id.toLowerCase()}-plan.json`, {
      schema_version: "0.1-control-plane",
      run_id: activeRun.id,
      run_name: activeRun.name,
      environment,
      nonce: activeRun.nonce,
      execution_mode: "serial",
      database_file: activeRun.dbName,
      capabilities: activeRun.capabilityIds,
      generated_at: new Date().toISOString(),
      note: "该文件为前端执行计划，不是 Runner 生成的 local-run.json。",
    });
  }

  async function handleFile(
    kind: "local" | "cloud" | "manifest",
    event: ChangeEvent<HTMLInputElement>,
  ) {
    const file = event.target.files?.[0];
    if (!file) return;

    const update =
      kind === "local"
        ? setLocalUpload
        : kind === "cloud"
          ? setCloudUpload
          : setManifestUpload;

    try {
      const parsed = parseJsonContainer(await file.text());
      let summary = "JSON 格式有效";

      if (kind === "local") {
        if (!isRecord(parsed)) throw new Error("本地结果必须是 JSON 对象");
        const runs = Array.isArray(parsed.capabilities) ? parsed.capabilities.length : 0;
        const run = isRecord(parsed.run) ? asString(parsed.run.run_id) : null;
        summary = `${run ?? "未知 Run ID"} · ${runs} 项能力`;
        localDataRef.current = parsed;
      } else if (kind === "cloud") {
        const records = Array.isArray(parsed)
          ? parsed.filter(isRecord)
          : isRecord(parsed) && Array.isArray(parsed.events)
            ? parsed.events.filter(isRecord)
            : [];
        if (records.length === 0) throw new Error("未发现云端事件数组");
        const processCreateCount = records.filter(
          (record) => record["Action.Name"] === "ProcessCreate",
        ).length;
        summary = `${records.length} 条事件 · ${processCreateCount} 条进程创建`;
        cloudDataRef.current = records;
      } else {
        if (!isRecord(parsed)) throw new Error("导出清单必须是 JSON 对象");
        const windowValue = isRecord(parsed.query_window) ? parsed.query_window : null;
        summary = windowValue ? "查询范围已声明" : "清单有效，但未声明查询范围";
        manifestDataRef.current = parsed;
      }

      update({
        name: file.name,
        size: formatBytes(file.size),
        summary,
        state: "ready",
      });
      setNotice(`${file.name} 已在浏览器本地解析，文件内容未上传。`);
    } catch (error) {
      update({
        name: file.name,
        size: formatBytes(file.size),
        summary: error instanceof Error ? error.message : "文件解析失败",
        state: "error",
      });
      if (kind === "local") localDataRef.current = null;
      if (kind === "cloud") cloudDataRef.current = null;
      if (kind === "manifest") manifestDataRef.current = null;
    } finally {
      event.target.value = "";
    }
  }

  function compareProcessCreate(
    capabilityRun: JsonRecord,
    programs: JsonRecord[],
    cloudEvents: JsonRecord[],
    hasManifest: boolean,
  ): ValidationEntry {
    const caseRunId = asString(capabilityRun.case_run_id) ?? "unknown";
    const nonce = asString(capabilityRun.nonce) ?? "";
    const actor = programs.find(
      (program) => program.case_run_id === caseRunId && program.role === "actor",
    );
    const target = programs.find(
      (program) => program.case_run_id === caseRunId && program.role === "target",
    );

    if (!actor || !target) {
      return {
        capabilityId: "win.process.create",
        capabilityName: "进程创建",
        status: "INCONCLUSIVE",
        message: "本地 JSON 缺少 Actor 或 Target 程序事实。",
        candidateCount: 0,
        score: null,
      };
    }

    const targetPid = asNumber(target.pid);
    const actorPid = asNumber(actor.pid);
    const targetPath = normalizePath(target.executable);
    const actorPath = normalizePath(actor.executable);
    const processEvents = cloudEvents.filter(
      (event) => event["Action.Name"] === "ProcessCreate",
    );

    const scored = processEvents
      .map((event) => {
        let score = 0;
        if (targetPath && normalizePath(event["Child.FilePath"]) === targetPath) score += 4;
        if (actorPath && normalizePath(event["Parent.FilePath"]) === actorPath) score += 3;
        if (targetPid !== null && asNumber(event["Child.ProcPid"]) === targetPid) score += 2;
        if (actorPid !== null && asNumber(event["Parent.ProcPid"]) === actorPid) score += 2;
        const childCommand = asString(event["Child.ProcCmdline"]) ?? "";
        const parentCommand = asString(event["Parent.ProcCmdline"]) ?? "";
        if (nonce && childCommand.includes(nonce)) score += 4;
        if (nonce && parentCommand.includes(nonce)) score += 4;
        return { event, score };
      })
      .filter((candidate) => candidate.score > 0)
      .sort((left, right) => right.score - left.score);

    const best = scored[0];
    if (!best) {
      return {
        capabilityId: "win.process.create",
        capabilityName: "进程创建",
        status: hasManifest ? "FAIL" : "INCONCLUSIVE",
        message: hasManifest
          ? "完整导出范围内未发现可关联的进程创建事件。"
          : "未发现候选事件，且未提供导出清单，无法证明日志范围完整。",
        candidateCount: 0,
        score: null,
      };
    }

    const requiredChecks = [
      targetPid !== null && asNumber(best.event["Child.ProcPid"]) === targetPid,
      actorPid !== null && asNumber(best.event["Parent.ProcPid"]) === actorPid,
      Boolean(targetPath) && normalizePath(best.event["Child.FilePath"]) === targetPath,
      Boolean(actorPath) && normalizePath(best.event["Parent.FilePath"]) === actorPath,
    ];
    const passed = requiredChecks.filter(Boolean).length;
    const status: ValidationStatus =
      passed === requiredChecks.length ? "PASS" : best.score >= 6 ? "PARTIAL" : "FAIL";

    return {
      capabilityId: "win.process.create",
      capabilityName: "进程创建",
      status,
      message:
        status === "PASS"
          ? "Target 与 Actor 的 PID、路径和父子关系均匹配。"
          : status === "PARTIAL"
            ? `已找到高相关候选，但仅 ${passed}/${requiredChecks.length} 项关键字段一致。`
            : "候选事件的关键父子进程字段不满足 BASELINE。",
      candidateCount: scored.length,
      score: best.score,
    };
  }

  function runComparison() {
    if (!localDataRef.current || !cloudDataRef.current) {
      setNotice("请先导入本地运行 JSON 和 EDR 云端事件 JSON。");
      return;
    }
    if (!isRecord(localDataRef.current) || !Array.isArray(cloudDataRef.current)) {
      setNotice("输入文件结构不符合比较要求。");
      return;
    }

    setIsComparing(true);
    window.setTimeout(() => {
      const local = localDataRef.current;
      const cloud = cloudDataRef.current;
      if (!isRecord(local) || !Array.isArray(cloud)) return;

      const capabilityRuns = Array.isArray(local.capabilities)
        ? local.capabilities.filter(isRecord)
        : [];
      const programs = Array.isArray(local.programs) ? local.programs.filter(isRecord) : [];
      const cloudEvents = cloud.filter(isRecord);
      const hasManifest = isRecord(manifestDataRef.current);
      const entries: ValidationEntry[] = capabilityRuns.map((capabilityRun) => {
        const capabilityId = asString(capabilityRun.capability_id) ?? "unknown";
        const localStatus = asString(capabilityRun.status);
        const capabilityName =
          capabilities.find((item) => item.id === capabilityId)?.name ?? capabilityId;

        if (localStatus !== "LOCAL_PASS") {
          return {
            capabilityId,
            capabilityName,
            status: "NOT_COMPARED",
            message: `本地状态为 ${localStatus ?? "未知"}，未进入云端比较。`,
            candidateCount: 0,
            score: null,
          };
        }
        if (capabilityId === "win.process.create") {
          return compareProcessCreate(capabilityRun, programs, cloudEvents, hasManifest);
        }
        return {
          capabilityId,
          capabilityName,
          status: "INCONCLUSIVE",
          message: "该能力的云端字段映射尚未在首版控制面中实现。",
          candidateCount: 0,
          score: null,
        };
      });

      const counts = { ...emptyCounts };
      entries.forEach((entry) => {
        counts[entry.status] += 1;
      });
      const run = isRecord(local.run) ? local.run : null;
      const result: ComparisonResult = {
        comparedAt: new Date().toISOString(),
        runId: (run && asString(run.run_id)) || "unknown",
        entries,
        counts,
      };
      setComparison(result);
      setIsComparing(false);
      setNotice(`离线比较完成：${counts.PASS} 项通过，${counts.INCONCLUSIVE} 项无法判定。`);
    }, 450);
  }

  return (
    <div className="app-shell">
      <aside className="sidebar" aria-label="主导航">
        <div className="brand-block">
          <div className="brand-mark" aria-hidden="true">E</div>
          <div>
            <p className="brand-name">EDR 验证平台</p>
            <p className="brand-subtitle">CONTROL PLANE · 0.1</p>
          </div>
        </div>

        <nav className="side-nav">
          <a className="nav-item active" href="#overview"><span>01</span>工作台</a>
          <a className="nav-item" href="#capabilities"><span>02</span>能力编排</a>
          <a className="nav-item" href="#compare"><span>03</span>离线比较</a>
          <a className="nav-item" href="#runs"><span>04</span>运行记录</a>
        </nav>

        <div className="sidebar-note">
          <div className="status-line"><span className="status-dot amber" />Runner 待接入</div>
          <p>日志只在浏览器本地解析，不会上传至服务器。</p>
        </div>
      </aside>

      <main className="main-content">
        <header className="topbar" id="overview">
          <div>
            <p className="eyebrow">离线能力验证 / 实验室控制面</p>
            <h1>验证工作台</h1>
            <p className="page-description">编排本地能力程序，管理每轮 SQLite 制品，并比较用户导入的 EDR 日志。</p>
          </div>
          <div className="topbar-actions">
            <span className="mode-chip"><span className="status-dot green" />离线模式</span>
            <button className="secondary-button" type="button" onClick={() => setNotice("当前版本使用默认实验室配置。")}>环境设置</button>
          </div>
        </header>

        <section className="metric-row" aria-label="平台摘要">
          <article className="metric-card">
            <p>可选能力</p>
            <strong>{capabilities.length}</strong>
            <span>Windows 基础遥测</span>
          </article>
          <article className="metric-card">
            <p>本轮已选</p>
            <strong>{selectedIds.length}</strong>
            <span>{selectedRiskCount ? `${selectedRiskCount} 项 L1` : "全部为 L0"}</span>
          </article>
          <article className="metric-card">
            <p>运行数据库</p>
            <strong className="metric-text">{activeRun ? activeRun.dbName : "待创建"}</strong>
            <span>一轮一个 SQLite</span>
          </article>
          <article className="metric-card">
            <p>比较状态</p>
            <strong className="metric-text">{comparison ? "已完成" : "待导入"}</strong>
            <span>{comparison ? `${comparison.counts.PASS} 通过 · ${comparison.counts.FAIL} 失败` : "本地 JSON + 云端 JSON"}</span>
          </article>
        </section>

        <div className="workspace-grid">
          <section className="panel capability-panel" id="capabilities">
            <div className="panel-heading">
              <div>
                <p className="section-index">01 / 能力编排</p>
                <h2>选择本轮能力</h2>
              </div>
              <div className="inline-actions">
                <button type="button" className="text-button" onClick={() => setSelectedIds(capabilities.map((item) => item.id))}>全选</button>
                <button type="button" className="text-button" onClick={() => setSelectedIds([])}>清空</button>
              </div>
            </div>

            <div className="capability-groups">
              {categoryOrder.map((category) => (
                <fieldset className="capability-group" key={category}>
                  <legend>{category}</legend>
                  <div className="capability-list">
                    {capabilities
                      .filter((capability) => capability.category === category)
                      .map((capability) => {
                        const selected = selectedIds.includes(capability.id);
                        return (
                          <label className={`capability-item ${selected ? "selected" : ""}`} key={capability.id}>
                            <input
                              type="checkbox"
                              checked={selected}
                              onChange={() => toggleCapability(capability.id)}
                            />
                            <span className="checkbox-ui" aria-hidden="true" />
                            <span className="capability-copy">
                              <span className="capability-title-row">
                                <strong>{capability.name}</strong>
                                <span className={`risk-badge ${capability.risk.toLowerCase()}`}>{capability.risk}</span>
                              </span>
                              <span className="capability-description">{capability.description}</span>
                              <span className="program-line">{capability.programs}</span>
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
            <div className="panel-heading">
              <div>
                <p className="section-index">02 / 测试轮次</p>
                <h2>创建运行计划</h2>
              </div>
              <span className="line-badge">串行</span>
            </div>

            <div className="form-stack">
              <label className="field-label">
                轮次名称
                <input value={runName} onChange={(event) => setRunName(event.target.value)} />
              </label>
              <label className="field-label">
                测试环境
                <select value={environment} onChange={(event) => setEnvironment(event.target.value)}>
                  <option>Windows 11 · 实验室</option>
                  <option>Windows Server 2022 · 实验室</option>
                </select>
              </label>
              <div className="plan-summary">
                <div><span>执行模式</span><strong>顺序执行</strong></div>
                <div><span>能力数量</span><strong>{selectedIds.length} 项</strong></div>
                <div><span>风险上限</span><strong>{selectedRiskCount ? "L1" : "L0"}</strong></div>
                <div><span>数据文件</span><strong>独立 .db</strong></div>
              </div>
              <button className="primary-button" type="button" onClick={startRun} disabled={activeRun?.status === "running"}>
                {activeRun?.status === "running" ? "轮次执行中" : "启动本轮测试"}
                <span aria-hidden="true">→</span>
              </button>
            </div>

            <div className="run-status" aria-live="polite">
              {activeRun ? (
                <>
                  <div className="run-status-head">
                    <div>
                      <span className={`status-dot ${activeRun.status === "completed" ? "green" : "blue"}`} />
                      <strong>{activeRun.phase}</strong>
                    </div>
                    <span>{activeRun.progress}%</span>
                  </div>
                  <div className="progress-track"><span style={{ width: `${activeRun.progress}%` }} /></div>
                  <dl className="run-details">
                    <div><dt>Run ID</dt><dd>{activeRun.id}</dd></div>
                    <div><dt>数据库</dt><dd>{activeRun.dbName}</dd></div>
                    <div><dt>Nonce</dt><dd>{activeRun.nonce}</dd></div>
                  </dl>
                  <button className="secondary-button full-width" type="button" onClick={downloadPlan}>下载执行计划</button>
                </>
              ) : (
                <div className="empty-state">
                  <span className="empty-glyph" aria-hidden="true">＋</span>
                  <p>选择能力并创建第一轮测试</p>
                  <span>Runner 会为每轮生成独立 SQLite 文件</span>
                </div>
              )}
            </div>
          </section>
        </div>

        <section className="panel compare-panel" id="compare">
          <div className="panel-heading compare-heading">
            <div>
              <p className="section-index">03 / 离线比较</p>
              <h2>导入结果并验证</h2>
              <p className="panel-description">文件仅在当前浏览器中读取。首版已实现腾讯 EDR `ProcessCreate` 的离线关联。</p>
            </div>
            <button className="primary-button compact" type="button" onClick={runComparison} disabled={isComparing}>
              {isComparing ? "正在比较" : "开始比较"}<span aria-hidden="true">→</span>
            </button>
          </div>

          <div className="upload-grid">
            <FileSlot
              id="local-file"
              step="A"
              title="本地运行 JSON"
              hint="由 EdrTest.Export.exe 导出"
              upload={localUpload}
              required
              onChange={(event) => void handleFile("local", event)}
            />
            <FileSlot
              id="cloud-file"
              step="B"
              title="EDR 云端事件"
              hint="支持 JSON 数组或 JSONL"
              upload={cloudUpload}
              required
              onChange={(event) => void handleFile("cloud", event)}
            />
            <FileSlot
              id="manifest-file"
              step="C"
              title="云端导出清单"
              hint="用于证明主机与时间范围完整"
              upload={manifestUpload}
              onChange={(event) => void handleFile("manifest", event)}
            />
          </div>

          <div className="comparison-result" aria-live="polite">
            {comparison ? (
              <>
                <div className="result-summary">
                  <div><span>通过</span><strong className="pass-text">{comparison.counts.PASS}</strong></div>
                  <div><span>部分通过</span><strong>{comparison.counts.PARTIAL}</strong></div>
                  <div><span>失败</span><strong className="fail-text">{comparison.counts.FAIL}</strong></div>
                  <div><span>无法判定</span><strong>{comparison.counts.INCONCLUSIVE}</strong></div>
                </div>
                <div className="result-list">
                  {comparison.entries.map((entry) => (
                    <div className="result-row" key={`${entry.capabilityId}-${entry.message}`}>
                      <span className={`result-status ${entry.status.toLowerCase()}`}>{statusLabel(entry.status)}</span>
                      <div><strong>{entry.capabilityName}</strong><p>{entry.message}</p></div>
                      <span className="candidate-count">{entry.candidateCount} 候选{entry.score !== null ? ` · ${entry.score} 分` : ""}</span>
                    </div>
                  ))}
                </div>
                <button className="secondary-button" type="button" onClick={() => downloadJson(`validation-${comparison.runId}.json`, comparison)}>下载验证结果 JSON</button>
              </>
            ) : (
              <div className="result-placeholder">
                <span className="bracket" aria-hidden="true">[ ]</span>
                <div><strong>等待比较</strong><p>导入两份必需 JSON 后，系统将按 BASELINE 关联本地事实与云端事件。</p></div>
              </div>
            )}
          </div>
        </section>

        <section className="panel runs-panel" id="runs">
          <div className="panel-heading">
            <div>
              <p className="section-index">04 / 运行记录</p>
              <h2>当前会话</h2>
            </div>
            <span className="line-badge">{recentRuns.length} 轮</span>
          </div>
          {recentRuns.length ? (
            <div className="table-wrap">
              <table>
                <thead><tr><th>轮次</th><th>能力</th><th>开始时间</th><th>本地状态</th><th>SQLite</th></tr></thead>
                <tbody>
                  {recentRuns.map((run) => (
                    <tr key={run.id}>
                      <td><strong>{run.name}</strong><span className="table-id">{run.id}</span></td>
                      <td>{run.capabilityIds.length} 项</td>
                      <td>{run.startedAt}</td>
                      <td><span className="table-status"><span className="status-dot green" />控制面完成</span></td>
                      <td className="mono-cell">{run.dbName}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="table-empty">当前浏览器会话还没有测试轮次。</div>
          )}
        </section>

        <footer className="footer-line">
          <span>EDR CAPABILITY VALIDATION</span>
          <span>本地优先 · 离线比较 · 证据可追溯</span>
        </footer>
      </main>

      <div className="toast" role="status" aria-live="polite">{notice}</div>
    </div>
  );
}

function FileSlot({
  id,
  step,
  title,
  hint,
  upload,
  required = false,
  onChange,
}: {
  id: string;
  step: string;
  title: string;
  hint: string;
  upload: UploadState;
  required?: boolean;
  onChange: (event: ChangeEvent<HTMLInputElement>) => void;
}) {
  return (
    <label className={`file-slot ${upload.state}`} htmlFor={id}>
      <input id={id} type="file" accept=".json,.jsonl,application/json" onChange={onChange} />
      <span className="file-step">{step}</span>
      <span className="file-copy">
        <span className="file-title-row"><strong>{title}</strong>{required ? <em>必需</em> : <em>可选</em>}</span>
        <span className="file-hint">{hint}</span>
        <span className="file-name">{upload.name}</span>
        <span className="file-summary">{upload.size} · {upload.summary}</span>
      </span>
      <span className="file-action">选择文件</span>
    </label>
  );
}
