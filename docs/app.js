(() => {
  const PIX_KEY = "95b9aa86-3c99-421d-9c86-6a47a3b09d7b";
  const LANG_STORAGE_KEY = "rcloneManager.lang";

  const DOCS_SOURCE_BY_LANG = {
    pt: {
      basePath: "",
      manifest: "docs-manifest.json",
      apiPaths: [
        "https://api.github.com/repos/Dillemon-Recla/RcloneManager/contents/docs",
        "https://api.github.com/repos/Dillemon-Recla/RcloneManager/contents/docs?ref=dev"
      ],
      blobBase: "https://github.com/Dillemon-Recla/RcloneManager/blob/main/docs/",
      folderLabel: "docs/"
    },
    en: {
      basePath: "en/",
      manifest: "en/docs-manifest.json",
      apiPaths: [
        "https://api.github.com/repos/Dillemon-Recla/RcloneManager/contents/docs/en",
        "https://api.github.com/repos/Dillemon-Recla/RcloneManager/contents/docs/en?ref=dev"
      ],
      blobBase: "https://github.com/Dillemon-Recla/RcloneManager/blob/main/docs/en/",
      folderLabel: "docs/en/"
    }
  };

  const buttonTimers = new WeakMap();

  function setTempButtonLabel(btn, tempText, defaultText, delayMs = 1600) {
    if (!btn) return;
    const prevTimer = buttonTimers.get(btn);
    if (prevTimer) clearTimeout(prevTimer);
    btn.textContent = tempText;
    const timer = setTimeout(() => {
      btn.textContent = defaultText;
      buttonTimers.delete(btn);
    }, delayMs);
    buttonTimers.set(btn, timer);
  }

  function normalizeLang(raw) {
    if (!raw || typeof raw !== "string") return null;
    const lower = raw.trim().toLowerCase();
    if (lower.startsWith("pt")) return "pt";
    if (lower.startsWith("en")) return "en";
    return null;
  }

  function detectInitialLanguage() {
    const params = new URLSearchParams(window.location.search);
    const urlLang = normalizeLang(params.get("lang"));
    if (urlLang) return urlLang;

    try {
      const savedLang = normalizeLang(localStorage.getItem(LANG_STORAGE_KEY));
      if (savedLang) return savedLang;
    } catch {
      // localStorage indisponível
    }

    const browserLangs =
      Array.isArray(navigator.languages) && navigator.languages.length > 0
        ? navigator.languages
        : [navigator.language || navigator.userLanguage || "en"];

    for (const candidate of browserLangs) {
      const normalized = normalizeLang(candidate);
      if (normalized) return normalized;
      if (candidate && candidate.trim()) return "en";
    }
    return "en";
  }

  let currentLang = detectInitialLanguage();

  function t(key, vars = null) {
    const dict = window.RCLONE_PAGES_I18N || {};
    const table = dict[currentLang] || dict.pt || {};
    let str = table[key] ?? (dict.pt ? dict.pt[key] : undefined) ?? key;
    if (vars && typeof str === "string") {
      Object.entries(vars).forEach(([k, v]) => {
        str = str.replaceAll(`{${k}}`, String(v));
      });
    }
    return str;
  }

  function getDocsSource() {
    return DOCS_SOURCE_BY_LANG[currentLang] || DOCS_SOURCE_BY_LANG.pt;
  }

  // Menu mobile
  const navToggle = document.getElementById("navToggle");
  const navLinks = document.getElementById("navLinks");
  if (navToggle && navLinks) {
    navToggle.addEventListener("click", () => {
      navLinks.classList.toggle("open");
    });
    navLinks.querySelectorAll("a").forEach((a) => {
      a.addEventListener("click", () => navLinks.classList.remove("open"));
    });
  }

  // Abas do mockup do aplicativo
  const mockupTabs = document.querySelectorAll(".mockup-tab");
  const mockupPanes = document.querySelectorAll(".mockup-pane");
  mockupTabs.forEach((tab) => {
    tab.addEventListener("click", () => {
      const paneId =
        tab.getAttribute("data-pane") ||
        (tab.getAttribute("data-tab") ? `pane-${tab.getAttribute("data-tab")}` : null);
      mockupTabs.forEach((t) => t.classList.remove("active"));
      mockupPanes.forEach((p) => p.classList.remove("active"));
      tab.classList.add("active");
      const pane = paneId ? document.getElementById(paneId) : null;
      if (pane) pane.classList.add("active");
    });
  });

  // Botão copiar Quickstart
  const copyQuickstartBtn = document.getElementById("copyQuickstartBtn");
  const quickstartCode = document.getElementById("quickstartCode");
  if (copyQuickstartBtn && quickstartCode) {
    copyQuickstartBtn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(quickstartCode.innerText);
        setTempButtonLabel(copyQuickstartBtn, t("dyn.copied"), t("qs.copyBtn"), 1800);
      } catch {
        setTempButtonLabel(copyQuickstartBtn, t("dyn.selectManual"), t("qs.copyBtn"), 2200);
      }
    });
  }

  // Botão copiar Pix
  const copyPixBtn = document.getElementById("copyPixBtn");
  const pixFeedback = document.getElementById("pixFeedback");
  let pixTimer = null;
  if (copyPixBtn && pixFeedback) {
    copyPixBtn.addEventListener("click", async () => {
      if (pixTimer) clearTimeout(pixTimer);
      try {
        await navigator.clipboard.writeText(PIX_KEY);
        pixFeedback.textContent = t("dyn.pixCopied");
      } catch {
        pixFeedback.textContent = t("dyn.pixManual", { key: PIX_KEY });
      }
      pixTimer = setTimeout(() => { pixFeedback.textContent = ""; }, 3200);
    });
  }

  // Utilitários Markdown -> HTML
  function escapeHtml(str) {
    return str
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function normalizeDocLink(rawHref) {
    return rawHref
      .trim()
      .replace(/^\.\//, "")
      .replace(/^docs\/(en\/)?/i, "")
      .replace(/^en\//i, "");
  }

  function formatInline(text) {
    const clean = text.replace(/\u0000/g, "");
    const codeTokens = [];

    let out = escapeHtml(clean);
    out = out.replace(/`([^`]+)`/g, (_, codeContent) => {
      const idx = codeTokens.length;
      codeTokens.push(`<code>${codeContent}</code>`);
      return `\u0000IC:${idx}\u0000`;
    });

    out = out.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
    out = out.replace(/\[([^\]]+)\]\((#[^\s)]+)\)/g, '<a href="$2">$1</a>');
    out = out.replace(/\[([^\]]+)\]\(([^)\s]+\.md)\)/g, (_, label, href) => {
      const normalized = normalizeDocLink(href);
      return `<a href="#doc=${encodeURIComponent(normalized)}" data-internal-doc="${normalized}">${label}</a>`;
    });

    out = out.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    out = out.replace(/\*([^*]+)\*/g, "<em>$1</em>");

    out = out.replace(/\u0000IC:(\d+)\u0000/g, (_, idx) => codeTokens[Number(idx)] || "");
    return out;
  }

  function renderMarkdown(md, options = {}) {
    const { stripLeadingH1 = true, copyLabel = "Copiar" } = options;
    const lines = md.replace(/\r\n/g, "\n").split("\n");
    const html = [];
    let i = 0;
    let strippedFirstH1 = !stripLeadingH1;

    while (i < lines.length) {
      const line = lines[i];

      // Bloco de código cercado
      if (line.trim().startsWith("```")) {
        const lang = line.trim().slice(3).trim();
        i++;
        const codeLines = [];
        while (i < lines.length && !lines[i].trim().startsWith("```")) {
          codeLines.push(lines[i]);
          i++;
        }
        i++;
        const langAttr = lang ? ` data-lang="${escapeHtml(lang)}"` : "";
        html.push(
          `<div class="code-block-wrap"><button type="button" class="code-copy-btn">${escapeHtml(copyLabel)}</button><pre${langAttr}><code>${escapeHtml(codeLines.join("\n"))}</code></pre></div>`
        );
        continue;
      }

      // Linha em branco
      if (!line.trim()) {
        i++;
        continue;
      }

      // Separador horizontal
      if (/^---+$/.test(line.trim())) {
        html.push("<hr />");
        i++;
        continue;
      }

      // Cabeçalhos
      const hMatch = line.match(/^(#{1,4})\s+(.*)$/);
      if (hMatch) {
        const level = hMatch[1].length;
        if (level === 1 && !strippedFirstH1) {
          strippedFirstH1 = true;
          i++;
          continue;
        }
        html.push(`<h${level}>${formatInline(hMatch[2])}</h${level}>`);
        i++;
        continue;
      }

      // Blockquote
      if (line.trim().startsWith(">")) {
        const quoteLines = [];
        while (i < lines.length && lines[i].trim().startsWith(">")) {
          quoteLines.push(lines[i].trim().replace(/^>\s?/, ""));
          i++;
        }
        html.push(`<blockquote>${formatInline(quoteLines.join(" "))}</blockquote>`);
        continue;
      }

      // Tabela Markdown simples
      if (line.includes("|") && i + 1 < lines.length && /^\s*\|?[\s:-]+\|/.test(lines[i + 1])) {
        const parseRow = (row) => row.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map((c) => c.trim());
        const headers = parseRow(line);
        i += 2;
        const rows = [];
        while (i < lines.length && lines[i].includes("|") && lines[i].trim() !== "") {
          rows.push(parseRow(lines[i]));
          i++;
        }
        let tableHtml = '<div class="table-wrap"><table><thead><tr>';
        headers.forEach((h) => { tableHtml += `<th>${formatInline(h)}</th>`; });
        tableHtml += "</tr></thead><tbody>";
        rows.forEach((r) => {
          tableHtml += "<tr>";
          r.forEach((c) => { tableHtml += `<td>${formatInline(c)}</td>`; });
          tableHtml += "</tr>";
        });
        tableHtml += "</tbody></table></div>";
        html.push(tableHtml);
        continue;
      }

      // Lista não ordenada
      if (/^\s*[-*]\s+/.test(line)) {
        html.push("<ul>");
        while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) {
          html.push(`<li>${formatInline(lines[i].replace(/^\s*[-*]\s+/, ""))}</li>`);
          i++;
        }
        html.push("</ul>");
        continue;
      }

      // Lista ordenada
      if (/^\s*\d+\.\s+/.test(line)) {
        html.push("<ol>");
        while (i < lines.length && /^\s*\d+\.\s+/.test(lines[i])) {
          html.push(`<li>${formatInline(lines[i].replace(/^\s*\d+\.\s+/, ""))}</li>`);
          i++;
        }
        html.push("</ol>");
        continue;
      }

      // Parágrafo
      const para = [];
      while (
        i < lines.length &&
        lines[i].trim() !== "" &&
        !lines[i].trim().startsWith("```") &&
        !/^(#{1,4})\s+/.test(lines[i]) &&
        !/^\s*[-*]\s+/.test(lines[i]) &&
        !/^\s*\d+\.\s+/.test(lines[i]) &&
        !lines[i].trim().startsWith(">") &&
        !/^---+$/.test(lines[i].trim())
      ) {
        para.push(lines[i].trim());
        i++;
      }
      html.push(`<p>${formatInline(para.join(" "))}</p>`);
    }

    return html.join("\n");
  }

  function extractTitle(md, fileName) {
    const match = md.match(/^#\s+(.+)$/m);
    if (match) return match[1].trim();
    return fileName
      .replace(/\.md$/i, "")
      .replace(/[-_]+/g, " ")
      .replace(/\b\w/g, (c) => c.toUpperCase());
  }

  // Docs Hub Dinâmico (.md -> Seções inline)
  const docsSidebar = document.getElementById("docsSidebar");
  const docsViewer = document.getElementById("docsViewer");
  const docsSearchInput = document.getElementById("docsSearchInput");
  const docsCountBadge = document.getElementById("docsCountBadge");
  const modeSingleBtn = document.getElementById("modeSingleBtn");
  const modeAllBtn = document.getElementById("modeAllBtn");

  const docsState = {
    docs: [],
    activeFile: null,
    activeIndex: 0,
    mode: "single",
    query: ""
  };

  async function discoverMarkdownFiles() {
    const source = getDocsSource();
    const ordered = [];
    const seen = new Set();

    const addFile = (name) => {
      if (!name || typeof name !== "string") return;
      const clean = name.trim();
      if (!clean.toLowerCase().endsWith(".md") || seen.has(clean)) return;
      seen.add(clean);
      ordered.push(clean);
    };

    try {
      const manifestRes = await fetch(source.manifest, { cache: "no-store" });
      if (manifestRes.ok) {
        const manifest = await manifestRes.json();
        if (Array.isArray(manifest)) manifest.forEach(addFile);
      }
    } catch {
      // Silencioso: fallback para descoberta via API ou file://
    }

    for (const apiUrl of source.apiPaths) {
      try {
        const res = await fetch(apiUrl, { headers: { Accept: "application/vnd.github+json" } });
        if (!res.ok) continue;
        const entries = await res.json();
        if (!Array.isArray(entries)) continue;
        entries
          .filter((item) => item && item.type === "file" && typeof item.name === "string" && item.name.toLowerCase().endsWith(".md"))
          .sort((a, b) => a.name.localeCompare(b.name))
          .forEach((item) => addFile(item.name));
        break;
      } catch {
        // Sem conexão ou rate-limit da API do GitHub
      }
    }

    return ordered;
  }

  function getDocHash() {
    const hash = window.location.hash || "";
    if (!hash.startsWith("#doc=")) return null;
    try {
      return normalizeDocLink(decodeURIComponent(hash.slice(5)));
    } catch {
      return normalizeDocLink(hash.slice(5));
    }
  }

  function setDocHash(fileName) {
    if (!fileName) return;
    const nextHash = `#doc=${encodeURIComponent(fileName)}`;
    if (window.location.hash !== nextHash) {
      history.replaceState(null, "", nextHash);
    }
  }

  function scrollToDocOrHub(fileName) {
    if (fileName) {
      const slug = fileName.replace(/[^a-zA-Z0-9_-]/g, "-");
      const sectionEl = document.getElementById(`section-${slug}`);
      if (sectionEl) {
        sectionEl.scrollIntoView({ behavior: "smooth", block: "start" });
        return;
      }
    }
    const docsSection = document.getElementById("documentacao");
    if (docsSection) {
      docsSection.scrollIntoView({ behavior: "smooth", block: "start" });
    }
  }

  function updateSidebarActiveState(fileName) {
    if (!docsSidebar) return;
    docsSidebar.querySelectorAll("button[data-select-doc]").forEach((btn) => {
      btn.classList.toggle("active", btn.getAttribute("data-select-doc") === fileName);
    });
  }

  function getFilteredDocs() {
    const q = docsState.query.trim().toLowerCase();
    if (!q) return docsState.docs;
    return docsState.docs.filter(
      (doc) =>
        doc.title.toLowerCase().includes(q) ||
        doc.file.toLowerCase().includes(q) ||
        doc.raw.toLowerCase().includes(q)
    );
  }

  function wireViewerActions() {
    if (!docsViewer) return;

    docsViewer.querySelectorAll(".code-copy-btn").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const pre = btn.parentElement && btn.parentElement.querySelector("pre");
        if (!pre) return;
        try {
          await navigator.clipboard.writeText(pre.innerText);
          setTempButtonLabel(btn, t("dyn.copied"), t("dyn.copy"), 1600);
        } catch {
          setTempButtonLabel(btn, t("dyn.copyError"), t("dyn.copy"), 1600);
        }
      });
    });

    docsViewer.querySelectorAll("a[data-internal-doc]").forEach((link) => {
      link.addEventListener("click", (e) => {
        const targetFile = link.getAttribute("data-internal-doc");
        const foundIdx = docsState.docs.findIndex((d) => d.file === targetFile);
        if (foundIdx === -1) return;
        e.preventDefault();
        docsState.activeFile = targetFile;
        docsState.activeIndex = foundIdx;
        docsState.mode = "single";
        setDocHash(targetFile);
        renderDocsHub();
        scrollToDocOrHub(targetFile);
      });
    });

    docsViewer.querySelectorAll("button[data-copy-doc-link]").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const fileName = btn.getAttribute("data-copy-doc-link");
        const url = `${window.location.origin}${window.location.pathname}?lang=${currentLang}#doc=${encodeURIComponent(fileName)}`;
        try {
          await navigator.clipboard.writeText(url);
          setTempButtonLabel(btn, t("dyn.linkCopied"), t("dyn.copyLink"), 1600);
        } catch {
          setTempButtonLabel(btn, t("dyn.copyUrlFallback"), t("dyn.copyLink"), 1600);
        }
      });
    });
  }

  function renderDocSectionHtml(doc) {
    const source = getDocsSource();
    const slug = doc.file.replace(/[^a-zA-Z0-9_-]/g, "-");
    return `
      <article id="section-${escapeHtml(slug)}" class="doc-section-card">
        <header class="doc-section-header">
          <div class="doc-section-meta">
            <h3>${escapeHtml(doc.title)}</h3>
            <span class="doc-section-filename">${escapeHtml(doc.file)}</span>
          </div>
          <div class="doc-section-actions">
            <button type="button" class="btn btn-sm" data-copy-doc-link="${escapeHtml(doc.file)}">${escapeHtml(t("dyn.copyLink"))}</button>
            <a class="btn btn-sm" href="${source.blobBase}${encodeURIComponent(doc.file)}" target="_blank" rel="noopener noreferrer">${escapeHtml(t("dyn.viewGitHub"))}</a>
          </div>
        </header>
        <div class="md-body">${doc.html}</div>
      </article>
    `;
  }

  function renderDocsHub() {
    if (!docsSidebar || !docsViewer) return;

    const filtered = getFilteredDocs();

    if (modeSingleBtn && modeAllBtn) {
      modeSingleBtn.classList.toggle("btn-primary", docsState.mode === "single");
      modeAllBtn.classList.toggle("btn-primary", docsState.mode === "all");
    }

    if (docsCountBadge) {
      const total = docsState.docs.length;
      docsCountBadge.textContent =
        filtered.length === total
          ? t("dyn.guidesBadge", { count: total })
          : t("dyn.guidesFilteredBadge", { shown: filtered.length, total });
    }

    if (docsState.docs.length === 0) {
      docsSidebar.innerHTML = `<div class="docs-empty">${escapeHtml(t("dyn.noLocalGuides"))}</div>`;
      return;
    }

    if (filtered.length === 0) {
      docsSidebar.innerHTML = `<div class="docs-empty">${escapeHtml(t("dyn.noSearchMatch"))}</div>`;
      docsViewer.innerHTML = `<div class="docs-empty">${t("dyn.noSectionMatch")}</div>`;
      return;
    }

    if (!docsState.activeFile || !filtered.some((d) => d.file === docsState.activeFile)) {
      docsState.activeFile = filtered[0].file;
    }

    docsSidebar.innerHTML = filtered
      .map((doc) => {
        const isActive = doc.file === docsState.activeFile;
        return `
          <button type="button" class="doc-nav-btn${isActive ? " active" : ""}" data-select-doc="${escapeHtml(doc.file)}">
            <span class="doc-nav-title">${escapeHtml(doc.title)}</span>
            <span class="doc-nav-file">${escapeHtml(doc.file)}</span>
          </button>
        `;
      })
      .join("");

    docsSidebar.querySelectorAll("button[data-select-doc]").forEach((btn) => {
      btn.addEventListener("click", () => {
        const fileName = btn.getAttribute("data-select-doc");
        docsState.activeFile = fileName;
        const idx = docsState.docs.findIndex((d) => d.file === fileName);
        if (idx >= 0) docsState.activeIndex = idx;
        setDocHash(fileName);
        if (docsState.mode === "all") {
          updateSidebarActiveState(fileName);
          scrollToDocOrHub(fileName);
          return;
        }
        renderDocsHub();
      });
    });

    const docsToRender =
      docsState.mode === "all"
        ? filtered
        : [filtered.find((d) => d.file === docsState.activeFile) || filtered[0]];

    docsViewer.innerHTML = docsToRender.map(renderDocSectionHtml).join("");
    wireViewerActions();
  }

  async function initDocsHub({ preserveIndex = false } = {}) {
    if (!docsSidebar || !docsViewer) return;

    const source = getDocsSource();

    if (window.location.protocol === "file:") {
      const fallbackFiles =
        currentLang === "en"
          ? [
              "architecture-and-hybrid-engine.md",
              "local-service-api.md",
              "security-remotes-and-wizard.md",
              "provisioning-and-release-lock.md",
              "filters-and-optimizer.md",
              "operations-and-windows-service.md"
            ]
          : [
              "arquitetura-e-motor-hibrido.md",
              "api-local-servico.md",
              "seguranca-remotes-e-wizard.md",
              "provisionamento-e-trava-release.md",
              "filtros-e-otimizador.md",
              "operacao-e-servico-windows.md"
            ];
      if (docsCountBadge) docsCountBadge.textContent = t("dyn.localModeBadge");
      docsSidebar.innerHTML = fallbackFiles
        .map(
          (f) =>
            `<a class="doc-nav-btn" href="${source.blobBase}${encodeURIComponent(f)}" target="_blank" rel="noopener noreferrer"><span class="doc-nav-title">${escapeHtml(f)}</span><span class="doc-nav-file">${escapeHtml(t("dyn.openGitHub"))}</span></a>`
        )
        .join("");
      docsViewer.innerHTML = `
        <div class="doc-section-card md-body">
          <h3>${t("dyn.localTitle")}</h3>
          <p>${t("dyn.localP1")}</p>
          <p>${t("dyn.localP2")}</p>
          <pre><code>python -m http.server 8080</code></pre>
          <p>${t("dyn.localP3")}</p>
        </div>
      `;
      return;
    }

    const fileNames = await discoverMarkdownFiles();
    const copyLabel = t("dyn.copy");
    const results = await Promise.all(
      fileNames.map(async (file) => {
        try {
          const res = await fetch(`${source.basePath}${file}`, { cache: "no-store" });
          if (!res.ok) return null;
          const raw = await res.text();
          return {
            file,
            title: extractTitle(raw, file),
            raw,
            html: renderMarkdown(raw, { stripLeadingH1: true, copyLabel })
          };
        } catch {
          return null;
        }
      })
    );

    docsState.docs = results.filter(Boolean);

    const hashDoc = getDocHash();
    if (!preserveIndex && hashDoc && docsState.docs.some((d) => d.file === hashDoc)) {
      docsState.activeFile = hashDoc;
      docsState.activeIndex = docsState.docs.findIndex((d) => d.file === hashDoc);
    } else if (preserveIndex && docsState.docs.length > 0) {
      const safeIdx = Math.min(Math.max(docsState.activeIndex, 0), docsState.docs.length - 1);
      docsState.activeIndex = safeIdx;
      docsState.activeFile = docsState.docs[safeIdx].file;
      if (hashDoc) setDocHash(docsState.activeFile);
    } else if (docsState.docs.length > 0) {
      docsState.activeIndex = 0;
      docsState.activeFile = docsState.docs[0].file;
    } else {
      docsState.activeFile = null;
    }

    if (docsState.docs.length === 0) {
      if (docsCountBadge) docsCountBadge.textContent = t("dyn.zeroGuides");
      docsViewer.innerHTML = `<div class="docs-empty">${t("dyn.loadError", { folder: source.folderLabel, url: source.blobBase })}</div>`;
    }

    renderDocsHub();

    if (!preserveIndex && hashDoc && docsState.docs.some((d) => d.file === hashDoc)) {
      scrollToDocOrHub(hashDoc);
    }
  }

  const langPtBtn = document.getElementById("langPtBtn");
  const langEnBtn = document.getElementById("langEnBtn");

  function applyLanguage(lang, { persist = false, updateUrl = false, reloadDocs = false } = {}) {
    currentLang = normalizeLang(lang) || "pt";

    document.documentElement.lang = currentLang === "en" ? "en" : "pt-BR";
    document.title = t("meta.title");

    const metaDesc = document.querySelector('meta[name="description"]');
    if (metaDesc) metaDesc.setAttribute("content", t("meta.description"));

    if (langPtBtn && langEnBtn) {
      langPtBtn.classList.toggle("active", currentLang === "pt");
      langEnBtn.classList.toggle("active", currentLang === "en");
    }

    document.querySelectorAll("[data-i18n]").forEach((el) => {
      el.textContent = t(el.getAttribute("data-i18n"));
    });

    document.querySelectorAll("[data-i18n-html]").forEach((el) => {
      el.innerHTML = t(el.getAttribute("data-i18n-html"));
    });

    document.querySelectorAll("[data-i18n-placeholder]").forEach((el) => {
      el.setAttribute("placeholder", t(el.getAttribute("data-i18n-placeholder")));
    });

    document.querySelectorAll("[data-i18n-aria]").forEach((el) => {
      el.setAttribute("aria-label", t(el.getAttribute("data-i18n-aria")));
    });

    if (persist) {
      try {
        localStorage.setItem(LANG_STORAGE_KEY, currentLang);
      } catch {
        // Sem armazenamento local
      }
    }

    if (updateUrl) {
      const nextUrl = new URL(window.location.href);
      nextUrl.searchParams.set("lang", currentLang);
      history.replaceState(null, "", `${nextUrl.pathname}${nextUrl.search}${window.location.hash}`);
    }

    if (reloadDocs) {
      initDocsHub({ preserveIndex: true });
    }
  }

  if (langPtBtn) {
    langPtBtn.addEventListener("click", () => {
      if (currentLang !== "pt") applyLanguage("pt", { persist: true, updateUrl: true, reloadDocs: true });
    });
  }

  if (langEnBtn) {
    langEnBtn.addEventListener("click", () => {
      if (currentLang !== "en") applyLanguage("en", { persist: true, updateUrl: true, reloadDocs: true });
    });
  }

  if (docsSearchInput) {
    docsSearchInput.addEventListener("input", () => {
      docsState.query = docsSearchInput.value;
      renderDocsHub();
    });
  }

  if (modeSingleBtn) {
    modeSingleBtn.addEventListener("click", () => {
      docsState.mode = "single";
      renderDocsHub();
    });
  }

  if (modeAllBtn) {
    modeAllBtn.addEventListener("click", () => {
      docsState.mode = "all";
      renderDocsHub();
    });
  }

  window.addEventListener("hashchange", () => {
    const hashDoc = getDocHash();
    if (hashDoc) {
      const foundIdx = docsState.docs.findIndex((d) => d.file === hashDoc);
      if (foundIdx >= 0) {
        docsState.activeFile = hashDoc;
        docsState.activeIndex = foundIdx;
        docsState.mode = "single";
        renderDocsHub();
        scrollToDocOrHub(hashDoc);
      }
    }
  });

  applyLanguage(currentLang, { persist: false, updateUrl: false, reloadDocs: false });
  initDocsHub({ preserveIndex: false });
})();
