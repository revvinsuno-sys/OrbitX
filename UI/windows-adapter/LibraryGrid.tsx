import { CoverLaunch } from "./CoverLaunch";
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Artwork } from '../orbit-upstream/src/Artwork';
import { LibraryCard, type LibraryCardItem } from './LibraryCard';
import { libraryCompatibility, compatibilityStatuses, badgeLabel, statusColor } from './Compatibility';

let rememberedGame = '';

function Preview({ item, source, anchor, close, retain, dismiss, play, choose, manage, pending }: {
  item: LibraryCardItem; source: 'kyty' | 'local'; anchor: HTMLElement; close: () => void; retain: () => void; dismiss: () => void;
  play: () => void; choose: () => void; manage: () => void; pending: boolean;
}) {
  const panel = useRef<HTMLDivElement>(null);
  const title = useRef<HTMLHeadingElement>(null);
  const [position, setPosition] = useState({ left: 0, top: 0, width: 0, height: 0 });
  const result = libraryCompatibility(item, source);
  useLayoutEffect(() => {
    const heading = title.current;
    if (!heading) return;
    const fit = () => {
      heading.style.fontSize = '';
      const style = getComputedStyle(heading);
      const baseSize = parseFloat(style.fontSize);
      const lineHeight = parseFloat(style.lineHeight) || baseSize * 1.15;
      const maxHeight = lineHeight * 3;
      if (heading.scrollHeight <= maxHeight) return;

      let low = 10, high = baseSize;
      for (let i = 0; i < 10; i++) {
        const size = (low + high) / 2;
        heading.style.fontSize = `${size}px`;
        if (heading.scrollHeight <= maxHeight) low = size;
        else high = size;
      }
      heading.style.fontSize = `${low}px`;
    };
    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(heading);
    return () => observer.disconnect();
  }, [item.id, item.title, position.width]);
  useLayoutEffect(() => {
    const place = () => {
      const rect = anchor.getBoundingClientRect();
      if (!anchor.isConnected || rect.bottom <= 0 || rect.top >= innerHeight) { dismiss(); return; }
      setPosition({ width: rect.width, height: rect.height, left: rect.left, top: rect.top });
    };
    place(); const observer = new ResizeObserver(place); observer.observe(panel.current!); observer.observe(anchor);
    window.addEventListener('resize', place); window.addEventListener('scroll', place, true);
    return () => { observer.disconnect(); window.removeEventListener('resize', place); window.removeEventListener('scroll', place, true); };
  }, [anchor, item.id]);
  return createPortal(<div ref={panel} id="library-preview" className="library-preview" style={{ left: position.left, top: position.top, width: position.width || undefined, height: position.height || undefined }} data-library-preview={item.id}
    onPointerEnter={retain} onPointerLeave={close} onFocus={retain} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node)) close(); }} role="region" aria-label={`Preview of ${item.title}`}>
    <h2 ref={title}>{item.title}</h2>
    <p className={`library-compatibility-pill${result.older ? ' library-compatibility-older' : ''}`} style={{ color: result.color, borderColor: `${result.color}a6` }}>
      <span className="library-source-dot" style={{ background: result.color }} aria-hidden="true" />{result.label}
    </p>
    {!item.actions.includes('launch') && <p className="library-unavailable">Play unavailable · {item.classification || 'Files are not ready for a launch attempt'}</p>}
    <div className="library-preview-actions">
      <button className="library-preview-play" type="button" disabled={pending || !item.actions.includes('launch')} onClick={play} aria-label={`Play ${item.title}`}>
        <span className="library-play-glyph" aria-hidden="true">A</span><strong>{pending ? 'Starting…' : 'Play'}</strong>
      </button>
      <button className="library-preview-control" type="button" disabled={pending || !item.actions.includes('chooseEmulator')} onClick={choose} title="Choose emulator" aria-label={`Choose emulator for ${item.title}`}>
        <span aria-hidden="true">X</span><strong>Choose emulator</strong>
      </button>
      <button className="library-preview-control library-preview-menu" type="button" onClick={manage} title="Menu" aria-label={`Menu for ${item.title}`}>
        <span aria-hidden="true">Y</span><strong>Menu</strong>
      </button>
    </div>
  </div>, document.body);
}

