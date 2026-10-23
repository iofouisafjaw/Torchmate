using System;
using System.Text.RegularExpressions;

namespace TerrarAI.Content.Companion;

// Terraria chat tags ([i/s50:SilverCoin], [c/FF0000:text] ...) read as noise to the model and the player.
public static class ChatTags
{
    private static readonly Regex Item = new(@"\[i(?:/s(\d+))?(?:/p\d+)?:([^\]]+)\]");
    private static readonly Regex Colored = new(@"\[c/[0-9A-Fa-f]{6}:([^\]]*)\]");
    private static readonly Regex Other = new(@"\[[a-z](?:/[^:\]]*)?:([^\]]*)\]");

    // itemName turns an id or internal name ("SilverCoin", "Fargowiltas/OverloadWorm", "73") into a display name.
    public static string Strip(string text, Func<string, string> itemName)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('[') < 0) return text;
        text = Item.Replace(text, m =>
        {
            string name = itemName(m.Groups[2].Value);
            if (string.IsNullOrEmpty(name)) name = m.Groups[2].Value;
            return m.Groups[1].Success ? $"{m.Groups[1].Value} {name}" : name;
        });
        text = Colored.Replace(text, "$1");
        return Other.Replace(text, "$1");
    }
}
