# 发布到 GitHub Releases 操作指南

## 一、准备工作（只需做一次）

### 1. 确认 .gitignore 生效

仓库里**不应该**提交编译产物和日志。当前 `.gitignore` 已覆盖：

```
*.exe  *.log  /dist/  /build/  tools/KeySpy/logs/
```

### 2. 初始化仓库

在项目根目录 `D:\Documents\Github\keyboard` 执行：

```bash
git init
git add .
git status          # ← 检查一下，确认没有 .exe 和 .log 被加进来
git commit -m "初始提交：键盘自定义键改造工具"
```

### 3. 在 GitHub 上建仓库

浏览器打开 https://github.com/new

- **Repository name**：`keyboard`（或你喜欢的名字）
- **Public / Private**：按需选择
- **不要**勾选 "Add a README file"（本地已有）

创建后，GitHub 会显示推送命令，执行：

```bash
git remote add origin https://github.com/<你的用户名>/keyboard.git
git branch -M main
git push -u origin main
```

---

## 二、发布一个版本（以后每次发版都这么做）

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
3. 打开 **Releases** 页面，就能看到 `v1.0.0` 已自动创建，附件里有 `OneKeyTool-v1.0.0-win-x64.zip`

**不需要**在本地编译，也**不需要**手动上传文件。

---

## 三、手动触发构建（不发 Release）

想在正式发版前先验证能否编译成功：

1. 仓库 → **Actions** → 左侧选 `Build and Release`
2. 右侧点 **Run workflow** → **Run workflow**
3. 跑完后在该次运行的页面底部 **Artifacts** 里下载 zip

这种方式**不会**创建 Release，只产出构建物（保留 90 天）。

---

## 四、发布前检查清单

- [ ] 本地跑过 `.\build.ps1` 且编译无警告
- [ ] 实际按过自定义键，确认识别正常、无误报
- [ ] `git status` 里没有 `.exe` / `.log` / `dist/` 被误加
- [ ] `README.md` 里的功能说明与实际一致
- [ ] tag 名与预期版本一致（如 `v1.0.0`，注意 `v` 前缀）

---

## 五、常见问题

### Q：Actions 里构建失败了怎么办？

点进失败的那次运行，展开红色步骤看报错。常见原因：

- 源文件路径写错 → 检查 `release.yml` 里的 `src/*.cs` 列表是否与实际文件名一致
- 中文乱码 → 脚本里的中文需存为 **UTF-8 with BOM**（本项目的 `build.ps1` 已处理）

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

一致。`build.ps1` 与 `release.yml` 的编译参数、打包内容完全相同，本地验证过就没问题。

---

## 六、目录说明

```
keyboard/
├─ .github/workflows/release.yml   ← 自动构建与发布（核心）
├─ .gitignore                      ← 排除编译产物
├─ build.ps1                       ← 本地构建（与 CI 等价）
├─ config.ini                      ← 默认配置模板（会打进 zip）
├─ LICENSE                         ← MIT
├─ README.md                       ← 仓库首页展示（会打进 zip）
├─ RELEASE.md                      ← 本文件（不会打进 zip）
├─ src/                            ← 主程序源码
└─ tools/                          ← 诊断工具源码
```
