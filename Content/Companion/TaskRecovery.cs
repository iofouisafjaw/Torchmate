using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerrarAI.Content.Companion;

public sealed record RecoveryStock(int Type, string Name, int Backpack, int Chests);
public sealed record TaskFailureReceipt(string TaskId, string Kind, string Code, string Reason, string Completed,
    IReadOnlyList<RecoveryStock> Stock, string Surroundings, bool CanReplan)
{
    public string Describe()
    {
        string resources=Stock.Count==0?"背包／可用附近箱子中没有可报告的物品":string.Join("、",Stock.Take(32)
            .Select(s=>$"{s.Name}（背包{s.Backpack}／附近箱子{s.Chests}）"))+(Stock.Count>32?$"；另有{Stock.Count-32}种，可用inventory/chests补查":"");
        return $"长任务失败（{Kind}，任务{TaskId}）：{Code}。{Reason}\n"+
            "已完成部分（保留，不回滚）："+Completed+"\n当前资源："+resources+"\n当前环境："+Surroundings+
            "\n原任务未通过最终验收，不得宣称完成。库存是此刻观察，不能把玩家、治疗或其他来源的变化归因给同伴。"+
            (CanReplan?"\n可用replan按原目标／原位置生成新预览；旧确认不继承，必须重新确认。":
                "\n此类重试需玩家重新明确下令，不能自动扩大采集、挖掘、移动或战斗范围。可补查、询问或停止。");
    }
}

// One lineage survives an intentional replan. Task identity, visit, control revision,
// request budgets and player identity are all checked again before requesting/executing a model call.
public sealed class TaskRecoverySession
{
    public const int MaxFailures=2, MaxModelRequests=6, MaxReplans=2;
    public Guid RootId {get;}=Guid.NewGuid();
    public Guid Session {get;}
    public int Player {get;}
    public int Companion {get;}
    public string Goal {get;}
    public string TaskId {get;private set;}="";
    public ControlStamp Stamp {get;private set;}
    public bool Cancelled {get;private set;}
    public bool Recovering {get;private set;}
    public int Failures {get;private set;}
    public int Requests {get;private set;}
    public int Replans {get;private set;}
    public DateTimeOffset Expires {get;private set;}
    public TaskFailureReceipt? Latest {get;private set;}
    private readonly HashSet<string> _closed=new();
    public TaskRecoverySession(Guid session,int player,int companion,string goal)
    {
        if(session==Guid.Empty||player<0||companion<0||string.IsNullOrWhiteSpace(goal))throw new ArgumentException("Invalid recovery owner/goal.");
        Session=session;Player=player;Companion=companion;Goal=goal;
    }
    public bool Bind(string taskId,ControlStamp stamp)
    {
        if(Cancelled||string.IsNullOrWhiteSpace(taskId)||taskId.Length>64||stamp.Session!=Session||_closed.Contains(taskId))return false;
        TaskId=taskId;Stamp=stamp;Recovering=false;return true;
    }
    public bool Matches(Guid session,long revision,int player,int companion,bool alive)
        =>!Cancelled&&alive&&player==Player&&companion==Companion&&Stamp.Matches(session,revision);
    public bool Fail(TaskFailureReceipt receipt,Guid session,long revision,int player,int companion,bool alive,DateTimeOffset now)
    {
        if(!Matches(session,revision,player,companion,alive)||TaskId!=receipt.TaskId||!_closed.Add(receipt.TaskId))return false;
        Latest=receipt;Failures++;
        if(Failures>MaxFailures){Cancel();return false;}
        Recovering=true;Expires=now.AddMinutes(5);return true;
    }
    public bool Allows(Guid session,long revision,int player,int companion,bool alive,DateTimeOffset now)
        =>Recovering&&now<Expires&&Matches(session,revision,player,companion,alive);
    public bool Request(Guid session,long revision,int player,int companion,bool alive,DateTimeOffset now)
    {
        if(!Allows(session,revision,player,companion,alive,now)||Requests>=MaxModelRequests)return false;
        Requests++;return true;
    }
    public bool Replan(Guid session,long revision,int player,int companion,bool alive,DateTimeOffset now)
    {
        if(!Allows(session,revision,player,companion,alive,now)||Latest?.CanReplan!=true||Replans>=MaxReplans)return false;
        Replans++;return true;
    }
    public void Cancel(){Cancelled=true;Recovering=false;}
}

