# 本 fork 的定制说明

上游：https://github.com/kookxiang/jellyfin-plugin-bangumi

## 分支约定

| 分支 | 作用 | 规则 |
|---|---|---|
| `master` | 上游镜像 | **永不提交**，只做 `merge --ff-only upstream/master`；与上游逐字节一致 |
| `emby` | 本 fork 的产品线 | 所有 Emby 侧改动都在这里；部署和发版都从它出 |
| `emby/*` | 单个功能 / 单个移植项 | 从 `emby` 开，做完 `merge --no-ff` 回 `emby` |

验证 `master` 干净：`git log --oneline upstream/master..master` 应为空。
验证 Jellyfin 侧零改动：`git diff upstream/master..emby -- Jellyfin.Plugin.Bangumi` 应为空。

## 改动分层（同步上游时的冲突预算）

- **L1 新增文件**：只在 `Emby.Plugin.Bangumi/` 下新建文件、改 `Configuration/ConfigPage.html` / `.js`。冲突风险零。
- **L2 条件编译**：在 `Jellyfin.Plugin.Bangumi/*.cs` 里加 `#if EMBY ... #endif`（1~10 行）。该文件从此进入下面的"已触碰清单"。
- **L3 脱钩复制**：复制文件到 `Emby.Plugin.Bangumi/Shared/` 并删掉 csproj 里对应的 `<Compile Include="..\Jellyfin...">`。该文件不再跟随上游。

## 已触碰的上游文件（L2 / L3，每次同步后必须人工核对）

| 文件 | 层级 | 改了什么 | 为什么 |
|---|---|---|---|
| _（暂无）_ | | | |

> 上游 `Emby.Plugin.Bangumi.csproj` 链接的共享文件：`BangumiApi.cs`、`Configuration/PluginConfiguration.cs`、`Model/*.cs`、`Utils/ImageUrlNormalizer.cs`、`OAuth/*.cs`（排除 `OAuthController.cs`）、`ScheduledTask/RatingRefreshTask.cs`、`ScheduledTask/TokenRefreshTask.cs`。改这些等于同时改 Jellyfin 插件。

## 发版规则

- 每次发布递增 `Emby.Plugin.Bangumi/Emby.Plugin.Bangumi.csproj` 里的 `<Version>` / `<AssemblyVersion>` / `<FileVersion>`（当前 **1.7.8.1001**）。
- 编码规则：**`<上游下一版本>.<1000 + 修订号>`**，例如 `1.7.8.1001`、`1.7.8.1002`。留 1000 的偏移是为了不和上游 CI 的 `tag.距tag提交数` 版本撞车（上游 distance 通常远小于 1000）；撞车的后果是两份 dll 的 ETag 相同，配置页会互相“串页”。
- **不要**改根目录的 `Directory.Build.props`：那是上游共享文件，改它会和上游的版本号变更永久冲突。
- 为什么必须递增：Emby 插件页的 `ETag` 由**插件版本**决定（同一插件的 HTML 和 JS 共享同一个 ETag，不同插件各不相同）。版本不变时浏览器带着旧 ETag 发条件请求，服务端回 `304 Not Modified`，用户会一直看到缓存里的旧配置页——表现为「更新插件后配置页没有新选项」。
- 版本号同时决定 Emby 插件列表里显示的版本。
- Release tag 用 `emby-v<dll 版本>-r<N>`，与上游 tag 区分开。

## 新增文件清单（L1）

| 文件 | 作用 |
|---|---|
| `Emby.Plugin.Bangumi/BangumiApi.EmbyCast.cs` | partial 扩展：`GetSubjectRelatedCharacters()`（`/v0/subjects/{id}/characters`）、`GetCharacterDetail()`（`/v0/characters/{id}`）。共享部分 `BaseUrl` 是 private，只能在同类内拼 URL |
| `Emby.Plugin.Bangumi/Cast/CastLocalizer.cs` | **角色名中文化**：只把 `PersonInfo.Role` 换成 Bangumi infobox 里的「简体中文名」，**不动 `Name`**（理由见下）；缓存：角色列表按 subject 缓存 10 分钟（同一 subject 一轮刷新只请求一次）、角色中文名进程内缓存并落盘到 `BangumiCast.json`；并发 3、单条目上限 20 角色 |

## 已实现的功能（相对上游 Emby）

