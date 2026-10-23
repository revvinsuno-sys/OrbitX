import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../orbit-upstream/src/api';
import { Modal } from '../orbit-upstream/src/components';
import { compatibilityStatuses, sharpEmuStatuses, StatusBadge } from './Compatibility';

export type LibraryCardItem = { id: string; title: string; version: string; integrity: string; extraction: string; classification: string;
  platform?: string; installedBytes?: number | null; installLabel?: string;
  actions: string[]; cover: string; coverFallback: string; titleId: string; compatibility: keyof typeof compatibilityStatuses; compatibilityOverride: string;
  compatibilityVersion: string; currentKytyVersion: string; compatibilityVersionMatch: string; websiteCompatibility: string;
  kytyLocalCompatibility: string; sharpEmuOfficialCompatibility: string; sharpEmuLocalCompatibility: string; sharpEmuCompatibilityVersion: string;
  websiteReports: { status: string; testedVersion: string; os: string; testedDate: string; versionMatch: string }[] };
function versionNote(match: string) { return match === 'current' ? 'Tested on current version' : match === 'older' ? 'Tested on older KytyPS5 version' : match === 'newer' ? 'Tested on newer KytyPS5 version' : match === 'different' ? 'Different version' : 'Current version not verified'; }
function CompatibilityResults({ item }: { item: LibraryCardItem }) {
  return <div className="library-compatibility-status">
    <p>Official KytyPS5: <StatusBadge inline status={item.websiteCompatibility} /></p>
    <p>Local KytyPS5: {item.compatibilityOverride ? <><StatusBadge inline local status={item.compatibilityOverride} /> <span>{item.compatibilityVersion || 'Version unknown'} · {versionNote(item.compatibilityVersionMatch)}</span></> : <StatusBadge inline local status="untested" />}</p>
    <p>Official SharpEmu: <StatusBadge inline emulator="SharpEmu" status={item.sharpEmuOfficialCompatibility} /></p>
    <p>Local SharpEmu: {item.sharpEmuLocalCompatibility ? <><StatusBadge inline local emulator="SharpEmu" status={item.sharpEmuLocalCompatibility} /> <span>{item.sharpEmuCompatibilityVersion || 'Version unknown'}</span></> : <StatusBadge inline local emulator="SharpEmu" status="untested" />}</p>
    {item.websiteReports.map((report, index) => <p key={index}>Website report · {report.os || 'OS unknown'}: <StatusBadge inline status={report.status} /> <span>{report.testedVersion || 'Version unknown'} · {versionNote(report.versionMatch)}{report.testedDate ? ` · ${report.testedDate}` : ''}</span></p>)}
    <p>Current KytyPS5: {item.currentKytyVersion || 'Version unavailable'}</p>
  </div>;
}
type PreparedDelete = { token: string; paths: string[]; holdMilliseconds: number };
function changed() { window.dispatchEvent(new Event('windows-state-changed')); }

