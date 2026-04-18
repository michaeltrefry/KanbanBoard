/* Kanban — React app wired to the .NET API.
   Single-file: API client + all components + root App. */

const { useState, useEffect, useMemo, useRef, useCallback } = React;

/* ============ Constants ============ */
const STATUS_ORDER = ["Backlog", "Ready", "InProgress", "Blocked", "Done"];
const STATUS_LABEL = { Backlog: "Backlog", Ready: "Ready", InProgress: "In Progress", Blocked: "Blocked", Done: "Done" };
const WORK_ITEM_TYPES = ["Story", "Issue", "Task"];
const PRIORITY_ORDER = ["Low", "Medium", "High", "Critical"];

const TWEAK_DEFAULTS = {
  theme: "ink",
  density: "comfortable",
  showAging: true,
};

/* ============ Helpers ============ */
function clsx(...xs) { return xs.filter(Boolean).join(" "); }

function shortKey(projectKey, id) {
  if (!id) return projectKey || "—";
  const hex = String(id).replace(/-/g, "").slice(0, 4).toUpperCase();
  return `${projectKey || "ITEM"}-${hex}`;
}

function daysBetween(a, b) {
  const ms = b.getTime() - a.getTime();
  return Math.max(0, Math.floor(ms / 86_400_000));
}

function agedDays(item) {
  if (!item?.updatedAtUtc) return 0;
  const d = new Date(item.updatedAtUtc);
  if (Number.isNaN(d.getTime())) return 0;
  return daysBetween(d, new Date());
}

function ageBand(d) {
  if (d >= 7) return "hot";
  if (d >= 3) return "warm";
  return "cold";
}
function ageLabel(d) {
  if (d === 0) return "today";
  if (d === 1) return "1d";
  return `${d}d`;
}

function parseLabels(str) {
  if (!str) return [];
  return String(str).split(",").map(s => s.trim()).filter(Boolean);
}
function labelsToString(arr) {
  return (arr || []).map(s => s.trim()).filter(Boolean).join(",");
}

function formatDate(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

/* ============ API Client ============ */
async function fetchJson(input, init) {
  const res = await fetch(input, {
    headers: { "Content-Type": "application/json" },
    ...init,
  });
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.message) message = body.message;
    } catch { /* ignore */ }
    const err = new Error(message);
    err.status = res.status;
    throw err;
  }
  if (res.status === 204) return null;
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

const api = {
  listProjects: (includeArchived = false) =>
    fetchJson(`/api/projects${includeArchived ? "?includeArchived=true" : ""}`),
  getProjectBoard: (projectId, { includeArchivedEpics = false } = {}) =>
    fetchJson(`/api/projects/${projectId}${includeArchivedEpics ? "?includeArchivedEpics=true" : ""}`),
  createProject: (payload) =>
    fetchJson(`/api/projects`, { method: "POST", body: JSON.stringify(payload) }),
  updateProject: (id, payload) =>
    fetchJson(`/api/projects/${id}`, { method: "PUT", body: JSON.stringify(payload) }),
  deleteProject: (id) =>
    fetchJson(`/api/projects/${id}`, { method: "DELETE" }),

  listProjectEpics: (projectId, includeArchived = false) =>
    fetchJson(`/api/projects/${projectId}/epics${includeArchived ? "?includeArchived=true" : ""}`),
  createEpic: (projectId, payload) =>
    fetchJson(`/api/projects/${projectId}/epics`, { method: "POST", body: JSON.stringify(payload) }),
  updateEpic: (id, payload) =>
    fetchJson(`/api/epics/${id}`, { method: "PUT", body: JSON.stringify(payload) }),
  deleteEpic: (id) =>
    fetchJson(`/api/epics/${id}`, { method: "DELETE" }),

  listEpicDocuments: (epicId) =>
    fetchJson(`/api/epics/${epicId}/documents`),
  createEpicDocument: (epicId, payload) =>
    fetchJson(`/api/epics/${epicId}/documents`, { method: "POST", body: JSON.stringify(payload) }),
  updateEpicDocument: (id, payload) =>
    fetchJson(`/api/epic-documents/${id}`, { method: "PUT", body: JSON.stringify(payload) }),

  createWorkItem: (payload) =>
    fetchJson(`/api/items`, { method: "POST", body: JSON.stringify(payload) }),
  updateWorkItem: (id, payload) =>
    fetchJson(`/api/items/${id}`, { method: "PUT", body: JSON.stringify(payload) }),
  moveWorkItem: (id, status, order) =>
    fetchJson(`/api/items/${id}/move`, { method: "POST", body: JSON.stringify({ status, order }) }),
  deleteWorkItem: (id) =>
    fetchJson(`/api/items/${id}`, { method: "DELETE" }),
};

/* ============ Feedback hook ============ */
function useFeedback() {
  const [msg, setMsg] = useState(null);
  const timer = useRef(null);
  const show = useCallback((text, tone = "error") => {
    clearTimeout(timer.current);
    setMsg({ text, tone });
    timer.current = setTimeout(() => setMsg(null), 4500);
  }, []);
  const clear = useCallback(() => { clearTimeout(timer.current); setMsg(null); }, []);
  useEffect(() => () => clearTimeout(timer.current), []);
  return { msg, show, clear };
}

function FeedbackBar({ msg, onClose }) {
  if (!msg) return null;
  return (
    <div className={clsx("feedback", msg.tone === "success" && "success")} role="status">
      <span>{msg.text}</span>
      <button onClick={onClose} aria-label="Dismiss">×</button>
    </div>
  );
}

/* ============ Sidebar ============ */
function BrandMark() {
  return (
    <div className="brand">
      <div className="brand-mark">K</div>
      <div className="brand-text">
        <span className="name">Kanban</span>
        <span className="sub">Single-dev workspace</span>
      </div>
    </div>
  );
}

function Sidebar({ projects, currentProjectId, setProjectId, onNewProject, showArchived, setShowArchived }) {
  return (
    <aside className="sidebar">
      <BrandMark />
      <div className="side-section">
        <div className="side-heading">
          <span>Projects</span>
          <button className="icon-btn" title="New project" aria-label="New project" onClick={onNewProject}>+</button>
        </div>
        {projects.length === 0 && (
          <div className="docs-empty" style={{padding:"8px 10px"}}>No projects yet. Click + to create one.</div>
        )}
        {projects.map(p => (
          <div
            key={p.id}
            className={clsx("proj-item", currentProjectId === p.id && "active")}
            onClick={() => setProjectId(p.id)}
          >
            <span className="proj-key">{p.key}</span>
            <span className="proj-name">
              {p.name}
              {p.isArchived && <span className="archived-tag" style={{marginLeft:6}}>archived</span>}
            </span>
            <span className="proj-count">{p.openItems ?? 0}</span>
          </div>
        ))}
        <label className="side-toggle">
          <input type="checkbox" checked={showArchived} onChange={e => setShowArchived(e.target.checked)} />
          Show archived
        </label>
      </div>

      <div className="spacer"/>
      <div style={{fontSize:11, color:"var(--muted)", padding:"8px 6px", borderTop:"1px solid var(--line)"}}>
        <span className="kbd">⌘K</span> &nbsp;quick add
      </div>
    </aside>
  );
}

