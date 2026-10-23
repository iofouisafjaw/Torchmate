using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;

namespace TerrarAI.Content.UI;

// Original code-drawn art: integer rectangles keep the frame and mascot crisp.
internal class PixelSurface : UIElement
{
    internal Color Fill = Ink.Window;
    internal Color Rim = Ink.Edge;
    internal PixelSurface() => SetPadding(10);
    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        var r = GetDimensions().ToRectangle();
        DrawFrame(spriteBatch, r, Fill, Rim);
    }
    internal static void DrawFrame(SpriteBatch sb, Rectangle r, Color fill, Color rim)
    {
        Ink.Card(sb, r, fill, rim, 3);
    }
}

internal sealed class PanelButton : PixelSurface
{
    private readonly UIText _label;
    internal Func<bool> Enabled = () => true;
    internal Func<bool> Selected = () => false;
    internal Func<string>? Caption;
    internal PanelButton(string text, Action action)
    {
        SetPadding(0);
        _label = new UIText(text, 0.75f) { HAlign = 0.5f, VAlign = 0.5f, IgnoresMouseInteraction = true };
        Append(_label);
        OnLeftClick += (_, _) => { if (Enabled()) action(); };
    }
    public override void Update(GameTime gameTime)
    {
        if (Caption != null) _label.SetText(Caption());
        bool enabled = Enabled();
        Fill = !enabled ? Ink.Window * 0.65f : Selected() ? Ink.Accent : IsMouseHovering ? Ink.Hover : Ink.Raised;
        Rim = Ink.Edge;
        _label.TextColor = !enabled ? Ink.Faint : Selected() ? Ink.Gold : Ink.Text;
        base.Update(gameTime);
    }
}

internal sealed class SlimePortrait : UIElement
{
    internal SlimePortrait() { Width.Set(38, 0); Height.Set(36, 0); IgnoresMouseInteraction = true; }
    protected override void DrawSelf(SpriteBatch sb)
    {
        var p = GetDimensions().Position();
        const int s = 2;
        string[] pixels = {
            "       ll         ", "      lll p       ", "       l ppp      ", "        l p       ",
            "     ooooooo      ", "   ooaaaaaaaaoo   ", "  oaaahhaaaaaaao  ", " oaaahhaaaaaaaaao ",
            " oaaaaakaaakaaaao ", " oaaaaakaaakaaaao ", " oaaaabbaabbaaaao ", "  oaaaakakaaaaoo  ",
            "   ooooooooooo    " };
        for (int y = 0; y < pixels.Length; y++)
            for (int x = 0; x < pixels[y].Length; x++)
            {
                Color c = pixels[y][x] switch {
                    'o' => new Color(43, 83, 79), 'a' => new Color(132, 203, 153),
                    'h' => new Color(196, 237, 190), 'k' => new Color(30, 53, 67),
                    'b' => new Color(235, 151, 148), 'l' => new Color(120, 173, 94),
                    'p' => new Color(241, 179, 189), _ => Color.Transparent };
                if (c != Color.Transparent) sb.Draw(TextureAssets.MagicPixel.Value,
                    new Rectangle((int)p.X + x * s, (int)p.Y + 4 + y * s, s, s), c);
            }
    }
}

internal sealed class TranscriptLine : PixelSurface
{
    private readonly string[] _lines;
    private readonly Color _color;
    private readonly string _heading;
    internal TranscriptLine(string text, Color color, float width, string heading = "")
    {
        _heading = heading;
        _lines = Wrap(text, Math.Max(40, width - 24), 0.78f);
        _color = color;
        Fill = new Color(30, 42, 63); Rim = new Color(71, 91, 111);
        Width.Set(0, 1);
        Height.Set(_lines.Length * 23 + 22 + (heading.Length > 0 ? 27 : 0), 0);
        IgnoresMouseInteraction = true;
    }
    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        base.DrawSelf(spriteBatch);
        var pos = GetDimensions().Position() + new Vector2(12, 10);
        if (_heading.Length > 0)
        {
            spriteBatch.DrawString(FontAssets.MouseText.Value, _heading, pos, new Color(235, 202, 145), 0,
                Vector2.Zero, 0.8f, SpriteEffects.None, 0); pos.Y += 27;
        }
        foreach (var line in _lines)
        {
            spriteBatch.DrawString(FontAssets.MouseText.Value, line, pos, _color, 0, Vector2.Zero, 0.78f, SpriteEffects.None, 0);
            pos.Y += 23;
        }
    }
    internal static string[] Wrap(string text, float width, float scale)
    {
        var lines = new List<string>(); var current = new StringBuilder();
        foreach (char c in text.Replace("\r", ""))
        {
            if (c == '\n') { lines.Add(current.ToString()); current.Clear(); continue; }
            if (current.Length > 0 && FontAssets.MouseText.Value.MeasureString(current.ToString() + c).X * scale > width)
            { lines.Add(current.ToString()); current.Clear(); }
            current.Append(c);
        }
        lines.Add(current.ToString()); return lines.ToArray();
    }
}
