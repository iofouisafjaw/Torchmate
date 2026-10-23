using System;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

public static class CombatRequest
{
    public static bool TryParse(string input,out string mode)
    {
        mode="";if(input.Length>240)return false;
        string text=input.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。','！','!','.');
        if(Regex.IsMatch(text,"[？?]|怎么|如何|能不能|能否|会不会|可以.*吗|吗$|以后|记住|每次|如果|假如|或者|然后|并且|而且"))return false;
        text=Regex.Replace(text,@"^(?:(?:请|麻烦你|麻烦|帮我)\s*)+","");
        mode=text switch{
            "保护我" or "护卫我" or "跟着我保护我" or "开启保护" or "守护我" or "protect"=>"protect",
            "清理附近小怪" or "清理附近的小怪" or "清掉附近小怪" or "清掉附近的小怪" or "把周围的小怪清了" or "把附近的小怪清了" or "清理周围小怪" or "清理周围的小怪" or "清怪" or "clear mobs"=>"clear",
            "停止攻击" or "停止战斗" or "取消保护" or "别打了" or "不要攻击" or "不要打怪" or "不要保护我" or "别保护我" or "停止保护" or "combat off"=>"off",_=>""};
        return mode.Length>0;
    }
}
