using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.UI;
using TerrarAI.Content.NPCs;
using TerrarAI.Content.Systems;
using TerrarAI.Content.Companion;

namespace TerrarAI.Content.UI;

// A plain chat window: one input, messages in a centred column, a strip for the running task.
internal static class Ink
{
    internal static readonly Color Shade = new(0, 0, 0, 120);
    internal static readonly Color Window = new(48, 64, 124, 245);
    internal static readonly Color Edge = new(114, 137, 190);
    internal static readonly Color Raised = new(61, 80, 142);
    internal static readonly Color Hover = new(81, 105, 174);
    internal static readonly Color Text = new(245, 245, 235);
    internal static readonly Color Muted = new(197, 208, 232);
    internal static readonly Color Faint = new(151, 167, 202);
    internal static readonly Color Accent = new(82, 105, 168);
    internal static readonly Color AccentHover = new(105, 130, 193);
    internal static readonly Color Warn = new(255, 195, 125);
    internal static readonly Color Calm = new(164, 224, 149);
    internal static readonly Color Gold = new(255, 226, 141);
    internal const float Body = 0.82f, Small = 0.7f;
    internal const int BodyLine = 25, SmallLine = 20;

    internal static void Box(SpriteBatch sb, int x, int y, int w, int h, Color c)
    { if (w > 0 && h > 0) sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle(x, y, w, h), c); }

    internal static void Round(SpriteBatch sb, Rectangle r, Color c, int radius)
    {
        radius = Math.Min(3, Math.Max(0, radius));
        Box(sb, r.X + radius, r.Y, r.Width - radius * 2, r.Height, c);
        Box(sb, r.X, r.Y + radius, r.Width, r.Height - 2 * radius, c);
    }

    internal static void Card(SpriteBatch sb, Rectangle r, Color fill, Color edge, int radius)
    {
        Utils.DrawInvBG(sb, r, fill);
        if (edge == Gold)
        {
            Box(sb, r.X + 8, r.Y + 2, r.Width - 16, 1, Gold);
            Box(sb, r.X + 8, r.Bottom - 3, r.Width - 16, 1, Gold);
        }
    }

    internal static float Width(string s, float scale) => FontAssets.MouseText.Value.MeasureString(s).X * scale;

    internal static void Write(SpriteBatch sb, string s, Vector2 p, Color c, float scale) =>
        sb.DrawString(FontAssets.MouseText.Value, s, new Vector2((int)p.X, (int)p.Y), c, 0, Vector2.Zero, scale,
            SpriteEffects.None, 0);

    internal static string Fit(string s, float width, float scale)
    {
        if (Width(s, scale) <= width) return s;
        while (s.Length > 0 && Width(s + "…", scale) > width) s = s[..^1];
        return s + "…";
    }

    // Text drawn at `scale` inside a box of `height`, vertically centred (the font carries extra top space).
    internal static float Middle(float top, float height, float scale) => top + (height - 26 * scale) / 2 + 2 * scale;
}

internal sealed class FlatButton : UIElement
{
    internal enum Look { Ghost, Outline, Primary }
    private readonly string _label;
    private readonly Look _look;
    private readonly float _scale;
    internal Func<bool> Enabled = () => true;

    internal FlatButton(string label, Action action, Look look = Look.Ghost, float scale = Ink.Small)
    {
        _label = label; _look = look; _scale = scale;
        OnLeftClick += (_, _) => { if (Enabled()) action(); };
    }

    internal float Natural => Ink.Width(_label, _scale) + 26;

    protected override void DrawSelf(SpriteBatch sb)
    {
        var r = GetDimensions().ToRectangle();
        bool on = Enabled(), hover = on && IsMouseHovering;
        if (hover) Main.LocalPlayer.mouseInterface = true;
        switch (_look)
        {
            case Look.Primary: Ink.Card(sb, r, !on ? Ink.Window * 0.65f : hover ? Ink.AccentHover : Ink.Accent, Ink.Edge, 3); break;
            case Look.Outline: Ink.Card(sb, r, hover ? Ink.Hover : Ink.Window, Ink.Edge, 8); break;
            default: if (hover) Ink.Round(sb, r, Ink.Hover, 8); break;
        }
        Color text = !on ? Ink.Faint : _look == Look.Primary ? Ink.Gold : hover ? Ink.Text : Ink.Muted;
        float w = Ink.Width(_label, _scale);
        Ink.Write(sb, _label, new Vector2(r.X + (r.Width - w) / 2, Ink.Middle(r.Y, r.Height, _scale)), text, _scale);
    }
}

