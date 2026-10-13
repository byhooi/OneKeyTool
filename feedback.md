# OneKeyTool 代码审查反馈

审查日期：2026-10-13
审查范围：`src/` 全部源码、`tools/` 诊断工具、`build.ps1`、`.github/workflows/release.yml`、文档

---

## 一、对既有 5 条反馈的核实

### ① 钩子回调中同步写日志

**结论：成立，但真正的风险点比"慢"更严重。**

`Detector.cs:562` 的 `Log` 使用 `File.AppendAllText`（每次打开 → 写 → 关闭 → 刷盘）。关键细节在于调用位置：

- `Detector.cs:168` 的 `lock (_gate)` **包住了** `Handle`，而所有 `Log` 调用都在 `Handle` 内部。因此同步磁盘 IO 发生在**钩子线程 + 持锁**双重条件下。
- `Detector.cs:164` 的 `DebugKeys` 分支对每个按键事件写一次日志；`Detector.cs:175` 的异常分支也写。
- `Detector.cs:574` 的 `Trace` 事件是**在钩子线程上同步调用**的。目前没有任何订阅者（已确认），但这是一个隐藏契约——将来若有 UI 层代码订阅它，会随机崩溃。

真正的危害不是"按键延迟"，而是：低级键盘钩子受 `LowLevelHooksTimeout`（默认 300ms）限制，回调超时后 **Windows 会静默摘掉钩子**，而程序完全不知情（`_hook` 句柄仍然非零，`Start()` 曾经返回过 `true`）。用户看到的现象是"程序时灵时不灵"，极难排查。触发条件是日志目录被杀软实时扫描，或日志位于网络盘 / 同步盘。

**对原建议的修正**：不只是"放入有界队列由后台线程批量落盘"，还必须**把 `Log` 移出 `lock (_gate)`**，并在 `Trace` 上明确标注"在钩子线程调用"。

---

### ② 按键重放没有使用记录的扫描码

**结论：成立，但原反馈抓错了重点。**

`InputSender.cs:23` 确实用 `MapVirtualKey` 重新推导扫描码，`KeyEvent.Scan`（`InputSender.cs:89`）是一个**从未被读取的死字段**——设计意图没有落地。

但比"特殊键盘布局不一致"严重得多的问题在 `InputSender.cs:30`：**`SendInput` 的返回值完全没有检查**。`SendInput` 返回实际注入的事件数，在 UIPI 拦截下返回 0。当前台窗口是管理员权限时（任务管理器、管理员 PowerShell、部分安装程序），非管理员权限的本程序注入会**全部失败**。此时：

> 用户按 Win 键 → 被暂扣 → 超时 `Abort` → `FlushHeld` → `SendInput` 失败 → **按键永久消失**

这直接推翻了 README 中"因此本工具不会破坏 Win 键、Win+R、方向键等任何正常功能"的承诺。这才是本条中最应优先修复的问题。

**附带发现的小 bug**：`InputSender.cs:77` 把 `0x0D`（Enter）硬编码为扩展键。主键盘 Enter 的扫描码是 `1D`（非扩展），只有小键盘 Enter 才是 `E0 1C`。配置 `Ctrl+Enter` 这类快捷键时会注入错误的组合。

---

### ③ 构建脚本递归删除整个 dist 目录

**结论：成立，但严重性在 5 条中最低。**

`build.ps1:24` 确实是 `Remove-Item 'dist' -Recurse -Force`。缓解因素是 `dist/` 已在 `.gitignore` 中，是明确的产物目录。实际风险有二：

1. 用户手动放进 `dist/` 的文件会被误删；
2. `$ErrorActionPreference = 'Stop'` 下，如果 `dist/` 内有被占用的文件（例如用户正开着那个 zip），脚本会直接报错中断。

---

### ④ 缺少可重复运行的状态机回归测试

**结论：成立，且是 5 条中价值最高的一条。**

