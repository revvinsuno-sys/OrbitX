# Third-party notices for OrbitX

These notices describe the components included in OrbitX source or a
framework-dependent Windows build. Original notices remain authoritative.
The root GPL-3.0 licence covers the combined application without replacing
each component's original licence or copyright.

## Orbit Store frontend

Orbit Store v1.3.5 by saawant12 and the Orbit Store contributors,
GPL-3.0-or-later. Source: <https://github.com/saawant12/orbit-store-ps5>.
The exact source commit, release archive digests and imported-file manifest are
recorded in `UI/provenance.json` and `UI/upstream-files.json`.

In source, the original licence and notices are in `UI/orbit-upstream/`.
In Windows builds they are in `licenses/OrbitStore/`.
The upstream notices also describe PS5 payload/TV-app components that OrbitX
does not bundle. They are retained as upstream notices, not a statement that
those other components are included in OrbitX.

Frontend runtime components (licence texts in
`UI/orbit-upstream/licenses/` in source, `licenses/OrbitStore/licenses/` in builds):

| Component | Pinned version | Licence text |
|---|---|---|
| React and React DOM, Meta Platforms, Inc. and affiliates | 19.3.0 | `React-MIT.txt` (MIT) |
| scheduler, Meta Platforms, Inc. and affiliates | 0.28.0 | `scheduler-MIT.txt` (MIT) |
| Vite module-preload runtime helper, VoidZero Inc. and contributors | 6.4.3 | `Vite-MIT.txt` (MIT) |
| Inter Tight, The Inter Project Authors | Fontsource 5.3.0 | `InterTight-OFL.txt` (SIL OFL 1.1) |
| Sora, Sora Project Authors | Fontsource 5.3.0 | `Sora-OFL.txt` (SIL OFL 1.1) |

Build-only npm dependencies retain their package licences and are pinned by
`UI/orbit-upstream/package-lock.json`; `node_modules` is not redistributed.

## Package readers

- **LibOrbisPkg**, maxton and contributors: LGPL version 3, commit
  `643477263b2644e0803e0f58b8726ea4e3f3b7d4` from
  <https://github.com/maxton/LibOrbisPkg>. Licence:
  `ThirdParty/LibOrbisPkg/LICENSE.txt` in source,
  `licenses/LibOrbisPkg/LICENSE.txt` in builds. The GPL v3 text is also supplied
  in the root `LICENSE`, as required by the LGPL v3 supplement.
- **LibProsperoPkg 2.6.0**, Copyright (C) 2026 SvenGDK: GPL-3.0-or-later,
  commit `748eabf1b7d17819528cabf367d8e27109d8fce3` from
  <https://github.com/SvenGDK/LibProsperoPKG>. Licence and derived-code notices:
  `ThirdParty/LibProsperoPkg/LICENSE` and `NOTICE` in source,
  `licenses/LibProsperoPkg/LICENSE` and `NOTICE` in builds.
  These include maxton's LibOrbisPkg and Powzix's GPLv3 Kraken/ooz decoder
  attribution. The combined application uses GPL version 3.

The corresponding imported reader source is included under
`ThirdParty/*/upstream`. `ThirdParty/package-reader-sources.json` records the
exact inputs. OrbitX's local changes are recorded in
`ThirdParty/LibProsperoPkg/local-streaming.patch` and local reader files outside
the imported snapshots. These sources and build project files are part of the
paired OrbitX source archive; they must remain available when distributing
the reader DLLs with OrbitX.

## SharpCompress

SharpCompress 1.0.0, Adam Hathcock and contributors, MIT. Source commit
`b6cc95af73950c914e0c6ce7ea3511528ede1121`:
<https://github.com/adamhathcock/sharpcompress>.
`licenses/SharpCompress/LICENSE.txt` contains the licence from that exact commit.
`licenses/SharpCompress/README.md` preserves the NuGet package's additional
attributions, including the Legion Of The Bouncy Castle permission notice.

## Microsoft WebView2 SDK

Microsoft.Web.WebView2 1.0.4129.50, Copyright (C) Microsoft Corporation.
The SDK's BSD-style three-clause licence and accompanying notices are copied
unmodified from the pinned NuGet package to `licenses/WebView2/LICENSE.txt`
and `licenses/WebView2/NOTICE.txt`. These cover the SDK assemblies and loader
included in the application folder. The separately installed Microsoft Edge
WebView2 Evergreen Runtime is not bundled; it has its own terms and notices.

## Separate emulator integration patches

- `integrations/kytyps5/controller-menu.patch` is based on KytyPS5 commit
  `1a053f9e13405bf34171a47aae9f2b9a6e7afd7d` from
  <https://github.com/KytyPS5/KytyPS5>. This separate patch, including OrbitX's
  patch additions, is distributed under GPL version 2; its licence is in
  `licenses/KytyPS5/LICENSE`.
- `integrations/sharpemu/f10-menu.patch` is based on SharpEmu commit
  `b6347f9f9f1c3e92ee3909e427cfba99397432c7` from
  <https://github.com/sharpemu/sharpemu>. This separate patch, including
  OrbitX's patch additions, is GPL-2.0-or-later, matching the patched source
  headers; its GPL v2 text is in `licenses/SharpEmu/LICENSE.txt`.

The build copies these patches to `KytyUpdate/` and `SharpEmuUpdate/`.
Emulator binaries are not bundled or linked into OrbitX. Their separate
installation/update workflows retain their upstream licences and notices;
distributing a patched emulator also requires its corresponding source and
applicable third-party notices, beyond the OrbitX source archive.

## External dependencies and assets

The documented framework-dependent release does not bundle .NET, Windows,
WebView2 Runtime, OSFMount, emulator binaries, WinRAR, PS5 payloads, game files,
catalogue feeds or decoding keys. Those products and data have separate terms.
Do not assume this inventory covers a self-contained build or an installer
that adds such components; review that output separately.

Original OrbitX branding/settings images and synthetic test fixtures are
covered by the project's licence unless a file states otherwise. Imported
Orbit logos/fonts retain their upstream terms. Neither the project licence
nor these notices grant rights to external commercial artwork or trademarks.