function InstallationDetails({ item, close }: { item: LibraryCardItem; close: () => void }) {
  const [fields, setFields] = useState<Record<string, string> | null>(null), [error, setError] = useState('');
  useEffect(() => {
    let alive = true;
    void api<{ fields: Record<string, string> }>('/windows/details', { id: item.id })
      .then(result => { if (alive) setFields(result.fields); }).catch(e => { if (alive) setError((e as Error).message); });
    return () => { alive = false; };
  }, [item.id]);
  return <Modal title={`Installation details for ${item.title}`} close={close}><h2>{item.title}</h2>
    {!fields && !error && <p>Loading installation details…</p>}
    <dl className="library-installation-details">{fields && Object.entries(fields).filter(([, value]) => value).map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    {error && <p role="alert">{error}</p>}
  </Modal>;
}

type RelinkChoice = { id: string; sourceId: string; source: string; title: string; platform: string; titleId: string; cover: string };
function RelinkLibraryDialog({ item, close }: { item: LibraryCardItem; close: () => void }) {
  const [query, setQuery] = useState(item.title), [choices, setChoices] = useState<RelinkChoice[]>([]);
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [searched, setSearched] = useState(false);
  async function search(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError(''); setSearched(false);
    try { const result = await api<{ items: RelinkChoice[] }>('/windows/library/relink/search', { query }); setChoices(result.items); setSearched(true); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  async function link(choice: RelinkChoice) {
    setBusy(true); setError('');
    try { await api('/windows/library/relink/link', { id: item.id, entryId: choice.id, sourceId: choice.sourceId }); changed(); close(); }
    catch (e) { setError((e as Error).message); setBusy(false); }
  }
  return <Modal className="library-relink-dialog" title={`Relink ${item.title}`} close={close}>
    <h2>Match this local game to the catalogue</h2>
    <p>Link this local folder to a catalogue game to restore its platform and thumbnail. This does not move or replace your local game files.</p>
    <form className="library-relink-search" onSubmit={search}><label>Game title<input autoFocus value={query} onChange={event => setQuery(event.target.value)} /></label>
      <button type="submit" disabled={busy || query.trim().length < 2}>{busy ? 'Searching…' : 'Search games'}</button></form>
    {error && <p role="alert">{error}</p>}
    {!choices.length && <p role="status">{searched ? 'No matching games found in the available catalogues.' : 'Search the available catalogues to find the matching game.'}</p>}
    <div className="library-relink-results">{choices.map(choice => <button key={`${choice.sourceId}:${choice.id}`} type="button" disabled={busy} onClick={() => void link(choice)}>
      <span><strong>{choice.title}</strong><small>{choice.platform} {choice.titleId && `· ${choice.titleId}`} · {choice.source}</small></span>
    </button>)}</div>
  </Modal>;
}

function LaunchLogViewer({ item, close }: { item: LibraryCardItem; close: () => void }) {
  const [text, setText] = useState('Loading log…'), [error, setError] = useState(''), [loading, setLoading] = useState(false);
  const alive = useRef(true);
  async function refresh() {
    setLoading(true); setError('');
    try { const result = await api<{ text: string }>('/windows/log', { id: item.id }); if (alive.current) setText(result.text); }
    catch (e) { if (alive.current) setError((e as Error).message); }
    finally { if (alive.current) setLoading(false); }
  }
  useEffect(() => {
    alive.current = true; void refresh();
    return () => { alive.current = false; };
  }, []);
  return <Modal title={`Launch log for ${item.title}`} close={close}>
    <h2>{item.title} · Launch log</h2><p>Saved console output from the latest launch. Refresh to see new output while the emulator is running.</p>
    <pre className="windows-launch-log" tabIndex={0} aria-label="Emulator console output">{text}</pre>
    {error && <p role="alert">{error}</p>}<div className="windows-actions"><button disabled={loading} onClick={() => void refresh()}>{loading ? 'Refreshing…' : 'Refresh log'}</button><button onClick={close}>Close</button></div>
  </Modal>;
}

function HoldDelete({ item }: { item: LibraryCardItem }) {
  const [fraction, setFraction] = useState(0), [pending, setPending] = useState(false), [checking, setChecking] = useState(false);
  const [error, setError] = useState(''), [paths, setPaths] = useState<string[]>([]);
  const alive = useRef(true);
  const hold = useRef<{ frame: number; started: number } | null>(null);
  function stop() {
    if (hold.current) cancelAnimationFrame(hold.current.frame);
    hold.current = null; setFraction(0); setChecking(false);
  }
  useEffect(() => {
    alive.current = true;
    const cancel = () => stop();
    window.addEventListener('blur', cancel); document.addEventListener('visibilitychange', cancel); window.addEventListener('scroll', cancel, true);
    return () => { alive.current = false; if (hold.current) cancelAnimationFrame(hold.current.frame); hold.current = null;
      window.removeEventListener('blur', cancel); document.removeEventListener('visibilitychange', cancel); window.removeEventListener('scroll', cancel, true); };
  }, []);
  useEffect(() => { if (!item.actions.includes('deleteLibrary')) stop(); }, [item.actions.join('|')]);
  async function start() {
    if (pending || hold.current) return;
    const current = { frame: 0, started: 0 }; hold.current = current; setError(''); setChecking(true);
    try {
      const prepared = await api<PreparedDelete>('/windows/delete/prepare', { id: item.id });
      if (!alive.current || hold.current !== current) return;
      setPaths(prepared.paths); setChecking(false); current.started = performance.now();
      const tick = () => {
        if (hold.current !== current) return;
        if (document.hidden || !document.hasFocus()) { stop(); return; }
        const progress = Math.min(1, (performance.now() - current.started) / prepared.holdMilliseconds); setFraction(progress);
        if (progress < 1) { current.frame = requestAnimationFrame(tick); return; }
        hold.current = null; setPending(true);
        void api('/windows/delete/confirm', { id: item.id, token: prepared.token }).then(changed)
          .catch(e => { if (alive.current) setError((e as Error).message); })
          .finally(() => { if (alive.current) { setPending(false); setFraction(0); } });
      };
      current.frame = requestAnimationFrame(tick);
    } catch (e) { if (alive.current && hold.current === current) { stop(); setError((e as Error).message); } }
  }
  return <div className="library-delete-control">
    <button className="library-delete" data-hold-delete data-windows-action="deleteLibrary" disabled={pending || !item.actions.includes('deleteLibrary')}
      aria-label={`Delete ${item.title}; hold for two seconds`}
      onPointerDown={e => { if (e.button !== 0 || !e.isPrimary) return; e.preventDefault(); e.currentTarget.focus(); e.currentTarget.setPointerCapture(e.pointerId); void start(); }}
      onPointerMove={e => { if (!hold.current) return; const box = e.currentTarget.getBoundingClientRect(); if (e.clientX < box.left || e.clientX > box.right || e.clientY < box.top || e.clientY > box.bottom) stop(); }}
      onPointerUp={stop} onPointerCancel={stop} onLostPointerCapture={stop} onPointerLeave={stop} onBlur={stop}
      onKeyDown={e => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); if (!e.repeat) void start(); } if (e.key === 'Escape') stop(); }}
      onKeyUp={e => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); stop(); } }} onClick={e => e.preventDefault()} onContextMenu={e => e.preventDefault()}>
      <svg className="delete-dial" viewBox="0 0 32 32" aria-hidden="true"><circle cx="16" cy="16" r="12" className="delete-track" /><circle cx="16" cy="16" r="12" pathLength="1" strokeDasharray="1" strokeDashoffset={1 - fraction} /></svg>
      {pending ? 'Deleting…' : checking ? 'Checking…' : 'Delete'}
    </button>
    {paths.length > 0 && <p className="library-delete-paths">{paths.join('\n')}</p>}
    {error && <p role="alert">{error}</p>}
  </div>;
}

