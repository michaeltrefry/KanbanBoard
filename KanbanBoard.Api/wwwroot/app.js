const statusOrder = ["Backlog", "Ready", "InProgress", "Blocked", "Done"];
const statusLabels = {
  Backlog: "Backlog",
  Ready: "Ready",
  InProgress: "In Progress",
  Blocked: "Blocked",
  Done: "Done"
};

const state = {
  projects: [],
  board: null,
  epics: [],
  epicDocuments: [],
  selectedProjectId: null,
  selectedEpicId: null,
  selectedDocumentId: null,
  draggedItemId: null,
  activeDetailItem: null,
  showArchivedProjects: false,
  showArchivedEpics: false,
  editingEpicId: null,
  editingDocumentId: null,
  feedbackTimeoutId: null
};

const projectList = document.querySelector("#project-list");
const board = document.querySelector("#board");
const boardTitle = document.querySelector("#board-title");
const boardDescription = document.querySelector("#board-description");
const projectStats = document.querySelector("#project-stats");
const feedbackBanner = document.querySelector("#feedback-banner");
const epicList = document.querySelector("#epic-list");
const epicFocusTitle = document.querySelector("#epic-focus-title");
const epicFocusBadge = document.querySelector("#epic-focus-badge");
const epicFocusDescription = document.querySelector("#epic-focus-description");
const epicFocusStats = document.querySelector("#epic-focus-stats");
const epicDocumentsList = document.querySelector("#epic-documents-list");
const epicDocumentTitle = document.querySelector("#epic-document-title");
const epicDocumentBody = document.querySelector("#epic-document-body");
const projectForm = document.querySelector("#project-form");
const projectNameInput = document.querySelector("#project-name");
const projectKeyInput = document.querySelector("#project-key");
const projectDescriptionInput = document.querySelector("#project-description");
const newProjectButton = document.querySelector("#new-project-button");
const showArchivedProjectsToggle = document.querySelector("#show-archived-projects");
const archiveProjectButton = document.querySelector("#archive-project-button");
const restoreProjectButton = document.querySelector("#restore-project-button");
const deleteProjectButton = document.querySelector("#delete-project-button");
const projectArchiveBadge = document.querySelector("#project-archive-badge");
const itemForm = document.querySelector("#item-form");
const itemModal = document.querySelector("#item-modal");
const openItemModalButton = document.querySelector("#open-item-modal-button");
const closeItemModalButton = document.querySelector("#close-item-modal-button");
const cancelItemModalButton = document.querySelector("#cancel-item-modal-button");
const itemModalBackdrop = document.querySelector("#item-modal-backdrop");
const itemTitleInput = document.querySelector("#item-title");
const itemPrioritySelect = document.querySelector("#item-priority");
const itemTypeSelect = document.querySelector("#item-type");
const itemStatusSelect = document.querySelector("#item-status");
const itemDescriptionInput = document.querySelector("#item-description");
const itemEstimateInput = document.querySelector("#item-estimate");
const itemLabelsInput = document.querySelector("#item-labels");
const itemEpicSelect = document.querySelector("#item-epic");
const newEpicButton = document.querySelector("#new-epic-button");
const editEpicButton = document.querySelector("#edit-epic-button");
const showArchivedEpicsToggle = document.querySelector("#show-archived-epics");
const archiveEpicButton = document.querySelector("#archive-epic-button");
const restoreEpicButton = document.querySelector("#restore-epic-button");
const deleteEpicButton = document.querySelector("#delete-epic-button");
const epicArchiveBadge = document.querySelector("#epic-archive-badge");
const epicModal = document.querySelector("#epic-modal");
const epicModalTitle = document.querySelector("#epic-modal-title");
const closeEpicModalButton = document.querySelector("#close-epic-modal-button");
const cancelEpicModalButton = document.querySelector("#cancel-epic-modal-button");
const epicModalBackdrop = document.querySelector("#epic-modal-backdrop");
const epicForm = document.querySelector("#epic-form");
const epicNameInput = document.querySelector("#epic-name");
const epicDescriptionInput = document.querySelector("#epic-description");
const epicFormError = document.querySelector("#epic-form-error");
const newDocumentButton = document.querySelector("#new-document-button");
const editDocumentButton = document.querySelector("#edit-document-button");
const documentModal = document.querySelector("#document-modal");
const documentModalTitle = document.querySelector("#document-modal-title");
const closeDocumentModalButton = document.querySelector("#close-document-modal-button");
const cancelDocumentModalButton = document.querySelector("#cancel-document-modal-button");
const documentModalBackdrop = document.querySelector("#document-modal-backdrop");
const documentForm = document.querySelector("#document-form");
const documentTitleInput = document.querySelector("#document-title-input");
const documentBodyInput = document.querySelector("#document-body-input");
const documentFormError = document.querySelector("#document-form-error");
const detailModal = document.querySelector("#detail-modal");
const detailModalBackdrop = document.querySelector("#detail-modal-backdrop");
const closeDetailModalButton = document.querySelector("#close-detail-modal-button");
const detailModalTitle = document.querySelector("#detail-modal-title");
const detailType = document.querySelector("#detail-type");
const detailPriority = document.querySelector("#detail-priority");
const detailStatus = document.querySelector("#detail-status");
const detailEpic = document.querySelector("#detail-epic");
const detailEstimate = document.querySelector("#detail-estimate");
const detailLabels = document.querySelector("#detail-labels");
const detailDescription = document.querySelector("#detail-description");
const editDetailButton = document.querySelector("#edit-detail-button");
const detailViewMode = document.querySelector("#detail-view-mode");
const detailEditForm = document.querySelector("#detail-edit-form");
const detailEditTitle = document.querySelector("#detail-edit-title");
const detailEditDescription = document.querySelector("#detail-edit-description");
const detailEditType = document.querySelector("#detail-edit-type");
const detailEditStatus = document.querySelector("#detail-edit-status");
const detailEditPriority = document.querySelector("#detail-edit-priority");
const detailEditEpic = document.querySelector("#detail-edit-epic");
const detailEditEstimate = document.querySelector("#detail-edit-estimate");
const detailEditLabels = document.querySelector("#detail-edit-labels");
const cancelDetailEditButton = document.querySelector("#cancel-detail-edit-button");
const detailEditError = document.querySelector("#detail-edit-error");

