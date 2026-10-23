using TerrarAI.Content.Companion;

static class HealingTests
{
    static HealingPotion Potion(int slot,int heal,int type=1)=>new(slot,type,10,heal);
    internal static void Run(Action<bool,string> check)
    {
        var small=Potion(0,50);var normal=Potion(1,100,2);var large=Potion(2,200,3);
        check(HealingRules.Select(30,100,new[]{normal,small})==small,"healing prefers a fitting 50 HP potion to wasting a 100 HP potion on 70 missing HP");
        check(HealingRules.Select(120,400,new[]{small,normal,large})==large,"largest fitting potion maximizes useful healing during native cooldown");
        check(HealingRules.Select(30,100,new[]{Potion(4,70),small})?.Heal==70,"exact fit takes priority over weaker potion");
        check(HealingRules.Select(20,100,new[]{normal})==normal&&HealingRules.Select(21,100,new[]{normal})==null,"non-emergency overheal requires at least eighty percent useful healing");
        check(HealingRules.Select(15,100,new[]{large})==large&&HealingRules.Select(16,100,new[]{large})==null,"emergency large-potion fallback has a precise fifteen-percent boundary");
        check(HealingRules.Select(1,100,new[]{large,small})==small,"available fitting potion avoids unnecessary emergency overflow");
        check(HealingRules.Select(10,100,new[]{Potion(3,200),Potion(1,150)})?.Heal==150,"emergency fallback chooses the smallest overlarge potion");
        check(HealingRules.Select(10,100,new[]{small,Potion(4,50)})==small,"equal healing uses stable slot order");
        foreach(var invalid in new[]{small with{Slot=-1},small with{Slot=50},small with{Type=0},small with{Count=0},small with{Heal=0},small with{Favorite=true},small with{Supported=false},small with{Usable=false}})
            check(HealingRules.Select(10,100,new[]{invalid})==null,"invalid, reserved or unsupported potion excluded: "+invalid);
        check(HealingRules.Select(0,100,new[]{small})==null&&HealingRules.Select(100,100,new[]{small})==null&&HealingRules.Select(100,0,new[]{small})==null,"dead/full/invalid life never selects a potion");
        check(HealingRules.Select(1,int.MaxValue,new[]{Potion(0,int.MaxValue)})!=null,"selection percentage comparisons do not overflow large life or heal values");
        var rig=new Rig();var result=rig.Step();
        check(result?.Success==true&&result.Receipt.Healed==50&&result.Receipt.Consumed==1&&rig.Site.NativeCalls==1,"eligible low-health player performs one real-site call and reports actual healing/consumption");
        check(rig.Site.Delay==3600&&rig.Site.Sick,"production policy leaves cooldown and sickness owned by native site");
        rig.Site.Life=20;rig.Step(advanceFrame:false);check(rig.Site.NativeCalls==1,"same-frame duplicate cannot consume twice even after further damage");
        rig.Advance(3599);check(rig.Site.NativeCalls==1,"persistent low life cannot bypass native sixty-second cooldown");
        rig.Step();check(rig.Site.NativeCalls==2,"low life can heal again after the real native cooldown reaches zero");
        var restoration=new Rig();restoration.Site.Potions[0]=Potion(0,50,5);restoration.Site.NativeDelay=2700;
        restoration.Step();restoration.Site.Life=20;restoration.Advance(2699);
        check(restoration.Site.NativeCalls==1,"restoration/accessory-style shorter native cooldown remains active until its own boundary");
        restoration.Step();check(restoration.Site.NativeCalls==2&&restoration.Site.Delay==2700,"helper honors shorter native cooldown without replacing it with fixed sixty seconds");
        foreach(string blocked in new[]{"disabled","paused","absent","dead","delay","sickness","items blocked","healthy","invalid max","invalid session","invalid player"})
        {
            var r=new Rig();
            switch(blocked)
            {
                case "disabled":r.Options=new(false);break;case "paused":r.Site.Paused=true;break;case "absent":r.Site.Available=false;break;
                case "dead":r.Site.Alive=false;break;case "delay":r.Site.Delay=10;break;case "sickness":r.Site.Sick=true;break;
                case "items blocked":r.Site.Blocked=true;break;case "healthy":r.Site.Life=31;break;case "invalid max":r.Site.Maximum=0;break;
                case "invalid session":r.Site.Session=Guid.Empty;break;case "invalid player":r.Site.Player=-1;break;
            }
            check(r.Step()==null&&r.Site.NativeCalls==0,"automatic treatment has no side effects when "+blocked);
        }
        foreach(int threshold in new[]{0,9,51,int.MaxValue})
        {var r=new Rig{Options=new(true,threshold)};check(r.Step()==null&&r.Site.NativeCalls==0,"invalid configured threshold blocks treatment: "+threshold);}
        var custom=new Rig{Options=new(true,50)};custom.Site.Life=40;check(custom.Step()?.Success==true,"configured health threshold uses effective life maximum");
        var effective=new Rig();effective.Site.Maximum=1000;effective.Site.Life=300;
        check(effective.Step()?.Success==true,"exact thirty percent uses modified effective maximum rather than fixed HP");
        var empty=new Rig();empty.Site.Potions.Clear();empty.Step();empty.Advance(29);
        check(empty.Site.Scans==1&&empty.Site.NativeCalls==0,"empty inventory scans are bounded without attempting native healing");
        empty.Site.Potions.Add(small);empty.Step();check(empty.Site.NativeCalls==1,"new main-bag potion becomes eligible at next bounded scan");
        var failedUse=new Rig();failedUse.Site.Reject=true;failedUse.Step();failedUse.Advance(299);
        check(failedUse.Site.Attempts==1&&failedUse.Site.NativeCalls==0,"native refusal does not consume and is not hammered every frame");
        failedUse.Site.Reject=false;failedUse.Step();check(failedUse.Site.NativeCalls==1,"failed native use can retry after a five-second active-update gap");
        foreach(string change in new[]{"healed","more damage","maximum","session","player","frame","pause","cooldown","item"})
        {
            var r=new Rig();r.Site.DuringScan=()=>{
                switch(change){case "healed":r.Site.Life=40;break;case "more damage":r.Site.Life=10;break;case "maximum":r.Site.Maximum=200;break;
                case "session":r.Site.Session=Guid.NewGuid();break;case "player":r.Site.Player++;break;case "frame":r.Site.Frame++;break;
                case "pause":r.Site.Paused=true;break;case "cooldown":r.Site.Delay=100;break;case "item":r.Site.Potions[0]=small with{Count=1};break;}
            };
            check(r.Step()==null&&r.Site.NativeCalls==0,"fresh frame/item validation rejects changed state before consumption: "+change);
        }
        var noEffect=new Rig();noEffect.Site.HealOverride=0;var bad=noEffect.Step();noEffect.Advance(5000);
        check(bad?.Success==false&&bad.Suspended&&noEffect.Site.NativeCalls==1,"consumed potion without actual healing suspends this episode instead of draining another potion");
        noEffect.Options=new(false);noEffect.Advance(600);noEffect.Options=new();noEffect.Step();
        check(noEffect.Site.NativeCalls==1,"settings toggle cannot clear failed-use suspension");
        noEffect.Site.Life=45;noEffect.Step();noEffect.Site.Life=20;noEffect.Site.HealOverride=null;noEffect.Step();
        check(noEffect.Site.NativeCalls==2,"recovery to the hysteresis threshold permits a new low-health episode");
        var zero=new Rig();zero.Site.Maximum=500;zero.Site.Life=10;zero.Site.NativeDelay=0;zero.Step();zero.Advance(5000);
        check(zero.Site.NativeCalls==1,"mod removed native cooldown cannot drain repeated potions while still in one low-health episode");
        zero.Site.Life=225;zero.Step();zero.Site.Life=10;zero.Step();check(zero.Site.NativeCalls==2,"no-cooldown mode rearms only after actual recovery and later damage");
        var infinite=new Rig();infinite.Site.Spend=0;var free=infinite.Step();
        check(free?.Success==true&&free.Receipt.Consumed==0&&infinite.Site.Potions[0].Count==10,"native consume-saving hook is respected, never followed by manual stack subtraction");
        var extra=new Rig();extra.Site.Spend=2;check(extra.Step()?.Suspended==true,"unexpected multi-potion consumption is reported and stops further use");
        var actual=new Rig();actual.Site.HealOverride=12;check(actual.Step()?.Receipt.Healed==12,"receipt uses actual life gain instead of nominal item healing");
        var exception=new Rig();exception.Site.Throw=true;check(exception.Step()?.Suspended==true,"native exception suspends and does not masquerade as successful treatment");
        var scanError=new Rig();scanError.Site.ThrowOnScan=true;scanError.Step();scanError.Advance(500);
        check(scanError.Site.Scans==1&&scanError.Site.NativeCalls==0,"failing inventory hook cannot crash or be retried each frame");
        var paused=new Rig();paused.Site.Reject=true;paused.Step();paused.Site.Paused=true;paused.Advance(2000);paused.Site.Paused=false;paused.Site.Reject=false;
        paused.Advance(299);check(paused.Site.NativeCalls==0,"paused updates do not consume failed-use retry budget");paused.Step();check(paused.Site.NativeCalls==1,"active updates resume the same retry deadline");
        var scope=new Rig();scope.Site.HealOverride=0;scope.Step();scope.Site.Session=Guid.NewGuid();scope.Site.Delay=0;scope.Site.Sick=false;scope.Site.HealOverride=null;scope.Site.Life=20;
        check(scope.Step()?.Success==true,"new world visit cannot inherit another session's healing suspension");
        scope.Site.Player++;scope.Site.Delay=0;scope.Site.Sick=false;scope.Site.Life=20;
        check(scope.Step()?.Success==true,"different player gets independent treatment state");
        Gate(check);ReminderIntegration(check);
        int uses=0;
        for(int run=0;run<60;run++)
        {
            var random=new Random(241026+run);var r=new Rig();bool valid=true;int priorCount=0;long lastUse=-100000;
            for(int tick=0;tick<12000;tick++)
            {
                if(tick%67==0)r.Site.Life=random.Next(1,101);
                if(tick%223==0)r.Options=new(random.Next(5)>0);
                r.Site.Paused=random.Next(20)==0;r.Site.Available=random.Next(30)!=0;r.Site.Blocked=random.Next(35)==0;
                int before=r.Site.Delay;bool pausedFrame=r.Site.Paused;
                r.Step();
                if(r.Site.NativeCalls==priorCount)continue;
                uses++;priorCount=r.Site.NativeCalls;
                valid&=r.Options.Enabled&&!pausedFrame&&r.Site.Available&&!r.Site.Blocked&&before<=1&&r.Site.Frame-lastUse>=AutomaticHealing.RetryGapTicks;
                lastUse=r.Site.Frame;
            }
            check(valid,"generated healing stream respects actual cooldown, flags and consumption spacing seed241026 case "+run);
        }
        check(uses>50,"generated streams exercise actual healing calls rather than only silence");
    }
    static void Gate(Action<bool,string> check)
    {
        var gate=new HealingSelectionGate<object,object>();var player=new object();var otherPlayer=new object();var chosen=new object();var other=new object();
        check(gate.Allows(player,other),"item selection gate is inert outside native quick-heal scope");
        bool selected=false,nested=true;
        gate.Run(player,chosen,()=>{selected=gate.Allows(player,chosen)&&!gate.Allows(player,other)&&gate.Allows(otherPlayer,other);nested=gate.Run(player,other,()=>{});});
        check(selected&&!nested&&gate.Allows(player,other),"scoped gate selects only original potion, leaves other players alone and cannot be replaced by a nested request");
        try{gate.Run(player,chosen,()=>throw new InvalidOperationException());}catch(InvalidOperationException){}
        check(gate.Allows(player,other)&&gate.Run(player,other,()=>{}),"exception always releases item filtering without inventory swaps");
    }
    static void ReminderIntegration(Action<bool,string> check)
    {
        var reminders=new ProactiveReminders();var session=Guid.NewGuid();var world=new ReminderWorldState(false,true,10,100,50);
        check(reminders.Tick(new(session,0,1,world,HealthHandled:true),new())==null,"visible healing receipt prevents same-frame low-health and blood-moon duplicate notices");
        int low=0,blood=0;
        for(int tick=2;tick<=3601;tick++){var n=reminders.Tick(new(session,0,tick,world),new());if(n?.Kind==ReminderKind.LowHealth)low++;if(n?.Kind==ReminderKind.BloodMoon)blood++;}
        check(low==0&&blood==1,"treatment acknowledges still-low episode and delays other reminders by existing shared gap");
        var healed=new ProactiveReminders();check(healed.Tick(new(session,0,1,world with{Life=70},HealthHandled:true),new())==null,"post-treatment health snapshot never reports old pre-heal life");
        var r=new Rig();var notice=new ProactiveReminders().Tick(new(session,0,1,world),new(Enabled:false));
        check(r.Step()?.Success==true&&notice==null,"reminder master switch is independent of the separate automatic healing option");
    }
    sealed class Rig
    {
        internal readonly AutomaticHealing Policy=new();internal readonly Site Site=new();internal HealingOptions Options=new();
        internal HealingResult? Step(bool advanceFrame=true)
        {if(advanceFrame){Site.Frame++;if(!Site.Paused&&Site.Delay>0){Site.Delay--;if(Site.Delay==0)Site.Sick=false;}}return Policy.Tick(Site,Options);}
        internal void Advance(int ticks){for(int i=0;i<ticks;i++)Step();}
    }
    sealed class Site:IAutomaticHealingSite
    {
        internal Guid Session=Guid.NewGuid();internal int Player,Life=20,Maximum=100,Delay,NativeDelay=3600,NativeCalls,Attempts,Scans,Spend=1;
        internal long Frame;internal bool Sick,Blocked,Paused,Alive=true,Available=true,Reject,Throw,ThrowOnScan;
        internal int? HealOverride;internal Action? DuringScan;
        internal List<HealingPotion> Potions=new(){Potion(0,50)};
        public HealingFrame Read()=>new(new(Session,Player,Frame,new(true,false,Life,Maximum,50),Available,Alive,Paused),Delay,Sick,Blocked);
        public IReadOnlyList<HealingPotion> Inventory(){Scans++;if(ThrowOnScan)throw new InvalidOperationException();var snapshot=Potions.ToArray();DuringScan?.Invoke();return snapshot;}
        public HealingReceipt Use(HealingPotion p)
        {
            Attempts++;if(Throw)throw new InvalidOperationException();int slot=Potions.FindIndex(x=>x.Slot==p.Slot);
            if(Reject||slot<0||Potions[slot]!=p)return new(false,Life,Life,p.Count,p.Count,Delay,Sick);
            NativeCalls++;int before=Life;Life=Math.Min(Maximum,Life+(HealOverride??p.Heal));
            int after=Math.Max(0,p.Count-Spend);Potions[slot]=p with{Count=after};Delay=NativeDelay;Sick=Delay>0;
            return new(true,before,Life,p.Count,after,Delay,Sick);
        }
    }
}
