import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import './launch-overlay.css';

type LaunchState = { game: string; emulator: string; exiting: boolean; fading: boolean };

export function LaunchOverlay() {
  const [launch, setLaunch] = useState<LaunchState | null>(null);

  useEffect(() => {
    const started = (event: Event) => {
      const detail = (event as CustomEvent<{ game: string; emulator: string }>).detail;
      setLaunch({ ...detail, exiting: false, fading: false });
    };
    const exited = (event: Event) => {
      const detail = (event as CustomEvent<{ emulator: string }>).detail;
      setLaunch(current => current ? { ...current, emulator: detail.emulator, exiting: true } : null);
    };
    window.addEventListener('windows-game-launch', started);
    window.addEventListener('windows-game-exit', exited);
    return () => {
      window.removeEventListener('windows-game-launch', started);
      window.removeEventListener('windows-game-exit', exited);
    };
  }, []);

  useEffect(() => {
    if (!launch?.exiting || launch.fading) return;
    const timer = setTimeout(() => setLaunch(current => current ? { ...current, fading: true } : null), 850);
    return () => clearTimeout(timer);
  }, [launch?.exiting, launch?.fading]);

  useEffect(() => {
    if (!launch?.fading) return;
    const timer = setTimeout(() => setLaunch(null), 450);
    return () => clearTimeout(timer);
  }, [launch?.fading]);

  useEffect(() => {
    if (!launch) return;
    const root = document.getElementById('root');
    const wasInert = root?.inert ?? false;
    root?.setAttribute('inert', '');
    document.body.classList.add('windows-launch-active');
    const blockKeys = (event: KeyboardEvent) => { event.preventDefault(); event.stopImmediatePropagation(); };
    window.addEventListener('keydown', blockKeys, true);
    return () => {
      window.removeEventListener('keydown', blockKeys, true);
      document.body.classList.remove('windows-launch-active');
      if (root && !wasInert) root.removeAttribute('inert');
    };
  }, [!!launch]);

  if (!launch) return null;
  return createPortal(
    <div className={`windows-launch-overlay${launch.fading ? ' is-fading' : ''}`} role="alertdialog" aria-modal="true" aria-live="assertive" aria-label="Game launch status">
      <div className="windows-launch-dialog">
        <span className="windows-launch-spinner" aria-hidden="true" />
        <p>{launch.exiting ? `${launch.emulator} exited. Resuming...` : `Launching ${launch.game} in ${launch.emulator}`}</p>
      </div>
    </div>, document.body,
  );
}
