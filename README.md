# 超强大脑

一个用快捷键随时呼出的 Windows 备忘录小窗。普通记录一条条编辑，账号密码放进独立的本地加密密码库；背景、配色和图片透明度都可以自己调整。

[下载最新版本](https://github.com/qzm-qzm/super-brain/releases/latest) · [使用说明](docs/使用说明.md) · [更新与发布](docs/更新与发布.md) · [数据与安全](docs/数据与安全.md) · [版本记录](CHANGELOG.md)

![超强大脑桌面软件](docs/preview.png)

## 开始使用

1. 在 Releases 页面下载 `SuperBrain-Setup-版本号-x64.exe`，完成安装。
2. 打开「超强大脑」，按 **Alt + Q** 呼出或收起。可以在设置中改为 `F8` 等快捷键，并启用开机启动。
3. 在「备忘录」中新建记录，修改后自动保存。需要存密码时，切换「账号密码」，先创建主密码。
4. 点击右上角调色盘，选择主题、自定义颜色或上传背景图片，拖动滑块调整图片透明度。

也提供 `SuperBrain-Portable-版本号-x64.exe` 免安装版。两个版本都把资料放在当前 Windows 用户的 `%APPDATA%/SuperBrain` 中；免安装版不会把资料自动放到 U 盘，也暂不支持开机启动。

当前发布 Windows x64 版本，建议在 Windows 11 使用。安装包尚未做 Windows 代码签名，系统可能提示「未知发布者」。发布页附带 `SHA256SUMS.txt`，用于核对下载文件完整性；它不能代替代码签名。

## 已有功能

| 使用场景 | 功能 |
| --- | --- |
| 随时记录 | 可自定义的全局快捷键、托盘入口、小窗拖动与调整大小、可选始终置顶 |
| 普通备忘录 | 新建、编辑、搜索、置顶、删除和短时撤销，自动保存 |
| 账号密码 | 主密码解锁、账号/密码/网站/备注、显示或隐藏密码、一键复制、生成随机密码 |
| 自动锁定 | 收起窗口、锁屏、休眠时锁定；闲置锁定时间可选，默认 5 分钟 |
| 个性外观 | 6 种配色、自定义背景色与主题色、本地背景图片、0%–100% 图片透明度 |
| 备份与更新 | 导出和恢复备份、修改主密码、手动检查 GitHub 新版本 |

**普通备忘录是明文；账号密码加密保存。主密码没有找回或重置解密功能。** 密码和私密备注请放在「账号密码」中，具体边界见[数据与安全](docs/数据与安全.md)。

## GitHub 托管什么

仓库保存软件代码，GitHub Actions 负责检查和构建，Releases 保存 Windows 安装包及版本记录。你的备忘录、密码和背景图片留在本机，不会因为使用这个仓库而上传到 GitHub。

本项目是桌面软件，没有网页版、账号系统或云同步。平常记录、查询和备份可离线使用；主动点击「检查更新」时才会访问 GitHub 的最新版本接口，打开仓库或下载页面也会使用网络。

## 开发与构建

使用 Windows、Node.js 24 和 Git。首次运行：

```powershell
git clone https://github.com/qzm-qzm/super-brain.git
cd super-brain
npm ci
node node_modules/electron/install.js
npm run check
npm test
npm run test:desktop
npm start
```

生成安装包和免安装包：

```powershell
npm run build
```

产物在 `dist/`。依赖由 `package-lock.json` 固定，Electron 运行时显式安装步骤也用于处理 npm 未运行依赖安装脚本的环境。`npm run test:desktop` 使用 Playwright 启动真实 Electron，以独立测试目录和虚构记录检查界面、进程通信、保存及重启恢复，无需另行下载 Playwright 浏览器。开发时运行 `npm start` 默认使用同一个 Windows 用户数据目录；调试请使用独立测试目录，方法见[使用说明](docs/使用说明.md#开发时隔离测试资料)。

## 后续如何更新

功能修改完成、检查通过后，在干净的 `main` 分支上运行：

```powershell
npm version patch -m "chore: release v%s"
git push origin main --follow-tags
```

小功能版本可把 `patch` 改为 `minor`。推送 `v版本号` 标签后，GitHub Actions 会检查版本号、测试、打包，并发布安装包。用户在软件「设置 → 检查更新」中前往下载，再退出软件并覆盖安装，已有资料保留。当前不支持后台自动下载或静默安装。

完整操作和发布失败的处理方法见[更新与发布](docs/更新与发布.md)。
