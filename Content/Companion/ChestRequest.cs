using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public enum ChestOperation { Query, Deposit, Withdraw }
public sealed record ChestItemRequest(string Name, int Count); // Deposit count 0 = all unfavorited stock.
public sealed record ChestRequest(ChestOperation Operation, IReadOnlyList<ChestItemRequest> Items)
{
    public const int MaxCount = 9999;
    private const string Box = @"(?:(?:附近|旁边|周围)的?)?(?:箱子|宝箱|箱)(?:里|中|内)?";
    private const string Lead = @"^(?:(?:请|帮我|麻烦你|麻烦|给我)\s*)?";
    private const string Num = @"([0-9负零〇一二两三四五六七八九十百千万.\-]+)";
    public static bool TryParse(string input, out ChestRequest? request, out string error)
    {
        request = null; error = "";
        if (input.Length > 240) return false;
        string text = input.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '！', '!');
        if (!Regex.IsMatch(text, "箱|chest", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"以后|记住|不要|别存|别取|别拿|别放|取消|怎么|如何|[？?]|吗$")) return false;
        string deposit = @"(?:存入|存进|存到|放进|放入|放到)";
        var m = Regex.Match(text, Lead + @"(?:把)?(?<items>.+?)(?:都|全部)?" + deposit + Box + "$");
        ChestOperation op = ChestOperation.Deposit;
        if (!m.Success) m = Regex.Match(text, Lead + deposit + Box + @"\s*(?<items>.+)$");
        if (!m.Success) m = Regex.Match(text, Lead + @"(?:存|存放)(?<items>.+?)(?:到|进)" + Box + "$");
        if (!m.Success)
        {
            op = ChestOperation.Withdraw;
            m = Regex.Match(text, Lead + @"(?:从|去)?" + Box + @"(?:给我|帮我)?(?:取出|取|拿出|拿)(?<items>.+?)(?:出来)?$");
        }
        if (!m.Success)
        {
            // Only bounded forms are consumed locally; a lookup never moves items.
            var query = Regex.Match(text, Lead + @"(?:查看|看看|查询|查查|查|翻翻|翻一下)" + Box + @"(?:的)?(?<filter>.*)$");
            if (query.Success && !Regex.IsMatch(query.Groups["filter"].Value, "存|取|拿|放|合成|制作|购买"))
            {
                string filter = query.Groups["filter"].Value.Trim();
                if (filter is "有什么" or "有啥" or "物品" or "东西") filter = "";
                request = new(ChestOperation.Query, filter.Length == 0 ? Array.Empty<ChestItemRequest>() : new[] { new ChestItemRequest(filter, 0) }); return true;
            }
            if (Regex.IsMatch(text, Lead + Box + @"(?:有啥|有什么|有什么东西)$"))
            { request = new(ChestOperation.Query, Array.Empty<ChestItemRequest>()); return true; }
            return false;
        }
        string body = Regex.Replace(m.Groups["items"].Value, @"^(?:背包里(?:的)?|背包的)", "");
        var atoms = Regex.Split(body, @"\s*(?:和|、|,|，|以及)\s*");
        if (atoms.Length > 8 || atoms.Any(string.IsNullOrWhiteSpace))
        { error = "一次请指定1–8种物品，并写清物品全名。"; return true; }
        var result = new List<ChestItemRequest>();
        foreach (string atom in atoms)
        {
            string value = atom.Trim();
            bool all = Regex.IsMatch(value, @"^(?:全部|所有)|(?:全部|所有|都)$");
            value = Regex.Replace(Regex.Replace(value, @"^(?:全部|所有)", ""), @"(?:全部|所有|都)$", "").Trim();
            var qty = Regex.Match(value, "^" + Num + @"\s*(?:个|块|根|份|瓶|件|把|张)\s*(?<name>.+)$");
            if (!qty.Success) qty = Regex.Match(value, @"^(?<name>.+?)\s*(?:×|x)\s*" + Num + "$");
            int count = 0; string name = value;
            if (qty.Success)
            {
                if (all || !PlatformRequest.Read(qty.Groups[1].Value, out count) || count > MaxCount)
                { error = "数量需为1–9999的整数；不要同时指定数量和全部。"; return true; }
                name = qty.Groups["name"].Value.Trim();
            }
            else if (Regex.IsMatch(value, @"^[0-9负零〇一二两三四五六七八九十百千万.\-]+"))
            { error = "请用明确个数，例如：从附近箱子取5个铁锭；不按组数取。"; return true; }
            if (op == ChestOperation.Withdraw && count == 0)
            { error = "取出时请给数量，例如：从附近箱子取5个铁锭。"; return true; }
            if (name.Length is < 1 or > 40 || Regex.IsMatch(name, @"来做|然后|之后|除了|剩下|保留|留着"))
            { error = "请单独指定存取的物品和数量；合成任务直接说要做什么，我会自动查附近箱子补料。"; return true; }
            result.Add(new(name, count));
        }
        if (result.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
        { error = "同一物品请只写一次，避免重复存取。"; return true; }
        request = new(op, result); return true;
    }

    public static bool Authorizes(string goal, ChestOperation operation, string item, int count,
        IReadOnlyList<AgentStep> steps, Func<string, int> resolve)
    {
        if (operation == ChestOperation.Query || !TryParse(goal, out var request, out _) || request == null ||
            request.Operation != operation || request.Items.Count != 1) return false;
        int type = resolve(item);
        return type > 0 && resolve(request.Items[0].Name) == type && request.Items[0].Count == count &&
            !steps.Any(s => s.Tool is "chest_put" or "chest_take" && AgentProtocol.IsSuccess(s.Result));
    }
}
