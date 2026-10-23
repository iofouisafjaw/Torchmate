using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public enum ChainState { Preview,Running,Completed,Failed,Cancelled }
public enum ChainGatherResult { Pending,WaitingForApproval,Finished,Failed }
public interface ICraftChainSite
{
    bool Valid(out string reason);
    int Stock(int item);
    ChainGatherResult Collect(int item,out string message);
    bool Craft(ChainStep step,out string message);
    bool Place(ChainStep step,out string message);
    bool Deliver(IReadOnlyList<ChainTarget> targets,out string message);
    bool Audit(IReadOnlyList<ChainTarget> targets,out string message);
    void StopGather();
}

// Program execution, shared by the game adapter and offline scenario tests.
public sealed class CraftChainJob
{
    public CraftChainPlan Plan{get;}
    public Guid PreviewId{get;}=Guid.NewGuid();
    public DateTimeOffset Expires{get;}
    public ChainState State{get;private set;}=ChainState.Preview;
    public int Cursor{get;private set;}
    public string Reason{get;private set;}="等待确认材料和制作站计划。";
    public bool Asking{get;private set;}
    private readonly Dictionary<int,int> _budget;
    private readonly int _maxTicks;
    private int _ticks;
    public CraftChainJob(CraftChainPlan plan,DateTimeOffset? now=null,int maxTicks=180000)
    {Plan=plan;_budget=new(plan.Gather);Expires=(now??DateTimeOffset.UtcNow).AddMinutes(2);_maxTicks=maxTicks;}

    public bool Confirm(Guid id,string liveSignature,DateTimeOffset now)
    {
        if(State!=ChainState.Preview||id!=PreviewId||now>=Expires||!Plan.CanExecute||liveSignature!=Plan.Signature)return false;
        State=ChainState.Running;Reason="已确认，按计划逐步执行。";return true;
    }
    public void Step(ICraftChainSite site)
    {
        if(State!=ChainState.Running)return;
        if(++_ticks>_maxTicks){Fail(site,"执行超时，已完成部分保留，请重新规划。");return;}
        if(!site.Valid(out string invalid)){Fail(site,invalid);return;}
        if(Cursor>=Plan.Steps.Count)
        {
            site.StopGather();
            if(!site.Deliver(Plan.Targets,out string delivered)||!site.Audit(Plan.Targets,out delivered)){Fail(site,delivered);return;}
            State=ChainState.Completed;Asking=false;Reason=delivered;return;
        }
        var step=Plan.Steps[Cursor];
        if(step.Kind==ChainStepKind.Gather)
        {
            int before=site.Stock(step.Item),missing=Math.Max(0,Plan.Required(step.Item,Cursor)-before);
            if(missing==0){site.StopGather();Asking=false;Cursor++;return;}
            if(missing>_budget.GetValueOrDefault(step.Item)){Fail(site,"材料缺口超过确认的采集预算，请重新规划。");return;}
            var result=site.Collect(step.Item,out string message);Reason=message;Asking=result==ChainGatherResult.WaitingForApproval;
            int gained=Math.Max(0,site.Stock(step.Item)-before);
            _budget[step.Item]=Math.Max(0,_budget[step.Item]-gained);
            if(result==ChainGatherResult.Failed)Fail(site,message);
            return;
        }
        site.StopGather();Asking=false;
        bool done=step.Kind==ChainStepKind.Craft?site.Craft(step,out string status):site.Place(step,out status);
        Reason=status;if(!done){Fail(site,status);return;}Cursor++;
    }
    public void Cancel(ICraftChainSite? site,string reason)
    {
        if(State is not (ChainState.Preview or ChainState.Running))return;
        site?.StopGather();State=ChainState.Cancelled;Asking=false;Reason=reason;
    }
    private void Fail(ICraftChainSite site,string reason)
    {site.StopGather();State=ChainState.Failed;Asking=false;Reason=reason;}
}
