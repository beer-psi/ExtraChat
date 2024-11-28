using Dalamud.Game.Command;
using ExtraChat.Util;

namespace ExtraChat;

internal class Commands : IDisposable {
    private static readonly string[] MainCommands = {
        "/extrachat",
        "/ec",
        "/eclcmd",
    };

    private Plugin Plugin { get; }
    private Dictionary<string, Guid> RegisteredInternal { get; } = new();
    internal IReadOnlyDictionary<string, Guid> Registered => this.RegisteredInternal;

    internal Commands(Plugin plugin) {
        this.Plugin = plugin;
        this.Plugin.ClientState.Logout += this.OnLogout;

        this.RegisterMain();
        this.RegisterAll();
    }

    private void OnLogout(int type, int code) {
        this.UnregisterAll();
    }

    private void RegisterMain() {
        foreach (var command in MainCommands) {
            this.Plugin.CommandManager.AddHandler(command, new CommandInfo(this.MainCommand) {
                HelpMessage = "Opens the main ExtraChat UI.",
            });
        }
    }

    private void UnregisterMain() {
        foreach (var command in MainCommands) {
            this.Plugin.CommandManager.RemoveHandler(command);
        }
    }

    private void MainCommand(string command, string arguments) {
        this.Plugin.PluginUi.Visible ^= true;
    }

    internal void ReregisterAll() {
        this.UnregisterAll();
        this.RegisterAll();
        this.Plugin.Ipc.BroadcastChannelCommandColours();
    }

    internal void RegisterAll() {
        var info = this.Plugin.ConfigInfo;
        foreach (var (idx, id) in info.ChannelOrder) {
            this.RegisterOne($"/ecl{idx + 1}", id);
        }

        foreach (var (alias, id) in info.Aliases) {
            this.RegisterOne(alias, id);
        }
    }

    internal void UnregisterAll() {
        foreach (var command in this.Registered.Keys) {
            this.Plugin.CommandManager.RemoveHandler(command);
        }

        this.RegisteredInternal.Clear();
    }

    private void RegisterOne(string command, Guid id) {
        this.RegisteredInternal[command] = id;

        void Handler(string _, string arguments) {
            Plugin.Log.Warning("Command handler actually invoked");
        }

        this.Plugin.CommandManager.AddHandler(command, new CommandInfo(Handler) {
            ShowInHelp = false,
        });
    }

    internal void SendMessage(Guid id, byte[] bytes) {
        if (!this.Plugin.ConfigInfo.Channels.TryGetValue(id, out var info)) {
            this.Plugin.ChatGui.PrintError("ExtraChat Linkshell information could not be loaded.");
            return;
        }

        var message = this.Plugin.GameFunctions.ResolvePayloads(bytes);
        var ciphertext = SecretBox.Encrypt(info.SharedSecret, message);
        Task.Run(async () => await this.Plugin.Client.SendMessage(id, ciphertext));
    }

    public void Dispose() {
        this.UnregisterAll();
        this.UnregisterMain();

        this.Plugin.ClientState.Logout -= this.OnLogout;
    }
}
