using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public enum ExpeditionMode { Underground,Tunnel,Scout }
public enum ExpeditionState { Running,Returning,Completed,Failed,Cancelled }
public enum ExpeditionStepState { Pending,Done,Failed }
public sealed record ExpeditionRequest(ExpeditionMode Mode,int Distance=32)
{
    public static bool TryParse(string input,out ExpeditionRequest? request,out string error)
    {
        request=null;error="";if(input.Length>240)return false;
        string text=input.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。','！','!','.');
        if(Regex.IsMatch(text,"[?？]|不要|别|不用|以后|记住|如果|怎么|如何|然后|并且|顺便|同时|[、和与]"))return false;
        text=Regex.Replace(text,@"^(?:(?:请|帮我|麻烦你)\s*)+","");
        if(text is "挖到地下层" or "往下挖到地下层" or "向下挖到地下层")request=new(ExpeditionMode.Underground);
        else if(text is "往下挖一条通道" or "向下挖一条通道" or "挖一条竖井" or "往下挖" or "向下挖")request=new(ExpeditionMode.Tunnel);
        else if(text is "去前面探探" or "去前面探探路" or "去前面探路" or "往前探路" or "向前探路")request=new(ExpeditionMode.Scout);
        else
        {
            var m=Regex.Match(text,@"^(?:(?<dig>往下挖|向下挖)|(?<scout>往前探路|向前探路))\s*(?<n>\d+)\s*格(?:的?通道)?$");
            if(!m.Success)return false;
            bool scout=m.Groups["scout"].Success;int max=scout?64:160;
            if(!int.TryParse(m.Groups["n"].Value,out int n)||n<1||n>max)
            {error=$"本次{(scout?"探路":"向下挖掘")}长度需为1–{max}格。";return true;}
            request=new(scout?ExpeditionMode.Scout:ExpeditionMode.Tunnel,n);
        }
        return request!=null;
    }
}

// Coordinates are tiles; StartY/EndY are the companion's feet, not its center.
public sealed record ExpeditionPlan(ExpeditionMode Mode,int StartX,int StartY,int EndY,int Direction,int Distance)
{
    public double CenterX=>StartX+0.5;
    public bool Digging=>Mode!=ExpeditionMode.Scout;
    public static ExpeditionPlan? Create(ExpeditionRequest request,int x,int feetY,double surface,int facing,int width,int height,out string error)
    {
        error="";
        if(x<14||x>=width-14||feetY<14||feetY>=height-14||!double.IsFinite(surface)||surface<0||surface>=height)
        {error="起点太靠近世界边界或地层信息尚未就绪。";return null;}
        if(request.Distance<1||request.Distance>(request.Mode==ExpeditionMode.Scout?64:160))
        {error="任务长度超出单次范围。";return null;}
        int end=request.Mode==ExpeditionMode.Underground?Math.Max(feetY,(int)Math.Ceiling(surface)+3):request.Mode==ExpeditionMode.Tunnel?feetY+request.Distance:feetY;
        int distance=request.Mode==ExpeditionMode.Scout?request.Distance:end-feetY;
        int direction=facing<0?-1:1;
        if(end>=height-14||distance>160||request.Mode==ExpeditionMode.Scout&&(x+direction*distance<14||x+direction*distance>=width-14))
        {error="目标超出单次深度或世界边界；请先靠近目标地层再安排任务。";return null;}
        return new(request.Mode,x,feetY,end,direction,distance);
    }
    public IEnumerable<(int X,int Y)> Row(int y)
    {yield return(StartX-1,y);yield return(StartX+1,y);yield return(StartX,y);}
    public bool ContainsDig(int x,int y)=>Digging&&Math.Abs(x-StartX)<=1&&y>=StartY&&y<EndY;
    public bool ContainsActor(double x,double feetY)=>double.IsFinite(x)&&double.IsFinite(feetY)&&
        (Digging?Math.Abs(x-CenterX)<=4&&feetY>=StartY-6&&feetY<=EndY+3:
            Math.Abs(x-CenterX)<=Distance+32&&Math.Abs(feetY-StartY)<=32);
    public string Describe=>Digging?$"三格宽竖井：x={StartX-1}…{StartX+1}，y={StartY}…{EndY-1}，最多{Distance}格深":
        $"按发令时玩家朝向向{(Direction>0?"右":"左")}探路{Distance}格，再尝试返回同伴起点";
}

