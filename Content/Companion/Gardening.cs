using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public enum GardenCrop { Acorn, Daybloom, Moonglow, Blinkroot, Deathweed, Waterleaf, Fireblossom, Shiverthorn }
public enum GardenOperation { Plant, Harvest }
public enum GardenGrowth { Empty, Immature, Mature, Blooming, Sapling, Blocked }
public enum GardenBed { None, Grass, JungleGrass, DirtMud, Evil, Sand, Ash, SnowIce, Pot, Planter }
public sealed record GardenRequest(GardenOperation Operation, GardenCrop? Crop, int Count, bool Exact = true)
{
    public const int MaxCount = 64, Radius = 28;
    public static string Name(GardenCrop crop) => crop switch {
        GardenCrop.Acorn=>"橡实",GardenCrop.Daybloom=>"太阳花",GardenCrop.Moonglow=>"月光草",GardenCrop.Blinkroot=>"闪耀根",
        GardenCrop.Deathweed=>"死亡草",GardenCrop.Waterleaf=>"幌菊",GardenCrop.Fireblossom=>"火焰花",_=>"寒颤棘" };
    public static GardenCrop? CropName(string value) => Enum.GetValues<GardenCrop>().Cast<GardenCrop?>()
        .FirstOrDefault(c=>Name(c!.Value)==value.Replace("种子","")||string.Equals(c.ToString(),value,StringComparison.OrdinalIgnoreCase));
    public static bool TryParse(string input,out GardenRequest? request,out string error)
    {
        request=null;error="";
        if(input.Length>240)return false;
        string s=input.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。','！','!');
        if(!Regex.IsMatch(s,"橡实|草药|太阳花|月光草|闪耀根|死亡草|幌菊|火焰花|寒颤棘"))return false;
        if(AgentProtocol.LooksLikeQuestion(s)||Regex.IsMatch(s,"以后|下次|不要|不用|取消|停止|别|如果|不想|能否|是否"))return false;
        var m=Regex.Match(s,@"^(?:(?:请|帮我|麻烦你|麻烦|给我)\s*)?(种植|种下|播种|种|采收|收获|采集|收割|收|采)\s*(?:附近的?|周围的?)?(.+)$");
        if(!m.Success)return false;
        GardenOperation op=m.Groups[1].Value.StartsWith("种")||m.Groups[1].Value=="播种"?GardenOperation.Plant:GardenOperation.Harvest;
        string value=m.Groups[2].Value.Replace(" ","").Replace("已开花的","").Replace("已开花","").Replace("开花的","");
        var names=Enum.GetValues<GardenCrop>().Where(c=>value.Contains(Name(c))).ToArray();
        if(names.Length>1||Regex.IsMatch(value,"然后|再|并且|同时|和|全部|所有|一直|自动"))
        {error="请一次指定一种作物或草药，例如：种5株太阳花、采收10株草药。";return true;}
        GardenCrop? crop=names.Length==1?names[0]:null;
        if(crop==GardenCrop.Acorn&&op==GardenOperation.Harvest){error="橡实用于种树；收木材请说砍树。";return true;}
        string rest=value.Replace(crop is {} c?Name(c):"草药","").Replace("种子","").Replace("的","");
        var number=Regex.Match(rest,@"^([0-9零〇一二两三四五六七八九十百千万负.\-]+)(?:株|棵|颗|粒|个)?$");
        bool exact=rest.Length>0;
        int count=op==GardenOperation.Harvest?20:crop==GardenCrop.Acorn?3:5;
        if(exact&&(!number.Success||!PlatformRequest.Read(number.Groups[1].Value,out count)||count>MaxCount))
        {error="种植和采收一次支持1–64株，请给整数数量。";return true;}
        request=new(op,crop,count,op==GardenOperation.Plant||exact);return true;
    }
}