newProjectButton.addEventListener("click", () => {
  state.selectedProjectId = null;
  projectForm.reset();
  projectKeyInput.value = "";
  syncProjectArchiveControls(null);
  updateActionAvailability();
  projectNameInput.focus();
});

showArchivedProjectsToggle.addEventListener("change", async event => {
  state.showArchivedProjects = event.target.checked;
  await withFeedback(async () => {
    await loadProjects();
  }, "Could not reload projects.");
});

archiveProjectButton.addEventListener("click", async () => {
  await updateProjectArchiveState(true);
});

restoreProjectButton.addEventListener("click", async () => {
  await updateProjectArchiveState(false);
});

deleteProjectButton.addEventListener("click", async () => {
  await deleteSelectedProject();
});

openItemModalButton.addEventListener("click", () => openItemModal());
closeItemModalButton.addEventListener("click", () => closeModal(itemModal, openItemModalButton));
cancelItemModalButton.addEventListener("click", () => closeModal(itemModal, openItemModalButton));
itemModalBackdrop.addEventListener("click", () => closeModal(itemModal, openItemModalButton));

newEpicButton.addEventListener("click", () => openEpicModal());
editEpicButton.addEventListener("click", () => openEpicModal(getSelectedEpic()));
showArchivedEpicsToggle.addEventListener("change", async event => {
  state.showArchivedEpics = event.target.checked;
  await withFeedback(async () => {
    if (state.selectedProjectId) {
      await loadBoard(state.selectedProjectId);
    } else {
      renderEpicWorkspace();
    }
  }, "Could not reload epics.");
});
archiveEpicButton.addEventListener("click", async () => {
  await updateEpicArchiveState(true);
});
restoreEpicButton.addEventListener("click", async () => {
  await updateEpicArchiveState(false);
});
deleteEpicButton.addEventListener("click", async () => {
  await deleteSelectedEpic();
});
closeEpicModalButton.addEventListener("click", () => closeModal(epicModal, newEpicButton));
cancelEpicModalButton.addEventListener("click", () => closeModal(epicModal, newEpicButton));
epicModalBackdrop.addEventListener("click", () => closeModal(epicModal, newEpicButton));

newDocumentButton.addEventListener("click", () => openDocumentModal());
editDocumentButton.addEventListener("click", () => openDocumentModal(getSelectedDocument()));
closeDocumentModalButton.addEventListener("click", () => closeModal(documentModal, newDocumentButton));
cancelDocumentModalButton.addEventListener("click", () => closeModal(documentModal, newDocumentButton));
documentModalBackdrop.addEventListener("click", () => closeModal(documentModal, newDocumentButton));

closeDetailModalButton.addEventListener("click", closeDetailModal);
detailModalBackdrop.addEventListener("click", closeDetailModal);
editDetailButton.addEventListener("click", startDetailEditMode);
cancelDetailEditButton.addEventListener("click", stopDetailEditMode);

document.addEventListener("keydown", event => {
  if (event.key !== "Escape") {
    return;
  }

  if (!detailModal.hidden) {
    closeDetailModal();
    return;
  }

  if (!documentModal.hidden) {
    closeModal(documentModal, newDocumentButton);
    return;
  }

  if (!epicModal.hidden) {
    closeModal(epicModal, newEpicButton);
    return;
  }

  if (!itemModal.hidden) {
    closeModal(itemModal, openItemModalButton);
  }
});

