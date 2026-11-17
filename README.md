# SokuVoice

東方非想天則 1.10a（th123）的外部语音托盘启动器。纯托盘程序，读取游戏进程状态后播放对应语音，并附带设置界面与实时调试窗口。

## 功能

- 托盘常驻，启动后自动跟随 `th123` 进程
- 设置项：音量、监控周期（PollMs）、玩家模式、系统音开关
- 联机限制：`NetplayRestrict=1` 时在联机对局中抑制语音
- 实时调试窗口，可查看命中的语音文件与匹配过程
- 语音目录可通过 `VoiceDir` 指定，默认 `th123v`

## 目录结构

```
SokuVoice.exe      编译产物
SokuVoice.cs       源码（C# / WinForms）
SokuVoice.ini      运行配置
app.ico            程序图标
build.bat          一键编译脚本
th123v/            语音包
  ├─ 0..19/        角色语音
  └─ sys/          系统语音（如 0.1.wav、1.1.wav）
```

## 编译

需要 Windows 与 .NET Framework 4.x 自带的 `csc`：

```bat
build.bat
```

等价于：

```bat
%WINDIR%\Microsoft\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /target:winexe /win32icon:app.ico /out:SokuVoice.exe SokuVoice.cs
```

## 使用

1. 把 `SokuVoice.exe`、`SokuVoice.ini`、`th123v\` 放在同一目录。
2. 启动 `SokuVoice.exe`，托盘出现图标即表示运行中。
3. 先启动 th123，语音会随对局状态播放；托盘菜单可开关语音、打开设置与调试窗口。
4. 修改 `SokuVoice.ini` 后重启程序生效。

### SokuVoice.ini

| 键 | 说明 |
| :--- | :--- |
| `Volume` | 音量（0-100） |
| `PollMs` | 进程状态轮询间隔（毫秒） |
| `PlayerMode` | 玩家模式选择 |
| `SystemSounds` | 是否播放系统语音（1/0） |
| `NetplayRestrict` | 联机对局时是否抑制语音（1/0） |
| `Enabled` | 总开关 |
| `VoiceDir` | 语音目录，默认 `th123v` |
| `ProcName` | 目标进程名，默认 `th123` |

## 说明

语音素材取自《東方非想天則》，版权归上海アリス幻樂団 / ZUN 所有，本仓库仅用于本地个人使用，请勿用于商业用途。
