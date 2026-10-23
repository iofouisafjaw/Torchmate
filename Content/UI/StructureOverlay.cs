using System;
using System.Linq;
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

// The small wooden card shown while placing / previewing / building a blueprint.
internal sealed class StructureOverlay : UIState
{
    private PixelSurface _surface = null!;
    private Guid _shownPreview;
    internal bool Hovered => IsVisible && _surface.ContainsPoint(Main.MouseScreen);
    private static bool IsVisible => StructureWorkshop.Visible && !CompanionUISystem.IsPanelOpen;

    public override void OnInitialize()
    {
        _surface = new Surface(); _surface.SetPadding(16);
        _surface.Width.Set(420, 0); _surface.Height.Set(196, 0); Append(_surface);
        Add("详情", -64, 0, 64, CompanionUISystem.OpenTask, right: true);
        var confirm = Add("确认施工", 0, 116, 124, () => CompanionUISystem.Message(
            StructureWorkshop.Confirm(Main.LocalPlayer, _shownPreview), Color.LightBlue));
        confirm.Caption = () => StructureWorkshop.ArenaMode ? StructureWorkshop.Phase == StructurePhase.Running ? "同意本次采集" : "备料并施工" : StructureWorkshop.HousingMode ? "确认备料修补" : "确认施工";
        confirm.Enabled = () => StructureWorkshop.ArenaMode ? StructureWorkshop.ArenaAsking && _shownPreview == StructureWorkshop.ArenaConfirmationId : (StructureWorkshop.HousingMode ? StructureWorkshop.CanRepair(Main.LocalPlayer) : StructureWorkshop.CanConfirm(Main.LocalPlayer))
            && _shownPreview == StructureWorkshop.PreviewId;
        var move = Add("换个位置", 132, 116, 124, () => {
            if (StructureWorkshop.ArenaMode) StructureWorkshop.RefreshArena(); else if (StructureWorkshop.HousingMode) StructureWorkshop.RecheckHousing(); else StructureWorkshop.Replace(); });
        move.Caption = () => StructureWorkshop.ArenaMode ? "刷新计划" : StructureWorkshop.HousingMode ? "再次检测" : "换个位置";
        move.Enabled = () => StructureWorkshop.HousingMode ? StructureWorkshop.Housing != null && StructureWorkshop.Phase != StructurePhase.Running
            : StructureWorkshop.Phase == StructurePhase.Preview;
        var cancel = Add("取消", 264, 116, 124, StructureWorkshop.Hide);
        cancel.Caption = () => StructureWorkshop.Active ? "取消" : "关闭";
        _repairButtons = new[] { confirm, move, cancel };
        ((Surface)_surface).DrawContent = (sb, area) =>
        {
            _shownPreview = StructureWorkshop.ArenaMode ? StructureWorkshop.ArenaConfirmationId : StructureWorkshop.PreviewId;
            void Line(string text, int y, Color color, float scale = 0.68f) =>
                CompanionDisplay.DrawLine(sb, text, area.Position() + new Vector2(0, y), area.Width, color, scale);
            var plan = StructureWorkshop.Plan;
            if (StructureWorkshop.ArenaMode)
            {
                Line("克眼战前准备", 0, Ink.Gold, 0.8f);
                Line("3层 × 100格 · 层距6格 · 2篝火／2心灯", 28, Ink.Text);
                Line(StructureWorkshop.ArenaIncludesPotions ? "铁皮／再生／敏捷各2 · 弱效治疗10" : "协作：本预览只建场地 · 药水单独准备", 50, Ink.Text);
                Line(StructureWorkshop.CollaborationPlayerBuild ? "你负责施工 · 完成后说「核验协作」" : "左键场景调整位置 · 详情查看完整材料与制作站", 74, Ink.Muted, 0.62f);
                Line(StructureWorkshop.Missing.Count > 0 ? "需补：" + StructureWorkshop.MissingText(4) : "确认后：背包 → 附近箱子 → 采集 → 合成 → 施工", 100, Ink.Muted, 0.62f);
                var arenaLines = TranscriptLine.Wrap(StructureWorkshop.Notice, area.Width, 0.68f);
                for (int i = 0; i < Math.Min(4, arenaLines.Length); i++) Line(arenaLines[i], 128 + i * 19, Ink.Text);
                return;
            }
            if (StructureWorkshop.HousingMode)
            {
                Line("房屋检测与修补", 0, Ink.Gold, 0.8f);
                var housing = StructureWorkshop.Housing;
                Line(housing == null ? "点击屋内空格 · 40格内" : housing.Suitable ? "原生检测：合格" : "原生检测：未通过", 28,
                    housing?.Suitable == true ? Color.LightGreen : Ink.Text);
                string issues = housing == null ? "检测及预览不消耗材料。" : string.Join("；", housing.Issues);
                var hints = TranscriptLine.Wrap(issues, area.Width, 0.68f);
                for (int i = 0; i < Math.Min(3, hints.Length); i++) Line(hints[i], 50 + i * 19, Ink.Muted);
                string parts = plan?.Steps.Count > 0 ? string.Join("、", plan.Materials().Select(x => $"{Crafting.Name(x.Key)}×{x.Value}")) : "无自动修补项";
                Line("修补：" + parts, 112, Ink.Text);
                var raw = StructureWorkshop.RepairBudget?.Consumed;
                Line("合成耗材：" + (raw?.Count > 0 ? string.Join("、", raw.Select(x => $"{Crafting.Name(x.Key)}×{x.Value}")) : "无需合成"), 134, Ink.Muted);
                if (StructureWorkshop.Missing.Count > 0) Line("还缺：" + StructureWorkshop.MissingText(4), 156, Color.Orange);
                else Line("蓝：待修补 · 原有内容保留 · 完整结果见详情", 156, Ink.Muted, 0.62f);
                var status = TranscriptLine.Wrap(StructureWorkshop.Notice, area.Width, 0.68f);
                for (int i = 0; i < Math.Min(2, status.Length); i++) Line(status[i], 178 + i * 19, Ink.Text);
                return;
            }
            Line("建筑工坊 · " + (plan?.Name ?? "未选择"), 0, Ink.Gold, 0.8f);
            if (plan != null)
                Line($"{plan.Width}×{plan.Height} 格 · {plan.Steps.Count} 步 · 来源：{plan.Source}", 28, Ink.Muted);
            var states = StructureWorkshop.States;
            if (states != null && StructureWorkshop.Phase == StructurePhase.Preview)
                Line($"要建 {states.Count(s => s is CellState.Empty or CellState.Clearable)} · 已有 {states.Count(s => s == CellState.Done)}" +
                     $" · 先挖 {states.Count(s => s == CellState.Clearable)} · 挡住 {states.Count(s => s == CellState.Blocked)}", 50, Ink.Text);
            string notice = StructureWorkshop.CollaborationPlayerBuild && StructureWorkshop.Phase == StructurePhase.Preview ?
                "你负责施工；完成后说「核验协作」，或说「住房你来」交接。" : StructureWorkshop.Phase == StructurePhase.Placing ? "移动鼠标选位置，左键预览；右键取消" : StructureWorkshop.Notice;
            var lines = TranscriptLine.Wrap(notice, area.Width, 0.70f);
            for (int i = 0; i < Math.Min(2, lines.Length); i++) Line(lines[i], 72 + i * 20, Ink.Muted, 0.70f);
            Line("蓝：要建  绿：已有  黄：先挖  红：保留", 160, Ink.Text, 0.62f);
        };
    }

