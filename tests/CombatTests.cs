using System.Numerics;
using System.Text.Json;
using TerrarAI.Content.Companion;

static class CombatTests
{
    static readonly EnemyKey Key=new(4,1,100);
    static CombatTarget Enemy(float x=48,float y=0,int life=100)=>new(Key,new(x,y),Life:life);
    sealed class Site: ICombatSite
    {
        internal Dictionary<EnemyKey,CombatTarget> Enemies=new();
        internal CombatFrame Frame=null!;
        internal int Strikes,Approaches,Follows,Holds,Releases;
        internal List<(long Tick,EnemyKey Key,int Damage,int Cooldown)> Receipts=new();
        internal Func<CombatTarget,CombatTarget>? OnRead,BeforeStrike;
        internal CombatMove Movement=CombatMove.Pending;
        internal bool NoDamage;
        private readonly AttackCadence _cadence=new();
        internal bool EnforceCooldown;
        internal CombatFrame At(long tick,params CombatTarget[] targets)
        {
            Enemies=targets.ToDictionary(t=>t.Key);Frame=new(tick,Vector2.Zero,Vector2.Zero,targets,new());return Frame;
        }
        public CombatTarget? Read(EnemyKey key)
        {
            if(!Enemies.TryGetValue(key,out var t))return null;
            if(OnRead!=null){t=OnRead(t);Enemies.Remove(key);Enemies[t.Key]=t;}return t;
        }
        public CombatMove Approach(EnemyKey key,out string message){Approaches++;message="move";return Movement;}
        public CombatHit Strike(EnemyKey key,int damage,int cooldown)
        {
            if(!Enemies.TryGetValue(key,out var t))return new(false);
            if(BeforeStrike!=null){t=BeforeStrike(t);Enemies.Remove(key);Enemies[t.Key]=t;}
            if(t.Key!=key||!CombatRules.Eligible(t)||!t.Visible||t.Immune||CombatRules.Distance(Frame.Agent,t)>CombatRules.Reach||
                !Frame.ScopeValid||!Frame.PlayerAlive||Frame.Paused||EnforceCooldown&&!_cadence.Reserve(Frame.Tick,cooldown))return new(false);
            Strikes++;Receipts.Add((Frame.Tick,key,damage,cooldown));
            int actual=NoDamage?0:Math.Min(t.Life,damage);Enemies[key]=t with{Life=t.Life-actual};return new(true,actual,t.Life>0&&actual==t.Life);
        }
        public void Hold()=>Holds++;
        public void Follow()=>Follows++;
        public void Release()=>Releases++;
    }
    static CombatJob Job(CombatMode mode=CombatMode.Protect)=>new(mode,Vector2.Zero);
    internal static void Run(Action<bool,string> check)
    {
        foreach(string text in new[]{"保护我","请保护我。","请帮我保护我","跟着我保护我","protect"})
            check(CombatRequest.TryParse(text,out var mode)&&mode=="protect","local protection command: "+text);
        foreach(string text in new[]{"清理附近小怪","帮我清理附近的小怪","把周围的小怪清了","清怪","clear mobs"})
            check(CombatRequest.TryParse(text,out var mode)&&mode=="clear","bounded local clear command: "+text);
        foreach(string text in new[]{"停止攻击","取消保护","别打了","不要保护我","combat off"})
            check(CombatRequest.TryParse(text,out var mode)&&mode=="off","local stop combat command: "+text);
        foreach(string text in new[]{"以后保护我","你会不会保护我","怎么保护我","保护我吗？","如果我说保护我","不要清理附近小怪","保护我然后砍树","清理附近Boss","别伤害NPC","清理背包"})
            check(!CombatRequest.TryParse(text,out _),"questions, standing rules, negation and unrelated/compound goals cannot directly attack: "+text);
        foreach(string mode in new[]{"protect","clear","off"})
        {
            var call=AgentProtocol.Parse(JsonSerializer.Serialize(new{tool="combat",mode}));
            check(call.Mode==mode&&call.Ends&&!AgentProtocol.IsLookup(call.Tool),"model combat tool hands off to program: "+mode);
        }
        foreach(string raw in new[]{"""{"tool":"combat"}""","""{"tool":"combat","mode":"boss"}""","""{"tool":"control","mode":"protect"}"""})
        {bool rejected=false;try{AgentProtocol.Parse(raw);}catch(FormatException){rejected=true;}check(rejected,"invalid combat/control modes cannot execute: "+raw);}
        var gate=new DefenseGate();
        check(gate.Allows(true,true,true,false,true),"idle following enables default automatic defense");
        check(!gate.Allows(false,true,true,false,true)&&!gate.Allows(true,false,true,false,true)&&!gate.Allows(true,true,false,false,true)&&
            !gate.Allows(true,true,true,true,true)&&!gate.Allows(true,true,true,false,false),"config off, waiting, another action, model work and death all disable automatic defense");
        gate.Stop();check(!gate.Allows(true,true,true,false,true),"stop remains effective even while ordinary following resumes");
        gate.Resume();check(gate.Allows(true,true,true,false,true),"explicit follow/protect can restore automatic defense");
        var progress=new[]{new CombatProgress(),new(Eye:true),new(Evil:true),new(Skeletron:true),new(Hardmode:true),new(Mech:true),new(Plantera:true),new(Golem:true),new(Moonlord:true)};
        check(progress.Select(CombatRules.DamageCap).SequenceEqual(new[]{8,10,14,18,26,34,42,50,60}),"world progress raises damage caps from 8 through 60");
        foreach(var p in progress)
            check(CombatRules.Damage(p,999999)==CombatRules.DamageCap(p)&&CombatRules.Damage(p,-1)>0&&CombatRules.Damage(p,1)>=CombatRules.Damage(p,0),
                "weapon damage has a progress floor and cap: "+CombatRules.DamageCap(p));
        check(CombatRules.Cooldown(1)==48&&CombatRules.Cooldown(70)==70&&CombatRules.Cooldown(1000)==90,"fast weapons cannot bypass minimum cooldown; very slow weapons are bounded");
        foreach(var protectedTarget in new[]{Enemy() with{Friendly=true},Enemy() with{Town=true},Enemy() with{Critter=true},Enemy() with{Boss=true},
            Enemy() with{Immortal=true},Enemy() with{Active=false},Enemy() with{Life=0},Enemy() with{Invulnerable=true},Enemy() with{Chaseable=false},Enemy() with{HitAllowed=false},
            Enemy() with{Position=new(float.NaN,0)},Enemy() with{Width=-1}})
        {
            var job=Job();var site=new Site();job.Tick(site.At(1,protectedTarget),site);
            check(site.Strikes==0&&site.Approaches==0&&job.Hits==0,"protected/inactive/invalid target never approached or damaged: "+protectedTarget);
        }
        {
            var town=Enemy() with{Key=new(2,2,200),Town=true};var pet=Enemy() with{Key=new(3,3,300),Critter=true};var foe=Enemy(50,life:3);
            var site=new Site();var job=Job();job.Tick(site.At(1,town,pet,foe),site);
            check(site.Strikes==1&&job.Hits==1&&job.Kills==1&&job.DamageDealt==3&&job.LastHit==foe.Key&&site.Enemies[town.Key].Life==town.Life&&site.Enemies[pet.Key].Life==pet.Life,
                "overlapping town NPC and pet receive no collateral; kills and damage reflect actual enemy health loss");
        }
        foreach(var change in new Func<CombatTarget,CombatTarget>[] {t=>t with{Friendly=true},t=>t with{Town=true},t=>t with{Boss=true},t=>t with{Invulnerable=true},t=>t with{Visible=false},t=>t with{Position=new(1000,0)},
            t=>t with{Immune=true},t=>t with{Key=t.Key with{Generation=t.Key.Generation+1}}})
        {
            var site=new Site{OnRead=change};var job=Job();job.Tick(site.At(1,Enemy()),site);
            check(site.Strikes==0&&job.Hits==0,"fresh read prevents friendly, protected, hidden, distant, immune or slot-reused target damage");
        }
        {
            var site=new Site{BeforeStrike=t=>t with{Town=true}};var job=Job();job.Tick(site.At(1,Enemy()),site);
            check(site.Strikes==0&&job.Hits==0,"adapter rejects a protected flag change after the controller's fresh read");
        }
        {
            var site=new Site();var job=Job();var frame=site.At(1,Enemy() with{Visible=false});job.Tick(frame,site);
            check(site.Approaches==1&&site.Strikes==0,"wall occlusion routes around terrain instead of striking through it");
            var open=site.At(2,Enemy());job.Tick(open,site);check(site.Strikes==1,"becoming close and visible permits the first strike");
        }
        {
            var site=new Site();var job=Job();job.Tick(site.At(1,Enemy(160)),site);
            check(site.Approaches==1&&site.Strikes==0,"out-of-melee-range enemy requires real navigation first");
        }
        {
            var site=new Site();var job=Job();var t=Enemy(life:10000);
            for(int tick=1;tick<=120;tick++)job.Tick(site.At(tick,t),site);
            check(site.Receipts.Select(r=>r.Tick).SequenceEqual(new long[]{1,49,97}),"single enemy can be struck only once per 48 ticks");
            job.Tick(site.Frame,site);check(site.Strikes==3,"duplicate update of the same game tick cannot double strike");
        }
        {
            var site=new Site{EnforceCooldown=true};var t=Enemy();var a=Job();a.Tick(site.At(1,t),site);var b=Job();b.Tick(site.At(2,t),site);
            check(site.Strikes==1&&b.Hits==0,"adapter cooldown survives task/controller replacement");
        }
        {
            var site=new Site{NoDamage=true};var job=Job();job.Tick(site.At(1,Enemy(life:1)),site);
            check(site.Strikes==1&&job.Hits==0&&job.Kills==0&&job.DamageDealt==0,"strike attempt without health loss cannot invent a hit or kill");
        }
        {
            var site=new Site();var job=Job(CombatMode.Clear);job.Tick(site.At(1,Enemy(life:1)),site);
            for(int tick=2;tick<=CombatRules.QuietTicks;tick++)job.Tick(site.At(tick),site);
            check(job.State==CombatState.Running&&job.Kills==1,"clear completion waits for a fresh quiet-area audit");
            job.Tick(site.At(CombatRules.QuietTicks+1),site);check(job.State==CombatState.Completed&&job.Kills==1,"clear finishes after 90 quiet active ticks");
        }
        {
            var site=new Site();var job=Job(CombatMode.Clear);
            for(int tick=1;tick<CombatRules.QuietTicks;tick++)job.Tick(site.At(tick),site);
            job.Tick(site.At(90,Enemy(life:1)),site);
            for(int tick=91;tick<180;tick++)job.Tick(site.At(tick),site);
            check(job.State==CombatState.Running&&job.Kills==1,"new arrival resets the quiet audit rather than being ignored");
            job.Tick(site.At(180),site);check(job.State==CombatState.Completed,"quiet audit starts again after the new threat is handled");
        }
        foreach(var t in new[]{Enemy() with{Invulnerable=true},Enemy() with{HitAllowed=false},Enemy() with{Chaseable=false},Enemy() with{Immortal=true},Enemy() with{ContactDamage=0,Chaseable=false}})
        {
            var site=new Site();var job=Job(CombatMode.Clear);job.Tick(site.At(1,t),site);
            check(job.State==CombatState.Failed&&job.Remaining==1&&job.Kills==0,"unattackable hostile remains in the clear audit and cannot produce a success");
        }
        {
            var site=new Site{Movement=CombatMove.Failed};var job=Job(CombatMode.Clear);job.Tick(site.At(1,Enemy(200)),site);job.Tick(site.At(2,Enemy(200)),site);
            check(job.State==CombatState.Failed&&job.Remaining==1&&job.Hits==0,"unreachable enemy cannot be silently omitted from completion");
        }
        {
            var site=new Site{NoDamage=true};var job=Job(CombatMode.Clear);var t=Enemy();
            for(int tick=1;tick<=CombatRules.MaxTargetTicks+2;tick++)job.Tick(site.At(tick,t),site);
            check(job.State==CombatState.Failed,"no-progress target has a bounded attempt duration");
        }
        {
            var site=new Site();var job=Job(CombatMode.Clear);var t=Enemy(life:1000000);
            for(int tick=1;tick<=CombatRules.MaxClearTicks+1;tick++)job.Tick(site.At(tick,t),site);
            check(job.State==CombatState.Failed&&job.Hits>50&&job.Reason.Contains("时间"),"successful hits reset target-stall time but the whole clear job still times out");
        }
        {
            var site=new Site();var job=Job(CombatMode.Clear);
            for(int tick=1;tick<=90;tick++)job.Tick(site.At(tick,Enemy(400)),site);
            check(job.State==CombatState.Completed&&job.Hits==0,"targets outside the fixed clear radius are not chased or counted as kills");
        }
        {
            var site=new Site();var job=Job(CombatMode.Clear);var frame=site.At(1,Enemy());frame=frame with{Agent=new(500,0)};site.Frame=frame;job.Tick(frame,site);
            check(job.State==CombatState.Failed&&site.Strikes==0&&site.Approaches==0,"clear movement cannot leave the bounded task region");
        }
        {
            var site=new Site();var job=Job();var frame=site.At(1,Enemy(500));frame=frame with{Player=new(500,0)};site.Frame=frame;
            check(job.Tick(frame,site)&&site.Follows==1&&site.Strikes==0,"protection returns to the player before chasing a distant enemy");
            var auto=Job(CombatMode.Automatic);check(!auto.Tick(frame,site)&&site.Strikes==0,"distant automatic defense yields to existing following");
        }
        {
            var site=new Site();var job=Job(CombatMode.Automatic);job.Tick(site.At(1,Enemy(170)),site);
            check(site.Strikes==0&&site.Approaches==0,"automatic defense does not hunt enemies outside its threat radius");
            job.Tick(site.At(2,Enemy(100)),site);check(site.Approaches==1,"nearby threat triggers automatic approach without a model call");
        }
        {
            var site=new Site();var job=Job();var attacker=Enemy(10) with{Key=new(1,1,101)};var threat=Enemy(0) with{Key=new(2,1,102)};
            job.Tick(site.At(1,attacker,threat),site);check(site.Receipts.Single().Key==threat.Key,"protection prioritizes the threat closest to the player");
        }
        {
            var site=new Site();var job=Job();job.Tick(site.At(1),site);check(job.State==CombatState.Running&&site.Follows==1,"protect stays active and follows when the area is quiet");
            job.Cancel(site,"stop");int strikes=site.Strikes;job.Tick(site.At(100,Enemy()),site);
            check(job.State==CombatState.Cancelled&&site.Strikes==strikes,"stop prevents all later strikes even when an enemy appears");
        }
        foreach(var mutate in new Func<CombatFrame,CombatFrame>[] {f=>f with{ScopeValid=false},f=>f with{PlayerAlive=false},f=>f with{Player=new(float.NaN,0)}})
        {
            var site=new Site();var job=Job();var frame=mutate(site.At(1,Enemy()));site.Frame=frame;job.Tick(frame,site);
            check(job.State==CombatState.Cancelled&&site.Strikes==0&&site.Approaches==0,"death, world/control invalidation or invalid location cancels before effects");
        }
        {
            var site=new Site();var job=Job(CombatMode.Clear);
            for(int tick=1;tick<=200;tick++){var f=site.At(tick,Enemy());f=f with{Paused=true};site.Frame=f;job.Tick(f,site);}
            check(job.State==CombatState.Running&&site.Strikes==0&&site.Approaches==0&&site.Holds==0,"paused updates do not strike, move, audit or consume execution time");
            job.Tick(site.At(201,Enemy()),site);check(site.Strikes==1,"execution resumes on the first active game tick");
        }
        {
            var site=new Site();var job=Job(CombatMode.Automatic);var f=site.At(1,Enemy()) with{FollowAllowed=false};site.Frame=f;
            check(!job.Tick(f,site)&&site.Strikes==0,"automatic mode cannot interrupt an active task or waiting state");
        }
        {
            var site=new Site{Movement=CombatMove.Failed};var job=Job();job.Tick(site.At(1,Enemy(160)),site);site.Movement=CombatMove.Pending;
            job.Tick(site.At(2,Enemy(160)),site);check(site.Approaches==1,"failed protection routes back off rather than replanning every frame");
            check(job.Reason.Contains("还有")&&job.Remaining==1,"unreachable threat is reported instead of claiming the player is safe");
            job.Tick(site.At(301,Enemy(160)),site);check(site.Approaches==2,"live protection can retry a previously unreachable enemy after backoff");
        }
        // Seeded geometry/flag combinations include protected NPCs overlapping enemies.
        var random=new Random(181026);
        for(int trial=0;trial<100;trial++)
        {
            var targets=Enumerable.Range(0,12).Select(i=>new CombatTarget(new(i,i+1,trial*100+i+1),new(random.Next(-230,231),random.Next(-80,81)),
                Friendly:random.Next(3)==0,Town:random.Next(4)==0,Critter:random.Next(6)==0,Boss:random.Next(6)==0,
                Invulnerable:random.Next(6)==0,Visible:random.Next(4)>0,Immune:random.Next(5)==0)).ToArray();
            var site=new Site();var job=Job();var frame=site.At(1,targets);job.Tick(frame,site);
            check(site.Receipts.All(r=>targets.Any(t=>t.Key==r.Key&&CombatRules.Eligible(t)&&!t.Immune&&t.Visible&&
                CombatRules.InScope(job.Mode,job.Origin,frame,t)&&CombatRules.Distance(frame.Agent,t)<=CombatRules.Reach&&r.Damage<=8)),
                "random target set cannot damage protected, distant or occluded NPCs; seed181026 case "+trial);
        }
    }
}
