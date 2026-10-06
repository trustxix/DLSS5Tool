DLSS 5 Tool
===========

One window, one button: puts DLSS 5 neural rendering into any game on your PC,
including games that never shipped DLSS, and takes it out again without a trace.

It finds your installed games, reads each executable to learn what it is
(32- or 64-bit, Direct3D 8/9/10/11/12, OpenGL or Vulkan, native DLSS or not),
picks the right route, and writes exactly the files that route needs: ReShade
with add-on support, the DLSS5-Feeder add-on and its helper, the motion-vector
shaders, the RenoDX DLSS 5 add-on, the NVIDIA runtimes, dgVoodoo2 for old
Direct3D, the Vulkan layer registration. Every file written is journaled and
every file replaced is backed up, so Uninstall restores the folder byte for byte.


BEFORE YOU START
----------------

* You need an NVIDIA RTX GPU and a recent driver. The neural model is fastest
  on RTX 50; RTX 20/30/40 use a community build with a noticeably higher frame
  cost.
* The neural-rendering runtime (nvngx_dlssnr.dll) is an unreleased NVIDIA file
  that surfaced in a third-party game's early-access build. This tool does not
  ship it. It downloads it from the public mirror the community uses, verifies
  the SHA-256, and installs it only when you press Install. Read
  THIRD_PARTY_NOTICES.txt and decide for yourself.
* This is community modding on top of beta add-ons. Expect ghosting on
  estimated motion vectors and the odd game that refuses. Nothing here touches
  the game's own files; uninstall and the game is as it was.


INSTALL
-------

1. Download DLSS5Tool.exe from the latest release and check it against
   SHA256SUMS.txt.
