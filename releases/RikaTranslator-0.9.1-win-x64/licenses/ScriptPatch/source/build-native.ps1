param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\adapters\script-patch\native'))
$ErrorActionPreference = 'Stop'
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'Open the MSVC x86 developer environment first.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$objects = Join-Path ([IO.Path]::GetTempPath()) ('RikaPatch-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutputDirectory,$objects -Force | Out-Null
$detours = @('creatwth.cpp','detours.cpp','disasm.cpp','image.cpp','modules.cpp') | ForEach-Object { Join-Path $PSScriptRoot ('Detours\' + $_) }
$flags = @('/nologo','/O2','/MT','/EHsc','/std:c++20','/utf-8','/DWIN32','/DUNICODE','/D_UNICODE','/DNDEBUG','/D_WIN32_WINNT=0x601',('/I' + $PSScriptRoot),('/I' + (Join-Path $PSScriptRoot 'Detours')),('/Fo' + $objects + '\'))
$libs = @('user32.lib','gdi32.lib','ole32.lib','shlwapi.lib','shell32.lib','bcrypt.lib')
& cl.exe @flags /LD @detours (Join-Path $PSScriptRoot 'KrkrPlugin\tp_stub.cpp') (Join-Path $PSScriptRoot 'patch.cpp') ('/Fe' + (Join-Path $OutputDirectory 'RikaScriptPatch.dll')) /link ('/DEF:' + (Join-Path $PSScriptRoot 'export.def')) ('/IMPLIB:' + (Join-Path $objects 'RikaScriptPatch.lib')) /DYNAMICBASE /NXCOMPAT /SAFESEH:NO @libs
if ($LASTEXITCODE -ne 0) { throw 'Patch DLL build failed.' }
& cl.exe @flags @detours (Join-Path $PSScriptRoot 'loader.cpp') ('/Fe' + (Join-Path $OutputDirectory 'RikaPatchLauncher.exe')) /link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DYNAMICBASE /NXCOMPAT /SAFESEH:NO @libs
if ($LASTEXITCODE -ne 0) { throw 'Patch launcher build failed.' }
$manifest = [ordered]@{}
foreach ($name in @('RikaPatchLauncher.exe','RikaScriptPatch.dll')) {
    $hash = [Security.Cryptography.SHA256]::Create();$stream = [IO.File]::OpenRead((Join-Path $OutputDirectory $name))
    try { $manifest[$name] = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose();$hash.Dispose() }
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'component-manifest.json'),($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
