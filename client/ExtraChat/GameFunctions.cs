using System.Buffers;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
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

    // [Signature("E8 ?? ?? ?? ?? 48 8B D0 48 8D 4D ?? E8 ?? ?? ?? ?? 41 B4")]
    // private readonly delegate* unmanaged<PronounModule*, Utf8String*, Utf8String*> _step1;

    // Client::UI::Misc::PronounModule::???
    // https://github.com/Caraxi/SimpleTweaksPlugin/blob/main/Tweaks/Chat/ChatSoundsEverywhere.cs
    [Signature("E8 ?? ?? ?? ?? 44 88 74 24 ?? 4C 8D 45")]
    private readonly delegate* unmanaged<PronounModule*, Utf8String*, byte, Utf8String*> _step2;
    
    // [Signature("E8 ?? ?? ?? ?? 49 8B 45 00 49 8B CD FF 50 68")]
    // private readonly delegate* unmanaged<RaptureShellModule*, int, uint, nint, byte, nint> _setChatChannel;
    
    // Client::UI::Shell::RaptureShellModule::SetChatChannel
    [Signature("E8 ?? ?? ?? ?? 33 C0 EB ?? 85 D2", DetourName = nameof(SetChatChannelDetour))]
    private Hook<SetChatChannelDelegate> SetChatChannelHook { get; init; }
    private delegate void SetChatChannelDelegate(RaptureShellModule* module, uint channel);

    // Client::UI::Agent::AgentChatLog::???
    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 40 32 FF 48 8B 49 10", DetourName = nameof(ShouldDoNameLookupDetour))]
    private Hook<ShouldDoNameLookupDelegate> ShouldDoNameLookupHook { get; init; }
    private delegate byte ShouldDoNameLookupDelegate(AgentChatLog* agent);
    
    private Hook<ShellCommandModule.Delegates.ExecuteCommandInner> ExecuteCommandInnerHook { get; init; }
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
        
        this.ExecuteCommandInnerHook =
            Plugin.GameInteropProvider.HookFromAddress<ShellCommandModule.Delegates.ExecuteCommandInner>(
                (nint)ShellCommandModule.MemberFunctionPointers.ExecuteCommandInner, ExecuteCommandInnerDetour);
        this.ChangeChannelNameHook =
            Plugin.GameInteropProvider.HookFromAddress<AgentChatLog.Delegates.ChangeChannelName>(
                (nint)AgentChatLog.MemberFunctionPointers.ChangeChannelName, ChangeChannelNameDetour);
        
        this.ExecuteCommandInnerHook!.Enable();
        this.SetChatChannelHook!.Enable();
        this.ChangeChannelNameHook!.Enable();
        this.ShouldDoNameLookupHook!.Enable();

        if (this.Plugin.ConfigInfo.CurrentChannel != Guid.Empty)
        {
            this.OverrideChannel = this.Plugin.ConfigInfo.CurrentChannel;
            this.UpdateChat();
        }
    }

    public void Dispose() {
        this.ShouldDoNameLookupHook.Dispose();
        this.ChangeChannelNameHook.Dispose();
        this.SetChatChannelHook.Dispose();
        this.ExecuteCommandInnerHook.Dispose();
    }

    internal void ResetOverride() {
        this.OverrideChannel = Guid.Empty;
    }

    internal byte[] ResolvePayloads(ReadOnlySpan<byte> input) {
        if (input.Length == 0) {
            return input.ToArray();
        }

        var module = UIModule.Instance()->GetPronounModule();
        
        Utf8String* str;
        if (input[^1] != 0)
        {
            var replacement = ArrayPool<byte>.Shared.Rent(input.Length + 1);
            
            input.CopyTo(replacement);
            replacement[input.Length] = 0;
            str = Utf8String.FromSequence(replacement);
            
            ArrayPool<byte>.Shared.Return(replacement);
        }
        else
            str = Utf8String.FromSequence(input);

        var postStep1 = module->ProcessString(str, true);
        var postStep2 = this._step2(module, postStep1, 1);

        var buf = postStep2->AsSpan().ToArray();

        str->Dtor(true);

        // postStep1->Dtor();
        // IMemorySpace.Free(postStep1);

        // game dies if you do this
        // postStep2->Dtor();
        // IMemorySpace.Free(postStep2);

        return buf;
    }

    private void ExecuteCommandInnerDetour(ShellCommandModule* a1, Utf8String* message, UIModule* a3) {
        try {
            if (this.ExecuteCommandInnerDetourInner(message, a3)) {
                this.ExecuteCommandInnerHook.Original(a1, message, a3);
            }
        } catch (Exception ex) {
            Plugin.Log.Error(ex, "Error in message detour");
        }
    }

    // Can't really hook the debug command handler since that cleans up all the auto-translate payloads and such.
    /// <returns>true if the original function should be called</returns>
    private bool ExecuteCommandInnerDetourInner(Utf8String* message, UIModule* uiModule)
    {
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
        var messageSpan = message->AsSpan();
        
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

            var command = Encoding.UTF8.GetString(messageSpan[..commandOffset]);

            if (this.Plugin.Commands.Registered.TryGetValue(command, out var id))
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
