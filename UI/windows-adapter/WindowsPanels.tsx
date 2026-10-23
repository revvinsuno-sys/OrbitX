import { type ReactNode } from 'react';
import { Appearance } from '../orbit-upstream/src/interfaces/Appearance';
import { LanguageSetting } from '../orbit-upstream/src/LanguageSetting';
import { Modal } from '../orbit-upstream/src/components';
import { navigate } from './bridge';
import { WindowsSettingsControls } from './WindowsManagement';
export { WindowsDownloads as Downloads, WindowsLibrary as Library } from './WindowsManagement';
export { SettingsPage, type SettingsSection } from './SettingsPage';

function Route({ page, children }: { page: 'Settings' | 'Downloads' | 'My Library'; children?: ReactNode }) {
  return <section className="page" aria-label={page}>
    <h1>{page}</h1>
    <p>{children ?? 'Manage catalogue sources, downloads, installed games and KytyPS5 in the application settings.'}</p>
    {page === 'Settings' ? <WindowsSettingsControls /> : <button className="primary" onClick={() => navigate(page)}>Open {page}</button>}
  </section>;
}
export function Sources() { return <Route page="Settings">Manage the catalogue sources shared by Orbit and Classic.</Route>; }
export function StorageOverview() { return <Route page="Settings">Manage download folders, installed games and storage configuration in Settings.</Route>; }
export function Pairing() { return <Route page="Settings">This interface is connected to OrbitX on this computer.</Route>; }
export function DownloadLimit() { return <button onClick={() => navigate('Downloads')}>Manage downloads</button>; }
export function ArchiveSetting() { return null; }
export function UpdateNotice() { return null; }
export function NativeAppNotice() { return null; }
export function AutoStart({ close }: { close: () => void }) {
  return <Modal title="App settings" close={close}><Route page="Settings" /><Appearance /><LanguageSetting /></Modal>;
}
