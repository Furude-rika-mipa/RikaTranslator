# Multi-engine script reader

The Rika offline reader is GPL-3.0, as is the main project. It reads user-owned
game files locally. No game resources, translation data, keys or title-key
database are included. Extraction does not use a translation API or launch a game.

Third-party components and references:

- GARbro, copyright morkt, MIT; archive parsing and bounded ACP decompression
  adapted from commit `b09ee4570ccb1daf6ac56710ee8934dc0b8baeb0`.
  https://github.com/morkt/GARbro
- VNTranslationTools, copyright arcusmaximus, MIT; YSTB command / attribute
  parsing and dialogue grouping adapted from commit
  `36fbe909d02f2e9bb187443e03e2df595f56bfec`.
  https://github.com/arcusmaximus/VNTranslationTools
- BGI ARC20 / DSC decoding also follows GARbro's `Ethornell/ArcBGI.cs` (MIT).
  Compiled-v1 operand schemas and string-stack handling follow
  VNTranslationTools' `EthornellV1Disassembler.cs` (MIT). Newer stack-based
  intrinsic handling follows msg-tool's BGI parser (GPL-3.0). Corresponding
  reference sources accompany this directory. The supported original script
  group is `Archive/data01*.arc`; third-party translation archives and user data
  are excluded.
- SiglusSceneScriptUtility, copyright Jirehlov, used under 0BSD (upstream offers
  Unlicense OR 0BSD), commit `198dbdb207b3a3126f1c6e8267051db5178c605c`.
  A pinned source copy is under `source/SiglusSSU`; the reader uses its pure
  Python scene decoding and instruction trace. No native Rust extension is bundled.
  https://github.com/Jirehlov/SiglusSceneScriptUtility
- msg-tool, copyright lifegpc and contributors, GPL-3.0; ExHIBIT RLD instruction
  layout reference. The Rika RLD 3 reader additionally derives its table locally
  from the selected game's bitmap resource and INI configuration.
  https://github.com/lifegpc/msg-tool
- pefile 2024.8.26, copyright Ero Carrera, MIT, for bounded PE resource access.
  https://github.com/erocarrera/pefile
- CPython 3.12 runtime, Python Software Foundation License. Its full license
  and third-party notices accompany the frozen reader.
- PyInstaller 6.22.3 bootloader, GPL-2.0-or-later WITH Bootloader-exception.
  The exception permits distributing applications built with the bootloader.
  https://github.com/pyinstaller/pyinstaller

Build: install `pyinstaller==6.22.3` and `pefile==2024.8.26` into a local Python
3.12 environment, then run `tools/ScriptExtraction/build-reader.ps1 -Python <python>`.
The optional `-Dependencies` argument supports a directory installed with pip
`--target`. Reader source: `tools/ScriptExtraction/multi_engine.py`.
The resulting reader and its SHA-256 manifest belong in
`adapters/script-extraction/multi/`. Python is not required on end-user computers.

Resource patch writers now support the verified BGI compiled-v1, AdvHD 2.2 UTF16, YU-RIS 494, ExHIBIT RLD 3, Escu:de message tables and Siglus Scene.pck formats. They validate current source rows, preserve untranslated text and instructions, and write only new resource copies. The standalone native loader redirects read-only resource opens inside the game process. It does not modify licensing/authentication code. AdvHD operand schemas and the SJIS character-tunneling technique are adapted from VNTranslationTools (MIT); Siglus rebuilding uses the pinned SSSU (0BSD). Corresponding writer/reference sources accompany this directory. Newer AdvHD graphics operands are locally validated against the verified game. Other engine versions may be rejected.
JSON / TXT exports are translation interchange files, not installable patches.
