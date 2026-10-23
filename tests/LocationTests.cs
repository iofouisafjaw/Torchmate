using System.Globalization;
using System.Text.Json;
using TerrarAI.Content.Companion;
using TerrarAI.Evals;

static class LocationTests
{
    sealed class Source(params MapLocation[] locations):ILocationSource
    {
        internal readonly List<MapLocation> Points=locations.ToList();
        internal Func<MapLocation,bool> Check=_=>true;
        public IEnumerable<MapLocation> Candidates(LocationRequest request)=>Points;
        public bool Valid(MapLocation location)=>Check(location);
    }
    static MapLocation Point(string id,int x,int y,LocationKind kind=LocationKind.Ore)=>new(kind,id,id,x,y);
    internal static void Run(Action<bool,string> check)
    {
        foreach(var (text,kind,name,personal) in new[]{
            ("最近的矿在哪",LocationKind.Ore,"",false),("附近有没有矿石？",LocationKind.Ore,"",false),
            ("最近的铁矿在哪里",LocationKind.Ore,"铁矿",false),("请帮我找一下附近的铜矿石。",LocationKind.Ore,"铜矿石",false),
            ("在地图上标出最近的金矿",LocationKind.Ore,"金矿",false),("找黑曜石",LocationKind.Ore,"黑曜石",false),
            ("陨石在哪",LocationKind.Ore,"陨石",false),("钛金在哪",LocationKind.Ore,"钛金",false),
            ("附近箱子在哪",LocationKind.Chest,"",false),("附近有没有宝箱",LocationKind.Chest,"",false),
            ("帮我找最近的箱子",LocationKind.Chest,"",false),("把最近的箱子标在地图上",LocationKind.Chest,"",false),
            ("离我最近的床在哪里",LocationKind.Bed,"",false),("标记一下床",LocationKind.Bed,"",false),
            ("床铺的位置在哪",LocationKind.Bed,"",false),("我的床在哪",LocationKind.Bed,"",true),
            ("找到我设置出生点的床",LocationKind.Bed,"",true),("我的出生点在哪",LocationKind.Bed,"",true),
            ("请在地图上标记我的床",LocationKind.Bed,"",true),
            ("最近的铁矿在哪里？",LocationKind.Ore,"铁矿",false)})
        {
            bool accepted=LocationRequest.TryParse(text,out var r,n=>n=="钛金");
            check(accepted&&r==new LocationRequest(kind,name,personal),"location intent: "+text);
        }
        foreach(string text in new[]{"查附近箱子","把木材存进箱子","从箱子取5个铁锭","挖最近的铁矿","铁锭怎么做", "心形水晶在哪",
            "Boss在哪","NPC在哪","木材在哪","怎么找矿","不要找床","以后找铁矿","如果找箱子","找铁矿然后挖掉", "找床和箱子",
            "记住我的床在哪","我说“找箱子”是什么意思","","找"+new string('铁',241)})
            check(!LocationRequest.TryParse(text,out _),"location query does not capture another action or unsupported target: "+text[..Math.Min(40,text.Length)]);
        foreach(string text in new[]{"清除标记","清除地图标记","删除地图标记","取消地图标记！"})
            check(LocationRequest.TryParse(text,out var r)&&r!.Action==LocationAction.Clear,"explicit marker clear: "+text);
        foreach(string text in new[]{"查看标记","查看地图标记","我的地图标记","当前标记"})
            check(LocationRequest.TryParse(text,out var r)&&r!.Action==LocationAction.Show,"marker summary: "+text);

        var ore=new LocationRequest(LocationKind.Ore);
        var chest=new LocationRequest(LocationKind.Chest);
        var bed=new LocationRequest(LocationKind.Bed);
        var home=bed with{Personal=true};
        MapLocation? Find(Source s,LocationRequest? r=null,double x=100.5,double y=100.5)=>LocationQueries.Find(r??ore,x,y,s);
        var nearest=Point("near",103,104);
        var source=new Source(Point("far",110,100),nearest,Point("hidden",101,100) with{Known=false});
        check(Find(source)==nearest,"nearest revealed ore uses straight-line distance rather than reachability cost");
        check(source.Points.Count==3&&source.Points[2].Known==false,"location query neither mines nor reveals hidden map cells");
        check(Find(new(Point("out",221,100)))==null,"120 tile search rejects just-outside radius");
        check(Find(new(Point("edge",220,100)))?.Identity=="edge","120 tile radius includes boundary");
        check(Find(new(Point("diagonal",190,190)))==null,"radius is circular rather than a bounding square");
        check(Find(new(Point("unknown",101,100) with{Known=false}))==null,"unexplored candidate cannot disclose a position");
        check(Find(new(Point("wrong-kind",100,100,LocationKind.Chest)))==null,"candidate source cannot substitute a different target kind");
        foreach(var bad in new[]{nearest with{X=-1},nearest with{Y=-1},nearest with{Width=0},nearest with{Height=0},nearest with{Width=17},nearest with{Height=17}})
            check(Find(new(bad))==null,"invalid location geometry is rejected: "+bad);
        check(Find(new(),x:double.NaN)==null&&Find(new(),y:double.PositiveInfinity)==null,"invalid player positions produce no fabricated location");
        check(Find(new(nearest),ore with{Action=LocationAction.Clear})==null,"marker actions never search world data");
        source=new(nearest,Point("second",108,100)){Check=p=>p.Identity!="near"};
        check(Find(source)?.Identity=="second","destroyed nearest ore is skipped during selection");
        int reads=0;source=new(nearest){Check=_=>++reads==1};
        check(Find(source)==null&&reads==2,"selected target is rechecked before publishing and stale hit is not reported");
        check(Find(new(Point("right",101,100),Point("above",100,99)))?.Identity=="above","equal distances break ties by Y then X, independent of enumeration");
        check(Find(new(Point("z",103,104),Point("a",103,104)))?.Identity=="a","same position tie is deterministic");
        var wide=new MapLocation(LocationKind.Chest,"wide","wide",103,100,Width:2,Height:2);
        var single=Point("single",104,100,LocationKind.Chest);
        check(Find(new(wide,single),chest)==wide,"furniture distance uses its center, not the top-left tile");

        var nearby=Point("guest",101,100,LocationKind.Bed) with{Width=4,Height=2};
        var saved=Point("saved",1000,2000,LocationKind.Bed) with{Personal=true,Known=false,Width=4,Height=2};
        check(Find(new(nearby,saved),home)==saved,"explicit personal bed uses known saved spawn even outside nearby map search");
        check(Find(new(nearby,saved),bed)==nearby,"ordinary bed query does not disclose a remote unexplored spawn");
        check(Find(new(nearby),home)==nearby&&!Find(new(nearby),home)!.Personal,"missing saved bed can fall back to a nearby explored bed without claiming it is home");
        source=new(nearby,saved){Check=p=>p.Identity!="saved"};
        check(Find(source,home)==nearby,"destroyed personal bed falls back without returning a stale spawn coordinate");
        check(Find(new(saved with{Personal=false}),home)==null,"unknown unrelated bed is not a personal-spawn exception");

        foreach(var (dx,dy,direction) in new[]{(10,0,"右方"),(10,10,"右下方"),(0,10,"下方"),(-10,10,"左下方"),(-10,0,"左方"),(-10,-10,"左上方"),(0,-10,"上方"),(10,-10,"右上方")})
            check(LocationQueries.Bearing(100.5,100.5,Point("dir",100+dx,100+dy)).Direction==direction,"screen-space direction: "+direction);
        check(LocationQueries.Bearing(100.5,100.5,nearest).Distance==5,"3-4-5 distance is in tiles");
        check(LocationQueries.Bearing(100.5,100.5,Point("feet",100,100)).Direction=="脚边","standing on target says at feet");
        check(LocationQueries.Bearing(110.5,104.5,nearest).Direction=="左方","hover direction is recalculated from current player position");
        var culture=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");check(LocationQueries.Bearing(100,100,nearest).Text.Contains("5.7格"),"distance formatting is independent of OS decimal separator");}
        finally{CultureInfo.CurrentCulture=culture;}

        var markers=new LocationMarkers();var session=Guid.NewGuid();
        IReadOnlyList<MapLocation> Current()=>markers.Current(session,2,_=>true);
        markers.Replace(session,2,LocationKind.Ore,nearest);
        markers.Replace(session,2,LocationKind.Chest,wide);
        markers.Replace(session,2,LocationKind.Bed,nearby);
        check(Current().Count==3,"ore, chest and bed have independent session markers");
        for(int i=0;i<10;i++)markers.Replace(session,2,LocationKind.Ore,Point("new"+i,100+i,100));
        check(Current().Count==3&&Current()[0].Identity=="new9","new query replaces only its kind and marker count stays bounded");
        markers.Replace(session,2,LocationKind.Ore,null);
        check(Current().Count==2&&Current().All(p=>p.Kind!=LocationKind.Ore),"failed search clears only the old same-kind marker");
        check(markers.Current(session,2,p=>p.Identity!="wide").Single()==nearby,"removed chest is pruned while other marker remains");
        markers.Replace(session,2,LocationKind.Bed,saved);
        check(markers.Current(session,2,p=>!p.Personal).Count==0,"changed saved spawn invalidates its personal bed marker");
        markers.Replace(session,2,LocationKind.Ore,nearest);
        check(markers.Current(Guid.NewGuid(),2,_=>true).Count==0,"world visit change clears location markers");
        markers.Replace(session,2,LocationKind.Ore,nearest);
        check(markers.Current(session,3,_=>true).Count==0,"player scope change cannot inherit markers");
        markers.Replace(Guid.Empty,2,LocationKind.Ore,nearest);
        check(markers.Current(Guid.Empty,2,_=>true).Count==0,"missing world session cannot store markers");
        markers.Replace(session,2,LocationKind.Ore,wide);
        check(Current().Count==0,"wrong-kind marker cannot replace another type");
        markers.Replace(session,2,LocationKind.Ore,nearest);markers.Clear();
        check(Current().Count==0,"explicit clear removes all markers");

        // Native beds have 4x2 tiles, 16px cells and 2px padding; adjacent
        // horizontal frame groups represent opposite orientations and styles.
        foreach(int baseX in new[]{0,72,144,216})foreach(int baseY in new[]{0,36,72})
            for(int row=0;row<2;row++)for(int col=0;col<4;col++)
            {
                var root=FurnitureFrames.Decode(20+col,30+row,baseX+18*col,baseY+18*row,4,2,16,2,new[]{16,16},72,36);
                check(root==new FurnitureLocation(20,30,baseX,baseY,col,row),$"bed root from each tile/orientation/style: {baseX},{baseY}/{col},{row}");
            }
        var uneven=FurnitureFrames.Decode(21,32,18,50,2,3,16,2,new[]{16,30,10},36,64);
        check(uneven==new FurnitureLocation(20,30,0,0,1,2),"mod furniture with uneven row heights decodes correct root");
        foreach(var (fx,fy) in new[]{(1,0),(0,1),(71,0),(0,35),(-1,0),(0,-1)})
            check(FurnitureFrames.Decode(20,30,fx,fy,4,2,16,2,new[]{16,16},72,36)==null,"misaligned or negative furniture frames rejected");
        check(FurnitureFrames.Decode(0,0,54,18,4,2,16,2,new[]{16,16},72,36)==null,"frame decoding cannot produce negative world coordinates");
        check(FurnitureFrames.Decode(20,30,0,0,0,2,16,2,new[]{16,16},72,36)==null&&
            FurnitureFrames.Decode(20,30,0,0,4,2,16,2,new[]{16},72,36)==null&&
            FurnitureFrames.Decode(20,30,0,0,4,2,16,2,new[]{16,16},0,36)==null,"malformed furniture metadata rejected before indexing");

        // Independent brute-force reference with shuffled discovery and mixed
        // kinds, fog, invalidated entities, object sizes and circular bounds.
        var random=new Random(181026);
        for(int run=0;run<100;run++)
        {
            var points=Enumerable.Range(0,90).Select(i=>Point("p"+i,random.Next(1,400),random.Next(1,400),(LocationKind)random.Next(3)) with
                {Known=random.Next(4)!=0,Width=random.Next(1,5),Height=random.Next(1,3)}).OrderBy(_=>random.Next()).ToArray();
            source=new(points){Check=p=>int.Parse(p.Identity[1..])%7!=0};
            var request=new LocationRequest((LocationKind)random.Next(3));
            double px=random.Next(50,350)+0.25,py=random.Next(50,350)+0.75;
            MapLocation? reference=null;double shortest=double.PositiveInfinity;
            foreach(var p in points.OrderBy(p=>p.Y).ThenBy(p=>p.X).ThenBy(p=>p.Identity,StringComparer.Ordinal))
            {
                if(p.Kind!=request.Kind||!p.Known||int.Parse(p.Identity[1..])%7==0)continue;
                double dx=p.X+p.Width/2.0-px,dy=p.Y+p.Height/2.0-py,d=Math.Sqrt(dx*dx+dy*dy);
                if(d<=120&&d<shortest){reference=p;shortest=d;}
            }
            check(LocationQueries.Find(request,px,py,source)==reference,"random mixed-map nearest candidate agrees with independent reference: "+run);
        }
        foreach(string item in new[]{"铁矿","箱子","我的床"})
        {
            var call=AgentProtocol.Parse(JsonSerializer.Serialize(new{tool="locate",item}));
            check(call.Ends&&call.Item==item&&AgentProtocol.IsLookup(call.Tool),"location tool is a read-only program handoff: "+item);
        }
        foreach(string item in new[]{"",new string('矿',41)})
        {bool rejected=false;try{AgentProtocol.Parse(JsonSerializer.Serialize(new{tool="locate",item}));}catch(FormatException){rejected=true;}check(rejected,"missing/oversized location argument rejected");}
        check(AgentProtocol.Schema.GetProperty("properties").GetProperty("tool").GetProperty("enum").EnumerateArray().Any(x=>x.GetString()=="locate"),"model schema exposes native location handoff");
        var spec=new WorldSpec{Bag=new(){{"木材",10}},Chests=new(){{"铁锭",4}}};
        var world=new FakeWorld(spec);var loop=new AgentLoop("附近的铁矿在哪");
        string result=world.Run(new("locate","铁矿"),loop);
        check(result.Contains("没有真实地图")&&result.Contains("未创建标记"),"offline tools do not invent native coordinates or rendered markers");
        check(spec.Bag["木材"]==10&&spec.Chests["铁锭"]==4,"offline location query does not transfer inventory");
    }
}
