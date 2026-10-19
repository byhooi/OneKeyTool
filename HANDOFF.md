# Handoff：修复自定义键「偶发失效」

> 日期：2026-10-19
> 状态：代码已改并验证，**尚未提交**；实际运行的旧实例尚未更新。

## 问题

用户报告自定义键「有时候会失效」：按下后没有执行动作，联想原网址照常打开。

## 根因（已用线上日志确认）

日志位置：`D:\Program Files\OneKeyTool\onekeytool.log`

关键记录：

```
21:08:39.088  放弃拦截（未出现特征串，判定为普通输入），重放 47 个事件
21:09:01.392  命中自定义键：暂扣 64 个事件，耗时 1159 ms

10:30:29.405  放弃拦截（未出现特征串，判定为普通输入），重放 47 个事件
10:33:08.493  命中自定义键：暂扣 64 个事件，耗时 1160 ms
```

失败时宏本身完整到达，但 `oneclick` 没有被匹配上，状态机走到 20 个字符上限后主动放弃，把整段宏重放回系统 → Win+R 被还原、联想网址被输入并打开，表现为「失效」。

原因链：

1. `MacroEngine` 用 `_pressed` 判定「自动重复」。只要某个按键的 UP 事件丢失（钩子被系统短暂摘掉、被钩子链上其他程序吞掉），`_pressed` 就会永久残留该键的按下状态。
2. 下一次宏走到同一个字符时，它的 DOWN 被误判成重复，`HandleMacro` 里 `if (ev.IsDown && !repeated)` 整个匹配分支被跳过。
3. `_sigSeen` 永远为 false → 到 `MaxMacroChars = 20` 后 `Abort("未出现特征串")` → 重放 47 个事件。
4. 本次宏里那个字符的 UP 会清掉 `_pressed` 残留，所以下一次按通常恢复正常——这解释了「偶发且自愈」。

反向验证：把判定临时改回旧逻辑跑新增用例，失败时**恰好重放 47 个事件**，与线上日志完全一致。

## 改动

### `src/MacroEngine.cs`

- 新增常量 `AutoRepeatWindowMs = 500`。
- 新增字段 `Dictionary<ushort, long> _lastDownAt`，记录每个键最近一次 DOWN 的时间。
- `Feed()` 中的自动重复判定改为同时要求时间窗口：

```csharp
long lastDown;
bool repeated = ev.IsDown && _pressed.Contains(ev.Vk)
    && _lastDownAt.TryGetValue(ev.Vk, out lastDown)
    && nowMs - lastDown <= AutoRepeatWindowMs;
```

- DOWN 时更新 `_lastDownAt`，UP 时移除。
- 真自动重复间隔约 30ms，仍在窗口内；残留状态通常间隔以秒计，会自然失效。
- 「自动重复计入 `_held` 与上限、但不刷新超时窗口」这条不变量未改变。

### `tests/StateMachineTests.cs`

- 新增常量 `K = 0x4B`。
- 新增用例 `TestLostKeyUpDoesNotBreakNextMacro`（注册名「丢失抬起事件不破坏后续识别」）：
  喂一个没有抬起的 `K` → 等 2 秒 → 喂完整真实固件宏 → 断言命中且无重放。拦截 / 观察两种模式都跑。

### `CLAUDE.md`

- 在架构不变量第 3 条补充了重复判定的时间窗口要求，指向新增用例。

## 验证

```powershell
.\build.ps1
```

结果：编译通过，**81 项断言全部通过**，打包成功。

反向验证：临时恢复旧判定后，新用例失败（触发 0 次、重放 47 个事件），确认测试能拦住这个回归；随后已恢复修复版并重新构建通过。

## 当前状态 / 下一步

- [ ] 改动尚未 commit。
- [ ] `D:\Program Files\OneKeyTool\OneKeyTool.exe` 仍是旧版本，且进程从 2026-10-15 起一直在运行，不会热更新。
- [ ] 若要立即验证：托盘退出旧实例 → 用 `dist\OneKeyTool.exe` 覆盖安装目录（版本号会显示 `v0.0.0-dev`）→ 重新启动。
- [ ] 正式发布：提交后打 `v*` tag 走 CI，Release 只挂 `dist\OneKeyTool-<版本>-win-x64.zip`。

## 仍未覆盖的情况

本次修复只处理「UP 丢失导致 repeated 误判」。如果失效来自：

- DOWN 事件本身被钩子链上其他程序吞掉（序列缺字符）；
- 钩子被 Windows 因回调超时静默摘掉（最长 10 分钟才自检重装）；
- 前台为管理员权限窗口 / 安全桌面时 `SendInput` 被 UIPI 阻止；

则需要另外排查。日志里的 `DebugKeys=true` 可以打出每个原始事件，用于区分。

## 日志里容易误读为故障、其实正常的记录

| 日志 | 含义 |
|---|---|
| `等待超时（单独按 Win）` | 单独按 Win，正常放行 |
| `Win 后跟了其他键: 0x4C / 0x44 / 0x58` | Win+L / Win+D / Win+X |
| `Win 键自动重复` | 长按 Win |
| `钩子已自检重装` | 每 10 分钟一次的正常自检 |
