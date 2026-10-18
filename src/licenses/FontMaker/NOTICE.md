# TMP 字体制作组件

- SDF 距离场计算来自 HanFengRuYue / XUnityToolkit-WebUI，MIT，Copyright (c) 2026 HanFengRuYue。对应源码提交 `8457137447b69d342f004dcf6ce0b37fb0a8701d` 的 `DistanceFieldGenerator.cs` 改为本项目命名空间；字形与字体包生成参考其 `TmpFontGeneratorService.cs`，任务隔离、校验、字符映射、存储与前端按本项目重写。原许可见 `XUnityToolkit-WebUI.LICENSE`。来源：<https://github.com/HanFengRuYue/XUnityToolkit-WebUI>。
- FreeTypeSharp 3.1.0 来自官方 NuGet，MIT。包对应源码提交 `a628eb1028605703254c469d41a6d28c25442912`。来源：<https://github.com/ryancheung/FreeTypeSharp>。
- FreeType 为 FreeTypeSharp 随包提供的原生字形渲染库，采用 FreeType License；许可与官方说明见 `FreeType.FTL.txt`、`FreeType.LICENSE.txt`。Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved.
- AssetsTools.NET 的许可另保留于 `../AssetsTools.NET.LICENSE`。本功能使用其公开 API 读写 UnityFS 字体包。
- `adapters/font-maker` 中的结构模板来自 XUnityToolkit-WebUI 4.9 随附的思源黑体 TMP 资源。原字体为 Adobe Source Han Sans，SIL Open Font License，原文见 `SourceHanSans.OFL.txt`。模板已重命名，清空原字符表、字形表、内嵌源字体引用与所有原始图集像素，保留各版本的类型树、材质与着色器结构；模板不包含可用于显示原思源字体的字形。程序不会把用户选择的字体或生成结果上传到网络。

字体模板的真实 Unity 版本、文件名和 SHA-256 记录在 `adapters/font-maker/templates.json`；未篡改 Unity 或 TMP 版本字段。生成结果的实际游戏兼容性需验证。