public sealed record DigCell(int X,int Y,int Type=-1,bool Mineable=false,bool Protected=false,bool Hazard=false,int? Substrate=null)
{
    // Only native ordinary dirt/grass cells opt into a shared substrate. Never
    // accept newly occupied air, different ores, tool loss or protection changes.
    public bool SameSubstrate(DigCell actual)=>this==actual||Type>=0&&actual.Type>=0&&Mineable&&actual.Mineable&&
        Substrate!=null&&Substrate==actual.Substrate&&(this with{Type=actual.Type})==actual;
}
public sealed record DigBand(IReadOnlyList<DigCell> Cells,bool LandingSafe=true,string Reason="")
{
    public string Problem(ExpeditionPlan plan,int row)
    {
        if(!plan.ContainsDig(plan.StartX,row))return "挖掘目标超出竖井范围。";
        if(Cells.Count!=30)return "周围地形未完整核实。";
        // A full fresh 5x6 neighborhood protects adjacent furniture/walls/wires.
        var cells=Cells.GroupBy(p=>(p.X,p.Y)).ToDictionary(g=>g.Key,g=>g.ToArray());
        for(int x=plan.StartX-2;x<=plan.StartX+2;x++)for(int y=row-3;y<=row+2;y++)
            if(!cells.TryGetValue((x,y),out var values)||values.Length!=1)return "周围地形未完整核实。";
        if(!LandingSafe)return Reason.Length>0?Reason:"下方落点不安全，未继续挖掘。";
        if(Cells.Any(p=>p.Hazard))return Reason.Length>0?Reason:"附近有液体、陷阱或掉落风险，未继续挖掘。";
        if(Cells.Any(p=>p.Protected))return "附近有家具、可住房背景墙或电路，未继续挖掘。";
        if(plan.Row(row).Any(p=>cells[p].Single() is DigCell c&&c.Type>=0&&!c.Mineable))return "当前方块不在可安全挖掘范围内，或镐力不足。";
        return "";
    }
}
public sealed record ExpeditionStep(ExpeditionStepState State,string Message="",int Removed=0);
public sealed record ExpeditionFrame(long Tick,double AgentX,double FeetY,bool Valid=true,bool Alive=true,bool Paused=false,
    bool Permitted=true,string Danger="");
public sealed record ExpeditionObservation(MapLocation Location,double ObserverX,double ObserverY,bool Visible=true);
public interface IExpeditionSite
{
    DigBand Band(int row);
    DigCell Read(int x,int y);
    ExpeditionStep Mine(int x,int y,int expectedType);
    ExpeditionStep Move(double centerX,double feetY);
    void Hold();
    void Release();
    IEnumerable<ExpeditionObservation> Observe();
    bool Mark(ExpeditionObservation finding);
}

public sealed class ExpeditionSurvey
{
    public const int Radius=12,Limit=12;
    private readonly List<ExpeditionObservation> _findings=new();
    public IReadOnlyList<ExpeditionObservation> Findings=>_findings;
    public int Marked{get;private set;}
    public bool Add(ExpeditionObservation observation,Func<ExpeditionObservation,bool> mark)
    {
        var p=observation.Location;
        if(!observation.Visible||p.Kind is not (LocationKind.Ore or LocationKind.Chest or LocationKind.Danger)||p.X<0||p.Y<0||p.Width is <1 or >16||p.Height is <1 or >16||
            !double.IsFinite(observation.ObserverX)||!double.IsFinite(observation.ObserverY)||
            Math.Pow(p.CenterX-observation.ObserverX,2)+Math.Pow(p.CenterY-observation.ObserverY,2)>Radius*Radius)return false;
        if(_findings.Count>=Limit||_findings.Any(f=>f.Location.Identity==p.Identity||f.Location.Kind==p.Kind&&f.Location.Name==p.Name&&
            Math.Pow(f.Location.CenterX-p.CenterX,2)+Math.Pow(f.Location.CenterY-p.CenterY,2)<64))return false;
        _findings.Add(observation);if(mark(observation))Marked++;return true;
    }
    public string Report(double playerX,double playerY)=>_findings.Count==0?"沿途12格视距内未观察到矿、箱子或已识别危险；不代表区域已安全排查。":
        "沿途观察（最多12处，相邻同种发现合并）：\n"+string.Join("\n",_findings.Select(f=>$"{f.Location.Name}：{LocationQueries.Bearing(playerX,playerY,f.Location).Text}，tile({f.Location.X},{f.Location.Y})"))+
        "\n地图每类保留最后一处有效发现；以上为观察时记录，物件或敌怪可能已经变化。";
}

