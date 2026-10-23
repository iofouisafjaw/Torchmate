using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
namespace TerrarAI.Content.Companion;

public sealed record OreTunnelRequest(string? Ore)
{
    public static bool TryParse(string text,out OreTunnelRequest? request)
    {
        request=null;
        if(text.Length>240||AgentProtocol.LooksLikeQuestion(text)||Regex.IsMatch(text,"不要|不用|别|取消|停止|如果|假如|以后|下次|记住|能否|是否|能不能"))return false;
        if(!Regex.IsMatch(text,"矿|石头|石块")||!Regex.IsMatch(text,"通道|开路|挖过去|挖过来"))return false;
        request=new(TaskGoal.OreWord(text));return true;
    }
}
public readonly record struct TunnelPose(int X,int FeetY)
{
    public IEnumerable<(int X,int Y)> Body(){for(int x=X-1;x<=X+1;x++)for(int y=FeetY-4;y<FeetY;y++)yield return(x,y);}
}
public sealed record OreTunnelPlan(TunnelPose Start,DigCell Target,IReadOnlyList<TunnelPose> Steps,IReadOnlyList<DigCell> Snapshot,IReadOnlyList<DigCell> Excavation)
{
    public const int MaxLength=28,MaxDepth=12,MaxSteps=40,MaxTiles=128,MaxExpanded=512;
    public string Describe=>$"矿格 tile({Target.X},{Target.Y})；三格宽、四格高的阶梯通道，{Steps.Count}段，最多移除{Excavation.Count+1}块（含目标矿格）；\n路线："+
        string.Join(" → ",new[]{Start}.Concat(Steps).Select(p=>$"({p.X},{p.FeetY})"));
    public bool Contains(double x,double y)=>double.IsFinite(x)&&double.IsFinite(y)&&
        x>=Steps.Append(Start).Min(p=>p.X)-4&&x<=Steps.Append(Start).Max(p=>p.X)+5&&
        y>=Steps.Append(Start).Min(p=>p.FeetY)-4&&y<=Steps.Append(Start).Max(p=>p.FeetY)+2;
    public static OreTunnelPlan? Create(TunnelPose start,DigCell target,Func<int,int,DigCell> read,out string error)
    {
        error="有限范围内没有避开危险且有支撑的阶梯通道；未开始挖掘。";
        if(target.Type<0||!target.Mineable||target.Protected||target.Hazard||Math.Abs(target.X-start.X)>MaxLength||
            target.Y<start.FeetY-4||target.Y>start.FeetY+MaxDepth)return null;
        var cache=new Dictionary<(int,int),DigCell>();
        DigCell Read(int x,int y){if(!cache.TryGetValue((x,y),out var c))cache[(x,y)]=c=read(x,y);return c;}
        IEnumerable<DigCell> Halo(TunnelPose p)=>Enumerable.Range(p.X-2,5).SelectMany(x=>Enumerable.Range(p.FeetY-5,7).Select(y=>Read(x,y)));
        bool Safe(TunnelPose p)=>Math.Abs(p.X-start.X)<=MaxLength&&p.FeetY>=start.FeetY-2&&p.FeetY<=start.FeetY+MaxDepth&&
            !p.Body().Contains((target.X,target.Y))&&Halo(p).All(c=>!c.Protected&&!c.Hazard)&&
            p.Body().Select(c=>Read(c.X,c.Y)).All(c=>c.Type<0||c.Mineable)&&
            Enumerable.Range(p.X-1,3).All(x=>Read(x,p.FeetY) is {Type:>=0,Mineable:true,Protected:false,Hazard:false});
        bool Goal(TunnelPose p)=>Math.Pow(p.X+.5-(target.X+.5),2)+Math.Pow(p.FeetY-1.25-(target.Y+.5),2)<=25&&
            new[]{(target.X-1,target.Y),(target.X+1,target.Y),(target.X,target.Y-1),(target.X,target.Y+1)}
                .Any(c=>Read(c.Item1,c.Item2).Type<0||p.Body().Contains(c));
        // The ore itself gets the same fresh neighborhood protection as the tunnel.
        for(int x=target.X-1;x<=target.X+1;x++)for(int y=target.Y-1;y<=target.Y+1;y++)
            if(Read(x,y) is {Protected:true} or {Hazard:true})return null;
        if(!Safe(start)||start.Body().Any(c=>Read(c.X,c.Y).Type>=0))return null;
        var queue=new PriorityQueue<TunnelPose,int>();var cost=new Dictionary<TunnelPose,int>{{start,0}};
        var parents=new Dictionary<TunnelPose,TunnelPose>();var closed=new HashSet<TunnelPose>();queue.Enqueue(start,0);
        while(queue.TryDequeue(out var at,out _)&&closed.Count<MaxExpanded)
        {
            if(!closed.Add(at))continue;
            if(Goal(at))
            {
                var steps=new List<TunnelPose>();var pos=at;
                while(parents.TryGetValue(pos,out var previous)){steps.Add(pos);pos=previous;}
                steps.Reverse();if(steps.Count>MaxSteps)continue;
                var cells=steps.SelectMany(p=>p.Body()).Distinct().Select(c=>Read(c.X,c.Y)).Where(c=>c.Type>=0).ToArray();
                if(cells.Length+1>MaxTiles)continue;
                var footprint=steps.Append(start).SelectMany(p=>Halo(p)).Concat(Enumerable.Range(target.X-1,3)
                    .SelectMany(x=>Enumerable.Range(target.Y-1,3).Select(y=>Read(x,y))))
                    .DistinctBy(c=>(c.X,c.Y)).ToArray();
                error="";return new(start,target,steps.ToArray(),footprint,cells);
            }
            foreach(int direction in new[]{-1,1})foreach(int dy in new[]{0,1,-1})
            {
                // Leave a full three-tile tread for the 40px native body.
                int dx=direction*(dy==0?1:3);
                var next=new TunnelPose(at.X+dx,at.FeetY+dy);if(closed.Contains(next)||!Safe(next))continue;
                int score=cost[at]+3+next.Body().Except(at.Body()).Count(c=>Read(c.X,c.Y).Type>=0)*4;
                if(cost.TryGetValue(next,out int old)&&old<=score)continue;
                cost[next]=score;parents[next]=at;queue.Enqueue(next,score+Math.Abs(next.X-target.X)+Math.Abs(next.FeetY-target.Y));
            }
        }
        return null;
    }
}
public interface IOreTunnelSite
{
    DigCell Read(int x,int y);
    ExpeditionStep Mine(int x,int y,int type);
    ExpeditionStep Move(TunnelPose pose);
    void Hold();
    void Release();
}
public enum OreTunnelState { Preview,Running,Completed,Failed,Cancelled }
public sealed class OreTunnelJob(OreTunnelPlan plan)
{
    public OreTunnelPlan Plan{get;}=plan;
    public OreTunnelState State{get;private set;}=OreTunnelState.Preview;
    public bool Active=>State is OreTunnelState.Preview or OreTunnelState.Running;
    public int Removed=>_removed.Count;
    public int VerifiedOre{get;private set;}
    public string Reason{get;private set;}="预览中，等待确认；尚未施工。";
    private readonly HashSet<(int,int)> _removed=new();
    private int _step,_age,_idle,_previousRemoved;
    private long _lastTick=-1;
    private double _lastX=plan.Start.X+.5,_lastY=plan.Start.FeetY;
    public string Check(IOreTunnelSite site)
    {
        foreach(var c in Plan.Snapshot)
        {
            var actual=site.Read(c.X,c.Y);
            if(_removed.Contains((c.X,c.Y)))
            {if(actual.Type>=0||actual.Protected||actual.Hazard)return "已挖位置变化或出现危险，需重新规划。";}
            else if(actual!=c&&!(State==OreTunnelState.Running&&c.SameSubstrate(actual)))
                return $"工具、目标或通道周围地形已变化，需重新预览确认：tile({c.X},{c.Y})，原类型{c.Type}→{actual.Type}，可挖{c.Mineable}→{actual.Mineable}，保护{c.Protected}→{actual.Protected}，危险{c.Hazard}→{actual.Hazard}。";
        }
        return "";
    }
    public bool Approve(IOreTunnelSite site)
    {if(State!=OreTunnelState.Preview||Check(site).Length>0)return false;State=OreTunnelState.Running;Reason="已确认，仅在预览范围内施工。";return true;}
    public void Cancel(IOreTunnelSite? site,string reason)
    {if(!Active)return;State=OreTunnelState.Cancelled;Reason=reason;site?.Release();}
    private void End(IOreTunnelSite site,string reason,bool done=false)
    {State=done?OreTunnelState.Completed:OreTunnelState.Failed;Reason=reason;site.Release();}
    public void Tick(ExpeditionFrame frame,IOreTunnelSite site)
    {
        if(!Active)return;
        if(!frame.Valid||!frame.Alive){Cancel(site,"角色、世界或指令变化，已停止。");return;}
        if(State==OreTunnelState.Preview||frame.Paused){site.Hold();return;}
        if(!frame.Permitted||frame.Danger.Length>0){End(site,"权限或周围安全条件变化，已停止："+frame.Danger);return;}
        if(frame.Tick==_lastTick)return;_lastTick=frame.Tick;
        if(!Plan.Contains(frame.AgentX,frame.FeetY)||++_age>30000){End(site,"已达到本次范围或时间上限，已完成部分保留。");return;}
        string error=Check(site);if(error.Length>0){End(site,error);return;}
        if(Math.Abs(frame.AgentX-_lastX)+Math.Abs(frame.FeetY-_lastY)>.2||Removed!=_previousRemoved)
        {_idle=0;_lastX=frame.AgentX;_lastY=frame.FeetY;_previousRemoved=Removed;}
        else if(++_idle>600){End(site,"连续10秒没有进展，已停止，已完成部分保留。");return;}
        var pose=_step<Plan.Steps.Count?Plan.Steps[_step]:Plan.Steps.LastOrDefault(Plan.Start);
        if(_step<Plan.Steps.Count&&!Enumerable.Range(pose.X-1,3).Any(x=>site.Read(x,pose.FeetY).Type>=0))
        {End(site,"下一落脚点已无支撑，未继续挖掘。");return;}
        var work=pose.Body().Where(p=>Plan.Excavation.Any(c=>c.X==p.X&&c.Y==p.Y)&&!_removed.Contains(p))
            .OrderBy(p=>p.Y).ThenByDescending(p=>Math.Abs(p.X-frame.AgentX)).Select(p=>Plan.Excavation.First(c=>c.X==p.X&&c.Y==p.Y)).FirstOrDefault();
        if(work!=null){Dig(work,false,site);return;}
        if(_step<Plan.Steps.Count)
        {
            var moved=site.Move(pose);
            if(moved.State==ExpeditionStepState.Failed){End(site,"已清通部分保留；通道移动失败："+moved.Message);return;}
            if(moved.State==ExpeditionStepState.Done&&Math.Abs(frame.AgentX-(pose.X+.5))<.3&&Math.Abs(frame.FeetY-pose.FeetY)<.25)
            {_step++;site.Release();}
            Reason=$"通道 {_step}/{Plan.Steps.Count} 段，已核实移除 {Removed} 块。";return;
        }
        Dig(Plan.Target,true,site);
    }
    private void Dig(DigCell cell,bool ore,IOreTunnelSite site)
    {
        var live=site.Read(cell.X,cell.Y);
        if(!cell.SameSubstrate(live)){End(site,"目标方块已变化，未继续挖掘。");return;}
        var result=site.Mine(cell.X,cell.Y,live.Type);
        if(result.Removed==1&&site.Read(cell.X,cell.Y).Type<0)_removed.Add((cell.X,cell.Y));
        else if(result.Removed!=0){End(site,"移除收据与地形不一致，未声称完成。");return;}
        if(result.State==ExpeditionStepState.Failed){End(site,result.Message);return;}
        if(result.State==ExpeditionStepState.Done)
        {
            if(!_removed.Contains((cell.X,cell.Y))){End(site,"没有本任务的真实移除记录，未声称完成。");return;}
            if(ore){VerifiedOre=1;End(site,"通道与目标矿格移除已核实；掉落入包需另行核对。",true);}
        }
    }
}
