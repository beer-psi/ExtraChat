
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Component.Shell;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;

namespace ExtraChat;

internal unsafe class GameFunctions : IDisposable {
    private Plugin Plugin { get; }

    // all this comes from 6.15: 751AF0

    // Processes <fixed(...)> macros in the provided SeString, then convert them back to a user-visible chat placeholder.
    // e.g. 02 2E 04 C9 06 01 03 "<fixed(200,5,0)>" will be converted back to "<se.1>".
    // This function may also play the sound effect if the third argument (2nd argument except this) is nonzero.
    // This can be found in ShellChatCommandTell.ExecuteCommand near the end of the IsInMordionGaol branch.
    [Signature("E8 ?? ?? ?? ?? 44 88 74 24 ?? 4C 8D 45")]
    private readonly delegate* unmanaged<PronounModule*, Utf8String*, byte, Utf8String*> _processFixedMacros;
    
    // Client::UI::Shell::RaptureShellModule::SetChatChannel
    [Signature("E8 ?? ?? ?? ?? 33 C0 EB ?? 85 D2", DetourName = nameof(SetChatChannelDetour))]
    private Hook<SetChatChannelDelegate> SetChatChannelHook { get; init; }
    private delegate void SetChatChannelDelegate(RaptureShellModule* module, uint channel);
    
    // Component::Shell::ShellCommandModule::??
    // Called by ShellCommandModule::ExecuteCommandInner and RaptureShellModule::Update on every message/macro line
    // after Component::Shell::ShellCommandModule::EvaluateTextCommand is called.
    // Signature is the call site in RaptureShellModule::Update.
    [Signature("E8 ?? ?? ?? ?? C6 87 ?? ?? ?? ?? ?? 48 8B 5C 24 ?? 48 85 DB", DetourName = nameof(ProcessCommandWithContextDetour))]
    private Hook<ProcessCommandWithContext> ProcessCommandWithContextHook { get; init; }
    private delegate void ProcessCommandWithContext(
        ShellCommandModule* self, Utf8String* command, UIModule* uiModule, ShellCommandInterface.CommandContext* ctx, int evaluateTextCommandReturn);

    #pragma warning disable CS0618
    private Guid OverrideChannel
    {
        get;
        set
        {
            field = value;
            this.UpdateChat();
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            this.Plugin.Ipc?.BroadcastOverride(value);
        }
    }
#pragma warning restore CS0618

    internal GameFunctions(Plugin plugin, Client client) {
        this.Plugin = plugin;
        this.Plugin.GameInteropProvider.InitializeFromAttributes(this);
        
        this.Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreRefresh, "ChatLog", OnAddonChatLogPreRefresh);
        
        this.SetChatChannelHook!.Enable();
        this.ProcessCommandWithContextHook!.Enable();