// One switch for items, saved buildings and explicit time/weather/boss/world-spawn operations.
internal sealed class CheatSwitch : UIElement
{
    internal CheatSwitch() => OnLeftClick += (_, _) => CheatTools.Toggle();

    protected override void DrawSelf(SpriteBatch sb)
    {
        var r = GetDimensions().ToRectangle();
        bool on = CheatTools.Enabled;
        if (IsMouseHovering) Main.LocalPlayer.mouseInterface = true;
        Ink.Card(sb, r, IsMouseHovering ? Ink.Hover : Ink.Window, Ink.Edge, 3);
        string label = on ? "作弊：开" : "作弊：关";
        Ink.Write(sb, label, new Vector2(r.X + (r.Width - Ink.Width(label, Ink.Small)) / 2,
            Ink.Middle(r.Y, r.Height, Ink.Small)), on ? Ink.Warn : Ink.Muted, Ink.Small);
    }
}

internal sealed class SendButton : UIElement
{
    internal Func<bool> Enabled = () => true;

    internal SendButton(Action action)
    {
        Width.Set(54, 0); Height.Set(32, 0);
        OnLeftClick += (_, _) => { if (Enabled()) action(); };
    }

    protected override void DrawSelf(SpriteBatch sb)
    {
        var r = GetDimensions().ToRectangle();
        bool on = Enabled();
        Ink.Card(sb, r, !on ? Ink.Window : IsMouseHovering ? Ink.Hover : Ink.Accent, Ink.Edge, 3);
        Ink.Write(sb, "发送", new Vector2(r.X + (r.Width - Ink.Width("发送", Ink.Small)) / 2,
            Ink.Middle(r.Y, r.Height, Ink.Small)), on ? Ink.Gold : Ink.Faint, Ink.Small);
    }
}

internal sealed class SlimScrollbar : UIScrollbar
{
    protected override void DrawSelf(SpriteBatch sb)
    {
        if (MaxViewSize <= ViewSize + 1) return;
        var d = GetDimensions();
        float h = Math.Max(28, d.Height * ViewSize / MaxViewSize);
        float y = d.Y + (d.Height - h) * (ViewPosition / Math.Max(1, MaxViewSize - ViewSize));
        Ink.Round(sb, new Rectangle((int)d.X + 3, (int)y, 5, (int)h), IsMouseHovering ? Ink.Muted : Ink.Edge, 2);
    }
}

internal enum Speaker { You, Qwen, Note, Problem }

internal sealed class MessageItem : UIElement
{
    private readonly Speaker _who;
    private readonly List<(string Text, bool Small)> _lines = new();
    private readonly float _column, _bubble;

    internal MessageItem(Speaker who, string text, float listWidth)
    {
        _who = who; IgnoresMouseInteraction = true; Width.Set(0, 1);
        _column = Column(listWidth);
        float wrap = who == Speaker.You ? _column * 0.78f - 32 : _column;
        foreach (string para in text.Replace("\r", "").Trim().Split('\n'))
        {
            bool small = who is Speaker.Note or Speaker.Problem || para.StartsWith("参考：");
            foreach (string line in TranscriptLine.Wrap(para, wrap, small ? Ink.Small : Ink.Body))
                _lines.Add((line, small));
        }
        float h = _lines.Sum(l => l.Small ? Ink.SmallLine : Ink.BodyLine);
        if (who == Speaker.You)
        {
            _bubble = Math.Min(_column * 0.78f, _lines.Max(l => Ink.Width(l.Text, Ink.Body)) + 32);
            h += 20;
        }
        Height.Set(h + 4, 0);
    }

