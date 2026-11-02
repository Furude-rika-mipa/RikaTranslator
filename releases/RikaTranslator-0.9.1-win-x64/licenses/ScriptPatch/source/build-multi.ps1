param([string]$ToolchainRoot = '', [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\adapters\script-patch\multi'))
$ErrorActionPreference = 'Stop'
$objects = Join-Path ([IO.Path]::GetTempPath()) ('RikaMultiPatch-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($objects) | Out-Null
$previous = @{ Path=$env:Path; INCLUDE=$env:INCLUDE; LIB=$env:LIB }
try {
    foreach ($arch in @('x86','x64')) {
        $out = Join-Path $OutputDirectory $arch
        [IO.Directory]::CreateDirectory($out) | Out-Null
        $obj = Join-Path $objects $arch
        [IO.Directory]::CreateDirectory($obj) | Out-Null
        $compiler = 'cl.exe'
        if ($ToolchainRoot) {
            $vc = Join-Path $ToolchainRoot 'VC\Tools\MSVC\14.44.35207'
            $sdk = Join-Path $ToolchainRoot 'Windows Kits\10'
            $compiler = Join-Path $vc "bin\Hostx64\$arch\cl.exe"
            $env:Path = (Split-Path $compiler) + ';' + (Join-Path $sdk 'bin\10.0.26100.0\x64') + ';' + $previous.Path
            $env:INCLUDE = (Join-Path $vc 'include') + ';' + ((@('ucrt','shared','um','winrt') | ForEach-Object { Join-Path $sdk "Include\10.0.26100.0\$_" }) -join ';')
            $env:LIB = (Join-Path $vc "lib\$arch") + ';' + (Join-Path $sdk "Lib\10.0.26100.0\ucrt\$arch") + ';' + (Join-Path $sdk "Lib\10.0.26100.0\um\$arch")
        } else {
            if (-not $env:VCToolsInstallDir -or -not $env:WindowsSdkDir -or -not $env:WindowsSDKVersion) { throw 'Use a Visual Studio developer shell or pass -ToolchainRoot (the pinned local MSVC/SDK directory).' }
            $compiler = Join-Path $env:VCToolsInstallDir "bin\Hostx64\$arch\cl.exe"
            $sdkVersion = $env:WindowsSDKVersion.TrimEnd('\')
            $env:Path = (Split-Path $compiler) + ';' + (Join-Path $env:WindowsSdkDir "bin\$sdkVersion\x64") + ';' + $previous.Path
            $env:LIB = (Join-Path $env:VCToolsInstallDir "lib\$arch") + ';' + (Join-Path $env:WindowsSdkDir "Lib\$sdkVersion\ucrt\$arch") + ';' + (Join-Path $env:WindowsSdkDir "Lib\$sdkVersion\um\$arch")
        }
        $flags = @('/nologo','/O2','/MT','/EHsc','/std:c++20','/utf-8','/DUNICODE','/D_UNICODE','/DNDEBUG','/D_WIN32_WINNT=0x601',('/I'+$PSScriptRoot),('/Fo'+$obj+'\'))
        $detours = @('creatwth.cpp','detours.cpp','disasm.cpp','image.cpp','modules.cpp') | ForEach-Object { Join-Path $PSScriptRoot "Detours\$_" }
        $libs = @('user32.lib','gdi32.lib','ole32.lib','shlwapi.lib','shell32.lib','bcrypt.lib')
        & $compiler @flags /LD @detours (Join-Path $PSScriptRoot 'multi_patch.cpp') ('/Fe'+(Join-Path $out 'RikaScriptPatch.dll')) /link ('/DEF:'+(Join-Path $PSScriptRoot 'multi_export.def')) ('/IMPLIB:'+(Join-Path $obj 'RikaScriptPatch.lib')) /DYNAMICBASE /NXCOMPAT @libs
        if ($LASTEXITCODE -ne 0) { throw "Multi patch DLL ($arch) build failed." }
        & $compiler @flags @detours (Join-Path $PSScriptRoot 'multi_loader.cpp') ('/Fe'+(Join-Path $out 'RikaPatchLauncher.exe')) /link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /DYNAMICBASE /NXCOMPAT @libs
        if ($LASTEXITCODE -ne 0) { throw "Multi patch launcher ($arch) build failed." }
        $manifest = [ordered]@{}
        foreach ($name in @('RikaScriptPatch.dll','RikaPatchLauncher.exe')) { $manifest[$name] = (Get-FileHash -LiteralPath (Join-Path $out $name)).Hash.ToLowerInvariant() }
        [IO.File]::WriteAllText((Join-Path $out 'component-manifest.json'),($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    }
} finally { $env:Path=$previous.Path; $env:INCLUDE=$previous.INCLUDE; $env:LIB=$previous.LIB }
