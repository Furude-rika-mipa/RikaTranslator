# Rika Translator（梨花翻译工具）

Windows 游戏翻译工具，支持实时取词、用户自己的 AI 服务，以及受支持游戏的剧本翻译。当前版本：**0.9.0 / Windows 10、11 x64**。

## 下载

从 [Releases](https://github.com/Furude-rika-mipa/RikaTranslator/releases/tag/v0.9.0) 下载 [完整程序 ZIP](https://github.com/Furude-rika-mipa/RikaTranslator/releases/download/v0.9.0/RikaTranslator-0.9.0-win-x64.zip)，解压后运行 `RikaTranslator.exe`。也可查看仓库中的[完整程序目录](releases/RikaTranslator-0.9.0-win-x64/)。

0.7.7 及更新版本可在“设置 → 程序更新”检查、下载并安装。更新会保留本地 `data`，首次运行将创建自己的配置。需要 Microsoft Edge WebView2 Runtime。

## 主要功能

- 游戏库、实时翻译、词表、字体设置与受支持游戏的翻译浮窗或内嵌显示。
- 受支持的 Unity 游戏可制作 TMP 字体包；部分游戏支持预读后续对白。
- 剧本翻译支持已适配的 KiriKiri / Hxv4、WillPlus / AdvHD、YU-RIS、ExHIBIT、Escu:de、SiglusEngine 和 BGI / Ethornell 格式。可翻译所选对白、当前剧本或全部待译，支持多 AI 服务分担和保存进度。
- 使用支持本地 stdio MCP 的外部助手翻译已授权范围，每批最多条数可调整。也可将已完成译文生成独立汉化补丁，并选择字体。

引擎识别不代表所有游戏和资源版本都兼容；请参阅[使用说明](docs/使用说明.md)和 [0.9.0 更新说明](docs/更新说明-0.9.0.md)。BGI 游戏副本已验证中文显示，其他新增引擎已验证资源回填，实际游戏排版和完整流程仍需逐项确认。

## 源码与许可

[src](src/) 包含构建文件、主程序源码、组件和相应许可；具体构建步骤见[构建说明](docs/构建说明.md)。主项目采用 [GPL-3.0](LICENSE)，第三方组件保留各自许可，见 [THIRD-PARTY.md](THIRD-PARTY.md) 与 `src/licenses`。

发布包不包含个人 API Key、游戏库、译文或游戏文件。请勿在问题反馈中公开密钥及个人信息。