        client.StatusChanged += OnClientStatusChanged;
    }

    public void Dispose()
    {
        this.Plugin.Client.StatusChanged -= OnClientStatusChanged;
        
        this.ProcessCommandWithContextHook.Dispose();
        this.SetChatChannelHook.Dispose();
        
        this.Plugin.AddonLifecycle.UnregisterListener(OnAddonChatLogPreRefresh);
    }

    internal void ResetOverride(bool save = false) {
        this.OverrideChannel = Guid.Empty;

        if (save)
        {
            this.Plugin.ConfigInfo.CurrentChannel = this.OverrideChannel;
            this.Plugin.SaveConfig();
        }
    }
    
    private readonly Lock _pronounModuleLock = new();

    internal byte[] ResolvePayloads(ReadOnlySpan<byte> input) {
        if (input.Length == 0) {
            return input.ToArray();
        }
        
        var str = input.ToUtf8String();
        Utf8String* result;
        lock (_pronounModuleLock)
        {
            var module = UIModule.Instance()->GetPronounModule();
            result = module->ProcessString(str, true);
        }
        var buf = result->AsSpan().ToArray();

        str->Dtor(true);

        return buf;
    }
    
    internal byte[] ProcessFixedMacros(ReadOnlySpan<byte> input, bool playSoundEffects)
    {
        if (input.Length == 0) {
            return input.ToArray();
        }
        
        var str = input.ToUtf8String();
        Utf8String* result;
        lock (_pronounModuleLock)
        {
            var pm = UIModule.Instance()->GetPronounModule();
            result = _processFixedMacros(pm, str, (byte)(playSoundEffects ? 1 : 0));
        }
        var buf = result->AsSpan().ToArray();

        str->Dtor(true);

        return buf;
    }
    
    private void UpdateChat() {
        var agent = UIModule.Instance()->GetAgentModule()->GetAgentByInternalId(AgentId.ChatLog);
        agent->VirtualTable->Update(agent, 0);
    }
    
    private void OnClientStatusChanged(object? sender, Client.State newStatus)
    {
        if (newStatus == Client.State.Connected && this.Plugin.ConfigInfo.CurrentChannel != Guid.Empty)
            this.OverrideChannel = this.Plugin.ConfigInfo.CurrentChannel;

        if (newStatus == Client.State.Disconnected)
            this.OverrideChannel = Guid.Empty;
    }

    private void OnAddonChatLogPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs refreshArgs || this.OverrideChannel == Guid.Empty)
            return;
        
        if (refreshArgs.AtkValueCount <= 37)
        {
            Plugin.Log.Error($"Expected refresh args for AddonChatLog to have more than 37 values, got {refreshArgs.AtkValueCount}.");
            return;
        }

        var span = new Span<AtkValue>((AtkValue*)refreshArgs.AtkValues, (int)refreshArgs.AtkValueCount);
        var outputType = this.Plugin.ConfigInfo.GetOutputChannel(this.OverrideChannel);
        var name = this.Plugin.ConfigInfo.GetFullName(this.OverrideChannel);
        uint chatEntryColor;

        if (outputType.IsLinkshell())
        {
            var rtm = UIModule.Instance()->GetRaptureTextModule();
            chatEntryColor = outputType switch
            {
                >= XivChatType.Ls1 and <= XivChatType.Ls8 => Unsafe.As<int, uint>(
                    ref rtm->GlobalParameters[(long)outputType + 1].IntValue),
                XivChatType.CrossLinkShell1 => Unsafe.As<int, uint>(ref rtm->GlobalParameters[34].IntValue),
                >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8 => Unsafe.As<int, uint>(
                    ref rtm->GlobalParameters[(long)outputType - 18].IntValue),
                _ => 0x12345678
            };
        }
        else
        {
            var channelColor = this.Plugin.ConfigInfo.GetUiColour(this.OverrideChannel);

            chatEntryColor = (this.Plugin.DataManager.GetExcelSheet<UIColor>().GetRowOrDefault(channelColor)?.Dark
                              ?? 0xFF5AD0FF) >> 8;
        }
            
        // 1 = displayed chat name
        // 2 = command prefix?
        // 4 = tab 3 name, 5 = tab 4 name
        // 8->?? = chat channel options in UI
        span[1].SetManagedString(Encoding.UTF8.GetBytes("\u3000 " + name + "\0"));
        span[37].UInt = chatEntryColor;
    }
    
    private void ProcessCommandWithContextDetour(
        ShellCommandModule* self, Utf8String* command, UIModule* uiModule, ShellCommandInterface.CommandContext* ctx, int evaluateTextCommandReturn)
    {
        try
        {
            if (this.ProcessCommandWithContextDetourInner(command, uiModule, evaluateTextCommandReturn))
                this.ProcessCommandWithContextHook.Original(self, command, uiModule, ctx, evaluateTextCommandReturn);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Error in message detour");
        }
    }

    private bool ProcessCommandWithContextDetourInner(Utf8String* command, UIModule* uiModule, int evaluateTextCommandReturn)
    {
        // 0 = EvaluateTextCommand success
        // -1 = is not command or is unknown command
        // -2 = is command but did not match expected command ID
        if (evaluateTextCommandReturn != -1)
            return true;
        
        // passthrough, we're in a /reply command sequence
        // /reply is handled by Client::UI::Shell::ShellCommandChatReply.ExecuteCommand, and works by
        //  - checking last player you sent a tell to
        //  - save old chat type
        //  - /tell player@world
        //  - message content
        //  - set old chat type again
        // If this sequence is not ongoing, TempChatType is set to -2, as done by the function that executes
        // the /tell command inside ShellCommandChatReply.ExecuteCommand.
        if (uiModule->GetRaptureShellModule()->TempChatType != -2)
            return true;
        
        var sendTo = this.OverrideChannel;
        var toSend = ReadOnlySpan<byte>.Empty;
        var messageSpan = command->AsSpan();
        
        if (messageSpan.Length > 1 && messageSpan[0] == '/') {
            sendTo = Guid.Empty;

            var commandOffset = -1;

            for (var i = 0; i < messageSpan.Length; i++)
            {
                if (messageSpan[i] == 0 || char.IsWhiteSpace((char)messageSpan[i]))
                {
                    commandOffset = i;
                    break;
                }
            }

            if (commandOffset == -1)
                commandOffset = messageSpan.Length;

            var commandName = Encoding.UTF8.GetString(messageSpan[..commandOffset]);

            if (this.Plugin.Commands.Registered.TryGetValue(commandName, out var id))
            {
                sendTo = id;

                if (commandOffset >= messageSpan.Length
                    || (toSend = messageSpan[(commandOffset + 1)..]).IsWhiteSpace())
                {
                    this.OverrideChannel = sendTo;
                    this.Plugin.ConfigInfo.CurrentChannel = sendTo;
                    this.Plugin.SaveConfig();
                    return false;
                }
            }
        }

        if (sendTo == Guid.Empty)
            return true;

        if (toSend.Length == 0)
            toSend = messageSpan;

        if (toSend.IsWhiteSpace())
            // don't send blank messages even to the original handler
            return false;

        this.Plugin.Commands.SendMessage(sendTo, toSend);
        return false;
    }

    private void SetChatChannelDetour(RaptureShellModule* module, uint channel) {
        // avoid potential stack overflow from recursion
        if (this.OverrideChannel != Guid.Empty) {
            this.ResetOverride(true);
        }

        this.SetChatChannelHook.Original(module, channel);
    }
}
