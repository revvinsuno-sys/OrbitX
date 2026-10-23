import type { Game, Release } from '../orbit-upstream/src/types';
import { Select } from '../orbit-upstream/src/Select';

export const compatibilityStatuses = {
  'doesnt-boot': { label: "Doesn't boot", color: '#9ca3af' },
  logo: { label: 'Logo', color: '#f87171' },
  'main-menu': { label: 'Main menu', color: '#fb923c' },
  'in-game': { label: 'In game', color: '#4ade80' },
  untested: { label: 'Not tested', color: '#64748b' },
  unavailable: { label: 'Status unavailable', color: '#9ca3af' },
};
export const sharpEmuStatuses = {
  nothing: { label: 'Nothing', color: '#ff5b63' }, boots: { label: 'Boots', color: '#ff8258' },
  menus: { label: 'Menus', color: '#ffc13b' }, ingame: { label: 'Ingame', color: '#7b9cff' },
  playable: { label: 'Playable', color: '#4bd16e' }, untested: { label: 'Not tested', color: '#94a3b8' }, unavailable: { label: 'Unavailable', color: '#94a3b8' },
};
export function badgeStatus(status: string) {
  return ({ 'doesnt-boot': 'nothing', logo: 'boots', 'main-menu': 'menus', 'in-game': 'ingame' } as Record<string, string>)[status] || status;
}
export function badgeLabel(status: string) {
  return ({ 'doesnt-boot': "Doesn't boot", logo: 'Logo', 'main-menu': 'Main menu', 'in-game': 'In game' } as Record<string, string>)[status]
    ?? sharpEmuStatuses[status as keyof typeof sharpEmuStatuses]?.label ?? 'Unavailable';
}
export function statusColor(status: string, source: 'official' | 'local') {
  const key = badgeStatus(status) as keyof typeof sharpEmuStatuses;
  const color = sharpEmuStatuses[key]?.color ?? sharpEmuStatuses.unavailable.color;
  return source === 'official' ? `color-mix(in oklab, ${color} 48%, #05070f)` : `color-mix(in oklab, ${color} 45%, #fff)`;
}
export function catalogCompatibilityColor(status: string) {
  return sharpEmuStatuses[badgeStatus(status) as keyof typeof sharpEmuStatuses]?.color ?? sharpEmuStatuses.unavailable.color;
}
// Library-only palette. Website colours are muted; status text retains contrast.
export const libraryCompatibilityPalette = {
  'doesnt-boot': { local: '#fb7185', website: '#84505d' },
  logo: { local: '#fb7185', website: '#84505d' },
  'main-menu': { local: '#fbbf24', website: '#806330' },
  'in-game': { local: '#4ade80', website: '#41684e' },
  untested: { local: '#94a3b8', website: '#94a3b8' },
  unavailable: { local: '#94a3b8', website: '#94a3b8' },
};
export function libraryCompatibility(item: { compatibilityOverride: string; websiteCompatibility: string; compatibilityVersionMatch: string; websiteReports: { status: string; versionMatch: string }[] }, source: 'kyty' | 'local') {
  const meaningful = (value: string) => value in compatibilityStatuses && value !== 'untested' && value !== 'unavailable';
  const local = source === 'local';
  const status = local ? (meaningful(item.compatibilityOverride) ? item.compatibilityOverride : 'untested')
    : meaningful(item.websiteCompatibility) ? item.websiteCompatibility : 'untested';
  const key = status as keyof typeof compatibilityStatuses;
  return { status: key, label: compatibilityStatuses[key].label, source: local ? 'My test' : key === 'untested' ? 'Not tested' : 'Website',
    color: libraryCompatibilityPalette[key][local ? 'local' : 'website'], older: local ? item.compatibilityVersionMatch === 'older'
      : item.websiteReports.some(report => report.status === status && report.versionMatch === 'older') };
}
export function releaseCompatibility(release: Release): keyof typeof compatibilityStatuses {
  const status = (release as Release & { windowsCompatibility?: string }).windowsCompatibility;
  return status && status in compatibilityStatuses ? status as keyof typeof compatibilityStatuses : 'unavailable';
}
export function gameCompatibility(game: Game) {
  const local = game.releases.find(release => (release as Release & { windowsCompatibilityLocal?: boolean }).windowsCompatibilityLocal);
  if (local) return releaseCompatibility(local);
  return game.releases.map(releaseCompatibility).find(status => status !== 'untested' && status !== 'unavailable')
    ?? (game.releases.some(release => releaseCompatibility(release) === 'untested') ? 'untested' : 'unavailable');
}
export function compatibilityLabel(game: Game, emulator: CompatibilityEmulator = 'any') {
  if (emulator === 'any') return compatibilityStatuses[gameCompatibility(game)].label;
  const field = emulator === 'sharpemu' ? 'windowsSharpEmuCompatibility' : 'windowsWebsiteCompatibility';
  const values = game.releases.map(release => (release as CompatibilityRelease)[field] || 'unavailable');
  const status = values.find(value => value !== 'untested' && value !== 'unavailable')
    ?? (values.some(value => value === 'untested') ? 'untested' : 'unavailable');
  return badgeLabel(status);
}
export function gameCompatibilityResult(game: Game) {
  const releases = game.releases as (Release & { windowsCompatibility?: string; windowsCompatibilityLocal?: boolean; windowsCompatibilityVersionMatch?: string })[];
  const localRelease = releases.find(release => release.windowsCompatibilityLocal);
  const meaningful = (status: string) => status in compatibilityStatuses && status !== 'untested' && status !== 'unavailable';
  const reportedRelease = releases.find(release => meaningful(release.windowsCompatibility ?? 'unavailable'));
  const release = localRelease ?? reportedRelease;
  const status = release ? releaseCompatibility(release) : gameCompatibility(game);
  const source: 'local' | 'website' = localRelease ? 'local' : 'website';
  const key = status as keyof typeof compatibilityStatuses;
  return { status: key, label: compatibilityStatuses[key].label, source,
    color: libraryCompatibilityPalette[key][source], older: localRelease?.windowsCompatibilityVersionMatch === 'older' };
}
export function StatusBadge({ status, title, inline = false, local = false, emulator = 'KytyPS5' }: { status: string; title?: string; inline?: boolean; local?: boolean; emulator?: string }) {
  const key = status in compatibilityStatuses ? status as keyof typeof compatibilityStatuses : 'unavailable';
  const info = compatibilityStatuses[key] ?? sharpEmuStatuses[badgeStatus(status) as keyof typeof sharpEmuStatuses] ?? sharpEmuStatuses.unavailable;
  return <span className={`windows-compatibility${inline ? ' compatibility-inline' : ''}`} data-compatibility={key} data-status={badgeStatus(status)} data-source={local ? 'local' : 'official'}
    title={title}
    aria-label={`${emulator} compatibility: ${info.label}${local ? ', local result' : ''}`}><span aria-hidden="true">●</span> {info.label}{local ? ' · Local' : ''}</span>;
}
export function CompatibilityBadge({ game, inline = false }: { game: Game; inline?: boolean }) {
  const status = gameCompatibility(game);
  const local = game.releases.some(release => (release as Release & { windowsCompatibilityLocal?: boolean }).windowsCompatibilityLocal);
  const release = game.releases.find(release => (release as Release & { windowsCompatibilityLocal?: boolean }).windowsCompatibilityLocal) as (Release & { windowsCompatibilityVersion?: string; windowsCompatibilityVersionMatch?: string; windowsWebsiteCompatibility?: string }) | undefined;
  const details = release ? `Your result · ${release.windowsCompatibilityVersion || 'Version unknown'} · ${release.windowsCompatibilityVersionMatch === 'current' ? 'Tested on current version' : release.windowsCompatibilityVersionMatch === 'different' ? 'Different version' : 'Current version not verified'} · Website: ${compatibilityStatuses[(release.windowsWebsiteCompatibility ?? 'unavailable') as keyof typeof compatibilityStatuses]?.label ?? 'Status unavailable'}` : '';
  return <StatusBadge status={status} local={local} inline={inline} title={local ? details : 'KytyPS5 community compatibility · overall result across reported operating systems'} />;
}
type CompatibilityRelease = Release & { windowsWebsiteCompatibility?: string; windowsKytyLocalCompatibility?: string;
  windowsSharpEmuCompatibility?: string; windowsSharpEmuLocalCompatibility?: string };
