# 实时 8D 耳机处理器

一个 Windows 桌面应用，用来把系统正在播放的声音实时处理成耳机里的 8D 环绕效果。

应用使用 WASAPI 捕获系统音频，经过轻量空间化 DSP 处理后，再输出到你选择的耳机或播放设备。

## 功能

- 系统声音实时捕获
- 标准 8D 环绕、快速环绕、强烈 8D 环绕等预设
- 可调输入增益、输出增益、旋转速度、环绕深度、环绕范围、上下幅度、上下速度、空间化强度、房间混响和限幅上限
- 中文桌面界面
- 单文件 exe 发布

## 运行

开发运行：

```powershell
dotnet run --project .\EightDRealtime.csproj
```

发布单文件 exe：

```powershell
dotnet publish .\EightDRealtime.csproj -c Release -r win-x64 --self-contained true -o .\dist-polished-ui /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

## 音频路由说明

应用有两种路由模式：

- 同设备模式：捕获除本应用外的系统声音，再输出回选中的播放设备。这个模式最方便，但原声可能仍会和处理后的声音同时存在。
- 普通回环模式：捕获一个播放设备，并输出到另一个播放设备。适合配合虚拟声卡或双设备路由使用。

如果想做到真正“只听到一个处理后的音源”，需要把音乐软件输出到虚拟声卡或另一个播放设备，再让本应用捕获该设备并输出到耳机。

## 当前限制

- 仅支持 Windows。
- 应用本身不安装驱动级虚拟声卡。
- 受保护或被系统限制的音频流可能无法捕获。
- 空间化使用轻量近似算法，不是个性化测量 HRTF。
