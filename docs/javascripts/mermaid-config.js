(function () {
  "use strict";

  const lightTheme = {
    primaryColor: "#e3f2fd",
    primaryTextColor: "#1565c0",
    primaryBorderColor: "#1976d2",
    secondaryColor: "#e8f5e9",
    secondaryTextColor: "#2e7d32",
    secondaryBorderColor: "#388e3c",
    tertiaryColor: "#fff3e0",
    tertiaryTextColor: "#c43e00",
    tertiaryBorderColor: "#f57c00",
    quaternaryColor: "#ffebee",
    quaternaryTextColor: "#b71c1c",
    quaternaryBorderColor: "#d32f2f",
    quinaryColor: "#f3e5f5",
    quinaryTextColor: "#4a148c",
    quinaryBorderColor: "#8e24aa",
    background: "#ffffff",
    mainBkg: "#f5f5f5",
    lineColor: "#37474f",
    textColor: "#263238",
    nodeBorder: "#1976d2",
    clusterBkg: "#eceff1",
    clusterBorder: "#90a4ae",
    defaultLinkColor: "#37474f",
    actorBkg: "#e3f2fd",
    actorBorder: "#1976d2",
    actorTextColor: "#1565c0",
    actorLineColor: "#37474f",
    signalColor: "#37474f",
    signalTextColor: "#263238",
    labelBoxBkgColor: "#eceff1",
    labelBoxBorderColor: "#90a4ae",
    labelTextColor: "#263238",
    loopTextColor: "#263238",
    noteBorderColor: "#f57c00",
    noteBkgColor: "#fff3e0",
    noteTextColor: "#c43e00",
    activationBorderColor: "#1976d2",
    activationBkgColor: "#e3f2fd",
    sequenceNumberColor: "#ffffff"
  };

  const darkTheme = {
    primaryColor: "#1e3a5f",
    primaryTextColor: "#90caf9",
    primaryBorderColor: "#42a5f5",
    secondaryColor: "#1b3d2e",
    secondaryTextColor: "#81c784",
    secondaryBorderColor: "#66bb6a",
    tertiaryColor: "#3d2e1a",
    tertiaryTextColor: "#ffb74d",
    tertiaryBorderColor: "#ffa726",
    quaternaryColor: "#3d1a1a",
    quaternaryTextColor: "#ef9a9a",
    quaternaryBorderColor: "#ef5350",
    quinaryColor: "#2d1f3d",
    quinaryTextColor: "#ce93d8",
    quinaryBorderColor: "#ab47bc",
    background: "#1e1e1e",
    mainBkg: "#2d2d2d",
    lineColor: "#90a4ae",
    textColor: "#e0e0e0",
    nodeBorder: "#42a5f5",
    clusterBkg: "#2d2d2d",
    clusterBorder: "#546e7a",
    defaultLinkColor: "#90a4ae",
    actorBkg: "#1e3a5f",
    actorBorder: "#42a5f5",
    actorTextColor: "#90caf9",
    actorLineColor: "#90a4ae",
    signalColor: "#90a4ae",
    signalTextColor: "#e0e0e0",
    labelBoxBkgColor: "#2d2d2d",
    labelBoxBorderColor: "#546e7a",
    labelTextColor: "#e0e0e0",
    loopTextColor: "#e0e0e0",
    noteBorderColor: "#ffa726",
    noteBkgColor: "#3d2e1a",
    noteTextColor: "#ffb74d",
    activationBorderColor: "#42a5f5",
    activationBkgColor: "#1e3a5f",
    sequenceNumberColor: "#1e1e1e"
  };

  // Strip diagram-level Mermaid init directives without consuming neighboring lines.
  const INIT_DIRECTIVE_PATTERN = /^[ \t]*%%\{init:.*?\}%%[ \t]*\r?\n?/gims;

  function stripInitDirectives(source) {
    return source.replace(INIT_DIRECTIVE_PATTERN, "");
  }

  function isDarkTheme() {
    const scheme = document.body.getAttribute("data-md-color-scheme");
    return scheme === "slate";
  }

  function getThemeConfig() {
    return isDarkTheme() ? darkTheme : lightTheme;
  }

  function buildMermaidConfig(options = {}) {
    const themeVars = getThemeConfig();
    const sequenceDefaults = {
      useMaxWidth: true,
      wrap: true
    };

    return {
      startOnLoad: false,
      theme: "base",
      themeVariables: themeVars,
      flowchart: {
        htmlLabels: true,
        curve: "basis",
        useMaxWidth: true
      },
      sequence: {
        ...sequenceDefaults,
        diagramMarginX: 50,
        diagramMarginY: 10,
        actorMargin: 50,
        boxMargin: 10,
        boxTextMargin: 5,
        noteMargin: 10,
        messageMargin: 35,
        ...options.sequence
      },
      securityLevel: "loose"
    };
  }

  function initMermaid() {
    if (typeof mermaid === "undefined") {
      setTimeout(initMermaid, 100);
      return;
    }

    mermaid.initialize(buildMermaidConfig());
    renderDiagrams();
  }

  async function renderDiagrams() {
    if (typeof mermaid === "undefined") {
      return;
    }

    const diagrams = document.querySelectorAll(".mermaid:not([data-processed])");

    for (let i = 0; i < diagrams.length; i++) {
      const element = diagrams[i];
      const graphDefinition = element.textContent || element.innerText;

      if (!graphDefinition.trim()) {
        continue;
      }

      try {
        const id = `mermaid-diagram-${Date.now()}-${i}`;
        const cleanedDefinition = stripInitDirectives(graphDefinition);
        const { svg } = await mermaid.render(id, cleanedDefinition);
        element.innerHTML = svg;
        element.setAttribute("data-processed", "true");
      } catch (error) {
        console.error("Mermaid rendering error:", error);
        const errorColor = isDarkTheme() ? "#ff6b6b" : "#c62828";
        const bgColor = isDarkTheme() ? "#3d1a1a" : "#ffebee";
        const borderColor = isDarkTheme() ? "#ef5350" : "#d32f2f";
        element.innerHTML = `<div style="padding: 1rem; border: 1px solid ${borderColor}; border-radius: 4px; background: ${bgColor}; color: ${errorColor}; font-family: system-ui, sans-serif;">⚠️ Diagram failed to render. Check console for details.</div>`;
      }
    }
  }

  async function reRenderDiagrams() {
    if (typeof mermaid === "undefined") {
      return;
    }

    mermaid.initialize(buildMermaidConfig());
    const diagrams = document.querySelectorAll(".mermaid[data-processed]");

    for (let i = 0; i < diagrams.length; i++) {
      const element = diagrams[i];

      const originalSource = element.getAttribute("data-original-source");
      if (!originalSource) {
        continue;
      }

      try {
        const id = `mermaid-rerender-${Date.now()}-${i}`;
        const cleanedSource = stripInitDirectives(originalSource);
        const { svg } = await mermaid.render(id, cleanedSource);
        element.innerHTML = svg;
      } catch (error) {
        console.error("Mermaid re-rendering error:", error);
      }
    }
  }

  function storeOriginalSources() {
    const diagrams = document.querySelectorAll(".mermaid:not([data-original-source])");
    diagrams.forEach((element) => {
      const source = element.textContent || element.innerText;
      if (source.trim()) {
        element.setAttribute("data-original-source", source.trim());
      }
    });
  }

  function debounce(func, wait) {
    let timeoutId = null;
    return function (...args) {
      if (timeoutId !== null) {
        clearTimeout(timeoutId);
      }
      timeoutId = setTimeout(() => {
        func.apply(this, args);
        timeoutId = null;
      }, wait);
    };
  }

  function observeThemeChanges() {
    const debouncedReRender = debounce(reRenderDiagrams, 150);
    const observer = new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (mutation.type === "attributes" && mutation.attributeName === "data-md-color-scheme") {
          debouncedReRender();
          break;
        }
      }
    });

    observer.observe(document.body, {
      attributes: true,
      attributeFilter: ["data-md-color-scheme"]
    });
  }

  function init() {
    storeOriginalSources();
    initMermaid();
    observeThemeChanges();

    if (typeof document$ !== "undefined") {
      document$.subscribe(function () {
        storeOriginalSources();
        initMermaid();
      });
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
