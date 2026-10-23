using TerrarAI.Content.Companion;

static class ReminderTests
{
    sealed class Rig
    {
        internal readonly ProactiveReminders Engine=new();
        internal Guid Session=Guid.NewGuid();
        internal int Player;
        internal long Tick;
        internal ReminderWorldState World=new(true,false,100,100,50);
        internal ReminderOptions Options=new();
        internal bool Available=true,Alive=true,Paused;
        internal readonly List<(long Tick,ReminderNotice Notice)> Notices=new();
        internal ReminderNotice? Step()
        {
            var notice=Engine.Tick(new(Session,Player,++Tick,World,Available,Alive,Paused),Options);
            if(notice!=null)Notices.Add((Tick,notice));return notice;
        }
        internal void Advance(int ticks){for(int i=0;i<ticks;i++)Step();}
        internal int Count(ReminderKind kind)=>Notices.Count(n=>n.Notice.Kind==kind);
    }
    internal static void Run(Action<bool,string> check)
    {
        var rig=new Rig();rig.Advance(1000);
        check(rig.Notices.Count==0,"healthy daytime and roomy bag need no reminder");
        rig.World=rig.World with{Day=false};
        check(rig.Step()?.Kind==ReminderKind.Night,"day-to-night transition sends one dusk reminder");
        rig.Advance(ProactiveReminders.KindGapTicks+100);
        check(rig.Count(ReminderKind.Night)==1,"ordinary night stays quiet even after cooldown elapsed");
        rig=new(){World=new(false,false,100,100,50)};rig.Advance(1000);
        check(rig.Notices.Count==0,"entering an existing ordinary night does not replay dusk");
        rig=new(){World=new(false,true,100,100,50)};rig.Advance(ProactiveReminders.WarmupTicks-1);
        check(rig.Notices.Count==0,"new-world grace avoids immediate join-time notification");
        check(rig.Step()?.Kind==ReminderKind.BloodMoon,"already active blood moon can be reported after join grace");
        rig.Advance(ProactiveReminders.KindGapTicks+100);
        check(rig.Count(ReminderKind.BloodMoon)==1&&rig.Count(ReminderKind.Night)==0,"one ongoing blood moon never repeats or adds ordinary dusk");
        rig=new();rig.Advance(400);rig.World=rig.World with{Day=false,BloodMoon=true};rig.Step();rig.Advance(4000);
        check(rig.Count(ReminderKind.BloodMoon)==1&&rig.Count(ReminderKind.Night)==0,"blood moon replaces simultaneous dusk instead of sending two notices");
        rig=new(){Options=new(BloodMoon:false)};rig.Advance(400);rig.World=rig.World with{Day=false,BloodMoon=true};
        check(rig.Step()?.Kind==ReminderKind.Night,"turning blood-moon reminder off keeps independent dusk setting effective");
        rig=new(){World=new(true,true,100,100,50)};rig.Advance(500);
        check(rig.Notices.Count==0,"transient blood-moon flag during daytime is not a live night event");

        rig=new(){World=new(false,true,20,100,2)};rig.Advance(8000);
        check(rig.Notices.Select(n=>n.Notice.Kind).SequenceEqual(new[]{ReminderKind.LowHealth,ReminderKind.BloodMoon,ReminderKind.Inventory}),
            "simultaneous needs prioritize health, blood moon and bag without flooding");
        check(rig.Notices.Zip(rig.Notices.Skip(1)).All(pair=>pair.Second.Tick-pair.First.Tick>=3600),"all categories share a minimum 60-second active-time gap");
        rig.Advance(40000);
        check(rig.Notices.Count==3,"persistent low health/full bag/blood moon never repeat just because five minutes passed");
        check(rig.Notices[0].Notice.Text.Contains("20/100")&&rig.Notices[2].Notice.Text.Contains("2个空位"),"message uses current factual health and free-slot counts");

        rig=new();rig.Advance(400);rig.World=rig.World with{Life=31};rig.Step();
        check(rig.Notices.Count==0,"31 percent health is above low-health trigger");
        rig.World=rig.World with{Life=30};check(rig.Step()?.Kind==ReminderKind.LowHealth,"exactly 30 percent triggers low health");
        rig.Advance(19000);rig.World=rig.World with{Life=44};rig.Step();rig.World=rig.World with{Life=20};rig.Step();
        check(rig.Count(ReminderKind.LowHealth)==1,"small healing below 45 percent cannot rearm an existing warning");
        rig.World=rig.World with{Life=45};rig.Step();rig.World=rig.World with{Life=20};
        check(rig.Step()?.Kind==ReminderKind.LowHealth,"recovery to 45 percent rearms a later low-health episode");
        rig=new();rig.Advance(400);rig.World=rig.World with{Life=20};rig.Step();
        rig.World=rig.World with{Life=100};rig.Step();rig.World=rig.World with{Life=20};rig.Advance(1000);
        check(rig.Count(ReminderKind.LowHealth)==1,"fast full heal and repeat damage cannot bypass per-kind five-minute cooldown");
        rig.Advance(18000);
        check(rig.Count(ReminderKind.LowHealth)==2&&rig.Notices[1].Tick-rig.Notices[0].Tick>=18000,"later current low-health episode may warn once when five-minute limit permits");
        rig=new(){World=new(true,false,300,1000,50)};rig.Advance(400);
        check(rig.Count(ReminderKind.LowHealth)==1,"low health uses effective life maximum, not a fixed HP cutoff");
        rig=new(){World=new(true,false,int.MaxValue,int.MaxValue,50)};rig.Advance(400);
        check(rig.Notices.Count==0,"large modded health values do not overflow percentage arithmetic");

        rig=new();rig.Advance(400);rig.World=rig.World with{FreeSlots=4};rig.Step();
        check(rig.Notices.Count==0,"four free main-inventory slots is above warning threshold");
        rig.World=rig.World with{FreeSlots=3};check(rig.Step()?.Kind==ReminderKind.Inventory,"three free main-inventory slots triggers once");
        rig.Advance(19000);rig.World=rig.World with{FreeSlots=7};rig.Step();rig.World=rig.World with{FreeSlots=2};rig.Step();
        check(rig.Count(ReminderKind.Inventory)==1,"freeing fewer than eight slots does not rearm the bag warning");
        rig.World=rig.World with{FreeSlots=8};rig.Step();rig.World=rig.World with{FreeSlots=0};
        check(rig.Step()?.Text.Contains("没有空位")==true,"eight free slots rearms a later completely full inventory episode");
        rig=new();rig.Advance(400);rig.World=rig.World with{FreeSlots=1};rig.Step();rig.World=rig.World with{FreeSlots=50};rig.Step();rig.World=rig.World with{FreeSlots=1};rig.Advance(1000);
        check(rig.Count(ReminderKind.Inventory)==1,"quick deposit and refill cannot bypass five-minute bag cooldown");
        foreach(int invalid in new[]{-1,51})
        {rig=new(){World=new(true,false,100,100,invalid)};rig.Advance(400);check(rig.Notices.Count==0,"invalid free-slot data cannot claim full inventory");}
        foreach(int invalidMax in new[]{0,-100})
        {rig=new(){World=new(true,false,1,invalidMax,50)};rig.Advance(400);check(rig.Notices.Count==0,"invalid life maximum cannot claim low health");}

        // Native snapshot and tests use this exact generic 50-slot counter.
        var inventory=Enumerable.Repeat(1,50).Concat(new int[8]).ToArray();
        check(ReminderInventory.FreeSlots(inventory,x=>x==0)==0,"empty coin/ammo slots do not count as main-bag capacity");
        inventory[49]=0;inventory[55]=1;
        check(ReminderInventory.FreeSlots(inventory,x=>x==0)==1,"last main-bag slot counts, ammo slot does not");
        check(ReminderInventory.FreeSlots(new int[50],x=>x==0)==50,"an empty main bag has exactly 50 free slots");
        check(ReminderInventory.FreeSlots(new int[49],x=>x==0)==-1&&ReminderInventory.FreeSlots<int>(null,x=>x==0)==-1,"missing/short inventory is unknown rather than full");

        // Delayed conditions are always reevaluated, never stale queued text.
        rig=new();rig.Advance(400);rig.World=rig.World with{Day=false};rig.Step();
        rig.World=rig.World with{Life=20};rig.Step();rig.World=rig.World with{Life=35};rig.Advance(4000);
        check(rig.Count(ReminderKind.LowHealth)==0,"healing above trigger while waiting for shared cooldown prevents stale low-health notice");
        rig.World=rig.World with{Life=29};check(rig.Step()?.Kind==ReminderKind.LowHealth,"unsent episode still warns if actual life drops below trigger again");
        rig.World=rig.World with{FreeSlots=2};rig.Step();rig.World=rig.World with{FreeSlots=5};rig.Advance(4000);
        check(rig.Count(ReminderKind.Inventory)==0,"bag no longer near full cannot emit old queued slot count");
        rig.World=rig.World with{FreeSlots=1};check(rig.Step()?.Text.Contains("1个空位")==true,"late bag warning uses current remaining slots");
        rig=new(){World=new(false,true,20,100,50)};rig.Advance(400);rig.World=rig.World with{Day=true,BloodMoon=false,Life=100};rig.Advance(4000);
        check(rig.Count(ReminderKind.BloodMoon)==0,"blood moon ended before delivery does not generate a catch-up warning");
        rig=new();rig.Advance(400);rig.World=rig.World with{Day=false};rig.Step();
        rig.World=rig.World with{Day=true};rig.Step();rig.World=rig.World with{Day=false};rig.Advance(20000);
        check(rig.Count(ReminderKind.Night)==1,"dusk suppressed by same-kind cooldown expires rather than appearing minutes into the night");
        rig.World=rig.World with{Day=true};rig.Step();rig.World=rig.World with{Day=false};
        check(rig.Step()?.Kind==ReminderKind.Night,"a fresh later dusk remains eligible after expired pending night");

        foreach(var option in new[]{new ReminderOptions(LowHealth:false),new ReminderOptions(BloodMoon:false),new ReminderOptions(Inventory:false)})
        {
            rig=new(){World=new(false,true,20,100,1),Options=option};rig.Advance(8000);
            check(rig.Notices.All(n=>n.Notice.Kind switch{ReminderKind.LowHealth=>option.LowHealth,ReminderKind.BloodMoon=>option.BloodMoon,ReminderKind.Inventory=>option.Inventory,_=>option.Night}),"individual setting blocks only the corresponding reminders");
        }
        rig=new(){Options=new(Night:false)};rig.Advance(400);rig.World=rig.World with{Day=false};rig.Advance(4000);
        check(rig.Count(ReminderKind.Night)==0,"dusk toggle suppresses its transition");
        rig=new(){Options=new(Enabled:false),World=new(false,true,20,100,0)};rig.Advance(20000);
        check(rig.Notices.Count==0,"master switch silences every category");
        rig.Options=new();rig.Advance(299);check(rig.Notices.Count==0,"reenabling master switch respects fresh five-second grace");
        check(rig.Step()?.Kind==ReminderKind.LowHealth,"currently active condition can warn after reenable without replaying dusk");
        rig.Options=new(Enabled:false);rig.Advance(500);rig.Options=new();rig.Advance(400);
        check(rig.Count(ReminderKind.LowHealth)==1,"master toggle does not reset sent episode latch or spam low-health reminder");
        rig=new();rig.Advance(400);rig.Options=new(Enabled:false);rig.World=rig.World with{Day=false};rig.Advance(400);rig.Options=new();rig.Advance(1000);
        check(rig.Count(ReminderKind.Night)==0,"night beginning while disabled is not replayed later");

        rig=new(){Available=false,World=new(false,true,20,100,0)};rig.Advance(1000);
        check(rig.Notices.Count==0,"dismissed/missing companion cannot speak reminders");
        rig.Available=true;rig.Advance(300);check(rig.Count(ReminderKind.LowHealth)==1,"summoning companion allows current condition after warmup");
        rig.Available=false;rig.Advance(400);rig.Available=true;rig.Advance(300);
        check(rig.Count(ReminderKind.LowHealth)==1,"dismiss/resummon does not replay same ongoing health warning");
        rig=new(){Alive=false,World=new(false,true,0,100,0)};rig.Advance(1000);
        check(rig.Notices.Count==0,"dead player receives no proactive notices");
        rig.Alive=true;rig.World=rig.World with{Life=100};rig.Advance(300);
        check(rig.Notices.Count==1&&rig.Notices[0].Notice.Kind==ReminderKind.BloodMoon,"respawn can report a currently active event without a stale zero-HP warning");
        rig=new(){Paused=true,World=new(false,true,20,100,0)};rig.Advance(20000);
        check(rig.Notices.Count==0,"paused updates emit no notices and consume no warmup");
        rig.Paused=false;rig.Advance(300);check(rig.Count(ReminderKind.LowHealth)==1,"warmup advances only on actual active world updates");
        rig.Paused=true;rig.Advance(20000);rig.Paused=false;rig.Step();
        check(rig.Notices.Count==1,"pause does not burn through shared cooldown");
        rig.Advance(3599);check(rig.Count(ReminderKind.BloodMoon)==1,"deferred event waits a full active minute after the previous notice");
        rig=new(){World=new(true,false,20,100,50)};rig.Advance(299);
        for(int i=0;i<1000;i++)rig.Engine.Tick(new(rig.Session,rig.Player,rig.Tick,rig.World),rig.Options);
        check(rig.Notices.Count==0&&rig.Step()?.Kind==ReminderKind.LowHealth,"duplicate frame cannot accelerate warmup or send twice");
        var duplicate=rig.Engine.Tick(new(rig.Session,rig.Player,rig.Tick,rig.World),rig.Options);
        check(duplicate==null,"emitted tick is idempotent");
        check(rig.Engine.Tick(new(rig.Session,rig.Player,rig.Tick-1,rig.World),rig.Options)==null,"old snapshot cannot rewind reminders");
        rig.Session=Guid.NewGuid();rig.Advance(300);
        check(rig.Count(ReminderKind.LowHealth)==2,"new world visit resets episode and cooldown scope");
        rig.Player=1;rig.Advance(300);
        check(rig.Count(ReminderKind.LowHealth)==3,"new player cannot inherit another player's reminder state");
        rig.Session=Guid.Empty;rig.Advance(400);
        check(rig.Notices.Count==3,"missing world session cannot deliver reminders");

        // Generated state streams check safety rules against independent episode
        // tracking, including toggles and conditions changing during cooldown.
        int generatedNotices=0;
        for(int run=0;run<50;run++)
        {
            var random=new Random(211026+run);rig=new();long active=0;long? last=null;
            var kindAt=new Dictionary<ReminderKind,long>();bool lowSent=false,bagSent=false,bloodSent=false,nightSent=false,valid=true;
            for(int frame=0;frame<12000;frame++)
            {
                if(frame%150==0)rig.World=new(random.Next(2)==0,random.Next(3)==0,random.Next(1,101),100,random.Next(0,12));
                if(frame%500==0){rig.Available=random.Next(10)>0;rig.Alive=random.Next(10)>0;}
                if(frame%2000==0)rig.Options=new(random.Next(10)>0,random.Next(5)>0,random.Next(5)>0,random.Next(5)>0,random.Next(5)>0);
                rig.Paused=random.Next(20)==0;
                if(!rig.Paused)
                {
                    active++;
                    if(!rig.Alive||rig.World.Life>=45)lowSent=false;
                    if(rig.World.FreeSlots>=8)bagSent=false;
                    if(rig.World.Day||!rig.World.BloodMoon)bloodSent=false;
                    if(rig.World.Day)nightSent=false;
                }
                var notice=rig.Step();if(notice==null)continue;generatedNotices++;
                valid&=!rig.Paused&&rig.Alive&&rig.Available&&rig.Options.Enabled;
                valid&=last==null||active-last>=3600;last=active;
                valid&=!kindAt.TryGetValue(notice.Kind,out long previous)||active-previous>=18000;kindAt[notice.Kind]=active;
                switch(notice.Kind)
                {
                    case ReminderKind.LowHealth:valid&=rig.Options.LowHealth&&rig.World.Life<=30&&!lowSent;lowSent=true;break;
                    case ReminderKind.BloodMoon:valid&=rig.Options.BloodMoon&&rig.World.BloodMoon&&!rig.World.Day&&!bloodSent;bloodSent=true;break;
                    case ReminderKind.Night:valid&=rig.Options.Night&&!rig.World.Day&&!nightSent&&!(rig.World.BloodMoon&&rig.Options.BloodMoon);nightSent=true;break;
                    case ReminderKind.Inventory:valid&=rig.Options.Inventory&&rig.World.FreeSlots<=3&&!bagSent;bagSent=true;break;
                }
            }
            check(valid,"generated reminder stream respects settings, current thresholds, latches and cooldowns: "+run);
        }
        check(generatedNotices>50,"generated scenarios actually exercise emitted reminders instead of only silence");
    }
}
