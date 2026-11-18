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

## 新增文件清单（L1）

| 文件 | 作用 |
|---|---|
| _（暂无）_ | |

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