type CompatibilityStatusField = "windowsWebsiteCompatibility" | "windowsKytyLocalCompatibility" | "windowsSharpEmuCompatibility" | "windowsSharpEmuLocalCompatibility";
const compatibilityRows = [
  ['Official KytyPS5', 'windowsWebsiteCompatibility', 'official'], ['Official SharpEmu', 'windowsSharpEmuCompatibility', 'official'],
  ['Local KytyPS5', 'windowsKytyLocalCompatibility', 'local'], ['Local SharpEmu', 'windowsSharpEmuLocalCompatibility', 'local'],
] as const;
export function CompatibilityColumns({ release }: { release: Release }) {
  const values = release as CompatibilityRelease;
  return <span className="windows-compatibility-column">{compatibilityRows.map(([label, field, source]) => {
    const status = values[field] || 'unavailable';
    const key = badgeStatus(status);
    const name = label.includes('SharpEmu') ? 'SharpEmu' : 'KytyPS5';
    const title = badgeLabel(status);
    return <span className="windows-compatibility-row" key={label}><span>{label}</span><span className="windows-compatibility-result" data-source={source} data-status={key}
      aria-label={`${name} ${source} compatibility: ${title}`}>{title}</span></span>;
  })}</span>;
}
export type CompatibilityEmulator = 'any' | 'kyty' | 'sharpemu';
export function CompatibilityDot({ game, emulator = 'any' }: { game: Game; emulator?: CompatibilityEmulator }) {
  const find = (field: CompatibilityStatusField) => {
    const values = game.releases.map(release => (release as CompatibilityRelease)[field] || 'unavailable');
    return values.find(status => status !== 'untested' && status !== 'unavailable') ??
      (values.some(status => status === 'untested') ? 'untested' : 'unavailable');
  };
  const parts = emulator === 'kyty'
    ? [['KytyPS5', find('windowsWebsiteCompatibility')]] as const
    : emulator === 'sharpemu'
      ? [['SharpEmu', find('windowsSharpEmuCompatibility')]] as const
      : [['KytyPS5', find('windowsWebsiteCompatibility')], ['SharpEmu', find('windowsSharpEmuCompatibility')]] as const;
  const title = parts.map(([name, status]) => `Official ${name}: ${badgeLabel(status)}`).join('\n');
  return <span className={`windows-compatibility-dot windows-compatibility-halves${parts.length === 1 ? ' windows-compatibility-solo' : ''}`}
    data-compatibility={emulator === 'any' ? gameCompatibility(game) : parts[0][1]} title={title} aria-label={title}>
    {parts.map(([name, status]) => <i key={name} style={{ background: catalogCompatibilityColor(status) }} aria-hidden="true" />)}
  </span>;
}
export function EmulatorFilter({ value, onChange, inlineLabel }: { value: CompatibilityEmulator; onChange: (value: CompatibilityEmulator) => void; inlineLabel: boolean }) {
  return <Select label="Emulator" inlineLabel={inlineLabel} value={value} onChange={value => onChange(value as CompatibilityEmulator)}
    options={[{ value: 'any', label: 'Any' }, { value: 'kyty', label: 'KytyPS5' }, { value: 'sharpemu', label: 'SharpEmu' }]} />;
}
export function CompatibilityFilter({ value, onChange, inlineLabel, emulator = 'any' }: { value: string; onChange: (value: string) => void; inlineLabel: boolean; emulator?: CompatibilityEmulator }) {
  const options = emulator === 'sharpemu'
    ? Object.entries(sharpEmuStatuses).map(([status, info]) => ({ value: `sharp:${status}`, label: info.label }))
    : Object.entries(compatibilityStatuses).map(([status, info]) => ({ value: status,
      label: emulator === 'any' ? status === 'doesnt-boot' ? "Doesn't boot / Nothing" : status === 'logo' ? 'Logo / Boots'
        : status === 'main-menu' ? 'Main menu / Menus' : status === 'in-game' ? 'In game / Playable' : info.label : info.label }));
  return <Select label="Compatibility" inlineLabel={inlineLabel} value={value} onChange={onChange}
    options={[{ value: '', label: 'All compatibility' }, ...options]} />;
}

