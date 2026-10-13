# 发布到 GitHub Releases 操作指南

> 仓库已经建好，远程指向 `https://github.com/byhooi/OneKeyTool`。
> 本文只讲**怎么发一个新版本**。想从零建仓库（换名字、搬去别处）再看下面的补充。

---

## 一、发版前的一次性检查

只在**第一次发版**或**改过构建配置**时需要确认，日常发版可跳过：

- `.gitignore` 覆盖了编译产物和日志，`git status` 里**不应出现** `.exe` / `.log` / `dist/`
- 远程地址正确：`git remote -v` 应显示 `byhooi/OneKeyTool`

<details>
<summary>真要重建仓库时的命令（一般用不到）</summary>

```bash
git init
git add .
git status          # ← 确认没有 .exe 和 .log 被加进来
git commit -m "初始提交：键盘自定义键改造工具"

# 在 https://github.com/new 建好空仓库（不要勾 "Add a README file"）后：
git remote add origin https://github.com/<用户名>/OneKeyTool.git
git branch -M main
git push -u origin main
```

</details>

---

## 二、发布一个版本（日常发版）

**核心：打 tag 并推送，剩下的 GitHub Actions 全自动完成。**

```bash
# 1. 确认代码已提交
git add .
git commit -m "修复 xxx"

# 2. 打个版本 tag（版本号按语义化版本写）
git tag v1.0.0

# 3. 推送代码和 tag
git push origin main
git push origin v1.0.0
```

推送 tag 后：

1. 打开仓库的 **Actions** 标签页，会看到 `Build and Release` 正在运行
2. 约 1~2 分钟后完成
3. 打开 **Releases** 页面，就能看到 `v1.0.0` 已自动创建

**Release 附件只有一个文件**（CI 会自动传）：

| 附件 | 用途 |
|---|---|
| `OneKeyTool-v1.0.0-win-x64.zip` | 用户下载解压即用（内含 exe、config.ini、README、LICENSE） |

> 发 zip 而不是裸 exe：浏览器下载裸 `.exe` 会弹安全警告，下载 `.zip` 不会。
> 注意 zip 只解决**下载时**的警告；资源管理器解压会把「来自网络」标记传给
> 里面的 exe，首次运行仍可能触发一次 SmartScreen（未签名程序的默认行为，
> 与是否打包无关）。README 里有面向用户的说明，别把它写成「完全无提示」。
> 程序自身不做自动更新，所以不需要额外的校验清单。

**不需要**在本地编译，也**不需要**手动上传文件。

---

## 三、手动触发构建（不发 Release）

想在正式发版前先验证能否编译成功：

1. 仓库 → **Actions** → 左侧选 `Build and Release`
2. 右侧点 **Run workflow** → **Run workflow**
3. 跑完后在该次运行的页面底部 **Artifacts** 里下载 exe

这种方式**不会**创建 Release，只产出构建物（保留 90 天）。

---

## 四、发布前检查清单

- [ ] 本地跑过 `.\build.ps1`，且状态机回归用例全部通过（脚本会自动跑，不通过会中断构建）
- [ ] 实际按过自定义键，确认识别正常、无误报
- [ ] 设置窗口里所有复选框都可见（改过界面布局时尤其要确认）
- [ ] 窗口右上角显示的版本号与 tag 一致（`-Version` 会自动注入，核对一眼即可）
- [ ] `dist/` 里生成了带版本号的 zip，且包内 4 个文件齐全（exe / config.ini / README.md / LICENSE）
- [ ] `git status` 里没有 `.exe` / `.log` / `dist/` 被误加
- [ ] `README.md` 里的功能说明与实际一致
- [ ] tag 名与预期版本一致（如 `v1.0.0`，注意 `v` 前缀）

---

## 五、常见问题

### Q：Actions 里构建失败了怎么办？

点进失败的那次运行，展开红色步骤看报错。常见原因：

- 源文件路径写错 → 检查 `build.ps1` 里的 `$src` 数组是否与实际文件名一致
- 状态机回归用例失败 → 展开 `使用统一构建脚本编译` 步骤，看是哪条断言
- 中文乱码 → `build.ps1` 含中文字面量，必须存为 **UTF-8 with BOM**（当前已是）。
  用编辑器另存时若丢掉 BOM，PowerShell 5.1 会按 ANSI 代码页解析，中文变乱码甚至语法报错

### Q：想改 Release 的说明文字？

改 `.github/workflows/release.yml` 里 `发布 Release` 那一步的 `body` 字段。
每次发版都会自动附上 `generate_release_notes: true` 生成的变更列表。

### Q：想重新发布同一个版本？

```bash
# 删除远程 tag 和 Release，再重新打
git tag -d v1.0.0
git push origin :refs/tags/v1.0.0
# 然后在 GitHub Releases 页面删掉那条 Release，再重新打 tag 推送
```

### Q：本地构建脚本和 CI 的行为一致吗？

一致，而且是**同一份脚本**：CI 的构建步骤直接调用 `.\build.ps1 -Version <tag>`，
不存在两份编译参数或文件清单。本地通过即 CI 通过（编译器版本可能不同，但参数相同）。

---

## 六、目录说明

```
OneKeyTool/
├─ .github/workflows/release.yml   ← 自动构建与发布（调用 build.ps1）
├─ .gitignore                      ← 排除编译产物
├─ build.ps1                       ← 构建脚本（本地与 CI 共用，唯一事实来源）
├─ config.ini                      ← 默认配置模板（程序首次启动时据此生成）
├─ LICENSE                         ← MIT
├─ README.md                       ← 仓库首页展示（不随发布文件分发）
├─ RELEASE.md                      ← 本文件（不随发布文件分发）
├─ src/                            ← 主程序源码
├─ tests/                          ← 状态机回归测试（构建时自动运行）
└─ tools/                          ← 诊断工具源码
```

> **注意**：CI 与本地统一调用 `build.ps1`。新增 `src/*.cs` 源文件时，
> 只需更新该脚本中的 `$src` 数组——测试编译复用同一份清单，不会漏。
