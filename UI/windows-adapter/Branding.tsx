import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { System } from '../orbit-upstream/src/types';
import logo from '../../PS5CatalogBrowser.App/Assets/Branding/orbitx-logo.png';
import './branding.css';

// Keep the supplied artwork intact; the viewport excludes its empty canvas.
export function OrbitXLogo({ className = '' }: { className?: string }) {
  return <svg className={`orbitx-logo ${className}`} viewBox="0 440 1254 345" role="img" aria-label="OrbitX">
    <image href={logo} width="1254" height="1254" />
  </svg>;
}

export function StartupSplash({ ready, error, system }: { ready: boolean; error: string; system: System | null }) {
  const [visible, setVisible] = useState(() => sessionStorage.getItem('orbitx.splash.shown') !== '1');
  const [leaving, setLeaving] = useState(false);
  const overlay = useRef<HTMLDivElement>(null), started = useRef(performance.now());
  const populated = !!error || ready && (system as (System & { startupLoading?: boolean }) | null)?.startupLoading === false;
  useLayoutEffect(() => {
    if (!visible) return;
    const siblings = [...(overlay.current?.parentElement?.children ?? [])].filter(node => node !== overlay.current) as HTMLElement[];
    const before = siblings.map(node => node.inert);
    const overflow = document.documentElement.style.overflow;
    const app = document.querySelector('.app');
    app?.classList.add('startup-pending');
    document.documentElement.style.overflow = 'hidden';
    siblings.forEach(node => { node.inert = true; });
    return () => { app?.classList.remove('startup-pending', 'startup-revealing'); document.documentElement.style.overflow = overflow; siblings.forEach((node, index) => { node.inert = before[index]; }); };
  }, [visible]);
  useLayoutEffect(() => {
    document.querySelector('.app')?.classList.toggle('startup-revealing', visible && leaving);
  }, [visible, leaving]);
  useEffect(() => {
    if (!visible || !populated) return;
    // React has committed the catalogue; let the browser lay out and paint its tiles.
    let frame = 0, fade = 0, done = 0;
    frame = requestAnimationFrame(() => { frame = requestAnimationFrame(() => {
      const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches || document.documentElement.dataset.motion === 'reduced';
      fade = window.setTimeout(() => {
        setLeaving(true);
        done = window.setTimeout(() => { sessionStorage.setItem('orbitx.splash.shown', '1'); setVisible(false); }, reduced ? 0 : 450);
      }, reduced ? 0 : Math.max(0, 900 - (performance.now() - started.current)));
    }); });
    return () => { cancelAnimationFrame(frame); clearTimeout(fade); clearTimeout(done); };
  }, [visible, populated]);
  return visible ? createPortal(<div ref={overlay} className={`orbitx-startup${leaving ? ' leaving' : ''}`} role="status" aria-label="Loading OrbitX">
    <OrbitXLogo /><p>Loading your library…</p>
  </div>, document.body) : null;
}
