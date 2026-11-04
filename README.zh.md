<p align="center">
  <img src="icon.svg" width="96" alt="dsh-shot logo">
</p>

# dsh-shot

中文 | [English](README.md)

**DeepSeek Harness（DSH）截图插件。** 全局快捷键、输入框相机按钮或 `/shot` 命令三种方式触发：全屏覆盖层拖框选区，截图直接进入当前对话的输入框草稿。智能体也能通过 `screenshot` 工具在你要求时主动查看屏幕。

## 功能

- **全局快捷键**（默认 `Ctrl+Alt+A`），浏览器没聚焦也能用
- **输入框相机按钮**：点击直接触发截图，截图进入输入框草稿，回车即发送
- **`/shot` 命令**：可带区域参数，如 `/shot 100,100,800,600`
- **`screenshot` 智能体工具**：你说"看看我的屏幕/这个报错弹窗"时，模型可以自己截屏（默认弹覆盖层拖框，也可整屏直截）
- **翻译 / OCR 动作**：快捷键流程里选择后自动附上预设说明（"翻译这张截图里的文字"/"提取全部文字"）
- **仅复制到剪贴板**模式：截图不进任何会话
- **绝不自动发送**：截图永远等你亲自按回车

## 环境要求

- Windows 10/11（截图通过 PowerShell + WinForms 覆盖层实现，暂不支持其他系统）
- DSH：**已在 `0.2.2-rc.1` 上实测**；声明兼容 `0.2.1+`（`engines` 字段，依赖 client-modules 网页端插件加载器）。更旧或未发布的版本行为可能不同。
- 零外部依赖、无编译产物：覆盖层窗口由 PowerShell `Add-Type` 进程内编译，天然避开"来路不明的新二进制"杀软误报

## 安装

用 DSH CLI 从 npm 安装：

```sh
dsh plugin --profile <你的profile> add dsh-shot
```

也可以从本地目录或 GitHub tarball 安装：

```sh
# 本地目录
dsh plugin --profile <你的profile> add file:/path/to/dsh-shot-plugin

# tarball（npm 风格 URL）
dsh plugin --profile <你的profile> add https://github.com/<owner>/<repo>/archive/refs/tags/v0.1.1.tar.gz
```

DSH Desktop 用户在应用内 **设置 → 插件** 搜索 `dsh-shot` 安装。装完后如果输入框里没出现相机按钮，刷新一次页面即可。

## 使用

| 操作 | 效果 |
|---|---|
| `Ctrl+Alt+A`（全局） | 覆盖层拖框 → 回车 → 截图进入最近活跃会话的草稿 |
| 输入框相机按钮 | 与快捷键完全相同的覆盖层与草稿流程 |
| `/shot` | 覆盖层拖框选区 |
| `/shot x,y,w,h` | 跳过覆盖层，直接截取指定矩形 |
| 智能体 `screenshot` 工具 | 你要求看屏幕/窗口/报错弹窗时由模型触发 |

覆盖层上可选动作：**入会话**（进输入框草稿）、**翻译**、**OCR**、**复制**（仅剪贴板）。

## 配置

通过 profile 补丁（`cordis.patch.yml`）自定义快捷键：

```yaml
- id: dsh-shot
  name: dsh-shot
  config:
    hotkey: Ctrl+Alt+A   # 任意 RegisterHotKey 风格组合键，如 Ctrl+Shift+S
```

## 隐私与安全

- 截图**完全在本地完成**：PNG 落在临时目录和你选择的会话里，本插件不向任何地方上传数据。
- 快捷键守护进程意外退出后保持停用（不静默自动重启），原因会记录到控制台。

## 常见问题

- **快捷键没反应** — 可能被其他应用占用，按上面的配置换成别的组合键后重载插件。
- **杀软拦截覆盖层** — 覆盖层是 PowerShell 承载的进程内编译 WinForms 代码，如被拦截可将 `helper/*.ps1` 加入白名单。
- **相机按钮不见了** — 刷新页面；客户端半区注册在 `conversation.input.left` 槽位。

## 更新日志

见 [CHANGELOG.md](CHANGELOG.md)。`0.1.1` 修复了 DSH `0.2.2-rc.1` 上网页端启动失败的问题（client bundle 改为以包名注册，匹配 client-modules 加载器的 row-id 契约）。

## 许可

[MIT](LICENSE)