项目没有任何测试工程（无 `.csproj` / `.sln` / `tests/`）。`MacroTest` 是端到端注入真实按键，依赖真实钩子、真实时序、前台窗口，**不可能在 CI 中运行**，也不覆盖"错误前缀""超时边界"这类分支。

**原反馈未看到的深层问题**：`Detector.cs` 中存在**两套状态机**。

- 拦截态：`_state` / `_sigPos` / `_prefixPos`，第 190–477 行
- 观察态：`_obsState` / `_obsSigPos` / `_obsPrefixPos`，第 210–308 行

两份实现逻辑相似但**并不等价**。例如观察态 `case 1`（`Detector.cs:246`）允许"Win 后直接跟 R"，拦截态在 `HandleWinDown`（`Detector.cs:358`）也允许，但两边的超时重置、自动重复处理、特征串回退策略（`_sigPos = (ev.Vk == _sig[0]) ? 1 : 0`）都是各写各的。

这直接威胁产品主流程：README 让用户"先在观察模式验证识别准确，再关闭观察模式启用拦截"。如果两套逻辑发生漂移，就会出现**观察模式报告识别成功、切换到拦截模式却不触发**（或反之）——用户会认为工具坏了。这是当前架构中最容易出线上事故的地方。

---

### ⑤ 本地构建和发布流程维护两份源文件清单

**结论：成立，问题明确。**

`build.ps1:33` 和 `release.yml:46` 各有一份 `$src` 数组。`RELEASE.md:124` 自己都承认了这个坑。

而且**已经出现了不一致**：`build.ps1` 打包时是 `if (Test-Path 'LICENSE')`，`release.yml` 是无条件 `Copy-Item 'LICENSE'`。

---

## 二、原反馈遗漏的问题（按严重性排序）

### A. `Config.Save` 非原子写（`Config.cs:164`）

`File.WriteAllText` 直接覆盖目标文件。写到一半崩溃或断电会导致配置文件损坏。而 `Config.Load`（`Config.cs:76`）读失败时 `catch { return c; }` **静默回退到默认值**，用户全部设置无声丢失。

**建议**：改为写临时文件 + `File.Replace`；`Load` 失败时至少记录一条日志。

---

### B. 单独按 Win 键的延迟比文档描述的更糟

`HandleWinDown`（`Detector.cs:345`）在每次 Win 自动重复时都顺延 deadline。因此用户**按住** Win 键时，暂扣会一直持续到 `_held.Count >= 64`（`Detector.cs:342`）才放弃——按 30 次/秒的重复率计算，约 **2 秒内开始菜单不弹、所有按键被吞**。

README 的表格里只写了"暂扣达 64 个自动放弃并放行"，没有提及这个延迟。

**好消息**：真实宏的 Win↓ → R↓ 只隔约 10ms（`MacroTest.cs:146`），所以 `WinWaitMs` 从 300 降到 50–80 完全够用，能基本消除这个体验问题。

---

### C. `GetAsyncKeyState` 的 `0x4000` 位依赖未定义行为

`Detector.cs:290` 与 `Detector.cs:420` 使用 `Native.GetAsyncKeyState(ev.Vk) & 0x4000` 判断自动重复。

MSDN 只定义了 bit 0（上次调用后被按过）和 bit 15（当前按下）。**`0x4000` 没有文档定义**，属于依赖未定义行为。

**建议**：改为显式追踪（记录"同 VK 连续 down 且中间无 up"），既不依赖未定义位，也能被单元测试覆盖。

---

### D. 文档声称的"自动化测试"并不存在

README 写着"已用自动化测试逐项验证"，但 CI（`release.yml`）中没有任何测试步骤，`MacroTest` 需要人工启动程序、人工切换窗口、人工肉眼观察。

这是文档与事实不符，可由 ④ 的修复来兑现。

---

### E. 日志无上限，`DebugKeys` 不持久化

