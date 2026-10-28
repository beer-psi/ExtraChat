using System.Numerics;
using ASodium;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ExtraChat.Integrations;
using ExtraChat.Ui;
using ExtraChat.Util;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Extensions;
using Lumina.Text.ReadOnly;

namespace ExtraChat;

// ReSharper disable once ClassNeverInstantiated.Global
public class Plugin : IAsyncDalamudPlugin {
    internal const ushort DefaultColour = 578;

    internal static string Name => "ExtraChat";

    [PluginService] internal IPluginLog Log { get; private set; } = null!;
    [PluginService] internal IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] internal IClientState ClientState { get; private set; } = null!;
    [PluginService] internal ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] private IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] internal IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal IDataManager DataManager { get; private set; } = null!;
    [PluginService] private INotificationManager NotificationManager { get; set; } = null!;
    [PluginService] private IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] internal IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] private IToastGui ToastGui { get; set; } = null!;
    [PluginService] internal IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] private IGameConfig GameConfig { get; set; } = null!;

    internal Configuration Config { get; }
    internal ConfigInfo ConfigInfo => this.Config.GetConfig(PlayerState.ContentId);
    internal Client Client { get; }
    private PluginCommandManager PluginCommandManager { get; }
    internal Commands Commands { get; }
    internal GameFunctions GameFunctions { get; }
    internal Ipc Ipc { get; }
    private IDisposable[] Integrations { get; }

    private readonly WindowSystem _windowSystem = new(Name);
    internal readonly ConfigWindow ConfigWindow;
    internal readonly InviteWindow InviteWindow;

    public Plugin() {
        SodiumInit.Init();
        WorldUtil.Initialise(this.DataManager);

        var configDir = Path.Join(this.Interface.GetPluginConfigDirectory(), "..");
        var originalExtraChat = Path.Join(configDir, "ExtraChat.json");
        var ourExtraChat = Path.Join(configDir, "ExtraChatFork.json");

        if (Path.Exists(originalExtraChat) && !Path.Exists(ourExtraChat))
            File.Copy(originalExtraChat, ourExtraChat);
        
        // register this before the client so it runs first and sets LocalPlayer for the client
        this.ClientState.Login += this.OnLogin;
        this.ClientState.Logout += this.OnLogout;
        
        this.Config = this.Interface.GetPluginConfig() as Configuration ?? new Configuration();

        if (this.Config.Version == 1)
        {
            File.Copy(ourExtraChat,
                Path.Join(this.Interface.GetPluginConfigDirectory(), $"{this.Config.Version}.bak.json"));
            
            this.Config.Version = 2;

            foreach (var config in this.Config.Configs.Values)
            {
                config.Key = null;
                config.Channels.Clear();
                config.CurrentChannel = Guid.Empty;
                config.LastSentChannel = Guid.Empty;
                config.ChannelOrder.Clear();
                config.Aliases.Clear();
                config.ChannelColors.Clear();
                config.ChannelMarkers.Clear();
                config.ChannelChannels.Clear();
                config.ChannelSoundEffects.Clear();
                config.ChannelSoundEffects.Clear();
                config.ChannelPlaySoundEffectMacros.Clear();
            }
            
            this.SaveConfig();
        }
        
        this.PluginCommandManager = new PluginCommandManager(this);
        this.Commands = new Commands(this, this.PluginCommandManager);
        this.Ipc = new Ipc(this);
        this.Client = new Client(this);
        this.GameFunctions = new GameFunctions(this, this.PluginCommandManager, this.Client);
        this.Integrations = [
            new ChatTwo(this),
        ];

        this.ConfigWindow = new ConfigWindow(this);
        this.InviteWindow = new InviteWindow(this);
        
        _windowSystem.AddWindow(this.ConfigWindow);
        _windowSystem.AddWindow(this.InviteWindow);

        Interface.UiBuilder.Draw += _windowSystem.Draw;
        Interface.UiBuilder.OpenConfigUi += this.ConfigWindow.Toggle;
        
        this.ContextMenu.OnMenuOpened += this.OnMenuOpened;
    }

    public Task LoadAsync(CancellationToken token)
    {
        if (this.ClientState.IsLoggedIn)
        {
            this.Client.StartLoop();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        this.GameFunctions.ResetOverride();

        this.ClientState.Logout -= this.OnLogout;
        this.ClientState.Login -= this.OnLogin;
        this.ContextMenu.OnMenuOpened -= this.OnMenuOpened;
        
        Interface.UiBuilder.OpenConfigUi -= this.ConfigWindow.Toggle;
        Interface.UiBuilder.Draw -= _windowSystem.Draw;
        
        _windowSystem.RemoveAllWindows();

        foreach (var integration in this.Integrations) {
            integration.Dispose();
        }
        
        this.GameFunctions.Dispose();
        await this.Client.DisposeAsync();
        this.Ipc.Dispose();
        this.Commands.Dispose();
        this.PluginCommandManager.Dispose();
        
        GC.SuppressFinalize(this);
    }

    private void OnLogin()
    {
        this.Client.StartLoop();
    }

    private void OnLogout(int type, int code)
    {
        this.Client.StopLoop();
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

        if (ctx->CurrentContextMenuTarget == null
            || ctx->CurrentContextMenuTarget->State == 0
            || (ctx->CurrentContextMenuTarget->State & InfoProxyCommonList.CharacterData.OnlineStatus.OfflineExd) != 0)
        {
            return;
        }

        var world = ctx->TargetHomeWorldId;

        if (world == 0) {
            return;
        }

        var name = new ReadOnlySeStringSpan(ctx->TargetName.AsSpan()).ExtractText();
        
        if (string.IsNullOrWhiteSpace(name)) {
            return;
        }

        args.AddMenuItem(new MenuItem {
            Name = "Invite to ExtraChat Linkshell",
            OnClicked = _ =>
            {
                this.InviteWindow.Name = name;
                this.InviteWindow.World = (ushort)world;
                this.InviteWindow.IsOpen = true;
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

                this.InviteWindow.Name = name;
                this.InviteWindow.World = (ushort)chara.HomeWorld.RowId;
                this.InviteWindow.IsOpen = true;
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
    
    internal bool GetChannelColour(Guid channel, out ushort colour, out uint rgba)
    {
        var uiColorSheet = this.DataManager.GetExcelSheet<UIColor>();
        var output = this.ConfigInfo.GetOutputChannel(channel);

        if (output.IsLinkshell())
        {
            if (this.GameConfig.TryGet(output.ToColorConfigOption(), out uint argb))
            {
                var rgba2 = rgba = BitOperations.RotateLeft(argb, 8);
                colour = (ushort)(uiColorSheet.FirstOrNull(color => color.Dark == rgba2)?.RowId ?? 0);
            }
            else
            {
                colour = Plugin.DefaultColour;
                rgba = uiColorSheet.GetRowOrDefault(colour)?.Dark ?? 0;
            }
        }
        else
        {
            colour = this.ConfigInfo.GetUiColour(channel);
            rgba = uiColorSheet.GetRowOrDefault(colour)?.Dark ?? 0;
        }

        return true;
    }
}
