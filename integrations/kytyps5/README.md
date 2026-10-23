# KytyPS5 controller menu and cache-folder patch

`controller-menu.patch` is a local customisation based on upstream commit
`1a053f9e13405bf34171a47aae9f2b9a6e7afd7d`. It adds the Xbox Guide / PlayStation PS
button or F10 menu, controller/keyboard/pointer navigation, input capture, and the
existing Kyty window-quit action. Continue is selected by default. The menu states
that the game keeps running; guest CPU execution is not suspended. Exiting loses
unsaved progress just as closing the game window does.

The same patch adds `--shader-cache-folder <absolute-path>` through ConfigOptions
and the existing Vulkan pipeline cache writer. Its default stays `_PipelineCache`
under the emulator working directory. Cache files remain per-title and retain the
existing GPU/driver signature validation and atomic save behavior. It does not
introduce offline shader pre-compilation, move caches, or change GPU-driver caches.

The patch is kept in this repository so the KytyPS5 source can remain on clean
upstream `main`. The helper stages only the patch when applying it and reverses
both the index and working files when removing it. It does not commit, stash,
reset, or push. Applying requires a clean checkout. Incompatible upstream
changes or edits to patched files cause a failure for review, without overwriting
them.

KytyPS5 Settings shows the installed build and the latest official GitHub release.
Startup checks are read-only. Use Check for updates to refresh, then Update KytyPS5
when the upstream versions differ (the custom menu `-dirty` suffix is ignored).
The explicit update fetches the matching release source, checks out its tag temporarily,
applies this patch, builds the configured Windows tree, then restores and
fast-forwards the clean `main` checkout. It installs from a staging directory.
The runtime is replaced only after the patched executable is
built and the installed hash matches the staged hash. Existing package files
are backed up until verification succeeds, and the previous files are restored
if replacement fails. Kyty must be closed. A dirty/non-main/non-upstream source,
an incompatible patch, an unavailable toolchain, or a failed build leaves the
installed runtime unchanged. The update runs in the background and blocks new
game launches, executable-path edits and application closure until it finishes.
The helper discovers CMake, Ninja and the compiler from the existing CMake cache,
then loads the installed Visual Studio C++ environment; it does not require a
developer terminal. It removes quotes from the updater process's inherited PATH
before loading that environment and checks that initialization succeeded. Before
compiling, it reconfigures the existing build with FetchContent downloads enabled
so new upstream dependencies can be populated, even if the cache was offline.
Missing tools or patch conflicts are reported in the settings
status and bounded update output. A failed check clears stale update availability;
a failed update can be retried after correcting its reported cause.

Manual check/build instructions remain available below for diagnosing an
update that was skipped or refused.

From the OrbitX repository root:

```powershell
$source = 'D:\Games\PS5\Tools\KytyPS5-source'
$patch = '.\integrations\kytyps5\Set-ControllerMenuPatch.ps1'

# Remove first if you previously left the patch applied.
& $patch -RepositoryPath $source -Action Remove
git -C $source pull --ff-only
& $patch -RepositoryPath $source -Action Check
& $patch -RepositoryPath $source -Action Apply
```

Build using the current upstream Windows build instructions and your existing
toolchain configuration. The affected targets and focused check are:

```powershell
cmake --build "$source\_Build\windows" --target launcher emulator_menu_tests host_input_tests --parallel 8
ctest --test-dir "$source\_Build\windows" -R '^(emulator_menu|host_input)$' --output-on-failure
```

Close running Kyty processes, install the rebuilt emulator and its matching runtime
dependencies using the upstream install procedure, then leave the source clean:

```powershell
cmake --install "$source\_Build\windows" --prefix 'D:\Games\PS5\Tools\KytyPS5'
& $patch -RepositoryPath $source -Action Remove
git -C $source status --short
```

For a source moved to a new directory, reconfigure its build cache using the
upstream instructions before building. A CMake build directory from the old path
cannot be used unchanged. Run these checks after every upstream update; patch
applicability alone does not establish runtime compatibility.

OrbitX restores My Library after the launched process exits. This
requires the browser to remain open and needs no Kyty-specific IPC or protocol.
Controller Guide-button delivery varies by driver and Windows settings; F10 is
available as a keyboard alternative. Physical controller and in-game acceptance
must be distinguished from the automated menu and process-return checks.

The menu uses explicit selection and confirmation (UI-INT-007), visible selection
markers and outlines (UI-A11Y-002/005), keyboard equivalents and cancellation
(UI-A11Y-001), and a layout shared by rendering and pointer hit testing
(UI-INT-011). Native ImGui screen-reader support is not established by these checks.

Validated on the base commit above: Windows Release build; `emulator_menu` and
`host_input` CTest checks; real child-process exits with codes 0 and 17 returning
to Classic and Orbit My Library; and idempotent patch check/apply/remove cycles.
The helper also refuses application over unrelated dirty state. Physical
controller delivery and the rendered menu inside a running game remain manual
acceptance checks. Installed binary and patch hashes are recorded in the local
Kyty installation's `build-info/controller-menu-build.json`.

OrbitX retains Git main-branch and dirty-build suffixes when reading
version metadata; the focused test exercises every possible buffer split of a
tag and a customised main build so updates cannot be mistaken for the last tag.