/* ============ Topbar ============ */
function Topbar({ project, epic, onOpenPalette, onOpenTweaks, onNewItem }) {
  return (
    <header className="topbar">
      <div className="crumbs">
        {project && <>
          <span className="crumb-key">{project.key}</span>
          <span className="crumb-title">{project.name}</span>
        </>}
        {epic && <>
          <span className="sep">/</span>
          <span className="crumb-title" style={{fontSize:16, color:"var(--ink-2)"}}>{epic.name}</span>
        </>}
      </div>
      <div className="top-actions">
        <button className="btn ghost" onClick={onOpenPalette} disabled={!project}>
          <span>Search</span>
          <span className="kbd">⌘K</span>
        </button>
        <button className="btn" onClick={onNewItem} disabled={!project}>+ Add story</button>
        <button className="btn ghost" onClick={onOpenTweaks} title="Tweaks">⚙</button>
      </div>
    </header>
  );
}

/* ============ Epic strip ============ */
function EpicStrip({ epics, items, currentEpicId, setEpicId, onNewEpic }) {
  const byEpic = useMemo(() => {
    const m = {};
    for (const it of items) {
      const id = it.epicId || "__none";
      if (!m[id]) m[id] = { Done: 0, InProgress: 0, Blocked: 0, Ready: 0, Backlog: 0, total: 0 };
      m[id][it.status]++;
      m[id].total++;
    }
    return m;
  }, [items]);

  return (
    <section className="epic-strip">
      <div className="epic-strip-header">
        <div className="epic-strip-title">
          <span className="epic-strip-eyebrow">Epics</span>
        </div>
        <div className="epic-strip-actions">
          <button className="btn small ghost" onClick={onNewEpic}>+ New epic</button>
        </div>
      </div>
      <div className="epic-rail">
        <div
          className={clsx("epic-card all-view", !currentEpicId && "active")}
          onClick={() => setEpicId(null)}
          style={{flex:"0 0 180px"}}
        >
          <div className="epic-card-row">
            <div className="epic-card-name">All project work</div>
          </div>
          <div className="epic-card-foot">
            <span>{items.length} stories total</span>
          </div>
        </div>
        {epics.map(e => {
          const c = byEpic[e.id] || { Done: 0, InProgress: 0, Blocked: 0, Ready: 0, Backlog: 0, total: 0 };
          const pct = (k) => c.total ? (c[k] / c.total) * 100 : 0;
          return (
            <div
              key={e.id}
              className={clsx("epic-card", currentEpicId === e.id && "active")}
              onClick={() => setEpicId(e.id)}
            >
              <div className="epic-card-row">
                <div className="epic-card-name">
                  {e.name}
                  {e.isArchived && <span className="archived-tag" style={{marginLeft:6}}>archived</span>}
                </div>
                <div className="epic-card-target">{formatDate(e.updatedAtUtc)}</div>
              </div>
              <div className="epic-progress" title={`${c.Done}/${c.total} done`}>
                <span className="seg-done"     style={{flex: pct("Done")}}/>
                <span className="seg-progress" style={{flex: pct("InProgress")}}/>
                <span className="seg-blocked"  style={{flex: pct("Blocked")}}/>
                <span className="seg-ready"    style={{flex: pct("Ready")}}/>
                <span className="seg-backlog"  style={{flex: pct("Backlog")}}/>
              </div>
              <div className="epic-card-foot">
                <span><span className="count-done">{c.Done}</span> / {c.total} done</span>
                {c.Blocked > 0 && <span style={{color:"var(--status-blocked-ink)"}}>{c.Blocked} blocked</span>}
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}

/* ============ Epic detail + docs rail ============ */
function EpicDetail({ epic, project, items, docs, onOpenDoc, onNewDoc, onEditEpic, onArchiveEpic, onDeleteEpic }) {
  const count = items.length;
  const done = items.filter(s => s.status === "Done").length;
  const wip = items.filter(s => s.status === "InProgress").length;
  const blk = items.filter(s => s.status === "Blocked").length;
  const est = items.reduce((a, s) => a + (s.estimate || 0), 0);

  if (!epic) {
    return (
      <section className="epic-detail">
        <div className="epic-headline">
          <h1 className="epic-title">{project ? `${project.name} — everything open` : "Select a project"}</h1>
          {project && <p className="epic-summary">{project.description || "No description yet."}</p>}
          <div className="epic-headline-stats">
            <span><b>{count}</b> stories</span>
            <span><b>{done}</b> done</span>
            <span><b>{wip}</b> in progress</span>
            {blk > 0 && <span style={{color:"var(--status-blocked-ink)"}}><b>{blk}</b> blocked</span>}
            <span><b>{est}</b> pts total</span>
          </div>
        </div>
        <aside className="docs-rail">
          <div className="docs-heading"><span>Docs</span></div>
          <div className="docs-empty">Select an epic to see its documents.</div>
        </aside>
      </section>
    );
  }

  return (
    <section className="epic-detail">
      <div className="epic-headline">
        <h1 className="epic-title">
          {epic.name}
          {epic.isArchived && <span className="archived-pill" style={{marginLeft:12, verticalAlign:"middle", fontSize:11}}>archived</span>}
        </h1>
        <p className="epic-summary">{epic.description || "No description yet."}</p>
        <div className="epic-headline-stats">
          <span><b>{done}/{count}</b> done</span>
          <span><b>{wip}</b> in flight</span>
          {blk > 0 && <span style={{color:"var(--status-blocked-ink)"}}><b>{blk}</b> blocked</span>}
          <span><b>{est}</b> pts</span>
          <span>updated <b>{formatDate(epic.updatedAtUtc)}</b></span>
          <span style={{display:"flex", gap:6, marginLeft:"auto"}}>
            <button className="btn small ghost" onClick={onEditEpic}>Edit</button>
            <button className="btn small ghost" onClick={onArchiveEpic}>{epic.isArchived ? "Restore" : "Archive"}</button>
            <button className="btn small ghost" onClick={onDeleteEpic}>Delete</button>
          </span>
        </div>
      </div>
      <aside className="docs-rail">
        <div className="docs-heading">
          <span>Docs</span>
          <button className="btn small ghost" onClick={onNewDoc}>+</button>
        </div>
        {docs.length === 0 && <div className="docs-empty">No docs yet. Capture PRDs, plans, runbooks.</div>}
        {docs.map(d => (
          <button key={d.id} className="doc-item" onClick={() => onOpenDoc(d)}>
            <span className="doc-ico"/>
            <span>
              <div className="doc-title">{d.title}</div>
              <div className="doc-preview">{(d.body || "").replace(/\n/g, " ").replace(/#+ /g, "").slice(0, 70)}</div>
            </span>
          </button>
        ))}
      </aside>
    </section>
  );
}

/* ============ Card + Board ============ */
function Card({ story, projectKey, onOpen, onDragStart, onDragEnd, dragging, showAging }) {
  const prioBars = { Low: 1, Medium: 2, High: 3, Critical: 4 }[story.priority] || 1;
  const aged = agedDays(story);
  const labels = parseLabels(story.labels);
  return (
    <article
      className={clsx("card", dragging && "dragging", story.status === "Done" && "done")}
      draggable
      onDragStart={(e) => onDragStart(e, story)}
      onDragEnd={onDragEnd}
      onClick={() => onOpen(story)}
    >
      <div className="card-head">
        <span className="card-key">{shortKey(projectKey, story.id)}</span>
        <span className={clsx("card-type", story.type)}>{story.type}</span>
        <span className={clsx("card-prio", "prio-" + story.priority)} title={story.priority}>
          {Array.from({length: prioBars}).map((_, i) => (
            <span key={i} className="bar" style={{height: 6 + i * 2}}/>
          ))}
        </span>
      </div>
      <p className="card-title">{story.title}</p>
      <div className="card-foot">
        <div className="card-foot-left">
          {story.estimate > 0 && <span className="card-est">{story.estimate}pt</span>}
          <span className="card-labels">
            {labels.slice(0, 2).map(l => <span key={l} className="label-chip">{l}</span>)}
            {labels.length > 2 && <span className="label-chip">+{labels.length - 2}</span>}
          </span>
        </div>
        {showAging && story.status !== "Done" && aged > 0 && (
          <span className={clsx("card-age", ageBand(aged))} title={`In column for ~${aged} day(s)`}>
            <span className="card-age-dot"/>
            {ageLabel(aged)}
          </span>
        )}
      </div>
    </article>
  );
}

function Board({ items, projectKey, onOpen, onMove, showAging }) {
  const [dragId, setDragId] = useState(null);
  const [dragOver, setDragOver] = useState(null);

  const onDragStart = (e, story) => {
    setDragId(story.id);
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", story.id);
  };
  const onDragEnd = () => { setDragId(null); setDragOver(null); };

  const onDragOverCol = (e, status) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    if (dragOver !== status) setDragOver(status);
  };
  const onDropCol = (e, status) => {
    e.preventDefault();
    const id = e.dataTransfer.getData("text/plain") || dragId;
    if (id) onMove(id, status);
    setDragOver(null);
    setDragId(null);
  };

  const byStatus = useMemo(() => {
    const m = Object.fromEntries(STATUS_ORDER.map(s => [s, []]));
    items.forEach(s => { if (m[s.status]) m[s.status].push(s); });
    return m;
  }, [items]);

  return (
    <div className="board-wrap">
      <div className="board">
        {STATUS_ORDER.map(s => {
          const list = byStatus[s];
          return (
            <section
              key={s}
              className={clsx("col", dragOver === s && "drag-over")}
              data-status={s}
              onDragOver={(e) => onDragOverCol(e, s)}
              onDragLeave={() => setDragOver(prev => prev === s ? null : prev)}
              onDrop={(e) => onDropCol(e, s)}
            >
              <header className="col-head">
                <div className="col-title">
                  <span className="col-dot"/>
                  <span>{STATUS_LABEL[s]}</span>
                </div>
                <span className="col-count">{list.length}</span>
              </header>
              <div className="col-body">
                {list.map(story => (
                  <Card
                    key={story.id}
                    story={story}
                    projectKey={projectKey}
                    onOpen={onOpen}
                    onDragStart={onDragStart}
                    onDragEnd={onDragEnd}
                    dragging={dragId === story.id}
                    showAging={showAging}
                  />
                ))}
                {list.length === 0 && <div className="col-empty">Drop stories here</div>}
              </div>
            </section>
          );
        })}
      </div>
    </div>
  );
}

/* ============ Command Palette ============ */
function CommandPalette({ open, onClose, items, projects, epics, currentProject, onOpenStory, onQuickAdd, setProjectId, setEpicId }) {
  const [q, setQ] = useState("");
  const [idx, setIdx] = useState(0);
  const inputRef = useRef(null);

  useEffect(() => {
    if (open) {
      setQ(""); setIdx(0);
      setTimeout(() => inputRef.current?.focus(), 30);
    }
  }, [open]);

  const rows = useMemo(() => {
    const s = q.trim().toLowerCase();
    const out = [];

    if (s && currentProject) {
      out.push({
        kind: "create",
        title: <>Create story: <span style={{color:"var(--ink)", fontWeight:600}}>"{q}"</span></>,
        hint: "↵ create",
        action: () => { onQuickAdd(q); onClose(); },
      });
    }

    const matchedStories = items
      .filter(st => !s || st.title.toLowerCase().includes(s))
      .slice(0, 8)
      .map(st => ({
        kind: "story",
        title: (<><span className="kbd" style={{marginRight:6}}>{shortKey(currentProject?.key, st.id)}</span>{st.title}</>),
        hint: st.status,
        action: () => { onOpenStory(st); onClose(); },
      }));

    const matchedEpics = epics
      .filter(e => !s || e.name.toLowerCase().includes(s))
      .slice(0, 4)
      .map(e => ({
        kind: "epic",
        title: <>Go to epic: {e.name}</>,
        hint: "epic",
        action: () => {
          setEpicId(e.id);
          onClose();
        },
      }));

    const matchedProjects = projects
      .filter(p => !s || p.name.toLowerCase().includes(s) || p.key.toLowerCase().includes(s))
      .slice(0, 4)
      .map(p => ({
        kind: "project",
        title: <>Go to project: {p.key} · {p.name}</>,
        hint: "project",
        action: () => { setProjectId(p.id); setEpicId(null); onClose(); },
      }));

    return [
      ...out,
      ...(matchedStories.length ? [{ label: "Stories" }, ...matchedStories] : []),
      ...(matchedEpics.length ? [{ label: "Epics" }, ...matchedEpics] : []),
      ...(matchedProjects.length ? [{ label: "Projects" }, ...matchedProjects] : []),
    ];
  }, [q, items, projects, epics, currentProject]);

  const actionable = rows.filter(i => !i.label);

  useEffect(() => {
    if (!open) return;
    const h = (e) => {
      if (e.key === "Escape") { onClose(); }
      else if (e.key === "ArrowDown") { setIdx(i => Math.min(i + 1, actionable.length - 1)); e.preventDefault(); }
      else if (e.key === "ArrowUp") { setIdx(i => Math.max(i - 1, 0)); e.preventDefault(); }
      else if (e.key === "Enter") { actionable[idx]?.action?.(); }
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, [open, actionable, idx, onClose]);

  if (!open) return null;
  let actionIdx = -1;
  return (
    <div className="palette-overlay" onClick={onClose}>
      <div className="palette" onClick={(e) => e.stopPropagation()}>
        <div className="palette-input-row">
          <span className="palette-eyebrow">Quick add</span>
          <input
            ref={inputRef}
            placeholder="Type a new story title, or search stories, epics, projects…"
            value={q}
            onChange={e => { setQ(e.target.value); setIdx(0); }}
          />
        </div>
        <div className="palette-body">
          {rows.length === 0 && (
            <div className="palette-section-label" style={{padding:16}}>Start typing to create or search.</div>
          )}
          {rows.map((it, i) => {
            if (it.label) return <div key={"l" + i} className="palette-section-label">{it.label}</div>;
            actionIdx++;
            const active = actionIdx === idx;
            return (
              <div
                key={i}
                className={clsx("palette-row", active && "active")}
                onMouseEnter={() => setIdx(actionIdx)}
                onClick={it.action}
              >
                <div className="palette-row-title">{it.title}</div>
                <div className="palette-row-hint">{it.hint}</div>
              </div>
            );
          })}
        </div>
        <div className="palette-footer">
          <span>Tip: type a full sentence and hit ↵ to create a new story in this project.</span>
          <div className="keys">
            <span className="kbd">↑↓</span><span className="kbd">↵</span><span className="kbd">esc</span>
          </div>
        </div>
      </div>
    </div>
  );
}

/* ============ Detail sheet ============ */
function DetailSheet({ story, epics, projectKey, onClose, onUpdate, onDelete }) {
  const [local, setLocal] = useState(story);
  const [saving, setSaving] = useState(false);
  const saveTimer = useRef(null);

  useEffect(() => { setLocal(story); }, [story?.id]);

  const patch = (p) => {
    const next = { ...local, ...p };
    setLocal(next);
    clearTimeout(saveTimer.current);
    saveTimer.current = setTimeout(() => {
      setSaving(true);
      onUpdate(next).finally(() => setSaving(false));
    }, 400);
  };

  if (!story || !local) return null;
  return (
    <>
      <div className="sheet-overlay" onClick={onClose}/>
      <aside className="sheet">
        <header className="sheet-head">
          <div style={{flex:1}}>
            <div className="sheet-eyebrow">{shortKey(projectKey, story.id)} {saving && "· saving…"}</div>
            <input
              className="sheet-title"
              style={{background:"transparent", border:0, outline:"none", padding:0, width:"100%"}}
              value={local.title}
              onChange={e => patch({ title: e.target.value })}
            />
          </div>
          <div className="sheet-head-actions">
            <button className="btn ghost" onClick={() => { if (confirm("Delete this item?")) { onDelete(story.id); onClose(); } }}>Delete</button>
            <button className="btn" onClick={onClose}>Close</button>
          </div>
        </header>
        <div className="sheet-body">
          <div>
            <div className="sheet-row">
              <label>Type</label>
              <select value={local.type} onChange={e => patch({ type: e.target.value })}>
                {WORK_ITEM_TYPES.map(t => <option key={t}>{t}</option>)}
              </select>
            </div>
            <div className="sheet-row">
              <label>Status</label>
              <select value={local.status} onChange={e => patch({ status: e.target.value })}>
                {STATUS_ORDER.map(s => <option key={s} value={s}>{STATUS_LABEL[s]}</option>)}
              </select>
            </div>
            <div className="sheet-row">
              <label>Priority</label>
              <select value={local.priority} onChange={e => patch({ priority: e.target.value })}>
                {PRIORITY_ORDER.map(p => <option key={p}>{p}</option>)}
              </select>
            </div>
            <div className="sheet-row">
              <label>Epic</label>
              <select value={local.epicId || ""} onChange={e => patch({ epicId: e.target.value || null })}>
                <option value="">(no epic)</option>
                {epics.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
              </select>
            </div>
            <div className="sheet-row">
              <label>Estimate</label>
              <input type="number" min={0} max={100} value={local.estimate ?? 0}
                     onChange={e => patch({ estimate: Number(e.target.value) })} />
            </div>
            <div className="sheet-row">
              <label>Labels</label>
              <input value={parseLabels(local.labels).join(", ")}
                     onChange={e => patch({ labels: labelsToString(e.target.value.split(",")) })} />
            </div>
          </div>
          <div>
            <div className="sheet-section-title">Description</div>
            <div className="sheet-row" style={{gridTemplateColumns:"1fr"}}>
              <textarea
                placeholder="Add a description…"
                value={local.description || ""}
                onChange={e => patch({ description: e.target.value })}
              />
            </div>
          </div>
        </div>
      </aside>
    </>
  );
}

/* ============ Doc sheet (view + edit) ============ */
function DocSheet({ doc, epicId, onClose, onSave }) {
  const [title, setTitle] = useState("");
  const [body, setBody] = useState("");
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!doc) return;
    setTitle(doc.title || "");
    setBody(doc.body || "");
    setEditing(!!doc.__draft);
  }, [doc?.id]);

  if (!doc) return null;
  const isDraft = !!doc.__draft;

  const commit = async () => {
    if (!title.trim() || !body.trim()) return;
    setSaving(true);
    try {
      await onSave({ id: doc.id, epicId: epicId || doc.epicId, title: title.trim(), body });
      setEditing(false);
      if (isDraft) onClose();
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <div className="sheet-overlay" onClick={onClose}/>
      <aside className="sheet">
        <header className="sheet-head">
          <div style={{flex:1}}>
            <div className="sheet-eyebrow">Epic document{saving && " · saving…"}</div>
            {editing ? (
              <input
                className="sheet-title"
                style={{background:"transparent", border:0, outline:"none", padding:0, width:"100%"}}
                value={title}
                onChange={e => setTitle(e.target.value)}
                placeholder="Doc title"
              />
            ) : (
              <h2 className="sheet-title">{title}</h2>
            )}
          </div>
          <div className="sheet-head-actions">
            {editing ? (
              <>
                <button className="btn ghost" onClick={onClose}>Cancel</button>
                <button className="btn primary" onClick={commit} disabled={saving}>Save</button>
              </>
            ) : (
              <>
                <button className="btn ghost" onClick={() => setEditing(true)}>Edit</button>
                <button className="btn" onClick={onClose}>Close</button>
              </>
            )}
          </div>
        </header>
        <div className="sheet-body">
          {editing ? (
            <div className="sheet-row" style={{gridTemplateColumns:"1fr"}}>
              <textarea
                placeholder="Markdown-flavoured body…"
                value={body}
                onChange={e => setBody(e.target.value)}
                style={{minHeight: "55vh"}}
              />
            </div>
          ) : (
            <pre style={{
              whiteSpace:"pre-wrap", fontFamily:"var(--font-body)",
              fontSize: 14, lineHeight: 1.65, color:"var(--ink-2)", margin: 0
            }}>{body}</pre>
          )}
        </div>
      </aside>
    </>
  );
}

/* ============ Tweaks panel ============ */
function TweaksPanel({ tweaks, setTweaks, open, onClose }) {
  const themes = [
    { id: "clay",  name: "Warm Clay",  chips: ["#c96b29", "#f4eee1", "#1e2024"] },
    { id: "paper", name: "Editorial",  chips: ["#1f1f22", "#fbf9f4", "#d7d1c0"] },
    { id: "ink",   name: "Focus Dark", chips: ["#e7e5de", "#0f1113", "#d0a45a"] },
  ];
  if (!open) return null;
  return (
    <div className="tweaks open">
      <div className="tweaks-head">
        <h4>Tweaks</h4>
        <button className="icon-btn" onClick={onClose}>×</button>
      </div>
      <div className="tweaks-body">
        <div className="tweak-group">
          <div className="tweak-label">Theme</div>
          <div className="theme-swatches">
            {themes.map(t => (
              <button
                key={t.id}
                className={clsx("theme-swatch", tweaks.theme === t.id && "on")}
                onClick={() => setTweaks({ ...tweaks, theme: t.id })}
              >
                <div className="chip-row">
                  {t.chips.map((c, i) => <span key={i} style={{background: c}}/>)}
                </div>
                <div className="name">{t.name}</div>
              </button>
            ))}
          </div>
        </div>

        <div className="tweak-group">
          <div className="tweak-label">Density</div>
          <div className="seg">
            <button className={tweaks.density === "comfortable" ? "on" : ""} onClick={() => setTweaks({...tweaks, density: "comfortable"})}>Comfortable</button>
            <button className={tweaks.density === "compact" ? "on" : ""}     onClick={() => setTweaks({...tweaks, density: "compact"})}>Compact</button>
          </div>
        </div>

        <div className="tweak-group">
          <div className="tweak-label">Show aging</div>
          <div className="seg">
            <button className={tweaks.showAging ? "on" : ""}  onClick={() => setTweaks({...tweaks, showAging: true})}>On</button>
            <button className={!tweaks.showAging ? "on" : ""} onClick={() => setTweaks({...tweaks, showAging: false})}>Off</button>
          </div>
        </div>
      </div>
    </div>
  );
}

/* ============ Project modal ============ */
function ProjectModal({ open, project, onClose, onSave, onDelete }) {
  const isEdit = !!project;
  const [name, setName] = useState("");
  const [key, setKey] = useState("");
  const [description, setDescription] = useState("");
  const [isArchived, setIsArchived] = useState(false);
  const [err, setErr] = useState(null);

  useEffect(() => {
    if (!open) return;
    setName(project?.name || "");
    setKey(project?.key || "");
    setDescription(project?.description || "");
    setIsArchived(project?.isArchived || false);
    setErr(null);
  }, [open, project?.id]);

  if (!open) return null;

  const submit = async () => {
    try {
      setErr(null);
      await onSave({
        name: name.trim(),
        key: key.trim().toUpperCase(),
        description: description.trim() || null,
        isArchived,
      });
      onClose();
    } catch (e) {
      setErr(e.message || "Save failed");
    }
  };

  return (
    <>
      <div className="sheet-overlay" onClick={onClose}/>
      <aside className="sheet" style={{width: "min(440px, 92vw)"}}>
        <header className="sheet-head">
          <div>
            <div className="sheet-eyebrow">{isEdit ? "Edit project" : "New project"}</div>
            <h2 className="sheet-title">{isEdit ? project.name : "Create a project"}</h2>
          </div>
          <div className="sheet-head-actions">
            <button className="btn" onClick={onClose}>Close</button>
          </div>
        </header>
        <div className="sheet-body">
          <div className="sheet-row">
            <label>Name</label>
            <input value={name} onChange={e => setName(e.target.value)} placeholder="Ledger API"/>
          </div>
          <div className="sheet-row">
            <label>Key</label>
            <input value={key} onChange={e => setKey(e.target.value.toUpperCase())} placeholder="API" maxLength={8}/>
          </div>
          <div className="sheet-row" style={{gridTemplateColumns: "90px 1fr"}}>
            <label>Description</label>
            <textarea value={description} onChange={e => setDescription(e.target.value)} placeholder="What's this project about?"/>
          </div>
          {isEdit && (
            <div className="sheet-row">
              <label>Archived</label>
              <label className="side-toggle" style={{padding:0}}>
                <input type="checkbox" checked={isArchived} onChange={e => setIsArchived(e.target.checked)}/>
                {isArchived ? "yes — hidden from default lists" : "no"}
              </label>
            </div>
          )}
          {err && <div style={{color:"var(--status-blocked-ink)", fontSize:13}}>{err}</div>}
          <div style={{display:"flex", gap:8, justifyContent: isEdit ? "space-between" : "flex-end"}}>
            {isEdit && <button className="btn ghost" onClick={() => { if (confirm("Delete project? All epics, docs, and stories go with it.")) { onDelete(project.id); onClose(); } }}>Delete</button>}
            <div style={{display:"flex", gap:8}}>
              <button className="btn ghost" onClick={onClose}>Cancel</button>
              <button className="btn primary" onClick={submit}>{isEdit ? "Save" : "Create project"}</button>
            </div>
          </div>
        </div>
      </aside>
    </>
  );
}

/* ============ Epic modal ============ */
function EpicModal({ open, epic, onClose, onSave }) {
  const isEdit = !!epic;
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [isArchived, setIsArchived] = useState(false);
  const [err, setErr] = useState(null);

  useEffect(() => {
    if (!open) return;
    setName(epic?.name || "");
    setDescription(epic?.description || "");
    setIsArchived(epic?.isArchived || false);
    setErr(null);
  }, [open, epic?.id]);

  if (!open) return null;

  const submit = async () => {
    try {
      setErr(null);
      await onSave({
        name: name.trim(),
        description: description.trim() || null,
        isArchived,
      });
      onClose();
    } catch (e) {
      setErr(e.message || "Save failed");
    }
  };

  return (
    <>
      <div className="sheet-overlay" onClick={onClose}/>
      <aside className="sheet" style={{width: "min(440px, 92vw)"}}>
        <header className="sheet-head">
          <div>
            <div className="sheet-eyebrow">{isEdit ? "Edit epic" : "New epic"}</div>
            <h2 className="sheet-title">{isEdit ? epic.name : "Create an epic"}</h2>
          </div>
          <div className="sheet-head-actions">
            <button className="btn" onClick={onClose}>Close</button>
          </div>
        </header>
        <div className="sheet-body">
          <div className="sheet-row">
            <label>Name</label>
            <input value={name} onChange={e => setName(e.target.value)} placeholder="Auth v2 (OAuth + refresh)"/>
          </div>
          <div className="sheet-row" style={{gridTemplateColumns: "90px 1fr"}}>
            <label>Description</label>
            <textarea value={description} onChange={e => setDescription(e.target.value)} placeholder="Scope, goals, constraints."/>
          </div>
          {isEdit && (
            <div className="sheet-row">
              <label>Archived</label>
              <label className="side-toggle" style={{padding:0}}>
                <input type="checkbox" checked={isArchived} onChange={e => setIsArchived(e.target.checked)}/>
                {isArchived ? "yes" : "no"}
              </label>
            </div>
          )}
          {err && <div style={{color:"var(--status-blocked-ink)", fontSize:13}}>{err}</div>}
          <div style={{display:"flex", gap:8, justifyContent:"flex-end"}}>
            <button className="btn ghost" onClick={onClose}>Cancel</button>
            <button className="btn primary" onClick={submit}>{isEdit ? "Save" : "Create epic"}</button>
          </div>
        </div>
      </aside>
    </>
  );
}

/* ============ Main App ============ */
function App() {
  const [projects, setProjects] = useState([]);
  const [epics, setEpics] = useState([]);
  const [docs, setDocs] = useState([]);
  const [items, setItems] = useState([]);

  const [currentProjectId, setCurrentProjectId] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    return params.get("project") || null;
  });
  const [currentEpicId, setCurrentEpicId] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    return params.get("epic") || null;
  });

  /* URL <-> state routing plumbing */
  const skipUrlPush = useRef(false);       // suppresses pushState when state change came from popstate
  const skipEpicReset = useRef(true);      // keep URL-loaded epic on initial mount + on popstate
  const initialUrlSyncDone = useRef(false);

  const [selectedStory, setSelectedStory] = useState(null);
  const [activeDoc, setActiveDoc] = useState(null);
  const [paletteOpen, setPaletteOpen] = useState(false);

  const [showArchivedProjects, setShowArchivedProjects] = useState(false);

  const [projectModal, setProjectModal] = useState({ open: false, project: null });
  const [epicModal, setEpicModal] = useState({ open: false, epic: null });

  const [tweaksOpen, setTweaksOpen] = useState(false);
  const [tweaks, setTweaks] = useState(() => {
    try {
      const saved = JSON.parse(localStorage.getItem("kanban-tweaks") || "null");
      return { ...TWEAK_DEFAULTS, ...(saved || {}) };
    } catch { return TWEAK_DEFAULTS; }
  });

  const [bootstrapped, setBootstrapped] = useState(false);
  const { msg, show, clear } = useFeedback();

  /* Apply theme + density */
  useEffect(() => {
    document.documentElement.dataset.theme = tweaks.theme;
    document.documentElement.dataset.density = tweaks.density;
    localStorage.setItem("kanban-tweaks", JSON.stringify(tweaks));
  }, [tweaks]);

  /* Sync URL <- state (replaceState for the first pass after bootstrap, pushState for real navigations) */
  useEffect(() => {
    if (!bootstrapped) return;
    if (skipUrlPush.current) {
      skipUrlPush.current = false;
      initialUrlSyncDone.current = true;
      return;
    }
    const params = new URLSearchParams();
    if (currentProjectId) params.set("project", currentProjectId);
    if (currentEpicId) params.set("epic", currentEpicId);
    const qs = params.toString();
    const newUrl = `${window.location.pathname}${qs ? "?" + qs : ""}`;
    const currentUrl = window.location.pathname + window.location.search;
    if (newUrl === currentUrl) {
      initialUrlSyncDone.current = true;
      return;
    }
    const state = { projectId: currentProjectId, epicId: currentEpicId };
    if (!initialUrlSyncDone.current) {
      window.history.replaceState(state, "", newUrl);
      initialUrlSyncDone.current = true;
    } else {
      window.history.pushState(state, "", newUrl);
    }
  }, [bootstrapped, currentProjectId, currentEpicId]);

  /* Sync state <- URL on back/forward */
  useEffect(() => {
    const onPop = () => {
      const params = new URLSearchParams(window.location.search);
      const nextProject = params.get("project") || null;
      const nextEpic = params.get("epic") || null;
      skipUrlPush.current = true;
      setCurrentProjectId(prev => {
        // Only suppress the project-change effect's epic reset if we'd
        // otherwise clobber a valid URL-provided epic.
        if (prev !== nextProject) skipEpicReset.current = true;
        return nextProject;
      });
      setCurrentEpicId(nextEpic);
    };
    window.addEventListener("popstate", onPop);
    return () => window.removeEventListener("popstate", onPop);
  }, []);

  const currentProject = useMemo(
    () => projects.find(p => p.id === currentProjectId) || null,
    [projects, currentProjectId]
  );
  const currentEpic = useMemo(
    () => epics.find(e => e.id === currentEpicId) || null,
    [epics, currentEpicId]
  );

  const boardItems = useMemo(() => {
    if (currentEpicId) return items.filter(s => s.epicId === currentEpicId);
    return items;
  }, [items, currentEpicId]);

  const epicDocs = useMemo(() => {
    if (!currentEpicId) return [];
    return docs.filter(d => d.epicId === currentEpicId);
  }, [docs, currentEpicId]);

  /* Fetch projects on mount + when archive toggle changes */
  const loadProjects = useCallback(async (preferredId) => {
    try {
      const list = await api.listProjects(showArchivedProjects);
      setProjects(list);
      let nextProjectId = null;
      if (list.length === 0) {
        setCurrentProjectId(null);
      } else if (preferredId && list.some(p => p.id === preferredId)) {
        nextProjectId = preferredId;
        setCurrentProjectId(preferredId);
      } else if (!currentProjectId || !list.some(p => p.id === currentProjectId)) {
        nextProjectId = list[0].id;
        setCurrentProjectId(list[0].id);
      } else {
        nextProjectId = currentProjectId;
      }
      return nextProjectId;
    } catch (e) {
      show(`Couldn't load projects: ${e.message}`);
      return null;
    }
  }, [showArchivedProjects, currentProjectId, show]);

  useEffect(() => {
    (async () => {
      await loadProjects();
      setBootstrapped(true);
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!bootstrapped) return;
    loadProjects();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [showArchivedProjects]);

  /* Fetch epics + board items + all docs when project changes */
  const loadProjectDetail = useCallback(async (projectId) => {
    if (!projectId) { setEpics([]); setItems([]); setDocs([]); return; }
    try {
      const [board, epicList] = await Promise.all([
        api.getProjectBoard(projectId, { includeArchivedEpics: true }),
        api.listProjectEpics(projectId, true),
      ]);
      setEpics(epicList);
      const nextItems = board.items || [];
      setItems(nextItems);
      setSelectedStory(current => current ? nextItems.find(item => item.id === current.id) || null : current);

      // Drop the current epic selection if it isn't valid for this project
      // (e.g. a stale ?epic=... in the URL after reloading into another project).
      setCurrentEpicId(eid => (eid && !epicList.some(e => e.id === eid)) ? null : eid);

      // Load docs for all epics in the project (small N — fine).
      const nextDocs = (await Promise.all(epicList.map(e => api.listEpicDocuments(e.id).catch(() => [])))).flat();
      setDocs(nextDocs);
      setActiveDoc(current => current ? nextDocs.find(doc => doc.id === current.id) || null : current);
    } catch (e) {
      show(`Couldn't load project: ${e.message}`);
    }
  }, [show]);

  const refreshLiveView = useCallback(async () => {
    const activeProjectId = currentProjectId;
    const resolvedProjectId = await loadProjects(activeProjectId);
    if (resolvedProjectId && resolvedProjectId === activeProjectId) {
      await loadProjectDetail(resolvedProjectId);
    }
  }, [currentProjectId, loadProjectDetail, loadProjects]);

  useEffect(() => {
    if (!currentProjectId) {
      setEpics([]); setItems([]); setDocs([]); setCurrentEpicId(null);
      skipEpicReset.current = false;
      return;
    }
    if (skipEpicReset.current) {
      // Initial mount or popstate — preserve the URL-provided epic.
      skipEpicReset.current = false;
    } else {
      setCurrentEpicId(null);
    }
    loadProjectDetail(currentProjectId);
  }, [currentProjectId, loadProjectDetail]);

  useEffect(() => {
    if (!bootstrapped || typeof window.EventSource === "undefined") return undefined;

    let disposed = false;
    let eventSource = null;
    let reconnectTimer = null;
    let refreshTimer = null;

    const scheduleRefresh = () => {
      window.clearTimeout(refreshTimer);
      refreshTimer = window.setTimeout(() => {
        if (!disposed) {
          void refreshLiveView();
        }
      }, 150);
    };

    const connect = () => {
      if (disposed) return;

      eventSource = new window.EventSource("/api/changes");
      eventSource.addEventListener("changed", scheduleRefresh);
      eventSource.onerror = () => {
        eventSource?.close();
        eventSource = null;

        if (!disposed) {
          reconnectTimer = window.setTimeout(connect, 2000);
        }
      };
    };

    connect();

    return () => {
      disposed = true;
      window.clearTimeout(refreshTimer);
      window.clearTimeout(reconnectTimer);
      eventSource?.close();
    };
  }, [bootstrapped, refreshLiveView]);

  /* Cmd+K / slash */
  useEffect(() => {
    const h = (e) => {
      const tag = (document.activeElement?.tagName || "").toLowerCase();
      const typing = tag === "input" || tag === "textarea" || tag === "select";
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setPaletteOpen(v => !v);
      }
      if (e.key === "/" && !typing) {
        e.preventDefault();
        setPaletteOpen(true);
      }
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, []);

  /* Story mutations */
  const moveStory = async (id, status) => {
    const prev = items;
    const current = prev.find(s => s.id === id);
    if (!current || current.status === status) return;
    // Optimistic
    setItems(prev.map(s => s.id === id ? { ...s, status, updatedAtUtc: new Date().toISOString() } : s));
    try {
      const col = prev.filter(s => s.status === status);
      const updated = await api.moveWorkItem(id, status, col.length);
      setItems(cur => cur.map(s => s.id === id ? updated : s));
    } catch (e) {
      show(`Move failed: ${e.message}`);
      setItems(prev);
    }
  };

  const updateStory = async (next) => {
    try {
      const saved = await api.updateWorkItem(next.id, {
        epicId: next.epicId || null,
        title: next.title,
        description: next.description || null,
        type: next.type,
        status: next.status,
        priority: next.priority,
        estimate: next.estimate ?? null,
        labels: next.labels || null,
      });
      setItems(cur => cur.map(s => s.id === saved.id ? saved : s));
      if (selectedStory?.id === saved.id) setSelectedStory(saved);
    } catch (e) {
      show(`Save failed: ${e.message}`);
    }
  };

  const deleteStory = async (id) => {
    try {
      await api.deleteWorkItem(id);
      setItems(cur => cur.filter(s => s.id !== id));
    } catch (e) {
      show(`Delete failed: ${e.message}`);
    }
  };

  const quickAdd = async (title) => {
    if (!currentProject) return;
    const epicId = currentEpicId || null;
    try {
      const created = await api.createWorkItem({
        projectId: currentProject.id,
        epicId,
        title,
        description: null,
        type: "Story",
        status: "Backlog",
        priority: "Medium",
        estimate: null,
        labels: null,
      });
      setItems(cur => [...cur, created]);
      setSelectedStory(created);
    } catch (e) {
      show(`Create failed: ${e.message}`);
    }
  };

  /* Project CRUD */
  const saveProject = async (payload) => {
    if (projectModal.project) {
      const saved = await api.updateProject(projectModal.project.id, payload);
      setProjects(cur => cur.map(p => p.id === saved.id ? { ...p, ...saved } : p));
      show("Project saved", "success");
    } else {
      const saved = await api.createProject(payload);
      await loadProjects(saved.id);
      show("Project created", "success");
    }
  };
  const removeProject = async (id) => {
    try {
      await api.deleteProject(id);
      await loadProjects();
      show("Project deleted", "success");
    } catch (e) {
      show(`Delete failed: ${e.message}`);
    }
  };

  /* Epic CRUD */
  const saveEpic = async (payload) => {
    if (epicModal.epic) {
      const saved = await api.updateEpic(epicModal.epic.id, payload);
      setEpics(cur => cur.map(e => e.id === saved.id ? saved : e));
      show("Epic saved", "success");
    } else {
      const saved = await api.createEpic(currentProject.id, payload);
      setEpics(cur => [...cur, saved]);
      setCurrentEpicId(saved.id);
      show("Epic created", "success");
    }
  };
  const toggleEpicArchive = async () => {
    if (!currentEpic) return;
    try {
      const saved = await api.updateEpic(currentEpic.id, {
        name: currentEpic.name,
        description: currentEpic.description || null,
        isArchived: !currentEpic.isArchived,
      });
      setEpics(cur => cur.map(e => e.id === saved.id ? saved : e));
      if (saved.isArchived) {
        setCurrentEpicId(null);
      }
      await loadProjectDetail(currentProject.id);
      show(saved.isArchived ? "Epic archived; unfinished items moved to the product backlog" : "Epic restored", "success");
    } catch (e) {
      show(`Update failed: ${e.message}`);
    }
  };
  const removeEpic = async () => {
    if (!currentEpic) return;
    if (!confirm(`Delete epic "${currentEpic.name}"? Its docs are removed; items unlink.`)) return;
    try {
      await api.deleteEpic(currentEpic.id);
      setEpics(cur => cur.filter(e => e.id !== currentEpic.id));
      setCurrentEpicId(null);
      await loadProjectDetail(currentProjectId);
      show("Epic deleted", "success");
    } catch (e) {
      show(`Delete failed: ${e.message}`);
    }
  };

  /* Doc CRUD */
  const openNewDoc = () => {
    if (!currentEpicId) return;
    setActiveDoc({ id: null, epicId: currentEpicId, title: "", body: "", __draft: true });
  };
  const saveDoc = async ({ id, epicId, title, body }) => {
    try {
      if (id) {
        const saved = await api.updateEpicDocument(id, { title, body });
        setDocs(cur => cur.map(d => d.id === saved.id ? saved : d));
        setActiveDoc(saved);
        show("Doc saved", "success");
      } else {
        const saved = await api.createEpicDocument(epicId, { title, body });
        setDocs(cur => [...cur, saved]);
        setActiveDoc(saved);
        show("Doc created", "success");
      }
    } catch (e) {
      show(`Save failed: ${e.message}`);
      throw e;
    }
  };

  /* Render gating */
  if (!bootstrapped) {
    return <div className="app-loading">Loading your workspace…</div>;
  }

  if (projects.length === 0) {
    return (
      <div className="app-empty">
        <div>
          <h2>No projects yet</h2>
          <p style={{maxWidth: 360, margin: "0 auto 18px"}}>Create your first project to start tracking epics, docs, and stories.</p>
          <button className="btn primary" onClick={() => setProjectModal({ open: true, project: null })}>+ New project</button>
        </div>
        <ProjectModal
          open={projectModal.open}
          project={projectModal.project}
          onClose={() => setProjectModal({ open: false, project: null })}
          onSave={saveProject}
          onDelete={removeProject}
        />
        <FeedbackBar msg={msg} onClose={clear}/>
      </div>
    );
  }

  const visibleEpics = epics.filter(e => !e.isArchived || currentEpicId === e.id);

  return (
    <div className="shell">
      <Sidebar
        projects={projects}
        currentProjectId={currentProjectId}
        setProjectId={(id) => setCurrentProjectId(id)}
        onNewProject={() => setProjectModal({ open: true, project: null })}
        showArchived={showArchivedProjects}
        setShowArchived={setShowArchivedProjects}
      />
      <main className="content">
        <Topbar
          project={currentProject}
          epic={currentEpic}
          onOpenPalette={() => setPaletteOpen(true)}
          onOpenTweaks={() => setTweaksOpen(v => !v)}
          onNewItem={() => setPaletteOpen(true)}
        />
        <EpicStrip
          epics={visibleEpics}
          items={items}
          currentEpicId={currentEpicId}
          setEpicId={setCurrentEpicId}
          onNewEpic={() => setEpicModal({ open: true, epic: null })}
        />
        <EpicDetail
          epic={currentEpic}
          project={currentProject}
          items={boardItems}
          docs={epicDocs}
          onOpenDoc={(d) => setActiveDoc(d)}
          onNewDoc={openNewDoc}
          onEditEpic={() => setEpicModal({ open: true, epic: currentEpic })}
          onArchiveEpic={toggleEpicArchive}
          onDeleteEpic={removeEpic}
        />
        <Board
          items={boardItems}
          projectKey={currentProject?.key}
          onOpen={(s) => setSelectedStory(s)}
          onMove={moveStory}
          showAging={tweaks.showAging}
        />
      </main>

      <CommandPalette
        open={paletteOpen}
        onClose={() => setPaletteOpen(false)}
        items={items}
        projects={projects}
        epics={epics}
        currentProject={currentProject}
        onOpenStory={(s) => setSelectedStory(s)}
        onQuickAdd={quickAdd}
        setProjectId={setCurrentProjectId}
        setEpicId={setCurrentEpicId}
      />
      <DetailSheet
        story={selectedStory}
        projectKey={currentProject?.key}
        epics={epics.filter(e => !e.isArchived)}
        onClose={() => setSelectedStory(null)}
        onUpdate={updateStory}
        onDelete={deleteStory}
      />
      <DocSheet
        doc={activeDoc}
        epicId={currentEpicId}
        onClose={() => setActiveDoc(null)}
        onSave={saveDoc}
      />
      <TweaksPanel
        tweaks={tweaks}
        setTweaks={setTweaks}
        open={tweaksOpen}
        onClose={() => setTweaksOpen(false)}
      />
      <ProjectModal
        open={projectModal.open}
        project={projectModal.project}
        onClose={() => setProjectModal({ open: false, project: null })}
        onSave={saveProject}
        onDelete={removeProject}
      />
      <EpicModal
        open={epicModal.open}
        epic={epicModal.epic}
        onClose={() => setEpicModal({ open: false, epic: null })}
        onSave={saveEpic}
      />
      <FeedbackBar msg={msg} onClose={clear}/>
    </div>
  );
}

const root = ReactDOM.createRoot(document.getElementById("root"));
root.render(<App/>);
