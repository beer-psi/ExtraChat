using System.Text;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using ExtraChat.Util;
using Lumina.Text.ReadOnly;

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
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        this.Plugin.Ipc?.BroadcastChannelCommandColours();
    }

    private void RegisterAll() {
        var info = this.Plugin.ConfigInfo;
        foreach (var (idx, id) in info.ChannelOrder) {
            this.RegisterLinkshellCommand($"/ecl{idx + 1}", id);
        }

        foreach (var (alias, id) in info.Aliases) {
            this.RegisterLinkshellCommand(alias, id);
        }
    }

    private void UnregisterAll() {
        foreach (var command in this.Registered.Keys) {
            this.Plugin.CommandManager.RemoveHandler(command);
        }

        this.RegisteredInternal.Clear();
    }

    private void RegisterLinkshellCommand(string command, Guid id) {
        this.RegisteredInternal[command] = id;
        this.Plugin.CommandManager.AddHandler(command, new CommandInfo(LinkshellCommandHandler) {
            ShowInHelp = false,
        });
    }

    private void LinkshellCommandHandler(string command, string arguments)
    {
        Plugin.Log.Warning($"Linkshell command handler actually executed: {command} {arguments}");
    }

    internal void SendMessage(Guid id, ReadOnlySpan<byte> bytes) {
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