public static class RecoveryPolicy
{
    public const string StopText="原任务尚未通过最终验收，已停止恢复。";
    private static readonly JsonElement RetrySchema=BuildSchema(true,false),ObserveSchema=BuildSchema(false,false),
        RetryDecisionSchema=BuildSchema(true,true),ObserveDecisionSchema=BuildSchema(false,true);
    public static JsonElement Schema(bool canReplan,bool decisionOnly=false)=>decisionOnly?
        canReplan?RetryDecisionSchema:ObserveDecisionSchema:canReplan?RetrySchema:ObserveSchema;
    private static JsonElement BuildSchema(bool canReplan,bool decisionOnly)
    {
        var ordinary=JsonNode.Parse(AgentProtocol.Schema.GetRawText())!;
        var properties=ordinary["properties"]!.AsObject();
        foreach(string name in properties.Select(p=>p.Key).Where(k=>k is not ("tool" or "item" or "text")).ToArray())properties.Remove(name);
        properties["tool"]!["enum"]=JsonSerializer.SerializeToNode(new[]{"inventory","look","recipe","house_materials","storage","chests","npcs","shop","locate","answer"});
        var branches=new JsonArray();if(!decisionOnly)branches.Add(ordinary);
        branches.Add(JsonNode.Parse("""{"type":"object","properties":{"tool":{"const":"ask"},"text":{"type":"string","minLength":1,"maxLength":300}},"required":["tool","text"],"additionalProperties":false}"""));
        branches.Add(JsonSerializer.SerializeToNode(new{type="object",properties=new{tool=new{ @const="reply"},text=new{ @const=StopText}},required=new[]{"tool","text"},additionalProperties=false}));
        branches.Add(JsonNode.Parse("""{"type":"object","properties":{"tool":{"const":"control"},"mode":{"const":"stop"}},"required":["tool","mode"],"additionalProperties":false}"""));
        if(canReplan)branches.Add(JsonNode.Parse("""{"type":"object","properties":{"tool":{"const":"replan"}},"required":["tool"],"additionalProperties":false}"""));
        return JsonSerializer.SerializeToElement(new JsonObject{["oneOf"]=branches});
    }
    public const string Prompt="""
        【长任务失败后的恢复】
        原任务没有完成。以程序返回的失败原因、已确认完成部分和当前库存为准，不把旧预览、采集移除数或历史库存当作已经入包。
        现在只允许：inventory/look/recipe/house_materials/storage/chests/npcs/shop/locate 补查；ask 询问；reply 如实说明并停止；control(stop)停止；answer交资料库。
        需要重试原任务时用replan，不填参数。它只按原目标刷新预览，旧许可作废，等玩家重新确认。建筑保留原位置、种植仅处理原固定目标；合成制作站和备料缺口按新预览核对。不会增加目标数量、跨到其他任务或自动执行。
        不调用craft/place/chop/mine/buy/chest_put/chest_take/作弊工具，也不直接调用build/platform/craft_chain/garden/prepare_eye/expedition/combat来绕过重规划。
        失败收据标明不可重规划时，请说明需要玩家重新下令；询问不能代替新预览的确认。查到足够信息就停止或replan，不重复相同查询。
        查配方或“现在能做”不代表已经制作。reply只表示停止恢复，完成状态由程序按收据显示，不自行声称任务已完成。
        """;
    public static bool AllowsDecision(AgentCall call,bool canReplan)=>call.Tool is "ask" or "reply"||
        call.Tool=="control"&&call.Mode=="stop"||call.Tool=="replan"&&canReplan;
    public static bool Allows(AgentCall call)=>call.Tool is "inventory" or "look" or "recipe" or "house_materials" or "storage" or
        "chests" or "npcs" or "shop" or "locate" or "reply" or "ask" or "answer" or "replan" || call.Tool=="control"&&call.Mode=="stop";
    public static bool IsStop(string text)=>Regex.IsMatch(text.Trim().TrimEnd('。','！','!'),
        "^(?:算了|不用了|不做了|别继续了|停止恢复|取消恢复|不恢复了)$");
    public const string Blocked="恢复阶段这项操作未执行：只可补查、询问、停止，或用无参数replan刷新原任务预览；新预览必须重新确认。";
}
