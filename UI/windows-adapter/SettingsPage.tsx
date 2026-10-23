import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction, type ReactNode } from 'react';
import { api } from '../orbit-upstream/src/api';
import { Appearance } from '../orbit-upstream/src/interfaces/Appearance';
import { LanguageSetting } from '../orbit-upstream/src/LanguageSetting';
import { Select } from '../orbit-upstream/src/Select';
import { WindowsSettingsControls } from './WindowsManagement';
import racing from '../../PS5CatalogBrowser.App/Assets/Settings/racing-background.png';
import { SettingsArtwork } from './SettingsArtwork';
import './settings.css';
import { OrbitXLogo } from './Branding';
import { interfaces, useInterfacePrefs } from '../orbit-upstream/src/interfaces/choice';
import { language, languages } from '../orbit-upstream/src/i18n';

export type SettingsSection = string;
type Source = { id: string; name: string; type: number; url: string; file: string; key: string; enabled: boolean; autoRefresh: boolean; fallback: string; format?: string; gameCount?: number; releaseCount?: number; lastError?: string; lastSuccessfulRefresh?: string };
type Settings = { fields: Record<string, string | number | boolean>; facts: Record<string, string>; status: string; actions: string[]; liveSourceUrl: string; passwords: { index: number }[]; sources: Source[]; keys: { description: string; path: string }[]; presentModes: string[]; shaderOptimizations: string[] };
const categories = [
  { id: 'sources', label: 'Catalogue sources' },
  { id: 'downloads', label: 'Downloads' },
  { id: 'passwords', label: 'Archive passwords' },
  { id: 'browser', label: 'Embedded browser' },
  { id: 'installation', label: 'Image installation' },
  { id: 'emulators', label: 'Emulators' },
  { id: 'appearance', label: 'Appearance' },
  { id: 'language', label: 'Language' },
  { id: 'metadata', label: 'Metadata' },
  { id: 'about', label: 'About OrbitX' },
];
const emptySource: Source = { id: '', name: '', type: 0, url: '', file: '', key: '', enabled: true, autoRefresh: true, fallback: '' };

function useSettings() {
  const [state, setState] = useState<Settings | null>(null), [error, setError] = useState('');
  const queue = useRef(Promise.resolve()), generation = useRef(0), alive = useRef(true);
  const refresh = useCallback(async () => {
    const version = generation.current;
    try { const next = await api<Settings>('/windows/settings'); if (alive.current && version === generation.current) setState(next); }
    catch (e) { if (alive.current) setError((e as Error).message); }
  }, []);
  useEffect(() => {
    alive.current = true; let polling = false;
    const poll = async () => { if (polling || document.hidden) return; polling = true; try { await refresh(); } finally { polling = false; } };
    void poll(); const timer = setInterval(() => void poll(), 1000);
    return () => { alive.current = false; clearInterval(timer); };
  }, [refresh]);
  const request = useCallback((body: unknown) => {
    generation.current++;
    const next = queue.current.then(async () => {
      try {
        const result = await api<Settings | { accepted: boolean }>('/windows/settings', body);
        generation.current++;
        if (alive.current) { if ('fields' in result) setState(result); setError(''); }
        await refresh();
      } catch (e) { if (alive.current) setError((e as Error).message); throw e; }
    });
    queue.current = next.catch(() => {});
    return next;
  }, [refresh]);
  return { state, error, request };
}
type Request = ReturnType<typeof useSettings>['request'];
function Section({ title, children }: { title: string; children: ReactNode }) {
  return <section className="orbit-settings-panel"><h2>{title}</h2>{children}</section>;
}
function Field({ name, label, state, request, disabled = false, immediate = false }: { name: string; label: string; state: Settings; request: Request; disabled?: boolean; immediate?: boolean }) {
  const value = String(state.fields[name] ?? ''), [text, setText] = useState(value), focused = useRef(false), dirty = useRef(false);
  useEffect(() => { if (!focused.current) setText(value); }, [value]);
  function save(next: string) { dirty.current = false; void request({ kind: 'field', name, value: next }).catch(() => {}); }
  return <label className="settings-field">{label}<input data-setting={name} value={text} disabled={disabled} onFocus={() => { focused.current = true; }}
    onChange={e => { setText(e.target.value); dirty.current = true; if (immediate) save(e.target.value); }}
    onBlur={() => { focused.current = false; if (dirty.current) save(text); }} /></label>;
}
function Toggle({ name, label, state, request, disabled = false }: { name: string; label: string; state: Settings; request: Request; disabled?: boolean }) {
  return <label className="settings-toggle"><input type="checkbox" data-setting={name} checked={!!state.fields[name]} disabled={disabled}
    onChange={e => void request({ kind: 'field', name, value: e.target.checked }).catch(() => {})} />{label}</label>;
}
function Facts({ state, items }: { state: Settings; items: [string, string][] }) {
  return <dl className="settings-facts">{items.map(([key, label]) => <div key={key}><dt>{label}</dt><dd>{state.facts[key] || 'Not recorded'}</dd></div>)}</dl>;
}
function Status({ children }: { children: ReactNode }) { return <p className="settings-status"><span aria-hidden="true">ⓘ</span> {children}</p>; }