    internal static float Column(float listWidth) => Math.Max(120, Math.Min(680, listWidth - 48));

    protected override void DrawSelf(SpriteBatch sb)
    {
        var d = GetDimensions();
        float x = d.X + (d.Width - _column) / 2, y = d.Y + 2;
        if (_who == Speaker.You)
        {
            var r = new Rectangle((int)(x + _column - _bubble), (int)y, (int)_bubble, (int)(d.Height - 4));
            Ink.Round(sb, r, Ink.Raised, 12);
            x = r.X + 16; y += 9;
        }
        foreach (var (text, small) in _lines)
        {
            Color c = _who switch {
                Speaker.Problem => Ink.Warn, Speaker.Note => Ink.Muted, _ => small ? Ink.Faint : Ink.Text };
            Ink.Write(sb, text, new Vector2(x, y), c, small ? Ink.Small : Ink.Body);
            y += small ? Ink.SmallLine : Ink.BodyLine;
        }
    }

    // Lines come from many places with their own prefixes; sort them into who said it.
    internal static (Speaker Who, string Text) Classify(CompanionUISystem.Line line)
    {
        string t = line.Text;
        bool problem = line.Color == Color.Orange || line.Color == Color.OrangeRed;
        foreach (string p in new[] { "你 · 提问：", "你：" })
            if (t.StartsWith(p)) return (Speaker.You, t[p.Length..]);
        foreach (string p in new[] { "Qwen · 问答：", "Qwen：", "[Qwen] " })
            if (t.StartsWith(p)) return (problem ? Speaker.Problem : Speaker.Qwen, t[p.Length..]);
        if (line.Color == Color.LightGoldenrodYellow) return (Speaker.Qwen, t);
        return (problem ? Speaker.Problem : Speaker.Note, t);
    }

    // Placeholders replaced by the typing indicator.
    internal static bool Hidden(CompanionUISystem.Line line) =>
        line.Text is "Qwen 正在听你说……" or "Qwen：我看看你的背包和进度……";
}

internal sealed class ThinkingItem : UIElement
{
    private readonly float _column;
    private readonly string _label;

    internal ThinkingItem(float listWidth, string label)
    {
        _column = MessageItem.Column(listWidth); _label = label;
        Width.Set(0, 1); Height.Set(30, 0); IgnoresMouseInteraction = true;
    }

    protected override void DrawSelf(SpriteBatch sb)
    {
        var d = GetDimensions();
        int x = (int)(d.X + (d.Width - _column) / 2), y = (int)d.Y + 11;
        double t = Environment.TickCount64 / 170.0;
        for (int i = 0; i < 3; i++)
        {
            float a = 0.3f + 0.7f * (0.5f + 0.5f * (float)Math.Sin(t - i * 0.9));
            Ink.Round(sb, new Rectangle(x + i * 12, y, 7, 7), Ink.Accent * a, 3);
        }
        Ink.Write(sb, _label, new Vector2(x + 46, Ink.Middle(y - 6, 20, Ink.Small)), Ink.Muted, Ink.Small);
    }
}

internal sealed class Welcome : UIElement
{
    private const string Greeting = "今天想做点什么？";
    internal static readonly (string Label, string Say)[] Suggestions = {
        ("下一步做什么？", "按我当前世界的进度和背包，下一步做什么？"),
        ("下一个 Boss 打谁？", "下一个Boss打谁，要怎么准备？"),
        ("搭平台", "搭平台"),
        ("盖一间 NPC 房", "帮我盖一间NPC房子"),
    };

    internal Welcome(float height, Action<string, string> pick)
    {
        Width.Set(0, 1); Height.Set(height, 0);
        var slime = new SlimePortrait { HAlign = 0.5f };
        slime.Top.Set(height * 0.5f - 108, 0); Append(slime);
        for (int i = 0; i < Suggestions.Length; i++)
        {
            var (label, say) = Suggestions[i];
            var chip = new FlatButton(label, () => pick(label, say), FlatButton.Look.Outline, 0.74f);
            chip.Width.Set(184, 0); chip.Height.Set(38, 0);
            chip.Left.Set(i % 2 == 0 ? -192 : 8, 0.5f);
            chip.Top.Set(height * 0.5f + (i / 2) * 46, 0);
            chip.Enabled = () => !CompanionUISystem.IsBusy;
            Append(chip);
        }
    }

