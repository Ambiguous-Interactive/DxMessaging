/**
 * Mermaid theme-aware configuration for MkDocs Material
 *
 * This script:
 * 1. Detects the current MkDocs Material theme (light vs slate/dark)
 * 2. Initializes Mermaid with appropriate theme variables
 * 3. Listens for theme changes and re-renders diagrams
 * 4. Uses semantic class-based styling that works with both themes
 * 5. Strips per-diagram %%{init:...}%% directives to prevent theme override conflicts
 *
 * IMPORTANT: Do not use %%{init: {'theme': '...'}}%% directives in docs/ markdown files.
 * This script manages theming globally and per-diagram directives bypass the theme switching,
 * causing diagrams to render incorrectly in light mode. The stripping function below removes
 * such directives before rendering to ensure consistent behavior.
 */

(function () {
  "use strict";

  // Color palettes for light and dark themes
  // These provide semantic colors for diagram elements
  const lightTheme = {
    // Base colors
    primaryColor: "#e3f2fd", // Light blue background
    primaryTextColor: "#1565c0", // Dark blue text
    primaryBorderColor: "#1976d2", // Blue border
    // Secondary (success)
    secondaryColor: "#e8f5e9", // Light green background
    secondaryTextColor: "#2e7d32", // Dark green text
    secondaryBorderColor: "#388e3c", // Green border
    // Tertiary (warning/accent)
    tertiaryColor: "#fff3e0", // Light orange background
    tertiaryTextColor: "#c43e00", // Dark orange text (contrast ratio ~5.5:1)
    tertiaryBorderColor: "#f57c00", // Orange border
    // Quaternary (danger/red)
    quaternaryColor: "#ffebee", // Light red background
    quaternaryTextColor: "#b71c1c", // Dark red text
    quaternaryBorderColor: "#d32f2f", // Red border
    // Quinary (accent/purple) - fifth semantic color
    quinaryColor: "#f3e5f5", // Light purple background
    quinaryTextColor: "#4a148c", // Dark purple text
    quinaryBorderColor: "#8e24aa", // Purple border
    // Neutral colors
    background: "#ffffff",
    mainBkg: "#f5f5f5",
    lineColor: "#37474f",
    textColor: "#263238",
    // Flowchart specific
    nodeBorder: "#1976d2",
    clusterBkg: "#eceff1",
    clusterBorder: "#90a4ae",
    defaultLinkColor: "#37474f",
    // Sequence diagram specific
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
    // Base colors
    primaryColor: "#1e3a5f", // Dark blue background
    primaryTextColor: "#90caf9", // Light blue text
    primaryBorderColor: "#42a5f5", // Blue border
    // Secondary (success)
    secondaryColor: "#1b3d2e", // Dark green background
    secondaryTextColor: "#81c784", // Light green text
    secondaryBorderColor: "#66bb6a", // Green border
    // Tertiary (warning/accent)
    tertiaryColor: "#3d2e1a", // Dark orange background
    tertiaryTextColor: "#ffb74d", // Light orange text
    tertiaryBorderColor: "#ffa726", // Orange border
    // Quaternary (danger/red)
    quaternaryColor: "#3d1a1a", // Dark red background
    quaternaryTextColor: "#ef9a9a", // Light red text
    quaternaryBorderColor: "#ef5350", // Red border
    // Quinary (accent/purple) - fifth semantic color
    quinaryColor: "#2d1f3d", // Dark purple background
    quinaryTextColor: "#ce93d8", // Light purple text
    quinaryBorderColor: "#ab47bc", // Purple border
    // Neutral colors
    background: "#1e1e1e",
    mainBkg: "#2d2d2d",
    lineColor: "#90a4ae",
    textColor: "#e0e0e0",
    // Flowchart specific
    nodeBorder: "#42a5f5",
    clusterBkg: "#2d2d2d",
    clusterBorder: "#546e7a",
    defaultLinkColor: "#90a4ae",
    // Sequence diagram specific
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

  /**
   * Regex pattern to match Mermaid init directives
   * Matches %%{init: {...}}%% at the start of any line in diagram source
   *
   * SYNC: Keep pattern in sync with scripts/__tests__/mermaid-config.test.js INIT_DIRECTIVE_PATTERN
   *
   * Flags (gims):
   * - 'g' (global): Finds and replaces all occurrences, not just the first one
   * - 'i' (case-insensitive): Matches regardless of case (e.g., `%%{Init:...}%%` or `%%{INIT:...}%%`)
   * - 'm' (multiline): Makes '^' match at the start of every line, not just the start of the string -
   *   this ensures init directives are stripped wherever they appear in the diagram source
   * - 's' (dotAll): Makes '.' match any character including newlines, allowing '.*?' to span
   *   multi-line directives
   *
   * These directives can override our theme settings, so we strip them.
   * We use [ \t]* instead of \s* around the directive to avoid consuming newlines,
   * which would concatenate adjacent diagram lines and break Mermaid syntax.
   */
  const INIT_DIRECTIVE_PATTERN = /^[ \t]*%%\{init:.*?\}%%[ \t]*\r?\n?/gims;

  /**
   * Strip per-diagram init directives that would override our theme configuration
   *
   * SYNC: Keep logic in sync with scripts/__tests__/mermaid-config.test.js stripInitDirectives
   *
   * @param {string} source - The original Mermaid diagram source
   * @returns {string} The source with init directives removed
   */
  function stripInitDirectives(source) {
    return source.replace(INIT_DIRECTIVE_PATTERN, "");
  }

  /**
   * Detect if dark theme is currently active
   * MkDocs Material uses data-md-color-scheme attribute on body
   */
  function isDarkTheme() {
    const scheme = document.body.getAttribute("data-md-color-scheme");
    return scheme === "slate";
  }

  /**
   * Get the current theme configuration
   */
  function getThemeConfig() {
    return isDarkTheme() ? darkTheme : lightTheme;
  }

  /**
   * Build Mermaid configuration object with current theme
   * @param {Object} options - Optional overrides for sequence diagram settings
   * @returns {Object} Mermaid configuration object
   */
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

  /**
   * Initialize or reinitialize Mermaid with the current theme
   */
  function initMermaid() {
    if (typeof mermaid === "undefined") {
      // Mermaid not loaded yet, wait and retry
      setTimeout(initMermaid, 100);
      return;
    }

    mermaid.initialize(buildMermaidConfig());
    renderDiagrams();
  }

  /**
   * Render all Mermaid diagrams on the page
   */
  async function renderDiagrams() {
    if (typeof mermaid === "undefined") {
      return;
    }

    const diagrams = document.querySelectorAll(".mermaid:not([data-processed])");

    for (let i = 0; i < diagrams.length; i++) {
      const element = diagrams[i];
      const graphDefinition = element.textContent || element.innerText;

      // Skip if already processed or empty
      if (!graphDefinition.trim()) {
        continue;
      }

      try {
        // Generate unique ID for this diagram
        const id = `mermaid-diagram-${Date.now()}-${i}`;

        // Strip any per-diagram init directives that would override our theme
        const cleanedDefinition = stripInitDirectives(graphDefinition);

        // Render the diagram
        const { svg } = await mermaid.render(id, cleanedDefinition);
        element.innerHTML = svg;
        element.setAttribute("data-processed", "true");
      } catch (error) {
        console.error("Mermaid rendering error:", error);
        // Show user-friendly error message styled for current theme
        const errorColor = isDarkTheme() ? "#ff6b6b" : "#c62828";
        const bgColor = isDarkTheme() ? "#3d1a1a" : "#ffebee";
        const borderColor = isDarkTheme() ? "#ef5350" : "#d32f2f";
        element.innerHTML = `<div style="padding: 1rem; border: 1px solid ${borderColor}; border-radius: 4px; background: ${bgColor}; color: ${errorColor}; font-family: system-ui, sans-serif;">⚠️ Diagram failed to render. Check console for details.</div>`;
      }
    }
  }

  /**
   * Re-render all diagrams (used after theme change)
   */
  async function reRenderDiagrams() {
    if (typeof mermaid === "undefined") {
      return;
    }

    // Reinitialize mermaid with new theme using shared config
    mermaid.initialize(buildMermaidConfig());

    // Find all processed diagrams and re-render them
    const diagrams = document.querySelectorAll(".mermaid[data-processed]");

    for (let i = 0; i < diagrams.length; i++) {
      const element = diagrams[i];

      // Get original source from data attribute or SVG title
      const originalSource = element.getAttribute("data-original-source");
      if (!originalSource) {
        continue;
      }

      try {
        const id = `mermaid-rerender-${Date.now()}-${i}`;
        // Strip any per-diagram init directives that would override our theme
        const cleanedSource = stripInitDirectives(originalSource);
        const { svg } = await mermaid.render(id, cleanedSource);
        element.innerHTML = svg;
      } catch (error) {
        console.error("Mermaid re-rendering error:", error);
      }
    }
  }

  /**
   * Store original diagram sources before first render
   */
  function storeOriginalSources() {
    const diagrams = document.querySelectorAll(".mermaid:not([data-original-source])");
    diagrams.forEach((element) => {
      const source = element.textContent || element.innerText;
      if (source.trim()) {
        element.setAttribute("data-original-source", source.trim());
      }
    });
  }

  /**
   * Create a debounced version of a function
   * @param {Function} func - Function to debounce
   * @param {number} wait - Milliseconds to wait before invoking
   * @returns {Function} Debounced function
   */
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

  /**
   * Set up theme change observer with debouncing
   */
  function observeThemeChanges() {
    // Debounce re-renders to prevent rapid theme toggle issues.
    // 150ms chosen as a balance: fast enough to feel responsive to users,
    // but slow enough to coalesce rapid theme toggles and avoid redundant renders.
    // Lower values (50-100ms) may cause double-renders; higher values (200ms+) feel sluggish.
    const debouncedReRender = debounce(reRenderDiagrams, 150);

    // MkDocs Material changes the data-md-color-scheme attribute on the body
    const observer = new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (mutation.type === "attributes" && mutation.attributeName === "data-md-color-scheme") {
          // Theme changed, re-render diagrams (debounced)
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

  /**
   * Initialize everything when DOM is ready
   */
  function init() {
    // Store original sources before any rendering
    storeOriginalSources();

    // Initialize Mermaid
    initMermaid();

    // Set up theme change observer
    observeThemeChanges();

    // Also handle MkDocs instant navigation (if enabled)
    // MkDocs fires a 'DOMContentLoaded'-like event when new content loads
    if (typeof document$ !== "undefined") {
      document$.subscribe(function () {
        storeOriginalSources();
        initMermaid();
      });
    }
  }

  // Wait for DOM to be ready
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
