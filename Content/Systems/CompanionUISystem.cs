using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using TerrarAI.Common.Commands;
using TerrarAI.Content.Companion;
using TerrarAI.Content.UI;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerrarAI.Content.Systems;

// Client presentation only. Commands still pass through the same world/session policy gates.
public sealed class CompanionUISystem : ModSystem
{
    internal sealed record Line(string Text, Color Color);
    internal static readonly List<Line> History = new();
    internal static int HistoryRevision { get; private set; }
    internal static ModKeybind? ToggleKey { get; private set; }
    private UserInterface? _chatInterface;
    private ChatWindow? _chat;
    private UserInterface? _constructionInterface;
    private PlatformConstructionOverlay? _constructionOverlay;
    private UserInterface? _structureInterface;
    private StructureOverlay? _structureOverlay;
    private GameTime _time = new();
    private long? _pausedTick;
    private bool _consumeEnterUntilReleased;
    private bool _constructionInputHeld;
    private bool _defaultChecked;
    private bool _eatEscape;
    private static uint _chatClosedAt;
    internal static void ChatClosed() => _chatClosedAt = Main.GameUpdateCount;

    // tModLoader only fills a mod keybind's default when the player presses "reset to default" in Controls;
    // a fresh install starts unbound. Bind the comma once, the first time we see it empty, and remember that
    // we did, so a player who later clears the key on purpose keeps it cleared.
    private static string DefaultKeyMarker => System.IO.Path.Combine(Main.SavePath, "ModData", "TerrarAI", "openchat-default-applied");

