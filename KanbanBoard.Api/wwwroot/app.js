/* Kanban — React app wired to the .NET API.
   Single-file: API client + all components + root App. */

const { useState, useEffect, useMemo, useRef, useCallback } = React;

/* ============ Constants ============ */
const STATUS_ORDER = ["Backlog", "Ready", "InProgress", "Blocked", "Done"];
const STATUS_LABEL = { Backlog: "Backlog", Ready: "Ready", InProgress: "In Progress", Blocked: "Blocked", Done: "Done" };
const STATUS_OPTIONS = [...STATUS_ORDER, "Closed"];
STATUS_LABEL.Closed = "Closed";
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

function isVisibleWorkItem(item) {
  return item?.status !== "Closed";
}

function formatDate(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

function formatDateTime(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });
}

/* ============ API Client ============ */
let csrfTokenPromise = null;

function isUnsafeMethod(method) {
  return !["GET", "HEAD", "OPTIONS", "TRACE"].includes(String(method || "GET").toUpperCase());
}

async function getCsrfToken() {
  csrfTokenPromise ||= fetchJson("/api/auth/antiforgery")
    .then(result => result?.token || "")
    .catch(error => {
      csrfTokenPromise = null;
      throw error;
    });
  return csrfTokenPromise;
}

async function fetchJson(input, init) {
  const method = init?.method || "GET";
  const headers = { "Content-Type": "application/json", ...(init?.headers || {}) };

  if (isUnsafeMethod(method)) {
    const token = await getCsrfToken();
    if (token) headers["X-CSRF-TOKEN"] = token;
  }

  const res = await fetch(input, {
    credentials: "same-origin",
    ...init,
    headers,
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
  // PATCH, not PUT: PUT leaves the repository URL and default branch alone.
  updateProject: (id, payload) =>
    fetchJson(`/api/projects/${id}`, { method: "PATCH", body: JSON.stringify(payload) }),
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

  getWorkItem: (id) =>
    fetchJson(`/api/items/${id}`),
  createWorkItem: (payload) =>
    fetchJson(`/api/items`, { method: "POST", body: JSON.stringify(payload) }),
  updateWorkItem: (id, payload) =>
    fetchJson(`/api/items/${id}`, { method: "PUT", body: JSON.stringify(payload) }),
  addWorkItemComment: (id, payload) =>
    fetchJson(`/api/items/${id}/comments`, { method: "POST", body: JSON.stringify(payload) }),
  moveWorkItem: (id, status, order) =>
    fetchJson(`/api/items/${id}/move`, { method: "POST", body: JSON.stringify({ status, order }) }),
  deleteWorkItem: (id) =>
    fetchJson(`/api/items/${id}`, { method: "DELETE" }),
  // The whole history of one item, oldest first: the feed read from its start, page after page, filtered to the item.
  listItemHistory: async (id) => {
    const entries = [];
    let query = "fromStart=true";
    for (;;) {
      const page = await fetchJson(`/api/items/changes?itemId=${id}&limit=200&${query}`);
      entries.push(...page.items);
      if (page.items.length < 200) return entries;
      query = `cursor=${encodeURIComponent(page.next_cursor)}`;
    }
  },

  getCurrentUser: () =>
    fetchJson(`/api/me`),
  listPersonalAccessTokens: () =>
    fetchJson(`/api/me/personal-access-tokens`),
  createPersonalAccessToken: (payload) =>
    fetchJson(`/api/me/personal-access-tokens`, { method: "POST", body: JSON.stringify(payload) }),
  revokePersonalAccessToken: (id) =>
    fetchJson(`/api/me/personal-access-tokens/${id}`, { method: "DELETE" }),

  listServiceIdentities: () =>
    fetchJson(`/api/admin/service-identities`),
  createServiceIdentity: (name) =>
    fetchJson(`/api/admin/service-identities`, { method: "POST", body: JSON.stringify({ name }) }),
  disableServiceIdentity: (identityId) =>
    fetchJson(`/api/admin/service-identities/${identityId}/disable`, { method: "POST" }),
  enableServiceIdentity: (identityId) =>
    fetchJson(`/api/admin/service-identities/${identityId}/enable`, { method: "POST" }),
  issueServiceIdentityToken: (identityId, payload) =>
    fetchJson(`/api/admin/service-identities/${identityId}/tokens`, { method: "POST", body: JSON.stringify(payload) }),
  listServiceIdentityTokens: (identityId) =>
    fetchJson(`/api/admin/service-identities/${identityId}/tokens`),
  revokeServiceIdentityToken: (identityId, tokenId) =>
    fetchJson(`/api/admin/service-identities/${identityId}/tokens/${tokenId}`, { method: "DELETE" }),
  rotateServiceIdentityToken: (identityId, tokenId, payload) =>
    fetchJson(`/api/admin/service-identities/${identityId}/tokens/${tokenId}/rotate`, { method: "POST", body: JSON.stringify(payload) }),
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
function Topbar({ project, epic, view, isAdmin, onOpenPalette, onOpenTweaks, onNewItem, onOpenSettings, onOpenAdmin, onOpenBoard }) {
  const onPage = view === "settings" || view === "admin" || view === "story";
  return (
    <header className="topbar">
      <div className="crumbs">
        {view === "settings" ? (
          <>
            <span className="crumb-key">USER</span>
            <span className="crumb-title">Settings</span>
          </>
        ) : view === "admin" ? (
          <>
            <span className="crumb-key">ADMIN</span>
            <span className="crumb-title">Service identities</span>
          </>
        ) : project && <>
          <span className="crumb-key">{project.key}</span>
          <span className="crumb-title">{project.name}</span>
        </>}
        {!onPage && epic && <>
          <span className="sep">/</span>
          <span className="crumb-title" style={{fontSize:16, color:"var(--ink-2)"}}>{epic.name}</span>
        </>}
      </div>
      <div className="top-actions">
        {onPage ? (
          <button className="btn ghost" onClick={onOpenBoard} disabled={!project}>Board</button>
        ) : (
          <>
            <button className="btn ghost" onClick={onOpenPalette} disabled={!project}>
              <span>Search</span>
              <span className="kbd">⌘K</span>
            </button>
            <button className="btn" onClick={onNewItem} disabled={!project}>+ Add story</button>
          </>
        )}
        {isAdmin && (
          <button className={clsx("btn ghost", view === "admin" && "active")} onClick={onOpenAdmin}>Admin</button>
        )}
        <button className={clsx("btn ghost", view === "settings" && "active")} onClick={onOpenSettings}>Settings</button>
        <button className="btn ghost" onClick={onOpenTweaks} title="Tweaks">⚙</button>
      </div>
    </header>
  );
}

/* ============ Epic strip ============ */
function EpicStrip({ epics, items, currentEpicId, setEpicId, onNewEpic, showArchived, setShowArchived }) {
  const visibleItems = useMemo(() => items.filter(isVisibleWorkItem), [items]);

  const byEpic = useMemo(() => {
    const m = {};
    for (const it of visibleItems) {
      const id = it.epicId || "__none";
      if (!m[id]) m[id] = { Done: 0, InProgress: 0, Blocked: 0, Ready: 0, Backlog: 0, total: 0 };
      m[id][it.status]++;
      m[id].total++;
    }
    return m;
  }, [visibleItems]);

  return (
    <section className="epic-strip">
      <div className="epic-strip-header">
        <div className="epic-strip-title">
          <span className="epic-strip-eyebrow">Epics</span>
        </div>
        <div className="epic-strip-actions">
          <label className="side-toggle epic-toggle">
            <input type="checkbox" checked={showArchived} onChange={e => setShowArchived(e.target.checked)} />
            Show archived
          </label>
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
            <span>{visibleItems.length} stories total</span>
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
function EpicDetail({ epic, project, items, docs, onOpenDoc, onNewDoc, onEditEpic, onArchiveEpic, onDeleteEpic, onEditProject }) {
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
          {project?.repository_url && (
            <p className="epic-summary">
              {/^https?:\/\//i.test(project.repository_url)
                ? <a href={project.repository_url} target="_blank" rel="noopener noreferrer">{project.repository_url}</a>
                : project.repository_url}
              {project.default_branch && <> · <span className="kbd">{project.default_branch}</span></>}
            </p>
          )}
          <div className="epic-headline-stats">
            <span><b>{count}</b> stories</span>
            <span><b>{done}</b> done</span>
            <span><b>{wip}</b> in progress</span>
            {blk > 0 && <span style={{color:"var(--status-blocked-ink)"}}><b>{blk}</b> blocked</span>}
            <span><b>{est}</b> pts total</span>
            {project && (
              <span style={{display:"flex", gap:6, marginLeft:"auto"}}>
                <button className="btn small ghost" onClick={onEditProject}>Edit project</button>
              </span>
            )}
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

/* ============ Story editor (shared by the detail sheet and the story page) ============ */
// The fields the editor writes, each with the fields of the story that come with it (the epic's name with its id).
const EDITABLE_FIELDS = {
  title: ["title"], description: ["description"], type: ["type"], status: ["status"], priority: ["priority"],
  epicId: ["epicId", "epic"], estimate: ["estimate"], labels: ["labels", "labelList"],
};

// The editable field whose control has the focus (each control carries data-field), if any.
function focusedStoryField() {
  return document.activeElement?.dataset?.field || null;
}

// Local copy of the story with a 400ms debounced autosave. flush() saves a pending edit now, before navigating away.
// A save sends only the fields edited since the last one, so it does not write back other fields as they were read.
function useStoryEditor(story, onUpdate) {
  const [local, setLocal] = useState(story);
  const [saving, setSaving] = useState(false);
  const saveTimer = useRef(null);
  const pending = useRef(null);
  const pendingFields = useRef(new Set());
  // Fields of saves under way, with how many of them name each.
  const savingFields = useRef(new Map());
  // The highest version a save of this editor answered with: a copy read before that save is older than the page.
  const savedVersion = useRef(0);
  const inFlight = useRef(Promise.resolve());

  // Another story (or none): an edit of the previous one still waiting for its debounce is saved now, not dropped.
  useEffect(() => {
    if (pending.current && pending.current.id !== story?.id) {
      clearTimeout(saveTimer.current);
      save(pending.current);
    }
    savedVersion.current = 0;
    setLocal(story);
  }, [story?.id]);

  const save = (next) => {
    const fields = [...pendingFields.current];
    pending.current = null;
    pendingFields.current = new Set();
    fields.forEach(f => savingFields.current.set(f, (savingFields.current.get(f) || 0) + 1));
    setSaving(true);
    const request = Promise.resolve(onUpdate(next, fields))
      .then(saved => {
        if (saved?.id === next.id) savedVersion.current = Math.max(savedVersion.current, saved.version || 0);
        return saved;
      })
      .finally(() => {
        fields.forEach(f => savingFields.current.set(f, savingFields.current.get(f) - 1));
        setSaving(false);
      });
    inFlight.current = request.catch(() => {});
    return request;
  };

  const patch = (p) => {
    const next = { ...local, ...p };
    setLocal(next);
    Object.keys(p).forEach(f => pendingFields.current.add(f));
    pending.current = next;
    clearTimeout(saveTimer.current);
    saveTimer.current = setTimeout(() => save(next), 400);
  };

  const flush = () => {
    clearTimeout(saveTimer.current);
    // A save already under way is waited for too.
    return pending.current ? save(pending.current) : inFlight.current;
  };

  // Drops an edit not yet saved, when there is nothing left to save it to (the story was deleted).
  const discard = () => {
    clearTimeout(saveTimer.current);
    pending.current = null;
    pendingFields.current = new Set();
  };

  // Whether a field is being edited here: its control has the focus, or an edit of it is not yet saved.
  const isEditing = (field) =>
    focusedStoryField() === field || pendingFields.current.has(field) || savingFields.current.get(field) > 0;

  // Takes a copy of the story read from the server, except for the fields being edited here, which keep what the
  // viewer typed. A copy older than the editor's own last save is dropped. Answers the held fields it would change.
  const merge = (fresh) => {
    if (!fresh || (fresh.version || 0) < savedVersion.current) return [];
    const held = Object.keys(EDITABLE_FIELDS).filter(isEditing);
    const differs = (f) => local?.id === fresh.id && (fresh[f] ?? null) !== (local[f] ?? null);
    setLocal(current => {
      if (!current || current.id !== fresh.id) return current;
      const next = { ...fresh };
      held.forEach(f => EDITABLE_FIELDS[f].forEach(key => { next[key] = current[key]; }));
      return next;
    });
    return held.filter(differs);
  };

  return { local, saving, patch, flush, discard, merge };
}

function StoryFields({ local, epics, patch }) {
  return (
    <div>
      <div className="sheet-row">
        <label>Type</label>
        <select data-field="type" value={local.type} onChange={e => patch({ type: e.target.value })}>
          {WORK_ITEM_TYPES.map(t => <option key={t}>{t}</option>)}
        </select>
      </div>
      <div className="sheet-row">
        <label>Status</label>
        <select data-field="status" value={local.status} onChange={e => patch({ status: e.target.value })}>
          {STATUS_OPTIONS.map(s => <option key={s} value={s}>{STATUS_LABEL[s]}</option>)}
        </select>
      </div>
      <div className="sheet-row">
        <label>Priority</label>
        <select data-field="priority" value={local.priority} onChange={e => patch({ priority: e.target.value })}>
          {PRIORITY_ORDER.map(p => <option key={p}>{p}</option>)}
        </select>
      </div>
      <div className="sheet-row">
        <label>Epic</label>
        <select data-field="epicId" value={local.epicId || ""} onChange={e => patch({ epicId: e.target.value || null })}>
          <option value="">(no epic)</option>
          {epics.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
        </select>
      </div>
      <div className="sheet-row">
        <label>Estimate</label>
        <input data-field="estimate" type="number" min={0} max={100} value={local.estimate ?? 0}
               onChange={e => patch({ estimate: Number(e.target.value) })} />
      </div>
      <div className="sheet-row">
        <label>Labels</label>
        <input data-field="labels" value={parseLabels(local.labels).join(", ")}
               onChange={e => patch({ labels: labelsToString(e.target.value.split(",")) })} />
      </div>
    </div>
  );
}

// Description, comments and the comment composer. Key it by story id so another story starts with an empty draft.
function StoryNotes({ storyId, local, merge, patch, onAddComment }) {
  const [commentAuthor, setCommentAuthor] = useState(() => localStorage.getItem("kanban-comment-author") || "");
  const [commentBody, setCommentBody] = useState("");
  const [commenting, setCommenting] = useState(false);

  const comments = useMemo(() => (
    [...(local?.comments || [])]
      .sort((a, b) => new Date(a.createdAtUtc).getTime() - new Date(b.createdAtUtc).getTime())
  ), [local?.comments]);

  const submitComment = async () => {
    const author = commentAuthor.trim();
    const body = commentBody.trim();
    if (!author || !body) return;

    setCommenting(true);
    try {
      localStorage.setItem("kanban-comment-author", author);
      const saved = await onAddComment(storyId, { author, body });
      // Only while the editor still shows this story (it may have switched to another while the comment saved), and
      // without the fields being edited, whose edit is not saved yet.
      merge(saved);
      setCommentBody("");
    } finally {
      setCommenting(false);
    }
  };

  return (
    <div>
      <div className="sheet-section-title">Description</div>
      <div className="sheet-row" style={{gridTemplateColumns:"1fr"}}>
        <textarea
          data-field="description"
          placeholder="Add a description…"
          value={local.description || ""}
          onChange={e => patch({ description: e.target.value })}
        />
      </div>
      <div className="sheet-section-title" style={{marginTop: 18}}>Comments</div>
      <div className="comments-panel">
        {comments.length === 0 && (
          <div className="comments-empty">No comments yet. Add context, updates, or investigation notes below.</div>
        )}
        {comments.map(comment => (
          <article key={comment.id} className="comment-item">
            <div className="comment-meta">
              <span className="comment-author">{comment.author}</span>
              <span className="comment-date">{formatDateTime(comment.createdAtUtc)}</span>
            </div>
            <p className="comment-body">{comment.body}</p>
          </article>
        ))}
        <div className="comment-compose">
          <div className="comment-compose-head">Add Comment</div>
          <input
            value={commentAuthor}
            onChange={e => {
              setCommentAuthor(e.target.value);
              localStorage.setItem("kanban-comment-author", e.target.value);
            }}
            placeholder="Author"
          />
          <textarea
            value={commentBody}
            onChange={e => setCommentBody(e.target.value)}
            placeholder="Add Comment"
          />
          <div className="comment-compose-actions">
            <button
              className="btn primary"
              onClick={submitComment}
              disabled={commenting || !commentAuthor.trim() || !commentBody.trim()}
            >
              {commenting ? "Adding…" : "Add Comment"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

/* ============ Detail sheet ============ */
function DetailSheet({ story, epics, projectKey, onClose, onUpdate, onDelete, onAddComment, onOpenPage }) {
  const { local, saving, patch, flush, merge } = useStoryEditor(story, onUpdate);

  // Saves a pending edit first, so the page loads the story as it was left here.
  const openPage = () => {
    const id = story.id;
    flush().finally(() => onOpenPage(id));
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
              data-field="title"
              className="sheet-title"
              style={{background:"transparent", border:0, outline:"none", padding:0, width:"100%"}}
              value={local.title}
              onChange={e => patch({ title: e.target.value })}
            />
          </div>
          <div className="sheet-head-actions">
            <button className="btn ghost sheet-open-page" onClick={openPage} title="Open as page" aria-label="Open as page">
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M9.5 2.5h4v4M13.5 2.5 9 7M6.5 13.5h-4v-4M2.5 13.5 7 9"/>
              </svg>
            </button>
            <button className="btn ghost" onClick={() => { if (confirm("Delete this item?")) { onDelete(story.id); onClose(); } }}>Delete</button>
            <button className="btn" onClick={onClose}>Close</button>
          </div>
        </header>
        <div className="sheet-body">
          <StoryFields local={local} epics={epics} patch={patch}/>
          <StoryNotes key={story.id} storyId={story.id} local={local} merge={merge} patch={patch} onAddComment={onAddComment}/>
        </div>
      </aside>
    </>
  );
}

/* ============ Story page (/stories/{id}) ============ */
// A link to another story's page. A plain click opens it here; a modified click is left to the browser (new tab).
function StoryRef({ item, onOpen }) {
  return (
    <a className="story-ref" href={`/stories/${item.id}`}
       onClick={e => {
         if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
         e.preventDefault();
         onOpen(item.id);
       }}>
      <span className="story-ref-title">{item.title}</span>
      <span className="story-ref-status">{STATUS_LABEL[item.status] || item.status}</span>
    </a>
  );
}

// The claim (read-only), the external links and the dependencies in both directions.
function StoryRelations({ local, onOpenStory }) {
  const links = local.links || [];
  const blockers = local.blockers || [];
  const dependents = local.dependents || [];
  return (
    <div className="story-relations">
      <div className="sheet-section-title">Claim</div>
      <div className="story-claim">
        {local.claimant
          ? <>Claimed by <strong>{local.claimant}</strong> · expires {formatDateTime(local.claim_expires_at_utc)}</>
          : <span className="story-none">Not claimed</span>}
      </div>

      <div className="sheet-section-title">Links</div>
      {links.length === 0 ? <div className="story-none">No links</div> : (
        <ul className="story-list">
          {links.map(link => (
            <li key={link.id}>
              <span className="story-link-kind">{link.kind}</span>
              <a href={link.url} target="_blank" rel="noopener noreferrer">{link.url}</a>
            </li>
          ))}
        </ul>
      )}

      <div className="sheet-section-title">Blocked by</div>
      {blockers.length === 0 ? <div className="story-none">Nothing</div> : (
        <ul className="story-list">
          {blockers.map(item => <li key={item.id}><StoryRef item={item} onOpen={onOpenStory}/></li>)}
        </ul>
      )}

      <div className="sheet-section-title">Blocks</div>
      {dependents.length === 0 ? <div className="story-none">Nothing</div> : (
        <ul className="story-list">
          {dependents.map(item => <li key={item.id}><StoryRef item={item} onOpen={onOpenStory}/></li>)}
        </ul>
      )}
    </div>
  );
}

const CHANGE_KIND_LABEL = {
  item_created: "Created", item_updated: "Updated", item_moved: "Moved", item_deleted: "Deleted",
  claimed: "Claimed", claim_renewed: "Claim renewed", claim_released: "Claim released",
  comment_added: "Comment added", link_added: "Link added", link_removed: "Link removed",
  dependency_added: "Blocker added", dependency_removed: "Blocker removed",
};

// One value of a change entry as short text: a comment by its author, a link by its URL, a blocker by its title
// when the story still lists it, a long text cut.
function changeValue(field, value, titles) {
  if (value === null || value === undefined) return "—";
  if (field === "comment") return `by ${value.author}`;
  if (field === "link") return `${value.kind} ${value.url}`;
  if (field === "blocked_by") return titles[value] || value;
  if (field === "claim_expires_at_utc") return formatDateTime(value);
  const text = String(value);
  return text.length > 80 ? text.slice(0, 80) + "…" : text;
}

// The story's change history, newest first. Read again after each save on this page and after a comment.
function StoryHistory({ storyId, local, saving, refreshCount }) {
  const [entries, setEntries] = useState(null);
  const [error, setError] = useState(null);
  const request = useRef(0);

  useEffect(() => {
    if (saving) return;
    const current = ++request.current;
    api.listItemHistory(storyId).then(
      loaded => { if (current === request.current) { setEntries(loaded.reverse()); setError(null); } },
      e => { if (current === request.current) setError(e.message); });
  }, [storyId, saving, local.comments?.length, refreshCount]);

  const titles = useMemo(() => Object.fromEntries(
    [...(local.blockers || []), ...(local.dependents || [])].map(item => [item.id, item.title])
  ), [local.blockers, local.dependents]);

  return (
    <div className="story-history">
      <div className="sheet-section-title" style={{marginTop: 18}}>History</div>
      {error && <div className="comments-empty">Couldn't load the history: {error}</div>}
      {!error && entries === null && <div className="story-none">Loading history…</div>}
      {!error && entries?.length === 0 && <div className="comments-empty">No recorded changes.</div>}
      {!error && entries?.length > 0 && (
        <ol className="history-list">
          {entries.map(entry => (
            <li key={entry.sequence} className="history-entry">
              <div className="comment-meta">
                <span className="comment-author">{CHANGE_KIND_LABEL[entry.kind] || entry.kind}{entry.actor?.name ? ` · ${entry.actor.name}` : ""}</span>
                <span className="comment-date">{formatDateTime(entry.created_at_utc)}</span>
              </div>
              <ul className="history-changes">
                {entry.changes.filter(change => change.field !== "version").map(change => (
                  <li key={change.field}>
                    <span className="history-field">{change.field}</span>{" "}
                    {changeValue(change.field, change.old, titles)} → {changeValue(change.field, change.new, titles)}
                  </li>
                ))}
              </ul>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function StoryPage({ storyId, projects, epics, changeSeq, onLoaded, onUpdate, onAddComment, onOpenBoard, onOpenStory }) {
  const [story, setStory] = useState(null);
  const [loadState, setLoadState] = useState("loading"); // loading | ready | notFound | deleted | error
  const [loadError, setLoadError] = useState(null);
  // Counts the copies of the story taken from the change feed, so the history is read again with each.
  const [refreshCount, setRefreshCount] = useState(0);
  const { local, saving, patch, flush, discard, merge } = useStoryEditor(story, onUpdate);
  // Saves a pending edit first, so coming back to this page loads the story as it was left.
  const leave = (projectId, epicId) => { flush().finally(() => onOpenBoard(projectId, epicId)); };
  const openStory = (id) => { flush().finally(() => onOpenStory(id)); };

  const load = useCallback(async () => {
    setLoadState("loading");
    setStory(null);
    try {
      const loaded = await api.getWorkItem(storyId);
      setStory(loaded);
      setLoadState("ready");
      onLoaded(loaded);
    } catch (e) {
      setLoadError(e.message);
      setLoadState(e.status === 404 ? "notFound" : "error");
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storyId]);

  useEffect(() => { load(); }, [load]);

  /* Live updates: App's change feed (its one EventSource) counts every change; the story is read again with each. */
  const latestRefresh = useRef(0);
  // An update held back for a field with the focus: taken when the field loses it.
  const heldBack = useRef(false);
  const refresh = async () => {
    const current = ++latestRefresh.current;
    try {
      const fresh = await api.getWorkItem(storyId);
      if (current !== latestRefresh.current) return;
      heldBack.current = merge(fresh).length > 0;
      setRefreshCount(n => n + 1);
    } catch (e) {
      if (current !== latestRefresh.current) return;
      // Deleted elsewhere while open here: an edit not yet saved has nothing left to go to.
      if (e.status === 404) { discard(); setLoadState("deleted"); }
    }
  };
  const firstChangeSeq = useRef(changeSeq);
  useEffect(() => {
    if (changeSeq === firstChangeSeq.current || loadState !== "ready") return;
    void refresh();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [changeSeq]);
  // Leaving a field takes what arrived for it while it had the focus (after the focus has moved, so it is not held).
  const onFieldBlur = () => {
    if (heldBack.current) setTimeout(() => { void refresh(); }, 0);
  };

  if (loadState === "deleted") {
    return (
      <section className="story-page">
        <div className="settings-panel settings-unavailable">
          <div>
            <div className="sheet-eyebrow">Story deleted</div>
            <h2>This story was deleted</h2>
          </div>
          <p>It was deleted elsewhere while it was open here.</p>
          <div className="settings-actions">
            <button className="btn" type="button" onClick={() => leave(null, null)}>Back to board</button>
          </div>
        </div>
      </section>
    );
  }

  if (loadState === "notFound" || loadState === "error") {
    const notFound = loadState === "notFound";
    return (
      <section className="story-page">
        <div className="settings-panel settings-unavailable">
          <div>
            <div className="sheet-eyebrow">{notFound ? "Story not found" : "Story unavailable"}</div>
            <h2>{notFound ? "There is no story with this id" : "Couldn't load this story"}</h2>
          </div>
          <p>{notFound ? "It may have been deleted, or the link is wrong." : loadError}</p>
          <div className="settings-actions">
            {!notFound && <button className="btn" type="button" onClick={load}>Retry</button>}
            <button className="btn" type="button" onClick={() => leave(null, null)}>Back to board</button>
          </div>
        </div>
      </section>
    );
  }

  if (loadState === "loading" || !local) {
    return <div className="story-page"><div className="app-loading">Loading story…</div></div>;
  }

  const project = projects.find(p => p.id === local.projectId) || null;
  // App's epics are those of its selected project, which is not always the story's (until the page scopes the board,
  // or when the story's project is archived): only the story's project's epics, and its own epic from the story.
  const storyEpics = epics.filter(e => e.projectId === local.projectId && (!e.isArchived || e.id === local.epicId));
  const ownEpic = local.epic?.id === local.epicId ? local.epic : null;
  const epic = storyEpics.find(e => e.id === local.epicId) || ownEpic;
  const epicOptions = epic && !storyEpics.includes(epic) ? [...storyEpics, epic] : storyEpics;
  return (
    <section className="story-page" onBlur={onFieldBlur}>
      <nav className="story-crumbs" aria-label="Breadcrumb">
        <a href={`/?project=${local.projectId}`}
           onClick={e => { e.preventDefault(); leave(local.projectId, null); }}>
          {project?.name || "Project"}
        </a>
        {epic && <>
          <span className="sep">›</span>
          <a href={`/?project=${local.projectId}&epic=${epic.id}`}
             onClick={e => { e.preventDefault(); leave(local.projectId, epic.id); }}>
            {epic.name}
          </a>
        </>}
        <span className="sep">›</span>
        <span className="story-crumb-current">{local.title}</span>
      </nav>
      <div className="story-page-panel">
        <header className="sheet-head">
          <div style={{flex:1}}>
            <div className="sheet-eyebrow">{shortKey(project?.key, local.id)} {saving && "· saving…"}</div>
            <input
              data-field="title"
              className="sheet-title"
              style={{background:"transparent", border:0, outline:"none", padding:0, width:"100%"}}
              value={local.title}
              onChange={e => patch({ title: e.target.value })}
            />
          </div>
        </header>
        <div className="story-page-body">
          <div>
            <StoryFields local={local} epics={epicOptions} patch={patch}/>
            <StoryRelations local={local} onOpenStory={openStory}/>
          </div>
          <div>
            <StoryNotes key={local.id} storyId={local.id} local={local} merge={merge} patch={patch} onAddComment={onAddComment}/>
            <StoryHistory storyId={local.id} local={local} saving={saving} refreshCount={refreshCount}/>
          </div>
        </div>
      </div>
    </section>
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

/* ============ User settings ============ */
function tokenState(token) {
  // The admin list carries the state the server computed; the Settings list does not.
  if (token.state) return token.state.charAt(0).toUpperCase() + token.state.slice(1);
  if (token.revokedAtUtc) return "Revoked";
  if (token.expiresAtUtc && new Date(token.expiresAtUtc).getTime() <= Date.now()) return "Expired";
  return token.isActive ? "Active" : "Inactive";
}

function tokenScopeLabel(token) {
  return token.scope === "read" ? "Read only" : "Full access";
}

function formatTokenDate(iso) {
  return iso ? formatDateTime(iso) : "Never";
}

/* The value of a datetime-local input for a date, in local time, rounded up to the next whole minute. */
function toDateTimeLocal(date) {
  const d = new Date(Math.ceil(date.getTime() / 60000) * 60000);
  const pad = n => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/* The expiry a rotate starts from: the old token's lifetime, carried forward from now; none when it had none. */
function rotatedExpiryLocal(token) {
  if (!token.expiresAtUtc) return "";
  const lifetime = new Date(token.expiresAtUtc).getTime() - new Date(token.createdAtUtc).getTime();
  return lifetime > 0 ? toDateTimeLocal(new Date(Date.now() + lifetime)) : "";
}

/* Name, optional expiry and scope; shows the plaintext of the token it created once, with a copy action.
   onCreate({ name, expiresAtUtc, scope }) returns the created token, or null when it failed. */
function TokenCreateForm({ eyebrow, title, placeholder, scopeId, onCreate }) {
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [expiresAtLocal, setExpiresAtLocal] = useState("");
  const [scope, setScope] = useState("full");
  const [createdToken, setCreatedToken] = useState(null);

  const createToken = async (e) => {
    e.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) return;

    setCreating(true);
    try {
      const expiresAtUtc = expiresAtLocal
        ? new Date(expiresAtLocal).toISOString()
        : null;
      const created = await onCreate({ name: trimmed, expiresAtUtc, scope });
      if (!created) return;
      setCreatedToken(created);
      setName("");
      setExpiresAtLocal("");
      setScope("full");
    } finally {
      setCreating(false);
    }
  };

  return (
    <form className="settings-panel" onSubmit={createToken}>
      <div className="settings-panel-head">
        <div>
          <div className="sheet-eyebrow">{eyebrow}</div>
          <h2>{title}</h2>
        </div>
      </div>
      <div className="sheet-row" style={{gridTemplateColumns:"110px 1fr"}}>
        <label>Name</label>
        <input
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder={placeholder}
          maxLength={160}
        />
      </div>
      <div className="sheet-row" style={{gridTemplateColumns:"110px 1fr"}}>
        <label>Expires</label>
        <input
          type="datetime-local"
          value={expiresAtLocal}
          onChange={e => setExpiresAtLocal(e.target.value)}
        />
      </div>
      <div className="sheet-row" style={{gridTemplateColumns:"110px 1fr"}}>
        <label htmlFor={scopeId}>Scope</label>
        <select id={scopeId} value={scope} onChange={e => setScope(e.target.value)}>
          <option value="full">Full access (read and write)</option>
          <option value="read">Read only</option>
        </select>
      </div>
      <div className="settings-actions">
        <button className="btn primary" type="submit" disabled={creating || !name.trim()}>
          {creating ? "Creating..." : "Create token"}
        </button>
      </div>

      {createdToken && <TokenReveal plaintextToken={createdToken.plaintextToken} />}
    </form>
  );
}

/* The plaintext of a token just created, shown once, with a copy action. */
function TokenReveal({ plaintextToken, note }) {
  const [copyState, setCopyState] = useState("");
  useEffect(() => { setCopyState(""); }, [plaintextToken]);

  const copyToken = async () => {
    try {
      await navigator.clipboard.writeText(plaintextToken);
      setCopyState("Copied");
    } catch {
      setCopyState("Select and copy");
    }
  };

  return (
    <div className="token-reveal" role="status">
      <div>
        <div className="sheet-eyebrow">Shown once</div>
        <p>{note ? `${note} ` : ""}This token will not be visible after you leave or refresh this page.</p>
      </div>
      <div className="token-secret-row">
        <code>{plaintextToken}</code>
        <button className="btn small" type="button" onClick={copyToken}>{copyState || "Copy"}</button>
      </div>
    </div>
  );
}

/* One token: name, state, scope, prefix, created, last used and expiry; children are its actions. */
function TokenRow({ token, children }) {
  const state = tokenState(token);
  return (
    <article className={clsx("token-row", state !== "Active" && "inactive")}>
      <div className="token-main">
        <div className="token-name-row">
          <span className="token-name">{token.name}</span>
          <span className={clsx("token-state", state.toLowerCase())}>{state}</span>
          <span className="token-state">{tokenScopeLabel(token)}</span>
        </div>
        <div className="token-prefix">{token.tokenPrefix}</div>
      </div>
      <div className="token-meta">
        <span>Created <b>{formatTokenDate(token.createdAtUtc)}</b></span>
        <span>Last used <b>{formatTokenDate(token.lastUsedAtUtc)}</b></span>
        <span>Expires <b>{token.expiresAtUtc ? formatTokenDate(token.expiresAtUtc) : "Never"}</b></span>
        {/* Only the admin list carries who issued and revoked a token. */}
        {"issuedBy" in token && <span>Issued by <b>{token.issuedBy?.name || "\u2014"}</b></span>}
        {"revokedBy" in token && token.revokedAtUtc && <span>Revoked by <b>{token.revokedBy?.name || "\u2014"}</b></span>}
      </div>
      <div className="token-actions">{children}</div>
    </article>
  );
}

function UserSettingsView({ show }) {
  const [user, setUser] = useState(null);
  const [tokens, setTokens] = useState([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(null);
  // The form is remounted, which hides the plaintext it shows, when that token is revoked.
  const [shownTokenId, setShownTokenId] = useState(null);
  const [formKey, setFormKey] = useState(0);

  const loadSettings = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const [nextUser, nextTokens] = await Promise.all([
        api.getCurrentUser(),
        api.listPersonalAccessTokens(),
      ]);
      setUser(nextUser);
      setTokens(nextTokens || []);
    } catch (e) {
      setLoadError(e.message || "Settings unavailable");
      show(`Couldn't load settings: ${e.message}`);
    } finally {
      setLoading(false);
    }
  }, [show]);

  useEffect(() => { loadSettings(); }, [loadSettings]);

  const createToken = async (payload) => {
    try {
      const created = await api.createPersonalAccessToken(payload);
      setShownTokenId(created.token.id);
      setTokens(cur => [created.token, ...cur.filter(token => token.id !== created.token.id)]);
      show("Personal access token created", "success");
      return created;
    } catch (e) {
      show(`Token create failed: ${e.message}`);
      return null;
    }
  };

  const revokeToken = async (token) => {
    if (!token?.id || !confirm(`Revoke "${token.name}"? Clients using this token will stop working.`)) return;
    try {
      await api.revokePersonalAccessToken(token.id);
      await loadSettings();
      if (shownTokenId === token.id) {
        setShownTokenId(null);
        setFormKey(key => key + 1);
      }
      show("Personal access token revoked", "success");
    } catch (e) {
      show(`Revoke failed: ${e.message}`);
    }
  };

  if (loading) {
    return <div className="settings-page"><div className="app-loading">Loading settings...</div></div>;
  }

  if (loadError) {
    return (
      <section className="settings-page">
        <div className="settings-panel settings-unavailable">
          <div>
            <div className="sheet-eyebrow">Settings unavailable</div>
            <h2>Sign in to manage personal access tokens</h2>
          </div>
          <p>{loadError}</p>
          <div className="settings-actions">
            <button className="btn" type="button" onClick={loadSettings}>Retry</button>
          </div>
        </div>
      </section>
    );
  }

  return (
    <section className="settings-page">
      <div className="settings-hero">
        <div>
          <div className="sheet-eyebrow">Signed in as</div>
          <h1 className="settings-title">{user?.displayName || user?.email || "Authenticated user"}</h1>
          <div className="settings-identity-grid">
            <div>
              <span>Email</span>
              <b>{user?.email || "Not provided"}</b>
            </div>
            <div>
              <span>Issuer</span>
              <b>{user?.issuer || "Unknown"}</b>
            </div>
            <div>
              <span>Subject</span>
              <b>{user?.subject || "Unknown"}</b>
            </div>
            <div>
              <span>Last seen</span>
              <b>{formatTokenDate(user?.lastSeenAtUtc)}</b>
            </div>
          </div>
        </div>
        <a className="btn ghost" href="/auth/logout">Logout</a>
      </div>

      <div className="settings-layout">
        <TokenCreateForm
          key={formKey}
          eyebrow="New token"
          title="Personal access token"
          placeholder="MCP desktop client"
          scopeId="pat-scope"
          onCreate={createToken}
        />

        <div className="settings-panel">
          <div className="settings-panel-head">
            <div>
              <div className="sheet-eyebrow">MCP credentials</div>
              <h2>Personal access tokens</h2>
            </div>
            <button className="btn small ghost" type="button" onClick={loadSettings}>Refresh</button>
          </div>

          {tokens.length === 0 ? (
            <div className="settings-empty">No personal access tokens yet.</div>
          ) : (
            <div className="token-list">
              {tokens.map(token => (
                <TokenRow key={token.id} token={token}>
                  <button
                    className="btn small ghost"
                    type="button"
                    onClick={() => revokeToken(token)}
                    disabled={tokenState(token) !== "Active"}
                  >
                    Revoke
                  </button>
                </TokenRow>
              ))}
            </div>
          )}
        </div>
      </div>
    </section>
  );
}

/* ============ Admin: service identities ============ */
/* The tokens of one service identity. Revoke asks first; rotate takes an optional new expiry and shows the
   replacement once. refreshKey changes when a token was issued for the identity elsewhere on the page. */
function ServiceIdentityTokens({ identity, show, refreshKey, onChanged }) {
  const [tokens, setTokens] = useState([]);
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState(null);
  const [rotatingId, setRotatingId] = useState(null);
  const [rotateExpiresLocal, setRotateExpiresLocal] = useState("");
  const [replacement, setReplacement] = useState(null);
  const panelRef = useRef(null);

  // The panel sits above the identity list, which may be long: bring it into view when an identity is chosen.
  useEffect(() => { panelRef.current?.scrollIntoView?.({ block: "nearest" }); }, []);

  const loadTokens = useCallback(async () => {
    try {
      setTokens(await api.listServiceIdentityTokens(identity.id) || []);
    } catch (e) {
      show(`Couldn't load the tokens of ${identity.name}: ${e.message}`);
    } finally {
      setLoading(false);
    }
  }, [identity.id, identity.name, show]);

  useEffect(() => { loadTokens(); }, [loadTokens, refreshKey]);

  // Disabling the identity revokes the replacement as well: its plaintext is no longer worth showing.
  useEffect(() => { if (identity.disabled) setReplacement(null); }, [identity.disabled]);

  const revokeToken = async (token) => {
    if (!confirm(`Revoke "${token.name}" of ${identity.name}? Clients using this token will stop working.`)) return;
    setBusyId(token.id);
    try {
      await api.revokeServiceIdentityToken(identity.id, token.id);
      if (replacement?.id === token.id) setReplacement(null);
      show(`${token.name} revoked`, "success");
      await loadTokens();
      onChanged(token.id);
    } catch (e) {
      show(`Revoke failed: ${e.message}`);
    } finally {
      setBusyId(null);
    }
  };

  const rotateToken = async (e, token) => {
    e.preventDefault();
    setBusyId(token.id);
    try {
      const expiresAtUtc = rotateExpiresLocal ? new Date(rotateExpiresLocal).toISOString() : null;
      const rotated = await api.rotateServiceIdentityToken(identity.id, token.id, { expiresAtUtc });
      setReplacement({ id: rotated.token.id, name: rotated.token.name, plaintextToken: rotated.plaintextToken });
      setRotatingId(null);
      setRotateExpiresLocal("");
      show(`${token.name} rotated; the old token is revoked`, "success");
      await loadTokens();
      onChanged(token.id);
    } catch (e) {
      show(`Rotate failed: ${e.message}`);
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div className="settings-panel" ref={panelRef}>
      <div className="settings-panel-head">
        <div>
          <div className="sheet-eyebrow">Tokens of {identity.name}</div>
          <h2>Service identity tokens</h2>
        </div>
        <button className="btn small ghost" type="button" onClick={loadTokens}>Refresh</button>
      </div>

      {replacement && (
        <TokenReveal plaintextToken={replacement.plaintextToken} note={`Replacement for "${replacement.name}"; the old token is revoked.`} />
      )}

      {loading ? (
        <div className="settings-empty">Loading tokens...</div>
      ) : tokens.length === 0 ? (
        <div className="settings-empty">No tokens yet.</div>
      ) : (
        <div className="token-list">
          {tokens.map(token => {
            const state = tokenState(token);
            return (
              <React.Fragment key={token.id}>
                <TokenRow token={token}>
                  <button
                    className="btn small ghost"
                    type="button"
                    onClick={() => { setRotatingId(token.id); setRotateExpiresLocal(rotatedExpiryLocal(token)); }}
                    disabled={identity.disabled || state === "Revoked" || busyId === token.id || rotatingId === token.id}
                  >
                    Rotate
                  </button>
                  <button
                    className="btn small ghost"
                    type="button"
                    onClick={() => revokeToken(token)}
                    disabled={state !== "Active" || busyId === token.id}
                  >
                    Revoke
                  </button>
                </TokenRow>
                {rotatingId === token.id && (
                  <form style={{display:"grid", gap:8, minWidth:0}} onSubmit={e => rotateToken(e, token)}>
                    <div className="sheet-row" style={{gridTemplateColumns:"110px minmax(0, 1fr)"}}>
                      <label htmlFor={`rotate-expires-${token.id}`}>New expiry</label>
                      <input
                        id={`rotate-expires-${token.id}`}
                        type="datetime-local"
                        value={rotateExpiresLocal}
                        onChange={e => setRotateExpiresLocal(e.target.value)}
                        style={{minWidth:0}}
                      />
                    </div>
                    {!rotateExpiresLocal && (
                      <div className="sheet-row" style={{gridTemplateColumns:"110px minmax(0, 1fr)"}}>
                        <span />
                        <span className="rotate-no-expiry" role="note">Replacement will never expire</span>
                      </div>
                    )}
                    <div className="settings-actions">
                      <button className="btn small ghost" type="button" onClick={() => setRotatingId(null)}>Cancel</button>
                      <button className="btn small primary" type="submit" disabled={busyId === token.id}>
                        {busyId === token.id ? "Rotating..." : `Rotate "${token.name}"`}
                      </button>
                    </div>
                  </form>
                )}
              </React.Fragment>
            );
          })}
        </div>
      )}
    </div>
  );
}

function AdminView({ show }) {
  const [identities, setIdentities] = useState([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(null);
  const [selectedId, setSelectedId] = useState(null);
  const [newName, setNewName] = useState("");
  const [creating, setCreating] = useState(false);
  const [busyId, setBusyId] = useState(null);
  const [tokensRefreshKey, setTokensRefreshKey] = useState(0);
  const [issuedTokenId, setIssuedTokenId] = useState(null);
  const [issueFormKey, setIssueFormKey] = useState(0);

  const loadIdentities = useCallback(async () => {
    setLoadError(null);
    try {
      setIdentities(await api.listServiceIdentities() || []);
    } catch (e) {
      setLoadError(e.message || "Service identities unavailable");
      show(`Couldn't load service identities: ${e.message}`);
    } finally {
      setLoading(false);
    }
  }, [show]);

  useEffect(() => { loadIdentities(); }, [loadIdentities]);

  // The selected identity's tokens are listed; a disabled identity gets no token, so the issue form is not offered for it.
  const selected = identities.find(identity => identity.id === selectedId) || null;

  const replaceIdentity = (updated) =>
    setIdentities(cur => cur.map(identity => identity.id === updated.id ? updated : identity));

  const createIdentity = async (e) => {
    e.preventDefault();
    const name = newName.trim();
    if (!name) return;
    setCreating(true);
    try {
      const created = await api.createServiceIdentity(name);
      setNewName("");
      setSelectedId(created.id);
      show(`Service identity ${created.name} created`, "success");
      await loadIdentities();
    } catch (e) {
      show(`Create failed: ${e.message}`);
    } finally {
      setCreating(false);
    }
  };

  const disableIdentity = async (identity) => {
    if (!confirm(`Disable "${identity.name}"? All of its tokens are revoked and stop working, and no token can be issued for it until it is enabled again. Enabling it later does not bring the revoked tokens back.`)) return;
    setBusyId(identity.id);
    try {
      replaceIdentity(await api.disableServiceIdentity(identity.id));
      setTokensRefreshKey(key => key + 1);
      show(`${identity.name} disabled; its tokens are revoked`, "success");
    } catch (e) {
      show(`Disable failed: ${e.message}`);
    } finally {
      setBusyId(null);
    }
  };

  const enableIdentity = async (identity) => {
    setBusyId(identity.id);
    try {
      replaceIdentity(await api.enableServiceIdentity(identity.id));
      show(`${identity.name} enabled; issue it a new token`, "success");
    } catch (e) {
      show(`Enable failed: ${e.message}`);
    } finally {
      setBusyId(null);
    }
  };

  const issueToken = async (payload) => {
    if (!selected) return null;
    try {
      const created = await api.issueServiceIdentityToken(selected.id, payload);
      setIssuedTokenId(created.token.id);
      show(`Token issued for ${selected.name}`, "success");
      loadIdentities();
      setTokensRefreshKey(key => key + 1);
      return created;
    } catch (e) {
      show(`Token issue failed: ${e.message}`);
      return null;
    }
  };

  // A revoke or rotate of the token just issued takes its plaintext off the issue form, as on the Settings page.
  const tokenChanged = (tokenId) => {
    if (tokenId === issuedTokenId) {
      setIssuedTokenId(null);
      setIssueFormKey(key => key + 1);
    }
    loadIdentities();
  };

  if (loading) {
    return <div className="settings-page"><div className="app-loading">Loading service identities...</div></div>;
  }

  if (loadError) {
    return (
      <section className="settings-page">
        <div className="settings-panel settings-unavailable">
          <div>
            <div className="sheet-eyebrow">Admin unavailable</div>
            <h2>Service identities could not be loaded</h2>
          </div>
          <p>{loadError}</p>
          <div className="settings-actions">
            <button className="btn" type="button" onClick={loadIdentities}>Retry</button>
          </div>
        </div>
      </section>
    );
  }

  return (
    <section className="settings-page">
      <div className="settings-layout">
        {selected && !selected.disabled ? (
          <TokenCreateForm
            key={`${selected.id}:${issueFormKey}`}
            eyebrow={`Issue token for ${selected.name}`}
            title="Service identity token"
            placeholder={`${selected.name} orchestrator`}
            scopeId="service-token-scope"
            onCreate={issueToken}
          />
        ) : (
          <div className="settings-panel">
            <div className="settings-panel-head">
              <div>
                <div className="sheet-eyebrow">Issue token</div>
                <h2>Service identity token</h2>
              </div>
            </div>
            <div className="settings-empty">
              {selected ? `${selected.name} is disabled; enable it to issue a token for it.` : "Choose an enabled service identity to issue a token for it."}
            </div>
          </div>
        )}

        <div style={{display:"grid", gap:18, minWidth:0}}>
        {selected && (
          <ServiceIdentityTokens
            key={selected.id}
            identity={selected}
            show={show}
            refreshKey={tokensRefreshKey}
            onChanged={tokenChanged}
          />
        )}

        <div className="settings-panel">
          <div className="settings-panel-head">
            <div>
              <div className="sheet-eyebrow">Non-human callers</div>
              <h2>Service identities</h2>
            </div>
            <button className="btn small ghost" type="button" onClick={loadIdentities}>Refresh</button>
          </div>

          <form className="sheet-row" style={{gridTemplateColumns:"110px 1fr auto"}} onSubmit={createIdentity}>
            <label htmlFor="service-identity-name">New identity</label>
            <input
              id="service-identity-name"
              value={newName}
              onChange={e => setNewName(e.target.value)}
              placeholder="Dark Factory"
              maxLength={120}
            />
            <button className="btn small primary" type="submit" disabled={creating || !newName.trim()}>
              {creating ? "Creating..." : "Create identity"}
            </button>
          </form>

          {identities.length === 0 ? (
            <div className="settings-empty">No service identities yet.</div>
          ) : (
            <div className="token-list">
              {identities.map(identity => (
                <article
                  key={identity.id}
                  className={clsx("token-row", identity.disabled && "inactive", identity.id === selected?.id && "selected")}
                >
                  <div className="token-main">
                    <div className="token-name-row">
                      <span className="token-name">{identity.name}</span>
                      {identity.disabled && (
                        <span className="token-state disabled" title={`Disabled ${formatTokenDate(identity.disabledAtUtc)}`}>Disabled</span>
                      )}
                    </div>
                  </div>
                  <div className="token-meta">
                    <span>Created <b>{formatTokenDate(identity.createdAtUtc)}</b></span>
                    <span>Active tokens <b>{identity.activeTokenCount}</b></span>
                    <span>Last used <b>{formatTokenDate(identity.lastUsedAtUtc)}</b></span>
                  </div>
                  <div className="token-actions">
                    <button
                      className="btn small ghost"
                      type="button"
                      onClick={() => setSelectedId(identity.id)}
                      disabled={identity.id === selected?.id}
                    >
                      Tokens
                    </button>
                    {identity.disabled ? (
                      <button
                        className="btn small ghost"
                        type="button"
                        onClick={() => enableIdentity(identity)}
                        disabled={busyId === identity.id}
                      >
                        Enable
                      </button>
                    ) : (
                      <>
                        <button
                          className="btn small ghost"
                          type="button"
                          onClick={() => disableIdentity(identity)}
                          disabled={busyId === identity.id}
                        >
                          Disable
                        </button>
                      </>
                    )}
                  </div>
                </article>
              ))}
            </div>
          )}
        </div>

        </div>
      </div>
    </section>
  );
}

/* ============ Project modal ============ */
function ProjectModal({ open, project, onClose, onSave, onDelete }) {
  const isEdit = !!project;
  const [name, setName] = useState("");
  const [key, setKey] = useState("");
  const [description, setDescription] = useState("");
  const [isArchived, setIsArchived] = useState(false);
  const [repositoryUrl, setRepositoryUrl] = useState("");
  const [defaultBranch, setDefaultBranch] = useState("");
  const [err, setErr] = useState(null);

  useEffect(() => {
    if (!open) return;
    setName(project?.name || "");
    setKey(project?.key || "");
    setDescription(project?.description || "");
    setIsArchived(project?.isArchived || false);
    setRepositoryUrl(project?.repository_url || "");
    setDefaultBranch(project?.default_branch || "");
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
        repositoryUrl: repositoryUrl.trim() || null,
        defaultBranch: defaultBranch.trim() || null,
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
          <div className="sheet-row">
            <label>Repository</label>
            <input value={repositoryUrl} onChange={e => setRepositoryUrl(e.target.value)} placeholder="https://github.com/owner/repo.git"/>
          </div>
          <div className="sheet-row">
            <label>Branch</label>
            <input value={defaultBranch} onChange={e => setDefaultBranch(e.target.value)} placeholder="main"/>
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
function viewFromUrl() {
  const view = new URLSearchParams(window.location.search).get("view");
  return view === "settings" || view === "admin" ? view : "board";
}

// The story id of a /stories/{id} path, or null anywhere else.
function storyIdFromUrl() {
  const match = window.location.pathname.match(/^\/stories\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?$/i);
  return match ? match[1] : null;
}

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
  const [currentView, setCurrentView] = useState(() => viewFromUrl());
  // Set while the story page is showing; the URL is then /stories/{id} and the board's query is left out.
  const [currentStoryId, setCurrentStoryId] = useState(() => storyIdFromUrl());
  // Counts the changes signalled by the change feed (/api/changes).
  const [changeSeq, setChangeSeq] = useState(0);
  // Only shows the Admin entry point; the API refuses every admin call of anyone who is not an admin.
  const [isAdmin, setIsAdmin] = useState(false);

  /* URL <-> state routing plumbing */
  const skipUrlPush = useRef(false);       // suppresses pushState when state change came from popstate
  const skipEpicReset = useRef(true);      // keep URL-loaded epic on initial mount + on popstate
  const initialUrlSyncDone = useRef(false);

  const [selectedStory, setSelectedStory] = useState(null);
  const [activeDoc, setActiveDoc] = useState(null);
  const [paletteOpen, setPaletteOpen] = useState(false);

  const [showArchivedProjects, setShowArchivedProjects] = useState(false);
  const [showArchivedEpics, setShowArchivedEpics] = useState(false);

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
    if (currentView !== "board") params.set("view", currentView);
    if (currentProjectId) params.set("project", currentProjectId);
    if (currentView === "board" && currentEpicId) params.set("epic", currentEpicId);
    const qs = params.toString();
    const newUrl = currentStoryId ? `/stories/${currentStoryId}` : `/${qs ? "?" + qs : ""}`;
    const currentUrl = window.location.pathname + window.location.search;
    if (newUrl === currentUrl) {
      initialUrlSyncDone.current = true;
      return;
    }
    const state = { projectId: currentProjectId, epicId: currentEpicId, view: currentView, storyId: currentStoryId };
    if (!initialUrlSyncDone.current) {
      window.history.replaceState(state, "", newUrl);
      initialUrlSyncDone.current = true;
    } else {
      window.history.pushState(state, "", newUrl);
    }
  }, [bootstrapped, currentProjectId, currentEpicId, currentView, currentStoryId]);

  /* Sync state <- URL on back/forward */
  useEffect(() => {
    const onPop = () => {
      const params = new URLSearchParams(window.location.search);
      const nextProject = params.get("project") || null;
      const nextEpic = params.get("epic") || null;
      const nextView = viewFromUrl();
      const nextStory = storyIdFromUrl();
      skipUrlPush.current = true;
      setCurrentStoryId(nextStory);
      // A story page's URL carries no board query: leave the board state alone, the page sets its project once it loads.
      if (nextStory) return;
      setCurrentProjectId(prev => {
        // Only suppress the project-change effect's epic reset if we'd
        // otherwise clobber a valid URL-provided epic.
        if (prev !== nextProject) skipEpicReset.current = true;
        return nextProject;
      });
      setCurrentEpicId(nextEpic);
      setCurrentView(nextView);
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

  const visibleItems = useMemo(
    () => items.filter(isVisibleWorkItem),
    [items]
  );

  const boardItems = useMemo(() => {
    if (currentEpicId) return visibleItems.filter(s => s.epicId === currentEpicId);
    return visibleItems;
  }, [visibleItems, currentEpicId]);

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
      const [, admin] = await Promise.all([
        loadProjects(),
        api.getCurrentUser().then(user => !!user?.isAdmin, () => false),
      ]);
      setIsAdmin(admin);
      if (!admin) setCurrentView(view => (view === "admin" ? "board" : view));
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
  // The project the latest load is for: an answer for any other project arrived late and is dropped.
  const latestDetailProjectId = useRef(null);
  const loadProjectDetail = useCallback(async (projectId) => {
    latestDetailProjectId.current = projectId;
    if (!projectId) { setEpics([]); setItems([]); setDocs([]); return; }
    try {
      const [board, epicList] = await Promise.all([
        api.getProjectBoard(projectId, { includeArchivedEpics: true }),
        api.listProjectEpics(projectId, true),
      ]);
      if (latestDetailProjectId.current !== projectId) return;
      setEpics(epicList);
      const nextItems = board.items || [];
      setItems(nextItems);
      setSelectedStory(current => current ? nextItems.find(item => item.id === current.id) || null : current);

      // Drop the current epic selection if it isn't valid for this project
      // (e.g. a stale ?epic=... in the URL after reloading into another project).
      setCurrentEpicId(eid => (eid && !epicList.some(e => e.id === eid)) ? null : eid);

      // Load docs for all epics in the project (small N — fine).
      const nextDocs = (await Promise.all(epicList.map(e => api.listEpicDocuments(e.id).catch(() => [])))).flat();
      if (latestDetailProjectId.current !== projectId) return;
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
    let reconnecting = false;

    const scheduleRefresh = () => {
      window.clearTimeout(refreshTimer);
      refreshTimer = window.setTimeout(() => {
        if (!disposed) {
          void refreshLiveView();
          // The story page reads its story again on this; it has no EventSource of its own.
          setChangeSeq(seq => seq + 1);
        }
      }, 150);
    };

    const connect = () => {
      if (disposed) return;

      eventSource = new window.EventSource("/api/changes");
      eventSource.addEventListener("changed", scheduleRefresh);
      // Changes made while the stream was down were never signalled: a reconnect refreshes once.
      if (reconnecting) eventSource.onopen = scheduleRefresh;
      eventSource.onerror = () => {
        eventSource?.close();
        eventSource = null;

        if (!disposed) {
          reconnecting = true;
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

  // Writes the given fields of the story (every field without a list): a field left out keeps its value on the server.
  const updateStory = async (next, fields) => {
    const payload = {
      epicId: next.epicId || null,
      title: next.title,
      description: next.description || null,
      type: next.type,
      status: next.status,
      priority: next.priority,
      estimate: next.estimate ?? null,
      labels: next.labels || null,
    };
    try {
      const saved = await api.updateWorkItem(next.id, fields
        ? Object.fromEntries(Object.entries(payload).filter(([field]) => fields.includes(field)))
        : payload);
      setItems(cur => cur.map(s => s.id === saved.id ? saved : s));
      // The sheet as it is now, not as it was when the request started: it may show another story by then.
      setSelectedStory(current => current?.id === saved.id ? saved : current);
      return saved;
    } catch (e) {
      show(`Save failed: ${e.message}`);
      return null;
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

  const addComment = async (id, payload) => {
    try {
      const saved = await api.addWorkItemComment(id, payload);
      setItems(cur => cur.map(item => item.id === saved.id ? saved : item));
      // The sheet as it is now, not as it was when the request started: it may show another story by then.
      setSelectedStory(current => current?.id === saved.id ? saved : current);
      return saved;
    } catch (e) {
      show(`Comment failed: ${e.message}`);
      throw e;
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

  if (projects.length === 0 && currentView === "board" && !currentStoryId) {
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

  const visibleEpics = epics.filter(e => showArchivedEpics || !e.isArchived || currentEpicId === e.id);
  const openSettings = () => {
    setSelectedStory(null);
    setActiveDoc(null);
    setPaletteOpen(false);
    setCurrentStoryId(null);
    setCurrentView("settings");
  };
  const openAdmin = () => {
    setSelectedStory(null);
    setActiveDoc(null);
    setPaletteOpen(false);
    setCurrentStoryId(null);
    setCurrentView("admin");
  };
  const openBoard = () => {
    setCurrentStoryId(null);
    setCurrentView("board");
  };

  /* Story page */
  // Points the board at a project and epic without the project change clearing the epic.
  const scopeBoard = (projectId, epicId) => {
    setCurrentProjectId(prev => {
      if (prev !== projectId) skipEpicReset.current = true;
      return projectId;
    });
    setCurrentEpicId(epicId);
  };
  const openStoryPage = (id) => {
    setSelectedStory(null);
    setActiveDoc(null);
    setPaletteOpen(false);
    setCurrentStoryId(id);
  };
  // The breadcrumb: back to the board, scoped to the story's project and epic when it has them.
  const openBoardFromStory = (projectId, epicId) => {
    if (projectId) scopeBoard(projectId, epicId);
    openBoard();
  };
  const onStoryLoaded = (story) => scopeBoard(story.projectId, story.epicId || null);

  return (
    <div className="shell">
      <Sidebar
        projects={projects}
        currentProjectId={currentProjectId}
        setProjectId={(id) => {
          setCurrentProjectId(id);
          setCurrentStoryId(null);
          setCurrentView("board");
        }}
        onNewProject={() => setProjectModal({ open: true, project: null })}
        showArchived={showArchivedProjects}
        setShowArchived={setShowArchivedProjects}
      />
      <main className="content">
        <Topbar
          project={currentProject}
          epic={currentEpic}
          view={currentStoryId ? "story" : currentView === "admin" && !isAdmin ? "board" : currentView}
          isAdmin={isAdmin}
          onOpenPalette={() => setPaletteOpen(true)}
          onOpenTweaks={() => setTweaksOpen(v => !v)}
          onNewItem={() => setPaletteOpen(true)}
          onOpenSettings={openSettings}
          onOpenAdmin={openAdmin}
          onOpenBoard={openBoard}
        />
        {currentStoryId ? (
          <StoryPage
            key={currentStoryId}
            storyId={currentStoryId}
            projects={projects}
            epics={epics}
            changeSeq={changeSeq}
            onLoaded={onStoryLoaded}
            onUpdate={updateStory}
            onAddComment={addComment}
            onOpenBoard={openBoardFromStory}
            onOpenStory={openStoryPage}
          />
        ) : currentView === "settings" ? (
          <UserSettingsView show={show} />
        ) : currentView === "admin" && isAdmin ? (
          <AdminView show={show} />
        ) : (
          <>
            <EpicStrip
              epics={visibleEpics}
              items={items}
              currentEpicId={currentEpicId}
              setEpicId={setCurrentEpicId}
              onNewEpic={() => setEpicModal({ open: true, epic: null })}
              showArchived={showArchivedEpics}
              setShowArchived={setShowArchivedEpics}
            />
            <EpicDetail
              epic={currentEpic}
              project={currentProject}
              items={boardItems}
              docs={epicDocs}
              onOpenDoc={(d) => setActiveDoc(d)}
              onNewDoc={openNewDoc}
              onEditEpic={() => setEpicModal({ open: true, epic: currentEpic })}
              onEditProject={() => setProjectModal({ open: true, project: currentProject })}
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
          </>
        )}
      </main>

      <CommandPalette
        open={paletteOpen}
        onClose={() => setPaletteOpen(false)}
        items={visibleItems}
        projects={projects}
        epics={epics}
        currentProject={currentProject}
        onOpenStory={(s) => setSelectedStory(s)}
        onQuickAdd={quickAdd}
        setProjectId={(id) => { setCurrentProjectId(id); openBoard(); }}
        setEpicId={(id) => { setCurrentEpicId(id); openBoard(); }}
      />
      <DetailSheet
        story={selectedStory}
        projectKey={currentProject?.key}
        epics={epics.filter(e => !e.isArchived)}
        onClose={() => setSelectedStory(null)}
        onUpdate={updateStory}
        onDelete={deleteStory}
        onAddComment={addComment}
        onOpenPage={openStoryPage}
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
