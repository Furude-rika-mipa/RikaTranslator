# Rika Cxdec Worker

独立的 32 位 KiriKiri / Hxv4 剧本提取进程，基于 YeLikesss/KrkrExtractForCxdecV2。
上游与本目录中的衍生代码采用 AGPL-3.0，原版权声明与 LICENSE 保留。

在已设置 MSVC 2022 x86 与 Windows SDK 环境的 PowerShell 中运行：

```powershell
.\build-native.ps1
```

用法：`RikaCxdecWorker.exe GAME_EXE JOB_DIRECTORY ARCHIVE_NAME`。
GAME_EXE 应为已经能正常启动的游戏；本组件不修改认证或游戏 EXE。
输出只写入指定的独立任务目录。调用者负责建立临时游戏副本，不应传入含有用户存档的目录。
工作程序关闭时通过 Job Object 结束自己创建的游戏进程；不连接或关闭用户已经运行的游戏。
只导出 PSB/MDF 和可识别的文本资源，不导出图片、音频或字体。
不接收翻译服务凭据、不联网、不调用 AI，不提供译文回写。

`upstream.json` 记录来源与基准提交。Common、Detours 和 KrkrPlugin 保留上游随附源码及各文件中的许可声明。
