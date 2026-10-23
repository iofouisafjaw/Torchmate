using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TerrarAI.Content.Companion;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerrarAI.Content.UI;

internal sealed class PlatformConstructionOverlay : UIState
{
    private Surface _surface = null!;
    private readonly System.Collections.Generic.List<PanelButton> _options = new();
    private PanelButton _supply = null!, _stock = null!;
    private Guid _shownPreview;
    internal bool Hovered => IsVisible && _surface.ContainsPoint(Main.MouseScreen);
    private static bool IsVisible => CompanionConstructionSystem.Visible && !CompanionUISystem.IsPanelOpen;
    private static bool Editing => CompanionConstructionSystem.NeedsPause;
    public override void OnInitialize()
    {
        _surface = new Surface { DrawContent = DrawContent }; _surface.SetPadding(16);
        _surface.Width.Set(420, 0); Append(_surface);
        _options.Add(Add("层 −", 0, 60, 84, () => Options(-1, 0)));
        _options.Add(Add("层 ＋", 92, 60, 84, () => Options(1, 0)));
        _options.Add(Add("间距 −", 192, 60, 92, () => Options(0, -1)));
        _options.Add(Add("间距 ＋", 292, 60, 96, () => Options(0, 1)));
        _supply = Add("备料并建造", 0, 166, 190, () => CompanionUISystem.Message(
            CompanionConstructionSystem.SupplyAsking ? CompanionConstructionSystem.ConfirmSupplyHarvest(Main.LocalPlayer)
                : CompanionConstructionSystem.ConfirmSupply(Main.LocalPlayer, _shownPreview), Color.LightBlue));
        _supply.Caption = () => CompanionConstructionSystem.SupplyAsking ? "同意砍这棵树" : "备料并建造";
        _supply.Selected = () => _supply.Enabled();
        _supply.Enabled = () => CompanionConstructionSystem.SupplyAsking || CompanionConstructionSystem.CanSupply(Main.LocalPlayer) && _shownPreview == CompanionConstructionSystem.PreviewId;
        _stock = Add("用现有平台", 198, 166, 190, () => CompanionUISystem.Message(
            CompanionConstructionSystem.Confirm(Main.LocalPlayer, _shownPreview), Color.LightBlue));
        _stock.Enabled = () => CompanionConstructionSystem.CanConfirm(Main.LocalPlayer) && _shownPreview == CompanionConstructionSystem.PreviewId;
        var manual = Add("重选范围", 0, -32, 124, CompanionUISystem.StartConstruction, bottom: true);
        manual.Caption = () => CompanionConstructionSystem.RequestedWidth != null ? "手动框选" : "重选范围";
        Add("详情", 132, -32, 124, CompanionUISystem.OpenTask, bottom: true);
        var close = Add("取消", 264, -32, 124, CompanionConstructionSystem.Hide, bottom: true);
        close.Caption = () => CompanionConstructionSystem.Active ? "取消" : "关闭";
    }
    private PanelButton Add(string label, float x, float y, float width, Action action, bool bottom = false)
    {
        var b = new PanelButton(label, action); b.Left.Set(x, 0); b.Top.Set(y, bottom ? 1 : 0);
        b.Width.Set(width, 0); b.Height.Set(30, 0); _surface.Append(b); return b;
    }
    private static void Options(int layers, int gap) => CompanionConstructionSystem.SetOptions(
        CompanionConstructionSystem.Layers + layers, CompanionConstructionSystem.Spacing + gap);
    private void DrawContent(SpriteBatch sb, CalculatedStyle area)
    {
        _shownPreview = CompanionConstructionSystem.PreviewId;
        void Line(string text, int y, Color color, float scale = 0.72f) => CompanionDisplay.DrawLine(sb, text, area.Position() + new Vector2(0, y), area.Width, color, scale);
        var phase = CompanionConstructionSystem.Phase;
        var blueprint = CompanionConstructionSystem.Blueprint;
        bool ready = phase == ConstructionPhase.Completed;
        bool overview = Editing && CompanionConstructionSystem.RequestedWidth != null;
        Line(ready ? "木平台 · 施工完成" : phase == ConstructionPhase.Failed ? "木平台 · 未完成" : "木平台工坊", 0,
            ready ? Ink.Calm : phase == ConstructionPhase.Failed ? Ink.Warn : Ink.Gold, 0.88f);
        Line($"{CompanionConstructionSystem.Layers} 层 · 间距 {CompanionConstructionSystem.Spacing} 格" +
            (blueprint == null ? "" : $" · 长 {blueprint.Region.Width} 格"), 30, Ink.Muted);
        int summaryY = Editing ? 100 : 58;
        Line(ready ? $"已验收 {CompanionConstructionSystem.Job?.Audit?.Present}/{blueprint?.Slots.Count} 格 · 剩余平台 {CompanionConstructionSystem.Stock(Main.LocalPlayer)}"
            : blueprint == null ? "点击两个对角选范围，右键取消" :
            $"平台 {CompanionConstructionSystem.Stock(Main.LocalPlayer)} / 需 {blueprint.Needed}  ·  复用 {blueprint.Reused}  ·  冲突 {blueprint.Conflicts}", summaryY, Ink.Text);
        string status = phase == ConstructionPhase.Selecting ? CompanionConstructionSystem.DragStart.HasValue ? "再点击另一个对角" : "先选范围，再确认材料与建造方式"
            : phase == ConstructionPhase.Preview && blueprint?.Conflicts == 0 ? CompanionConstructionSystem.SupplyDescription() : CompanionConstructionSystem.Notice;
        if (ready && CompanionConstructionSystem.Supply is { } supply)
            status = $"消耗木材 {supply.WoodConsumed} · 合成平台 {supply.Crafted}\n背包剩余木材 {TerrariaPlatformSupplySite.CountWood(Main.LocalPlayer)}，平台 {CompanionConstructionSystem.Stock(Main.LocalPlayer)}";
        else if (phase == ConstructionPhase.Failed) status = CompanionConstructionSystem.Supply?.Reason ?? status;
        var lines = TranscriptLine.Wrap(status, area.Width, 0.70f);
        for (int i = 0; i < Math.Min(overview ? 1 : Editing || CompanionConstructionSystem.SupplyAsking ? 2 : 3, lines.Length); i++)
            Line(lines[i], summaryY + 26 + i * 21, phase == ConstructionPhase.Failed ? Ink.Warn : Ink.Muted, 0.70f);
        if (overview)
        {
            Line("全长预览 · 点击世界调整左下角", 150, Ink.Muted, .68f);
            if (blueprint != null)
                foreach (var slot in blueprint.Slots)
                {
                    float step = area.Width / blueprint.Region.Width;
                    float x = area.X + (slot.At.X - blueprint.Region.Left) * step;
                    float y = area.Y + 176 + (slot.At.Y - blueprint.Region.Top) * 30f / Math.Max(1, blueprint.Region.Height - 1);
                    var color = slot.Before == PlatformCell.Protected ? Ink.Warn : slot.Before == PlatformCell.Platform ? Ink.Calm : new Color(114, 201, 241);
                    sb.Draw(TextureAssets.MagicPixel.Value, new Rectangle((int)x, (int)y, Math.Max(1, (int)(step - 1)), 3), color);
                }
        }
    }
    public override void Update(GameTime gameTime)
    {
        if (!IsVisible) { _surface.Remove(); return; }
        if (_surface.Parent == null) Append(_surface);
        bool actions = Editing || CompanionConstructionSystem.SupplyAsking;
        bool overview = Editing && CompanionConstructionSystem.RequestedWidth != null;
        float extra = overview ? 60 : 0;
        _supply.Top.Set(166 + extra, 0); _stock.Top.Set(166 + extra, 0);
        _surface.Height.Set(actions ? 268 + extra : 216, 0);
        _surface.Left.Set(-436, 1); _surface.Top.Set(actions ? -284 - extra : -232, 1);
        foreach (var b in _options) Show(b, Editing);
        Show(_supply, actions); Show(_stock, Editing);
        Recalculate();
        if (Hovered) { Main.LocalPlayer.mouseInterface = true; PlayerInput.LockVanillaMouseScroll("TerrarAI: Platform workshop"); }
        base.Update(gameTime);
    }
    private void Show(UIElement element, bool show)
    { if (show && element.Parent == null) _surface.Append(element); else if (!show) element.Remove(); }
    public override void Draw(SpriteBatch sb) { if (IsVisible) base.Draw(sb); }
    private sealed class Surface : PixelSurface
    {
        internal Action<SpriteBatch, CalculatedStyle>? DrawContent;
        protected override void DrawSelf(SpriteBatch sb) { base.DrawSelf(sb); DrawContent?.Invoke(sb, GetInnerDimensions()); }
    }
}

