using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TerrarAI.Content.Companion;

public sealed record ChainTarget(int Item,int Count);
public sealed record ChainRecipe(int Id,int Item,int Output,IReadOnlyList<IngredientRequirement> Ingredients,IReadOnlyList<int> Stations)
{
    public string Signature=>JsonSerializer.Serialize(new{Id,Item,Output,ingredients=Ingredients.Select(r=>new{r.Type,r.Count,accepted=r.Accepted.OrderBy(x=>x).ToArray()}),stations=Stations.OrderBy(x=>x).ToArray()});
}
public interface ICraftChainCatalog
{
    IEnumerable<ChainRecipe> Recipes(int item);
    IEnumerable<int> StationItems(int tile);
    bool Collectable(int item);
    IEnumerable<int> SupportStations(int tile) => Array.Empty<int>();
}
public enum ChainStepKind { Gather,Craft,Place }
public sealed record ChainStep(ChainStepKind Kind,int Item,int Count,ChainRecipe? Recipe=null,int Station=-1,
    IReadOnlyDictionary<int,int>? Ingredients=null);

public sealed class CraftChainPlan
{
    public IReadOnlyList<ChainTarget> Targets {get;}
    public IReadOnlyList<ChainStep> Steps {get;}
    public IReadOnlyDictionary<int,int> Manual {get;}
    public IReadOnlyDictionary<int,int> Gather {get;}
    public string Signature=>JsonSerializer.Serialize(new{Targets,steps=Steps.Select(s=>new{s.Kind,s.Item,s.Count,s.Station,recipe=s.Recipe?.Signature,
        ingredients=s.Ingredients?.OrderBy(x=>x.Key).ToArray()}),manual=Manual.OrderBy(x=>x.Key).ToArray()});
    public bool CanExecute=>Manual.Count==0;
    internal CraftChainPlan(IReadOnlyList<ChainTarget> targets,IReadOnlyList<ChainStep> steps,IReadOnlyDictionary<int,int> manual)
    {
        Targets=targets;Steps=steps;Manual=manual;
        Gather=steps.Where(s=>s.Kind==ChainStepKind.Gather).GroupBy(s=>s.Item).ToDictionary(g=>g.Key,g=>g.Sum(s=>s.Count));
    }

    // Stock required now to execute every prefix of the remaining plan and retain
    // the final requested items. Whole-tree surplus can therefore skip later gathers.
    public int Required(int item,int cursor)
    {
        int deficit=0,peak=0;
        foreach(var step in Steps.Skip(cursor))
        {
            if(step.Kind==ChainStepKind.Gather)continue;
            if(step.Kind==ChainStepKind.Place&&step.Item==item)deficit++;
            if(step.Kind==ChainStepKind.Craft)
            {
                deficit=checked(deficit+(step.Ingredients?.GetValueOrDefault(item)??0));peak=Math.Max(peak,deficit);
                if(step.Item==item)deficit=checked(deficit-step.Count*step.Recipe!.Output);
            }
            peak=Math.Max(peak,deficit);
        }
        deficit=checked(deficit+Targets.Where(t=>t.Item==item).Sum(t=>t.Count));
        return Math.Max(peak,Math.Max(0,deficit));
    }
}

