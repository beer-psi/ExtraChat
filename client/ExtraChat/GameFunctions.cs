using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace ExtraChat;

internal unsafe class GameFunctions : IDisposable {
    private Plugin Plugin { get; }

    // all this comes from 6.15: 751AF0

    [Signature("41 8B C1 4D 85 C0")]
    private readonly delegate* unmanaged<PronounModule*, Utf8String*, ulong, uint, Utf8String*> _resolvePayloads;

    // [Signature("E8 ?? ?? ?? ?? 48 8B D0 48 8D 4D ?? E8 ?? ?? ?? ?? 41 B4")]
    // private readonly delegate* unmanaged<PronounModule*, Utf8String*, Utf8String*> _step1;

    [Signature("E8 ?? ?? ?? ?? 44 88 74 24 ?? 4C 8D 45")]
    private readonly delegate* unmanaged<PronounModule*, Utf8String*, byte, Utf8String*> _step2;

    [Signature("E8 ?? ?? ?? ?? 49 8B 45 00 49 8B CD FF 50 68")]
    private readonly delegate* unmanaged<RaptureShellModule*, int, uint, nint, byte, nint> _setChatChannel;

    private delegate void SendMessageDelegate(nint a1, Utf8String* message, nint a3);

    private delegate void SetChatChannelDelegate(RaptureShellModule* module, uint channel);

    [Signature(
        "E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87",
        DetourName = nameof(SendMessageDetour)
    )]
    private Hook<SendMessageDelegate> SendMessageHook { get; init; }

    [Signature(
        "E8 ?? ?? ?? ?? 33 C0 EB ?? 85 D2",
        DetourName = nameof(SetChatChannelDetour)
    )]
    private Hook<SetChatChannelDelegate> SetChatChannelHook { get; init; }

    private delegate nint ChangeChannelNameDelegate(nint agent);

    [Signature(
        "E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4D B0 48 8B F8 E8 ?? ?? ?? ?? 41 8B D6",
        DetourName = nameof(ChangeChannelNameDetour)
    )]
    private Hook<ChangeChannelNameDelegate> ChangeChannelNameHook { get; init; }

    private delegate byte ShouldDoNameLookupDelegate(nint agent);

    [Signature(
        "48 89 5C 24 ?? 57 48 83 EC 20 48 8B D9 40 32 FF 48 8B 49 10",
        DetourName = nameof(ShouldDoNameLookupDetour)
    )]
    private Hook<ShouldDoNameLookupDelegate> ShouldDoNameLookupHook { get; init; }

    [Obsolete("Use OverrideChannel")]
    private Guid _overrideChannel = Guid.Empty;

    #pragma warning disable CS0618
    internal Guid OverrideChannel {
        get => this._overrideChannel;
        private set {
            this._overrideChannel = value;
            this.UpdateChat();
            this.Plugin.Ipc.BroadcastOverride();
        }
    }
    #pragma warning restore CS0618
    private bool _shouldForceNameLookup;

    internal GameFunctions(Plugin plugin) {
        this.Plugin = plugin;
        this.Plugin.GameInteropProvider.InitializeFromAttributes(this);

        this.SendMessageHook!.Enable();
        this.SetChatChannelHook!.Enable();
        this.ChangeChannelNameHook!.Enable();
        this.ShouldDoNameLookupHook!.Enable();
    }

    public void Dispose() {
        this.ShouldDoNameLookupHook.Dispose();
        this.ChangeChannelNameHook.Dispose();
        this.SetChatChannelHook.Dispose();
        this.SendMessageHook.Dispose();
    }

    internal void ResetOverride() {
        this.OverrideChannel = Guid.Empty;
    }

