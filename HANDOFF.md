# Handoff：自启动静默化（v1.0.4）

> 日期：2026-10-20
> 状态：代码已提交并推送（`fb9dfac`），`v1.0.4` 标签已推送，等待 CI 构建结果。
> 上一版 handoff（修复自定义键偶发失效）的内容已随 v1.0.3 发布完成。

## 问题

用户反馈：「开机自启动后不是静默的，会弹出主窗口。」

界面上的文案写着「登录后静默运行，只在托盘显示图标」，但实际启动会显示设置窗口。

## 根因

**静默启动从未实现过**，不是某个开关失效：

1. `AutoStart.SetEnabled()` 只往 `HKCU\...\Run` 写入 `"OneKeyTool.exe"`，没有带任何参数。
2. `Program.Main()` 无条件执行 `Application.Run(new MainForm(...))`，窗口必然显示。
3. 界面文案只是一句承诺。

## 改动（4 个文件，+52 / -8）

### `src/AutoStart.cs`

- 新增 `public const string SilentArg = "--silent"`。
- 新增 `BuildCommand()`，统一生成 `"<exe路径>" --silent`（路径带引号，参数在引号外）。
- `SetEnabled(true)` 写入带参数的命令行。
- `RefreshPath()` 把旧版写入的、不带参数的条目自动升级为静默启动格式。

### `src/Program.cs`

- 解析命令行参数（不区分大小写）。
- 静默启动时若发现已有实例在运行，**不弹「已经在运行了」提示框**，直接退出，避免登录时弹窗打扰。

### `src/MainForm.cs`

- 构造函数新增 `bool startSilent` 参数，记录到 `_startSilentOnce`。
- 重写 `SetVisibleCore(bool value)`：拦下第一次「请求可见」（`Application.Run(form)` 内部的 `Show()`），改为不可见；需要时先 `CreateHandle()`，保证托盘等功能仍有有效句柄。
- 之后从托盘「显示设置」调用的 `Show()` 正常显示窗口。

### `README.md`

- 「开机自动启动」章节说明方法一为静默启动。
- 方法二（`shell:startup` 快捷方式）补充：默认不静默，可在快捷方式「目标」末尾加 ` --silent`。

## 验证

```powershell
.\build.ps1
```

结果：编译通过，**81 项断言全部通过**，打包成功。

⚠️ 运行时行为只做了代码审查，**尚未在真机登录场景下验证**（见下方待办）。

## 发布过程踩过的坑

本次发布反复撤销重来，记录一下：

1. **删 GitHub Release 不会删 tag**。删 Release 时「also delete the tag」有时不弹，必须用
   `git ls-remote --tags origin` 核对。
2. **本地 tag 会从远程「复活」**。远程 tag 未删干净时，一次 `git fetch/pull` 就会把它拉回本地。
   之后 `git tag -a v1.0.4` 报 `tag 'v1.0.4' already exists`。处理：`git tag -d v1.0.4` 后重打。
3. **已推送的提交要撤销，只能强推**。本次用 `git push --force-with-lease origin main`；
   注意执行前不要 `git fetch`，否则 lease 校验目标会变。
4. `git pull` 在「本地已撤销 + 远程还旧提交」时会生成 merge 提交，把旧历史又带回来。
   发布期间尽量避免 pull，直接强推。
5. 最终发布提交：`fb9dfac feat: 支持静默启动，优化自启功能以仅显示托盘图标`，
   `v1.0.4` 标签指向它，远程已核对无误。

## 当前状态 / 下一步

- [x] `fb9dfac` 已推送，本地与 `origin/main` 一致。
- [x] `v1.0.4` 标签已创建并推送，远程指向 `fb9dfac`。
- [ ] 到 GitHub Actions 确认构建成功、Release 已创建（Release 只应挂
      `OneKeyTool-v1.0.4-win-x64.zip`）。
- [ ] 用户机器上仍在运行的旧实例不会热更新：托盘退出 → 用新 exe 覆盖 → 重新启动。
- [ ] 重启 / 登录后确认：无主窗口弹出，托盘有图标，双击托盘能正常打开设置。
- [ ] 检查注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `OneKeyTool`
      值为 `"<安装路径>\OneKeyTool.exe" --silent`。

## 手动验证静默启动

不重启也能验证：

```powershell
# 先退出正在运行的实例（托盘右键 → 退出）
& "D:\Program Files\OneKeyTool\OneKeyTool.exe" --silent
```

预期：无窗口出现，托盘出现图标；双击托盘图标能正常弹出设置窗口。

> 注意：程序有单实例互斥。若旧实例还在运行，`--silent` 启动会静默退出，什么都不会发生。

## 相关文件

| 文件 | 说明 |
|---|---|
| `src/AutoStart.cs` | 自启注册表读写、`--silent` 命令行生成 |
| `src/Program.cs` | 参数解析、单实例处理 |
| `src/MainForm.cs` | `SetVisibleCore` 静默启动 |
| `README.md` | 用户文档 |