    protected override void DrawSelf(SpriteBatch sb)
    {
        var d = GetDimensions();
        const float scale = 1.05f;
        float w = Ink.Width(Greeting, scale);
        Ink.Write(sb, Greeting, new Vector2(d.X + (d.Width - w) / 2, d.Y + d.Height * 0.5f - 62), Ink.Text, scale);
    }
}

internal sealed class TaskStrip : UIElement
{
    private readonly FlatButton _yes, _no, _stop;
    private bool _asking;
    private Guid _shownChain;
    private Guid _shownExpedition;
    private Guid _shownArena;
    private Guid _shownGarden;

    internal TaskStrip()
    {
        Height.Set(42, 0);
        _yes = Add("同意", "confirm", FlatButton.Look.Primary);
        _no = Add("不用了", "reject", FlatButton.Look.Ghost);
        _stop = Add("停止", "stop", FlatButton.Look.Outline);
        Arrange(false);
    }

    private FlatButton Add(string label, string command, FlatButton.Look look)
    {
        var b = new FlatButton(label, () => {
            if (command == "confirm" && (CompanionGardenSystem.Active || _shownGarden!=Guid.Empty))
                CompanionUISystem.Message(CompanionGardenSystem.Confirm(Main.LocalPlayer,_shownGarden),Ink.Text);
            else if (command == "confirm" && (StructureWorkshop.ArenaAsking || _shownArena != Guid.Empty))
                CompanionUISystem.Message(StructureWorkshop.Confirm(Main.LocalPlayer, _shownArena), Ink.Text);
            else if (command == "confirm" && (CompanionExpeditionSystem.Asking || _shownExpedition != Guid.Empty))
                CompanionUISystem.Message(CompanionExpeditionSystem.Confirm(Main.LocalPlayer, _shownExpedition), Ink.Text);
            else if (command == "confirm" && (CraftChainSystem.Active || _shownChain != Guid.Empty))
                CompanionUISystem.Message(CraftChainSystem.Confirm(Main.LocalPlayer, _shownChain), Ink.Text);
            else CompanionUISystem.Send(command);
        }, look);
        b.Top.Set(7, 0); b.Height.Set(28, 0); b.Width.Set(b.Natural, 0);
        return b;
    }

    private static bool Asking => ChatCoordinator.FindCompanion()?.HasPendingApproval == true;

    internal static bool Wanted
    {
        get
        {
            var v = CompanionTaskTracker.Current;
            return Asking || CompanionCollaborationSystem.Current is { Complete: false } || (v != null && !v.Closed) || StructureWorkshop.Active || CompanionConstructionSystem.Active;
        }
    }

    private void Arrange(bool asking)
    {
        _asking = asking;
        RemoveAllChildren();
        float right = -8;
        foreach (var b in asking ? new[] { _yes, _no } : new[] { _stop })
        {
            right -= b.Width.Pixels; b.Left.Set(right, 1); right -= 6;
            Append(b);
        }
        Recalculate();
    }

    public override void Update(GameTime gameTime)
    {
        if (Asking != _asking) Arrange(Asking);
        base.Update(gameTime);
    }

