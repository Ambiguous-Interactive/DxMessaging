#!/usr/bin/env node

/**
 * Generate _Sidebar.md for GitHub Wiki from wiki page structure.
 */

const fs = require("fs");
const path = require("path");

const NAV_STRUCTURE = [
  { title: "Home", page: "Home" },
  {
    title: "Getting Started",
    children: [
      { title: "Overview", page: "Getting-Started-Overview" },
      { title: "Getting Started Guide", page: "Getting-Started-Getting-Started" },
      { title: "Installation", page: "Getting-Started-Install" },
      { title: "Quick Start", page: "Getting-Started-Quick-Start" },
      { title: "Visual Guide", page: "Getting-Started-Visual-Guide" }
    ]
  },
  {
    title: "Concepts",
    children: [
      { title: "Message Types", page: "Concepts-Message-Types" },
      { title: "Listening Patterns", page: "Concepts-Listening-Patterns" },
      { title: "Targeting & Context", page: "Concepts-Targeting-And-Context" },
      { title: "Interceptors", page: "Concepts-Interceptors-And-Ordering" }
    ]
  },
  {
    title: "Guides",
    children: [
      { title: "Patterns", page: "Guides-Patterns" },
      { title: "Unity Integration", page: "Guides-Unity-Integration" },
      { title: "Testing", page: "Guides-Testing" },
      { title: "Diagnostics", page: "Guides-Diagnostics" },
      { title: "Advanced", page: "Guides-Advanced" },
      { title: "Migration", page: "Guides-Migration-Guide" }
    ]
  },
  {
    title: "Architecture",
    children: [
      { title: "Design", page: "Architecture-Design-And-Architecture" },
      { title: "Comparisons", page: "Architecture-Comparisons" },
      { title: "Performance", page: "Architecture-Performance" }
    ]
  },
  {
    title: "Advanced",
    children: [
      { title: "Emit Shorthands", page: "Advanced-Emit-Shorthands" },
      { title: "Message Bus Providers", page: "Advanced-Message-Bus-Providers" },
      { title: "Runtime Config", page: "Advanced-Runtime-Configuration" },
      { title: "String Messages", page: "Advanced-String-Messages" }
    ]
  },
  {
    title: "Integrations",
    children: [
      { title: "VContainer", page: "Integrations-Vcontainer" },
      { title: "Zenject", page: "Integrations-Zenject" },
      { title: "Reflex", page: "Integrations-Reflex" }
    ]
  },
  {
    title: "Examples",
    children: [
      { title: "End-to-End", page: "Examples-End-To-End" },
      { title: "Scene Transitions", page: "Examples-End-To-End-Scene-Transitions" }
    ]
  },
  {
    title: "Reference",
    children: [
      { title: "API Reference", page: "Reference-Reference" },
      { title: "Quick Reference", page: "Reference-Quick-Reference" },
      { title: "Helpers", page: "Reference-Helpers" },
      { title: "FAQ", page: "Reference-Faq" },
      { title: "Glossary", page: "Reference-Glossary" },
      { title: "Troubleshooting", page: "Reference-Troubleshooting" },
      { title: "Compatibility", page: "Reference-Compatibility" }
    ]
  }
];

function pageExists(wikiDir, pageName) {
  return fs.existsSync(path.join(wikiDir, `${pageName}.md`));
}

function generateSidebar(wikiDir) {
  const lines = ["# DxMessaging Wiki", ""];

  for (const item of NAV_STRUCTURE) {
    if (item.children) {
      lines.push(`### ${item.title}`);
      for (const child of item.children) {
        if (pageExists(wikiDir, child.page)) {
          lines.push(`- [[${child.page}|${child.title}]]`);
        } else {
          console.warn(`Missing page: ${child.page}.md - marked as "coming soon"`);
          lines.push(`- ${child.title} *(coming soon)*`);
        }
      }
      lines.push("");
    } else {
      if (pageExists(wikiDir, item.page)) {
        lines.push(`- [[${item.page}|${item.title}]]`);
      }
    }
  }

  lines.push("---");
  lines.push("");
  lines.push("**Links**");
  lines.push("- [📦 GitHub](https://github.com/Ambiguous-Interactive/DxMessaging)");
  lines.push("- [📖 Documentation](https://ambiguous-interactive.github.io/DxMessaging/)");

  return lines.join("\n");
}

// Main
function main() {
  const args = process.argv.slice(2);
  if (args.length < 1) {
    console.error("Usage: node generate-wiki-sidebar.js <wiki-dir>");
    process.exit(1);
  }

  const wikiDir = path.resolve(args[0]);
  const sidebar = generateSidebar(wikiDir);
  const sidebarPath = path.join(wikiDir, "_Sidebar.md");
  fs.writeFileSync(sidebarPath, sidebar);
  console.log("Generated _Sidebar.md");
}

// Only run main when executed directly (not when required as a module)
if (require.main === module) {
  try {
    main();
  } catch (error) {
    console.error("Error generating wiki sidebar:", error.message);
    process.exit(1);
  }
}
