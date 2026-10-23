using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.Graphics;
using ReLogic.Localization.IME;
using ReLogic.OS;
using TerrarAI.Content.Companion;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;

namespace TerrarAI.Content.UI;

internal sealed class CompanionInput : PixelSurface
{
    internal string Text = "";
    internal string Hint = "点这里输入，不用先按 Enter…";
    internal bool Focused { get; private set; }
    internal Action? Submitted;
    internal Action? Escaped;
    // Bare: the parent draws the box; only text and caret are drawn here.
    internal bool Bare;
    internal Color TextColor = new(242, 236, 215), HintColor = new(143, 161, 176);
    private int _blink;
    private bool _wasComposing;
    private readonly TextEntryKeys _keys = new();

    internal CompanionInput()
    {
        SetPadding(10); Fill = new Color(17, 26, 42);
        OnLeftMouseDown += (_, _) => SetFocus(true);
    }
    internal void SetFocus(bool value)
    {
        if (Focused == value) return;
        Focused = value;
        if (value)
        {
            Main.clrInput();
            Main.ClosePlayerChat();
            _keys.Reset(Main.keyState.IsKeyDown(Keys.Enter), Main.keyState.IsKeyDown(Keys.Escape));
            Main.blockInput = true;
            PlayerInput.WritingText = true;
        }
        else { Main.blockInput = false; PlayerInput.WritingText = false; }
    }
    public override void Update(GameTime gameTime)
    {
        if (Focused)
        {
            if ((Main.mouseLeft || Main.mouseRight) && !ContainsPoint(Main.MouseScreen)) SetFocus(false);
            else { Main.blockInput = true; PlayerInput.WritingText = true; }
        }
        Rim = Focused ? new Color(169, 207, 150) : new Color(84, 103, 125);
        base.Update(gameTime);
    }
    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        // Vanilla updates its keyboard state after UpdateUI. Read here, like its own text fields.
        if (Focused && Main.hasFocus)
        {
            Main.blockInput = true; PlayerInput.WritingText = true;
            Main.instance.HandleIME();
            var ime = Platform.Get<IImeService>();
            bool composing = ime.IsCandidateListVisible || !string.IsNullOrEmpty(ime.CompositionString);
            // Backspace while the IME is composing belongs to the pinyin, not to the committed text.
            // The composition can empty in the same frame, so the previous frame counts too.
            string before = Text;
            Text = Main.GetInputText(Text).Replace("\r", " ").Replace("\n", " ");
            if ((composing || _wasComposing) && Text.Length < before.Length && before.StartsWith(Text)) Text = before;
            _wasComposing = composing;
            if (Text.Length > CompanionProtocol.MaxCommandLength) Text = Text[..CompanionProtocol.MaxCommandLength];
            bool enter = Main.inputText.IsKeyDown(Keys.Enter);
            bool escape = Main.inputText.IsKeyDown(Keys.Escape);
            var action = _keys.Consume(enter, escape, composing);
            if (action == TextEntryAction.Blur) { SetFocus(false); Escaped?.Invoke(); }
            else if (action == TextEntryAction.Submit) Submitted?.Invoke();
        }
        if (!Bare) base.DrawSelf(spriteBatch);
        var area = GetInnerDimensions();
        string display = Text.Length == 0 ? Hint : Text;
        if (Focused && ++_blink % 80 < 40) display += "|";
        var lines = TranscriptLine.Wrap(display, area.Width - 4, 0.78f);
        var pos = area.Position();
        for (int i = Math.Max(0, lines.Length - 2); i < lines.Length; i++)
        {
            spriteBatch.DrawString(FontAssets.MouseText.Value, lines[i], pos,
                Text.Length == 0 ? HintColor : TextColor,
                0, Vector2.Zero, 0.78f, SpriteEffects.None, 0); pos.Y += 23;
        }
        if (Focused) Main.instance.DrawWindowsIMEPanel(new Vector2(area.X, area.Y + area.Height));
    }
}