    protected override void DrawSelf(SpriteBatch sb)
    {
        _shownGarden=CompanionGardenSystem.ConfirmationId;
        _shownChain = CraftChainSystem.ConfirmationId;
        _shownExpedition = CompanionExpeditionSystem.ConfirmationId;
        _shownArena = StructureWorkshop.ArenaMode && StructureWorkshop.Active ? StructureWorkshop.ArenaConfirmationId : Guid.Empty;
        var r = GetDimensions().ToRectangle();
        Ink.Card(sb, r, Ink.Raised, Ink.Edge, 10);
        Ink.Round(sb, new Rectangle(r.X + 14, r.Y + r.Height / 2 - 4, 8, 8), _asking ? Ink.Accent : Ink.Calm, 4);
        var v = CompanionTaskTracker.Current;
        string text = CompanionDisplay.StateText() + (v != null && !v.Closed && v.Goal.Length > 0 ? " · " + v.Goal : "");
        if (CompanionCollaborationSystem.Current is { Complete: false } collaboration && collaboration.Next is {} work)
            text = $"协作{(collaboration.Paused ? "已暂停" : "进行中")} · {work.Name} · {(work.Owner == CollaborationOwner.Player ? "你负责" : "同伴负责")}";
        float buttons = _asking ? _yes.Width.Pixels + _no.Width.Pixels + 26 : _stop.Width.Pixels + 20;
        Ink.Write(sb, Ink.Fit(text, r.Width - 34 - buttons, Ink.Small),
            new Vector2(r.X + 30, Ink.Middle(r.Y, r.Height, Ink.Small)), Ink.Text, Ink.Small);
    }
}

// Shown on screen only while something is going on and the window is closed.
internal sealed class TaskPill : UIElement
{
    private string _text = "";

    internal TaskPill(Action open)
    {
        HAlign = 0.5f; Top.Set(14, 0); Height.Set(32, 0);
        OnLeftClick += (_, _) => open();
    }

    internal static bool Wanted => !CompanionConstructionSystem.Visible && !StructureWorkshop.Visible && (TaskStrip.Wanted || CompanionDialogueSystem.IsBusy || CompanionInquirySystem.IsBusy || CompanionAgent.Running);

    internal void Refresh()
    {
        var v = CompanionTaskTracker.Current;
        string text = "同伴 · " + CompanionDisplay.StateText();
        string key = ChatWindow.KeyName();
        if (key.Length > 0) text += "　" + key + " 打开";
        text = Ink.Fit(text, 520, Ink.Small);
        if (text == _text) return;
        _text = text; Width.Set(Ink.Width(text, Ink.Small) + 40, 0); Recalculate();
    }

    protected override void DrawSelf(SpriteBatch sb)
    {
        var r = GetDimensions().ToRectangle();
        if (IsMouseHovering) Main.LocalPlayer.mouseInterface = true;
        Ink.Card(sb, r, IsMouseHovering ? Ink.Hover : Ink.Window * 0.94f, Ink.Edge, 16);
        Ink.Round(sb, new Rectangle(r.X + 14, r.Y + r.Height / 2 - 4, 8, 8),
            ChatCoordinator.FindCompanion()?.HasPendingApproval == true ? Ink.Accent : Ink.Calm, 4);
        Ink.Write(sb, _text, new Vector2(r.X + 30, Ink.Middle(r.Y, r.Height, Ink.Small)), Ink.Text, Ink.Small);
    }
}

internal sealed class ChatWindow : UIState
{
    private const float HeaderHeight = 46, FooterHeight = 26, ShellHeight = 54;
    private Frame _frame = null!;
    private UIList _list = null!;
    private SlimScrollbar _bar = null!;
    private Shell _shell = null!;
    private CompanionInput _input = null!;
    private SendButton _send = null!;
    private TaskStrip _strip = null!;
    private TaskPill _pill = null!;
    private FlatButton _close = null!;
    private FlatButton _workshop = null!;
    private CheatSwitch _cheat = null!;
    private bool _open, _placed, _dragging, _busy, _stripShown, _escHeld, _keyHeld, _closeRequested, _sendRequested;
    private int _revision = -1;
    private int _swallowFrames;   // the hotkey that opened the window must not land in the input as a ","
    private float _listWidth = 600, _listHeight = 400;
    private Vector2 _screen, _dragOffset;
    internal bool IsOpen => _open;

