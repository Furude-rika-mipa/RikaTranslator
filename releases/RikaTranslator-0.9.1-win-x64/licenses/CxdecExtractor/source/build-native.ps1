param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\adapters\script-extraction\native'))
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$objectDirectory = Join-Path ([IO.Path]::GetTempPath()) ('RikaCxdec-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutputDirectory,$objectDirectory -Force | Out-Null
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw '请先打开 MSVC x86 Developer Command Prompt，或设置 x86 MSVC / Windows SDK 编译环境。' }
$includes = @('Common','KrkrPlugin','Detours') | ForEach-Object { '/I' + (Join-Path $PSScriptRoot $_) }
$detours = @('creatwth.cpp','detours.cpp','disasm.cpp','image.cpp','modules.cpp') | ForEach-Object { Join-Path $PSScriptRoot ('Detours\' + $_) }
$core = @((Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Common') -Filter '*.cpp').FullName) + $detours +
    @(Join-Path $PSScriptRoot 'KrkrPlugin\tp_stub.cpp') + @((Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'CxdecExtractor') -Filter '*.cpp').FullName)
$common = @('/nologo','/O2','/MT','/EHsc','/std:c++20','/utf-8','/D_WIN32_WINNT=0x601','/DWIN32','/DNDEBUG','/DUNICODE','/D_UNICODE',('/Fo' + $objectDirectory + '\')) + $includes
$libraries = @('user32.lib','shell32.lib','ole32.lib','advapi32.lib','shlwapi.lib')
& cl.exe @common /LD @core ('/Fe' + (Join-Path $OutputDirectory 'RikaCxdecExtractor.dll')) /link ('/DEF:' + (Join-Path $PSScriptRoot 'CxdecExtractor\export.def')) ('/IMPLIB:' + (Join-Path $objectDirectory 'RikaCxdecExtractor.lib')) /SAFESEH:NO /DYNAMICBASE /NXCOMPAT @libraries
if ($LASTEXITCODE -ne 0) { throw '提取 DLL 编译失败。' }
$loader = @($detours) + @(Join-Path $PSScriptRoot 'loader.cpp')
& cl.exe @common @loader ('/Fe' + (Join-Path $OutputDirectory 'RikaCxdecWorker.exe')) /link /SAFESEH:NO /DYNAMICBASE /NXCOMPAT @libraries
if ($LASTEXITCODE -ne 0) { throw '提取工作程序编译失败。' }
$manifest = [ordered]@{}
foreach ($name in @('RikaCxdecWorker.exe','RikaCxdecExtractor.dll')) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead((Join-Path $OutputDirectory $name))
    try { $manifest[$name] = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'component-manifest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host '已生成独立提取组件与 SHA-256 清单。'