    private void ApplyDefaultKeyOnce()
    {
        try
        {
            if (ToggleKey == null || System.IO.File.Exists(DefaultKeyMarker)) return;
            var keys = ToggleKey.GetAssignedKeys(InputMode.Keyboard);
            if (keys.Count == 0)
            {
                keys.Add("OemComma");
                PlayerInput.Save();
                Main.InputProfiles.Save();
                Publish("· 按 , 打开／关闭 Qwen 对话窗口（设置 → 控件 里可以改）。", Color.LightGray);
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DefaultKeyMarker)!);
            System.IO.File.WriteAllText(DefaultKeyMarker, "OemComma");
        }
        catch (System.Exception ex) { Mod.Logger.Warn("Could not set the default chat key: " + ex.Message); }
    }

    public override void Load()
    {
        if (Main.dedServ) return;
        // New name so an old saved "unbound" entry does not hide the default. Rebind under Controls.
        ToggleKey = KeybindLoader.RegisterKeybind(Mod, "OpenChat", "OemComma");
        On_Main.CanPauseGame += PauseForPanel;
        On_Main.DoUpdate_Enter_ToggleChat += BlockVanillaEnter;
        On_Main.DoUpdate_HandleChat += BlockVanillaChatInput;
        _chat = new ChatWindow();
        _chat.Activate();
        _chatInterface = new UserInterface();
        _chatInterface.SetState(_chat);
        _constructionOverlay = new PlatformConstructionOverlay(); _constructionOverlay.Activate();
        _constructionInterface = new UserInterface(); _constructionInterface.SetState(_constructionOverlay);
        _structureOverlay = new StructureOverlay(); _structureOverlay.Activate();
        _structureInterface = new UserInterface(); _structureInterface.SetState(_structureOverlay);
    }

    internal static void Publish(string text, Color color)
    {
        if (Main.dedServ) return;
        History.Add(new Line(text, color));
        if (History.Count > 80) History.RemoveAt(0);
        HistoryRevision++;
    }

    internal static void Message(string text, Color color)
    {
        Publish(text, color);
        if (!IsPanelOpen && !CompanionConstructionSystem.Visible && !StructureWorkshop.Visible) Main.NewText(text, color);
    }
    // Passive notices must remain visible during running construction too.
    // Reuse chat history/text without opening a panel or taking input focus.
    internal static void Notify(string text,Color color)
    {
        Publish(text,color);
        if(!IsPanelOpen)Main.NewText(text,color);
    }

    internal static void Send(string text)
    {
        if (Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer) return;
        ChatCoordinator.HandleInput(Main.LocalPlayer, text, Message);
    }

    internal static void CheckConnection() => TestAPICommand.Check(Message);

    internal static void Ask(string question)
    {
        if (Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer) return;
        try { CompanionInquirySystem.Submit(Main.LocalPlayer, question); }
        catch (System.Exception ex) { Message(ex.Message, Color.Orange); }
    }

    internal static bool ForgetPreference(CompanionStore owner, LearnedPreference preference)
    {
        int index = ReferenceEquals(owner, CompanionMemorySystem.Store)
            ? owner.Memory.Preferences.FindIndex(p => ReferenceEquals(p, preference)) : -1;
        if (index < 0) { Message("这条记忆已更新，请刷新后再选。", Color.Orange); return false; }
        Send("forget " + (index + 1)); return true;
    }

    internal static void ForgetNote(CompanionStore owner, string note)
    {
        int index = ReferenceEquals(owner, CompanionMemorySystem.Store) ? owner.Memory.Notes.IndexOf(note) : -1;
        if (index < 0) { Message("这条备注已更新，请刷新后再选。", Color.Orange); return; }
        Send("forget " + (owner.Memory.Preferences.Count + index + 1));
    }

    internal static bool IsBusy => CompanionDialogueSystem.IsBusy || CompanionInquirySystem.IsBusy || CompanionAgent.Thinking || TestAPICommand.IsChecking;
    internal static bool IsPanelOpen => !Main.dedServ && ModContent.GetInstance<CompanionUISystem>().AnyOpen;
    private bool AnyOpen => _chat?.IsOpen == true;
    // The chat window does not pause: the companion keeps working while you talk to it.
    // Only the workshops pause, while you pick a spot in the world.
    internal bool WantsPause => !Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer &&
        (CompanionConstructionSystem.NeedsPause || StructureWorkshop.NeedsPause);
    // Enter and the vanilla chat line belong to our input while any window is up.
    private bool OwnsChatKeys => WantsPause || _chat?.IsOpen == true;
    private bool PauseForPanel(On_Main.orig_CanPauseGame orig) => WantsPause || orig();

    private void BlockVanillaEnter(On_Main.orig_DoUpdate_Enter_ToggleChat orig)
    {
        bool enter = Main.keyState.IsKeyDown(Keys.Enter);
        if (OwnsChatKeys)
        {
            _consumeEnterUntilReleased = enter;
            Main.chatRelease = !enter;
            return;
        }
        // Closing the panel while Enter is held must not leak that press to chat.
        if (_consumeEnterUntilReleased)
        {
            if (!enter) { _consumeEnterUntilReleased = false; Main.chatRelease = true; }
            return;
        }
        orig();
    }

    private void BlockVanillaChatInput(On_Main.orig_DoUpdate_HandleChat orig)
    {
        if (!OwnsChatKeys) { orig(); return; }
        if (Main.drawingPlayerChat) Main.ClosePlayerChat();
        // Leave text events for CompanionInput.DrawSelf; vanilla must not consume them first.
    }

    public override void PostUpdateInput()
    {
        // Mod keybinds are not registered yet while the title screen loads mods.
        if (Main.dedServ || Main.gameMenu) return;
        if (!_defaultChecked) { _defaultChecked = true; ApplyDefaultKeyOnce(); }
        // Esc closes the chat window; it must not also open the inventory behind it (held until released).
        bool escape = Main.keyState.IsKeyDown(Keys.Escape) || PlayerInput.Triggers.Current.Inventory;
        if (escape && (_eatEscape || _chat?.IsOpen == true || _chatClosedAt + 30 > Main.GameUpdateCount))
        {
            _eatEscape = true;
            PlayerInput.Triggers.JustPressed.Inventory = false;
            PlayerInput.Triggers.Current.Inventory = false;
        }
        else if (!escape) _eatEscape = false;
        // This runs during autopause as well as normal gameplay.
        if (ToggleKey?.JustPressed == true && !PlayerInput.WritingText && !Main.drawingPlayerChat) Toggle();
        if (WantsPause) Main.blockInput = true;
        else if (_constructionInputHeld && !PlayerInput.WritingText && !Main.drawingPlayerChat)
            Main.blockInput = false;
        _constructionInputHeld = CompanionConstructionSystem.NeedsPause || StructureWorkshop.NeedsPause;
    }

    public override void UpdateUI(GameTime gameTime)
    {
        _time = gameTime;
        if (!Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer)
        {
            _chatInterface?.Update(gameTime);
            _constructionInterface?.Update(gameTime);
            _structureInterface?.Update(gameTime);
            if (!IsPanelOpen && StructureWorkshop.NeedsPause)
                StructureWorkshop.PlacingInput(WorldMouseTile(), Main.mouseLeft, Main.mouseRight,
                    Main.keyState.IsKeyDown(Keys.Escape), Main.LocalPlayer.mouseInterface || Main.mapFullscreen);
            if (!IsPanelOpen && CompanionConstructionSystem.Active)
            {
                bool escape = Main.keyState.IsKeyDown(Keys.Escape);
                if (CompanionConstructionSystem.Phase == ConstructionPhase.Preview && CompanionConstructionSystem.RequestedWidth != null && !escape && !Main.mouseRight)
                    CompanionConstructionSystem.PlacementInput(new(WorldMouseTile().X, WorldMouseTile().Y), Main.mouseLeft,
                        Main.LocalPlayer.mouseInterface || Main.mapFullscreen);
                if (CompanionConstructionSystem.Phase == ConstructionPhase.Selecting)
                {
                    var tile = WorldMouseTile();
                    CompanionConstructionSystem.SelectionInput(new(tile.X, tile.Y),
                        Main.mouseLeft, Main.mouseRight, escape, Main.LocalPlayer.mouseInterface || Main.mapFullscreen);
                }
                else if (CompanionConstructionSystem.Phase == ConstructionPhase.Preview && (Main.mouseRight || escape))
                    CompanionConstructionSystem.Cancel("你取消了施工。");
            }
            if (WantsPause)
            {
                Main.blockInput = true;
                if (Main.drawingPlayerChat) Main.ClosePlayerChat();
                var companion = ChatCoordinator.FindCompanion();
                long tick = Main.GameUpdateCount;
                companion?.HoldActionTimersWhilePaused(_pausedTick.HasValue ? tick - _pausedTick.Value : 0);
                _pausedTick = tick;
                // Receive results without ticking physics, NPC AI or actions.
                CompanionDialogueSystem.ProcessPending();
                CompanionInquirySystem.ProcessPending();
                ChatCoordinator.FindCompanion()?.ProcessPlanningWhilePaused();
            }
            else _pausedTick = null;
        }
        else { _chat?.Close(); _pausedTick = null; }
    }

    private static Point WorldMouseTile()
    {
        // UpdateUI receives mouse coordinates in UI space. Terraria's world
        // conversion also accounts for game zoom and inverted gravity.
        PlayerInput.SetZoom_World();
        try { return Main.MouseWorld.ToTileCoordinates(); }
        finally { PlayerInput.SetZoom_UI(); }
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        // Vanilla draws this after Mouse Text; hide it while our modal is up so
        // old game-chat messages cannot paint over the companion's input box.
        if (OwnsChatKeys) layers.RemoveAll(layer => layer.Name == "Vanilla: Player Chat");
        int index = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        if (index < 0) index = layers.Count;
        layers.Insert(index, new LegacyGameInterfaceLayer("TerrarAI: Companion", () =>
        {
            if (!Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer)
            {
                _chatInterface?.Draw(Main.spriteBatch, _time);
                _constructionInterface?.Draw(Main.spriteBatch, _time);
                _structureInterface?.Draw(Main.spriteBatch, _time);
            }
            return true;
        }, InterfaceScaleType.UI));
    }

    // The hotkey opens the chat; it closes whichever window is up.
    internal void Toggle()
    {
        if (AnyOpen) _chat?.Close();
        else _chat?.Open();
    }
    internal static void OpenChat()
    {
        var self = ModContent.GetInstance<CompanionUISystem>();
        self._chat?.Open();
    }
    internal static void OpenTask()
    {
        string details = CompanionDisplay.TaskBody();
        if (History.Count == 0 || History[^1].Text != details) Publish(details, Ink.Text);
        OpenChat();
    }
    // "搭平台": pick the area in the world, so the window gets out of the way.
    internal static void StartConstruction()
    {
        var self = ModContent.GetInstance<CompanionUISystem>();
        Message(CompanionConstructionSystem.BeginSelection(Main.LocalPlayer), Color.LightBlue);
        if (CompanionConstructionSystem.Phase == ConstructionPhase.Selecting) self._chat?.Close();
    }
    internal static void StartArena()
    {
        var self = ModContent.GetInstance<CompanionUISystem>();
        Message(StructureWorkshop.BeginArena(Main.LocalPlayer), Color.LightBlue);
        if (StructureWorkshop.ArenaMode && StructureWorkshop.Phase == StructurePhase.Preview) self._chat?.Close();
    }
    internal static void RevealStructureSelection() => ModContent.GetInstance<CompanionUISystem>()._chat?.Close();

    internal static void StartHousing()
    {
        var self = ModContent.GetInstance<CompanionUISystem>();
        Message(StructureWorkshop.BeginHousing(Main.LocalPlayer), Color.LightBlue);
        if (StructureWorkshop.HousingMode && StructureWorkshop.Phase == StructurePhase.Placing) self._chat?.Close();
    }
    internal static void StartConstruction(PlatformRequest request)
    {
        var self = ModContent.GetInstance<CompanionUISystem>();
        string notice = CompanionConstructionSystem.BeginRequest(Main.LocalPlayer, request);
        Message(notice, Color.LightBlue);
        if (CompanionConstructionSystem.Phase == ConstructionPhase.Preview) self._chat?.Close();
    }
    internal static void StartStructure(StructureLibrary.Entry entry)
    {
        var self = ModContent.GetInstance<CompanionUISystem>();
        string message = StructureWorkshop.Begin(Main.LocalPlayer, entry);
        Message(message, Color.LightBlue);
        // Placing happens in the world, so get every window out of the way.
        if (StructureWorkshop.Phase == StructurePhase.Placing) self._chat?.Close();
    }

    private void ResetWorld()
    {
        if (_constructionInputHeld) Main.blockInput = false;
        _constructionInputHeld = false;
        _chat?.ResetWorld();
        CheatTools.Reset();
        _pausedTick = null;
        _consumeEnterUntilReleased = false;
        History.Clear();
        HistoryRevision++;
    }

    public override void OnWorldLoad() => ResetWorld();
    public override void OnWorldUnload() => ResetWorld();
    public override void Unload()
    {
        if (!Main.dedServ)
        {
            On_Main.CanPauseGame -= PauseForPanel;
            On_Main.DoUpdate_Enter_ToggleChat -= BlockVanillaEnter;
            On_Main.DoUpdate_HandleChat -= BlockVanillaChatInput;
        }
        ResetWorld();
        _chatInterface?.SetState(null); _chatInterface = null; _chat = null;
        _constructionInterface?.SetState(null); _constructionInterface = null; _constructionOverlay = null;
        ToggleKey = null;
    }
}

