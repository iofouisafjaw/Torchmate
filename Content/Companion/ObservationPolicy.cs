using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

// A player's request to inspect stopped work is not a new grant to execute it.
// This policy is shared by direct chat routing and the model loop's runtime gate.
public static class ObservationPolicy
{
    private static string Normalize(string text)=>text.Normalize(System.Text.NormalizationForm.FormKC).Trim();
    private static bool Deferred(string text)=>Regex.IsMatch(text,"以后|下次|记住|如果|假如|他说|她说|怎么停止|如何停止|怎么取消|如何取消");
    public static bool StopsWork(string text)=>!Deferred(text)&&Regex.IsMatch(Normalize(text),
        "(?:不要|别|不用|不许|停止)\\s*(?:再|继续|重新|自动)?\\s*(?:挖|采|砍|建|盖|铺|种|施工|开工|修补|执行|重试|恢复|做|攻击)");
    public static bool Matches(string text)=>StopsWork(text)||text.Length<=240&&!Deferred(text)&&(
        Regex.IsMatch(Normalize(text),"检查最新资源|查看最新资源|失败原因|(?:为什么|为啥|原因|状态|进度).*(?:停|失败|通道|任务|刚才)|(?:刚才|任务|通道).*(?:为什么|为啥|失败原因|状态|进度)"));
    public static bool Allows(AgentCall call)=>RecoveryPolicy.Allows(call)&&call.Tool!="replan";
    public static string Describe(CompanionTaskView? view)=>view==null?"目前没有任务记录。":
        $"任务：{view.Goal} · {view.PhaseText}\n{view.OutcomeText}"+
        (view.Result.Length>0&&view.Result!=view.OutcomeText?"\n"+view.Result:"");
    public const string Blocked="本次只检查状态和资源，没有执行授权；未开始采集、挖掘、施工或其他资源操作。";
    public static readonly JsonElement Schema=BuildSchema();
    private static JsonElement BuildSchema()
    {
        var schema=JsonNode.Parse(RecoveryPolicy.Schema(false).GetRawText())!;
        foreach(var branch in schema["oneOf"]!.AsArray())
            if(branch?["properties"]?["tool"]?["const"]?.GetValue<string>()=="reply")
                branch["properties"]!["text"]=JsonSerializer.SerializeToNode(new{type="string",minLength=1,maxLength=600});
        return JsonSerializer.SerializeToElement(schema);
    }
}
