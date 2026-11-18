using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace Jellyfin.Plugin.Bangumi.Cast;

/// <summary>
///     把演职人员列表里的演员名（<see cref="PersonInfo.Name" />）和角色名（<see cref="PersonInfo.Role" />）
///     替换成 Bangumi 的中文名，对应上游 PR #256 的「演职人员界面支持中文」。
///     <para>
///         数据来源：<c>/v0/subjects/{id}/characters</c>（角色 + 演员）→ 每个角色一次
///         <c>/v0/characters/{id}</c>、每个演员一次 <c>/v0/persons/{id}</c>。
///         Emby 侧没有 HTTP 缓存层，所以这里做进程内缓存并限制单条目请求量。
///     </para>
/// </summary>
public static class CastLocalizer
{
    private const int MaxConcurrency = 3;

    /// <summary>
    ///     单个条目最多翻译多少个角色；超出直接跳过，避免一次刷新打出上百个请求。
    /// </summary>
    private const int MaxCharactersPerSubject = 20;

    /// <summary>
    ///     单个条目最多翻译多少个演员。
    /// </summary>
    private const int MaxActorsPerSubject = 40;

    /// <summary>
    ///     角色 id → 中文名；空字符串表示「查过了但没有中文名」，避免重复请求。
    /// </summary>
    private static readonly ConcurrentDictionary<int, string> CharacterNameCache = new();

    /// <summary>
    ///     人物 id → 中文名；同样带负缓存。
    /// </summary>
    private static readonly ConcurrentDictionary<int, string> PersonNameCache = new();

    /// <summary>
    ///     只在配置为「人物优先中文」时启用；默认 Original，零开销。
    /// </summary>
    public static bool Enabled =>
        Plugin.Instance?.Configuration?.PersonTranslationPreference == TranslationPreferenceType.Chinese;

    /// <summary>
    ///     就地修改 <paramref name="people" />。<b>任何失败都只影响单条记录</b>，
    ///     最坏情况是保留原名，绝不丢整批演职人员。
    /// </summary>
    public static async Task LocalizeAsync(BangumiApi api, int subjectId, IReadOnlyCollection<PersonInfo> people,
        ILogger log, CancellationToken token)
    {
        if (!Enabled || subjectId <= 0 || people == null || people.Count == 0)
            return;

        try
        {
            var characters = await api.GetSubjectRelatedCharacters(subjectId, token).ConfigureAwait(false);
            if (characters == null || characters.Count == 0)
                return;

            if (characters.Count > MaxCharactersPerSubject)
            {
                log.Info("Bangumi: 条目 {0} 有 {1} 个角色，超过上限 {2}，跳过演职人员翻译",
                    subjectId, characters.Count, MaxCharactersPerSubject);
                return;
            }

            using var semaphore = new SemaphoreSlim(MaxConcurrency);

            // 1) 角色名：character.Name → 简体中文名
            var roleTasks = characters
                .Select(async character => (
                    character.Name,
                    Translated: await TranslateCharacterAsync(api, character.Id, semaphore, log, token).ConfigureAwait(false)))
                .ToList();
            var roleResults = await Task.WhenAll(roleTasks).ConfigureAwait(false);

            var roleMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (original, translated) in roleResults)
                if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(translated) && original != translated)
                    roleMap[original] = translated;

            // 2) 演员名：PersonInfo 的 ProviderIds 里就是演员的 Bangumi id
            var actorIds = new List<int>();
            foreach (var person in people)
            {
                if (person.ProviderIds == null) continue;
                if (!person.ProviderIds.TryGetValue(Constants.ProviderName, out var raw)) continue;
                if (!int.TryParse(raw, out var actorId) || actorId <= 0) continue;
                if (actorIds.Contains(actorId)) continue;
                actorIds.Add(actorId);
                if (actorIds.Count >= MaxActorsPerSubject) break;
            }

            var personTasks = actorIds
                .Select(async id => (Id: id, Name: await TranslatePersonAsync(api, id, semaphore, log, token).ConfigureAwait(false)))
                .ToList();
            var personResults = await Task.WhenAll(personTasks).ConfigureAwait(false);

            var personMap = personResults
                .Where(r => !string.IsNullOrEmpty(r.Name))
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First().Name);

            // 3) 回填
            var changed = 0;
            foreach (var person in people)
            {
                if (person.ProviderIds != null
                    && person.ProviderIds.TryGetValue(Constants.ProviderName, out var raw)
                    && int.TryParse(raw, out var actorId)
                    && personMap.TryGetValue(actorId, out var name)
                    && !string.IsNullOrEmpty(name)
                    && name != person.Name)
                {
                    person.Name = name;
                    changed++;
                }

                if (!string.IsNullOrEmpty(person.Role)
                    && roleMap.TryGetValue(person.Role, out var role)
                    && role != person.Role)
                {
                    person.Role = role;
                    changed++;
                }
            }

            if (changed > 0)
                log.Info("Bangumi: 条目 {0} 已翻译 {1} 处演职人员", subjectId, changed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Error("Bangumi: 翻译演职人员失败 (subject {0}): {1}", subjectId, ex);
        }
    }

    private static async Task<string> TranslateCharacterAsync(BangumiApi api, int id, SemaphoreSlim semaphore,
        ILogger log, CancellationToken token)
    {
        if (id <= 0) return string.Empty;
        if (CharacterNameCache.TryGetValue(id, out var cached)) return cached;

        await semaphore.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (CharacterNameCache.TryGetValue(id, out cached)) return cached;
            var detail = await api.GetCharacterDetail(id, token).ConfigureAwait(false);
            var translated = detail?.TranslatedName ?? string.Empty;
            CharacterNameCache[id] = translated;
            return translated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Debug("Bangumi: 角色 {0} 翻译失败: {1}", id, ex.Message);
            CharacterNameCache[id] = string.Empty;
            return string.Empty;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<string> TranslatePersonAsync(BangumiApi api, int id, SemaphoreSlim semaphore,
        ILogger log, CancellationToken token)
    {
        if (id <= 0) return string.Empty;
        if (PersonNameCache.TryGetValue(id, out var cached)) return cached;

        await semaphore.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (PersonNameCache.TryGetValue(id, out cached)) return cached;
            var detail = await api.GetPerson(id, token).ConfigureAwait(false);
            var translated = detail?.TranslatedName ?? string.Empty;
            PersonNameCache[id] = translated;
            return translated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Debug("Bangumi: 人物 {0} 翻译失败: {1}", id, ex.Message);
            PersonNameCache[id] = string.Empty;
            return string.Empty;
        }
        finally
        {
            semaphore.Release();
        }
    }
}
