# Rika Script Patch loader

RikaPatchLauncher / RikaScriptPatch are GPL-3.0 components. This folder contains their complete corresponding source, including the build script and bundled dependencies.

- Stream interception and import stubs adapted from crskycode/KrkrPatch, commit 587261001bf95feab4f0f1cbcbd22cfdadb97e31 (GPL-3.0): https://github.com/crskycode/KrkrPatch
- Microsoft Detours, commit 1dbd4ea281d73d478aa8d09a8b83fbd265f715f7 (MIT). Exact source and copyright notice in Detours/; https://github.com/microsoft/Detours
- Public KiriKiri stream ABI declarations match krkrz/krkrz, commit fd5c4baa6a2ef5978db1bd043634351f48667daf. Copyright (C) 2000 W.Dee and contributors; see KiriKiri-LICENSE.txt. https://github.com/krkrz/krkrz

Modifications: exact SHA-256 resource matching, bounded payload/map validation, independent patch directory, version validation, optional Chinese UI font substitution and private TTF/OTF file loading, and local diagnostic status. No original game executable, assets or saves are bundled with the loader source.

## Build

Use an x86 MSVC 2022 developer environment with the Windows SDK and PowerShell. Run:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\build-native.ps1 -OutputDirectory .\native

Both native outputs use /MT and only Windows system libraries; no separate VC runtime download is needed. Rebuilding produces a new component-manifest.json. No online download is used by this script.

The main application's PSB writer is independently implemented in ScriptTranslation/PsbScenarioWriter.cs; existing PSB layout references retain the GARbro MIT notice. FreeMote code is not included or linked.

Resource-patch format 2 adds x86/x64 launchers and a process-local, read-only file-open redirector, SJIS tunnel decoding, guarded internal conversion-table support and GDI font substitution. It loads rewritten resource copies without overwriting game files or altering licensing/authentication. Build both architectures with build-multi.ps1 (MSVC 2022 + Windows SDK); resource writers and third-party references accompany generated patches under ScriptExtraction.
