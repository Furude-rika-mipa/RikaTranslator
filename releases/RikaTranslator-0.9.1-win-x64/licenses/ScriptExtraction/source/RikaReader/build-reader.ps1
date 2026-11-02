param(
    [string]$Python = 'python',
    [string]$Dependencies = '',
    [string]$WorkDirectory = ''
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = Join-Path $project 'licenses\ScriptExtraction\source\SiglusSSU\src'
$output = Join-Path $project 'adapters\script-extraction\multi'
if (-not (Test-Path -LiteralPath $source)) { throw '缺少锁定版本的 SiglusSSU 源码，见 licenses/ScriptExtraction/NOTICE.md。' }
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $project ('obj\script-reader-' + [guid]::NewGuid().ToString('N')) }
[IO.Directory]::CreateDirectory($WorkDirectory) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
$previousPath = $env:PYTHONPATH
try {
    $env:PYTHONPATH = (@($Dependencies, $source, $previousPath) | Where-Object { $_ }) -join [IO.Path]::PathSeparator
    $modulePaths = @($Dependencies, $source) | Where-Object { $_ }
    $encodedPaths = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($modulePaths) -Compress)))
    $bootstrap = "import sys,json,base64,runpy;sys.dont_write_bytecode=True;sys.path[:0]=json.loads(base64.b64decode('$encodedPaths'));runpy.run_module('PyInstaller',run_name='__main__')"
    & $Python -c $bootstrap --noconfirm --clean --onefile --console --name RikaScriptReader --distpath $output --workpath (Join-Path $WorkDirectory 'work') --specpath $WorkDirectory --paths $source --collect-submodules siglus_ssu --add-data ((Join-Path $source 'siglus_ssu\const.py') + ';siglus_ssu') --exclude-module PIL --exclude-module numpy --exclude-module tkinter (Join-Path $PSScriptRoot 'multi_engine.py')
    if ($LASTEXITCODE -ne 0) { throw '多引擎剧本读取组件构建失败。' }
    $hash = (Get-FileHash -LiteralPath (Join-Path $output 'RikaScriptReader.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output 'component-manifest.json'), (@{ 'RikaScriptReader.exe' = $hash } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
} finally { $env:PYTHONPATH = $previousPath }
