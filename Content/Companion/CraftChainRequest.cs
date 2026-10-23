using System;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public sealed record CraftChainRequest(string Item,int Count=1,bool IronSet=false)
{
    public static bool TryParse(string text,out CraftChainRequest? request,out string error)
    {
        request=null;error="";text=text.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。','！','!');
        if(text.Length>240||Regex.IsMatch(text,"以后|记住|不要|别做|取消|怎么|如何|多少|材料|[？?]|吗$"))return false;
        if(Regex.IsMatch(text,@"^(?:请|帮我)?(?:做|合成|制作|打造)(?:一|1)?套铁(?:盔甲|护甲|套装)$"))
        {request=new("铁盔甲",IronSet:true);return true;}
        var match=Regex.Match(text,@"^(?:请|帮我)?(?:规划|计划)(?:并)?(?:制作|合成|打造|做)?(?:完整)?(?:合成链)?\s*(?<item>.+)$");
        if(!match.Success)match=Regex.Match(text,@"^(?:请|帮我)?(?:完整合成|全流程合成)\s*(?<item>.+)$");
        if(!match.Success)match=Regex.Match(text,@"^(?:请|帮我)?(?:做|制作|合成|打造)(?<item>.+)$");
        if(!match.Success)return false;
        string body=match.Groups["item"].Value.Trim();
        if(Regex.IsMatch(body,"放在|放旁边|放到|摆在|摆到"))return false;
        var quantity=Regex.Match(body,@"^(?<count>[0-9零〇一二两三四五六七八九十百千.\-]+)\s*(?:个|把|件|块|瓶|张)\s*(?<item>.+)$");
        int count=1;string item=body;
        if(quantity.Success)
        {
            if(!PlatformRequest.Read(quantity.Groups["count"].Value,out count)||count>999)
            {error="每种合成目标需为1–999个整数。";return true;}
            item=quantity.Groups["item"].Value.Trim();
        }
        if(!quantity.Success&&item.StartsWith("个"))item=item[1..].Trim();
        if(item is "一套铁盔甲" or "铁盔甲" or "铁套装")
        {if(count!=1)error="套装目前一次支持一套铁盔甲。";else request=new("铁盔甲",IronSet:true);}
        else if(item.Length is <1 or >40||Regex.IsMatch(item,"然后|再做|并且|一套|所有"))error="请指定一种物品全名，例如：规划合成1个熔炉；套装目前支持一套铁盔甲。";
        else request=new(item,count);
        return true;
    }
}