public sealed record GardenCell(BuildPoint At,GardenCrop Crop,GardenGrowth Growth,GardenBed Bed,bool Safe,string Stamp);
public static class GardenRules
{
    public static bool Plantable(GardenCell c) => c.Safe&&c.Growth==GardenGrowth.Empty&&(c.Crop==GardenCrop.Acorn?
        c.Bed==GardenBed.Grass:c.Bed is GardenBed.Pot or GardenBed.Planter||c.Bed==(c.Crop switch {
            GardenCrop.Daybloom=>GardenBed.Grass,GardenCrop.Moonglow=>GardenBed.JungleGrass,GardenCrop.Blinkroot=>GardenBed.DirtMud,
            GardenCrop.Deathweed=>GardenBed.Evil,GardenCrop.Waterleaf=>GardenBed.Sand,GardenCrop.Fireblossom=>GardenBed.Ash,_=>GardenBed.SnowIce }));
    public static bool Harvestable(GardenCell c) => c.Safe&&c.Crop!=GardenCrop.Acorn&&c.Growth==GardenGrowth.Blooming;
    public static bool InRange(BuildPoint at,BuildPoint origin) => Math.Abs((long)at.X-origin.X)<=GardenRequest.Radius&&Math.Abs((long)at.Y-origin.Y)<=GardenRequest.Radius;
}
public sealed class GardenPlan
{
    public GardenRequest Request { get; }
    public BuildPoint Origin { get; }
    public IReadOnlyList<GardenCell> Targets { get; }
    public string Blocked { get; }
    public bool CanExecute => Blocked.Length==0&&Targets.Count>0;
    public string Signature => JsonSerializer.Serialize(new { Request,Origin,Targets,Blocked });
    private GardenPlan(GardenRequest request,BuildPoint origin,IReadOnlyList<GardenCell> targets,string blocked)
    {Request=request;Origin=origin;Targets=targets;Blocked=blocked;}
    public GardenPlan Remaining(int cursor,IReadOnlyDictionary<GardenCrop,int> seeds,IEnumerable<GardenCell> live)
    {
        if(cursor<0||cursor>=Targets.Count)throw new ArgumentException("没有可重新规划的未完成植株，请检查最终现场后重新下令。");
        var allowed=Targets.Skip(cursor).Select(c=>(c.At,c.Crop)).ToHashSet();
        return BuildFixed(Request with{Count=Targets.Count-cursor,Exact=true},Origin,seeds,live,allowed);
    }
    public static GardenPlan BuildFixed(GardenRequest request,BuildPoint origin,IReadOnlyDictionary<GardenCrop,int> seeds,
        IEnumerable<GardenCell> live,IReadOnlySet<(BuildPoint At,GardenCrop Crop)> allowed)=>
        Build(request,origin,seeds,live.Where(c=>allowed.Contains((c.At,c.Crop))));
    public static GardenPlan Build(GardenRequest request,BuildPoint origin,IReadOnlyDictionary<GardenCrop,int> seeds,IEnumerable<GardenCell> cells)
    {
        if(request.Count is <1 or >GardenRequest.MaxCount||request.Operation==GardenOperation.Harvest&&request.Crop==GardenCrop.Acorn)
            throw new ArgumentException("作物或数量超出支持范围。");
        var remaining=new Dictionary<GardenCrop,int>(seeds);var selected=new List<GardenCell>();
        var candidates=cells.Where(c=>GardenRules.InRange(c.At,origin)&&(request.Crop==null?c.Crop!=GardenCrop.Acorn:c.Crop==request.Crop))
            .OrderBy(c=>Math.Abs((long)c.At.X-origin.X)+Math.Abs((long)c.At.Y-origin.Y)).ThenBy(c=>c.At.X).ThenBy(c=>c.At.Y).ThenBy(c=>c.Crop);
        foreach(var c in candidates)
        {
            if(selected.Count==request.Count)break;
            if(selected.Any(s=>s.At==c.At||s.Crop==GardenCrop.Acorn&&c.Crop==GardenCrop.Acorn&&Math.Abs(s.At.X-c.At.X)<4&&Math.Abs(s.At.Y-c.At.Y)<4))continue;
            if(request.Operation==GardenOperation.Harvest)
            {if(GardenRules.Harvestable(c))selected.Add(c);continue;}
            if(!GardenRules.Plantable(c)||remaining.GetValueOrDefault(c.Crop)<1)continue;
            selected.Add(c);remaining[c.Crop]--;
        }
        string blocked=selected.Count==0?"28格范围内没有符合条件的目标；种植需种子与合适土壤／空花盆／种植箱，采收只选开花草药。":
            request.Exact&&selected.Count<request.Count?$"要求{request.Count}株，目前只找到{selected.Count}个有种子／适合地面或已开花的目标；请补齐后重新规划。":"";
        return new(request,origin,selected.ToArray(),blocked);
    }
}
public enum GardenState { Preview,Running,Completed,Failed,Cancelled }
public enum GardenStep { Pending,Asking,Done,Failed }
public sealed record GardenReceipt(int Planted,int Herbs,int Seeds);
public interface IGardenSite
{
    bool Valid(out string reason);
    GardenStep Work(GardenCell target,GardenOperation operation,out GardenReceipt receipt,out string message);
    bool Planted(GardenCell target);
    bool Removed(GardenCell target);
    void Stop();
}
public sealed class GardenJob(GardenPlan plan,DateTimeOffset? now=null)
{
    public GardenPlan Plan { get; }=plan;
    public Guid PreviewId { get; }=Guid.NewGuid();
    public DateTimeOffset Expires { get; }=(now??DateTimeOffset.UtcNow).AddMinutes(2);
    public GardenState State { get; private set; }=GardenState.Preview;
    public int Cursor { get; private set; }
    public int Planted { get; private set; }
    public int Herbs { get; private set; }
    public int Seeds { get; private set; }
    public bool Asking { get; private set; }
    public string Reason { get; private set; }=plan.Blocked.Length>0?plan.Blocked:"等待确认目标与种子。";
    private int _ticks;
    public bool Confirm(Guid id,string signature,DateTimeOffset time)
    {if(State!=GardenState.Preview||id!=PreviewId||time>=Expires||signature!=Plan.Signature||!Plan.CanExecute)return false;State=GardenState.Running;return true;}
    public void Tick(IGardenSite site,bool paused=false)
    {
        if(paused||State!=GardenState.Running)return;
        if(++_ticks>36000){Fail(site,"种植／采收超时，已完成部分保留。");return;}
        if(!site.Valid(out string invalid)){Fail(site,invalid);return;}
        var target=Plan.Targets[Cursor];
        var step=site.Work(target,Plan.Request.Operation,out var receipt,out string message);Reason=message;Asking=step==GardenStep.Asking;
        if(step==GardenStep.Failed){Fail(site,message);return;}
        if(step!=GardenStep.Done)return;
        if(Plan.Request.Operation==GardenOperation.Plant?(receipt.Planted!=1||!site.Planted(target)):(receipt.Herbs<1||!site.Removed(target)))
        {Fail(site,"实际植株／背包核验未通过，已完成部分保留。");return;}
        Planted+=receipt.Planted;Herbs+=receipt.Herbs;Seeds+=receipt.Seeds;Cursor++;Asking=false;
        if(Cursor<Plan.Targets.Count)return;
        bool verified=Plan.Request.Operation==GardenOperation.Harvest||Plan.Targets.All(site.Planted);
        if(!verified){Fail(site,"最终检查发现已种植株发生变化，未标记完成。");return;}
        site.Stop();State=GardenState.Completed;Reason=$"完成{Cursor}株；已种{Planted}株，实际收到草药{Herbs}、种子{Seeds}。幼苗／未开花草药保留，生长由游戏自然进行。";
    }
    public void Cancel(IGardenSite? site,string reason)
    {if(State is not (GardenState.Preview or GardenState.Running))return;site?.Stop();State=GardenState.Cancelled;Asking=false;Reason=reason;}
    private void Fail(IGardenSite site,string reason){site.Stop();State=GardenState.Failed;Asking=false;Reason=reason;}
}