    internal byte[] ResolvePayloads(byte[] input) {
        if (input.Length == 0) {
            return input;
        }

        var module = UIModule.Instance()->GetPronounModule();
        var memorySpace = IMemorySpace.GetDefaultSpace();
        var str = memorySpace->Create<Utf8String>();

        if (input[^1] != 0) {
            var replacement = new byte[input.Length + 1];
            input.CopyTo(replacement, 0);
            replacement[^1] = 0;
            input = replacement;
        }

        fixed (byte* bytesPtr = input) {
            str->SetString(bytesPtr);
        }

        var postStep1 = this._resolvePayloads(module, str, 1, 0x3FF);
        var postStep2 = this._step2(module, postStep1, 1);

        var list = new List<byte>();
        for (var i = 0; i < postStep2->BufUsed && postStep2->StringPtr.Value[i] != 0; i++) {
            list.Add(postStep2->StringPtr.Value[i]);
        }

        str->Dtor();
        IMemorySpace.Free(str);

        // postStep1->Dtor();
        // IMemorySpace.Free(postStep1);

        // game dies if you do this
        // postStep2->Dtor();
        // IMemorySpace.Free(postStep2);

        return list.ToArray();
    }

    private void SendMessageDetour(nint a1, Utf8String* message, nint a3) {
        try {
            if (this.SendMessageDetourInner(message)) {
                this.SendMessageHook.Original(a1, message, a3);
            }
        } catch (Exception ex) {
            Plugin.Log.Error(ex, "Error in message detour");
        }
    }

    /// <returns>true if the original function should be called</returns>
    private bool SendMessageDetourInner(Utf8String* message) {
        var sendTo = this.OverrideChannel;

        byte[]? toSend = null;
        if (message->StringPtr.Value[0] == 2) {
            // check for autotranslate commands
            var payload = Payload.Decode(new BinaryReader(new UnmanagedMemoryStream(message->StringPtr, message->BufSize)));
            if (payload is AutoTranslatePayload at && at.Text[2..].StartsWith('/')) {
                // there are no AT entries for custom commands, so we can just
                // hand this back to the game
                return true;
            }
        }

        if (message->StringPtr.Value[0] == '/') {
            sendTo = Guid.Empty;
            var command = "";
            int i;
            for (i = 0; i < message->BufSize; i++) {
                var c = message->StringPtr.Value[i];
                if (c == 0 || char.IsWhiteSpace((char) c)) {
                    break;
                }

                command += (char) c;
            }

            if (this.Plugin.Commands.Registered.TryGetValue(command, out var id)) {
                var entireMessage = MemoryHelper.ReadRawNullTerminated((nint) message->StringPtr.Value);
                sendTo = id;
                if (entireMessage.Length - 1 >= i && char.IsWhiteSpace((char) entireMessage[i])) {
                    i += 1;
                }

                toSend = entireMessage[i..];

                var isBlank = toSend.Length == 0 || toSend.All(c => char.IsWhiteSpace((char) c));
                if (isBlank) {
                    this.OverrideChannel = id;
                    return false;
                }
            }
        }

        if (sendTo == Guid.Empty) {
            return true;
        }

        toSend ??= MemoryHelper.ReadRawNullTerminated((nint) message->StringPtr.Value);

        if (toSend.Length == 0 || toSend.All(c => char.IsWhiteSpace((char) c))) {
            // don't send blank messages even to the original handler
            return false;
        }

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
        }

        this.SetChatChannelHook.Original(module, channel);
    }

    private nint ChangeChannelNameDetour(nint agent) {
        var ret = this.ChangeChannelNameHook.Original(agent);

        if (this.OverrideChannel == Guid.Empty) {
            return ret;
        }

        var chatChannel = (Utf8String*) (agent + 0x48);
        var name = this.Plugin.ConfigInfo.GetFullName(this.OverrideChannel);
        fixed (byte* bytesPtr = Encoding.UTF8.GetBytes("\u3000 " + name + "\0")) {
            chatChannel->SetString(bytesPtr);
        }

        return (nint) chatChannel->StringPtr.Value;
    }

    private byte ShouldDoNameLookupDetour(nint agent) {
        if (this._shouldForceNameLookup) {
            this._shouldForceNameLookup = false;
            return 1;
        }

        return this.ShouldDoNameLookupHook.Original(agent);
    }
}