- `Program.cs:48` 只在启动时删除一次日志，运行期间无限追加。
- `DebugKeys` 能从 ini 读取（`Config.cs:115`），但 `Save`（`Config.cs:130-165`）不写回——界面上改不了，保存后还会从 ini 中消失。
- 同理 `Signature`（`Config.cs:58`）硬编码。README 说"换键盘要改 `Detector.cs`"，但既然已有 ini 机制，增加一个 `Signature=` 配置项成本很低。

---

### F. 死代码与文档过时

- `LastMacroEvents`（`Detector.cs:60`）没有任何消费者。
- `Trace`（`Detector.cs:78`）没有任何订阅者。
- README / RELEASE 中的目录树仍写作 `keyboard/`（`README.md:174`、`RELEASE.md:124`），实际仓库名已是 `OneKeyTool`。

---

## 三、建议的实施顺序

| 优先级 | 项 | 理由 | 成本 |
|---|---|---|---|
| **P0** | ② 检查 `SendInput` 返回值，失败时记录并提示 | 唯一会导致**按键永久丢失**的缺陷 | 小 |
| **P0** | ① 日志异步化 + 移出 `_gate` 锁 | 可致钩子被系统静默摘除，故障极难定位 | 中 |
| **P1** | ④ 抽出纯状态机 + 自包含测试工程 | 消除两套逻辑漂移，兑现 README 承诺 | 中大 |
| **P1** | A `Config.Save` 原子化 | 用户配置无声丢失 | 小 |
| **P2** | B `WinWaitMs` 降到 50–80 + 自动重复即时放弃 | 影响每一次正常按 Win 键的体感 | 小 |
| **P2** | ⑤ CI 直接调用 `build.ps1`，删掉重复清单 | 顺带修掉 LICENSE 判断不一致 | 小 |
| **P2** | C 显式追踪自动重复，弃用 `0x4000` | 去掉未定义行为依赖 | 小 |
| **P3** | ③ `dist` 只清理本次产物 | 低危，但顺手改掉 | 小 |
| **P3** | E / F 配置补全、死代码清理、文档路径修正 | 收尾 | 小 |

---

## 四、关于 ④ 的实施方案

考虑到项目坚持"零依赖、系统自带 `csc` 即可编译"（`build.ps1` 只依赖 .NET Framework 4.0），建议**不要**引入 .NET SDK + xUnit，而是在 `tests/` 下放一个自包含的控制台程序，手写断言，并在 `build.ps1` 中增加一步编译并运行。

抽出的接口大致如下：

```csharp
internal enum Decision { Pass, Swallow }

internal interface IMacroEngine
{
    Decision Feed(KeyEvent ev, long nowMs);   // 纯函数式，时间可注入
    void Tick(long nowMs);                     // 超时收尾
    event Action<KeyEvent> NeedReplay;         // 需重放的事件走事件，而非直接注入
}
```

**关键收益**：观察模式与拦截模式共用同一个引擎——观察模式只是把 `Swallow` 降级为 `Pass`、且不执行重放。这样两套状态机的不一致从根上消失，而且宏识别、超时、错误前缀、完整命中全都能用模拟事件序列覆盖。

需要覆盖的回归用例：

- 单独按 Win 键（含长按自动重复）
- Win + 其他键组合（如 Win+E）
- 手动 Win+R 后输入其他内容
- 宏超时（各阶段超时边界）
- 错误前缀（非 http 开头）
- 完整命中
- 正常打字（间隔 60ms 以上）
- 方向键连按

---

## 五、修复记录（2026-10-13）

codex 按本文档改了一版后，复审发现 ① ② ③ ⑤ A B C E F 已修好，但 **P0 回归**（`_held` 无界增长）
与 ④ 的核心（两套状态机漂移）仍在。以下为后续修复。

### 架构变更

