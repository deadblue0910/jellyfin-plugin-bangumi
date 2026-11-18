using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Configuration;
using Jellyfin.Plugin.Bangumi.Model;
using Jellyfin.Plugin.Bangumi.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace Jellyfin.Plugin.Bangumi.Cast;

/// <summary>
///     把演职人员列表里的<b>角色名</b>（<see cref="PersonInfo.Role" />）替换成 Bangumi 的中文名。
///     <para>
///         只改 Role、不动 Name：Role 是「条目 ↔ 人物」关联上的字段，改它不会新建人物实体；
///         Name 决定人物实体身份，改了会让 Emby 按新名字建条目，切回上游插件时会留下重复人物。
///     </para>
///     <para>
///         请求策略：角色列表按 subject 缓存 10 分钟；角色中文名进程内缓存并落盘到
///         <c>BangumiCast.json</c>，重启后不重复请求；并发 3，单条目上限 20 个角色。
///     </para>
/// </summary>
public static class CastLocalizer
{
    private const int MaxConcurrency = 3;

    /// <summary>
    ///     单个条目最多发多少次「角色详情」请求。
    ///     超出时只翻译优先度高的角色（主角 → 配角 → 客串），**不再整条跳过**。
    /// </summary>
    private const int MaxLookupsPerSubject = 40;

    /// <summary>
    ///     通用占位角色名：这类条目的「角色名」只是表示「有配音」，没有独立的角色释义，
    ///     查中文名既浪费请求又会把同一个 Role 文本映射成同一个名字；直接跳过。
    /// </summary>
    private static readonly string[] GenericRoleKeywords = new[] { "声の出演", "声のみ", "ボイス", "配音演员" };

    /// <summary>
    ///     角色列表的缓存有效期。一轮刷新里 Series 和 Season 会先后拿同一个 subject，
    ///     靠它把重复请求压成一次。
    /// </summary>
    private static readonly TimeSpan SubjectCacheTtl = TimeSpan.FromMinutes(10);

    private const string CacheFileName = "BangumiCast.json";

    /// <summary>
    ///     角色 id → 中文名；空字符串表示「查过了但没有中文名」，避免重复请求。
    /// </summary>
    private static readonly ConcurrentDictionary<int, string> CharacterNames = new();

    private static readonly ConcurrentDictionary<int, SubjectEntry> SubjectCache = new();

    private static readonly object SyncRoot = new();
    private static bool _loaded;
    private static bool _dirty;

    /// <summary>
    ///     只在配置为「人物优先中文」时启用；默认 Original，零开销。
    /// </summary>
    public static bool Enabled =>
        Plugin.Instance?.Configuration?.PersonTranslationPreference == TranslationPreferenceType.Chinese;

    /// <summary>
    ///     取词条关联的角色（按「主角/配角/客串」排序），带 10 分钟缓存。
    ///     这一步替代了共享的 <c>GetSubjectCharacters</c>，因为翻译需要角色 id，
    ///     而共享实现只返回拍平后的 <see cref="PersonInfo" />。
    /// </summary>
    public static async Task<List<RelatedCharacter>?> GetCharactersAsync(BangumiApi api, int subjectId,
        CancellationToken token)
    {
        if (subjectId <= 0) return null;

        if (SubjectCache.TryGetValue(subjectId, out var cached) && DateTime.UtcNow - cached.CachedAt < SubjectCacheTtl)
            return cached.Characters;

        var characters = await api.GetSubjectRelatedCharacters(subjectId, token).ConfigureAwait(false);
        if (characters == null) return null;

        characters = characters
            .OrderBy(c => c.Relation switch
            {
                "主角" => 0,
                "配角" => 1,
                "客串" => 2,
                _ => 3
            })
            .ToList();

        SubjectCache[subjectId] = new SubjectEntry(DateTime.UtcNow, characters);
        return characters;
    }

