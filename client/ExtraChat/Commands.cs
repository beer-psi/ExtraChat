using System.Text;
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
        
        this.Plugin.CommandManager.AddHandler("/ecl", new CommandInfo(RecentLinkshellCommandHandler)
        {
            ShowInHelp = false
        });
    }

    private void UnregisterMain() {
        foreach (var command in MainCommands) {
            this.Plugin.CommandManager.RemoveHandler(command);
        }

        this.Plugin.CommandManager.RemoveHandler("/ecl");
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

    private void RecentLinkshellCommandHandler(string command, string arguments)
    {
        Plugin.Log.Warning($"Linkshell command handler actually executed: {command} {arguments}");

        if (this.Plugin.ConfigInfo.LastSentChannel == Guid.Empty)
        {
            Plugin.ChatGui.PrintError("The last ExtraChat linkshell you sent a message to was not known.");
            return;
        }

        if (arguments.IsWhiteSpace())
            this.Plugin.GameFunctions.OverrideChannel = this.Plugin.ConfigInfo.LastSentChannel;
        else
            SendMessage(this.Plugin.ConfigInfo.LastSentChannel, Encoding.UTF8.GetBytes(arguments));
    }

    private void LinkshellCommandHandler(string command, string arguments)
    {
        Plugin.Log.Warning($"Linkshell command handler actually executed: {command} {arguments}");

        if (!this.RegisteredInternal.TryGetValue(command, out var channel))
        {
            this.Plugin.ChatGui.PrintError($"Could not find ExtraChat linkshell for command {command}.");
            return;
        }

        if (arguments.IsWhiteSpace())
            this.Plugin.GameFunctions.OverrideChannel = channel;
        else
            SendMessage(channel, Encoding.UTF8.GetBytes(arguments));
    }

    internal void SendMessage(Guid id, ReadOnlySpan<byte> bytes) {
        if (!this.Plugin.ConfigInfo.Channels.TryGetValue(id, out var info)) {
            this.Plugin.ChatGui.PrintError("ExtraChat Linkshell information could not be loaded.");
            return;
        }
        
        this.Plugin.ConfigInfo.LastSentChannel = id;
        this.Plugin.SaveConfig();
        
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