2. Run it. No installer, no admin rights (Vulkan games ask once to register
   ReShade's layer).
3. Pick a game, read the plan on the right, press "Install DLSS 5".
4. Launch the game, press Home for the ReShade overlay, and follow the two or
   three lines the tool shows after installing (tick "Enable DLSS Neural
   Rendering", turn the game's MSAA off).

The tool checks GitHub Releases on start and offers updates with one click.


WHAT IT DOES PER GAME
---------------------

64-bit, Direct3D 11/12, ships DLSS
    RenoDX DLSS 5 add-on hooks the game's own DLSS calls.
    Writes: dxgi.dll (ReShade), renodx-dlss5.addon64, nvngx_dlssnr.dll,
    ReShade.ini

64-bit, Direct3D 11/12, OpenGL, Vulkan, no DLSS
    DLSS5-Feeder builds the DLSS contract from ReShade's depth and estimated
    motion vectors.
    Writes: ReShade, dlss5-feed.addon64, DLSS5_Feed.fx, LumeniteFX shaders,
    framework headers, RenoDX add-on, nvngx_dlssnr.dll, nvngx_dlss.dll,
    ReShade.ini, ReShadePreset.ini

64-bit Direct3D 9
    dgVoodoo2 (x64) translates to Direct3D 11, then the feeder route.
    Adds: D3D9.dll, dgVoodoo.conf, dgVoodooCpl.exe

32-bit, Direct3D 10/11, OpenGL, Vulkan (DXVK)
    32-bit feeder add-on plus the 64-bit helper process.
    Writes: dlss5-feed.addon32, shaders, and host64\ with
    dlss5-feed-host64.exe, 64-bit ReShade, the RenoDX add-on and the NVIDIA
    runtimes

32-bit Direct3D 8/9, DirectDraw, Glide
    dgVoodoo2 (x86) first, then the 32-bit route.
    Adds: the matching wrapper DLLs and dgVoodoo.conf

Vulkan (any bitness)
    ReShade's machine-wide Vulkan layer, game added to ReShadeApps.ini,
    fallback layer copied to layer\. One UAC prompt.

Not handled: 64-bit Direct3D 10, 32-bit Direct3D 12, RTX Remix games,
non-NVIDIA GPUs. The tool says so instead of guessing.

The route logic follows the DLSS5-Feeder documentation
(https://github.com/jlrouzies-fr/DLSS5-Feeder); the tool automates its install
sections, it is not a new technique.


DETECTION
---------

The executable's import table and delay-load table name the graphics API in
most games. Engines that load their renderer at run time (Unity, Source) are
read through the DLLs beside the executable; as a last resort the binary is
scanned for the API DLL names. DXVK, RTX Remix, a shipped D3D12 Agility SDK,
Streamline, an existing ReShade (and whether it is the add-on build), dgVoodoo2
and OptiScaler proxies are all recognised. When an executable touches several
APIs (many engines do) the tool applies ReShade's own preference order and
shows an API box so you can correct it.

Launchers scanned: Steam (all libraries), Epic Games, GOG Galaxy, Ubisoft
Connect, EA, Battle.net, Xbox app, Rockstar Games Launcher, Amazon Games.
Anything else: "Add game (.exe)".


SAFETY
------

* Journal: DLSS5Tool-install.json next to the game lists every file created,
  replaced or merged.
* Backups: originals go to DLSS5Tool-backup\<timestamp>\ and come back on
  uninstall; merged .ini files are restored, not re-generated.
* Conflicts (OptiScaler proxies, a second neural add-on) are renamed to
  *.disabled-by-dlss5tool, never deleted, and renamed back on uninstall.
* Verification: every third-party file has a pinned SHA-256 in
  src/DLSS5Tool/components.json; nothing is installed that does not match.
* Network: the tool talks only to the URLs in that file and to api.github.com
  for its own updates. No telemetry. --offline refuses all downloads.
* Verify button: static checks of the install plus a read of dlss5-feed.log
  and ReShade.log once the game has run. "Self-test this PC" runs
  DLSS5-Feeder's helper in --test mode: 300 real DLSS evaluates through the
  add-on and neural model, no game needed, fifteen seconds.


COMMAND LINE
------------

  DLSS5Tool --scan [--json]                   games found on this PC
  DLSS5Tool --analyze <game.exe> [--json]     what the executable tells us
  DLSS5Tool --plan <game.exe> [--api d3d11]   the exact file list, nothing written
  DLSS5Tool --install <game.exe> [--api <api>] [--offline]
  DLSS5Tool --uninstall <game.exe>
  DLSS5Tool --verify <game.exe> [--json]
  DLSS5Tool --selftest
  DLSS5Tool --components                      every third-party piece, version, availability
  DLSS5Tool --check-update | --update

Add --out <file> to capture the output. Exit codes: 0 ok, 1 error,
2 not supported / not installed, 3 usage.
Logs: %LOCALAPPDATA%\DLSS5Tool\logs\DLSS5Tool.log


SUPPLYING FILES YOURSELF
------------------------

Put them under %LOCALAPPDATA%\DLSS5Tool\local\<component>\ and the tool uses
them instead of downloading:
  nvngx-dlssnr\nvngx_dlssnr.dll
  nvngx-dlssnr-sf\nvngx_dlssnr.dll
  nvngx-dlss\nvngx_dlss.dll
  renodx-dlss5\renodx-dlss5.addon64
  lumenitefx\Shaders\...
A newer add-on build from the RenoDX Discord goes in the same way (the hash
mismatch is logged, not refused).


TROUBLESHOOTING
---------------

Nothing happens in the game, no dlss5-feed.log
    ReShade did not load: wrong executable (try another in the Executable
    box), or a launcher starts a different exe.

Sharp when still, smears when moving
    Motion vectors are not reaching DLSS. Verify checks the technique order
    and DLSS5_MV_PROVIDER.

"feature 18 create failed with 0xbad00001" in ReShade.log
    This neural model build does not run on this GPU; RTX 20/30/40 need the
    community build (the tool picks it when it detects the card).

"D3D12CreateDevice failed 0x887E0003"
    The game's D3D12\ Agility SDK folder; rename it to D3D12_off for a test.

Vulkan: half the frames unprocessed
    Turn NVIDIA Smooth Motion off for that game.

Self-test fails on every evaluate
    Driver / add-on / model combination; check the DLSS5-Feeder README's
    compatibility table.

Issues: include the Verify output and the logs named in the issue template.


BUILD FROM SOURCE
-----------------

Requirements: Windows, .NET SDK 10 (see global.json), PowerShell 7.

  .\tools\Build-Payload.ps1          pinned, hash-checked download of the redistributable pieces
  dotnet test src\DLSS5Tool.Tests    55 tests, no GPU or network needed
  dotnet run --project src\DLSS5Tool the window
  .\tools\Build-Release.ps1          dist\DLSS5Tool.exe (single file, self-contained) + SHA256SUMS.txt

DLSS5Tool.exe --help lists the headless commands; --plan <game.exe> is the
fastest way to see what the tool would do without touching anything.

Updating a third-party component: everything is pinned in
src/DLSS5Tool/components.json (URL, archive SHA-256, SHA-256 of every file
taken from it). Download the new archive, compute Get-FileHash for it and for
each extracted file, edit the component's version/url/sha256 values, run
.\tools\Build-Payload.ps1 (it refuses anything that does not match) and the
tests. Keep "distribution" honest: "bundled" only for pieces whose license
allows redistribution inside this exe, "on-demand" for the rest.

Layout:
  src/DLSS5Tool/Core/Games        launcher scanning, VDF parser, executable ranking
  src/DLSS5Tool/Core/Analysis     PE inspection and the per-game profile
  src/DLSS5Tool/Core/Planning     profile + GPU -> route + exact file list (no writes)
  src/DLSS5Tool/Core/Install      journaled installer, uninstall, Vulkan layer step, verifier
  src/DLSS5Tool/Core/Components   component catalog and the fetch/verify/cache store
  src/DLSS5Tool/Core/Update       self-update
  src/DLSS5Tool/Cli, Ui           command line and the WinForms window
  tools/                          payload and release build scripts

Pull requests: one change per PR, tests for behaviour that can be tested
without a GPU, no new runtime dependencies.

Licensed under the MIT License (see LICENSE); third-party terms in
THIRD_PARTY_NOTICES.txt.


CREDITS
-------

DLSS5-Feeder by jlrouzies-fr (https://github.com/jlrouzies-fr/DLSS5-Feeder),
RenoDX (https://github.com/clshortfuse/renodx) and the DLSS 5 add-on by Krish,
ReShade by crosire (https://reshade.me), LumeniteFX by Afzaal
(https://github.com/umar-afzaal/LumeniteFX), dgVoodoo2 by Dege
(https://github.com/dege-diosg/dgVoodoo2), and the RHI repository maintainers
for hosting the runtimes. DLSS is NVIDIA's.