    public override void OnInitialize()
    {
        _frame = new Frame(this);
        _frame.OnLeftMouseDown += (evt, _) => {
            if (evt.Target == _frame && evt.MousePosition.Y < _frame.GetDimensions().Y + HeaderHeight)
            { _dragging = true; _dragOffset = evt.MousePosition - _frame.GetDimensions().Position(); }
        };
        _close = new FlatButton("×", Close, FlatButton.Look.Ghost, 0.95f);
        _close.Width.Set(32, 0); _close.Height.Set(30, 0); _close.Top.Set(8, 0); _frame.Append(_close);
        _workshop = new FlatButton("搭平台", CompanionUISystem.StartConstruction, FlatButton.Look.Outline);
        _workshop.Width.Set(72, 0); _workshop.Height.Set(28, 0); _workshop.Top.Set(9, 0); _frame.Append(_workshop);
        _cheat = new CheatSwitch();
        _cheat.Width.Set(76, 0); _cheat.Height.Set(28, 0); _cheat.Top.Set(9, 0); _frame.Append(_cheat);

        _bar = new SlimScrollbar(); _bar.Width.Set(11, 0); _frame.Append(_bar);
        _list = new UIList { ListPadding = 16 };
        _list.ManualSortMethod = _ => { };
        _list.SetScrollbar(_bar); _frame.Append(_list);

        _strip = new TaskStrip();
        _shell = new Shell();
        _shell.OnLeftMouseDown += (_, _) => _input.SetFocus(true);
        // Enter and Esc arrive while drawing; act on them in the next update, where windows may close.
        _input = new CompanionInput { Submitted = () => _sendRequested = true, Escaped = () => _closeRequested = true, Bare = true, Hint = "给 Qwen 发消息…",
            TextColor = Ink.Text, HintColor = Ink.Faint };
        _input.SetPadding(0);
        _input.Left.Set(16, 0); _input.Top.Set(12, 0); _input.Width.Set(-90, 1); _input.Height.Set(-18, 1);
        _shell.Input = _input; _shell.Append(_input);
        _send = new SendButton(Send);
        _send.Left.Set(-66, 1); _send.Top.Set(11, 0);
        _send.Enabled = () => !CompanionUISystem.IsBusy && !string.IsNullOrWhiteSpace(_input.Text);
        _shell.Append(_send);
        _frame.Append(_shell);

        _pill = new TaskPill(Open);
    }

