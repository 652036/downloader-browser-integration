# IDM 式下载接管完整版设计（v2）

日期：2026-07-05
前置：本设计在 2026-07-02 原型（`2026-07-02-native-download-manager-design.md`）基础上迭代，目标是把原型升级为日常可用的 IDM 替代品。

## 目标

1. 可靠接管 Chrome 与 Edge 的下载：命中可配置类型列表的下载被取消并转交本地程序，弹出 IDM 式确认窗。
2. 多线程分段下载引擎：Range 分段并发（默认 8 连接）、断点续传、暂停/恢复。
3. WPF 常驻桌面程序：下载列表、进度展示、确认弹窗、设置窗口、托盘常驻。
4. Cookie/Referer/User-Agent 转发，使登录态下载可用。
5. 关闭浏览器后下载不中断；Chrome 与 Edge 共享同一份下载列表。

## 非目标（本轮不做）

- 视频站嗅探、流媒体分段合并。
- 限速、代理设置、多语言、"下载全部链接"。
- 系统级（管理员）安装；继续使用 HKCU 按用户注册。
- 复制 IDM 的代码、资源、名称或私有行为；不绕过 DRM、付费墙或浏览器安全提示。

## 总体架构

```
浏览器 (Chrome / Edge)
  └─ MV3 扩展 (extension/)
       │ chrome.runtime.connectNative 长连接端口
       ▼
LocalDownloader.Host  —— 瘦代理，浏览器按需拉起
       │ 命名管道 \\.\pipe\LocalDownloader.App（4 字节长度前缀 JSON 帧，
       │ 与 Native Messaging 相同的封帧格式，复用同一套代码）
       ▼
LocalDownloader.App  —— WPF 常驻（托盘），单实例
  ├─ 确认弹窗 / 主窗口 / 设置窗口
  └─ LocalDownloader.Core —— 下载引擎共享库
```

生命周期规则：

- Host 由浏览器随扩展端口建立而启动、端口断开而退出；它不承载任何下载。
- App 常驻。Host 连接管道失败时启动同目录下的 `LocalDownloader.App.exe`，以 500ms 间隔重试连接，总超时 5 秒。
- App 通过命名互斥锁 `Local\LocalDownloader.App.SingleInstance` 保证单实例；第二个实例检测到互斥锁后直接退出。
- 关闭主窗口 = 最小化到托盘；托盘菜单"退出"才真正退出。退出时未完成任务先暂停并落盘。

## 项目重组

在现有解决方案 `NativeDownloadManager.sln` 上重构，不推倒重来：

| 项目 | 角色 | 来源 |
|---|---|---|
| `apps/core/LocalDownloader.Core` | 类库：下载引擎、任务模型、设置模型、IPC 消息契约与封帧 | 新建；`DownloadEngine`/`DownloadRequest`/`FileNameSanitizer`/`TaskStore`/`NativeMessaging`（封帧部分）自 Host 迁入并升级 |
| `apps/host/LocalDownloader.Host` | 瘦代理：stdio 协议 ↔ 命名管道；App 未运行时拉起 | 现有项目瘦身 |
| `apps/app/LocalDownloader.App` | WPF 程序：三窗口 + 托盘 + 引擎宿主 + 任务持久化 | 新建，MVVM（CommunityToolkit.Mvvm） |
| `apps/host/LocalDownloader.Tests` | 单元/集成测试 | 现有项目扩充 |
| `extension/` | MV3 扩展：拦截、Cookie 收集、类型列表同步、旁路名单 | 现有代码增强 |

## IPC 消息契约

所有链路（扩展↔Host、Host↔App）传递相同的 JSON 消息；Host 仅做双向转发，不解析业务字段（只在管道断开时生成错误响应）。

扩展 → App：

```json
{
  "type": "download.create",
  "id": "browser-1751700000-ab12cd34",
  "url": "https://example.com/file.zip",
  "suggestedFilename": "file.zip",
  "referrer": "https://example.com/page",
  "cookieHeader": "session=...; token=...",
  "userAgent": "Mozilla/5.0 ...",
  "fileSize": 1048576,
  "mime": "application/zip",
  "source": "browser-download | context-menu"
}
```

`fileSize` 与 `mime` 为可选字段，来自浏览器下载项，仅用于确认窗初始显示；权威值以引擎探测为准。

扩展 → App（启动时同步拦截配置）：

```json
{ "type": "settings.get", "id": "..." }
```

App → 扩展（响应）：

```json
{
  "type": "settings.value",
  "id": "...",
  "interceptExtensions": [".zip", ".exe", ".mp4", "..."],
  "interceptMimePrefixes": ["application/octet-stream", "..."]
}
```

App → 扩展（用户在确认窗选择"交回浏览器"）：

```json
{
  "type": "download.returnToBrowser",
  "id": "...",
  "url": "https://example.com/file.zip",
  "suggestedFilename": "file.zip"
}
```

