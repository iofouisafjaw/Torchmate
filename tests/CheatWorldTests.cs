using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TerrarAI.Content.Companion;

static class CheatWorldTests
{
    internal static void Run(Action<bool,string> check)
    {
        var authority=new CheatAuthority();
        var disabled=authority.Capture();
        check(!authority.Enabled&&!authority.Allows(disabled),"cheat switch starts disabled with no valid permit");
        authority.Toggle(); var permit=authority.Capture();
        check(authority.Allows(permit)&&!authority.Allows(disabled),"turning on grants a fresh permit, never upgrades an old request");
        authority.Toggle(); check(!authority.Allows(permit),"turning off revokes in-flight cheat permission");
        authority.Toggle(); check(!authority.Allows(permit),"off then on cannot revive a pending model action");
        permit=authority.Capture(); authority.Reset();
        check(!authority.Enabled&&!authority.Allows(permit),"visit reset disables switch and revokes every earlier permit");
        authority.Toggle(); check(!authority.Allows(permit),"new visit cannot reuse old visit revision zero");
        check(!new CheatAuthority().Allows(authority.Capture()),"permissions are bound to their authority session");

        var cases=new (string Text,CheatWorldOperation Operation,string Value)[] {
            ("把时间改成白天",CheatWorldOperation.Time,"04:30"),("请帮我把时间调到晚上。",CheatWorldOperation.Time,"19:30"),
            ("作弊时间设为午夜",CheatWorldOperation.Time,"00:00"),("时间改成正午",CheatWorldOperation.Time,"12:00"),
            ("调到黄昏",CheatWorldOperation.Time,"19:30"),("时间调整到04:29",CheatWorldOperation.Time,"04:29"),
            ("时间设为４：３０",CheatWorldOperation.Time,"04:30"),("把时间设为23:59",CheatWorldOperation.Time,"23:59"),
            ("天气改成晴天",CheatWorldOperation.Weather,"clear"),("帮我天气改为下雨",CheatWorldOperation.Weather,"rain"),
            ("停止下雨",CheatWorldOperation.Weather,"clear"),("开始下雨",CheatWorldOperation.Weather,"rain"),
            ("召唤克眼",CheatWorldOperation.Boss,"eye"),("请召唤一个Boss：双子魔眼",CheatWorldOperation.Boss,"twins"),
            ("召唤 Moon Lord",CheatWorldOperation.Boss,"moon_lord"),("刷出猪鲨boss！",CheatWorldOperation.Boss,"duke"),
            ("把出生点设在这里",CheatWorldOperation.Spawn,"here"),("设置出生点",CheatWorldOperation.Spawn,"here"),
            ("在这里设置世界出生点",CheatWorldOperation.Spawn,"here"),("把世界出生点设置为我脚下",CheatWorldOperation.Spawn,"here")
        };
        foreach(var c in cases)
        {
            bool parsed=CheatWorldRequest.TryParse(c.Text,out var request,out string error);
            check(parsed&&error.Length==0&&request==new CheatWorldRequest(c.Operation,c.Value),"bounded direct cheat request: "+c.Text);
            check(request!=null&&CheatWorldRequest.Authorizes(c.Text,request),"original goal authorizes exact cheat target: "+c.Text);
        }
        foreach(string text in new[]{"怎么召唤克眼","召唤克眼吗","不要召唤克眼","别把时间改成晚上","我想知道怎么改天气", "帮我准备打克眼",
            "最近的床在哪","召唤物是什么","克眼掉什么","保护我","请先查背包","记住以后遇到Boss就召唤克眼","他说‘召唤克眼’", "天气怎么样", "设置出生点需要床吗"})
            check(!CheatWorldRequest.TryParse(text,out _,out _),"queries/negation/preferences/other jobs do not become cheat commands: "+text);
        foreach(string text in new[]{"时间设为24:00","时间设为12:60","时间设为-1:00","时间设为4:3","时间设为晚上再召唤克眼",
            "天气改为血月","天气改为暴风雨","召唤50","召唤所有Boss","召唤两个克眼","召唤克眼和骷髅王","召唤模组Boss","召唤克眼的物品"})
            check(CheatWorldRequest.TryParse(text,out var bad,out string error)&&bad==null&&error.Length>0,"ambiguous/unsupported cheat action returns a refusal: "+text);
        check(!CheatWorldRequest.TryParse(new string('a',241),out _,out _),"cheat direct input is bounded");

        foreach(var (minute,day,ticks) in new (int,bool,double)[]{(0,false,16200),(269,false,32340),(270,true,0),(720,true,27000),(1169,true,53940),(1170,false,0),(1439,false,16140)})
            check(CheatWorldRequest.Clock(minute)==(day,ticks),"Terraria day/night boundary and tick offset at minute "+minute);
        bool allMinutes=true;
        for(int minute=0;minute<1440;minute++)
        {
            string text=$"{minute/60:00}:{minute%60:00}";
            var c=CheatWorldRequest.Clock(minute);
            allMinutes &= CheatWorldRequest.TryMinute(text,out int read)&&read==minute&&c.Ticks>=0&&c.Ticks<(c.Day?54000:32400)&&
                ((int)c.Ticks/60+(c.Day?270:1170))%1440==minute;
        }
        check(allMinutes,"all 1440 minutes round-trip using actual Terraria day/night lengths");
        foreach(int minute in new[]{-1,1440,int.MinValue,int.MaxValue})
        {
            bool rejected=false;try{CheatWorldRequest.Clock(minute);}catch(ArgumentOutOfRangeException){rejected=true;}
            check(rejected,"invalid clock minute rejected: "+minute);
        }
        foreach(string text in new[]{"00:00","04:30","19:30","23:59"})
            check(CheatWorldRequest.FromTool("time",text).Valid,"model time value accepted: "+text);
        check(CheatWorldRequest.Authorizes("时间改成黎明",CheatWorldRequest.FromTool("time","4:30")),"model HH:mm shorthand normalizes to the same authorized clock target");
        var goodState=new CheatWorldContext(true,true,false,false,false,200,new[]{"corruption","crimson","jungle","snow","underworld","hallow","deep_jungle","temple","ocean"});
        foreach(var b in CheatWorldRequest.Bosses)
        {
            var request=new CheatWorldRequest(CheatWorldOperation.Boss,b.Key);
            check(CheatWorldRequest.TryParse("召唤"+b.Name,out var parsed,out _)&&parsed==request,"every vanilla boss has a deterministic command: "+b.Name);
            check(CheatWorldRules.Refusal(request,goodState)==null,"valid context permits catalog boss: "+b.Name);
            check(CheatWorldRules.Refusal(request,goodState with{BossActive=true})!=null,"any active boss prevents stacking: "+b.Name);
            check(CheatWorldRules.Refusal(request,goodState with{FreeNpcSlots=b.Slots-1})!=null,"native NPC capacity preflight: "+b.Name);
            check(CheatWorldRules.Refusal(request,goodState with{Day=true})==null==!b.Night,"night requirements match boss catalog: "+b.Name);
            if(b.Biome.Length>0)check(CheatWorldRules.Refusal(request,goodState with{Biomes=Array.Empty<string>()})!=null,"biome requirement enforced: "+b.Name);
            foreach(string alias in b.Aliases)check(CheatWorldRequest.FindBoss(alias)?.Key==b.Key,"exact boss alias: "+alias);
        }
        check(CheatWorldRequest.Bosses.Select(b=>b.Key).Distinct().Count()==18,"bounded 18-boss vanilla catalog");
        check(CheatWorldRequest.FindBoss("眼")==null&&CheatWorldRequest.FindBoss("NPCID.50")==null,"boss lookup never uses fuzzy substrings or numeric NPC IDs");

        var validRequests=new[]{new CheatWorldRequest(CheatWorldOperation.Time,"21:00"),new(CheatWorldOperation.Weather,"rain"),
            new(CheatWorldOperation.Boss,"eye"),new(CheatWorldOperation.Spawn,"here")};
        foreach(var request in validRequests)
        {
            authority.Reset(); var site=new Site{State=goodState};
            check(!CheatWorldExecutor.Execute(authority,authority.Capture(),request,site).Success&&site.Reads==0&&site.Applies==0,"disabled switch prevents even native preflight: "+request.Operation);
            authority.Toggle(); permit=authority.Capture();
            check(CheatWorldExecutor.Execute(authority,permit,request,site).Success&&site.Applies==1,"authorized command runs exactly once: "+request.Operation);
            foreach(var blocked in new[]{goodState with{SinglePlayer=false},goodState with{Alive=false},goodState with{Paused=true}})
            {
                var s=new Site{State=blocked};
                check(!CheatWorldExecutor.Execute(authority,permit,request,s).Success&&s.Applies==0&&s.Preflights==0,"all cheat operations share live/singleplayer/unpaused gate: "+request.Operation);
            }
            foreach(string change in new[]{"off","off/on","reset","death","multiplayer","pause"})
            {
                var s=new Site{State=goodState}; var p=authority.Capture();
                s.OnPreflight=()=> { switch(change) {
                    case "off":authority.Toggle();break;case "off/on":authority.Toggle();authority.Toggle();break;
                    case "reset":authority.Reset();authority.Toggle();break;case "death":s.State=s.State with{Alive=false};break;
                    case "multiplayer":s.State=s.State with{SinglePlayer=false};break;case "pause":s.State=s.State with{Paused=true};break;
                }};
                check(!CheatWorldExecutor.Execute(authority,p,request,s).Success&&s.Applies==0,"fresh gate after preflight prevents stale mutation: "+request.Operation+" "+change);
                if(!authority.Enabled)authority.Toggle();
            }
        }
        var timeRequest=validRequests[0]; permit=authority.Capture();
        foreach(string change in new[]{"boss appeared","dawn","biome changed","slots filled"})
        {
            var request=new CheatWorldRequest(CheatWorldOperation.Boss,"eater");
            var changed=new Site{State=goodState};
            changed.OnPreflight=()=>changed.State=change switch {
                "boss appeared"=>goodState with{BossActive=true}, "dawn"=>goodState with{Day=true},
                "biome changed"=>goodState with{Biomes=Array.Empty<string>()}, _=>goodState with{FreeNpcSlots=0}
            };
            if(change=="dawn")request=new(CheatWorldOperation.Boss,"eye");
            check(!CheatWorldExecutor.Execute(authority,permit,request,changed).Success&&changed.Applies==0,"boss conditions are freshly revalidated after preflight: "+change);
        }
        var failed=new Site{State=goodState,Error="站位不安全"};
        check(CheatWorldExecutor.Execute(authority,permit,timeRequest,failed).Message=="站位不安全"&&failed.Applies==0,"preflight refusal preserves its real reason without mutation");
        var partial=new Site{State=goodState,Result=new(false,"只生成了一个主体")};
        check(!CheatWorldExecutor.Execute(authority,permit,timeRequest,partial).Success&&partial.Applies==1,"partial native receipt is not claimed as success or retried");
        foreach(string stage in new[]{"read","preflight","apply"})
        {
            var throwing=new Site{State=goodState,Throw=stage};
            check(!CheatWorldExecutor.Execute(authority,permit,timeRequest,throwing).Success&&throwing.Applies<2,"native exception yields bounded honest failure: "+stage);
        }
        foreach(var invalid in new[]{new CheatWorldRequest((CheatWorldOperation)999,"here"),new(CheatWorldOperation.Time,"24:00"),
            new(CheatWorldOperation.Weather,"bloodmoon"),new(CheatWorldOperation.Boss,"50"),new(CheatWorldOperation.Spawn,"100,200")})
        {
            var s=new Site{State=goodState};
            check(!CheatWorldExecutor.Execute(authority,permit,invalid,s).Success&&s.Applies==0&&s.Preflights==0,"invalid programmatic request never reaches world mutation: "+invalid);
        }

        CheatSpawnCell Ground(int x,int y)=>new(y<100,y==100,false);
        check(CheatWorldRules.SafeSpawn(100,100,1000,500,Ground),"world spawn feet coordinate uses three wide dry full-block floor and four clear rows");
        foreach(var point in new[]{(11,100),(988,100),(100,11),(100,488),(int.MinValue,int.MaxValue)})
        {
            int reads=0;
            check(!CheatWorldRules.SafeSpawn(point.Item1,point.Item2,1000,500,(x,y)=>{reads++;throw new Exception();})&&reads==0,"world-edge spawn rejected before tile reads: "+point);
        }
        foreach(string problem in new[]{"solid body","liquid body","missing floor","hazard floor"})
        {
            bool safe=CheatWorldRules.SafeSpawn(100,100,1000,500,(x,y)=>
                problem=="solid body"&&x==99&&y==96?new(false,false,false):
                problem=="liquid body"&&x==101&&y==99?new(true,false,true):
                problem=="missing floor"&&x==99&&y==100?new(false,false,false):
                problem=="hazard floor"&&x==101&&y==100?new(false,true,true):Ground(x,y));
            check(!safe,"spawn validation scans full footprint and refuses "+problem);
        }

        foreach(var c in new[]{("time","21:00"),("weather","clear"),("boss","twins"),("spawn","here")})
        {
            string json=JsonSerializer.Serialize(new{tool="cheat_world",mode=c.Item1,item=c.Item2});
            var call=AgentProtocol.Parse(json);
            check(call.Ends&&call.Tool=="cheat_world"&&call.Mode==c.Item1,"new model world tool is a terminal handoff: "+c.Item1);
            check(new AgentLoop("测试").OnModel(json).Kind==AgentOutcome.Run,"real agent loop routes world tool once: "+c.Item1);
        }
        foreach(string json in new[]{"""{"tool":"cheat_world"}""", """{"tool":"cheat_world","mode":"boss","item":"50"}""",
            """{"tool":"cheat_world","mode":"spawn","item":"here","x":100}""", """{"tool":"cheat_world","mode":"boss","item":"eye","count":2}""",
            """{"tool":"cheat_world","mode":"time","item":"24:00"}""", """{"tool":"cheat_world","mode":"weather","item":"bloodmoon"}""",
            """{"tool":"cheat_world","mode":"boss","item":"eye","item":"twins"}""", """{"tool":"cheat_world","mode":"time","item":"00:00","text":"召唤Boss"}"""})
        {
            bool rejected=false;try{AgentProtocol.Parse(json);}catch(FormatException){rejected=true;}
            check(rejected,"malformed, multiplied or coordinate-bearing model cheat rejected: "+json);
        }
        check(!CheatWorldRequest.Authorizes("准备打克眼",new(CheatWorldOperation.Boss,"eye"))&&
            !CheatWorldRequest.Authorizes("召唤克眼",new(CheatWorldOperation.Boss,"moon_lord"))&&
            !CheatWorldRequest.Authorizes("改成白天",new(CheatWorldOperation.Time,"21:00")),"model cannot change original operation or target");
        var fake=new TerrarAI.Evals.FakeWorld(new TerrarAI.Evals.WorldSpec{Cheat=true});
        string receipt=fake.Run(new("cheat_world","eye",Mode:"boss"),new AgentLoop("召唤克眼"));
        check(receipt.Contains("未修改真实")&&fake.Spawned.Count==0,"offline adapter never pretends a native boss or world edit happened");
        var off=new TerrarAI.Evals.FakeWorld(new TerrarAI.Evals.WorldSpec());
        check(off.Run(new("cheat_world","eye",Mode:"boss"),new AgentLoop("召唤克眼"))==CheatWorldExecutor.Off,"offline adapter also honors shared cheat switch");
    }
    private sealed class Site:ICheatWorldSite
    {
        public CheatWorldContext State;
        public int Reads,Preflights,Applies;
        public string? Error;
        public string Throw="";
        public Action? OnPreflight;
        public CheatWorldResult Result=new(true,"fixture observed");
        public CheatWorldContext Read(){Reads++;if(Throw=="read")throw new Exception("read fixture");return State;}
        public string? Preflight(CheatWorldRequest request){Preflights++;OnPreflight?.Invoke();if(Throw=="preflight")throw new Exception("preflight fixture");return Error;}
        public CheatWorldResult Apply(CheatWorldRequest request){Applies++;if(Throw=="apply")throw new Exception("apply fixture");return Result;}
    }
}