function Passwords({ state, request }: { state: Settings; request: Request }) {
  const [index, setIndex] = useState<number | null>(null), [password, setPassword] = useState(''), [pending, setPending] = useState(false);
  async function save(operation: string, row: number) {
    setPending(true);
    try { await request({ kind: 'password', operation, index: row, count: state.passwords.length, value: password }); setPassword(''); setIndex(null); }
    catch { /* The page owns the accessible error. Keep the pending edit for correction. */ }
    finally { setPending(false); }
  }
  return <Section title="Archive passwords"><p>Passwords are tried in order after any password supplied by the selected release. Case and spaces are preserved.</p>
    <ol className="settings-passwords">{state.passwords.map(row => <li key={row.index}><span>Password {row.index + 1}</span><span aria-label="Saved password, masked">••••••••</span>
      <button disabled={pending} onClick={() => { setPassword(''); setIndex(row.index); }}>Edit password {row.index + 1}</button>
      <button disabled={pending} onClick={() => void save('remove', row.index)}>Remove password {row.index + 1}</button></li>)}</ol>
    {!state.passwords.length && <p>No saved passwords.</p>}
    <form onSubmit={e => { e.preventDefault(); void save(index === null ? 'add' : 'edit', index ?? state.passwords.length); }}>
      <label className="settings-field">{index === null ? 'New password' : `Replacement for password ${index + 1}`}<input type="password" autoComplete="new-password" maxLength={1024} required value={password} onChange={e => setPassword(e.target.value)} /></label>
      <div className="settings-actions"><button className="primary" disabled={pending}>{index === null ? 'Add password' : 'Save replacement'}</button>
      {index !== null && <button type="button" disabled={pending} onClick={() => { setIndex(null); setPassword(''); }}>Cancel edit</button>}</div>
    </form><p>Up to 64 passwords, 1024 characters each. Existing passwords stay masked and are never sent to this page. Changes use the same local settings store.</p>
  </Section>;
}