    internal static string KeyName()
    {
        var keys = CompanionUISystem.ToggleKey?.GetAssignedKeys();
        if (keys == null || keys.Count == 0) return "";
        return keys[0] switch {
            "OemTilde" => "~", "OemQuestion" => "/", "OemPipe" => "\\", "OemSemicolon" => ";", "OemQuotes" => "'",
            "OemComma" => ",", "OemPeriod" => ".", "OemOpenBrackets" => "[", "OemCloseBrackets" => "]",
            string k when k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) => k[1..], string k => k };
    }

    // The input owns the keyboard while focused, so the hotkey is read directly to close the window.
    private static bool KeyDown()
    {
        var keys = CompanionUISystem.ToggleKey?.GetAssignedKeys();
        return keys != null && keys.Count > 0 && Enum.TryParse<Keys>(keys[0], out var key) && Main.keyState.IsKeyDown(key);
    }

    private static bool Thinking
    {
        get
        {
            if (CompanionDialogueSystem.IsBusy || CompanionInquirySystem.IsBusy || CompanionAgent.Thinking) return true;
            var state = ChatCoordinator.FindCompanion()?.InterfaceState;
            return state is AgentState.Planning or AgentState.Replanning;
        }
    }

    private static string ThinkingLabel => CompanionInquirySystem.IsBusy ? "在翻你的背包和资料…"
        : CompanionAgent.Thinking ? "在想下一步…" : "在想…";

    internal static string Status()
    {
        if (Thinking) return ThinkingLabel;
        if (ChatCoordinator.FindCompanion()?.HasPendingApproval == true) return "等你点同意";
        return CompanionDisplay.StateText();
    }

    internal static Color StatusColor() => Thinking || ChatCoordinator.FindCompanion()?.HasPendingApproval == true
        ? Ink.Accent : ChatCoordinator.FindCompanion() == null ? Ink.Faint : Ink.Calm;

    internal void Open()
    {
        if (_open) return;
        _open = true; _closeRequested = _sendRequested = false;
        Main.ClosePlayerChat();
        _pill.Remove();
        if (_frame.Parent == null) Append(_frame);
        _stripShown = TaskStrip.Wanted;
        Layout(false);
        Rebuild();
        _input.SetFocus(true);
        _escHeld = Main.keyState.IsKeyDown(Keys.Escape);
        _keyHeld = KeyDown();
        _swallowFrames = 15;
    }

    internal void Close()
    {
        _input?.SetFocus(false);
        _dragging = false;
        if (!_open) return;
        _open = false;
        Main.blockInput = false;
        _frame.Remove();
        CompanionUISystem.ChatClosed();
    }

    internal void ResetWorld()
    {
        Close();
        if (_input != null) _input.Text = "";
        _revision = -1;
    }

    private void Send()
    {
        string text = (_input.Text ?? "").Trim();
        if (text.StartsWith("/ai ", StringComparison.OrdinalIgnoreCase)) text = text[4..].Trim();
        if (text.Length == 0 || CompanionUISystem.IsBusy) return;
        _input.Text = "";
        Say(text, text);
        _input.SetFocus(true);
    }

    private static void Say(string shown, string command)
    {
        if (CompanionUISystem.IsBusy) return;
        CompanionUISystem.Publish("你：" + shown, Ink.Text);
        CompanionUISystem.Send(command);
    }

    private void Layout(bool recenter)
    {
        _screen = new Vector2(GetDimensions().Width, GetDimensions().Height);
        float w = Math.Min(620, _screen.X - 32), h = Math.Min(540, _screen.Y - 48);
        _frame.Width.Set(w, 0); _frame.Height.Set(h, 0);
        if (recenter || !_placed)
        {
            _frame.Left.Set((_screen.X - w) / 2, 0); _frame.Top.Set((_screen.Y - h) / 2, 0); _placed = true;
        }
        else
        {
            _frame.Left.Set(Math.Clamp(_frame.Left.Pixels, 8, Math.Max(8, _screen.X - w - 8)), 0);
            _frame.Top.Set(Math.Clamp(_frame.Top.Pixels, 8, Math.Max(8, _screen.Y - h - 8)), 0);
        }
        _close.Left.Set(w - 46, 0);
        _cheat.Left.Set(w - 130, 0);
        _workshop.Left.Set(w - 212, 0);

        float column = MessageItem.Column(w), side = (w - column) / 2;
        float shellTop = h - FooterHeight - ShellHeight;
        _shell.Left.Set(side, 0); _shell.Top.Set(shellTop, 0); _shell.Width.Set(column, 0); _shell.Height.Set(ShellHeight, 0);
        float bottom = shellTop - 10;
        if (_stripShown)
        {
            _strip.Left.Set(side, 0); _strip.Width.Set(column, 0); _strip.Top.Set(bottom - 42, 0);
            if (_strip.Parent == null) _frame.Append(_strip);
            bottom -= 52;
        }
        else _strip.Remove();
        float top = HeaderHeight + 8;
        _listWidth = w; _listHeight = Math.Max(60, bottom - top);
        _list.Left.Set(0, 0); _list.Top.Set(top, 0); _list.Width.Set(w, 0); _list.Height.Set(_listHeight, 0);
        _bar.Left.Set(w - 16, 0); _bar.Top.Set(top + 4, 0); _bar.Height.Set(_listHeight - 8, 0);
        Recalculate();
        _revision = -1;
    }

    private void Rebuild()
    {
        _revision = CompanionUISystem.HistoryRevision;
        _busy = Thinking;
        _list.Clear();
        var lines = CompanionUISystem.History.Where(l => !MessageItem.Hidden(l)).ToList();
        if (lines.Count == 0 && !_busy) _list.Add(new Welcome(_listHeight - 8, Say));
        foreach (var line in lines)
        {
            var (who, text) = MessageItem.Classify(line);
            if (text.Trim().Length > 0) _list.Add(new MessageItem(who, text, _listWidth));
        }
        if (_busy) _list.Add(new ThinkingItem(_listWidth, ThinkingLabel));
        _list.Recalculate();
        _list.ViewPosition = float.MaxValue;
    }

    public override void Update(GameTime gameTime)
    {
        if (_open)
        {
            var screen = new Vector2(GetDimensions().Width, GetDimensions().Height);
            if (screen != _screen) Layout(true);
            if (_dragging)
            {
                if (!Main.mouseLeft) _dragging = false;
                else
                {
                    var pos = Main.MouseScreen - _dragOffset;
                    _frame.Left.Set(Math.Clamp(pos.X, 8, Math.Max(8, _screen.X - _frame.Width.Pixels - 8)), 0);
                    _frame.Top.Set(Math.Clamp(pos.Y, 8, Math.Max(8, _screen.Y - _frame.Height.Pixels - 8)), 0);
                    Recalculate();
                }
            }
            if (_frame.ContainsPoint(Main.MouseScreen))
            { Main.LocalPlayer.mouseInterface = true; PlayerInput.LockVanillaMouseScroll("TerrarAI: Chat"); }
            bool escape = Main.keyState.IsKeyDown(Keys.Escape);
            bool hotkey = KeyDown();
            // While typing, the hotkey is just a character (a comma is part of a sentence); Esc still closes.
            bool closeNow = _closeRequested || escape && !_escHeld && !_input.Focused;
            _escHeld = escape; _keyHeld = hotkey;
            if (closeNow) { Close(); base.Update(gameTime); return; }
            if (_sendRequested) { _sendRequested = false; Send(); }
            if (_swallowFrames > 0)
            {
                _swallowFrames--;
                if (_input.Text is "," or "，" or "`" or "·") _input.Text = "";
            }
            bool strip = TaskStrip.Wanted;
            if (strip != _stripShown) { _stripShown = strip; Layout(false); }
            if (_revision != CompanionUISystem.HistoryRevision || Thinking != _busy) Rebuild();
        }
        else if (TaskPill.Wanted)
        {
            _pill.Refresh();
            if (_pill.Parent == null) { Append(_pill); Recalculate(); }
        }
        else _pill.Remove();
        base.Update(gameTime);
    }

    private sealed class Frame : UIElement
    {
        private readonly ChatWindow _owner;
        internal Frame(ChatWindow owner) => _owner = owner;

        protected override void DrawSelf(SpriteBatch sb)
        {
            var r = GetDimensions().ToRectangle();
            var shadow = r; shadow.Offset(0, 6); shadow.Inflate(4, 2);
            Ink.Round(sb, shadow, Ink.Shade, 18);
            Ink.Card(sb, r, Ink.Window, Ink.Edge, 14);
            Ink.Box(sb, r.X + 1, r.Y + (int)HeaderHeight, r.Width - 2, 1, Ink.Edge);
            const float titleScale = 0.92f;
            float tx = r.X + 22;
            Ink.Write(sb, "同伴", new Vector2(tx, Ink.Middle(r.Y, HeaderHeight, titleScale)), Ink.Text, titleScale);
            tx += Ink.Width("同伴", titleScale) + 14;
            Ink.Round(sb, new Rectangle((int)tx, r.Y + (int)HeaderHeight / 2 - 3, 7, 7), StatusColor(), 3);
            tx += 15;
            float room = _owner._workshop.GetDimensions().X - tx - 12;
            Ink.Write(sb, Ink.Fit(Status(), room, Ink.Small), new Vector2(tx, Ink.Middle(r.Y, HeaderHeight, Ink.Small)),
                Ink.Muted, Ink.Small);
            string key = KeyName();
            string footer = "Enter 发送  ·  Esc 关闭  ·  聊天时同伴继续工作";
            float fw = Ink.Width(footer, 0.62f);
            Ink.Write(sb, footer, new Vector2(r.X + (r.Width - fw) / 2, Ink.Middle(r.Bottom - FooterHeight, FooterHeight - 4, 0.62f)),
                Ink.Faint, 0.62f);
        }
    }

    private sealed class Shell : UIElement
    {
        internal CompanionInput? Input;

        protected override void DrawSelf(SpriteBatch sb)
        {
            var r = GetDimensions().ToRectangle();
            bool focus = Input?.Focused == true;
            Ink.Card(sb, r, Ink.Raised, focus ? Ink.Gold : Ink.Edge, 14);
        }
    }
}
