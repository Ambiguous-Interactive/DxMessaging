# DxMessaging — production assets

Everything in the locked Dx-family direction: DxKit system, amber product hue,
"Decoupled, simple systems." lead.

## brand/ (vector, source of truth)

| file | use |
|---|---|
| `dxmessaging-mark.svg` | The mark on dark. Docs logo, README, embeds. |
| `dxmessaging-mark-light.svg` | The mark on paper/light backgrounds. |
| `dxmessaging-mark-mono.svg` | Single color, inherits `currentColor`. Footers, stamps. |
| `dxmessaging-icon-tile.svg` | Mark on the dark rounded tile (160). Avatars, app icons. |

## png/ (rendered)

| file | size | use |
|---|---|---|
| `dxmessaging-banner.png` | 800×200 | README hero. Replaces `docs/images/DxMessaging-banner.svg`. |
| `dxmessaging-og-1200x630.png` | 1200×630 | Open Graph / social. |
| `dxmessaging-store-icon-320.png` | 320×320 | Asset Store icon (downscale to 160 on upload). |
| `dxmessaging-store-card-420x280.png` | 420×280 | Asset Store card image. |
| `icon-256.png` | 256 | Package / large app icon. |
| `favicon-48.png` / `favicon-32.png` | 48 / 32 | Full signal mark. |
| `favicon-16.png` | 16 | Solid amber tile + node (signal detail drops below 32). |

## mkdocs/

1. Copy `stylesheets/extra.css` to `docs/stylesheets/extra.css`.
2. Copy `overrides/home.html` to `docs/overrides/home.html`.
3. Merge `mkdocs-theme-snippet.yml` into `mkdocs.yml`
   (replaces the current indigo/Roboto `theme:` block; keep your `nav:`,
   `plugins:`, and `markdown_extensions:` as they are).
4. Copy `brand/dxmessaging-mark.svg` and `png/favicon-32.png` into `docs/images/`.
5. Add front-matter to `docs/index.md`:

   ```yaml
   ---
   template: home.html
   hide:
     - navigation
     - toc
   ---
   ```

Taxonomy badges are available in markdown via attr_list:
`[Untargeted]{ .dxm-badge .dxm-untargeted }` (also `.dxm-targeted`, `.dxm-broadcast`).

## Reference

- Direction board: `DxMessaging × DxKit Alignment.dc.html` (§00–§12).
- Render source for the PNGs: `production/Export Sheet.dc.html`.
- Archive of the earlier violet exploration: `DxMessaging Brand Direction.dc.html`.
