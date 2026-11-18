using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Model;

namespace Jellyfin.Plugin.Bangumi;

/// <summary>
///     Emby 侧的 Bangumi API 扩展。
///     共享的 <c>GetSubjectCharacters</c> 只返回 <c>PersonInfo</c>（角色名被拍平进 Role），
///     拿不到「角色 id → 角色名」的对应关系，所以这里单独暴露原始结构。
///     BaseUrl 在共享部分是 private，只有同类内（partial）才能拼 URL。
/// </summary>
public partial class BangumiApi
{
    /// <summary>
    ///     取词条关联的角色（含演员列表），用于建立角色名 / 演员名的翻译映射。
    /// </summary>
    public async Task<List<RelatedCharacter>?> GetSubjectRelatedCharacters(int subjectId, CancellationToken token)
    {
        if (subjectId <= 0) return null;
        var characters = await Get<IEnumerable<RelatedCharacter>>($"{BaseUrl}/v0/subjects/{subjectId}/characters", token);
        return characters?.ToList();
    }

    /// <summary>
    ///     取角色详情。<c>PersonDetail.TranslatedName</c> 会按 <c>PersonTranslationPreference</c>
    ///     取 infobox 里的「简体中文名」，没有则回退原名。
    /// </summary>
    public Task<PersonDetail?> GetCharacterDetail(int id, CancellationToken token)
    {
        return id <= 0 ? Task.FromResult<PersonDetail?>(null) : Get<PersonDetail>($"{BaseUrl}/v0/characters/{id}", token);
    }
}
