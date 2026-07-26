
using System.Text;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.Shell;
using InteropGenerator.Runtime;

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

    // Client::UI::Agent::AgentChatLog::???
    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 40 32 FF 48 8B 49 10", DetourName = nameof(ShouldDoNameLookupDetour))]
    private Hook<ShouldDoNameLookupDelegate> ShouldDoNameLookupHook { get; init; }
    private delegate byte ShouldDoNameLookupDelegate(AgentChatLog* agent);
    
    // Component::Shell::ShellCommandModule::??
    // Called by ShellCommandModule::ExecuteCommandInner and RaptureShellModule::Update on every message/macro line
    // after Component::Shell::ShellCommandModule::EvaluateTextCommand is called.
    // Signature is the call site in RaptureShellModule::Update.
    [Signature("E8 ?? ?? ?? ?? C6 87 ?? ?? ?? ?? ?? 48 8B 5C 24 ?? 48 85 DB", DetourName = nameof(ProcessCommandWithContextDetour))]
    private Hook<ProcessCommandWithContext> ProcessCommandWithContextHook { get; init; }
    private delegate void ProcessCommandWithContext(
        ShellCommandModule* self, Utf8String* command, UIModule* uiModule, ShellCommandInterface.CommandContext* ctx, int evaluateTextCommandReturn);
    
    private Hook<AgentChatLog.Delegates.ChangeChannelName> ChangeChannelNameHook { get; init; }

    #pragma warning disable CS0618
    internal Guid OverrideChannel
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
    
    private bool _shouldForceNameLookup;

    internal GameFunctions(Plugin plugin) {
        this.Plugin = plugin;
        this.Plugin.GameInteropProvider.InitializeFromAttributes(this);
        
        this.ChangeChannelNameHook =
            Plugin.GameInteropProvider.HookFromAddress<AgentChatLog.Delegates.ChangeChannelName>(
                (nint)AgentChatLog.MemberFunctionPointers.ChangeChannelName, ChangeChannelNameDetour);
        
        this.SetChatChannelHook!.Enable();
        this.ChangeChannelNameHook!.Enable();
        this.ShouldDoNameLookupHook!.Enable();
        this.ProcessCommandWithContextHook!.Enable();

        if (this.Plugin.ConfigInfo.CurrentChannel != Guid.Empty)
        {
            this.OverrideChannel = this.Plugin.ConfigInfo.CurrentChannel;
            this.UpdateChat();
        }
    }

    public void Dispose() {
        this.ProcessCommandWithContextHook.Dispose();
        this.ShouldDoNameLookupHook.Dispose();
        this.ChangeChannelNameHook.Dispose();
        this.SetChatChannelHook.Dispose();
    }

    internal void ResetOverride() {
        this.OverrideChannel = Guid.Empty;
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
    
    private void UpdateChat() {
        this._shouldForceNameLookup = true;
        var agent = UIModule.Instance()->GetAgentModule()->GetAgentByInternalId(AgentId.ChatLog);
        agent->VirtualTable->Update(agent, 0);
    }

    private void SetChatChannelDetour(RaptureShellModule* module, uint channel) {
        // avoid potential stack overflow from recursion
        if (this.OverrideChannel != Guid.Empty) {
            this.OverrideChannel = Guid.Empty;
            this.Plugin.ConfigInfo.CurrentChannel = this.OverrideChannel;
            this.Plugin.SaveConfig();
        }

        this.SetChatChannelHook.Original(module, channel);
    }

    private CStringPointer ChangeChannelNameDetour(AgentChatLog* agent) {
        var ret = this.ChangeChannelNameHook.Original(agent);

        if (this.OverrideChannel == Guid.Empty) {
            return ret;
        }
        
        var name = this.Plugin.ConfigInfo.GetFullName(this.OverrideChannel);
        fixed (byte* bytesPtr = Encoding.UTF8.GetBytes("\u3000 " + name + "\0")) {
            agent->ChannelLabel.SetString(bytesPtr);
        }

        return agent->ChannelLabel.StringPtr;
    }

    private byte ShouldDoNameLookupDetour(AgentChatLog* agent) {
        if (this._shouldForceNameLookup) {
            this._shouldForceNameLookup = false;
            return 1;
        }

        return this.ShouldDoNameLookupHook.Original(agent);
    }
}
