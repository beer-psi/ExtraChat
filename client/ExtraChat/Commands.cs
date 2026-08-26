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
    private PluginCommandManager PluginCommandManager { get; }

    internal Commands(Plugin plugin, PluginCommandManager pluginCommandManager) {
        this.Plugin = plugin;
        this.PluginCommandManager = pluginCommandManager;
        
        this.Plugin.ClientState.Logout += this.OnLogout;

        this.RegisterMain();
        this.RegisterAll();
    }

    private void OnLogout(int type, int code) {
        this.UnregisterAll();
    }

    private void RegisterMain() {
        foreach (var command in MainCommands) {
            this.PluginCommandManager.AddHandler(command, new PluginCommandInfo(MainCommand)
            {
                HelpMessage = "Opens the main ExtraChat UI.",
            });
        }
        
        this.PluginCommandManager.AddHandler("/ecl", new PluginCommandInfo(RecentLinkshellCommandHandler)
        {
            ShowInHelp = false
        });
    }

    private void UnregisterMain() {
        foreach (var command in MainCommands)
            this.PluginCommandManager.RemoveHandler(command);

        this.PluginCommandManager.RemoveHandler("/ecl");
    }

    private void MainCommand(string command, ReadOnlySpan<byte> arguments) {
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

        foreach (var (idx, id) in info.ChannelOrder)
            this.RegisterLinkshellCommand($"/ecl{idx + 1}", id);

        foreach (var (alias, id) in info.Aliases)
            this.RegisterLinkshellCommand(alias, id);
    }

    private void UnregisterAll() {
        var info = this.Plugin.ConfigInfo;

        foreach (var (alias, _) in info.Aliases)
            this.PluginCommandManager.RemoveHandler(alias);
            
        foreach (var (idx, _) in info.ChannelOrder)
            this.PluginCommandManager.RemoveHandler($"/ecl{idx + 1}");
    }

    private void RegisterLinkshellCommand(string command, Guid id) {
        this.PluginCommandManager.AddHandler(command, new PluginCommandInfo(CreateLinkshellCommandHandler(id)) {
            ShowInHelp = false,
        });
    }

    private void RecentLinkshellCommandHandler(string command, ReadOnlySpan<byte> arguments)
    {
        if (this.Plugin.ConfigInfo.LastSentChannel == Guid.Empty)
        {
            Plugin.ChatGui.PrintError("The last ExtraChat linkshell you sent a message to was not known.");
            return;
        }

        if (arguments.Length == 0)
            this.Plugin.GameFunctions.OverrideChannel = this.Plugin.ConfigInfo.LastSentChannel;
        else
            SendMessage(this.Plugin.ConfigInfo.LastSentChannel, arguments);
    }

    private PluginCommandInfo.HandlerDelegate CreateLinkshellCommandHandler(Guid channel)
    {
        return (_, arguments) =>
        {
            if (arguments.Length == 0)
                this.Plugin.GameFunctions.OverrideChannel = channel;
            else
                SendMessage(channel, arguments);
        };
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
