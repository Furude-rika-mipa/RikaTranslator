# LunaHook 内嵌字号修复（Rika 0.7.4）

2026-10-08，针对《放課後シンデレラ２》修改内嵌字号后中文不显示的反馈。

**当前状态：完整 LunaHook 32/64 位 DLL 已用 MSVC 重新编译，已通过字体和原生注入回归，随 Rika 0.7.4 程序目录提供。** 本目录保留未压缩的修复源码、构建和测试材料，没有生成新的便携或更新 ZIP。

## 原因与复现

v12.0.1 的 `DCFontSwitcher` 从当前字体的 `TEXTMETRIC.tmHeight` 再次计算比例，并把替换字体持续选入绘字 DC。《放課後シンデレラ２》记录的 BGI UTF-16 通道对应的上游适配会同时拦截 `GetTextExtentPoint32W` 和 `TextOutW`，测量和绘制都会再次缩放。源码检查与原生 GDI 复现表明，这条路径会累计缩小或放大文字。[上游字体实现](https://github.com/HIllya51/LunaTranslator/blob/v12.0.1/src/NativeImpl/LunaHook/LunaHook/hijackfuns.cc)、[BGI 适配](https://github.com/HIllya51/LunaTranslator/blob/v12.0.1/src/NativeImpl/LunaHook/LunaHook/engine32/BGI.cpp)。

固定初始 32 像素黑体，循环执行真实测量、绘制，并读取 DIB 中的文字像素：

| 比例 | 修复前 | 修复后，每平台连续 1000 次 |
| --- | --- | --- |
| 70% | 第一轮字高 16，20 轮后只剩 2 像素 | 始终 22 像素，中文像素正常 |
| 100% | 字高稳定，但 20 轮创建 40 个字体对象 | 始终 33 像素，与 Windows 原字体 metrics 相符；同名字体无需创建替换对象 |
| 120% | 第一轮字高 46，12 轮后 2456，文字超出画布 | 始终 38 像素，中文像素正常 |

这里的字高是 Windows 实际返回的 metrics，可能包含字体内部间距，因此不等于简单整数比例。检查同时比较 LOGFONT、测量高度、中文 glyph 索引和画布像素。

## 修改内容

- 用原始 `LOGFONTW` 保存字体基准；读取到自己之前创建的字体时先恢复基准，再计算比例。保留宽度、正负字号、方向、粗细等原设置。
- 创建字体与绘制字体的拦截共享基准记录，避免两条路径同时启用时重复缩放。
- 字体缓存同时比较原始基准和目标设置，防止整数取整后相同的字号混用不同基准；使用互斥锁，忽略已删除或失效的字体句柄。
- 修复每次绘字都重新创建同名字体的问题；同一设置持续绘字不再增加 GDI 字体对象。
- 完整复制 UTF-16 字体名并限制长度；ANSI 创建入口转为 Unicode 字体配置，保证中文字体名能传递到正确接口。
- 保留上游持续选择替换字体的行为，避免直接恢复旧句柄影响原有 Mogeko Castle 兼容逻辑。

改动仅在 `src/NativeImpl/LunaHook/LunaHook/hijackfuns.cc`。完整替换文件为本目录的 `hijackfuns.cc`，统一差异为 `font-resize.patch`；来源、前后 SHA-256 和当前部署状态见 `patch-manifest.json`。

## 测试结果与边界

32 位和 64 位分别通过 85 项场景检查，合计 170 项。覆盖 50/70/100/120/200%、正负字号、显式字宽、四种字体创建 API、实时切换字号、中文/英文名称的字体切换、恢复原始字体、两个不同基准取整到同一高度、删除缓存字体、长字体名、8 个绘字线程并发，以及连续 1000 次绘字后 GDI 对象数不增长。

字体测试直接编译修改文件中实际的字体管理、创建和 BGI 绘字函数，调用 Windows GDI 在独立内存画布上绘制中文；没有另写一份缩放算法。对照测试使用未修改的 v12.0.1 同一函数。LLVM-MinGW 和正式 DLL 所用 MSVC 均通过 170 项场景检查。新编译的完整 32/64 位 DLL 另通过 83 项实际注入、取词、配置 ABI、模拟翻译、取消/恢复及组件校验回归。所有翻译请求使用本机模拟服务，没有使用真实 API Key。

精简结果：`test-results.json`、`msvc-test-results.json`；DLL 架构、依赖、大小和 SHA-256 见 `native-build.json`。项目内报告：`tests/artifacts/font-resize-074/{upstream,fixed}/verification.json`、`msvc-verification.json` 以及 `tests/artifacts/galgame-native-88904635b9a44ade9c0b5d62f44347e6/verification.json`。测试源码的副本在本目录 `tests`。

完整 DLL 已在《放課後シンデレラ２》原始日文 EXE 的游戏副本验证。重启后 70%、100%、120% 的固定中文译文均可见，原始截图中同一对白的白色文字像素高度分别为 15、21、26 像素；继续切换对白后 70% 的文字像素与首次显示一致，GDI 对象数保持 36。翻译只连接本机模拟服务，没有使用真实 API Key。实际加载的是 0.7.4 的 `LunaHook32.dll`。`game-test-results.json` 记录验证范围和字形缓存限制，程序目录的 `validation` 保存未修改截图。已验证开场对白，尚未覆盖完整剧情、存档往返及其他 BGI 游戏。

项目内复现：

```powershell
python tests/font_resize_regression.py
python tests/font_resize_regression.py --source tests/hook-research/font-fix/LunaTranslator-12.0.1 --label fixed --fixed
```

单独使用测试副本时，指定已解压的上游/修复源码目录、已准备的 LLVM-MinGW 编译器 `bin` 和输出目录：

```powershell
python tests/font_resize_regression.py --source <完整源码目录> --compiler-dir <LLVM-MinGW的bin目录> --output-dir <结果目录> --fixed
```

验证所用 LLVM-MinGW 20260908、源码与编译器下载 SHA-256 保存在清单。编译器仅用于独立测试，不能把这些测试 EXE 当成完整 LunaHook DLL。

## 完整 DLL 构建

需要已配置的 MSVC x86/x64 开发命令环境、Windows SDK、ATL 头文件、CMake 和 Ninja。对应的 [MinHook v1.3.4 源码](https://github.com/TsudaKageyu/minhook/releases/tag/v1.3.4) 已完整保存在本目录 `minhook-source`。`build-native.ps1` 检查上游源码包与替换文件的 SHA-256，解压到全新目录，替换唯一修改文件，再使用上游 CMake 构建完整 Hook；MinHook 默认使用随目录提供的本地源码。

只准备完整源码，可在当前环境运行：

```powershell
.\build-native.ps1 -Architecture x86 -PrepareOnly -BuildRoot <新的空目录>
```

后续在对应架构的 MSVC 开发环境中分别执行：

```powershell
.\build-native.ps1 -Architecture x86 -MinHookSourceDirectory <MinHook源码目录> -BuildRoot <新的空目录>
.\build-native.ps1 -Architecture x64 -MinHookSourceDirectory <MinHook源码目录> -BuildRoot <另一个新的空目录>
```

生成结果在构建目录 `output`。构建脚本不会自动替换应用 DLL 或更改 `native-manifest.json`。本次完整构建使用 MSVC 19.44.35229、SDK 10.0.26100.0、MinHook v1.3.4、Release/静态 CRT，不依赖额外的 MSVC 运行库 DLL。两个正式 Hook DLL 与组件校验清单已同步；上游 Host 和注入程序保持原文件。

## 游戏重启与字形缓存

升级后，旧游戏进程必须完全退出一次，再用新工具启动或连接；只断开 Hook 不会卸载旧 DLL。内嵌比例仍支持 50–200%，100% 对应游戏原字号。

《放課後シンデレラ２》会缓存已经绘制的字形。实时调整比例后，缓存的旧字形仍保持原大小，新字形按新比例绘制，可能出现大小混用；修改字体或字号后，保存进度并完全重启游戏可清除缓存。BGI 设置界面和保存提示已明确说明这一点。缓存现象与累计缩放缺陷分开验证；本修复不会强制重启游戏或修改游戏资源。

## 许可证与对应源码

上游为 LunaTranslator v12.0.1，保留 GPL v3 原许可证及版权声明，全文在 `COPYING`。完整原始源码包 `../LunaTranslator-v12.0.1-source.zip` 与本目录修改文件、差异、构建说明、MinHook 源码和测试源码共同保存修复 DLL 的对应材料；同时保留原有第三方声明。两个修改 DLL 标记为 `12.0.1-rika-fontfix1`，校验值见应用的 `native-manifest.json`。
