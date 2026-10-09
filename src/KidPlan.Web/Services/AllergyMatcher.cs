using KidPlan.Web.Models;

namespace KidPlan.Web.Services;

/// <summary>
/// 过敏原匹配器。
///
/// 输入是小孩的过敏原列表（每个带等级），输出是某份菜单命中了哪些过敏原。
/// 这里刻意做成纯函数，方便单元测试。
///
/// 匹配规则：
/// 1. 过敏原名字查同义词表，展开成一组相关关键词（如「牛奶」 → 奶、酸奶、奶酪、芝士、黄油、奶油、乳制品、奶粉）；
/// 2. 菜单 / 食材文本里只要出现这组关键词中的任意一个，就算命中；
/// 3. 只是**同一大类**的不同写法都算命中，但不同过敏原之间互不牵连
///    （对牛奶过敏不会被判成对鸡蛋过敏）。
///
/// 关键词表只覆盖最常见的儿童食物过敏原，宁可多报（提示家长确认）也不要漏报。
/// </summary>
public static class AllergyMatcher
{
    // 注意：不要把“和/及/与”当作分隔符，否则“和牛”“及第粥”这类菜名会被切坏。
    private static readonly char[] Separators =
        [',', '，', '、', ';', '；', '/', '\\', '|', ' ', '\t', '\r', '\n', '+'];

    /// <summary>常见过敏原 → 需要一起检查的相关关键词。</summary>
    private static readonly Dictionary<string, string[]> SynonymGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["牛奶"] = ["牛奶", "奶", "酸奶", "奶酪", "芝士", "黄油", "奶油", "乳制品", "奶粉", "炼乳", "奶昔"],
        ["鸡蛋"] = ["鸡蛋", "蛋", "蛋清", "蛋黄", "蛋糕", "蛋挞", "蛋卷", "鸡蛋羹"],
        ["花生"] = ["花生", "花生酱", "花生油", "花生米"],
        ["坚果"] = ["坚果", "核桃", "腰果", "杏仁", "开心果", "榛子", "夏威夷果", "碧根果", "松子"],
        ["海鲜"] = ["海鲜", "虾", "蟹", "贝", "蛤", "生蚝", "牡蛎", "鱿鱼", "章鱼", "海参"],
        ["鱼"] = ["鱼", "三文鱼", "鳕鱼", "鲈鱼", "带鱼", "黄花鱼", "鱼油"],
        ["小麦"] = ["小麦", "面粉", "面包", "面条", "馒头", "包子", "饺子", "饼干", "麸质", "面筋"],
        ["大豆"] = ["大豆", "黄豆", "豆浆", "豆腐", "豆制品", "酱油", "腐竹", "豆皮"],
        ["芝麻"] = ["芝麻", "芝麻酱", "芝麻油", "香油"],
        ["芒果"] = ["芒果"],
        ["菠萝"] = ["菠萝", "凤梨"],
        ["猕猴桃"] = ["猕猴桃", "奇异果"],
        ["草莓"] = ["草莓"],
        ["桃子"] = ["桃子", "水蜜桃", "黄桃"],
        ["柑橘"] = ["柑橘", "橙子", "橘子", "柚子", "柠檬"],
        ["牛肉"] = ["牛肉", "牛腩", "牛排"],
        ["羊肉"] = ["羊肉", "羊排"],
        ["猪肉"] = ["猪肉", "排骨", "五花肉", "里脊"],
        ["玉米"] = ["玉米", "玉米面", "玉米油"],
        ["燕麦"] = ["燕麦", "燕麦片"],
        ["蜂蜜"] = ["蜂蜜"],
        ["添加剂"] = ["色素", "防腐剂", "香精", "味精", "亚硝酸盐"],
    };

    /// <summary>
    /// 把自由文本拆成去重后的词条列表。新增过敏原时依旧允许「一次贴一串」，
    /// 由调用方拆成多条 <see cref="ChildAllergen"/>。
    /// </summary>
    public static IReadOnlyList<string> SplitTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 找出菜单命中的过敏原。只有 <see cref="AllergenKind.Food"/> 的条目参与比对，
    /// 药物 / 环境类过敏原拿菜名去比是没意义的。
    /// </summary>
    /// <param name="allergens">小孩登记的过敏原。</param>
    /// <param name="foodTexts">这一餐的菜单 / 食材等文本，可以传多个。</param>
    /// <returns>命中的过敏原及触发命中的关键词（按等级从高到低排序）。</returns>
    public static IReadOnlyList<AllergyMatch> FindMatches(
        IEnumerable<ChildAllergen>? allergens,
        params string?[] foodTexts)
    {
        if (allergens is null)
        {
            return [];
        }

        var combined = string.Join(' ', foodTexts.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (combined.Length == 0)
        {
            return [];
        }

        var results = new List<AllergyMatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var allergen in allergens)
        {
            if (allergen.Kind != AllergenKind.Food || string.IsNullOrWhiteSpace(allergen.Name))
            {
                continue;
            }

            var name = allergen.Name.Trim();
            if (!seen.Add(name))
            {
                continue;
            }

            var keywords = SynonymGroups.TryGetValue(name, out var group) ? group : [name];

            foreach (var keyword in keywords)
            {
                if (!combined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new AllergyMatch(name, keyword, allergen.Severity, allergen.Kind));
                break;
            }
        }

        // 最危险的排最前面：模板里直接 foreach，不用每个页面各自再排一次。
        return results
            .OrderByDescending(m => m.Severity)
            .ThenBy(m => m.Allergen, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>命中的最高等级；没有命中就是 null。</summary>
    public static AllergySeverity? MaxSeverity(IEnumerable<AllergyMatch> matches)
    {
        AllergySeverity? max = null;
        foreach (var match in matches)
        {
            if (max is null || match.Severity > max)
            {
                max = match.Severity;
            }
        }

        return max;
    }
}

/// <summary>一条过敏命中：哪个过敏原被哪个关键词触发，以及它的危险程度。</summary>
/// <param name="Allergen">小孩登记的过敏原词。</param>
/// <param name="MatchedKeyword">在菜单里实际命中的关键词。</param>
/// <param name="Severity">该过敏原的等级。</param>
/// <param name="Kind">食物还是非食物（命中一定是食物）。</param>
public sealed record AllergyMatch(
    string Allergen,
    string MatchedKeyword,
    AllergySeverity Severity = AllergySeverity.Moderate,
    AllergenKind Kind = AllergenKind.Food)
{
    /// <summary>命中之后该用什么力度处理。</summary>
    public AllergyRiskAction RiskAction => Severity.ToRiskAction();

    public string Describe() => $"{Allergen}（{Severity.ToChinese()}，命中「{MatchedKeyword}」）";
}
