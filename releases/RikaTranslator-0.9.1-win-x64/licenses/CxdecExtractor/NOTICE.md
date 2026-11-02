# KiriKiri / Hxv4 剧本提取组件

来源：https://github.com/YeLikesss/KrkrExtractForCxdecV2
基准提交：1829eb0abb8740f2db5b7fc497b7483372b5b6a0
许可证：AGPL-3.0，原 LICENSE 与各文件版权声明保留。

Rika 改动：独立命令行启动、任务输出目录、Job Object 生命周期、资源大小限制、仅导出剧本形态的资源、取消时结束自有进程、关闭原调试窗口和提示框。
提取组件独立运行，不接收或调用 AI 服务。当前适配 32 位 Hxv4 / PSB 场景；不修改游戏 EXE、认证或封包，不提供译文回写。
完整对应源码及编译说明在 source/。Rika 自有界面和任务代码保留其原 GPL-3.0，第三方组件保留其各自许可；完整 Rika 对应源码同时提供。

PSB 数据格式读取参考 GARbro 的 ArcPSB.cs（Copyright (C) 2016 morkt，MIT）。原许可保留在 GARbro-MIT.txt；PSB 场景提取代码在主项目 ScriptTranslation/PsbScenarioReader.cs 中。