function CompatibilityHalves({ item }: { item: LibraryCardItem }) {
  const hasLocalStatus = (status: string) => status.length > 0 && status !== 'untested' && status !== 'unavailable';
  const parts = [
    ['KytyPS5', hasLocalStatus(item.kytyLocalCompatibility) ? item.kytyLocalCompatibility : item.websiteCompatibility,
      hasLocalStatus(item.kytyLocalCompatibility) ? 'Local' : 'Official', hasLocalStatus(item.kytyLocalCompatibility) ? 'local' : 'official'],
    ['SharpEmu', hasLocalStatus(item.sharpEmuLocalCompatibility) ? item.sharpEmuLocalCompatibility : item.sharpEmuOfficialCompatibility,
      hasLocalStatus(item.sharpEmuLocalCompatibility) ? 'Local' : 'Official', hasLocalStatus(item.sharpEmuLocalCompatibility) ? 'local' : 'official'],
  ] as const;
  const describe = badgeLabel;
  const title = parts.map(([name, status, source]) => `${source} ${name}: ${describe(status)}`).join('\n');
  return <span className="library-compatibility-halves" title={title} aria-label={title}>
    {parts.map(([name, status, , source]) => <i key={name} style={{ background: statusColor(status, source) }} aria-hidden="true" />)}
  </span>;
}

