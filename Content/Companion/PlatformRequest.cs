using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

// User dimensions only. Position, protected cells, stock and recipes stay in
// the game; neither chat nor a model can directly confirm this specification.
public sealed record PlatformRequest(int Layers, int Width, int Spacing = 6)
{
    public int Height => (Layers - 1) * Spacing + 1;
    public int Total => Layers * Width;
    public BuildRegion At(BuildPoint lowerLeft) => new(lowerLeft.X, lowerLeft.Y - Height + 1, lowerLeft.X + Width - 1, lowerLeft.Y);
    private const string Number = @"(?<![0-9A-Za-z.\-负零〇一二两三四五六七八九十百千万])(-?\d+(?:\.\d+)?|[负零〇一二两三四五六七八九十百千万]+)";
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool TryParse(string input, out PlatformRequest? request, out string error)
    {
        request = null; error = "";
        if (input.Length > 240) return false;
        string text = input.Normalize(NormalizationForm.FormKC).Trim();
        if (!Regex.IsMatch(text, "平台|platform", RegexOptions.IgnoreCase)) return false;
        var layers = Matches(text, Number + @"\s*(?:层|排|layers?\b|rows?\b)");
        var widths = Matches(text, @"(?:每(?:层|排)\s*(?:长(?:度)?(?:为)?|宽(?:度)?(?:为)?|各|有)?\s*|(?:长|宽)(?:度)?(?:为)?\s*)" + Number + @"\s*(?:格|块|个)");
        if (widths.Count == 0) widths = Matches(text, Number + @"\s*(?:tiles?|blocks?)\s*(?:each|per\s+(?:layer|row))\b");
        var gaps = Matches(text, @"(?:间距|间隔|相隔|gap|spacing)\s*(?:为|是|of)?\s*" + Number + @"\s*(?:格|块|tiles?)?");
        bool dimension = layers.Count + widths.Count + gaps.Count > 0 || Regex.IsMatch(text, @"每层|每排|\d+\s*格");
        if (!dimension) return false; // Bare '搭平台' keeps manual selection.
        if (Regex.IsMatch(text, @"不要|别搭|别建|别铺|不许|先不|取消|以后|记住|how\s+to|不要|怎么|如何", RegexOptions.IgnoreCase)) return false;
        bool action = Regex.IsMatch(text, @"搭|铺|建|造|生成|预览|build|make|preview", RegexOptions.IgnoreCase);
        if (!action && Regex.IsMatch(text, @"多少|材料|\?|？|能不能|是否|need|cost", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"玻璃|红木|珍珠木|棕榈木|乌木|暗影木|竹平台|石平台|铁平台|glass|metal", RegexOptions.IgnoreCase))
        { error = "目前蓝图只支持普通木平台，请明确使用木平台。"; return true; }
        if (layers.Count > 1 || widths.Count > 1 || gaps.Count > 1)
        { error = "尺寸有多种说法，请只给一组层数、每层长度和间距。"; return true; }
        if (widths.Count == 0)
        { error = "请说明每层长度，例如：三层、每层100格木平台，间距6格。"; return true; }
        if (!Read(widths[0].Groups[1].Value, out int width) ||
            !Read(layers.Count == 0 ? "1" : layers[0].Groups[1].Value, out int layer) ||
            !Read(gaps.Count == 0 ? "6" : gaps[0].Groups[1].Value, out int gap))
        { error = "层数、长度和间距都需要正整数，可用中文数字或阿拉伯数字。"; return true; }
        if (layer is < 1 or > PlatformBlueprint.MaxLayers || width is < 4 or > PlatformBlueprint.MaxWidth || gap is < 4 or > 12)
        { error = "蓝图支持1–6层、每层4–160格、间距4–12格；请缩小或调整尺寸。"; return true; }
        request = new(layer, width, gap); return true;
    }
    internal static bool Read(string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return value > 0;
        const string digits = "零一二三四五六七八九";
        text = text.Replace('两', '二').Replace('〇', '零'); value = 0;
        if (text.Length == 0 || text.Length > 12) return false;
        if (text.All(c => digits.Contains(c)))
        { if (text.Length > 4) return false; foreach (char c in text) value = value * 10 + digits.IndexOf(c); return value > 0 && value <= 9999; }
        int digit = -1, lastUnit = 10000;
        foreach (char c in text)
        {
            int n = digits.IndexOf(c);
            if (n >= 0) { if (n == 0) { if (digit > 0) return false; continue; } if (digit >= 0) return false; digit = n; continue; }
            int unit = c switch { '十' => 10, '百' => 100, '千' => 1000, _ => 0 };
            if (unit == 0 || unit >= lastUnit || digit < 0 && !(unit == 10 && value == 0)) return false;
            value += (digit < 0 ? 1 : digit) * unit; digit = -1; lastUnit = unit;
        }
        value += Math.Max(0, digit); return value > 0 && value <= 9999;
    }
}