    /// <summary>
    ///     把角色列表摊平成演职人员列表，语义与共享的 <c>GetSubjectCharacters</c> 一致
    ///     （每个角色 × 每个演员一条，图片地址归一化）。
    ///     <para>
    ///     返回值保留「这条 PersonInfo 属于哪个角色」的对应关系，回填时按角色 id 精确匹配。
    ///     按名字匹配在「两个不同角色同名」时会互相串名，而 <see cref="PersonInfo.Role" /> 是纯字符串，
    ///     交给 Emby 之后就无法再区分，所以只能在摊平阶段把 id 带出来。
    ///     </para>
    /// </summary>
    public static List<CastEntry> ToPeople(IEnumerable<RelatedCharacter> characters)
    {
        var entries = new List<CastEntry>();
        foreach (var character in characters)
        {
            foreach (var info in character.ToPersonInfos())
            {
                info.ImageUrl = ImageUrlNormalizer.Normalize(info.ImageUrl);
                entries.Add(new CastEntry(info, character.Id));
            }
        }

        return entries;
    }

    /// <summary>
    ///     一条演职人员记录，以及它属于哪个 Bangumi 角色。
    /// </summary>
    public readonly record struct CastEntry(PersonInfo Info, int CharacterId);

    /// <summary>
    ///     就地修改 <paramref name="entries" /> 里每条记录的 Role。<b>任何失败都只影响单个角色</b>，
    ///     最坏情况是保留原名，绝不丢整批演职人员。
    /// </summary>
    public static async Task LocalizeAsync(BangumiApi api, int subjectId, IReadOnlyList<RelatedCharacter> characters,
        IReadOnlyList<CastEntry> entries, ILogger log, CancellationToken token)
    {
        if (!Enabled || subjectId <= 0 || characters == null || characters.Count == 0 || entries == null || entries.Count == 0)
            return;

        EnsureLoaded();

        // 过滤通用占位角色（声の出演 等）后按角色 id 去重（同一条目里角色 id 不会重复，这里只是兜底）。
        var lookups = characters
            .Where(character => !IsGenericRoleName(character.Name))
            .GroupBy(character => character.Id)
            .Select(group => group.First())
            .ToList();

        // 超过预算时只翻优先度高的那部分（characters 已按 主角→配角→客串 排序），不再整条跳过。
        if (lookups.Count > MaxLookupsPerSubject)
            lookups = lookups.Take(MaxLookupsPerSubject).ToList();

        if (lookups.Count == 0)
        {
            log.Debug("Bangumi: 条目 {0} 没有需要翻译的角色名", subjectId);
            return;
        }

        var skipped = characters.Count - lookups.Count;

        try
        {
            using var semaphore = new SemaphoreSlim(MaxConcurrency);

            var tasks = lookups
                .Select(async character => await TranslateCharacterAsync(api, character.Id, semaphore, log, token).ConfigureAwait(false))
                .ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            // 角色 id → 中文名。同名不同角色各自独立，不会再互相串名。
            // 只有「查询成功且确实有中文名」的才进映射；查询失败的不缓存，下次刷新会重试。
            var roleMap = new Dictionary<int, string>();
            var succeeded = 0;
            var failed = 0;
            for (var i = 0; i < results.Length; i++)
            {
                var result = results[i];
                if (!result.Ok)
                {
                    failed++;
                    continue;
                }

                succeeded++;
                if (!string.IsNullOrEmpty(result.Translated))
                    roleMap[lookups[i].Id] = result.Translated;
            }

            if (skipped > 0)
                log.Info("Bangumi: 条目 {0} 共 {1} 个角色，跳过 {2} 个通用/超限，实际查询 {3} 个",
                    subjectId, characters.Count, skipped, lookups.Count);

            if (roleMap.Count == 0)
            {
                if (failed > 0)
                    log.Warn("Bangumi: 条目 {0} 有 {1} 个角色查询失败（未缓存，下次刷新重试），本次无翻译",
                        subjectId, failed);
                else if (succeeded > 0)
                    log.Info("Bangumi: 条目 {0} 查询了 {1} 个角色，均无简体中文名，保持原名", subjectId, succeeded);
                return;
            }

            var changed = 0;
            foreach (var entry in entries)
            {
                if (!roleMap.TryGetValue(entry.CharacterId, out var role)) continue;
                if (entry.Info.Role == role) continue;
                entry.Info.Role = role;
                changed++;
            }

            if (changed > 0)
                log.Info("Bangumi: 条目 {0} 已翻译 {1} 个角色名（查询 {2} 个，失败 {3} 个）",
                    subjectId, changed, succeeded, failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Error("Bangumi: 翻译角色名失败 (subject {0}): {1}", subjectId, ex);
        }
        finally
        {
            SaveCache(log);
        }
    }

    /// <summary>
    ///     判断是否是「声の出演」这类通用占位角色名：角色名本身没有角色释义，
    ///     查中文名既浪费请求，又会让同一个 Role 文本被错误地映射成某个具体角色名。
    /// </summary>
    private static bool IsGenericRoleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        foreach (var keyword in GenericRoleKeywords)
            if (name.Contains(keyword, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    ///     查询单个角色的简体中文名。
    ///     返回值里的 <c>Ok</c> 表示这次查询是否真的成功：
    ///     只有成功才允许写缓存，避免一次网络故障把「没有中文名」永久钉死。
    ///     成功但没有中文名时缓存空字符串，下次直接跳过。
    /// </summary>
    private static async Task<(bool Ok, string Translated)> TranslateCharacterAsync(BangumiApi api, int id,
        SemaphoreSlim semaphore, ILogger log, CancellationToken token)
    {
        if (id <= 0) return (false, string.Empty);
        if (CharacterNames.TryGetValue(id, out var cached)) return (true, cached);

        await semaphore.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (CharacterNames.TryGetValue(id, out cached)) return (true, cached);

            var detail = await api.GetCharacterDetail(id, token).ConfigureAwait(false);
            if (detail == null)
            {
                // 请求失败/被限流：不写缓存，下次刷新重试。
                log.Debug("Bangumi: 角色 {0} 查询无结果，本次不缓存", id);
                return (false, string.Empty);
            }

            var translated = detail.TranslatedName;
            var value = string.IsNullOrEmpty(translated) || translated == detail.Name ? string.Empty : translated;
            CharacterNames[id] = value;
            _dirty = true;
            return (true, value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Debug("Bangumi: 角色 {0} 翻译失败（不缓存）: {1}", id, ex.Message);
            return (false, string.Empty);
        }
        finally
        {
            semaphore.Release();
        }
    }

    #region 落盘缓存

    private sealed class CacheDocument
    {
        /// <summary>
        ///     2 = 早期版本（把「查询失败」也写成空字符串，会永久钉死）；
        ///     3 = 只缓存成功结果，空字符串明确表示「查过，确实没有中文名」。
        /// </summary>
        public int Version { get; set; } = 3;

        public Dictionary<int, string> Characters { get; set; } = new();
    }

    private sealed record SubjectEntry(DateTime CachedAt, List<RelatedCharacter> Characters);

    private static string? GetCacheFilePath()
    {
        var configPath = Plugin.Instance?.ConfigurationFilePath;
        if (string.IsNullOrEmpty(configPath)) return null;
        var dir = Path.GetDirectoryName(configPath);
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, CacheFileName);
    }

    private static void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_loaded) return;
            _loaded = true;

            var file = GetCacheFilePath();
            if (file == null || !File.Exists(file)) return;

            try
            {
                var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(file));
                if (document?.Characters == null) return;

                // 旧版本会把失败写成空串，升级时丢掉这些空值，让它们重新查一次。
                var dropEmpty = document.Version < 3;

                foreach (var pair in document.Characters)
                {
                    if (dropEmpty && string.IsNullOrEmpty(pair.Value)) continue;
                    CharacterNames.TryAdd(pair.Key, pair.Value);
                }
            }
            catch
            {
                // 缓存文件损坏：当作没有缓存，下次写入会覆盖。
            }
        }
    }

    private static void SaveCache(ILogger log)
    {
        if (!_dirty) return;

        lock (SyncRoot)
        {
            if (!_dirty) return;

            var file = GetCacheFilePath();
            if (file == null)
            {
                log.Warn("Bangumi: 无法确定插件配置目录，角色名缓存不会落盘（每次刷新都会重新查询）");
                return;
            }

            try
            {
                var document = new CacheDocument
                {
                    Characters = CharacterNames.ToDictionary(pair => pair.Key, pair => pair.Value)
                };
                File.WriteAllText(file, JsonSerializer.Serialize(document));
                _dirty = false;
                log.Debug("Bangumi: 角色名缓存已写入 {0}（{1} 条）", file, CharacterNames.Count);
            }
            catch (Exception ex)
            {
                // 写盘失败不影响本次翻译结果，但要让日志里能看见，否则会长期静默。
                log.Warn("Bangumi: 写入角色名缓存失败 {0}: {1}", file, ex.Message);
            }
        }
    }

    #endregion
}
