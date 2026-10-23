using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TerrarAI.Content.Companion;

public sealed record WikiSource(int Id, string Title, string Url, string Excerpt, string GameVersion, string Modified);
public sealed record WikiContext(string Status, string GameVersion, string Snapshot, string Evidence,
    string License, string Attribution, WikiSource[] Sources);

public static class WikiKnowledge
{
    public static Task<WikiContext> SearchAsync(string path, string version, string question, CancellationToken token) =>
        Task.Run(() => Search(path, version, question, token), token);

    public static WikiContext Search(string path, string version, string question, CancellationToken token = default)
    {
        WikiContext Empty(string status) => new(status, version, "", "", "", "", Array.Empty<WikiSource>());
        token.ThrowIfCancellationRequested();
        version = NormalizeVersion(version);
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(\.\d+)?$")) return Empty("未能读取当前 Terraria 版本，未使用 Wiki。");
        if (!File.Exists(path)) return Empty("还未导入离线 Wiki；只能回答当前世界已读取的事实。");
        try
        {
            using var db = new WikiSqlite(path, token);
            var meta = db.Query("SELECT key,value FROM metadata", 2).ToDictionary(r => r[0], r => r[1]);
            if (meta.GetValueOrDefault("schema") != "1") return Empty("Wiki 索引格式不兼容，请重新导入。");
            if (meta.GetValueOrDefault("scope") != "vanilla" || meta.GetValueOrDefault("game_version") != version ||
                string.IsNullOrWhiteSpace(meta.GetValueOrDefault("version_evidence")))
                return Empty($"Wiki 版本 {(string.IsNullOrWhiteSpace(meta.GetValueOrDefault("game_version")) ? "未知" : meta["game_version"])} 与游戏 {version} 未确认匹配，已隔离。");
            var tokens = Terms(QueryText(question)).Distinct().Take(64).ToArray();
            var aliases = db.Query("SELECT alias,target FROM redirects WHERE instr(?1,alias)>0 AND length(alias)>=2 LIMIT 8", 2, question);
            tokens = tokens.Concat(aliases.SelectMany(r => Terms(r[1]))).Distinct().Take(96).ToArray();
            bool titleOnly = false;
            var found = new List<string[]>();
            if (tokens.Length > 0)
            {
                string match = string.Join(" OR ", tokens.Select(t => "\"" + t + "\""));
                try
                {
                    found = db.Query("SELECT c.title,c.url,c.text,c.game_version,c.modified FROM search JOIN chunks c ON c.id=search.rowid " +
                        "WHERE search MATCH ?1 AND c.game_version=?2 AND c.scope='vanilla' ORDER BY bm25(search,8.0,1.0) LIMIT 8", 5,
                        "title_terms : (" + match + ")", version);
                    found.AddRange(db.Query("SELECT c.title,c.url,c.text,c.game_version,c.modified FROM search JOIN chunks c ON c.id=search.rowid " +
                        "WHERE search MATCH ?1 AND c.game_version=?2 AND c.scope='vanilla' ORDER BY bm25(search,8.0,1.0) LIMIT 24", 5, match, version));
                }
                catch (InvalidOperationException)
                {
                    titleOnly = true;
                    // Some Windows system SQLite builds lack FTS5: bounded title-only search still works.
                    found = db.Query("SELECT title,url,text,game_version,modified FROM chunks WHERE game_version=?1 AND scope='vanilla' " +
                        "AND instr(?2,title)>0 ORDER BY length(title) DESC LIMIT 24", 5, version, question + string.Join(" ",aliases.Select(r => r[1])));
                }
            }
            // A title hit locates the article; inspect its bounded passages before choosing evidence.
            // Otherwise a shared crafting-station table can outrank the requested item's recipe.
            var titles = found.Select(r=>r[0]).Where(t=>t.Length>=2 &&
                (question.Contains(t,StringComparison.OrdinalIgnoreCase) || aliases.Any(a=>a[1]==t))).Distinct()
                .OrderByDescending(t=>t.Length).Take(3).ToArray();
            foreach (string title in titles)
                found.AddRange(db.Query("SELECT title,url,text,game_version,modified FROM chunks WHERE title=?1 " +
                    "AND game_version=?2 AND scope='vanilla' LIMIT 256",5,title,version));
            bool recipe = new[] { "合成", "配方", "制作", "材料", "怎么做", "craft", "recipe" }
                .Any(w=>question.Contains(w,StringComparison.OrdinalIgnoreCase));
            int Score(string[] row)
            {
                string body=row[2];
                var names=aliases.Select(a=>a[0]).Append(row[0]).Where(n=>question.Contains(n,StringComparison.OrdinalIgnoreCase)).Distinct();
                int score=tokens.Count(t=>body.Contains(t,StringComparison.OrdinalIgnoreCase));
                foreach(string name in names)
                {
                    if(body.Contains(name,StringComparison.OrdinalIgnoreCase)) score+=8;
                    if(recipe && body.StartsWith("配方："+name+" ×",StringComparison.OrdinalIgnoreCase)) score+=100;
                }
                return score;
            }
            int TitlePriority(string[] row)
            {
                var names=aliases.Where(a=>a[1]==row[0]).Select(a=>a[0]).Append(row[0]);
                var positions=names.Select(n=>new { Name=n, Pos=question.IndexOf(n,StringComparison.OrdinalIgnoreCase) }).Where(n=>n.Pos>=0);
                return positions.Select(n=>1000-n.Pos*10+n.Name.Length).DefaultIfEmpty(0).Max();
            }
            var sources = found.Where(r => ValidUrl(r[1])).GroupBy(r => r[0])
                .Select(g=>g.OrderByDescending(Score).First())
                .OrderByDescending(TitlePriority).ThenByDescending(Score).Take(2)
                .Select((r,i) => new WikiSource(i+1, r[0], r[1], Excerpt(r[2], tokens), r[3], r[4])).ToArray();
            return new((sources.Length > 0 ? "已检索本地 Wiki（版本匹配）" : "本地 Wiki 未找到相关词条，请换用物品／首领的具体名称。") + (titleOnly ? "（当前系统使用标题搜索）" : ""), version,
                meta.GetValueOrDefault("snapshot", "未知"), meta["version_evidence"], meta.GetValueOrDefault("license", ""),
                meta.GetValueOrDefault("attribution", "Terraria Wiki contributors"), sources);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Empty("本地 Wiki 暂不可用：" + ex.GetBaseException().Message); }
    }
    private static bool ValidUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "terraria.wiki.gg" && (uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/zh/wiki/", StringComparison.Ordinal));
    public static string NormalizeVersion(string version)
    {
        var match = Regex.Match(version.Trim(), @"^(?:Terraria )?v?(\d+\.\d+\.\d+(?:\.\d+)?)$");
        return match.Success ? match.Groups[1].Value : "";
    }
    private static readonly System.Collections.Generic.HashSet<string> StopWords = new(new[] {
        "怎么", "如何", "合成", "制作", "需要", "多少", "什么", "可以", "请问", "告诉", "我想", "一下", "哪个", "哪些", "这个", "那个", "当前", "现在", "是否", "有什", "么用", "起来", "进行", "我们", "the", "how", "to", "a", "is", "do", "make", "craft" });
    private static string QueryText(string question)
    {
        foreach(string phrase in new[] { "的配方是什么", "怎么合成", "如何合成", "怎么制作", "如何制作", "需要多少", "的配方", "的材料" })
            question=question.Replace(phrase," ",StringComparison.OrdinalIgnoreCase);
        return question;
    }
    internal static IEnumerable<string> Terms(string text)
    {
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_]+|[\u3400-\u9fff]+"))
        {
            if (m.Value[0] < '\u3400') { if (!StopWords.Contains(m.Value)) yield return m.Value; continue; }
            if (m.Length == 1) { yield return m.Value; continue; }
            for (int i=0; i<m.Length-1; i++) { string term = m.Value.Substring(i,2); if (!StopWords.Contains(term)) yield return term; }
        }
    }
    private static string Excerpt(string text, string[] tokens)
    {
        if (text.Length <= 220) return text;
        // Select a compact window with the most matching query terms, rather than the page introduction alone.
        int start = 0, best = -1;
        for (int i=0; i<text.Length; i+=100)
        {
            string window = text.Substring(i, Math.Min(220,text.Length-i));
            int score = tokens.Count(t => window.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (score > best) { start=i; best=score; }
        }
        return text.Substring(start,Math.Min(220,text.Length-start));
    }
}
