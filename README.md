# Rika Translator（梨花翻译工具）

Windows 游戏实时翻译工具，使用你自己的 OpenAI 兼容 API。
当前随附版本：**0.8.1 / Windows 10、11 x64**。

0.8.1 修复 Unity 6（6000.x）版本识别，字体制作可补查旧的空版本记录；游戏库增加直接移除入口，搜索框去掉放大镜图标。

## 功能

- 游戏详情可扫描并清理可核验的残留组件，默认保留原始备份，完成恢复后可选删除旧备份。

- 配置服务地址、API Key、模型和可选请求参数；支持获取模型列表、JSON 和 SSE 返回。
- 管理游戏库，识别引擎、架构和适配能力，提供受支持路线的安装、备份与恢复。
- 提供翻译队列、缓存、人工译文、词表、游戏上下文和日志。
- Galgame 文本 Hook、透明翻译浮窗、独立字体和字号；可回填通道支持游戏内嵌。
- Unity、Ren’Py、RPG Maker 和匹配 Unreal 路线的字体设置及适配。
- Flower Knight Girl Viewer 逐句对白翻译，以及不额外调用 API 的本地中文菜单、搜索和设置。
- 能可靠预读时后台提前翻译 1–10 句，默认 3；其他游戏保持实时翻译。
- 本机 TTF/OTF 制作 TMP 字体包，提供六套 Unity 系列模板；支持取消、导出和用于游戏。
- 一键断开所有 Hook 连接；已下载更新包保留到下次打开。
- GitHub 在线检查、下载、校验和确认后安装重启；保留本地专用更新包导入，默认不备份旧程序。

本项目不提供 OCR。引擎识别不代表所有游戏或控件均可翻译，具体能力见[使用说明](docs/使用说明.md)。

## 获取和运行

从 [Releases](https://github.com/Furude-rika-mipa/RikaTranslator/releases/latest) 下载完整程序包：
[RikaTranslator-0.8.1-win-x64.zip](https://github.com/Furude-rika-mipa/RikaTranslator/releases/download/v0.8.1/RikaTranslator-0.8.1-win-x64.zip)。
完整解压后进入 `RikaTranslator-0.8.1-win-x64`，双击 `RikaTranslator.exe`。
同页附带 `SHA256SUMS.txt`，可校验下载的完整程序包。

仓库内提供[完整成品目录](releases/RikaTranslator-0.8.1-win-x64/)和 [src 源码](src/)，也可通过 Releases 下载 ZIP。
本版另附 [RikaTranslator-0.8.1-source.zip](https://github.com/Furude-rika-mipa/RikaTranslator/releases/download/v0.8.1/RikaTranslator-0.8.1-source.zip)，包含完整构建文件。
普通用户无需编译，也无需安装 .NET SDK、Python 或 Node.js。

需要 Microsoft Edge WebView2 Runtime；缺少时从[微软官方页面](https://developer.microsoft.com/microsoft-edge/webview2/)安装。
首次运行会创建自己的 `data`。分发文件没有预置个人 API Key、服务配置或游戏库。

更新内容见 [0.8.1 更新说明](docs/更新说明-0.8.1.md)。

**在线更新：** 从 0.7.7 开始，在“设置 → 程序更新”检查和下载新版，确认后安装并重启。
默认不备份旧程序，保留完整个人 `data`。0.7.7/0.7.8 可直接在线升级到本版；0.7.6 及更早版本需要先手动安装一次完整新版，更新程序文件时保留原 `data`。

升级后已经注入旧版 LunaHook 的游戏需完全退出一次。
BGI 等缓存字形的游戏修改字体或内嵌比例后，也需要保存进度并完全重启。

## 目录

```text
.github/                        问题反馈模板
docs/                           使用说明、更新说明与构建说明
releases/RikaTranslator-0.8.1-win-x64/  完整成品目录
src/                            主程序、更新器、Hook Worker 和构建工具源码
LICENSE                         主项目 GPL-3.0 许可证
THIRD-PARTY.md                   第三方组件来源与许可
```

## 构建

在 Windows 安装 .NET 10 SDK；首次构建需要还原 NuGet 依赖。
以下命令从仓库根目录执行，输出到新的空目录：

```powershell
.\src\build.ps1 -Version '0.8.1' -LooseHookSource -OutputDirectory '.\out\RikaTranslator-0.8.1-win-x64'
```

`-LooseHookSource` 将 Hook Worker 对应源码作为目录提供。
成品已包含修复后的 LunaHook；只有修改原生组件时才需要对应的 MSVC、SDK、ATL、CMake 和 Ninja。
具体说明见[构建说明](docs/构建说明.md)。

## 数据与隐私

API Key 和额外鉴权头通过 Windows DPAPI 保护，保存在本机用户数据中。
翻译原文及上下文会发送给你配置的翻译服务，费用和数据处理方式由该服务决定。
提交问题时只提供脱敏信息，保留 API Key、个人服务地址、游戏库、存档和用户数据在本机。
公开仓库不包含开发测试截图、游戏验证记录、测试结果、个人配置或升级备份。

## 开源许可与第三方组件

主项目自有程序源码采用 **GPL-3.0**，见 [LICENSE](LICENSE)。
RikaHookWorker 保留 GPL-3.0-or-later。第三方组件按各自原许可证分发，见 [THIRD-PARTY.md](THIRD-PARTY.md)。

Galgame 取词使用 [LunaTranslator / LunaHook](https://github.com/HIllya51/LunaTranslator)。
随附 `12.0.1-rika-fontfix1` 的完整上游源码、修改文件、差异和构建说明。
Unity 适配使用 [BepInEx](https://github.com/BepInEx/BepInEx) 与 [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator)；
匹配 Unreal 路线使用 [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS)。
第三方许可证、版权声明及对应源码保存在源码和成品各自的 `licenses`、`adapters/packages` 中。

字体制作参考 XUnityToolkit-WebUI 的 MIT 源码，使用 FreeTypeSharp / FreeType 与 AssetsTools.NET；原许可与模板来源见 `src/licenses/FontMaker`。