function Sources({ state, request, draft, setDraft }: { state: Settings; request: Request; draft: Source; setDraft: Dispatch<SetStateAction<Source>> }) {
  function text(key: 'name' | 'url' | 'file' | 'key', label: string) {
    return <label className="settings-field">{label}<input data-source={key} value={draft[key]} maxLength={4096} onChange={e => setDraft({ ...draft, [key]: e.target.value })} /></label>;
  }
  function action(action: string, label: string, selected = false) {
    return <button type="button" data-settings-action={action} disabled={!state.actions.includes(action) || selected && !draft.id}
      onClick={() => void request({ kind: 'action', action, source: draft }).catch(() => {})}>{label}</button>;
  }
  return <><Section title="Catalogue sources"><p>Manage the connections OrbitX uses to discover content.</p>
    <div className="settings-source-list">{state.sources.map(source => <button key={source.id} disabled={!state.actions.includes('sourceAdd')} aria-pressed={draft.id === source.id} onClick={() => setDraft(source)}>
      <strong>{source.name}</strong><span>{source.enabled ? 'Enabled' : 'Disabled'} · {source.format || ['HTTP JSON', 'Local encrypted catalogue', 'Live catalogue'][source.type]}</span>
      <span title={source.file || source.url}>{source.file || source.url}</span><span>{source.gameCount} games · {source.releaseCount} releases</span>{source.lastError && <span role="status">{source.lastError}</span>}
    </button>)}</div>{!state.sources.length && <p>No catalogue sources.</p>}
    <div className="settings-actions"><button disabled={!state.actions.includes('sourceAdd')} onClick={() => setDraft({ ...emptySource })}>New source</button>
    <button disabled={!state.actions.includes('refreshCatalogues')} onClick={() => void request({ kind: 'action', action: 'refreshCatalogues' }).catch(() => {})}>Refresh all sources</button></div></Section>
    <Section title="Source connection"><fieldset disabled={!state.actions.includes('sourceAdd')}>
      {text('name', 'Friendly name')}
      <div className="settings-field"><Select label="Source type" value={String(draft.type)} onChange={value => { const type = Number(value); setDraft({ ...draft, type, ...(type === 2 && draft.type !== 2 ? { name: 'Orbit Store', url: state.liveSourceUrl, autoRefresh: true } : {}) }); }} options={[
        { value: '0', label: 'HTTP — JSON catalogue' }, { value: '1', label: 'Orbit Store — Local encrypted catalogue' }, { value: '2', label: 'Orbit Store — Live catalogue' }
      ]} /></div>
      {draft.type !== 1 && text('url', 'Catalogue HTTPS URL')}
      {draft.type === 1 && <>{text('file', 'Local encrypted catalogue (.enc)')}{action('sourceFile', 'Choose catalogue file')}</>}
      {draft.type !== 0 && <><div className="settings-field"><Select label="Available decoding keys" value={draft.key} onChange={value => setDraft({ ...draft, key: value })} options={[{ value: '', label: 'Select a managed key' }, ...state.keys.map(key => ({ value: key.path, label: key.description }))]} /></div>
        {text('key', 'Key file (managed or manually supplied)')}<div className="settings-actions">{action('sourceKey', 'Import key file')}{action('sourceDownloadKey', 'Download official Orbit key')}</div>
        <p>Downloads the official source bundle, verifies it and keeps only the decoding key. An existing key selection is preserved.</p></>}
      {draft.type === 2 && <><div className="settings-field"><Select label="Local fallback for this same feed" value={draft.fallback} onChange={value => setDraft({ ...draft, fallback: value })} options={[{ value: '', label: 'None' }, ...state.sources.filter(source => source.type === 1).map(source => ({ value: source.id, label: source.name }))]} /></div>
        <label className="settings-toggle"><input type="checkbox" checked={draft.autoRefresh} onChange={e => setDraft({ ...draft, autoRefresh: e.target.checked })} />Refresh automatically on startup and every six hours</label></>}
      <label className="settings-toggle"><input type="checkbox" checked={draft.enabled} onChange={e => setDraft({ ...draft, enabled: e.target.checked })} />Enabled</label>
      <div className="settings-actions">{action('sourceAdd', 'Add source')}{action('sourceSave', 'Save selected', true)}{action('sourceRemove', 'Remove selected', true)}{action('sourceTest', 'Test connection', true)}{action('sourceRefresh', 'Refresh selected', true)}</div>
    </fieldset>{state.actions.includes('sourceCancelKey') && action('sourceCancelKey', 'Cancel key download')}</Section></>;
}