projectForm.addEventListener("submit", async event => {
  event.preventDefault();

  const payload = {
    name: projectNameInput.value,
    key: projectKeyInput.value,
    description: projectDescriptionInput.value || null,
    isArchived: getSelectedProject()?.isArchived ?? false
  };

  const isEditing = Boolean(state.selectedProjectId);
  const url = isEditing ? `/api/projects/${state.selectedProjectId}` : "/api/projects";
  const method = isEditing ? "PUT" : "POST";

  try {
    const savedProject = await fetchJson(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    state.selectedProjectId = savedProject.id;
    await loadProjects();
    showFeedback(isEditing ? "Project updated." : "Project created and selected.");
  } catch (error) {
    showFeedback(error.message || "Could not save project.", "error");
  }
});

itemForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (!state.selectedProjectId) {
    showFeedback("Select a project before creating work.", "error");
    return;
  }

  const estimateValue = itemEstimateInput.value;
  const payload = {
    projectId: state.selectedProjectId,
    epicId: itemEpicSelect.value || null,
    title: itemTitleInput.value,
    description: itemDescriptionInput.value || null,
    type: itemTypeSelect.value,
    status: itemStatusSelect.value,
    priority: itemPrioritySelect.value,
    estimate: estimateValue ? Number(estimateValue) : null,
    labels: itemLabelsInput.value || null
  };

  try {
    await fetchJson("/api/items", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    itemForm.reset();
    itemPrioritySelect.value = "Medium";
    closeModal(itemModal, openItemModalButton);
    await loadBoard(state.selectedProjectId);
    showFeedback("Work item created.");
  } catch (error) {
    showFeedback(error.message || "Could not create work item.", "error");
  }
});

epicForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (!state.selectedProjectId) {
    showFeedback("Select a project before creating an epic.", "error");
    return;
  }

  const selectedEpic = getSelectedEpic();
  const isEditing = Boolean(state.editingEpicId);
  const url = isEditing
    ? `/api/epics/${state.editingEpicId}`
    : `/api/projects/${state.selectedProjectId}/epics`;
  const method = isEditing ? "PUT" : "POST";
  const payload = {
    name: epicNameInput.value,
    description: epicDescriptionInput.value || null,
    isArchived: getSelectedEpic()?.isArchived ?? false
  };

  try {
    epicFormError.hidden = true;
    epicFormError.textContent = "";

    const savedEpic = await fetchJson(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    state.selectedEpicId = savedEpic.id;
    await loadBoard(state.selectedProjectId);
    closeModal(epicModal, isEditing ? editEpicButton : newEpicButton);
    showFeedback(isEditing ? "Epic updated." : "Epic created.");
    if (!selectedEpic || selectedEpic.id !== savedEpic.id) {
      state.selectedDocumentId = null;
    }
  } catch (error) {
    epicFormError.hidden = false;
    epicFormError.textContent = error.message || "Could not save epic.";
  }
});

documentForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (!state.selectedEpicId) {
    showFeedback("Select an epic before saving a document.", "error");
    return;
  }

  const isEditing = Boolean(state.editingDocumentId);
  const url = isEditing
    ? `/api/epic-documents/${state.editingDocumentId}`
    : `/api/epics/${state.selectedEpicId}/documents`;
  const method = isEditing ? "PUT" : "POST";
  const payload = {
    title: documentTitleInput.value,
    body: documentBodyInput.value
  };

  try {
    documentFormError.hidden = true;
    documentFormError.textContent = "";

    const savedDocument = await fetchJson(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    state.selectedDocumentId = savedDocument.id;
    await loadSelectedEpicDocuments();
    renderEpicWorkspace();
    updateActionAvailability();
    closeModal(documentModal, isEditing ? editDocumentButton : newDocumentButton);
    showFeedback(isEditing ? "Document updated." : "Document created.");
  } catch (error) {
    documentFormError.hidden = false;
    documentFormError.textContent = error.message || "Could not save document.";
  }
});

detailEditForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (!state.activeDetailItem) {
    return;
  }

  const estimateValue = detailEditEstimate.value;
  const payload = {
    epicId: detailEditEpic.value || null,
    title: detailEditTitle.value,
    description: detailEditDescription.value || null,
    type: detailEditType.value,
    status: detailEditStatus.value,
    priority: detailEditPriority.value,
    estimate: estimateValue ? Number(estimateValue) : null,
    labels: detailEditLabels.value || null
  };

  detailEditError.hidden = true;
  detailEditError.textContent = "";

  try {
    const updatedItem = await fetchJson(`/api/items/${state.activeDetailItem.id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    state.activeDetailItem = updatedItem;
    await loadBoard(state.selectedProjectId);
    populateDetailModal(updatedItem);
    stopDetailEditMode();
    showFeedback("Work item updated.");
  } catch (error) {
    detailEditError.hidden = false;
    detailEditError.textContent = error.message || "Could not save changes.";
  }
});

async function loadProjects() {
  state.projects = await fetchJson(`/api/projects?includeArchived=${state.showArchivedProjects}`);

  if (state.selectedProjectId && !state.projects.some(project => project.id === state.selectedProjectId)) {
    state.selectedProjectId = state.projects[0]?.id ?? null;
  }

  if (!state.selectedProjectId && state.projects.length > 0) {
    state.selectedProjectId = state.projects[0].id;
  }

  renderProjectList();

  if (state.selectedProjectId) {
    await loadBoard(state.selectedProjectId);
  } else {
    state.board = null;
    state.epics = [];
    state.epicDocuments = [];
    state.selectedEpicId = null;
    state.selectedDocumentId = null;
    board.innerHTML = "";
    renderEpicWorkspace();
    projectForm.reset();
    projectKeyInput.value = "";
    boardTitle.textContent = "No active project selected";
    boardDescription.textContent = state.showArchivedProjects
      ? "Choose a project or create a new one."
      : "No active projects are visible. Toggle archived projects or create a new one.";
    projectStats.textContent = "";
    syncProjectArchiveControls(null);
    updateActionAvailability();
  }
}

async function loadBoard(projectId) {
  const boardUrl = `/api/projects/${projectId}?includeArchivedEpics=${state.showArchivedEpics}`;
  const epicsUrl = `/api/projects/${projectId}/epics?includeArchived=${state.showArchivedEpics}`;
  const [boardData, epicData] = await Promise.all([
    fetchJson(boardUrl),
    fetchJson(epicsUrl)
  ]);

  state.board = boardData;
  state.epics = epicData;
  state.selectedProjectId = projectId;
  if (!state.epics.some(epic => epic.id === state.selectedEpicId)) {
    state.selectedEpicId = null;
  }

  await loadSelectedEpicDocuments();

  if (state.activeDetailItem) {
    state.activeDetailItem = state.board.items.find(item => item.id === state.activeDetailItem.id) ?? state.activeDetailItem;
  }

  syncEpicSelectOptions();
  renderProjectList();
  renderEpicWorkspace();
  renderBoard();
  populateProjectEditor();
  updateActionAvailability();
}

function renderProjectList() {
  projectList.innerHTML = "";

  for (const project of state.projects) {
    const safeName = project.name || "Untitled project";
    const safeKey = project.key || "No key";
    const button = document.createElement("button");
    button.type = "button";
    button.className = `project-tile ${project.id === state.selectedProjectId ? "active" : ""}`;
    button.innerHTML = `
      <strong>${escapeHtml(safeName)}</strong>
      <div class="subtle">${escapeHtml(safeKey)}</div>
      <div class="subtle">${project.isArchived ? "Archived project" : "Active project"}</div>
      <div class="subtle">${project.openItems} open / ${project.totalItems} total</div>
    `;
    button.addEventListener("click", async () => {
      await loadBoard(project.id);
    });
    projectList.appendChild(button);
  }
}

function renderBoard() {
  if (!state.board) {
    return;
  }

  const visibleItems = getVisibleItems();
  const selectedEpic = getSelectedEpic();
  boardTitle.textContent = selectedEpic ? `${state.board.name} / ${selectedEpic.name}` : state.board.name || "Untitled project";
  boardDescription.textContent = selectedEpic
    ? selectedEpic.description || "This epic does not have a description yet."
    : state.board.description || "No project description yet.";
  const openCount = visibleItems.filter(item => item.status !== "Done").length;
  projectStats.textContent = `${openCount} active ${selectedEpic ? "items in epic" : "items"}`;

  board.innerHTML = "";

  for (const status of statusOrder) {
    const items = getOrderedItemsByStatus(visibleItems, status);

    const column = document.createElement("section");
    column.className = "column";
    column.dataset.status = status;
    column.innerHTML = `
      <div class="column-header">
        <h3>${statusLabels[status]}</h3>
        <span class="stat-pill">${items.length}</span>
      </div>
      <div class="column-items"></div>
    `;

    const columnItems = column.querySelector(".column-items");
    columnItems.addEventListener("dragover", event => event.preventDefault());
    columnItems.addEventListener("drop", async event => {
      event.preventDefault();
      if (!state.draggedItemId) {
        return;
      }

      try {
        await moveItem(state.draggedItemId, status, getStatusItems(status).length);
        showFeedback(`Moved item to ${statusLabels[status]}.`);
      } catch (error) {
        showFeedback(error.message || "Could not move item.", "error");
      } finally {
        state.draggedItemId = null;
      }
    });

    for (const item of items) {
      const node = createCard(item);
      columnItems.appendChild(node);
    }

    board.appendChild(column);
  }
}

function createCard(item) {
  const template = document.querySelector("#item-template");
  const node = template.content.firstElementChild.cloneNode(true);
  node.dataset.id = item.id;
  node.querySelector(".item-type").textContent = item.type;
  const priorityBadge = node.querySelector(".item-priority");
  priorityBadge.textContent = item.priority;
  priorityBadge.classList.add(`priority-${item.priority}`);
  node.querySelector(".item-title").textContent = item.title || "Untitled item";
  node.querySelector(".item-title-button").addEventListener("click", () => openDetailModal(item));
  node.querySelector(".item-description").textContent = item.description || "No description";
  node.querySelector(".estimate").textContent = item.estimate ? `${item.estimate} pts` : "No estimate";
  node.querySelector(".epic-name").textContent = item.epic?.name ? `Epic: ${item.epic.name}` : "No epic";
  node.querySelector(".labels").textContent = item.labels || "No labels";

  node.addEventListener("dragstart", () => {
    state.draggedItemId = item.id;
    node.classList.add("dragging");
  });

  node.addEventListener("dragend", () => {
    node.classList.remove("dragging");
    state.draggedItemId = null;
  });

  node.querySelector(".move-up").addEventListener("click", async () => {
    await reorderWithinStatus(item, -1);
  });
  node.querySelector(".move-down").addEventListener("click", async () => {
    await reorderWithinStatus(item, 1);
  });
  node.querySelector(".move-left").addEventListener("click", async () => {
    await stepMove(item, -1);
  });
  node.querySelector(".move-right").addEventListener("click", async () => {
    await stepMove(item, 1);
  });
  node.querySelector(".delete-button").addEventListener("click", async () => {
    if (!window.confirm(`Delete "${item.title || "this item"}"?`)) {
      return;
    }

    try {
      await fetchJson(`/api/items/${item.id}`, { method: "DELETE" });
      await loadBoard(state.selectedProjectId);
      showFeedback("Work item deleted.");
    } catch (error) {
      showFeedback(error.message || "Could not delete work item.", "error");
    }
  });

  return node;
}

async function reorderWithinStatus(item, delta) {
  const statusItems = getStatusItems(item.status);
  const currentIndex = statusItems.findIndex(candidate => candidate.id === item.id);
  const nextIndex = currentIndex + delta;
  if (currentIndex === -1 || nextIndex < 0 || nextIndex >= statusItems.length) {
    return;
  }

  try {
    await moveItem(item.id, item.status, nextIndex);
    showFeedback(delta < 0 ? "Moved card up." : "Moved card down.");
  } catch (error) {
    showFeedback(error.message || "Could not reorder card.", "error");
  }
}

async function stepMove(item, delta) {
  const currentIndex = statusOrder.indexOf(item.status);
  const nextIndex = currentIndex + delta;
  if (nextIndex < 0 || nextIndex >= statusOrder.length) {
    return;
  }

  const nextStatus = statusOrder[nextIndex];
  const nextOrder = getStatusItems(nextStatus).length;

  try {
    await moveItem(item.id, nextStatus, nextOrder);
    showFeedback(`Moved item to ${statusLabels[nextStatus]}.`);
  } catch (error) {
    showFeedback(error.message || "Could not move item.", "error");
  }
}

async function moveItem(itemId, status, order) {
  await fetchJson(`/api/items/${itemId}/move`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ status, order })
  });

  await loadBoard(state.selectedProjectId);
}

function populateProjectEditor() {
  const selected = getSelectedProject();
  if (!selected) {
    projectForm.reset();
    projectKeyInput.value = "";
    syncProjectArchiveControls(null);
    return;
  }

  projectNameInput.value = selected.name;
  projectKeyInput.value = selected.key;
  projectDescriptionInput.value = selected.description || "";
  syncProjectArchiveControls(selected);
}

function renderEpicWorkspace() {
  if (!state.board) {
    epicList.innerHTML = '<div class="empty-note">Select a project to browse its epics.</div>';
    epicFocusTitle.textContent = "All project work";
    epicFocusBadge.textContent = "All Work";
    epicFocusDescription.textContent = "Choose a project to browse its epics, work items, and planning documents.";
    epicFocusStats.innerHTML = "";
    epicDocumentsList.innerHTML = '<div class="empty-note">Epic documents will appear here after a project is selected.</div>';
    epicDocumentTitle.textContent = "Select a project";
    epicDocumentBody.textContent = "Pick a project first, then choose an epic to read its documents.";
    epicDocumentBody.className = "document-body subtle";
    updateActionAvailability();
    return;
  }

  renderEpicList();
  renderEpicFocus();
  updateActionAvailability();
}

function renderEpicList() {
  epicList.innerHTML = "";

  const allButton = document.createElement("button");
  allButton.type = "button";
  allButton.className = `epic-tile ${state.selectedEpicId ? "" : "active"}`;
  allButton.innerHTML = `
    <strong>All project work</strong>
    <div class="subtle">See every work item across the project.</div>
  `;
  allButton.addEventListener("click", async () => selectEpic(null));
  epicList.appendChild(allButton);

  if (state.epics.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty-note";
    empty.textContent = "No epics yet. Create one to group delivery work and documents.";
    epicList.appendChild(empty);
    return;
  }

  for (const epic of state.epics) {
    const itemCount = state.board.items.filter(item => item.epicId === epic.id).length;
    const button = document.createElement("button");
    button.type = "button";
    button.className = `epic-tile ${epic.id === state.selectedEpicId ? "active" : ""}`;
    button.innerHTML = `
      <strong>${escapeHtml(epic.name)}</strong>
      <div class="subtle">${escapeHtml(epic.description || "No epic description yet.")}</div>
      <div class="subtle">${epic.isArchived ? "Archived epic" : "Active epic"}</div>
      <div class="subtle">${itemCount} linked work items</div>
    `;
    button.addEventListener("click", async () => selectEpic(epic.id));
    epicList.appendChild(button);
  }
}

function renderEpicFocus() {
  const selectedEpic = getSelectedEpic();
  const visibleItems = getVisibleItems();
  const openItems = visibleItems.filter(item => item.status !== "Done").length;

  epicFocusTitle.textContent = selectedEpic ? selectedEpic.name : "All project work";
  epicFocusBadge.textContent = selectedEpic ? "Epic Focus" : "All Work";
  epicFocusDescription.textContent = selectedEpic
    ? selectedEpic.description || "This epic groups related work and planning docs for one feature track."
    : "Browse every work item in the project or narrow the board to a specific epic.";

  epicFocusStats.innerHTML = "";
  for (const stat of [
    `${visibleItems.length} items`,
    `${openItems} active`,
    selectedEpic ? `${state.epicDocuments.length} docs` : `${state.epics.length} epics`
  ]) {
    const pill = document.createElement("span");
    pill.className = "ghost-badge";
    pill.textContent = stat;
    epicFocusStats.appendChild(pill);
  }

  renderEpicDocuments(selectedEpic);
  syncEpicArchiveControls(selectedEpic);
}

function renderEpicDocuments(selectedEpic) {
  epicDocumentsList.innerHTML = "";

  if (!selectedEpic) {
    const note = document.createElement("div");
    note.className = "empty-note";
    note.textContent = "Select an epic to view its PRDs, plans, and working notes.";
    epicDocumentsList.appendChild(note);
    epicDocumentTitle.textContent = "Select an epic document";
    epicDocumentBody.textContent = "Choose an epic first, then pick one of its documents to preview it here.";
    epicDocumentBody.className = "document-body subtle";
    return;
  }

  if (state.epicDocuments.length === 0) {
    const note = document.createElement("div");
    note.className = "empty-note";
    note.textContent = "No documents have been added to this epic yet. Start with a PRD or implementation plan.";
    epicDocumentsList.appendChild(note);
    epicDocumentTitle.textContent = "No epic documents";
    epicDocumentBody.textContent = "This epic is ready for PRDs, implementation plans, and working notes.";
    epicDocumentBody.className = "document-body subtle";
    return;
  }

  for (const document of state.epicDocuments) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `document-tile ${document.id === state.selectedDocumentId ? "active" : ""}`;
    button.innerHTML = `
      <strong>${escapeHtml(document.title)}</strong>
      <div class="subtle">${formatRelativeDocument(document)}</div>
    `;
    button.addEventListener("click", () => {
      state.selectedDocumentId = document.id;
      renderEpicDocuments(selectedEpic);
      updateActionAvailability();
    });
    epicDocumentsList.appendChild(button);
  }

  const activeDocument = getSelectedDocument();
  epicDocumentTitle.textContent = activeDocument.title;
  epicDocumentBody.textContent = activeDocument.body;
  epicDocumentBody.className = "document-body";
}

async function selectEpic(epicId) {
  state.selectedEpicId = epicId;
  await loadSelectedEpicDocuments();
  renderEpicWorkspace();
  renderBoard();
}

function getSelectedProject() {
  return state.projects.find(project => project.id === state.selectedProjectId) ?? null;
}

function getSelectedEpic() {
  return state.epics.find(epic => epic.id === state.selectedEpicId) ?? null;
}

function getSelectedDocument() {
  return state.epicDocuments.find(document => document.id === state.selectedDocumentId) ?? state.epicDocuments[0] ?? null;
}

function getVisibleItems() {
  if (!state.selectedEpicId) {
    return state.board.items;
  }

  return state.board.items.filter(item => item.epicId === state.selectedEpicId);
}

function getStatusItems(status) {
  return [...state.board.items]
    .filter(item => item.status === status)
    .sort(compareItems);
}

function getOrderedItemsByStatus(items, status) {
  return [...items]
    .filter(item => item.status === status)
    .sort(compareItems);
}

function compareItems(left, right) {
  if (left.order !== right.order) {
    return left.order - right.order;
  }

  return new Date(left.createdAtUtc) - new Date(right.createdAtUtc);
}

function syncProjectArchiveControls(project) {
  const archived = project?.isArchived ?? false;
  projectArchiveBadge.hidden = !archived;
  archiveProjectButton.hidden = !project || archived;
  restoreProjectButton.hidden = !project || !archived;
  deleteProjectButton.hidden = !project;
}

function syncEpicArchiveControls(epic) {
  const archived = epic?.isArchived ?? false;
  epicArchiveBadge.hidden = !archived;
  archiveEpicButton.hidden = !epic || archived;
  restoreEpicButton.hidden = !epic || !archived;
  deleteEpicButton.hidden = !epic;
}

async function updateProjectArchiveState(isArchived) {
  const selected = getSelectedProject();
  if (!selected) {
    showFeedback("Select a project first.", "error");
    return;
  }

  try {
    await fetchJson(`/api/projects/${selected.id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: selected.name,
        key: selected.key,
        description: selected.description,
        isArchived
      })
    });

    if (isArchived && !state.showArchivedProjects) {
      state.selectedProjectId = null;
    }

    await loadProjects();
    showFeedback(isArchived ? "Project archived." : "Project restored.");
  } catch (error) {
    showFeedback(error.message || "Could not update project archive state.", "error");
  }
}

