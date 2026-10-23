using System.Text.Json;
using System.Text.Json.Nodes;
using TerrarAI.Content.Companion;

static class BrainInquiryTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string version = "1.4.4.9";
        string Body(string sourceVersion = version, string corpusVersion = version, string evidence = "fixture") =>
            JsonSerializer.Serialize(new { reply = "1 木材制作 2 木平台。", basis = "wiki", citations = new[] { 1 },
                game_version = corpusVersion, evidence, sources = new[] { new { id = 1, title = "平台",
                    url = "https://terraria.wiki.gg/zh/wiki/平台", excerpt = "1 木材制作 2 木平台。", game_version = sourceVersion } } });
        var valid = BrainInquiryProtocol.Parse(Body(), "Terraria v1.4.4.9");
        check(valid.Wiki.Sources[0].GameVersion == version &&
            WikiInquiryProtocol.Parse(valid.Raw, valid.Wiki).Basis == "wiki", "Brain preserves matching source versions and accepts normalized runtime versions");
        foreach (var (body, runtime) in new[] {
            (Body(sourceVersion: "1.4.5.8"), version), (Body(sourceVersion: ""), version),
            (Body(corpusVersion: "1.4.5.8"), version), (Body(corpusVersion: ""), version),
            (Body(evidence: " "), version), (Body(), "1.4.5.8"), (Body(), "unknown") })
        {
            bool rejected = false;
            try { BrainInquiryProtocol.Parse(body, runtime); } catch (FormatException) { rejected = true; }
            check(rejected, "Brain rejects unknown/mismatched provenance without relabeling it as the game version");
        }
        var legacy = JsonNode.Parse(Body())!;
        legacy["sources"]![0]!.AsObject().Remove("game_version");
        bool legacyRejected = false;
        try { BrainInquiryProtocol.Parse(legacy.ToJsonString(), version); } catch (FormatException) { legacyRejected = true; }
        check(legacyRejected, "legacy Brain sources without an explicit version fail closed");
        var foreign = JsonNode.Parse(Body())!;
        foreign["sources"]![0]!["url"] = "https://untrusted.invalid/article";
        bool foreignRejected = false;
        try { BrainInquiryProtocol.Parse(foreign.ToJsonString(), version); } catch (FormatException) { foreignRejected = true; }
        check(foreignRejected, "matching version labels cannot authorize a foreign Wiki source");
        var calamity = JsonNode.Parse(Body())!;
        calamity["sources"]![0]!["url"] = "https://calamity.huijiwiki.com/wiki/%E7%81%BE%E5%8E%84";
        check(BrainInquiryProtocol.Parse(calamity.ToJsonString(), version).Wiki.Sources.Length == 1,
            "a Calamity wiki page with matching provenance is accepted");
        check(!BrainInquiryProtocol.TrustedSite("https://calamity.huijiwiki.com.evil.invalid/wiki/x") &&
            !BrainInquiryProtocol.TrustedSite("http://calamitymod.wiki.gg/wiki/x") &&
            !BrainInquiryProtocol.TrustedSite("https://calamitymod.wiki.gg/index.php?title=x"),
            "look-alike hosts, plain http and non-article paths are not trusted");
        var world = BrainInquiryProtocol.Parse("""{"reply":"剩余生命43。","basis":"world","citations":[],"sources":[],"game_version":"","status":"Wiki 已隔离"}""", version);
        check(WikiInquiryProtocol.Parse(world.Raw, world.Wiki).Basis == "world", "quarantined Wiki does not prevent world-only answers");
        var game = JsonNode.Parse(Body(corpusVersion: "", evidence: ""))!;
        game["sources"]![0]!["url"] = "";
        var gameResult = BrainInquiryProtocol.Parse(game.ToJsonString(), version);
        check(gameResult.Wiki.Sources[0].GameVersion == version, "current-game recipe sources work independently of Wiki metadata");
    }
}