App → 扩展（任务受理确认）：`download.accepted`；错误：`download.error`（错误码沿用 v1：`unsupported_url`、`host_protocol_error`、`network_error`、`disk_error`、`permission_denied` 等）。

## 浏览器扩展设计

**连接方式**：由 `sendNativeMessage`（一次一进程）改为 `chrome.runtime.connectNative` 长连接端口。端口断开时按需重连（下次拦截时重建）。

**拦截点**：`chrome.downloads.onDeterminingFilename` —— 此时文件名与 MIME 已确定。命中规则即 `chrome.downloads.cancel(downloadItem.id)` 并发送 `download.create`。保留 `onCreated` 监听仅用于尽早记录，不做取消决策。

**拦截规则**（全部满足才拦截）：

1. URL 为 http/https。
2. 非隐身窗口、非其他扩展发起（`byExtensionId` 为空）、`danger` 为 safe 或未标记。
3. 文件扩展名命中类型列表，或 MIME 命中前缀列表。
4. 下载 id / URL 不在旁路名单中。

**默认类型列表**（App 端持有权威副本，设置窗口可编辑；扩展启动时经 `settings.get` 拉取并缓存到 `chrome.storage.local`，拉取失败时使用内置默认副本）：

- 压缩包：`.zip .rar .7z .tar .gz .tgz .bz2 .xz .cab`
- 安装包：`.exe .msi .msix .apk .dmg .pkg .deb .rpm`
- 视频：`.mp4 .mkv .avi .mov .wmv .flv .webm .ts .m4v`
- 音频：`.mp3 .flac .wav .aac .ogg .m4a .wma`
- 文档：`.pdf .doc .docx .xls .xlsx .ppt .pptx .epub .mobi`
- 镜像与其他：`.iso .img .bin .torrent .ttf .otf .psd`
- MIME 前缀：沿用 v1 列表并追加 `video/`、`audio/`、`application/pdf`。
- 默认不拦：网页、图片、纯文本。

**Cookie 转发**：manifest 增加 `cookies` 权限与 `<all_urls>` host 权限。拦截时 `chrome.cookies.getAll({ url })` 将结果拼为单个 `Cookie` 请求头字符串放入 `cookieHeader`。Cookie 只按目标 URL 收集，不扩大范围。

**旁路名单**：收到 `download.returnToBrowser` 后，扩展将该 URL 加入内存旁路集合（含 10 分钟过期），再调用 `chrome.downloads.download({url, filename})`；`onDeterminingFilename` 检查旁路集合命中则放行。

**失败放行（fail-open）**：端口错误、Host 无响应（3 秒超时）或返回 `download.error` 时，由于原下载已被 cancel，扩展将该 URL 加入旁路名单后调用 `chrome.downloads.download({url, filename})` 重新触发，让浏览器自行完成。任何本地程序故障都不能弄丢用户的下载。

**右键菜单**：保留现有"Download with Local Downloader"单链接下载，走同一 `download.create` 通道。

## 下载引擎（LocalDownloader.Core）

**探测**：发送 `GET` 且带 `Range: bytes=0-0`。响应 206 且含 `Content-Range` 总大小 → 支持分段；响应 200 → 不支持 Range，同时从 `Content-Length` 取大小（可能未知）。文件名解析优先级：`Content-Disposition` > 浏览器建议名 > URL 路径段 > `download.bin`。探测请求即携带 Cookie/Referer/UA。

**分段下载**（支持 Range 且大小已知）：

1. 按总大小均分 N 段（N = 设置的每任务连接数，默认 8，范围 1–32；小文件按每段最小 256KB 自动降低段数）。
2. 预分配 `.part` 文件至完整大小（`FileStream.SetLength`）。
3. 每段一个 HTTP 连接、独立 `FileStream` 定位写入自身偏移区间。
4. 每段进度周期性（每 500ms 或每 1MB）写入 `.task.json`。
5. 全部段完成后校验实际写入字节数等于总大小，`.part` 原子改名为最终文件，删除 `.task.json`。

**单流退化**（不支持 Range 或大小未知）：沿用 v1 单流逻辑；此类任务恢复时从头重下并在 UI 提示。

**暂停/恢复**：暂停 = 取消所有段连接、保留 `.part` 与 `.task.json`；恢复 = 读取各段已完成偏移，从断点继续。App 重启后扫描任务注册表，把未完成任务以 paused 状态载入列表。

**重试**：单段连接失败自动重试 3 次（指数退避 1s/2s/4s）；仍失败则整个任务标 `failed`，用户可手动重试（等价于恢复）。

**任务状态机**：`queued → probing → downloading ⇄ paused → completed / failed / canceled`；`failed` 可重试回 `queued`。同时下载任务数默认 3，超出的排队（FIFO）。

**持久化**：