// Executes the actual production state machine against either the native site or
// deterministic test fixtures. A removed tile is not an inventory/material receipt.
public sealed class ExpeditionJob(ExpeditionPlan plan)
{
    public const int MaxDigTicks=36000,MaxScoutTicks=3600;
    public ExpeditionPlan Plan{get;}=plan;
    public ExpeditionState State{get;private set;}=ExpeditionState.Running;
    public ExpeditionSurvey Survey{get;}=new();
    public int Removed{get;private set;}
    public int ClearedRows{get;private set;}
    public string Reason{get;private set;}="准备出发。";
    private int _row=plan.StartY,_age,_idle,_scan;
    private long _lastTick=-1;
    private double _lastX=plan.CenterX,_lastY=plan.StartY;
    private int _lastRemoved;
    public bool Active=>State is ExpeditionState.Running or ExpeditionState.Returning;
    public void Cancel(IExpeditionSite? site,string reason)
    {if(!Active)return;State=ExpeditionState.Cancelled;Reason=reason;site?.Release();}
    private void End(IExpeditionSite site,bool success,string reason)
    {State=success?ExpeditionState.Completed:ExpeditionState.Failed;Reason=reason;site.Release();}
    public void Tick(ExpeditionFrame frame,IExpeditionSite site)
    {
        if(!Active)return;
        if(!frame.Valid||!frame.Alive){Cancel(site,"角色、同伴、世界或控制指令已变化，任务停止。");return;}
        if(!frame.Permitted){End(site,false,"挖掘许可或采集规则已变化，任务停止，已挖部分保留。");return;}
        if(frame.Paused||frame.Tick==_lastTick)return;
        _lastTick=frame.Tick;
        if(!Plan.ContainsActor(frame.AgentX,frame.FeetY)){End(site,false,"同伴离开本次任务边界，未继续行动。");return;}
        if(++_age>(Plan.Digging?MaxDigTicks:MaxScoutTicks)){End(site,false,"达到本次任务时间上限，已完成部分和发现保留。");return;}
        if(_scan++%30==0||frame.Danger.Length>0)
            foreach(var finding in site.Observe().Take(2048).OrderByDescending(f=>f.Location.Kind==LocationKind.Danger))Survey.Add(finding,site.Mark);
        if(frame.Danger.Length>0){End(site,false,"遇到危险，已停止前进："+frame.Danger);return;}
        if(Math.Abs(frame.AgentX-_lastX)+Math.Abs(frame.FeetY-_lastY)>=0.25||Removed!=_lastRemoved)
        {_idle=0;_lastX=frame.AgentX;_lastY=frame.FeetY;_lastRemoved=Removed;}
        else if(++_idle>600){End(site,false,"连续10秒没有位置或挖掘进展，未声称完成。");return;}
        if(Plan.Digging){Dig(frame,site);return;}
        double goalX=State==ExpeditionState.Returning?Plan.CenterX:Plan.CenterX+Plan.Direction*Plan.Distance;
        double goalY=Plan.StartY;
        var moved=site.Move(goalX,goalY);
        if(moved.State==ExpeditionStepState.Failed){End(site,false,(State==ExpeditionState.Returning?"返回起点失败：":"前方路线不可达：")+moved.Message);return;}
        bool arrived=Math.Abs(frame.AgentX-goalX)<=1.5&&Math.Abs(frame.FeetY-goalY)<=(State==ExpeditionState.Returning?3:24);
        if(moved.State==ExpeditionStepState.Done&&arrived)
        {
            if(State==ExpeditionState.Returning)End(site,true,"短程探路完成，已回到出发点附近；报告仅包含实际观察。");
            else{State=ExpeditionState.Returning;_idle=0;site.Release();Reason="已到前方目标，沿可走路线返回起点。";}
        }
        else Reason=State==ExpeditionState.Returning?"正在返回出发点。":"正在向前探路，不开挖通道。";
    }
    private void Dig(ExpeditionFrame frame,IExpeditionSite site)
    {
        if(_row>=Plan.EndY)
        {
            site.Hold();
            if(frame.FeetY>=Plan.EndY-0.25)End(site,true,Plan.Mode==ExpeditionMode.Underground?"通道已挖到地下层，并核对同伴实际深度。":"向下通道已完成，并核对同伴实际深度。");
            else Reason="通道已清空，等待同伴实际下降到目标深度。";
            return;
        }
        if(_row-frame.FeetY>1.25){site.Hold();Reason="等待下降到下一层，不远距离挖掘。";return;}
        var band=site.Band(_row);string problem=band.Problem(Plan,_row);
        if(problem.Length>0){End(site,false,problem);return;}
        var cells=band.Cells.ToDictionary(c=>(c.X,c.Y));
        foreach(var at in Plan.Row(_row))
        {
            var cell=cells[at];if(cell.Type<0)continue;
            var fresh=site.Read(at.X,at.Y);
            if(fresh!=cell){End(site,false,"挖掘前目标地形变化，未继续破坏。");return;}
            var result=site.Mine(at.X,at.Y,cell.Type);
            if(result.Removed==1&&site.Read(at.X,at.Y).Type<0)Removed++;
            else if(result.Removed!=0){End(site,false,"挖掘收据与地形不一致，未声称完成。");return;}
            if(result.State==ExpeditionStepState.Failed){End(site,false,result.Message);return;}
            if(result.State==ExpeditionStepState.Done&&site.Read(at.X,at.Y).Type>=0){End(site,false,"方块仍在，未把动作返回值当成挖掘成功。");return;}
            Reason=$"向下挖掘：已清空{ClearedRows}/{Plan.Distance}层，核实移除{Removed}块。";return;
        }
        _row++;ClearedRows++;site.Hold();Reason="这一层已清空，等待自然下落。";
    }
}
