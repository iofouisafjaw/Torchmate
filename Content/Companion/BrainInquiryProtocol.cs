using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrarAI.Content.Companion;

// Keep remote provenance intact; a caller's game version is never source evidence.
public static class BrainInquiryProtocol
{
    // The vanilla Terraria Wiki and the Calamity Mod wikis (Chinese and official); nothing else is shown as a source.
    private static readonly (string Host, string[] Paths)[] Sites = {
        ("terraria.wiki.gg", new[] { "/wiki/", "/zh/wiki/" }),
        ("calamitymod.wiki.gg", new[] { "/wiki/", "/zh/wiki/" }),
        ("calamity.huijiwiki.com", new[] { "/wiki/" }),
    };

    public static bool TrustedSite(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        Sites.Any(s => uri.Host == s.Host && s.Paths.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.Ordinal)));

    public static WikiInquiryResult Parse(string body, string version)
    {
        version = WikiKnowledge.NormalizeVersion(version);
        if (version.Length == 0) throw new FormatException("无法核对当前游戏版本。");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string S(JsonElement node, string name) => node.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var sources = new List<WikiSource>();
        if (root.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
            {
                string sourceVersion = S(s, "game_version"), url = S(s, "url");
                if (sourceVersion != version || (url.Length > 0 &&
                    (S(root, "game_version") != version || string.IsNullOrWhiteSpace(S(root, "evidence")))))
                    throw new FormatException("Wiki 来源版本未知或与当前游戏不匹配，已阻止显示。");
                if (url.Length > 0 && !TrustedSite(url))
                    throw new FormatException("Wiki 来源地址无法核对。");
                sources.Add(new WikiSource(s.GetProperty("id").GetInt32(), S(s, "title"), url,
                    S(s, "excerpt"), sourceVersion, ""));
            }
        if (sources.Select(s => s.Id).Distinct().Count() != sources.Count)
            throw new FormatException("Wiki 来源编号重复。");
        var context = new WikiContext(S(root, "status"), version, S(root, "snapshot"), S(root, "evidence"),
            S(root, "license"), S(root, "attribution"), sources.ToArray());
        string raw = JsonSerializer.Serialize(new { reply = S(root, "reply"), basis = S(root, "basis"),
            citations = root.GetProperty("citations") });
        WikiInquiryProtocol.Parse(raw, context);
        return new WikiInquiryResult(context, raw);
    }
}
