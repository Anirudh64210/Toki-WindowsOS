# design-sync notes for the Toki repo

- This repo is a C#/WPF app with NO JavaScript. `design-web/` is a hand-authored React MIRROR of
  the app's design system (source of truth: `src/Toki/Theme/ThemeManager.cs` + `Theme.xaml` +
  the Figma file Udtb5KHB2iTLIVP5FhKaSl node 288:4). When the WPF theme changes, update
  `design-web/src/styles/tokens.css` + components to match, then re-sync.
- Build is `npm run build` inside `design-web/` (tsc + `scripts/build-css.mjs`, which concatenates
  tokens.css + components.css into `dist/toki.css` - the cssEntry MUST be that compiled file; the
  `src/styles/toki.css` @import stub trips [CSS_IMPORT_MISSING]).
- Converter invocation (from repo root):
  `node .ds-sync/package-build.mjs --config .design-sync/config.json --node-modules design-web/node_modules --entry design-web/dist/index.js --out ./ds-bundle`
- NO playwright browser is installed. The validate/capture harnesses run against system Chrome via
  `DS_CHROMIUM_PATH="C:/Program Files/Google/Chrome/Application/chrome.exe"` - set it on EVERY
  validate/capture/driver run or capture dies asking for `npx playwright install`.
- npm here blocks postinstall scripts (allow-scripts). esbuild worked regardless; if it ever fails
  to import, approve its script.
- All 15 previews are authored in `.design-sync/previews/` and graded good (2026-08-14). Previews
  wrap content in dark grounds (#0E0E10 / #161618 / a wallpaper gradient) because the components
  are dark-first; a preview without a ground renders white-on-white.
- `cardMode: column` overrides for GlanceRow / GlassPanel / NotchShell (wide stories) live in
  config `overrides`.
- Fonts: the system is Segoe UI Variable via a system font stack - nothing to ship, no
  [FONT_MISSING] fired. On non-Windows viewers it falls back to system-ui by design.
- The owner (Sid, they/them) does ALL git; never commit from a sync run - report what is safe to
  commit instead.

## Re-sync risks

- The mirror can silently DRIFT from the WPF theme - nothing diffs them. Before a re-sync, check
  ThemeManager.cs for new/changed tokens (the palette table) and port them.
- `guidelines/` content is a distillation of the Figma file; if the Figma design changes, the doc
  (design-web/docs/guides/toki-design-language.md) must be re-distilled by hand.
- The conventions header references the guidelines doc generically ("under guidelines/") because
  the converter preserves the docs/guides subpath - don't "fix" it to a literal path without
  checking the uploaded layout.
- Verified against Chrome (system, via DS_CHROMIUM_PATH), not playwright's pinned chromium.