| 功能 | 开关 | 实现位置 | 备注 |
|---|---|---|---|
| 角色名中文化 | 配置页「人物翻译」= 优先使用中文翻译（`PersonTranslationPreference`） | `Cast/CastLocalizer.cs` + 三个 provider 调用点 | 语义来自上游 PR #256（该 PR 用 `#if !EMBY` 把 Emby 排除了），但**只翻 Role**：Role 是「条目 ↔ 人物」关联字段，改它不新建人物实体，切回上游刷新即可还原；`Name` 决定人物实体身份，改它会留下重复人物 |
| 配置页补上「人物翻译」下拉框 | — | `Configuration/ConfigPage.html` | 共享配置里一直有 `PersonTranslationPreference`（默认 `Original`），但 Emby 配置页从未提供入口，导致该开关在 Emby 侧改不了 |

已知限制（详见 `PLAN`/对话记录）：

- 翻译请求为 `1 + N角色` 次/条目（首次、缓存未命中时）；角色列表 10 分钟内复用，角色中文名落盘后重启不再请求。不再请求 `/v0/persons/{id}`。
- **演员名保持原名**（不翻译 `Name`）。这样与上游插件互换时人物实体不变、不会产生重复人物；代价是演职人员列表里演员仍显示日文名。
- 未实现「把角色当作演职人员」（PR #256 的 `AddCharacterToPerson`）：Emby 不认 `c{id}` 前缀、没有人物图片 provider，会污染全局人物库。

## 移植清单（Jellyfin → Emby）

| 功能 | Jellyfin 源文件 | Emby 目标 | 状态 |
|---|---|---|---|
| 人物头像 | `Providers/PersonImageProvider.cs` | `ExternalIdProvider/PersonImageProvider.cs` | 待做 |
| 合集 | `Providers/BoxSetProvider.cs` | `ExternalIdProvider/BoxSetProvider.cs` | 待做 |
| 书籍 | `Providers/BookProvider.cs` | `ExternalIdProvider/BookProvider.cs` | 待做 |
| 音乐艺术家 | `Providers/MusicArtistProvider.cs` | `ExternalIdProvider/MusicArtistProvider.cs` | 待做 |
| 音乐专辑 | `Providers/AlbumProvider.cs` | `ExternalIdProvider/AlbumProvider.cs` | 待做 |
| 音乐歌曲 | `Providers/MusicSongProvider.cs` | `ExternalIdProvider/MusicSongProvider.cs` | 待做 |
| 缺集补齐 | `Providers/MissingEpisodeProvider.cs`、`MissingEpisodeCleanupProvider.cs` | 待评估（上游标注为 Jellyfin 专属） | 待做 |
| 剧集预刷新 | `Providers/EpisodePreRefreshProvider.cs` | 待评估（Emby 是否有等价钩子） | 待做 |

移植时的固定套路：

1. 先读两份实现：Jellyfin 的 `Providers/XxxProvider.cs`（业务逻辑）+ Emby 的 `ExternalIdProvider/XxxProvider.cs`（接口/基类骨架）。
2. **以 Emby 侧骨架为准**，把 Jellyfin 的逻辑搬进来。
3. Emby 侧 `Plugin.cs` 没有注册代码，新 provider 类丢进程序集即被发现；不要照抄 Jellyfin 的 `PluginServiceRegistrator`。
4. 注意 C# 语言版本：上游把 `LangVersion` 拼成了 `LangvVersion`（不生效），net6.0 默认落到 C# 10 —— 别用 C# 11/12 语法。

## 本地验证环境

| 项 | 值 |
|---|---|
| Emby | `D:\Emby`（便携版，数据在 `D:\Emby\programdata`），启动脚本 `D:\Emby\start-emby.ps1` |
| Emby 版本 | **4.10.2.0**（下载的是 4.9.1.90 包，首次启动自动更新到 4.10.2） |
| 插件部署位置 | `D:\Emby\programdata\plugins\`（**dll 直接放这里，不建子目录**） |
| .NET SDK | 10.0.404（`C:\Program Files\dotnet`） |
| 验证结果 | 插件加载成功：`Loading Emby.Plugin.Bangumi, Version=1.7.1.0` + `Starting entry point ...PlaybackScrobbler` |

构建 + 部署一条命令：

```powershell
dotnet build Emby.Plugin.Bangumi/Emby.Plugin.Bangumi.csproj -c Release
Copy-Item Emby.Plugin.Bangumi\bin\Release\net6.0\Emby.Plugin.Bangumi.dll D:\Emby\programdata\plugins\ -Force
# 然后重启 Emby（D:\Emby\start-emby.ps1）
```

## 同步上游后的检查

- [ ] `dotnet build Emby.Plugin.Bangumi/Emby.Plugin.Bangumi.csproj -c Release` 通过
- [ ] `git diff upstream/master..emby -- Jellyfin.Plugin.Bangumi` 为空
- [ ] 手测：搜索匹配、剧集、剧照、人物、播放 scrobble
- [ ] 本文件"移植清单"逐条回归
- [ ] 打 tag：`git tag emby-vX.Y.Z && git push origin --tags`