async function deleteSelectedProject() {
  const selected = getSelectedProject();
  if (!selected) {
    showFeedback("Select a project first.", "error");
    return;
  }

  if (!window.confirm(`Delete project "${selected.name}" and all of its data?`)) {
    return;
  }

  try {
    await fetchJson(`/api/projects/${selected.id}`, { method: "DELETE" });
    state.selectedProjectId = null;
    await loadProjects();
    showFeedback("Project deleted.");
  } catch (error) {
    showFeedback(error.message || "Could not delete project.", "error");
  }
}

async function updateEpicArchiveState(isArchived) {
  const selected = getSelectedEpic();
  if (!selected) {
    showFeedback("Select an epic first.", "error");
    return;
  }

  try {
    await fetchJson(`/api/epics/${selected.id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: selected.name,
        description: selected.description,
        isArchived
      })
    });

    if (isArchived && !state.showArchivedEpics) {
      state.selectedEpicId = null;
      state.selectedDocumentId = null;
    }

    await loadBoard(state.selectedProjectId);
    showFeedback(isArchived ? "Epic archived." : "Epic restored.");
  } catch (error) {
    showFeedback(error.message || "Could not update epic archive state.", "error");
  }
}

async function deleteSelectedEpic() {
  const selected = getSelectedEpic();
  if (!selected) {
    showFeedback("Select an epic first.", "error");
    return;
  }

  if (!window.confirm(`Delete epic "${selected.name}" and its documents? Work items will be kept and unlinked.`)) {
    return;
  }

  try {
    await fetchJson(`/api/epics/${selected.id}`, { method: "DELETE" });
    state.selectedEpicId = null;
    state.selectedDocumentId = null;
    await loadBoard(state.selectedProjectId);
    showFeedback("Epic deleted.");
  } catch (error) {
    showFeedback(error.message || "Could not delete epic.", "error");
  }
}

async function loadSelectedEpicDocuments() {
  if (!state.selectedEpicId) {
    state.epicDocuments = [];
    state.selectedDocumentId = null;
    return;
  }

  state.epicDocuments = await fetchJson(`/api/epics/${state.selectedEpicId}/documents`);
  if (!state.epicDocuments.some(document => document.id === state.selectedDocumentId)) {
    state.selectedDocumentId = state.epicDocuments[0]?.id ?? null;
  }
}

function openItemModal() {
  if (!state.selectedProjectId) {
    showFeedback("Create or select a project before adding work.", "error");
    return;
  }

  syncEpicSelectOptions();
  itemEpicSelect.value = state.selectedEpicId || "";
  openModal(itemModal);
  itemTitleInput.focus();
}

function openEpicModal(epic = null) {
  if (!state.selectedProjectId) {
    showFeedback("Select a project before working with epics.", "error");
    return;
  }

  state.editingEpicId = epic?.id ?? null;
  epicModalTitle.textContent = epic ? "Edit Epic" : "New Epic";
  epicNameInput.value = epic?.name ?? "";
  epicDescriptionInput.value = epic?.description ?? "";
  epicFormError.hidden = true;
  epicFormError.textContent = "";
  openModal(epicModal);
  epicNameInput.focus();
}

function openDocumentModal(document = null) {
  if (!state.selectedEpicId) {
    showFeedback("Select an epic before working with documents.", "error");
    return;
  }

  state.editingDocumentId = document?.id ?? null;
  documentModalTitle.textContent = document ? "Edit Document" : "New Document";
  documentTitleInput.value = document?.title ?? "";
  documentBodyInput.value = document?.body ?? "";
  documentFormError.hidden = true;
  documentFormError.textContent = "";
  openModal(documentModal);
  documentTitleInput.focus();
}

function openDetailModal(item) {
  state.activeDetailItem = item;
  populateDetailModal(item);
  stopDetailEditMode();
  detailEditError.hidden = true;
  detailEditError.textContent = "";
  openModal(detailModal);
  closeDetailModalButton.focus();
}

function populateDetailModal(item) {
  detailModalTitle.textContent = item.title || "Untitled item";
  detailType.textContent = item.type;
  detailPriority.textContent = item.priority;
  detailPriority.className = "item-priority";
  detailPriority.classList.add(`priority-${item.priority}`);
  detailStatus.textContent = statusLabels[item.status] || item.status;
  detailEpic.textContent = item.epic?.name || "No epic";
  detailEstimate.textContent = item.estimate ? `${item.estimate} pts` : "No estimate";
  detailLabels.textContent = item.labels || "No labels";
  detailDescription.textContent = item.description || "No description";
}

function closeDetailModal() {
  state.activeDetailItem = null;
  stopDetailEditMode();
  closeModal(detailModal, null);
}

function startDetailEditMode() {
  if (!state.activeDetailItem) {
    return;
  }

  detailEditTitle.value = state.activeDetailItem.title;
  detailEditDescription.value = state.activeDetailItem.description || "";
  detailEditType.value = state.activeDetailItem.type;
  detailEditStatus.value = state.activeDetailItem.status;
  detailEditPriority.value = state.activeDetailItem.priority;
  syncEpicSelectOptions();
  detailEditEpic.value = state.activeDetailItem.epicId || "";
  detailEditEstimate.value = state.activeDetailItem.estimate ?? "";
  detailEditLabels.value = state.activeDetailItem.labels || "";
  detailEditError.hidden = true;
  detailEditError.textContent = "";
  detailViewMode.hidden = true;
  detailEditForm.hidden = false;
  editDetailButton.hidden = true;
  detailEditTitle.focus();
}

function stopDetailEditMode() {
  detailViewMode.hidden = false;
  detailEditForm.hidden = true;
  editDetailButton.hidden = false;
  detailEditError.hidden = true;
  detailEditError.textContent = "";
}

function syncEpicSelectOptions() {
  populateEpicSelect(itemEpicSelect, state.epics);
  populateEpicSelect(detailEditEpic, state.epics);
}

function populateEpicSelect(select, epics) {
  const selectedValue = select.value;
  select.innerHTML = '<option value="">No epic</option>';

  for (const epic of epics) {
    const option = document.createElement("option");
    option.value = epic.id;
    option.textContent = epic.name;
    select.appendChild(option);
  }

  if (selectedValue && epics.some(epic => epic.id === selectedValue)) {
    select.value = selectedValue;
  }
}

function updateActionAvailability() {
  const hasProject = Boolean(state.selectedProjectId);
  const hasEpic = Boolean(getSelectedEpic());
  const hasDocument = Boolean(getSelectedDocument());

  openItemModalButton.disabled = !hasProject;
  newEpicButton.disabled = !hasProject;
  editEpicButton.disabled = !hasEpic;
  archiveEpicButton.disabled = !hasEpic;
  restoreEpicButton.disabled = !hasEpic;
  deleteEpicButton.disabled = !hasEpic;
  deleteProjectButton.disabled = !hasProject;
  newDocumentButton.disabled = !hasEpic;
  editDocumentButton.disabled = !hasDocument;
}

function openModal(modal) {
  modal.hidden = false;
  syncModalState();
}

function closeModal(modal, focusTarget) {
  modal.hidden = true;

  if (modal === epicModal) {
    state.editingEpicId = null;
    epicFormError.hidden = true;
    epicFormError.textContent = "";
  }

  if (modal === documentModal) {
    state.editingDocumentId = null;
    documentFormError.hidden = true;
    documentFormError.textContent = "";
  }

  syncModalState();
  focusTarget?.focus();
}

function syncModalState() {
  const modalOpen = [itemModal, epicModal, documentModal, detailModal].some(modal => !modal.hidden);
  document.body.classList.toggle("modal-open", modalOpen);
}

function showFeedback(message, type = "success") {
  feedbackBanner.hidden = false;
  feedbackBanner.textContent = message;
  feedbackBanner.className = `feedback-banner ${type}`;

  if (state.feedbackTimeoutId) {
    window.clearTimeout(state.feedbackTimeoutId);
  }

  state.feedbackTimeoutId = window.setTimeout(() => {
    feedbackBanner.hidden = true;
  }, 4000);
}

async function withFeedback(callback, errorMessage) {
  try {
    await callback();
  } catch (error) {
    showFeedback(error.message || errorMessage, "error");
  }
}

function formatRelativeDocument(document) {
  const updated = new Date(document.updatedAtUtc);
  return `Updated ${updated.toLocaleDateString()}`;
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const body = await response.text();
    let message = body;

    try {
      const parsed = JSON.parse(body);
      message = parsed.message ?? body;
    } catch {
      message = body;
    }

    throw new Error(message || `Request failed: ${response.status}`);
  }

  return response.status === 204 ? null : response.json();
}

function escapeHtml(value) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

loadProjects().catch(error => {
  boardTitle.textContent = "Could not load board";
  boardDescription.textContent = error.message;
  showFeedback(error.message || "Could not load board.", "error");
});
