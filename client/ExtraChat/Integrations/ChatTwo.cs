using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Ipc;

namespace ExtraChat.Integrations;

internal class ChatTwo : IDisposable {
    private Plugin Plugin { get; }

    private ICallGateSubscriber<string> Register { get; }
    private ICallGateSubscriber<string, object?> Unregister { get; }
    private ICallGateSubscriber<object?> Available { get; }
    private ICallGateSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?> Invoke { get; }

    private string? _id;

    internal ChatTwo(Plugin plugin) {
        this.Plugin = plugin;

        this.Register = this.Plugin.Interface.GetIpcSubscriber<string>("ChatTwo.Register");
        this.Unregister = this.Plugin.Interface.GetIpcSubscriber<string, object?>("ChatTwo.Unregister");
        this.Invoke = this.Plugin.Interface.GetIpcSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?>("ChatTwo.Invoke");
        this.Available = this.Plugin.Interface.GetIpcSubscriber<object?>("ChatTwo.Available");

        this.Available.Subscribe(this.DoRegister);
        try {
            this.DoRegister();
        } catch (Exception) {
            // try to register if chat 2 is already loaded
            // if not, just ignore exception
        }

        this.Invoke.Subscribe(this.Integration);
    }

    public void Dispose() {
        if (this._id != null) {
            try {
                this.Unregister.InvokeAction(this._id);
            } catch (Exception) {
                // no-op
            }

            this._id = null;
        }

        this.Invoke.Unsubscribe(this.Integration);
        this.Available.Unsubscribe(this.DoRegister);
    }

    private void DoRegister() {
        this._id = this.Register.InvokeFunc();
    }

    private void Integration(string id, PlayerPayload? sender, ulong contentId, Payload? payload, SeString? senderString, SeString? content) {
        if (id != this._id) {
            return;
        }

        if (payload is not PlayerPayload player) {
            return;
        }

        if (ImGui.Selectable("Invite to ExtraChat Linkshell")) {
            this.Plugin.PluginUi.InviteInfo = (player.PlayerName, (ushort) player.World.RowId);
        }
    }
}