public static class CraftChainPlanner
{
    public const int MaxDepth=24,MaxSteps=256,MaxSearch=1200,MaxAlternatives=8;
    public static CraftChainPlan Build(IReadOnlyList<ChainTarget> goals,IReadOnlyDictionary<int,int> stock,
        IEnumerable<int> stations,ICraftChainCatalog catalog)
    {
        if(goals.Count is <1 or >8||goals.Any(t=>t.Item<1||t.Count is <1 or >999)||goals.Select(t=>t.Item).Distinct().Count()!=goals.Count)
            throw new ArgumentException("合成目标需为1–8种明确物品，每种1–999个。 ");
        var state=new State(stock,stations);int searched=0;
        var recyclingRoots=new HashSet<int>();
        bool EnsureStation(int tile,int depth,HashSet<int> path,HashSet<int> stationPath,ref State current)
        {
            if(current.Stations.Contains(tile))return true;
            if(depth>MaxDepth||stationPath.Contains(tile)||++searched>MaxSearch)return false;
            var supports=new HashSet<int>(stationPath){tile};
            var baseState=current.Copy();
            foreach(int support in catalog.SupportStations(tile).Distinct())
                if(!EnsureStation(support,depth+1,path,supports,ref baseState))return false;
            State? best=null;
            foreach(int item in catalog.StationItems(tile).Distinct().Take(4))
            {
                // A decorative/drop-only item that happens to create the same tile
                // is not a useful alternative to a craftable station. Owned ones
                // remain first-class candidates, including those from chests.
                if(baseState.Stock.GetValueOrDefault(item)==0&&!catalog.Collectable(item)&&!catalog.Recipes(item).Any())continue;
                var branch=baseState.Copy();
                if(!Ensure(item,1,depth+1,path,ref branch)||branch.Steps.Count>=MaxSteps)continue;
                branch.Steps.Add(new(ChainStepKind.Place,item,1,Station:tile));branch.Stations.Add(tile);
                if(best==null||Score(branch)<Score(best))best=branch;
                // Prefer a feasible station over exploring every decorative
                // variant. Inventory is checked first by the native catalogue.
                if(branch.Manual.Values.Sum(v=>(long)v)==baseState.Manual.Values.Sum(v=>(long)v))break;
            }
            if(best==null)return false;current=best;return true;
        }
        bool Ensure(int item,int count,int depth,HashSet<int> path,ref State current)
        {
            if(count==0)return true;
            if(depth>MaxDepth||++searched>MaxSearch||current.Steps.Count>=MaxSteps)return false;
            int take=Math.Min(count,current.Stock.GetValueOrDefault(item));
            current.Stock[item]=current.Stock.GetValueOrDefault(item)-take;
            int missing=count-take;if(missing==0)return true;
            if(path.Contains(item)){recyclingRoots.Add(item);return false;}
            var nextPath=new HashSet<int>(path){item};
            State? best=null;
            if(catalog.Collectable(item))
            {
                best=current.Copy();best.Steps.Add(new(ChainStepKind.Gather,item,missing));
            }
            foreach(var recipe in catalog.Recipes(item).Take(MaxAlternatives))
            {
                if(recipe.Output<1||recipe.Item!=item||recipe.Ingredients.Any(r=>r.Count<1))continue;
                var branch=current.Copy();bool valid=true;
                foreach(int tile in recipe.Stations.Distinct())
                {
                    if(!EnsureStation(tile,depth+1,nextPath,new(),ref branch)){valid=false;break;}
                }
                if(!valid)continue;
                int batches=checked((missing+recipe.Output-1)/recipe.Output);
                var requirements=recipe.Ingredients.Select(r=>r with{Count=checked(r.Count*batches)}).ToArray();
                var reserved=IngredientAllocation.Plan(branch.Stock,requirements);
                var concrete=new Dictionary<int,int>(reserved.Used);
                foreach(var (type,amount) in reserved.Used)branch.Stock[type]-=amount;
                foreach(var (required,amount) in reserved.Missing)
                {
                    var requirement=requirements.Single(r=>r.Type==required);
                    State? ingredientBest=null;int chosen=0;
                    foreach(int candidate in requirement.Accepted.Prepend(required).Distinct().Take(MaxAlternatives))
                    {
                        var ingredientBranch=branch.Copy();
                        if(!Ensure(candidate,amount,depth+1,nextPath,ref ingredientBranch))continue;
                        if(ingredientBest==null||Score(ingredientBranch)<Score(ingredientBest)){ingredientBest=ingredientBranch;chosen=candidate;}
                        if(ingredientBranch.Manual.Values.Sum(v=>(long)v)==branch.Manual.Values.Sum(v=>(long)v))break;
                    }
                    if(ingredientBest==null){valid=false;break;}
                    branch=ingredientBest;concrete[chosen]=checked(concrete.GetValueOrDefault(chosen)+amount);
                }
                if(!valid)continue;
                branch.Steps.Add(new(ChainStepKind.Craft,item,batches,recipe,Ingredients:concrete));
                branch.Stock[item]=checked(branch.Stock.GetValueOrDefault(item)+batches*recipe.Output-missing);
                if(best==null||Score(branch)<Score(best))best=branch;
            }
            if(best==null)
            {
                // A blocked leaf is shown in the complete bill but cannot execute.
                // Sand/cactus and other uncollectable raw materials may only have
                // recycling recipes (platforms/walls -> their original material).
                // Propagate the loop back to its repeated item, then list that
                // material as manual supply. Never pretend the recycling loop
                // produces it, nor hide a genuine search-budget failure.
                if(searched>MaxSearch||catalog.Recipes(item).Any()&&!recyclingRoots.Contains(item))return false;
                best=current.Copy();best.Manual[item]=checked(best.Manual.GetValueOrDefault(item)+missing);
            }
            if(best.Steps.Count>MaxSteps)return false;
            current=best;return true;
        }
        foreach(var target in goals)
            if(!Ensure(target.Item,target.Count,0,new(),ref state))
                throw new ArgumentException(searched>MaxSearch?
                    $"合成目标 {target.Item} 达到配方搜索预算（{MaxSearch}），请先准备部分中间件／制作站后重试；未执行备料。":
                    $"合成目标 {target.Item} 的配方存在循环或制作站依赖不可用，请先准备部分中间件／制作站后重试；未执行备料。");
        return new(goals.ToArray(),state.Steps.ToArray(),new Dictionary<int,int>(state.Manual));
    }
    private static long Score(State state)=>state.Manual.Values.Sum(v=>(long)v)*1000000+
        state.Steps.Where(s=>s.Kind==ChainStepKind.Gather).Sum(s=>(long)s.Count)*100+state.Steps.Count;
    private sealed class State
    {
        internal Dictionary<int,int> Stock,Manual=new();internal HashSet<int> Stations;internal List<ChainStep> Steps=new();
        internal State(IReadOnlyDictionary<int,int> stock,IEnumerable<int> stations){Stock=stock.Where(x=>x.Value>0).ToDictionary(x=>x.Key,x=>x.Value);Stations=stations.ToHashSet();}
        internal State Copy()=>new(Stock,Stations){Manual=new(Manual),Steps=new(Steps)};
    }
}
