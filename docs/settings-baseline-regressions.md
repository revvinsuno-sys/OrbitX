# Settings regression baseline comparison

Baseline commit: `0bbd0d9b2de60942ffa95efa7e8efb472721a913`.

A separate detached worktree was created at `D:\Games\PS5\.orbit-settings-baseline-0bbd0d9`. The canonical checkout's existing uncommitted redesign was retained and backed up before the polish pass. Baseline `git status --porcelain` was empty before and after the comparison; its tracked production **and test source files were unchanged**. Its Release build passed with zero warnings/errors. During the original redesign acceptance comparison (committed as `e60a194`), baseline and redesign frontend asset manifests were also identical. The subsequent graphical Settings routing correction changes the adapter bundle and has separate focused navigation validation.

The commands were run serially to avoid focus contention between native/WebView tests. Both checkouts used their Release apphosts on the same machine. The observed exceptions and failing operations matched; classification is based on executed tests, not source-file similarity.

| BrowserTests command | Classification | Matching failure | Evidence |
|---|---|---|---|
| `--sharpemu` | Confirmed baseline | `SharpEmuUiTests.cs:126`: pointer cancellation prevents launch | Baseline exit 1; redesign exit 1; matching exception and failing operation |
| `--orbit-frontend` | Confirmed baseline | `OrbitInstallationFlowTests.cs:157`: Choose a complete, available PKG or exFAT image that is not already installing or queued. | Baseline exit 1; redesign exit 1; matching exception and failing operation |
| `--settings-paths` | Confirmed baseline | `RunnableContentUiTests.cs:90`: The operation has timed out. | Baseline exit 1; redesign exit 1; matching exception and failing operation |
| `--orbit-management` | Confirmed baseline | `OrbitFrontendTests.cs:267`: Orbit UI condition timed out. | Baseline exit 1; redesign exit 1; matching exception and failing operation |

No failure among these four was introduced by Settings. None remains inconclusive. Their remediation is excluded from the Settings commit. The known failures do not count as green tests: acceptance uses the passing focused Settings tests plus the independently established baseline exceptions.

## Baseline command routing

The baseline already supports `--sharpemu` and `--orbit-frontend`, so its unmodified `PS5CatalogBrowser.BrowserTests.exe` ran those commands directly.

The starting commit contains `SettingsPathsTests.RunAsync` and `OrbitFrontendTests.ManagedActionsAsync`, but does not expose `--settings-paths` or `--orbit-management`. An external WPF runner invoked exactly those existing methods through reflection, without editing baseline sources. The runner used `ShutdownMode.OnExplicitShutdown`, dispatched the async method on the STA application dispatcher, and loaded the baseline BrowserTests assembly from its original output directory. It preloaded the output's `runtimes/win-x64/native/WebView2Loader.dll` because the external runner has no WebView2 NuGet dependency manifest. Preliminary loader failures were corrected before classification and are not counted as product failures.

The runner and baseline binaries share `AppContext.BaseDirectory` in the baseline output folder, preserving the expected frontend, native libraries and content assets. The two redesigned selectors invoke the same methods directly. In particular, `--settings-paths` fails in its existing content-lifecycle prerequisite before reaching path-specific assertions; those independent path semantics are verified separately by the passing focused Settings restart test.

`--orbit-management` times out while awaiting the existing `clearDownload` action, before its Settings assertions. `--orbit-frontend` fails during repeated installation admission, also before its Settings assertions. `--sharpemu` passes native process/preference/selector and Settings path checks before its pointer-out assertion fails.

## Retained evidence and cleanup

Raw baseline and redesign stdout/stderr are retained in the canonical checkout's ignored `UI/local-evidence/settings-acceptance/baseline-*.log` and `redesign-*.log`. `baseline-worktree-state.txt` records the clean baseline SHA; `baseline-build.log` records its build. The external runner source is retained under `baseline-runner/` for reproduction. The existing redesign backup is `preserved-redesign.zip` with its tracked diff in `preserved-redesign.patch`.

The temporary baseline worktree was unregistered by Git. Initial generated-directory cleanup was blocked; after the user's explicit script request, `D:\Games\PS5\Cleanup-OrbitSettingsBaseline.ps1` removed the baseline directory and runner bin/obj directories. Their absence was verified. Runner project source is preserved as `SettingsBaselineRunner.csproj.txt` to prevent background design-time builds from recreating its outputs. The canonical checkout, final build, screenshots, original artwork and unique validation evidence are retained.

## Graphical page follow-up comparison

The graphical page implementation reran all four commands. `--sharpemu` again failed at pointerout cancellation (`SharpEmuUiTests.cs:126`); `--settings-paths` again timed out in `CloseDuringVerificationAsync:90`; `--orbit-management` again timed out awaiting `clearDownload` (now `OrbitFrontendTests.cs:270`, before Settings assertions); the repeated `--orbit-frontend` run again failed installation admission at `OrbitInstallationFlowTests.cs:157`. The original four classifications remain **Confirmed baseline**. Logs are retained in ignored `UI/local-evidence/settings-web-page/`.

An initial frontend run also terminated with `ObjectDisposedException` from `MainViewModel.RefreshCompatibilityAsync:126`. A fresh detached worktree at the original starting commit built successfully and stayed clean. Three baseline frontend runs reached the already-known installation-admission failure rather than reproducing that intermittent shutdown timing. An external lifecycle probe then loaded each checkout's actual Release assembly, created an isolated settings model, disposed it, and invoked its public compatibility-refresh method. Both assemblies produced the same disposed-token exception at line 126. Thus the lifecycle defect is **Confirmed baseline by execution**; the exact intermittent full-suite scheduling is not claimed to be reproduced. No compatibility-lifecycle repair is included here.

The probe changed no baseline sources; its source and both exception logs are retained under `settings-web-page/lifecycle-probe/`, `baseline-disposal-probe.log` and `redesign-disposal-probe.log`. The temporary baseline was unregistered and its generated remnants removed with the previously authorised cleanup script. The probe project is retained as `.csproj.txt` after `dotnet clean`.