抽出 `src/MacroEngine.cs`：识别状态机成为纯逻辑类，不碰 Win32、不做注入，
时间由调用方注入（`Feed(ev, nowMs)` / `Tick(nowMs)`），暂扣事件通过 `TakeReplay` 交还。

`src/Detector.cs` 退化为适配层，只负责钩子安装、事件翻译、重放注入、异步日志。
**观察模式与拦截模式共用同一个 `MacroEngine`**，观察模式只是不吞事件、不重放、
不执行动作——两套状态机的不一致从根上消失。

### 逐项状态

| 项 | 状态 | 说明 |
|---|---|---|
| ① 钩子内同步写日志 | 已修复 | 回调只入队 + `Set()`；磁盘 IO 在后台线程，含 2MB 轮转；`Stop()` 会 `Join(1000)` |
| ② `SendInput` 返回值 | 已修复 | 检查注入数，失败抛 `Win32Exception` 并经 `ReportError` 提示 |
| ② 重放用原始扫描码 | 已修复 | `KEYEVENTF_SCANCODE` + 原始 `Scan`，`Scan==0` 时回退虚拟键 |
| ② Enter 扩展键 | 已修复 | 已从 `IsExtendedVk` 移除 `0x0D` |
| ③ dist 递归删除 | 已修复 | 只删本脚本管理的产物 |
| ④ 两套状态机漂移 | **已修复** | 合并为单一 `MacroEngine`；10 万条随机序列差分测试零分歧 |
| ④ 回归测试 | 已修复 | 13 组用例 / 68 项断言，构建时自动运行，不通过即中断 |
| ⑤ 两份源文件清单 | 已修复 | CI 直接调 `build.ps1`；测试编译复用同一份 `$src` |
| A 配置原子写 | 已修复 | 临时文件 + `File.Replace`；`Load` 失败经 `LastLoadError` 上报 |
| B 长按 Win 延迟 | 已修复 | 自动重复立即 `Abort`；`WinWaitMs` 默认 300 → 80 |
| C `0x4000` 未定义位 | 已修复 | 改为显式 `_pressedKeys` 追踪 |
| E 配置补全 | 已修复 | `DebugKeys` / `Signature` 持久化 |
| F 死代码与文档 | 已修复 | 移除 `LastMacroEvents` / `Trace` / `GetAsyncKeyState`；文档路径与描述已更新 |

### P0 回归（本轮新发现并修复）

`HandleMacro` 中新增的自动重复分支绕过了 `MaxHeld` 上限与超时顺延控制：

```csharp
// 修复前：重复事件既进 _held 又顺延 _deadline，且直接 return 跳过上限检查
if (_currentRepeated) { _held.Add(ev); _deadline = Now + MacroTimeoutMs; return Swallow(); }
```

实测按住某键 5000 次后 `_held` 达 2007 且状态永不超时；松手后按下不匹配的键会
把上千个被吞按键一次性重放。触发路径：`Win+R` 后在"运行"对话框里输入 `http` 再按住某键。

修复后：重复事件仍计入 `_held` 与上限检查，但**不刷新超时窗口**，
实测在 `MacroTimeoutMs`（4000ms）处按时放弃，重放 134 个事件，`_held` 归零。

### 验证结果

- `build.ps1` 全流程通过，13 组用例 / 68 项断言全绿
- 真实固件序列（`Win↓ R↓ Win↑ R↑`）在两种模式下均识别
- 10 万条随机序列差分测试：两模式识别结果零分歧、零异常
- P0 复核：按住键 5000 次后 `HeldCount` 有界，不再无界增长

### 遗留

- `Signature` 仅支持英文字母与数字（`ParseSignature` 会拒绝其他字符并回退默认值）；
  宏网址若含连字符等字符，需要先扩展该校验。
- `WinWaitMs=80` 配合 50ms 粒度的 `Tick` 泵，实际有效窗口约为 `[80, 130]ms`。
  实测固件约 10ms，余量充足；若遇到慢速固件可上调至 100~150。
