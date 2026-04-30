using ASodium;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ExtraChat.Integrations;
using ExtraChat.Ui;
using ExtraChat.Util;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ExtraChat;

// ReSharper disable once ClassNeverInstantiated.Global
public class Plugin : IDalamudPlugin {
    internal const ushort DefaultColour = 578;

    internal static string Name => "ExtraChat";

    [PluginService]
    internal static IPluginLog Log { get; private set; }

    [PluginService]
    internal IDalamudPluginInterface Interface { get; init; }

    [PluginService]
    internal IClientState ClientState { get; init; }

    [PluginService]
    internal ICommandManager CommandManager { get; init; }

    [PluginService]
    internal IContextMenu ContextMenu { get; init; }

    [PluginService]
    internal IChatGui ChatGui { get; init; }

    [PluginService]
    internal IDataManager DataManager { get; init; }

    [PluginService]
    internal IFramework Framework { get; init; }

    [PluginService]
    internal IGameGui GameGui { get; init; }

    [PluginService]
    internal INotificationManager NotificationManager { get; init; }

    [PluginService]
    internal IObjectTable ObjectTable { get; init; }

    [PluginService]
    internal IPlayerState PlayerState { get; init; }

    [PluginService]
    internal ITargetManager TargetManager { get; init; }

    [PluginService]
    internal IGameInteropProvider GameInteropProvider { get; init; }

    [PluginService]
    private IToastGui ToastGui { get; init; }

    internal Configuration Config { get; }
    internal ConfigInfo ConfigInfo => this.Config.GetConfig(this.PlayerState.ContentId);
    internal Client Client { get; }
    internal Commands Commands { get; }
    internal PluginUi PluginUi { get; }
    internal GameFunctions GameFunctions { get; }
    internal Ipc Ipc { get; }
    private IDisposable[] Integrations { get; }

    private IPlayerCharacter? _localPlayer;
    private readonly Mutex _localPlayerLock = new();

    internal IPlayerCharacter? LocalPlayer {
        get {
            this._localPlayerLock.WaitOne();
            var player = this._localPlayer;
            this._localPlayerLock.ReleaseMutex();
            return player;
        }
        private set {
            this._localPlayerLock.WaitOne();
            this._localPlayer = value;
            this._localPlayerLock.ReleaseMutex();
        }
    }

    public Plugin() {
        SodiumInit.Init();
        WorldUtil.Initialise(this.DataManager!);
        this.Config = this.Interface!.GetPluginConfig() as Configuration ?? new Configuration();
        this.Client = new Client(this);
        this.Commands = new Commands(this);
        this.PluginUi = new PluginUi(this);
        this.GameFunctions = new GameFunctions(this);
        this.Ipc = new Ipc(this);

        this.Integrations = [
            new ChatTwo(this),
        ];

        this.Framework!.Update += this.FrameworkUpdate;
        this.ContextMenu!.OnMenuOpened += this.OnMenuOpened;
    }

    public void Dispose() {
        this.GameFunctions.ResetOverride();

        this.ContextMenu.OnMenuOpened -= this.OnMenuOpened;
        this.Framework.Update -= this.FrameworkUpdate;
        this._localPlayerLock.Dispose();

        foreach (var integration in this.Integrations) {
            integration.Dispose();
        }

        this.Ipc.Dispose();
        this.GameFunctions.Dispose();
        this.PluginUi.Dispose();
        this.Commands.Dispose();
        this.Client.Dispose();
    }

    private void FrameworkUpdate(IFramework framework) {
        if (this.ObjectTable.LocalPlayer is { } player) {
            this.LocalPlayer = player;
        } else if (!this.ClientState.IsLoggedIn) {
            // only set to null if not logged in
            this.LocalPlayer = null;
        }
    }

    private unsafe void OnMenuOpened(IMenuOpenedArgs args) {
        var ctx = AgentContext.Instance();
        if (args.AgentPtr != (nint) ctx) {
            return;
        }

        if (ctx->TargetObjectId.ObjectId != 0xE000_0000) {
            this.ObjectContext(args, ctx->TargetObjectId.ObjectId);
            return;
        }

        var world = ctx->TargetHomeWorldId;
        if (world == 0) {
            return;
        }

        var name = SeString.Parse(ctx->TargetName.AsSpan()).TextValue;
        if (string.IsNullOrWhiteSpace(name)) {
            return;
        }

        args.AddMenuItem(new MenuItem {
            Name = "Invite to ExtraChat Linkshell",
            OnClicked = _ => {
                this.PluginUi.InviteInfo = (name, (ushort) world);
            },
        });
    }

    private void ObjectContext(IMenuOpenedArgs args, uint objectId) {
        var obj = this.ObjectTable.SearchById(objectId);
        if (obj is not IPlayerCharacter chara) {
            return;
        }

        args.AddMenuItem(new MenuItem {
            Name = "Invite to ExtraChat Linkshell",
            OnClicked = _ => {
                var name = chara.Name.TextValue;
                this.PluginUi.InviteInfo = (name, (ushort) chara.HomeWorld.RowId);
            },
        });
    }

    internal void SaveConfig() {
        this.Interface.SavePluginConfig(this.Config);
    }

    internal void ShowInfo(string message) {
        if (this.Config.UseNativeToasts) {
            this.ToastGui.ShowNormal(message);
        } else {
            this.NotificationManager.AddNotification(new Notification {
                Type = NotificationType.Info,
                Title = Name,
                Content = message,
            });
        }

        this.ChatGui.Print(new XivChatEntry {
            Type = XivChatType.SystemMessage,
            Message = message,
        });
    }

    internal void ShowError(string message) {
        if (this.Config.UseNativeToasts) {
            this.ToastGui.ShowError(message);
        } else {
            this.NotificationManager.AddNotification(new Notification {
                Type = NotificationType.Error,
                Title = Name,
                Content = message,
            });
        }

        this.ChatGui.Print(new XivChatEntry {
            Type = XivChatType.ErrorMessage,
            Message = message,
        });
    }
}
