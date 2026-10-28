using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ExtraChat.Protocol.Channels;

namespace ExtraChat.Ui;

public class InviteWindow(Plugin plugin) 
    : Window("Invite###ec-linkshell-invite", ImGuiWindowFlags.AlwaysAutoResize)
{
    internal string? Name;
    internal ushort World;
    private Guid _inviteId = Guid.Empty;

    public override bool DrawConditions() => !string.IsNullOrEmpty(Name) && World != 0;

    public override void OnOpen()
    {
        if (!string.IsNullOrEmpty(Name))
            WindowName = $"Invite: {Name}###ec-linkshell-invite";
        
        ImGui.SetWindowPos(ImGui.GetMousePos());
    }

    public override void Draw()
    {
        var preview = _inviteId == Guid.Empty ? "Choose a linkshell" : "???";

        if (_inviteId != Guid.Empty && plugin.ConfigInfo.Channels.TryGetValue(_inviteId, out var selectedInfo))
        {
            preview = selectedInfo.Name;
        }

        using (var combo = ImRaii.Combo("##ec-linkshell-invite-linkshell", preview))
        {
            if (combo.Success)
            {
                foreach (var (id, _) in plugin.Client.Channels) {
                    if (!plugin.Client.ChannelRanks.TryGetValue(id, out var rank) || rank < Rank.Moderator) {
                        continue;
                    }

                    if (!plugin.ConfigInfo.Channels.TryGetValue(id, out var info)) {
                        continue;
                    }

                    if (ImGui.Selectable($"{info.Name}##{id}", id == _inviteId)) {
                        _inviteId = id;
                    }
                }
            }
        }

        using (ImRaii.Disabled(_inviteId == Guid.Empty))
        {
            if (ImGui.Button("Invite"))
            {
                Task.Run(async () => await plugin.Client.InviteToast(Name!, World, _inviteId));
                IsOpen = false;
            }
        }
    }
    
    public override void OnClose()
    {
        Name = null;
        World = 0;
        _inviteId = Guid.Empty;
    }
}