export function matchesCompatibilityFilter(game: Game, filter: string, emulator: CompatibilityEmulator = 'any') {
  if (emulator === 'kyty') return game.releases.some(release => (release as CompatibilityRelease).windowsWebsiteCompatibility === filter);
  if (emulator === 'sharpemu') {
    const status = filter.startsWith('sharp:') ? filter.slice('sharp:'.length) : filter;
    return game.releases.some(release => (release as CompatibilityRelease).windowsSharpEmuCompatibility === status);
  }
  const aliases: Record<string, { kyty: string[]; sharpEmu: string[] }> = {
    'doesnt-boot': { kyty: ['doesnt-boot'], sharpEmu: ['nothing'] },
    logo: { kyty: ['logo'], sharpEmu: ['boots'] },
    'main-menu': { kyty: ['main-menu'], sharpEmu: ['menus'] },
    'in-game': { kyty: ['in-game'], sharpEmu: ['ingame', 'playable'] },
  };
  const group = aliases[filter];
  if (!group) return game.releases.some(release => {
    const values = release as CompatibilityRelease;
    return values.windowsWebsiteCompatibility === filter || values.windowsSharpEmuCompatibility === filter;
  });
  return game.releases.some(release => {
    const values = release as CompatibilityRelease;
    return group.kyty.includes(values.windowsWebsiteCompatibility ?? '')
      || group.sharpEmu.includes(values.windowsSharpEmuCompatibility ?? '');
  });
}