export function LibraryGrid<T extends LibraryCardItem>({ items, action, children }: {
  items: T[]; action: (item: T, name: string) => Promise<void>; children: (item: T) => ReactNode;
}) {
  const [query, setQuery] = useState(''), [sort, setSort] = useState('title'), [filter, setFilter] = useState('');
  const [active, setActive] = useState(''), [managing, setManaging] = useState(''), [pending, setPending] = useState(''), [error, setError] = useState('');
  const [compatibilitySource, setCompatibilitySource] = useState<'kyty' | 'local'>('kyty');
  const grid = useRef<HTMLDivElement>(null), timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined), launching = useRef(false);
  const activeRef = useRef(active); activeRef.current = active;
  function cancelTimer() { clearTimeout(timer.current); }
  function show(id: string, immediate: boolean) {
    cancelTimer();
    if (immediate) { rememberedGame = id; setActive(id); }
    else timer.current = setTimeout(() => setActive(id), 200);
  }
  function leave() {
    cancelTimer();
    timer.current = setTimeout(() => {
      const card = grid.current?.querySelector<HTMLElement>(`[data-windows-id="${activeRef.current}"]`);
      const preview = document.querySelector<HTMLElement>('.library-preview');
      if (card?.contains(document.activeElement) || card?.matches(':hover') || preview?.matches(':hover') || preview?.contains(document.activeElement)) return;
      setActive('');
    }, 100);
  }
  useEffect(() => {
    const back = (event: Event) => {
      if (document.querySelector('[role="dialog"], [role="alertdialog"]')) return;
      const tools = document.querySelector<HTMLDetailsElement>('.library-tools[open]');
      if (tools) { event.preventDefault(); tools.open = false; tools.querySelector('summary')?.focus(); return; }
      if (activeRef.current) { event.preventDefault(); cancelTimer(); setActive(''); }
    };
    document.addEventListener('windows-back', back);
    return () => { cancelTimer(); document.removeEventListener('windows-back', back); };
  }, []);
  useEffect(() => {
    if (!rememberedGame || !items.length) return;
    const card = grid.current?.querySelector<HTMLButtonElement>(`[data-windows-id="${rememberedGame}"] .library-play-surface`);
    if (card && !document.querySelector('[role="dialog"]') && (!document.activeElement || document.activeElement === document.body || document.activeElement.closest('.main-nav'))) {
      card.focus({ preventScroll: true }); card.scrollIntoView({ block: 'nearest' });
    }
  }, [items.length]);
  const visible = items.filter(item => item.title.toLocaleLowerCase().includes(query.toLocaleLowerCase()) && (!filter || libraryCompatibility(item, compatibilitySource).status === filter))
    .sort((a, b) => sort === 'status' ? libraryCompatibility(a, compatibilitySource).label.localeCompare(libraryCompatibility(b, compatibilitySource).label) || a.title.localeCompare(b.title) : a.title.localeCompare(b.title));
  const selected = items.find(item => item.id === active), managed = items.find(item => item.id === managing);
  const anchor = active ? grid.current?.querySelector<HTMLElement>(`[data-windows-id="${active}"] .library-poster`) : null;
  async function play(item: T) {
    if (launching.current || !item.actions.includes('launch')) return;
    launching.current = true; setPending(item.id); setError('');
    try { await action(item, 'launch'); } catch (e) { setError((e as Error).message); }
    finally { launching.current = false; setPending(''); }
  }
  async function choose(item: T) {
    if (launching.current || !item.actions.includes('chooseEmulator')) return;
    launching.current = true; setPending(item.id); setError(''); cancelTimer(); setActive('');
    try { await action(item, 'chooseEmulator'); } catch (e) { setError((e as Error).message); }
    finally { launching.current = false; setPending(''); }
  }
  return <>
    <header className="library-header"><div><p className="library-eyebrow">YOUR COLLECTION</p><h1>My Library <span>{items.length} installed</span></h1></div>
      <p className="library-emulator">{items[0]?.currentKytyVersion || 'KytyPS5 version unavailable'}</p>
      <div className="library-toolbar"><label>Search library<input type="search" value={query} onChange={e => { cancelTimer(); setActive(''); setQuery(e.target.value); }} placeholder="Find a game" /></label>
        <label>Sort<select value={sort} onChange={e => { cancelTimer(); setActive(''); setSort(e.target.value); }}><option value="title">Title A–Z</option><option value="status">Compatibility</option></select></label>
        <label>Compatibility<select value={filter} onChange={e => { cancelTimer(); setActive(''); setFilter(e.target.value); }}><option value="">All results</option>{Object.entries(compatibilityStatuses).filter(([key]) => key !== 'unavailable').map(([key, info]) => <option key={key} value={key}>{info.label}</option>)}</select></label>
        <fieldset className="library-compatibility-source"><legend>Compatibility source</legend><div role="group" aria-label="Compatibility result source">
          <button type="button" aria-pressed={compatibilitySource === 'kyty'} onClick={() => { cancelTimer(); setActive(''); setCompatibilitySource('kyty'); }}>KytyPS5</button>
          <button type="button" aria-pressed={compatibilitySource === 'local'} onClick={() => { cancelTimer(); setActive(''); setCompatibilitySource('local'); }}>Local</button>
        </div></fieldset>
      </div>
    </header>
    {error && <p role="alert">{error}</p>}
    <div className="library-cover-grid browse-grid" ref={grid} onKeyDown={event => {
      if (!event.key.startsWith('Arrow') || (event.target as Element).closest('[role="dialog"]')) return;
      const card = (event.target as HTMLElement).closest<HTMLElement>('.windows-library-card'); if (!card) return;
      event.preventDefault(); event.stopPropagation();
      const cards = [...grid.current!.querySelectorAll<HTMLElement>('.windows-library-card')];
      const index = cards.indexOf(card), top = cards[0].getBoundingClientRect().top;
      const columns = cards.filter(value => Math.abs(value.getBoundingClientRect().top - top) < 2).length;
      if ((event.target as HTMLElement).matches('.library-more-button') && event.key === 'ArrowLeft') card.querySelector<HTMLButtonElement>('.library-play-surface')?.focus();
      else {
        const step = event.key === 'ArrowLeft' ? -1 : event.key === 'ArrowRight' ? 1 : event.key === 'ArrowUp' ? -columns : columns;
        const next = cards[index + step]?.querySelector<HTMLButtonElement>('.library-play-surface');
        next?.focus({ preventScroll: true }); next?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      }
    }}>
      {visible.map(item => {
        const result = libraryCompatibility(item, compatibilitySource);
        return <article key={item.id} className="windows-library-card" data-windows-id={item.id} data-expanded={active === item.id || undefined} data-own-arrows
          onPointerEnter={() => show(item.id, false)} onPointerLeave={leave}
          onFocus={() => show(item.id, true)} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node)) leave(); }}>
          <CoverLaunch className="game-tile library-play-surface" data-windows-action="launch" aria-current={active === item.id ? 'true' : undefined} aria-disabled={!!pending || !item.actions.includes('launch')}
            aria-label={`${item.actions.includes('launch') ? 'Play' : 'Play unavailable for'} ${item.title}; ${result.source}: ${result.label}`}
            aria-describedby={active === item.id ? 'library-preview' : undefined} play={() => void play(item)} choose={() => void choose(item)}>
            <span className="cover library-poster"><Artwork src={item.cover} fallbackSrc={item.coverFallback} alt="" width="200" height="200" />
              <CompatibilityHalves item={item} />
            </span><span className="tile-title library-card-title">{item.title}</span>
          </CoverLaunch>
          <button className="library-more-button" aria-label={`More actions for ${item.title}`} aria-haspopup="dialog" onClick={event => { event.stopPropagation(); cancelTimer(); setActive(''); setManaging(item.id); }}>•••</button>
        </article>;
      })}
    </div>
    {!visible.length && <p role="status">{items.length ? 'No games match these filters.' : 'No installed games yet. Install a completed download or add a local game folder.'}</p>}
    {selected && anchor && !managing && !document.querySelector('[role="dialog"]') && <Preview item={selected} source={compatibilitySource} anchor={anchor} close={leave} retain={cancelTimer} dismiss={() => setActive('')}
      choose={() => { void choose(selected as T); }}
      play={() => { void play(selected as T); }} manage={() => { cancelTimer(); setActive(''); setManaging(selected.id); }} pending={pending === selected.id} />}
    {managed && <LibraryCard item={managed} action={name => action(managed, name)} close={() => {
      const id = managed.id; setManaging('');
      requestAnimationFrame(() => grid.current?.querySelector<HTMLButtonElement>(`[data-windows-id="${id}"] .library-more-button`)?.focus({ preventScroll: true }));
    }}>{children(managed)}</LibraryCard>}
  </>;
}
