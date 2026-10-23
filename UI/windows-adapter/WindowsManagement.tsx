import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { api } from '../orbit-upstream/src/api';
import { Modal } from '../orbit-upstream/src/components';
import { bytes } from '../orbit-upstream/src/types';
import './windows.css';
import './igdb-settings.css';
import { type LibraryCardItem } from './LibraryCard';
import { LibraryGrid } from './LibraryGrid';

type ItemProgress = { id: string; kind: string; text: string; percentage: number | null; cancellable: boolean };
type ItemFailure = { label: string; error: string; description: string };
type RecordActions = { id: string; title: string; actions: string[]; progress?: ItemProgress; failure?: ItemFailure; installationStatus?: string; installLabel?: string };
type Download = RecordActions & { state: string; error: string; archiveStatus: string; method: string; retryLabel: string; downloadedBytes: number; totalBytes?: number; percentage?: number; bytesPerSecond: number; fileName: string; installLabel: string };
type LibraryItem = RecordActions & LibraryCardItem;
type Installation = RecordActions & { cardId: string; fileName: string; state: string; error: string; totalBytes: number; cleanupResult: string };
type Release = { title: string; provider: string; filename: string; version: string; format: string; size?: number; actions: string[]; downloadStatus?: 'queued' | 'downloading' | null; instructions: Record<string, string> };
type EmulatorAvailability = { available: boolean; availability: string };
type State = { status: string; scanning: boolean; scanStatus: string; downloadRoot: string; extraction: string; extractionPercentage: number; installation: string; installationProgress: string; installationPercentage: number; actions: string[]; emulators: { kytyPS5: EmulatorAvailability; sharpEmu: EmulatorAvailability; shadPS4: EmulatorAvailability }; downloads: Download[]; providerSessions: RecordActions[]; library: LibraryItem[]; installations: Installation[] };
const labels: Record<string, string> = {
  findDownloadGame: 'Find game',
  checkStatus: 'Check status', cancelContentMove: 'Cancel verification',
  download: 'Download', browser: 'Download', external: 'Open provider in browser', import: 'Import completed file',
  downloadAll: 'Download all files (PS5 Library)', deleteDownload: 'Delete', scanDownloads: 'Scan downloads',
  retryDownload: 'Retry', retryInstallationDownload: 'Retry', pause: 'Pause', resume: 'Resume', cancel: 'Cancel download', restart: 'Restart download', discard: 'Discard incomplete', clearDownload: 'Remove', installDownload: 'Install / extract',
  clearDownloads: 'Clear completed history', downloadsFolder: 'Open Downloads folder', addFolder: 'Add folder', addElf: 'Add ELF', installFile: 'Install from file',
  openFolder: 'Open folder', reinspect: 'Reinspect', extract: 'Extract ZIP / RAR', installLibrary: 'Install', deleteLibrary: 'Delete', launch: 'Play', chooseEmulator: 'Choose emulator',
  libraryDetails: 'Installation details', installationDetails: 'Details', retryInstallation: 'Retry installation', deleteInstallation: 'Delete', retryCleanup: 'Retry source cleanup', cancelInstallation: 'Cancel installation', clearInstallations: 'Clear installation history',
  settings: 'Settings', refresh: 'Refresh catalogues',
  cancelQueuedInstallation: 'Cancel Install',
};
async function act(action: string, id = '', backend?: string) { await api('/windows/action', { action, id, ...(backend ? { backend } : {}) }); window.dispatchEvent(new Event('windows-state-changed')); }
function useWindows() {
  const [state, setState] = useState<State | null>(null), [error, setError] = useState('');
  useEffect(() => {
    let alive = true, pending = false;
    const refresh = async () => {
      if (pending || document.hidden) return; pending = true;
      try { const next = await api<State>('/windows/state'); if (alive) { setState(next); setError(''); } }
      catch (e) { if (alive) setError((e as Error).message); }
      finally { pending = false; }
    };
    void refresh(); const timer = setInterval(() => void refresh(), 1000);
    window.addEventListener('windows-state-changed', refresh); document.addEventListener('visibilitychange', refresh);
    return () => { alive = false; clearInterval(timer); window.removeEventListener('windows-state-changed', refresh); document.removeEventListener('visibilitychange', refresh); };
  }, []);
  return { state, error };
}
function Actions({ actions, id = '', only, installLabel, retryLabel }: { actions: string[]; id?: string; only?: string[]; installLabel?: string; retryLabel?: string }) {
  const [pending, setPending] = useState(false), [error, setError] = useState('');
  return <><div className="windows-actions">{actions.filter(action => !only || only.includes(action)).map(action =>
    <button key={action} disabled={pending} data-windows-action={action} onClick={async () => {
      setPending(true); setError(''); try { await act(action, id); } catch (e) { setError((e as Error).message); } finally { setPending(false); }
    }}>{action === 'retryDownload' && retryLabel ? retryLabel : ['installDownload', 'retryInstallation'].includes(action) && installLabel ? installLabel : labels[action] ?? action}</button>)}</div>{error && <p role="alert">{error}</p>}</>;
}
function QueuedStatus({ status }: { status?: string }) {
  return status ? <span className="windows-badge windows-badge-queued" role="status">{status}</span> : null;
}
function Progress({ value, label }: { value?: number; label: string }) {
  return <progress max={100} value={value == null ? undefined : value} aria-label={label} />;
}
function OperationProgress({ progress, showBar = true }: { progress?: ItemProgress; showBar?: boolean }) {
  return progress ? <section aria-label={`Active ${progress.kind.toLowerCase()}`} role="status">
    <p className="windows-message">{progress.text}</p>
    {showBar && <Progress value={progress.percentage ?? undefined} label={`${progress.kind} progress`} />}
    {progress.cancellable && <Actions actions={[progress.kind === 'Verification' ? 'cancelContentMove' : 'cancelInstallation']} />}
  </section> : null;
}
function FailureNotice({ failure }: { failure?: ItemFailure }) {
  return failure ? <div className="windows-failure" role="status">
    <span className="windows-badge windows-badge-error">{failure.label}</span>
    <p>{failure.error}</p>{failure.description && <p>{failure.description}</p>}
  </div> : null;
}
function TransferCard({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  return <article className="windows-record" data-windows-id={id} tabIndex={-1}><h2>{title}</h2>{children}</article>;
}
function Installations({ state, items }: { state: State; items: RecordActions[] }) {
  return <section aria-label="Installations"><h2>Installations</h2>
    <Actions actions={state.actions} only={['installFile', 'clearInstallations']} />
    <p>{state.installation}</p>

    {state.installations.map(job => <TransferCard key={job.cardId} id={job.cardId} title={job.title || 'Installation'}><p>{job.fileName}</p><p>{job.state} · {bytes(job.totalBytes)}</p><p>{job.cleanupResult}</p><QueuedStatus status={job.installationStatus} /><FailureNotice failure={job.failure} />{!items.some(item => item.progress?.id === job.id) && <OperationProgress progress={job.progress} />}{job.error && <p role="alert">{job.error}</p>}<Actions id={job.id} actions={job.actions} installLabel={job.installLabel} /></TransferCard>)}
  </section>;
}
export function WindowsDownloads({ targetId }: { targetId: string | null }) {
  const { state, error } = useWindows();
  const section = useRef<HTMLElement>(null), focusedTarget = useRef<string | null>(null);
  useEffect(() => {
    if (!targetId) { focusedTarget.current = null; return; }
    if (focusedTarget.current === targetId) return;
    const row = [...section.current?.querySelectorAll<HTMLElement>('[data-windows-id]') ?? []]
      .find(element => element.dataset.windowsId === targetId);
    if (!row) return;
    row.scrollIntoView({ block: 'center', behavior: 'instant' });
    row.focus({ preventScroll: true });
    focusedTarget.current = targetId;
  }, [targetId, state]);
  const canRefresh = state?.actions.includes('scanDownloads') ?? false;
  useEffect(() => { if (canRefresh) void act('refreshDownloads').catch(() => {}); }, [canRefresh]);
  return <section ref={section} className="page windows-management" aria-label="Downloads"><h1>Downloads</h1>
    {error && <p role="alert">{error}</p>}{!state ? <p>Loading downloads…</p> : <>
      <Actions actions={state.actions} only={['scanDownloads', 'clearDownloads', 'downloadsFolder', 'settings']} />

      {state.scanning && <button disabled>Scanning…</button>}
      {(state.scanning || !state.downloads.some(job => job.progress) && !state.installations.some(job => job.progress)) && <p role="status">{state.scanning ? state.scanStatus : state.status}</p>}
      {!state.scanning && state.scanStatus && state.scanStatus !== state.status && <p>{state.scanStatus}</p>}

      {state.providerSessions.map(session => <article className="windows-record" key={session.id} data-windows-id={session.id}><h2>{session.title}</h2><p>Provider browser open · waiting for a download offer</p><Actions id={session.id} actions={session.actions} /></article>)}
      {!state.downloads.length && !state.providerSessions.length && !state.installations.length && <p>No downloads yet. Open a game, choose a release, then select Download or Browser-assisted download.</p>}
      {state.downloads.map(job => <TransferCard key={job.id} id={job.id} title={job.title}><p>{job.fileName} · {job.method}</p><p>{job.progress ? `${job.progress.kind} in progress` : <>{job.state} · {bytes(job.downloadedBytes)} / {job.totalBytes == null ? 'Size unknown' : bytes(job.totalBytes)}{job.bytesPerSecond > 0 ? ` · ${bytes(job.bytesPerSecond)}/s` : ''}</>}</p><Progress value={job.progress ? job.progress.percentage ?? undefined : ['Downloaded', 'Imported', 'Installed'].includes(job.state) ? 100 : job.percentage} label={job.progress ? `${job.progress.kind} progress` : `${job.title} download progress`} /><OperationProgress progress={job.progress} showBar={false} /><QueuedStatus status={job.installationStatus} /><FailureNotice failure={job.failure} />{job.archiveStatus && <span className="windows-badge" role="status">{job.archiveStatus}</span>}{job.error && <p role="alert">{job.error}</p>}<Actions id={job.id} actions={job.actions} installLabel={job.installLabel} retryLabel={job.retryLabel} /></TransferCard>)}
      <Installations state={state} items={state.downloads} />
    </>}
  </section>;
}
export function WindowsLibrary() {
  const { state, error } = useWindows();
  return <section className="page windows-management windows-library" aria-label="My Library">
    {error && <p role="alert">{error}</p>}{!state ? <p>Loading library…</p> : <>
      <LibraryGrid items={state.library} emulators={state.emulators} action={(item, name, backend) => act(name, item.id, backend)}>{item => <><OperationProgress progress={item.progress} /><QueuedStatus status={item.installationStatus} /><FailureNotice failure={item.failure} /></>}</LibraryGrid>
      <details className="library-tools"><summary>Library tools and installation activity</summary><Actions actions={state.actions} only={['addFolder', 'addElf', 'settings']} /><p role="status">{state.status}</p><Installations state={state} items={state.library} /></details>
    </>}
  </section>;
}
export function WindowsSettingsControls({ includeSettingsAction = true }: { includeSettingsAction?: boolean } = {}) {
  const { state, error } = useWindows();
  const [clientId, setClientId] = useState(''), [clientSecret, setClientSecret] = useState(''), [hasSecret, setHasSecret] = useState(false);
  const [loading, setLoading] = useState(true), [saving, setSaving] = useState(false), [message, setMessage] = useState('');
  useEffect(() => {
    let alive = true;
    void api<{ clientId: string; hasClientSecret: boolean }>('/windows/igdb-settings').then(value => {
      if (alive) { setClientId(value.clientId); setHasSecret(value.hasClientSecret); }
    }).catch(e => { if (alive) setMessage((e as Error).message); }).finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, []);
  async function save(event: FormEvent) {
    event.preventDefault(); setSaving(true); setMessage('');
    try {
      const saved = await api<{ clientId: string; hasClientSecret: boolean }>('/windows/igdb-settings', { clientId, clientSecret });
      setClientId(saved.clientId); setHasSecret(saved.hasClientSecret); setClientSecret('');
      setMessage('IGDB media settings saved.');
    } catch (e) { setMessage((e as Error).message); }
    finally { setSaving(false); }
  }
  return <>{error && <p role="alert">{error}</p>}{state && <><Actions actions={state.actions} only={includeSettingsAction ? ['settings', 'refresh'] : ['refresh']} /><p role="status">{state.status}</p></>}
    <section className="windows-record" aria-labelledby="igdb-settings-title">
      <h2 id="igdb-settings-title">IGDB media</h2>
      <p>Credentials are stored for this Windows account and used to load game images and trailers.</p>
      {loading ? <p>Loading IGDB settings…</p> : <form className="igdb-settings" onSubmit={save}>
        <label>Client ID<input autoComplete="off" value={clientId} maxLength={256} onChange={event => setClientId(event.target.value)} required /></label>
        <label>Client secret<input type="password" autoComplete="new-password" value={clientSecret} maxLength={1024} placeholder={hasSecret ? 'Saved; leave blank to keep it' : 'Enter client secret'} onChange={event => setClientSecret(event.target.value)} /></label>
        {hasSecret && !clientSecret && <p>A client secret is saved.</p>}
        <button className="primary" type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save IGDB settings'}</button>
        {message && <p role="status">{message}</p>}
      </form>}
    </section>
  </>;
}
export function WindowsReleaseActions() {
  const [id, setId] = useState(''), [release, setRelease] = useState<Release | null>(null), [error, setError] = useState(''), [pending, setPending] = useState(false);
  useEffect(() => {
    const open = (event: Event) => { setId((event as CustomEvent<string>).detail); setRelease(null); setError(''); };
    const close = () => setId('');
    window.addEventListener('windows-release', open); window.addEventListener('windows-appearance', close); window.addEventListener('windows-page', close);
    return () => { window.removeEventListener('windows-release', open); window.removeEventListener('windows-appearance', close); window.removeEventListener('windows-page', close); };
  }, []);
  useEffect(() => {
    if (!id) return; let alive = true, loading = false;
    const refresh = async () => {
      if (loading) return;
      loading = true;
      try { const value = await api<Release>('/windows/release', { id }); if (alive) { setRelease(value); setError(''); } }
      catch (e) { if (alive) setError((e as Error).message); }
      finally { loading = false; }
    };
    void refresh();
    const timer = setInterval(() => void refresh(), 1200);
    return () => { alive = false; clearInterval(timer); };
  }, [id]);
  const downloadLabel = release?.downloadStatus === 'queued' ? 'Added to queue' : 'Downloading';
  return id ? <Modal title="Download release" close={() => setId('')}><div className="windows-management">
    {release ? <><h2>{release.title}</h2><p>{release.provider}</p><p>{release.filename} · {release.format}{release.version ? ` · ${release.version}` : ''} · {release.size == null ? 'Size unknown' : bytes(release.size)}</p><p>Save to your configured Windows Downloads folder. Installation and launching are separate.</p>
      {!release.actions.includes('browser') && <p>This release has no available download link.</p>}
      <div className="windows-actions">{release.actions.map(action => {
        const downloadAction = action === 'download' || action === 'browser';
        const busy = downloadAction && !!release.downloadStatus;
        return <div className="windows-method" key={action}><button aria-describedby={`windows-${action}-instructions`} className={action === release.actions[0] ? 'primary' : ''} disabled={pending || busy} data-windows-action={action} onClick={async () => {
        setPending(true); setError(''); try { await act(action, id); const updated = await api<Release>('/windows/release', { id }); setRelease(updated); }
        catch (e) { setError((e as Error).message); } finally { setPending(false); }
      }}>{busy ? downloadLabel : labels[action]}</button><p id={`windows-${action}-instructions`}>{release.instructions[action]}</p></div>;
      })}</div></> : !error && <p>Loading release…</p>}
    {error && <p role="alert">{error}</p>}
  </div></Modal> : null;
}

export function WindowsSaveTo({ className = '' }: { className?: string }) {
  const { state, error } = useWindows();
  return <section className={`windows-save-to ${className}`} aria-label="Save to"><h2>Save to</h2><p>{state?.downloadRoot ?? (error || 'Loading download path…')}</p></section>;
}

export function WindowsDownloadToast() {
  const [message, setMessage] = useState('');
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout>;
    const show = (event: Event) => { clearTimeout(timer); setMessage((event as CustomEvent<string>).detail); timer = setTimeout(() => setMessage(''), 5000); };
    window.addEventListener('windows-toast', show);
    return () => { window.removeEventListener('windows-toast', show); clearTimeout(timer); };
  }, []);
  return message ? <div className="windows-toast" role="status" aria-live="polite" aria-atomic="true">{message}</div> : null;
}
