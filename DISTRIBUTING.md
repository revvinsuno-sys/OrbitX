# Distributing OrbitX builds

OrbitX's combined application is distributed under GNU GPL version 3; see
[LICENSE](LICENSE), [NOTICE.md](NOTICE.md) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Keep all of these and the
`licenses/` directory with every binary distribution. The source's imported
components retain their original licences and copyright notices.

## Build and source pair

Use a clean, committed checkout and the requirements in the [README](https://github.com/revvinsuno-sys/OrbitX/blob/main/README.md).
From PowerShell at the repository root, run:

```powershell
pwsh -File scripts/Publish-OrbitX.ps1
```

The script makes a framework-dependent Windows x64 publish and a source ZIP
from that exact Git commit under `artifacts/OrbitX-<commit>-win-x64/`. It refuses
uncommitted source changes or an existing output directory. It produces:

- `OrbitX-<commit>-win-x64.zip`: the application, licence/notice files and
  `SOURCE.txt` recording the full source commit and matching archive name.
- `OrbitX-<commit>-source.zip`: all tracked source at that commit, including
  imported frontend/package-reader code, local modifications, dependency
  lockfile, build scripts, licences and instructions.
- `SHA256SUMS`: hashes for the two archives.

Publish both ZIPs together from the same download/release location, with
source accessible without extra restrictions or a private-repository login.
Keep the exact source archive available for as long as you offer the build.
An evolving `main` link alone does not identify the corresponding source of
an older binary. If you distribute local changes, commit them and regenerate
both archives. Distributing binaries to private recipients also requires
satisfying the applicable source obligations to those recipients.

`dotnet publish` alone copies the licence/notice files but does not create a
corresponding-source archive. Use the script for release packaging. Do not
replace the generated `licenses/` folder with a shorter summary.

## Scope of the package

The script uses `--self-contained false`: .NET Desktop Runtime and WebView2
Runtime are installed separately. Do not add emulator binaries, OSFMount,
catalogue keys/data, credentials, personal settings or commercial artwork to
the package. A self-contained build, installer or patched emulator release
requires a separate dependency/source/notice review.

This process does not assert that every application test passes. Check the
README's known validation limitations and complete the relevant release tests
before announcing a supported binary release. The script packages source and
notices; it does not publish a GitHub release or upload binaries automatically.
