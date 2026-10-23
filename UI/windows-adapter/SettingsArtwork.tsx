import catalogue from './assets/settings/tiles-catalogue-downloads.png';
import security from './assets/settings/tiles-passwords-browser.png';
import installation from './assets/settings/tiles-installation-sharpemu.png';
import emulation from './assets/settings/tiles-kyty-appearance.png';
import metadata from './assets/settings/tiles-language-metadata.png';
import about from './assets/settings/tiles-about.png';

type Region = [number, number, number, number];
type Artwork = { sheet: string; size: [number, number]; square: Region; wide: Region; wideAlign?: 'xMid' | 'xMax' };
// Viewports select the supplied variants without modifying the source sheets.
const artwork: Record<string, Artwork> = {
  sources: { sheet: catalogue, size: [1536, 1024], square: [56, 57, 429, 428], wide: [523, 57, 960, 427] },
  downloads: { sheet: catalogue, size: [1536, 1024], square: [56, 529, 429, 426], wide: [523, 529, 960, 426] },
  passwords: { sheet: security, size: [1536, 1024], square: [49, 88, 449, 414], wide: [531, 88, 957, 414] },
  browser: { sheet: security, size: [1536, 1024], square: [49, 522, 449, 415], wide: [531, 522, 957, 415] },
  installation: { sheet: installation, size: [1536, 1024], square: [42, 67, 405, 405], wide: [468, 67, 1030, 405] },
  sharpemu: { sheet: installation, size: [1536, 1024], square: [42, 501, 405, 446], wide: [468, 501, 1030, 446] },
  kyty: { sheet: emulation, size: [1536, 1024], square: [46, 58, 443, 417], wide: [533, 87, 959, 388], wideAlign: 'xMid' },
  appearance: { sheet: emulation, size: [1536, 1024], square: [46, 516, 443, 418], wide: [533, 520, 959, 403], wideAlign: 'xMax' },
  language: { sheet: metadata, size: [1536, 1024], square: [52, 95, 428, 400], wide: [530, 95, 958, 400] },
  metadata: { sheet: metadata, size: [1536, 1024], square: [52, 532, 428, 415], wide: [530, 532, 958, 415] },
  about: { sheet: about, size: [2172, 724], square: [69, 94, 557, 535], wide: [739, 94, 1366, 535], wideAlign: 'xMid' },
};

export function SettingsArtwork({ category, square }: { category: string; square: boolean }) {
  const art = artwork[category], region = square ? art.square : art.wide;
  return <span className="settings-tile-art" data-variant={square ? 'square' : 'wide'} aria-hidden="true">
    <svg viewBox={region.join(' ')} preserveAspectRatio={`${square ? 'xMid' : art.wideAlign ?? 'xMin'}YMid slice`}>
      <defs><clipPath id={`settings-art-${category}`}><rect x={region[0]} y={region[1]} width={region[2]} height={region[3]} /></clipPath></defs>
      <image href={art.sheet} width={art.size[0]} height={art.size[1]} clipPath={`url(#settings-art-${category})`} />
    </svg>
  </span>;
}
