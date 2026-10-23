import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
const upstream = fileURLToPath(new URL('../orbit-upstream/', import.meta.url));
const adapter = fileURLToPath(new URL('./', import.meta.url));
const require = createRequire(path.join(upstream, 'package.json'));
const { defineConfig } = await import(pathToFileURL(require.resolve('vite')).href);
const { default: react } = await import(pathToFileURL(require.resolve('@vitejs/plugin-react')).href);
const normal = value => value.replaceAll('\\', '/');
const substitutes = new Set(['AutoStart', 'Library', 'Downloads', 'Sources', 'LibraryActions', 'DownloadLimit', 'ArchiveSetting', 'Pairing', 'UpdateNotice', 'NativeAppNotice', 'interfaces/SettingsPage']);
export default defineConfig({
  root: upstream,
  plugins: [{
    name: 'windows-boundary', enforce: 'pre',
    transformIndexHtml(html) {
      html = html.replace('<title>Orbit Store</title>', '<title>OrbitX</title>');
      return { html, tags: [{ tag: 'meta', attrs: { 'http-equiv': 'Content-Security-Policy', content: "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: http://127.0.0.1:* http://localhost:*; font-src 'self'; connect-src 'self'; object-src 'none'; frame-src https://www.youtube-nocookie.com; worker-src 'none'; base-uri 'none'; form-action 'none'" }, injectTo: 'head-prepend' }] };
    },
    resolveId(source, importer) {
      if (!importer || !normal(importer).startsWith(normal(path.join(upstream, 'src')))) return;
      const resolved = path.resolve(path.dirname(importer), source);
      const relative = path.relative(path.join(upstream, 'src'), resolved).replaceAll('\\', '/');
      if (substitutes.has(relative)) return path.join(adapter, 'WindowsPanels.tsx');
    },
    transform(code, id) {
      if (normal(id) === normal(path.join(upstream, 'src', 'App.tsx'))) {
        const brand = '<img src="/orbit.svg" alt="" />\n          Orbit Store\n          <span className="beta-label">{t("Beta")}</span>';
        if (!code.includes(brand)) throw new Error('Orbit Store branding anchor changed');
        code = code.replace(brand, '<OrbitXLogo />').replace('aria-label={t("Orbit Store home")}', 'aria-label="OrbitX home"');
        const graphicalSettings = 'openSettings={paired(() => setSettingsPage("appearance"))}';
        const storeSettings = 'paired(() => setAutoOpen(true))';
        if (!code.includes(graphicalSettings) || !code.includes(storeSettings)) throw new Error('Orbit primary Settings route changed');
        const openSettings = '() => { setGameId(null); setSettingsPage("home"); }';
        code = code.replace(graphicalSettings, 'openSettings={' + openSettings + '}').replaceAll(storeSettings, openSettings);
        const restoreSettingsFocus = '    if (restoreSelection) onReturn?.();';
        if (!code.includes(restoreSettingsFocus)) throw new Error('Orbit Settings focus contract changed');
        code = code.replace(restoreSettingsFocus, '    if (restoreSelection) { if (onReturn) onReturn(); else requestAnimationFrame(() => document.querySelector<HTMLElement>(".main-nav [aria-current=page]")?.focus({ preventScroll: true })); }');
        for (const anchor of ['["Discover", "Browse", "Library", "Downloads"] as const', 'className={tab === name ? "active" : ""}', 'aria-current={tab === name ? "page" : undefined}', 'onClick={() => go(name)}', '{sourceSetupNode ? ('])
          if (!code.includes(anchor)) throw new Error('Orbit Store Settings page anchor changed: ' + anchor);
        code = code.replace('["Discover", "Browse", "Library", "Downloads"] as const', '["Discover", "Browse", "Library", "Downloads", "Settings"] as const')
          .replace('className={tab === name ? "active" : ""}', 'className={(settingsPage ? "Settings" : tab) === name ? "active" : ""}')
          .replace('aria-current={tab === name ? "page" : undefined}', 'aria-current={(settingsPage ? "Settings" : tab) === name ? "page" : undefined}')
          .replace('onClick={() => go(name)}', 'onClick={() => name === "Settings" ? setSettingsPage("home") : go(name)}')
          .replace('{sourceSetupNode ? (', '{settingsNode ? settingsNode : sourceSetupNode ? (');
        const oldBack = 'else if (tab !== "Discover") {\n      setTab("Discover");\n      requestAnimationFrame(() => {\n        const first = document.querySelector<HTMLElement>(".hero .primary");\n        first?.focus({ preventScroll: true });\n      });\n    }';
        if (!code.includes(oldBack)) throw new Error('Orbit page Back contract changed');
        code = 'import { usePageHistory, dismissWindowsLayer } from "../../windows-adapter/ControllerBoundary";\n' + code.replace('  const back = useCallback(() => {', '  const previousPage = usePageHistory(tab);\n  const back = useCallback(() => {\n    if (dismissWindowsLayer()) return;')
          .replace(oldBack, 'else { const previous = previousPage(); if (previous) { setTab(previous); requestAnimationFrame(() => document.querySelector<HTMLElement>(".main-nav [aria-current=page]")?.focus({ preventScroll: true })); } }');
        code = code.replace('!game && tab !== "Downloads" ? t("Open game") : t("Select")', 'settingsPage ? t("Select") : tab === "Library" ? "More" : !game && tab !== "Downloads" ? t("Open game") : t("Select")')
          .replace('{!game && tab === "Browse" && (', '{!game && !settingsPage && tab === "Browse" && (')
          .replace('{game && (\n            <button onClick={back}>', '{(game || settingsPage) && (\n            <button onClick={back}>');
        const filterAnchor = '          label={t("Platform")}';
        if (!code.includes(filterAnchor)) throw new Error('Orbit compatibility filter anchor changed');
        code = code.replace('        <Select\n          inlineLabel={iface !== "store"}\n' + filterAnchor,
          '        <EmulatorFilter inlineLabel={iface !== "store"} value={filters.emulator} onChange={value => { filter("emulator", value); filter("compatibility", ""); }} />\n        <CompatibilityFilter inlineLabel={iface !== "store"} emulator={filters.emulator} value={filters.compatibility} onChange={value => filter("compatibility", value)} />\n        <Select\n          inlineLabel={iface !== "store"}\n' + filterAnchor)
          .replaceAll('filters.platform ||', 'filters.emulator !== defaultFilters.emulator || filters.compatibility || filters.platform ||');
        const browseTileAnchor = '{visibleGames.map((g, index) => (\n                    <GameTile\n                      key={g.id}\n                      game={g}';
        if (!code.includes(browseTileAnchor)) throw new Error('Orbit Browse compatibility badge anchor changed');
        code = code.replace(browseTileAnchor, browseTileAnchor + '\n                      compatibilityEmulator={filters.emulator}');
        if (!code.includes('<KeepInterface />') || !code.includes('const detailsNode = game ?')) throw new Error('Orbit appearance mount changed');
        code = code.replace('const detailsNode = game ?', 'useEffect(() => { const show = () => { setGameId(null); setSourcesOpen(false); setStorageOpen(false); setPairOpen(false); setSettingsPage("appearance"); }; window.addEventListener("windows-appearance", show); return () => window.removeEventListener("windows-appearance", show); }, []);\n  const detailsNode = game ?');
        code = code.replace('const detailsNode = game ?', 'useEffect(() => { const route = event => { closeSettings(false); setSourcesOpen(false); setStorageOpen(false); setPairOpen(false); if (event.detail === "Settings") { setGameId(null); setSettingsPage("home"); } else go(event.detail); }; window.addEventListener("windows-page", route); return () => window.removeEventListener("windows-page", route); }, [closeSettings]);\n  const detailsNode = game ?');
        code = code.replace('const detailsNode = game ?', 'useEffect(() => { const show = event => { const target = orbit.games.find(g => g.id === event.detail.id); closeSettings(false); setSourcesOpen(false); setStorageOpen(false); setPairOpen(false); go("Browse"); if (target) open(target); else { setFilters({ ...defaultFilters, query: event.detail.query }); requestAnimationFrame(() => search.current?.focus()); } }; window.addEventListener("windows-game", show); return () => window.removeEventListener("windows-game", show); }, [orbit.games, closeSettings, open]);\n  const detailsNode = game ?');
        return 'import { OrbitXLogo, StartupSplash } from "../../windows-adapter/Branding";\nimport { CompatibilityFilter, EmulatorFilter } from "../../windows-adapter/Compatibility";\nimport { WindowsReleaseActions, WindowsDownloadToast } from "../../windows-adapter/WindowsManagement";\nimport { LaunchOverlay } from "../../windows-adapter/LaunchOverlay";\n' + code.replace('<KeepInterface />', '<StartupSplash ready={orbit.ready} error={orbit.error} system={orbit.system} /><WindowsReleaseActions /><WindowsDownloadToast /><LaunchOverlay /><KeepInterface />');
      }
      if (normal(id) === normal(path.join(upstream, 'src', 'browse.ts'))) {
        for (const anchor of ['  platform: string;', '  platform: "",', '      if (filters.platform &&'])
          if (!code.includes(anchor)) throw new Error('Orbit compatibility browse anchor changed: ' + anchor);
        return 'import { matchesCompatibilityFilter } from "../../windows-adapter/Compatibility";\n' + code
          .replace('  platform: string;', '  compatibility?: string;\n  emulator: "any" | "kyty" | "sharpemu";\n  platform: string;')
          .replace('  platform: "",', '  compatibility: "",\n  emulator: "any",\n  platform: "",')
          .replace('  const matches = games.flatMap((game) => {', '  const matches = games.flatMap((game) => {\n    if (filters.compatibility && !matchesCompatibilityFilter(game, filters.compatibility, filters.emulator)) return [];');
      }
      if (normal(id) === normal(path.join(upstream, 'src', 'components.tsx'))) {
        const anchor = '        />\n      </span>\n      <span className="tile-title">{game.title}</span>';
        if (!code.includes(anchor)) throw new Error('Orbit compatibility tile anchor changed');
        return 'import { CompatibilityDot, compatibilityLabel } from "../../windows-adapter/Compatibility";\n' + code.replace(anchor, '        />\n        <CompatibilityDot game={game} emulator={compatibilityEmulator} />\n      </span>\n      <span className="tile-title">{game.title}</span>')
          .replace('  status,\n}: {', '  status,\n  compatibilityEmulator = "any",\n}: {')
          .replace('  status?: string;\n}) {', '  status?: string;\n  compatibilityEmulator?: "any" | "kyty" | "sharpemu";\n}) {')
          .replace('aria-label={`${t("View {title}", { title: game.title })}', 'aria-label={`${t("View {title}", { title: game.title })}, ${compatibilityEmulator === "any" ? "KytyPS5 and SharpEmu" : compatibilityEmulator === "sharpemu" ? "SharpEmu" : "KytyPS5"} compatibility: ${compatibilityLabel(game, compatibilityEmulator)}');
      }
      if (normal(id) === normal(path.join(upstream, 'src', 'interfaces', 'CinemaParts.tsx'))) {
        const anchor = '            {f.badge &&';
        if (!code.includes(anchor) || !code.includes('  badge?: string;')) throw new Error('Orbit Cinema compatibility anchor changed');
        return code.replace('  badge?: string;', '  compatibility?: ReactNode;\n  badge?: string;')
          .replaceAll(anchor, '            {f.compatibility}\n' + anchor);
      }
      if (normal(id) === normal(path.join(upstream, 'src', 'controller.ts'))) {
        if (!code.includes('behavior: "smooth",')) throw new Error('Orbit controller scroll changed');
        for (const anchor of ['function confirmKey(e: KeyboardEvent) {', 'function cursorConfirm(e: MouseEvent | PointerEvent) {'])
          if (!code.includes(anchor)) throw new Error('Orbit hold control boundary changed');
        for (const anchor of ['    function tick(now: number) {\n      const pad = navigator.getGamepads?.()[0];', '    function blur() {', '    function goBack() {', '    frame = requestAnimationFrame(tick);\n    return () => {'])
          if (!code.includes(anchor)) throw new Error('Orbit controller lifecycle contract changed: ' + anchor);
        code = code.replace('    let frame = 0,', '    const input = new FocusedControllerInput();\n    let heldDelete: HTMLElement | null = null;\n    function releaseDelete() { heldDelete?.dispatchEvent(new KeyboardEvent("keyup", { key: "Enter", bubbles: true })); heldDelete = null; }\n    let frame = 0,')
          .replace("'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), [tabindex=\"0\"]'", "'button:not([disabled]), summary, a[href], input:not([disabled]), select:not([disabled]), [tabindex=\"0\"]'")
          .replace('    function shortcut(command: string) {', '    function shortcut(command: string) {\n      if ((command === "details" || command === "search") && !document.querySelector("[role=dialog], [role=alertdialog]")) { const preview = document.querySelector<HTMLElement>(".library-preview"); const action = preview?.querySelector<HTMLButtonElement>(command === "details" ? ".library-preview-control:not(.library-preview-menu)" : ".library-preview-menu"); if (action && !action.disabled) { action.click(); return; } const card = document.activeElement?.closest<HTMLElement>(".windows-library-card"); const fallback = command === "details" ? null : card?.querySelector<HTMLButtonElement>(".library-more-button"); if (fallback) { fallback.click(); return; } }')
          .replace('    function blur() {', '    function blur() {\n      releaseDelete(); input.reset(); held = ""; last = 0; previousFrame = 0;')
          .replace('    function tick(now: number) {\n      const pad = navigator.getGamepads?.()[0];', '    function tick(now: number) {\n      const pad = navigator.getGamepads?.()[0];\n      if (!input.ready(pad)) { releaseDelete(); held = ""; last = 0; previousFrame = 0; frame = requestAnimationFrame(tick); return; }\n      if (!pad?.buttons[0]?.pressed) releaseDelete();')
          .replace('              confirm();\n            }\n          } else if (command === "back")', '              const target = document.activeElement as HTMLElement | null;\n              if (target?.matches("[data-hold-delete]:not(:disabled)")) { heldDelete = target; sendKey(target, "Enter"); } else confirm();\n            }\n          } else if (command === "back")')
          .replace('    function goBack() {', '    function goBack() {\n      if (document.hidden || !document.hasFocus()) return;')
          .replace('        move(e.key);', '        if (!railKey(e.key)) move(e.key);')
          .replace('    frame = requestAnimationFrame(tick);\n    return () => {', '    const contextBack = (event: MouseEvent) => { event.preventDefault(); goBack(); };\n    document.addEventListener("contextmenu", contextBack);\n    document.addEventListener("visibilitychange", blur);\n    frame = requestAnimationFrame(tick);\n    return () => {\n      releaseDelete();\n      document.removeEventListener("contextmenu", contextBack);\n      document.removeEventListener("visibilitychange", blur);');
        return 'import { FocusedControllerInput } from "../../windows-adapter/ControllerBoundary";\n' + code.replaceAll('behavior: "smooth",', 'behavior: "auto",')
          .replace('function key(e: KeyboardEvent) {', 'function key(e: KeyboardEvent) {\n      if (document.hidden || !document.hasFocus()) return;')
          .replace('function confirmKey(e: KeyboardEvent) {', 'function confirmKey(e: KeyboardEvent) {\n      if (document.hidden || !document.hasFocus()) return;')
          .replace('function confirmKey(e: KeyboardEvent) {', 'function confirmKey(e: KeyboardEvent) {\n      if (e.key === "Enter" && (e.target as Element)?.closest?.("[data-hold-delete]")) return;')
          .replace('function cursorConfirm(e: MouseEvent | PointerEvent) {', 'function cursorConfirm(e: MouseEvent | PointerEvent) {\n      if (document.hidden || !document.hasFocus()) return;\n      if ((e.target as Element)?.closest?.("[data-hold-delete]")) return;');
      }
      // Fail on changed upstream anchors instead of silently enabling unreviewed actions.
      if (normal(id) === normal(path.join(upstream, 'src', 'Details.tsx'))) {
        const factsCondition = '{(game.publisher || game.releaseDate) && (';
        const releaseDateFact = '          {game.releaseDate && (\n            <div>\n              <dt>{t("Release date")}</dt>\n              <dd>\n                {formatDate(new Date(game.releaseDate), {\n                  dateStyle: "medium",\n                  timeZone: "UTC",\n                })}\n              </dd>\n            </div>\n          )}';
        if (!code.includes(factsCondition) || !code.includes(releaseDateFact)) throw new Error('Orbit game compatibility fact anchor changed');
        code = code.replace(factsCondition, '{(game.publisher || game.releaseDate || game.releases.length > 0) && (')
          .replace(releaseDateFact, releaseDateFact + '\n          <div>\n            <dt>{t("Compatibility")}</dt>\n            <dd><CompatibilityColumns release={release} /></dd>\n          </div>');
        const storageButton = /          \{system\?\.paired && \(\n            <button\n              className="sheet-setting"\n              data-setting="storage"[\s\S]*?\n          \)\}/;
        const panoramaStorage = /          \{showDownload && system\?\.paired && \(\n            <PnGroup title=\{t\("Save to"\)\}>[\s\S]*?\n          \)\}\n/;
        const cinemaStorage = /                  \{showDownload && system\?\.paired && \(\n                    <>\n                      <p className="cn-section-label">\{t\("Save to"\)\}<\/p>[\s\S]*?\n                  \)\}/;
        const crossbarStorage = /      \.\.\.\(showDownload && system\?\.paired\n        \? \[valueRow\("save"[\s\S]*?        : \[\]\),\n/;
        for (const pattern of [storageButton, panoramaStorage, cinemaStorage, crossbarStorage])
          if (!pattern.test(code)) throw new Error('Orbit storage presentation changed: ' + pattern);
        code = code.replace(storageButton, '          <WindowsSaveTo />')
          .replace(panoramaStorage, '').replace(cinemaStorage, '                  <WindowsSaveTo />').replace(crossbarStorage, '');
        const panoramaEnd = '            {facts}\n          </section>\n        </div>';
        const crossbarEnd = '    ];\n    const menuItems =';
        if (!code.includes(panoramaEnd) || !code.includes(crossbarEnd)) throw new Error('Orbit storage order changed');
        code = code.replace(panoramaEnd, '            {facts}\n          </section>\n          <WindowsSaveTo className="pn-group pn-text-group windows-save-to-group" />\n        </div>')
          .replace(crossbarEnd, '      { key: "save", disabled: true, render: () => <WindowsSaveTo /> },\n' + crossbarEnd);
        for (const [from, to] of [
          ['"Download to PS5"', '"Download"'],
          ['"Saves a file to your console. Installation and launching are separate."', '"Saves to your Windows Downloads folder. Installation and launching are separate."'],
          ['<span>{actionLabel}</span>', '<span>{!downloadAgain && collection?.job ? actionLabel : t("View releases")}</span>'],
          ['{firstAction}', '{!downloadAgain && collection?.job ? actionLabel : t("View releases")}'],
          ['const preferred =', 'useEffect(() => { const close = () => { setSheet(null); setXbMenu(null); setShowAbout(false); }; window.addEventListener("windows-appearance", close); return () => window.removeEventListener("windows-appearance", close); }, []);\n  const preferred ='],
          ['function primaryAction(review = false) {', 'function primaryAction(review = false) {\n    if (!downloadAgain && collection?.job) { openQueue(collection.job.id); return; }\n    if (system?.platform === "desktop" && !review) { setSheet(null); setXbMenu(null); setShowAbout(false); openRelease(release.id); return; }'],
          ['const primaryDisabled =\n    system?.paired && showDownload', 'const primaryDisabled =\n    system?.platform === "desktop" ? pending || ((downloadAgain || !collection?.job) && !canOpenRelease(release)) : system?.paired && showDownload'],
          ['system?.paired && showDownload\n                    ? downloadUnavailable || (useBrowser && !browser?.available)\n                    : pending', 'primaryDisabled'],
        ]) {
          if (!code.includes(from)) throw new Error('Orbit Details contract changed: ' + from);
          code = code.replaceAll(from, to);
        }
        const downloadUsingGroup = /          \{showDownload && \(\n            <PnGroup title=\{t\("Download using"\)\}>[\s\S]*?            <\/PnGroup>\n          \)\}\n/;
        if (!downloadUsingGroup.test(code)) throw new Error('Orbit Panorama Download using group changed');
        code = code.replace(downloadUsingGroup, '          <MediaGallery key={game.id} gameTitle={game.title} />\n');
        const versionsGroup = '<PnGroup title={t("Versions")}';
        if (!code.includes(versionsGroup)) throw new Error('Orbit Panorama Versions group changed');
        code = code.replace(versionsGroup, '<PnGroup title={t("Versions")} className="versions-group"');
        return 'import { CompatibilityColumns } from "../../windows-adapter/Compatibility";\nimport { WindowsSaveTo } from "../../windows-adapter/WindowsManagement";\nimport { openRelease, canOpenRelease } from "../../windows-adapter/bridge";\nimport { MediaGallery } from "../../windows-adapter/MediaGallery";\n' + code;
      }
      if (normal(id) === normal(path.join(upstream, 'src', 'interfaces', 'Shell.tsx'))) {
        const wordmark = '<img src="/orbit.svg" alt="" />\n          Orbit';
        if (code.split(wordmark).length !== 3) throw new Error('Orbit shell wordmarks changed');
        code = 'import { OrbitXLogo } from "../../../windows-adapter/Branding";\n' + code.replaceAll(wordmark, '<OrbitXLogo />');
        code = code.replace('<span className="xb-row-art">', '<span className="xb-row-art"><CompatibilityDot game={game} />');
        code = code.replace('      badge: state?.label ?', '      compatibility: <CompatibilityDot game={g} />,\n      badge: state?.label ?');
        for (const [from, to] of [
          ['size ? `${t("Download")} · ${bytes(size)}` : t("Download")', 't("View game")'],
          ['view = true,', 'view = false,'],
          ['onClick={() => props.open(game)}>\n          <Glyph name="down"', 'onClick={() => props.open(game)}>\n          <Glyph name="eye"'],
        ]) {
          if (!code.includes(from)) throw new Error('Orbit shell contract changed: ' + from);
          code = code.replaceAll(from, to);
        }
        return 'import { CompatibilityDot } from "../../../windows-adapter/Compatibility";\n' + code;
      }
      if (normal(id) === normal(path.join(upstream, 'src', 'types.ts'))) {
        const anchor = 'export const bytes = (n: number) => {';
        if (!code.includes(anchor)) throw new Error('Orbit size formatter contract changed');
        return code.replace(anchor, anchor + '\n  if (!Number.isFinite(n) || n < 0) return "Size unknown";');
      }
    },
  }, react()],
  resolve: { dedupe: ['react', 'react-dom'], alias: { react: path.join(upstream, 'node_modules/react'), 'react-dom': path.join(upstream, 'node_modules/react-dom') } },
  build: { outDir: fileURLToPath(new URL('../build/', import.meta.url)), emptyOutDir: true, target: 'es2020', sourcemap: false },
});