public sealed class PlatformBlueprintRenderer : ModSystem
{
    public override void PostDrawTiles()
    {
        if (Main.dedServ || !CompanionConstructionSystem.Active || Main.gameMenu) return;
        var region = CompanionConstructionSystem.Region;
        if (CompanionConstructionSystem.DragStart is { } start)
            region = BuildRegion.Between(start, new((int)(Main.MouseWorld.X / 16), (int)(Main.MouseWorld.Y / 16)));
        var sb = Main.spriteBatch;
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
        try
        {
            void Box(int x, int y, int width, int height, Color color) => sb.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle(x - (int)Main.screenPosition.X, y - (int)Main.screenPosition.Y, width, height), color);
            if (region is { } r)
            {
                int x = r.Left * 16, y = r.Top * 16, w = r.Width * 16, h = r.Height * 16;
                var color = new Color(180, 216, 157, 200);
                Box(x, y, w, 2, color); Box(x, y + h - 2, w, 2, color);
                Box(x, y, 2, h, color); Box(x + w - 2, y, 2, h, color);
            }
            if (CompanionConstructionSystem.Blueprint is { } b)
                foreach (var s in b.Slots)
                {
                    Color c = s.Before switch { PlatformCell.Platform => new Color(126, 220, 125, 180),
                        PlatformCell.Protected => new Color(239, 112, 112, 220), _ => new Color(114, 201, 241, 180) };
                    Box(s.At.X * 16 + 1, s.At.Y * 16 + 3, 14, 5, c);
                }
        }
        finally { sb.End(); }
    }
}
