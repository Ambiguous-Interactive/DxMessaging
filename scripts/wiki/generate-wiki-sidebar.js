#!/usr/bin/env node

/**
 * Generate _Sidebar.md for GitHub Wiki from wiki page structure.
 */

const fs = require("fs");
const path = require("path");

const NAV_STRUCTURE = require("./wiki-navigation.json");

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

  lines.push(
    "---",
    "",
    "**Links**",
    "- [📦 GitHub](https://github.com/Ambiguous-Interactive/DxMessaging)",
    "- [📖 Documentation](https://ambiguous-interactive.github.io/DxMessaging/)"
  );

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
