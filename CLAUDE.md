# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目是什么

把联想键盘上那个"一按就打开 `oneclick.lenovo.com.cn`"的键改成自定义功能。

关键在于：**这不是联想软件干的，是键盘固件自己在键盘通道上"敲"出了整个网址**。所以拦截点是低级键盘钩子（`WH_KEYBOARD_LL`）——走标准输入通道意味着软件层能完整拦截改写，不用改固件。

固件模拟的宏序列（实测抓包得出，`tools/KeySpy` 可复现）：

```
Win↓  R↓  Win↑  R↑        ← 约 12ms 内完成，唤起"运行"对话框
  ↓ 约 90ms
h t t p : / / o n e c l i c k . l e n o v o . c o m . c n
  ↓ 约 1.1s
Enter
```

字符间隔 0.03~15ms，而人打字最快 60ms 以上——差两个数量级，这是识别的依据。

`tests\StateMachineTests.cs` 的 `RealFirmwareMacro()` 逐键码复现了这条波形（含 Shift+OEM 标点键、尾部 `Enter↑`）。改 `tools\MacroTest\MacroTest.cs` 的 `ReplayOnce()` 时要同步改它，否则回归测试验的就不是真实输入了。

## 构建与测试

```powershell
.\build.ps1                      # 版本号 v0.0.0-dev
.\build.ps1 -Version v1.0.0      # 指定版本
```

产物：`dist\OneKeyTool-<版本>-win-x64.zip`（包内 `OneKeyTool.exe` + `config.ini` + `README.md` + `LICENSE`）。**Release 只挂这一个 zip**。

`build.ps1` 三步：编译主程序 → 编译并**运行**状态机回归测试（不通过即中断构建）→ 打包。所以「跑构建」就是「跑测试」。

### 只跑测试 / 跑单个用例

测试是自包含控制台程序，手写断言，无测试框架。独立编译运行（注意 `$src` 清单要与 `build.ps1` 一致，且必须先跑一次 `build.ps1` 以生成 `dist\_gen\BuildInfo.cs`）：

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /utf8output `
  /main:OneKeyTool.Tests.TestRunner /reference:System.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /out:dist\_tools\StateMachineTests.exe `
  dist\_gen\BuildInfo.cs src\Program.cs src\Native.cs src\Config.cs src\InputSender.cs `
  src\MacroEngine.cs src\Detector.cs src\ActionRunner.cs src\AutoStart.cs src\MainForm.cs `
  tests\StateMachineTests.cs
dist\_tools\StateMachineTests.exe
```

**没有按名字过滤的开关**。要跑单个用例，把 `tests\StateMachineTests.cs` 的 `Main()` 里 `Run(...)` 那批调用注释掉，只留目标那条。

## 架构

### 核心设计：状态机与 Win32 分离

| 文件 | 职责 |
|---|---|
| `src/MacroEngine.cs` | **纯逻辑状态机**。不碰 Win32、不做注入；时间由调用方注入（`Feed(ev, nowMs)` / `Tick(nowMs)`），暂扣事件通过 `TakeReplay` 交还 |
| `src/Detector.cs` | 适配层。在**专用线程**上装钩子、翻译事件、喂引擎、重放注入、驱动超时与钩子自检、异步日志 |
| `src/InputSender.cs` | `SendInput` 封装 |
| `src/ActionRunner.cs` | 执行动作（url / program / hotkey / none） |
| `src/MainForm.cs` | 设置界面 + 托盘常驻 |
| `src/Config.cs` | `config.ini` 读写（原子写：临时文件 + `File.Replace`） |

### 必须守住的架构不变量

**1. 观察模式与拦截模式共用同一个 `MacroEngine`。**
观察模式只是把 `Swallow` 降级为 `Pass` 且不重放、不执行动作。这条性质由 `tests\StateMachineTests.cs` 里的「观察模式与拦截模式一致」用例保证。

这是项目最需要保证的性质：README 教用户"先在观察模式验证识别准确，再关掉观察模式启用拦截"。**一旦两者漂移，用户会看到观察模式报告识别成功、切到拦截模式却不触发**——直接毁掉信任。历史上这里曾有两套并行状态机（`_state` / `_obsState`）导致漂移，抽成 `MacroEngine` 就是为了从根上消除它。**不要再引入第二份识别逻辑。**

**2. 钩子跑在专用线程上，那条线程除了消息循环什么都不做。**
低级键盘钩子受 `LowLevelHooksTimeout`（默认 300ms）限制，回调超时后 **Windows 会静默摘掉钩子**，程序毫不知情（`_hook` 句柄仍非零，`Start()` 曾返回 `true`）。用户看到的现象是"时灵时不灵"，极难排查。

所以钩子由 `Detector.HookThreadProc` 那条专用线程安装并跑消息循环。**别再把它挪回界面线程**：钩子回调在"安装它的那个线程"上执行，界面线程上的任何耗时操作（写配置、`Process.Start`）都会顺带把钩子一起拖住。配套的四条：

- `Detector.Log()` 只入队 + `Set()`，磁盘写入在后台线程 `WriteLogs()`（2MB 轮转）——回调里永远不做磁盘 IO。
- 超时收尾由该线程自己的 `SetTimer`（`TickIntervalMs = 15`）驱动，界面定时器不再参与识别。
- `TickIntervalMs` 直接决定"单独按 Win"多等多久（实际延迟 ≈ `WinWaitMs` + 本值），改动它要同步 README 与 `config.ini` 里的延迟说明。
- 每 10 分钟在空闲（`HeldCount == 0`）时 `ReinstallHook()` 自检重装一次，把被静默摘掉的钩子自愈回来。**必须先摘后装**——两个钩子并存会让每个按键被处理两次。

**3. 自动重复事件计入 `_held` 与上限，但不刷新超时窗口。**
这是曾经的 P0 回归：重复分支既进 `_held` 又顺延 `_deadline`，导致按住某键时状态永不超时、`_held` 无界增长（实测 2007 个事件），松手后一次性重放上千个按键。`MacroEngine.cs` 里相关注释标明了这一点，改动该处务必对照「宏中长按不无界增长」用例。

**4. 重放安全靠 `dwExtraInfo` 魔数。**
`InputSender.Magic` = `0x4F4B5459`（"OKTY"）。钩子无条件忽略带此标记的事件，所以"放弃拦截 → 重放"不会自我循环。重放走 `KEYEVENTF_SCANCODE` + 原始扫描码，`Scan == 0` 时回退虚拟键。

**5. 命中后要吞掉被丢弃序列里"按下未抬起"的键。**
实测固件在回车之后还会补一个 `Enter↑`，而触发命中的那个 `Enter↓` 并不在 `_held` 里（`HandleMacro` 在 `_held.Add` 之前就 `return` 了）。`Confirm()` 因此要把它们记进 `_dangling`，在 Idle 状态下连抬起事件一起吞掉，否则系统会收到一个没有对应按下的孤立 KEYUP。由「命中后尾部抬起事件」用例保证。注意 `_dangling` 的赋值必须在两种模式下完全一致——观察模式只由 `Detector` 把 `Swallow` 降级为放行，引擎内部状态不能分叉。

### 测试钩子

`Detector` 暴露 `FeedForTest` / `TickForTest` / `ReplayedForTest` / `ReplayListForTest` / `HeldCountForTest`，**没有 `#if` 保护，会编译进正式产物**（`internal`，且 `Detector` 本身不导出）。构造函数第三参 `testMode: true` 时不装钩子、不真正注入。

