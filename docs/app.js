(() => {
  const PIX_KEY = "95b9aa86-3c99-421d-9c86-6a47a3b09d7b";
  const GITHUB_BLOB_BASE = "https://github.com/Dillemon-Recla/RcloneManager/blob/dev/docs/";
  const GITHUB_API_DOCS = [
    "https://api.github.com/repos/Dillemon-Recla/RcloneManager/contents/docs",
    "https://api.github.com/repos/Dillemon-Recla/RcloneManager/contents/docs?ref=dev"
  ];

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
      const target = tab.getAttribute("data-tab");
      mockupTabs.forEach((t) => t.classList.remove("active"));
      mockupPanes.forEach((p) => p.classList.remove("active"));
      tab.classList.add("active");
      const pane = document.getElementById(`pane-${target}`);
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
        const orig = copyQuickstartBtn.textContent;
        copyQuickstartBtn.textContent = "Copiado!";
        setTimeout(() => { copyQuickstartBtn.textContent = orig; }, 1800);
      } catch {
        copyQuickstartBtn.textContent = "Selecione manualmente";
      }
    });
  }

  // Botão copiar Pix
  const copyPixBtn = document.getElementById("copyPixBtn");
  const pixFeedback = document.getElementById("pixFeedback");
  if (copyPixBtn && pixFeedback) {
    copyPixBtn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(PIX_KEY);
        pixFeedback.textContent = "Chave Pix copiada para a área de transferência!";
      } catch {
        pixFeedback.textContent = `Copie manualmente: ${PIX_KEY}`;
      }
      setTimeout(() => { pixFeedback.textContent = ""; }, 3200);
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

  function formatInline(text) {
    let out = escapeHtml(text);
    out = out.replace(/`([^`]+)`/g, "<code>$1</code>");
    out = out.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    out = out.replace(/\*([^*]+)\*/g, "<em>$1</em>");
    out = out.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+|#[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
    out = out.replace(/\[([^\]]+)\]\(([^)\s]+\.md)\)/g, '<a href="#doc=$2" data-internal-doc="$2">$1</a>');
    return out;
  }

  function renderMarkdown(md) {
    const lines = md.replace(/\r\n/g, "\n").split("\n");
    const html = [];
    let i = 0;

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
          `<div class="code-block-wrap"><button type="button" class="code-copy-btn">Copiar</button><pre${langAttr}><code>${escapeHtml(codeLines.join("\n"))}</code></pre></div>`
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
        let tableHtml = "<table><thead><tr>";
        headers.forEach((h) => { tableHtml += `<th>${formatInline(h)}</th>`; });
        tableHtml += "</tr></thead><tbody>";
        rows.forEach((r) => {
          tableHtml += "<tr>";
          r.forEach((c) => { tableHtml += `<td>${formatInline(c)}</td>`; });
          tableHtml += "</tr>";
        });
        tableHtml += "</tbody></table>";
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
    mode: "single",
    query: ""
  };

  async function discoverMarkdownFiles() {
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
      const manifestRes = await fetch("docs-manifest.json", { cache: "no-store" });
      if (manifestRes.ok) {
        const manifest = await manifestRes.json();
        if (Array.isArray(manifest)) manifest.forEach(addFile);
      }
    } catch {
      // Silencioso: fallback para descoberta via API ou file://
    }

    for (const apiUrl of GITHUB_API_DOCS) {
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
      return decodeURIComponent(hash.slice(5));
    } catch {
      return hash.slice(5);
    }
  }

  function setDocHash(fileName) {
    if (!fileName) return;
    const nextHash = `#doc=${encodeURIComponent(fileName)}`;
    if (window.location.hash !== nextHash) {
      history.replaceState(null, "", nextHash);
    }
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
          const orig = btn.textContent;
          btn.textContent = "Copiado!";
          setTimeout(() => { btn.textContent = orig; }, 1600);
        } catch {
          btn.textContent = "Erro";
        }
      });
    });

    docsViewer.querySelectorAll("a[data-internal-doc]").forEach((link) => {
      link.addEventListener("click", (e) => {
        const targetFile = link.getAttribute("data-internal-doc");
        const exists = docsState.docs.some((d) => d.file === targetFile);
        if (!exists) return;
        e.preventDefault();
        docsState.activeFile = targetFile;
        docsState.mode = "single";
        setDocHash(targetFile);
        renderDocsHub();
      });
    });

    docsViewer.querySelectorAll("button[data-copy-doc-link]").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const fileName = btn.getAttribute("data-copy-doc-link");
        const url = `${window.location.origin}${window.location.pathname}#doc=${encodeURIComponent(fileName)}`;
        try {
          await navigator.clipboard.writeText(url);
          const orig = btn.textContent;
          btn.textContent = "Link copiado!";
          setTimeout(() => { btn.textContent = orig; }, 1600);
        } catch {
          btn.textContent = "Copie a URL";
        }
      });
    });
  }

  function renderDocSectionHtml(doc) {
    const slug = doc.file.replace(/[^a-zA-Z0-9_-]/g, "-");
    return `
      <article id="section-${escapeHtml(slug)}" class="doc-section-card">
        <header class="doc-section-header">
          <div class="doc-section-meta">
            <h3>${escapeHtml(doc.title)}</h3>
            <span class="doc-section-filename">${escapeHtml(doc.file)}</span>
          </div>
          <div class="doc-section-actions">
            <button type="button" class="btn btn-sm" data-copy-doc-link="${escapeHtml(doc.file)}">Copiar link</button>
            <a class="btn btn-sm" href="${GITHUB_BLOB_BASE}${encodeURIComponent(doc.file)}" target="_blank" rel="noopener noreferrer">Ver no GitHub</a>
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
          ? `${total} guias .md`
          : `${filtered.length} de ${total} guias .md`;
    }

    if (docsState.docs.length === 0) {
      docsSidebar.innerHTML = '<div class="docs-empty">Nenhum guia carregado localmente.</div>';
      return;
    }

    if (filtered.length === 0) {
      docsSidebar.innerHTML = '<div class="docs-empty">Nenhum guia corresponde à busca.</div>';
      docsViewer.innerHTML = '<div class="docs-empty">Nenhuma seção encontrada para o filtro atual. Limpe a busca para ver todos os guias <code>.md</code>.</div>';
      return;
    }

    if (!docsState.activeFile || !filtered.some((d) => d.file === docsState.activeFile)) {
      docsState.activeFile = filtered[0].file;
    }

    docsSidebar.innerHTML = filtered
      .map((doc) => {
        const isActive = docsState.mode === "single" && doc.file === docsState.activeFile;
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
        setDocHash(fileName);
        if (docsState.mode === "all") {
          const slug = fileName.replace(/[^a-zA-Z0-9_-]/g, "-");
          const target = document.getElementById(`section-${slug}`);
          if (target) target.scrollIntoView({ behavior: "smooth", block: "start" });
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

  async function initDocsHub() {
    if (!docsSidebar || !docsViewer) return;

    if (window.location.protocol === "file:") {
      const fallbackFiles = [
        "arquitetura-e-motor-hibrido.md",
        "api-local-servico.md",
        "seguranca-remotes-e-wizard.md",
        "provisionamento-e-trava-release.md",
        "filtros-e-otimizador.md",
        "operacao-e-servico-windows.md"
      ];
      if (docsCountBadge) docsCountBadge.textContent = "Modo arquivo local";
      docsSidebar.innerHTML = fallbackFiles
        .map(
          (f) =>
            `<a class="doc-nav-btn" href="${GITHUB_BLOB_BASE}${encodeURIComponent(f)}" target="_blank" rel="noopener noreferrer"><span class="doc-nav-title">${escapeHtml(f)}</span><span class="doc-nav-file">Abrir no GitHub</span></a>`
        )
        .join("");
      docsViewer.innerHTML = `
        <div class="doc-section-card md-body">
          <h3>Visualização local via <code>file://</code></h3>
          <p>Navegadores bloqueiam <code>fetch()</code> de arquivos <code>.md</code> quando a página é aberta diretamente do disco (<code>file://</code>). No GitHub Pages ou em qualquer servidor HTTP local, todos os arquivos <code>docs/*.md</code> são lidos automaticamente como seções.</p>
          <p>Para testar localmente com todas as seções automáticas, rode na pasta <code>docs/</code>:</p>
          <pre><code>python -m http.server 8080</code></pre>
          <p>Depois acesse <code>http://localhost:8080</code> ou abra qualquer guia diretamente pelos links na barra lateral.</p>
        </div>
      `;
      return;
    }

    const fileNames = await discoverMarkdownFiles();
    const results = await Promise.all(
      fileNames.map(async (file) => {
        try {
          const res = await fetch(file, { cache: "no-store" });
          if (!res.ok) return null;
          const raw = await res.text();
          return {
            file,
            title: extractTitle(raw, file),
            raw,
            html: renderMarkdown(raw)
          };
        } catch {
          return null;
        }
      })
    );

    docsState.docs = results.filter(Boolean);

    const hashDoc = getDocHash();
    if (hashDoc && docsState.docs.some((d) => d.file === hashDoc)) {
      docsState.activeFile = hashDoc;
    } else if (docsState.docs.length > 0) {
      docsState.activeFile = docsState.docs[0].file;
    }

    if (docsState.docs.length === 0) {
      if (docsCountBadge) docsCountBadge.textContent = "0 guias carregados";
      docsViewer.innerHTML = `<div class="docs-empty">Não foi possível carregar os arquivos <code>.md</code>. Verifique se estão publicados em <code>docs/</code> ou acesse pelo <a href="${GITHUB_BLOB_BASE}" target="_blank" rel="noopener noreferrer">GitHub</a>.</div>`;
    }

    renderDocsHub();
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
    if (hashDoc && docsState.docs.some((d) => d.file === hashDoc)) {
      docsState.activeFile = hashDoc;
      docsState.mode = "single";
      renderDocsHub();
    }
  });

  initDocsHub();
})();
