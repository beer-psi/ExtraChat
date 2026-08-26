using System.Collections.Concurrent;
using System.Text;
using Dalamud.Game.Command;

namespace ExtraChat;

/// <summary>
/// A custom plugin command handler. This registers stubs in Dalamud, but we don't expect those to be invoked under
/// normal circumstances, since we do our own command handling between normal vanilla commands and debug vanilla commands.
///
/// The reason for doing this is that when invoking a debug command, all the payloads are decoded before passing
/// to the handler, making stuff like auto-translate phrases and &lt;se.#&gt; macros useless. 
/// </summary>
/// <param name="plugin"></param>
internal class PluginCommandManager(Plugin plugin) : IDisposable
{
    private readonly ConcurrentDictionary<string, PluginCommandInfo> _commandMap = new();
    
    public void Dispose()
    {
        List<string> commands = [.. _commandMap.Keys];

        foreach (var command in commands)
            RemoveHandler(command);
        
        GC.SuppressFinalize(this);
    }

    public bool ProcessCommand(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<byte> command;
        ReadOnlySpan<byte> arguments;

        var separatorIndex = content.IndexOf(" "u8);

        if (separatorIndex == -1 || separatorIndex + 1 >= content.Length)
        {
            command = separatorIndex + 1 >= content.Length
                ? content[..separatorIndex]
                : content;
            arguments = ReadOnlySpan<byte>.Empty;
        }
        else
        {
            command = content[..separatorIndex];
            arguments = content[(separatorIndex + 1)..];
        }

        var commandName = Encoding.UTF8.GetString(command);

        if (!_commandMap.TryGetValue(commandName, out var handler))
            return false;

        DispatchCommand(commandName, arguments, handler);
        return true;
    }

    private void DispatchCommand(string command, ReadOnlySpan<byte> arguments, PluginCommandInfo info)
    {
        try
        {
            info.Handler(command, arguments);
        }
        catch (Exception ex)
        {
            plugin.Log.Error(ex, "Error while dispatching command {CommandName} (Argument: {Argument})", command,
                Convert.ToHexString(arguments));
        }
    }

    public bool AddHandler(string command, PluginCommandInfo info)
    {
        var result = plugin.CommandManager.AddHandler(command, new CommandInfo(DalamudCommandHandler)
        {
            HelpMessage = info.HelpMessage,
            ShowInHelp = info.ShowInHelp,
            DisplayOrder = info.DisplayOrder,
            AllowedInMacros = true
        });

        if (!result)
        {
            plugin.Log.Error("Command {CommandName} is already registered with Dalamud.", command);
            return false;
        }

        if (!_commandMap.TryAdd(command, info))
        {
            plugin.Log.Error("Command {CommandName} is already registered.", command);
            return false;
        }

        return true;
    }

    public bool RemoveHandler(string command)
    {
        var removed = _commandMap.TryRemove(command, out _);

        if (!plugin.CommandManager.RemoveHandler(command))
            plugin.Log.Warning("Command {CommandName} was registered with the plugin, but not with Dalamud?", command);

        return removed;
    }

    private void DalamudCommandHandler(string command, string arguments)
    {
        plugin.Log.Warning("Dalamud command handler invoked for {CommandName} (Argument: {Argument})", command, arguments);

        if (_commandMap.TryGetValue(command, out var handler))
            DispatchCommand(command, Encoding.UTF8.GetBytes(arguments), handler);
    }
}

internal class PluginCommandInfo(PluginCommandInfo.HandlerDelegate handler)
{
    public delegate void HandlerDelegate(string command, ReadOnlySpan<byte> arguments);
    
    public HandlerDelegate Handler { get; init; } = handler;
    
    public string HelpMessage { get; set; } = string.Empty;
    
    public bool ShowInHelp { get; set; } = true;
    
    public int DisplayOrder { get; set; } = -1;
}