## 硬约束

这些是项目性质决定的，改动时不要破坏：

- **零依赖**。只用系统自带 `csc.exe`（.NET Framework 4.0），只引用 `System.dll` / `System.Drawing.dll` / `System.Windows.Forms.dll`。不引入 NuGet、不引入 .NET SDK、不加新程序集引用。
- **不含任何网络代码**。`src/` 里不应出现 `System.Net` / `WebClient` / `HttpWebRequest` / `ServicePointManager`。这是有意为之：自动更新功能曾被实现又**被刻意移除**，理由是会引入下载源被篡改、校验被绕过等攻击面，而本程序持有键盘钩子权限。唯一的网络入口是「关于」对话框里调 `Process.Start` 打开浏览器。
- **`src/BuildInfo.cs` 不能进 `build.ps1` 的 `$src`**。它是 IDE/手工编译的兜底默认值；`build.ps1` 会生成 `dist\_gen\BuildInfo.cs` 覆盖它。两者**必须保持字段同步**——往 `BuildInfo` 加常量时要同时改 `build.ps1` 里那个 here-string 模板，否则正式构建会报 `CS0117`。
- **`build.ps1` 必须存为 UTF-8 with BOM**。它含中文字面量，而 PowerShell 5.1 读无 BOM 文件会按 ANSI 代码页解析，导致乱码甚至语法错误。
- **`.gitattributes` 规定 `*.ps1` 用 CRLF**。编辑后确认换行符没被改成 LF（`git ls-files --eol` 查看）。
- **源文件清单只有一处**：`build.ps1` 的 `$src`。新增 `src/*.cs` 只需改这里，测试编译复用同一份。CI 直接调 `build.ps1`，不存在第二份清单。
- **`dist/` 在 `.gitignore` 里**，永不提交。

## 发布

推 `v*` tag → GitHub Actions 自动编译并建 Release：

```bash
git tag -a v1.0.0 -m "..."
git push origin v1.0.0
```

### 几个踩过的坑

- **CI 跑的是被 tag 指向的那个提交里的 `release.yml`**，不是 `main` 上的。工作区或 `main` 上的改动没提交就推 tag，构建出来的是旧代码。
- **删 Release 不会删 tag**（GitHub 的「also delete the tag」有时不弹）。留着 tag 会让后续重推同名 tag 被拒（`already exists in the remote`）。删 Release 后务必确认远程 tag 也清了：`git ls-remote --tags origin`。
- **Release 标题由 `release.yml` 里 `发布 Release` 那一步的 `name:` 字段决定**，缺省时 `softprops/action-gh-release` 拿 tag 名当标题。历史 tag 的代码里没有这个字段，所以重发旧 tag 得到的仍是光秃秃的 `v1.0.0`。

## 读这个仓库的注意事项

源码注释、README、`config.ini` 全是中文，且文件是 UTF-8（部分无 BOM）。**用 PowerShell 的 `Get-Content` 读会按 ANSI 代码页解析而显示成乱码**——那是显示问题，文件本身没问题。用 Read 工具，或 `Get-Content -Encoding UTF8`。

写含中文的文件时同理：`Set-Content` / `Add-Content` 默认用系统 ANSI 代码页，需要显式 `-Encoding UTF8`。