export function LibraryCard({ item, action, children, close }: { item: LibraryCardItem; action: (action: string) => Promise<void>; children: ReactNode; close: () => void }) {
  const [viewingLog, setViewingLog] = useState(false);
  const [viewingDetails, setViewingDetails] = useState(false);
  const [relinking, setRelinking] = useState(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [editing, setEditing] = useState(false);
  const [status, setStatus] = useState(item.compatibilityOverride);
  const [sharpStatus, setSharpStatus] = useState(item.sharpEmuLocalCompatibility);
  const pickerId = useId();
  async function run(name: string) {
    setBusy(true); setError('');
    try { await action(name); } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  const secondary = item.actions.filter(name => ['chooseEmulator', 'openFolder', 'reinspect', 'libraryDetails', 'relink'].includes(name));
  return <>{!viewingLog && !editing && !viewingDetails && !relinking && <Modal title={`Manage ${item.title}`} close={close}><div className="windows-management library-management" data-library-management={item.id}>
    <div className="library-card-body"><h2>{item.title}</h2>{item.version && <p>{item.version}</p>}<p>{item.classification}</p><p>{item.integrity} · {item.extraction}</p>
      <div className="windows-actions library-card-actions">
        {item.actions.includes('viewLog') && <button data-windows-action="viewLog" onClick={() => setViewingLog(true)}>View log</button>}
        {item.actions.filter(name => ['installLibrary', 'extract'].includes(name)).map(name => <button key={name} disabled={busy} data-windows-action={name} onClick={() => void run(name)}>{name === 'installLibrary' ? item.installLabel || 'Install' : 'Extract and install'}</button>)}
        <button data-windows-action="compatibility" onClick={() => { setStatus(item.compatibilityOverride); setSharpStatus(item.sharpEmuLocalCompatibility); setEditing(true); }}>Compatibility</button>
        {secondary.length > 0 && <div className="library-management-actions">
          {secondary.map(name => <button key={name} disabled={busy} data-windows-action={name} onClick={() => { if (name === 'libraryDetails') setViewingDetails(true); else if (name === 'relink') setRelinking(true); else void run(name); }}>{{ chooseEmulator: 'Choose emulator', openFolder: 'Open folder', reinspect: 'Reinspect', libraryDetails: 'Installation details', relink: 'Relink game' }[name]}</button>)}
        </div>}
      </div>
      <CompatibilityResults item={item} />
      {children}{error && <p role="alert">{error}</p>}
      {item.actions.includes('deleteLibrary') && <section className="library-delete-section"><h3>Delete game files</h3><p>Deletes owned game files and removes this entry from My Library.</p><div className="windows-actions"><HoldDelete item={item} /></div></section>}
    </div>
    </div></Modal>}
    {viewingLog && <LaunchLogViewer item={item} close={() => setViewingLog(false)} />}
    {viewingDetails && <InstallationDetails item={item} close={close} />}
    {relinking && <RelinkLibraryDialog item={item} close={() => setRelinking(false)} />}
    {editing && <Modal className="library-compatibility-editor" title={`Compatibility for ${item.title}`} close={() => { if (!busy) setEditing(false); }}><h2>Compatibility</h2>
      <p>{item.titleId ? `${item.titleId} · Applies to this title ID in My Library and Browse.` : 'No verified PPSA ID: applies to this library item only.'}</p><p>Saved on this computer. This does not submit a community report.</p>
      <CompatibilityResults item={item} /><p>Local results record the installed emulator build when available.</p>
      <label htmlFor={pickerId}>KytyPS5 status</label><select id={pickerId} value={status} disabled={busy} onChange={e => setStatus(e.target.value)}>
        <option value="">Use community status</option>{Object.entries(compatibilityStatuses).filter(([key]) => key !== 'unavailable').map(([key, info]) => <option key={key} value={key}>{info.label}</option>)}
      </select><label htmlFor={`${pickerId}-sharpemu`}>SharpEmu status</label><select id={`${pickerId}-sharpemu`} value={sharpStatus} disabled={busy} onChange={e => setSharpStatus(e.target.value)}>
        <option value="">Use community status</option>{Object.entries(sharpEmuStatuses).filter(([key]) => key !== 'unavailable' && key !== 'untested').map(([key, info]) => <option key={key} value={key}>{info.label}</option>)}
      </select><div className="windows-actions"><button disabled={busy} onClick={() => setEditing(false)}>Cancel</button><button className="library-play" disabled={busy} onClick={async () => {
        setBusy(true); setError(''); try { await api('/windows/compatibility', { id: item.id, status, sharpEmuStatus: sharpStatus }); changed(); setEditing(false); }
        catch (e) { setError((e as Error).message); } finally { setBusy(false); }
      }}>Save compatibility</button></div>{error && <p role="alert">{error}</p>}
    </Modal>}
  </>;
}
