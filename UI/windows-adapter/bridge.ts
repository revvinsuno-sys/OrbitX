type NativeCommand = { version: 1; command: 'navigate'; page: 'Settings' | 'Downloads' | 'My Library' | 'Classic Windows' } | { version: 1; command: 'fullscreen' | 'exitFullscreen' | 'toolbar' | 'ready' | 'error' };
declare global {
  interface Window { chrome?: { webview?: { postMessage: (message: NativeCommand) => void; addEventListener: (type: string, action: (event: MessageEvent) => void) => void } } }
}
export function navigate(page: Extract<NativeCommand, { command: 'navigate' }>['page']) {
  window.chrome?.webview?.postMessage({ version: 1, command: 'navigate', page });
}
export function openRelease(releaseId: string) {
  window.dispatchEvent(new CustomEvent('windows-release', { detail: releaseId }));
}
export function canOpenRelease(release: { id: string }) {
  return (release as { windowsAvailable?: boolean }).windowsAvailable === true;
}
function showAppearance() {
  if (!document.querySelector('[role="alertdialog"]')) window.dispatchEvent(new Event('windows-appearance'));
}
const directions: Record<string, string> = { w: 'ArrowUp', a: 'ArrowLeft', s: 'ArrowDown', d: 'ArrowRight' };
function editing(target: EventTarget | null) {
  return target instanceof HTMLElement && !!target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])');
}
let fullscreen = false;
document.addEventListener('wheel', event => {
  if (event.ctrlKey || event.metaKey || event.defaultPrevented || Math.abs(event.deltaX) >= Math.abs(event.deltaY) || editing(event.target)) return;
  for (let element = event.target instanceof Element ? event.target : null; element; element = element.parentElement) {
    const style = getComputedStyle(element);
    // A nested vertical scroller (dialogs, text panels) keeps its normal wheel behavior.
    if (element.scrollHeight > element.clientHeight + 1 && /auto|scroll/.test(style.overflowY)) return;
    if (element.scrollWidth > element.clientWidth + 1 && (/auto|scroll/.test(style.overflowX) || element.getAttribute('data-scroll') === 'x')) {
      event.preventDefault(); event.stopPropagation();
      const scale = event.deltaMode === 1 ? 32 : event.deltaMode === 2 ? element.clientWidth : 1;
      element.scrollLeft += event.deltaY * scale;
      return;
    }
  }
}, { passive: false, capture: true });
const ready = new MutationObserver(() => {
  if (document.querySelector('#root .app')) {
    ready.disconnect(); window.chrome?.webview?.postMessage({ version: 1, command: 'ready' });
  }
});
ready.observe(document.documentElement, { childList: true, subtree: true });
window.addEventListener('error', () => window.chrome?.webview?.postMessage({ version: 1, command: 'error' }));
window.chrome?.webview?.addEventListener('message', event => {
  if (typeof event.data?.fullscreen === 'boolean') fullscreen = event.data.fullscreen;
  if (event.data?.command === 'appearance') showAppearance();
  if (event.data?.command === 'toast' && typeof event.data.message === 'string' && event.data.message.length <= 1024)
    window.dispatchEvent(new CustomEvent('windows-toast', { detail: event.data.message }));
  if (event.data?.command === 'game' && (event.data.id === null || typeof event.data.id === 'string' && /^win-game-[a-f0-9]+$/.test(event.data.id)) &&
      typeof event.data.query === 'string' && event.data.query.length <= 1024)
    window.dispatchEvent(new CustomEvent('windows-game', { detail: { id: event.data.id, query: event.data.query } }));
  if (event.data?.command === 'page' && ['Discover', 'Browse', 'Downloads', 'Library', 'Settings'].includes(event.data.page))
    window.dispatchEvent(new CustomEvent('windows-page', { detail: event.data.page }));
  if (event.data?.command === 'settingsSourcePicker' && typeof event.data.file === 'string' && typeof event.data.key === 'string')
    window.dispatchEvent(new CustomEvent('windows-settings-source-picker', { detail: { file: event.data.file, key: event.data.key } }));
  if (event.data?.command === 'launching' && typeof event.data.game === 'string' && event.data.game.length <= 500 &&
      ['KytyPS5', 'SharpEmu'].includes(event.data.emulator))
    window.dispatchEvent(new CustomEvent('windows-game-launch', { detail: { game: event.data.game, emulator: event.data.emulator } }));
  if (event.data?.command === 'gameExited' && ['KytyPS5', 'SharpEmu'].includes(event.data.emulator))
    window.dispatchEvent(new CustomEvent('windows-game-exit', { detail: { emulator: event.data.emulator } }));
});
window.addEventListener('keydown', event => {
  if (event.key === 'F6' && fullscreen) {
    event.preventDefault(); event.stopImmediatePropagation();
    window.chrome?.webview?.postMessage({ version: 1, command: 'toolbar' }); return;
  }
  if (event.key === 'F2' && !event.altKey && !event.ctrlKey && !event.metaKey) {
    event.preventDefault(); event.stopImmediatePropagation(); showAppearance(); return;
  }
  if (!event.isComposing && !event.altKey && !event.ctrlKey && !event.metaKey && !editing(event.target)) {
    const direction = directions[event.key.toLowerCase()];
    if (direction) {
      event.preventDefault(); event.stopImmediatePropagation();
      (document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent('keydown', { key: direction, bubbles: true, cancelable: true, repeat: event.repeat }));
      return;
    }
    if (event.key.startsWith('Arrow') && document.activeElement === document.body && !document.querySelector('[role="dialog"], [role="alertdialog"]')) {
      const first = document.querySelector<HTMLElement>('.game-tile[aria-current="true"], .game-tile, .xb-row-main, .cn-frame, .hub-primary, .pn-tile-hit:not(:disabled)');
      if (first) { first.focus({ preventScroll: true }); first.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'auto' }); }
    }
  }
  if (event.key === 'F11') {
    event.preventDefault(); event.stopImmediatePropagation();
    window.chrome?.webview?.postMessage({ version: 1, command: 'fullscreen' });
  }
}, true);