export function SettingsPage({ section, choose }: { section: SettingsSection; choose: (section: SettingsSection) => void; [key: string]: unknown }) {
  const { state, error, request } = useSettings();
  const { prefs } = useInterfacePrefs();
  const [sourceDraft, setSourceDraft] = useState<Source>(emptySource);
  useEffect(() => {
    const picker = (event: Event) => setSourceDraft(old => ({ ...old, ...(event as CustomEvent).detail }));
    window.addEventListener('windows-settings-source-picker', picker);
    return () => window.removeEventListener('windows-settings-source-picker', picker);
  }, []);
  const current = categories.find(category => category.id === section), selected = current?.id ?? 'home';
  const container = useRef<HTMLElement>(null);
  useEffect(() => {
    const rail = container.current?.querySelector<HTMLElement>('.settings-tiles');
    if (!rail) return;
    const wheel = (event: WheelEvent) => {
      if (event.ctrlKey || Math.abs(event.deltaX) >= Math.abs(event.deltaY)) return;
      const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? rail.clientWidth : 1;
      if (event.deltaY < 0 ? rail.scrollLeft > 0 : rail.scrollLeft + rail.clientWidth < rail.scrollWidth - 1) {
        rail.scrollLeft += event.deltaY * unit; event.preventDefault();
      }
    };
    rail.addEventListener('wheel', wheel, { passive: false });
    return () => rail.removeEventListener('wheel', wheel);
  }, []);
  useEffect(() => {
    const root = document.querySelector<HTMLElement>('.app'); root?.classList.add('settings-active'); root?.style.setProperty('--settings-background', `url("${racing}")`);
    return () => { root?.classList.remove('settings-active'); root?.style.removeProperty('--settings-background'); };
  }, []);
  useEffect(() => {
    const back = (event: Event) => {
      if (selected === 'home') return;
      event.preventDefault(); (document.activeElement as HTMLElement)?.blur(); choose('home');
      requestAnimationFrame(() => container.current?.querySelector<HTMLElement>(`[data-settings-category="${selected}"]`)?.focus());
    };
    document.addEventListener('windows-back', back);
    return () => document.removeEventListener('windows-back', back);
  }, [selected, choose]);
  function tileStatus(id: string): string {
    if (!state) return 'Loading…';
    switch (id) {
      case 'sources': return `${state.sources.length} ${state.sources.length === 1 ? 'source' : 'sources'}`;
      case 'downloads': return state.fields.DownloadRoot ? 'Folder configured' : 'Not configured';
      case 'passwords': return state.passwords.length ? `${state.passwords.length} saved` : 'Not configured';
      case 'browser': return 'Device profile';
      case 'installation': return state.facts.InstallationAvailability.includes('signatures verified') ? 'OSFMount verified' : state.facts.InstallationAvailability.startsWith('Checking') ? 'Checking…' : 'Needs attention';
      case 'emulators': return [state.facts.EmulatorAvailability, state.facts.SharpEmuAvailability, state.facts.ShadPS4Availability].filter(value => value === 'Executable found').length + ' configured';
      case 'appearance': return interfaces.find(item => item.name === prefs.name)!.title;
      case 'language': return languages.find(item => item.code === language())!.name;
      case 'metadata': return state.facts.MetadataCredentials;
      case 'about': return `Version ${state.facts.AboutBuildVersion.split('+')[0]}`;
      default: return '';
    }
  }
  const action = (id: string, label: string, primary = false) => <button data-settings-action={id} className={primary ? 'primary' : undefined} disabled={!state?.actions.includes(id)} onClick={() => void request({ kind: 'action', action: id }).catch(() => {})}>{label}</button>;
  const field = (name: string, label: string, disabled = false, immediate = false) => <Field name={name} label={label} state={state!} request={request} disabled={disabled} immediate={immediate} />;
  const toggle = (name: string, label: string, disabled = false) => <Toggle name={name} label={label} state={state!} request={request} disabled={disabled} />;
  const facts = (items: [string, string][]) => <Facts state={state!} items={items} />;
  const choices = (name: string, label: string, options: string[], disabled: boolean) => <div className="settings-field"><Select label={label} value={String(state!.fields[name])} disabled={disabled} onChange={value => void request({ kind: 'field', name, value }).catch(() => {})} options={options.map(value => ({ value, label: value }))} /></div>;
  let detail: ReactNode = null;
  if (state) switch (selected) {
    case 'sources': detail = <Sources state={state} request={request} draft={sourceDraft} setDraft={setSourceDraft} />; break;
    case 'downloads': detail = <Section title="Download defaults"><p>Configure where content is downloaded before installation.</p>{field('DownloadRoot', 'Downloads directory', false, true)}<div className="settings-actions">{action('chooseDownload', 'Choose folder')}{action('openDownloads', 'Open downloads folder')}</div>{field('DownloadBandwidthMBps', 'Download bandwidth limit (MB/s)')}<p>One managed network transfer at a time. Default 100 MB/s; 0 means unlimited. Changes apply to the next transfer. External browser transfers and local imports are outside this cap.</p></Section>; break;
    case 'passwords': detail = <Passwords state={state} request={request} />; break;
    case 'browser': detail = <Section title="Embedded browser"><p>The dedicated browser profile stores site sessions under LocalAppData. Clearing site data signs you out of websites. Finish active browser downloads and close their windows first.</p>{action('clearBrowser', 'Clear browser site data')}</Section>; break;
    case 'installation': detail = <Section title="Image installation"><p>Configure image mounting and the installed games directory.</p>{field('OSFMountPath', 'OSFMount executable path')}<Status>{state.facts.InstallationAvailability}</Status><div className="settings-actions">{action('chooseOSF', 'Choose executable')}{action('detectOSF', 'Detect installation')}{action('testOSF', 'Test availability')}{action('getOSF', 'Get OSFMount')}</div><p>Requires OSFMount 3.3.1000 or later. Choose OSFMount.com. Install its driver yourself; each read-only mount requests temporary elevation.</p>{field('InstalledGamesRoot', 'Installed games directory', false, true)}{action('chooseInstalled', 'Choose installed games folder')}{toggle('RetainOriginalImage', 'Retain source archives/images after installation')}<p>Independent of Downloads. Each installation asks you to confirm its folder and deletion option. Complete PS4/PS5 debug or fake PKGs and exFAT images are supported; exFAT requires OSFMount.</p></Section>; break;
    case 'emulators': detail = <><Section title="shadPS4 (PS4)"><p>Experimental PS4 emulation. Compatibility varies by game; provide game dumps and firmware modules from your own console.</p>{field('ShadPS4Path', 'shadPS4 executable path', !state.actions.includes('chooseShadPS4'))}<Status>{state.facts.ShadPS4Availability}</Status><div className="settings-actions">{action('chooseShadPS4', 'Choose executable')}{action('testShadPS4', 'Test availability')}</div>{toggle('ShadPS4Fullscreen', 'Start full screen')}</Section><Section title="SharpEmu">{field('SharpEmuPath', 'Executable path', !state.actions.includes('chooseSharp'))}{action('chooseSharp', 'Choose executable')}<div className="settings-status-row"><Status>{state.facts.SharpEmuAvailability}</Status><Status>{state.facts.SharpEmuOwnership}</Status>{toggle('SharpEmuFullscreen', 'Start full screen')}</div>{facts([['SharpEmuInstalledVersion', 'Installed version'], ['SharpEmuInstalledRevision', 'Upstream commit'], ['SharpEmuAvailableRevision', 'Latest checked upstream main'], ['SharpEmuInstallDirectory', 'Installation directory'], ['SharpEmuCheckTime', 'Last successful check'], ['SharpEmuUpdateTime', 'Last successful update'], ['SharpEmuF10Support', 'F10 menu support']])}<div className="settings-actions">{action('checkSharp', 'Check for updates', true)}{action('updateSharp', 'Update/build managed SharpEmu')}{action('sharpLog', 'Open build log')}</div><p>Check first to select the latest official main revision. Close games before updating. External executable selections are preserved.</p><p role="status">{state.facts.SharpEmuUpdateStatus}</p><details><summary>Build output</summary><pre tabIndex={0} aria-label="SharpEmu build output">{state.facts.SharpEmuUpdateOutput || 'No build output yet.'}</pre></details></Section><Section title="KytyPS5">{field('EmulatorPath', 'KytyPS5 executable path', !state.actions.includes('chooseEmulator'))}<Status>{state.facts.EmulatorAvailability}</Status><div className="settings-actions">{action('chooseEmulator', 'Choose executable')}{action('testEmulator', 'Test availability')}</div>{facts([['InstalledKytyVersion', 'Current installed version'], ['RepositoryKytyVersion', 'Latest official release']])}<div className="settings-actions">{action('checkKyty', 'Check for updates', true)}{action('updateKyty', 'Update KytyPS5')}</div><p role="status">{state.facts.KytyUpdateStatus}</p><p>Downloads release source, applies the custom menu patch, compiles Release and verifies the installed build. Requires configured Windows build and Visual Studio C++ tools. Close KytyPS5 first.</p><details><summary>Update output</summary><pre tabIndex={0} aria-label="KytyPS5 update output">{state.facts.KytyUpdateOutput || 'No update output yet.'}</pre></details><h3>Arguments</h3><p>Edit options, then Apply arguments to save for the next launch.</p>{toggle('ArgumentFullscreen', 'Start full screen')}{toggle('PerformanceArgumentsEnabled', 'Use structured performance settings')}<div className="settings-field-grid">{field('ArgumentWidth', 'Screen width', !state.fields.PerformanceArgumentsEnabled, true)}{field('ArgumentHeight', 'Screen height', !state.fields.PerformanceArgumentsEnabled, true)}{field('ArgumentVblank', 'Vblank frequency', !state.fields.PerformanceArgumentsEnabled, true)}{choices('ArgumentPresentMode', 'Present mode', state.presentModes, !state.fields.PerformanceArgumentsEnabled)}{choices('ArgumentShaderOptimization', 'Shader optimisation', state.shaderOptimizations, !state.fields.PerformanceArgumentsEnabled)}</div>{toggle('ArgumentPerformanceOverlay', 'Performance overlay', !state.fields.PerformanceArgumentsEnabled)}<p>Vblank is a virtual display frequency; 30 does not guarantee a 30 FPS cap. Options require support in the selected build.</p>{toggle('ArgumentAmdCpuPatches', 'AMD CPU instruction patches')}{toggle('ArgumentRedZoneProtection', 'Red Zone protection (Windows)')}{field('ArgumentShaderCacheFolder', 'Pipeline shader cache folder', false, true)}{action('chooseCache', 'Choose cache folder')}<p>Leave empty for _PipelineCache beside the executable. A custom folder requires the maintained patched build. Existing caches are never moved or deleted.</p>{field('AdditionalArguments', 'Additional arguments', false, true)}<p>Quote paths with spaces. The game path is supplied automatically. Managed flags cannot also appear here while structured settings are enabled.</p><h3>Effective options preview</h3><pre aria-label="Effective options preview">{state.facts.ArgumentsPreview}</pre>{state.facts.ArgumentsValidation && <p role="alert">{state.facts.ArgumentsValidation}</p>}<p role="status">{state.facts.ArgumentsSaveState}</p>{action('applyArguments', 'Apply arguments', true)}</Section></>; break;
    case 'appearance': detail = <><Section title="App startup"><div className="settings-field"><Select label="Startup display mode" value={state.fields.StartupFullscreen ? 'fullscreen' : 'windowed'} onChange={value => void request({ kind: 'field', name: 'StartupFullscreen', value: value === 'fullscreen' }).catch(() => {})} options={[{ value: 'windowed', label: 'Windowed' }, { value: 'fullscreen', label: 'Fullscreen' }]} /></div><p>Applies the next time OrbitX starts in the graphical interface. F11 switches the current session.</p></Section><Appearance page /></>; break;
    case 'language': detail = <LanguageSetting />; break;
    case 'metadata': detail = <Section title="Metadata"><WindowsSettingsControls includeSettingsAction={false} /></Section>; break;
    case 'about': detail = <Section title="About OrbitX"><OrbitXLogo /><p>A Windows game library and PS5 emulator frontend.</p><p>Based on the original Orbit Store frontend for PS5 by saawant12.</p><p><a href="https://github.com/saawant12/orbit-store-ps5" target="_blank" rel="noreferrer">Original Orbit repository on GitHub</a></p><p><a href="https://github.com/revvinsuno-sys/OrbitX" target="_blank" rel="noreferrer">OrbitX corresponding source and build instructions</a></p><p>Copyright (C) 2026 OrbitX contributors. GNU GPL version 3. You may redistribute and modify this application under that licence. No warranty is provided. Third-party components retain their own terms.</p><p><a href="https://github.com/revvinsuno-sys/OrbitX/blob/main/LICENSE" target="_blank" rel="noreferrer">View licence</a> — LICENSE, NOTICE.md and THIRD-PARTY-NOTICES.md are also included in the application folder.</p>{facts([['AboutBuildVersion', 'Current build version'], ['AboutLastUpdated', 'Last updated (local build time)']])}</Section>; break;
  }
  return <section ref={container} className={`page orbit-settings ${selected === 'home' ? 'settings-home' : 'settings-detail'}`} aria-label="Settings">
    <h1 className="settings-title">Settings</h1><div className="settings-breadcrumb"><button onClick={() => choose('home')}>Settings</button>{current && <><span aria-hidden="true">›</span><span>{current.label}</span></>}</div>
    <nav className="settings-tiles game-rail" aria-label="Settings categories" data-own-arrows onKeyDown={event => {
      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
      const tiles = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('[data-settings-category]')], at = tiles.indexOf(event.target as HTMLButtonElement);
      if (at < 0) return;
      if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
        if (selected !== 'home') return;
        const columns = getComputedStyle(event.currentTarget).gridTemplateColumns.split(' ').length;
        const next = at + (event.key === 'ArrowDown' ? columns : -columns);
        if (next < 0 || next >= tiles.length) return;
        event.preventDefault(); tiles[next].focus(); return;
      }
      event.preventDefault();
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tiles.length - 1 : (at + (event.key === 'ArrowRight' ? 1 : -1) + tiles.length) % tiles.length;
      tiles[next].focus();
    }}>{categories.map(category => {
      return <button key={category.id} data-settings-category={category.id} aria-current={selected === category.id ? 'page' : undefined} title={selected === 'home' ? `${category.label}: ${tileStatus(category.id)}` : category.label} onFocus={event => event.currentTarget.scrollIntoView({ block: 'nearest', inline: 'nearest' })} onClick={() => { (document.activeElement as HTMLElement)?.blur(); choose(category.id); requestAnimationFrame(() => container.current?.querySelector<HTMLElement>(`[data-settings-category="${category.id}"]`)?.scrollIntoView({ block: 'nearest', inline: 'nearest' })); }}>
        <SettingsArtwork category={category.id} square={selected !== 'home'} />
        <span className="settings-tile-caption"><strong>{category.label}</strong>{selected === 'home' && <span className="settings-tile-status">{tileStatus(category.id)}</span>}</span>
      </button>;
    })}</nav>
    {error && <p role="alert" className="settings-error">{error}</p>}
    {!state && <p role="status">Loading settings…</p>}
    {current && <div className="orbit-settings-content" tabIndex={-1} key={selected}>{detail}</div>}
    {state && <p className="settings-page-status" role="status">{state.status}</p>}
  </section>;
}