- 设置：`%APPDATA%\LocalDownloader\settings.json`（保存目录、连接数、并发任务数、类型列表、开机自启）。
- 任务注册表：`%APPDATA%\LocalDownloader\tasks.json`（任务 id、URL、状态、目标路径的列表）。
- 分段状态：`<目标文件>.task.json`（各段区间与已完成字节），与 `.part` 同目录。
- 默认保存目录沿用 `Downloads\LocalDownloader`。

## WPF UI（LocalDownloader.App）

**主窗口**：DataGrid 下载列表 —— 文件名、大小、进度条、速度、剩余时间、状态。工具栏：新建任务（手动输入 URL）、暂停/恢复、取消、删除（可选同时删文件）、打开所在文件夹、设置。引擎进度事件经 `Dispatcher` 更新 ViewModel，UI 刷新节流至每 250ms。

**确认弹窗**（收到 `download.create` 时置顶弹出）：文件名（可编辑）、来源域名、大小（先显示浏览器提供值，探测完成后更新）、保存目录（可浏览更换）；按钮【开始下载】【取消】【交回浏览器】。同时到达多个请求时按序排队弹出。

**设置窗口**：保存目录、每任务连接数（1–32）、同时下载任务数、拦截类型列表编辑（每行一个扩展名）、开机自启动（写 HKCU Run 键）。保存后即时生效，扩展下次 `settings.get` 时拿到新列表。

**托盘**：常驻图标；左键双击显示主窗口；右键菜单：显示主窗口 / 全部暂停 / 全部开始 / 退出。

## 错误处理

原则：任何环节失败都不能弄丢用户的下载。

| 故障 | 行为 |
|---|---|
| App 拉起失败 / 管道连接超时 | Host 向扩展返回 `download.error`，扩展放行浏览器自行下载 |
| 扩展等待响应超时（3 秒） | 同上，fail-open |
| 分段连接失败 | 该段重试 3 次（指数退避），仍失败任务标 failed，可手动重试续传 |
| 断电 / 进程被杀 | `.task.json` 周期落盘；重启后任务以 paused 回到列表 |
| 不支持 Range 的任务恢复 | 从头重下，UI 提示 |
| 磁盘满 / 权限错误 | 任务 failed，错误信息显示在列表行内 |
| 管道消息格式错误 | 记日志、丢弃该消息，连接保持 |

## 安全边界

- 仅接受 `http://`、`https://` URL；拒绝本地路径与命令样输入。
- 文件名净化沿用 `FileNameSanitizer`，防路径穿越；保存位置限制在用户选择的下载目录内。
- Cookie 仅按目标 URL 域收集、仅存在于内存中的任务对象，不写入日志；`tasks.json` 不保存 `cookieHeader`（恢复下载时若 Cookie 已失效则任务失败，由用户重新从浏览器发起）。
- Native Messaging 清单继续按扩展 ID 白名单（`allowed_origins`）。
- 命名管道使用默认 ACL（仅当前用户可连接），不监听任何 TCP 端口。
- stdout 仅用于 Native Messaging 帧；诊断日志写 `%LOCALAPPDATA%\LocalDownloader\logs\`。

## 测试计划

**单元测试**：

- 分段区间计算（整除/非整除/小文件降段/单字节文件）。
- 恢复偏移计算与 `.task.json` 读写往返。
- IPC 消息序列化与 Host 转发路由（含错误注入）。
- 旁路名单命中与过期。
- 文件名净化（现有用例保留）。

**集成测试**（本机 ASP.NET 最小服务器，双模式：支持 / 不支持 Range）：

- 8 连接分段下载字节级校验。
- 不支持 Range 时单流退化。
- 中途取消后恢复续传，最终文件校验。
- Cookie/Referer/UA 头正确到达服务器。

**手动验收**（Chrome 与 Edge 各一遍）：

1. 点击直链下载 → 确认窗弹出 → 开始下载 → 列表显示多连接进度。
2. 暂停 → 恢复，文件最终完整。
3. 下载中关闭浏览器，下载继续完成。
4. 确认窗选"交回浏览器"，浏览器正常下载且不被二次拦截。
5. 登录态站点附件（带 Cookie）下载成功。
6. 停掉 App 进程后点下载，浏览器 fail-open 自行完成下载。
7. 设置里修改类型列表后，新类型可被拦截。

## 实施顺序建议（供实施计划参考）

1. Core 类库抽取与 IPC 契约 + 封帧复用。
2. 多线程分段引擎 + 断点续传（纯 Core，配集成测试服务器）。
3. App 骨架：单实例、托盘、管道服务端、任务持久化。
4. 主窗口 + 确认弹窗 + 设置窗口。
5. Host 瘦身为代理 + 拉起逻辑。
6. 扩展升级：长连接、类型列表同步、Cookie 收集、旁路名单、fail-open。
7. 端到端手动验收。
