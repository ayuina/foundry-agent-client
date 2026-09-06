(() => {
  "use strict";

  const historyScope = document.querySelector(".app-shell")?.dataset.historyScope || "authenticated-user";
  const connectionKey = `foundry-chat.${historyScope}.connection`;
  const conversationKey = `foundry-chat.${historyScope}.conversation`;
  const historyKey = `foundry-chat.${historyScope}.history`;
  const maxHistoryItems = 100;

  const elements = {
    connectionForm: document.querySelector("#connection-form"),
    resourceName: document.querySelector("#resource-name"),
    projectName: document.querySelector("#project-name"),
    agentName: document.querySelector("#agent-name"),
    agentVersion: document.querySelector("#agent-version"),
    refreshResources: document.querySelector("#refresh-resources"),
    discoveryStatus: document.querySelector("#discovery-status"),
    activeAgent: document.querySelector("#active-agent"),
    activeAgentName: document.querySelector("#active-agent-name"),
    activeProjectName: document.querySelector("#active-project-name"),
    indicator: document.querySelector("#connection-indicator"),
    chatForm: document.querySelector("#chat-form"),
    messageInput: document.querySelector("#message-input"),
    sendButton: document.querySelector("#send-button"),
    messages: document.querySelector("#messages"),
    welcome: document.querySelector("#welcome"),
    warning: document.querySelector("#connection-warning"),
    clearButton: document.querySelector("#clear-chat"),
    connectionButton: document.querySelector("#connection-form button[type='submit']"),
    settingsPanel: document.querySelector("#settings-panel"),
    settingsToggle: document.querySelector("#settings-toggle")
  };

  if (!elements.connectionForm || !elements.chatForm) {
    return;
  }

  let connection = readJson(sessionStorage, connectionKey);
  let conversationToken = sessionStorage.getItem(conversationKey);
  let history = readJson(localStorage, historyKey);
  let resources = [];
  let agents = [];
  let sending = false;
  let discovering = false;
  let agentLoadGeneration = 0;
  let agentLoadController = null;

  if (!connection?.projectToken || !connection.resourceId || !connection.projectId) {
    connection = null;
    conversationToken = null;
    sessionStorage.removeItem(connectionKey);
    sessionStorage.removeItem(conversationKey);
  }
  if (!Array.isArray(history)) {
    history = [];
  }

  renderHistory();
  updateConnectionUi();
  void loadResources(true);

  elements.resourceName.addEventListener("change", () => {
    populateProjects();
    void loadAgents();
  });
  elements.projectName.addEventListener("change", () => void loadAgents());
  elements.agentName.addEventListener("change", () => populateVersions(false));
  elements.agentVersion.addEventListener("change", updatePickerState);
  elements.refreshResources.addEventListener("click", () => void loadResources(false));

  elements.connectionForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (!elements.connectionForm.reportValidity()) {
      return;
    }

    const resource = getSelectedResource();
    const project = getSelectedProject();
    const agent = getSelectedAgent();
    if (!resource || !project || !agent) {
      return;
    }

    const nextConnection = {
      projectToken: project.projectToken,
      resourceName: resource.name,
      resourceId: resource.id,
      projectName: project.name,
      projectId: project.id,
      subscriptionId: resource.subscriptionId,
      subscriptionName: resource.subscriptionName,
      agentName: agent.name,
      agentVersion: elements.agentVersion.value || null
    };

    const changed = !connection ||
      connectionFingerprint(connection) !== connectionFingerprint(nextConnection);
    if (connection && changed) {
      await endCurrentConversation();
    }

    connection = nextConnection;
    sessionStorage.setItem(connectionKey, JSON.stringify(connection));
    if (changed) {
      conversationToken = null;
      sessionStorage.removeItem(conversationKey);
    }
    updateConnectionUi();
    elements.messageInput.focus();
    elements.settingsPanel.classList.remove("open");
    elements.settingsToggle.setAttribute("aria-expanded", "false");
  });

  elements.chatForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    await sendMessage(elements.messageInput.value);
  });

  elements.messageInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      elements.chatForm.requestSubmit();
    }
  });

  elements.messageInput.addEventListener("input", () => {
    elements.messageInput.style.height = "auto";
    elements.messageInput.style.height = `${Math.min(elements.messageInput.scrollHeight, 160)}px`;
  });

  elements.clearButton.addEventListener("click", async () => {
    elements.clearButton.disabled = true;
    await endCurrentConversation();
    history = [];
    localStorage.removeItem(historyKey);
    renderHistory();
    elements.clearButton.disabled = false;
  });

  elements.settingsToggle.addEventListener("click", () => {
    const open = elements.settingsPanel.classList.toggle("open");
    elements.settingsToggle.setAttribute("aria-expanded", String(open));
  });

  document.querySelectorAll("[data-prompt]").forEach((button) => {
    button.addEventListener("click", () => {
      elements.messageInput.value = button.dataset.prompt;
      elements.messageInput.dispatchEvent(new Event("input"));
      if (connection) {
        elements.messageInput.focus();
      } else {
        elements.settingsPanel.classList.add("open");
        elements.resourceName.focus();
      }
    });
  });

  async function loadResources(restoreSavedSelection) {
    restoreSavedSelection = Boolean(connection) || restoreSavedSelection;
    discovering = true;
    setDiscoveryStatus("利用可能な Foundry Project を読み込んでいます...");
    setSelectOptions(elements.resourceName, [], "読み込み中...");
    setSelectOptions(elements.projectName, [], "Foundry リソースを選択");
    setSelectOptions(elements.agentName, [], "Project を選択");
    setSelectOptions(elements.agentVersion, [], "最新バージョン");
    updatePickerState();

    try {
      const response = await fetch("/api/discovery/projects", {
        credentials: "same-origin",
        headers: { "Accept": "application/json" }
      });
      if (!response.ok) {
        throw new Error(await readProblem(response));
      }

      resources = await response.json();
      setSelectOptions(
        elements.resourceName,
        resources.map((resource, index) => ({
          value: String(index),
          label: `${resource.name} · ${resource.subscriptionName}`
        })),
        resources.length ? "Foundry リソースを選択" : "利用可能なリソースがありません");

      if (restoreSavedSelection && connection) {
        const resourceIndex = resources.findIndex((resource) =>
          resource.id === connection.resourceId);
        if (resourceIndex >= 0) {
          elements.resourceName.value = String(resourceIndex);
        } else {
          clearStaleConnection();
        }
      }

      populateProjects(restoreSavedSelection);
      const hasSelectedProject = Boolean(getSelectedProject());
      await loadAgents(restoreSavedSelection);
      if (!hasSelectedProject) {
        setDiscoveryStatus(resources.length
          ? "接続先を選択してください。"
          : "参照可能な Foundry Project が見つかりませんでした。");
      }
    } catch (error) {
      resources = [];
      setSelectOptions(elements.resourceName, [], "取得に失敗しました");
      setDiscoveryStatus(error instanceof Error ? error.message : "接続先を取得できませんでした。", true);
    } finally {
      discovering = false;
      updatePickerState();
    }
  }

  function populateProjects(restoreSavedSelection = false) {
    const resource = getSelectedResource();
    const projects = resource?.projects || [];
    setSelectOptions(
      elements.projectName,
      projects.map((project, index) => ({
        value: String(index),
        label: project.displayName || project.name
      })),
      resource ? "Project を選択" : "Foundry リソースを選択");

    if (restoreSavedSelection && connection) {
      const projectIndex = projects.findIndex((project) => project.id === connection.projectId);
      if (projectIndex >= 0) {
        elements.projectName.value = String(projectIndex);
        connection.projectToken = projects[projectIndex].projectToken;
        sessionStorage.setItem(connectionKey, JSON.stringify(connection));
      } else if (getSelectedResource()) {
        clearStaleConnection();
      }
    }
    updatePickerState();
  }

  async function loadAgents(restoreSavedSelection = false) {
    agentLoadController?.abort();
    const generation = ++agentLoadGeneration;
    agentLoadController = new AbortController();
    const project = getSelectedProject();
    agents = [];
    setSelectOptions(elements.agentName, [], project ? "Agent を読み込み中..." : "Project を選択");
    setSelectOptions(elements.agentVersion, [], "最新バージョン");
    if (!project) {
      updatePickerState();
      return;
    }

    discovering = true;
    setDiscoveryStatus("Agent と Version を読み込んでいます...");
    updatePickerState();
    try {
      const response = await fetch(`/api/discovery/agents?projectToken=${encodeURIComponent(project.projectToken)}`, {
        credentials: "same-origin",
        headers: { "Accept": "application/json" },
        signal: agentLoadController.signal
      });
      if (!response.ok) {
        throw new Error(await readProblem(response));
      }

      const loadedAgents = await response.json();
      if (generation !== agentLoadGeneration ||
          getSelectedProject()?.projectToken !== project.projectToken) {
        return;
      }
      agents = loadedAgents;
      setSelectOptions(
        elements.agentName,
        agents.map((agent, index) => ({ value: String(index), label: agent.name })),
        agents.length ? "Agent を選択" : "Agent がありません");

      if (restoreSavedSelection && connection) {
        const agentIndex = agents.findIndex((agent) => agent.name === connection.agentName);
        if (agentIndex >= 0) {
          elements.agentName.value = String(agentIndex);
        } else {
          clearStaleConnection();
        }
      }
      populateVersions(restoreSavedSelection);
      setDiscoveryStatus(agents.length ? "Agent を選択してください。" : "この Project に Agent がありません。");
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }
      setSelectOptions(elements.agentName, [], "取得に失敗しました");
      setDiscoveryStatus(error instanceof Error ? error.message : "Agent を取得できませんでした。", true);
    } finally {
      if (generation === agentLoadGeneration) {
        discovering = false;
        agentLoadController = null;
        updatePickerState();
      }
    }
  }

  function populateVersions(restoreSavedSelection = false) {
    const agent = getSelectedAgent();
    setSelectOptions(
      elements.agentVersion,
      (agent?.versions || []).map((version) => ({ value: version, label: `Version ${version}` })),
      "最新バージョン");
    if (restoreSavedSelection && connection?.agentVersion &&
        agent?.versions.includes(connection.agentVersion)) {
      elements.agentVersion.value = connection.agentVersion;
    }
    updatePickerState();
  }

  async function sendMessage(rawMessage) {
    const message = rawMessage.trim();
    if (!connection || !message || sending) {
      return;
    }

    sending = true;
    setComposerState();
    elements.messageInput.value = "";
    elements.messageInput.style.height = "auto";
    appendHistory({ role: "user", text: message, createdAt: new Date().toISOString() });
    renderHistory();
    const pending = renderPending();

    try {
      let response = await postJson("/api/chat", {
        projectToken: connection.projectToken,
        agentName: connection.agentName,
        agentVersion: connection.agentVersion,
        message,
        conversationToken
      });

      if (response.status === 409 && conversationToken) {
        conversationToken = null;
        sessionStorage.removeItem(conversationKey);
        response = await postJson("/api/chat", {
          projectToken: connection.projectToken,
          agentName: connection.agentName,
          agentVersion: connection.agentVersion,
          message,
          conversationToken: null
        });
      }

      if (!response.ok) {
        throw new Error(await readProblem(response));
      }

      const reply = await response.json();
      conversationToken = reply.conversationToken;
      sessionStorage.setItem(conversationKey, conversationToken);
      appendHistory({
        role: "assistant",
        text: reply.text || "回答本文がありませんでした。",
        citations: reply.citations || [],
        assets: reply.assets || [],
        createdAt: new Date().toISOString()
      });
      renderHistory();
    } catch (error) {
      pending.remove();
      renderError(error instanceof Error ? error.message : "応答を取得できませんでした。");
    } finally {
      sending = false;
      setComposerState();
      elements.messageInput.focus();
    }
  }

  async function endCurrentConversation() {
    if (!connection || !conversationToken) {
      conversationToken = null;
      sessionStorage.removeItem(conversationKey);
      return;
    }

    try {
      const response = await postJson("/api/chat/clear", {
        projectToken: connection.projectToken,
        agentName: connection.agentName,
        agentVersion: connection.agentVersion,
        conversationToken
      });
      if (!response.ok && response.status !== 404) {
        throw new Error(await readProblem(response));
      }
    } catch (error) {
      renderError(error instanceof Error ? error.message : "会話を終了できませんでした。");
    } finally {
      conversationToken = null;
      sessionStorage.removeItem(conversationKey);
    }
  }

  async function postJson(url, body) {
    return fetch(url, {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "RequestVerificationToken": document.querySelector('input[name="__RequestVerificationToken"]').value
      },
      body: JSON.stringify(body)
    });
  }

  async function readProblem(response) {
    try {
      const problem = await response.json();
      return problem.detail || problem.title || `リクエストに失敗しました (${response.status})`;
    } catch {
      return `リクエストに失敗しました (${response.status})`;
    }
  }

  function appendHistory(item) {
    history.push(item);
    history = history.slice(-maxHistoryItems);
    try {
      localStorage.setItem(historyKey, JSON.stringify(history));
    } catch {
      history = history.slice(-20);
      localStorage.setItem(historyKey, JSON.stringify(history));
    }
  }

  function renderHistory() {
    elements.messages.querySelectorAll(".message-row, .error-message").forEach((node) => node.remove());
    elements.welcome.hidden = history.length > 0;
    history.forEach(renderMessage);
    scrollToLatest();
  }

  function renderMessage(item) {
    const row = createElement("article", `message-row ${item.role}`);
    const card = createElement("div", "message-card");

    if (item.role === "assistant") {
      card.append(createElement("span", "message-label", "Foundry Agent"));
    }
    card.append(createElement("div", "message-text", item.text));

    const imageSources = (item.citations || []).filter((citation) =>
      citation.kind === "image" && (citation.href || citation.url));
    const imageAssets = (item.assets || []).filter((asset) => asset.isImage);
    if (imageSources.length || imageAssets.length) {
      const gallery = createElement("div", "asset-gallery");
      imageSources.forEach((citation) =>
        gallery.append(createImageCard(citation.href || citation.url, citation.title)));
      imageAssets.forEach((asset) => gallery.append(createImageCard(asset.url, asset.name)));
      card.append(gallery);
    }

    const citations = item.citations || [];
    const files = (item.assets || []).filter((asset) => !asset.isImage);
    if (citations.length || files.length) {
      const sources = createElement("div", "sources");
      sources.append(createElement("div", "sources-title", "Sources & files"));
      const grid = createElement("div", "source-grid");
      citations.forEach((citation) => grid.append(createSourceCard(citation)));
      files.forEach((asset) => grid.append(createSourceCard({
        title: asset.name,
        href: asset.url,
        originalUrl: null,
        kind: "download"
      })));
      sources.append(grid);
      card.append(sources);
    }

    card.append(createElement("time", "message-time", formatTime(item.createdAt)));
    row.append(card);
    elements.messages.append(row);
  }

  function createImageCard(url, title) {
    const link = createElement("a", "asset-image");
    link.href = url;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    const image = document.createElement("img");
    image.src = url;
    image.alt = title || "回答に関連する画像";
    image.loading = "lazy";
    image.addEventListener("error", () => link.remove());
    link.append(image, createElement("span", "", title || "関連画像"));
    return link;
  }

  function createSourceCard(citation) {
    const href = citation.href || citation.url;
    const originalUrl = citation.originalUrl || citation.url;
    const card = href ? document.createElement("a") : document.createElement("div");
    card.className = "source-card";
    if (href) {
      card.href = href;
      card.target = "_blank";
      card.rel = "noopener noreferrer";
    }
    const iconText = citation.kind === "file" || citation.kind === "download" ? "DOC" : "URL";
    const content = createElement("span", "source-content");
    content.append(createElement("span", "source-name", citation.title || "参照元"));
    if (originalUrl) {
      content.append(createElement("span", "source-url", originalUrl));
    }
    card.append(createElement("span", "source-icon", iconText), content);
    if (citation.kind === "download") {
      card.download = citation.title;
    }
    return card;
  }

  function renderPending() {
    const row = createElement("article", "message-row assistant pending");
    const card = createElement("div", "message-card");
    card.append(
      createElement("span", "message-label", "Foundry Agent"),
      createElement("div", "message-text typing-dots", "ドキュメントを検索しています")
    );
    row.append(card);
    elements.messages.append(row);
    scrollToLatest();
    return row;
  }

  function renderError(message) {
    elements.messages.append(createElement("div", "error-message", message));
    scrollToLatest();
  }

  function setSelectOptions(select, options, placeholder) {
    select.replaceChildren();
    const empty = document.createElement("option");
    empty.value = "";
    empty.textContent = placeholder;
    select.append(empty);
    options.forEach((option) => {
      const element = document.createElement("option");
      element.value = option.value;
      element.textContent = option.label;
      select.append(element);
    });
    select.disabled = options.length === 0;
  }

  function getSelectedResource() {
    const index = Number.parseInt(elements.resourceName.value, 10);
    return Number.isInteger(index) ? resources[index] : null;
  }

  function getSelectedProject() {
    const resource = getSelectedResource();
    const index = Number.parseInt(elements.projectName.value, 10);
    return resource && Number.isInteger(index) ? resource.projects[index] : null;
  }

  function getSelectedAgent() {
    const index = Number.parseInt(elements.agentName.value, 10);
    return Number.isInteger(index) ? agents[index] : null;
  }

  function updatePickerState() {
    elements.resourceName.disabled = discovering || resources.length === 0;
    elements.projectName.disabled = !getSelectedResource();
    if (discovering) {
      elements.projectName.disabled = true;
    }
    elements.agentName.disabled = !getSelectedProject() || discovering;
    elements.agentVersion.disabled = !getSelectedAgent() || discovering;
    elements.connectionButton.disabled = discovering || sending || !getSelectedAgent();
    elements.refreshResources.disabled = discovering || sending;
  }

  function setDiscoveryStatus(message, isError = false) {
    elements.discoveryStatus.textContent = message;
    elements.discoveryStatus.classList.toggle("error", isError);
  }

  function createElement(tagName, className, text) {
    const element = document.createElement(tagName);
    if (className) {
      element.className = className;
    }
    if (text !== undefined) {
      element.textContent = text;
    }
    return element;
  }

  function updateConnectionUi() {
    const ready = Boolean(connection);
    elements.messageInput.disabled = !ready || sending;
    elements.sendButton.disabled = !ready || sending;
    elements.warning.hidden = ready;
    elements.indicator.classList.toggle("connected", ready);
    elements.indicator.title = ready ? "設定済み" : "未設定";
    elements.activeAgent.hidden = !ready;
    if (ready) {
      elements.activeAgentName.textContent =
        connection.agentName + (connection.agentVersion ? ` · v${connection.agentVersion}` : " · latest");
      elements.activeProjectName.textContent =
        `${connection.resourceName} / ${connection.projectName}`;
    }
  }

  function setComposerState() {
    updateConnectionUi();
    elements.messageInput.disabled = !connection || sending;
    elements.sendButton.disabled = !connection || sending;
    elements.clearButton.disabled = sending;
    updatePickerState();
  }

  function connectionFingerprint(value) {
    return [
      value.projectId,
      value.agentName,
      value.agentVersion || ""
    ].join("\n");
  }

  function clearStaleConnection() {
    connection = null;
    conversationToken = null;
    sessionStorage.removeItem(connectionKey);
    sessionStorage.removeItem(conversationKey);
    updateConnectionUi();
  }

  function formatTime(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return "";
    }
    return new Intl.DateTimeFormat("ja-JP", {
      hour: "2-digit",
      minute: "2-digit"
    }).format(date);
  }

  function readJson(storage, key) {
    try {
      const value = storage.getItem(key);
      return value ? JSON.parse(value) : null;
    } catch {
      storage.removeItem(key);
      return null;
    }
  }

  function scrollToLatest() {
    requestAnimationFrame(() => {
      elements.messages.scrollTop = elements.messages.scrollHeight;
    });
  }
})();