    private PanelButton[] _repairButtons = Array.Empty<PanelButton>();

    private PanelButton Add(string text, float x, float y, float width, Action action, bool right = false)
    {
        var b = new PanelButton(text, () => { if (IsVisible) action(); }); b.Left.Set(x, right ? 1 : 0); b.Top.Set(y, 0);
        b.Width.Set(width, 0); b.Height.Set(32, 0); _surface.Append(b); return b;
    }

    public override void Update(GameTime gameTime)
    {
        if (!IsVisible) { _surface.Remove(); return; }
        if (_surface.Parent == null) Append(_surface);
        _surface.Left.Set(-436, 1);
        bool repair = StructureWorkshop.HousingMode || StructureWorkshop.ArenaMode;
        _surface.Height.Set(repair ? 294 : 196, 0);
        _surface.Top.Set(repair ? -310 : -212, 1);
        foreach (var button in _repairButtons) button.Top.Set(repair ? 222 : 116, 0);
        Recalculate();
        if (Hovered) { Main.LocalPlayer.mouseInterface = true; PlayerInput.LockVanillaMouseScroll("TerrarAI: Structure workshop"); }
        base.Update(gameTime);
    }

    public override void Draw(SpriteBatch spriteBatch) { if (IsVisible) base.Draw(spriteBatch); }

    private sealed class Surface : PixelSurface
    {
        internal Action<SpriteBatch, CalculatedStyle>? DrawContent;
        protected override void DrawSelf(SpriteBatch sb) { base.DrawSelf(sb); DrawContent?.Invoke(sb, GetInnerDimensions()); }
    }
}

// Ghost of the blueprint in the world: follows the mouse while placing, colored by cell state after.
public sealed class StructureRenderer : ModSystem
{
    public override void PostDrawTiles()
    {
        if (Main.dedServ || Main.gameMenu || !StructureWorkshop.Active || StructureWorkshop.Plan is not { } plan) return;
        var origin = StructureWorkshop.Origin ?? StructureWorkshop.GhostOrigin(Main.MouseWorld.ToTileCoordinates());
        var states = StructureWorkshop.Phase == StructurePhase.Placing ? null : StructureWorkshop.States;
        int cursor = StructureWorkshop.Job?.Cursor ?? 0;
        var sb = Main.spriteBatch;
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
        try
        {
            void Box(int x, int y, int w, int h, Color c) => sb.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle(x - (int)Main.screenPosition.X, y - (int)Main.screenPosition.Y, w, h), c);
            int ox = origin.X * 16, oy = origin.Y * 16, ow = plan.Width * 16, oh = plan.Height * 16;
            var frame = new Color(244, 218, 159, 220);
            Box(ox, oy, ow, 2, frame); Box(ox, oy + oh - 2, ow, 2, frame); Box(ox, oy, 2, oh, frame); Box(ox + ow - 2, oy, 2, oh, frame);
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var s = plan.Steps[i];
                var state = states != null && i < states.Length ? states[i] : CellState.Empty;
                Color c = state switch
                {
                    CellState.Done => new Color(126, 220, 125, 110),
                    CellState.Clearable => new Color(236, 206, 96, 150),
                    CellState.Blocked => new Color(239, 112, 112, 190),
                    _ => s.Kind == StepKind.Wall ? new Color(90, 150, 200, 60) : new Color(114, 201, 241, 150)
                };
                if (StructureWorkshop.Phase == StructurePhase.Running && i < cursor) c *= 0.25f;
                if (s.Kind == StepKind.Furniture)
                    foreach (var (fx, fy) in s.Footprint()) Box((origin.X + fx) * 16 + 2, (origin.Y + fy) * 16 + 2, 12, 12, c);
                else Box((origin.X + s.Dx) * 16 + 1, (origin.Y + s.Dy) * 16 + 1, 14, 14, c);
            }
        }
        finally { sb.End(); }
    }
}
