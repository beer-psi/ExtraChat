using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ExtraChat.Protocol;
using ExtraChat.Protocol.Channels;
using ExtraChat.Util;
using Lumina.Excel.Sheets;

namespace ExtraChat.Ui;

internal class ChannelList {
    private Plugin Plugin { get; }

    private readonly List<(string, List<World>)> _worlds;

    private string _createName = string.Empty;
    private Guid _selectedChannel = Guid.Empty;
    private string _inviteName = string.Empty;
    private ushort _inviteWorld;
    private string _rename = string.Empty;

    internal ChannelList(Plugin plugin) {
        this.Plugin = plugin;

        this._worlds = this.Plugin.DataManager.GetExcelSheet<World>()!
            .Where(row => row.IsPublic)
            .GroupBy(row => row.DataCenter.Value!)
            .Where(grouping => grouping.Key.Region.RowId != 0)
            .OrderBy(grouping => grouping.Key.Region.RowId)
            .ThenBy(grouping => grouping.Key.Name.ExtractText())
            .Select(grouping => (grouping.Key.Name.ExtractText(), grouping.OrderBy(row => row.Name.ExtractText()).ToList()))
            .ToList();
    }

    internal void Draw() {
        var anyChanged = false;

        ImGui.PushFont(UiBuilder.IconFont);

        var syncButton = ImGui.CalcTextSize(FontAwesomeIcon.Sync.ToIconString()).X
                         + ImGui.GetStyle().FramePadding.X * 2;
        // Plugin.Log.Info($"syncButton: {syncButton}");
        var addButton = ImGui.CalcTextSize(FontAwesomeIcon.Plus.ToIconString()).X
                        + ImGui.GetStyle().FramePadding.X * 2;
        // Plugin.Log.Info($"addButton: {addButton}");
        var syncOffset = ImGui.GetContentRegionAvail().X - syncButton;
        var addOffset = ImGui.GetContentRegionAvail().X - syncButton - ImGui.GetStyle().ItemSpacing.X - addButton;
        ImGui.SameLine(syncOffset);

        if (ImGui.Button(FontAwesomeIcon.Sync.ToIconString())) {
            Task.Run(async () => await this.Plugin.Client.ListAll());
        }

        anyChanged |= ImGuiUtil.Tutorial(this.Plugin, 1);

        ImGui.SameLine(addOffset);

        if (ImGui.Button(FontAwesomeIcon.Plus.ToIconString())) {
            ImGui.OpenPopup("create-channel-popup");
        }

        anyChanged |= ImGuiUtil.Tutorial(this.Plugin, 0);

        ImGui.PopFont();

        if (ImGui.BeginPopup("create-channel-popup")) {
            ImGui.TextUnformatted("Create a new ExtraChat Linkshell");

            ImGui.SetNextItemWidth(350 * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##linkshell-name", "Linkshell name", ref this._createName, 64);

            if (ImGui.IsWindowAppearing()) {
                ImGui.SetKeyboardFocusHere(-1);
            }

            if (!string.IsNullOrWhiteSpace(this._createName) && ImGui.Button("Create") && !this.Plugin.PluginUi.Busy) {
                this.Plugin.PluginUi.Busy = true;
                var name = this._createName;
                Task.Run(async () => await this.Plugin.Client.Create(name))
                    .ContinueWith(_ => this.Plugin.PluginUi.Busy = false);
                ImGui.CloseCurrentPopup();
                this._createName = string.Empty;
            }

            ImGui.EndPopup();
        }

        if (this.Plugin.Client.Channels.Count == 0 && this.Plugin.Client.InvitedChannels.Count == 0) {
            ImGui.TextUnformatted("You aren't in any linkshells yet. Try creating or joining one first.");
            goto AfterTable;
        }

        this.DrawTable(ref anyChanged);

        AfterTable:
        if (anyChanged) {
            this.Plugin.SaveConfig();
        }
    }

    private void DrawTable(ref bool anyChanged) {
        if (!ImGui.BeginTable("ecls-list", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit)) {
            return;
        }

        ImGui.TableSetupColumn("##channels", ImGuiTableColumnFlags.WidthFixed, 125 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##members", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        var channelOrder = this.Plugin.ConfigInfo.ChannelOrder.ToDictionary(
            entry => entry.Value,
            entry => entry.Key
        );

        var orderedChannels = this.Plugin.Client.Channels.Keys
            .OrderBy(id => channelOrder.ContainsKey(id) ? channelOrder[id] : int.MaxValue)
            .Concat(this.Plugin.Client.InvitedChannels.Keys);

        var childSize = new Vector2(
            -1,
            ImGui.GetContentRegionAvail().Y
            - ImGui.GetStyle().WindowPadding.Y
            - ImGui.GetStyle().ItemSpacing.Y
        );

        if (ImGui.TableSetColumnIndex(0)) {
            this.DrawChannelList(ref anyChanged, childSize, orderedChannels, channelOrder);
        }

        if (ImGui.TableSetColumnIndex(1) && this._selectedChannel != Guid.Empty) {
            if (ImGui.IsWindowAppearing()) {
                Task.Run(async () => await this.Plugin.Client.ListMembers(this._selectedChannel));
            }

            if (ImGui.BeginChild("channel-info", childSize)) {
                this.DrawInfo(ref anyChanged);
                ImGui.EndChild();
            }
        }

        ImGui.EndTable();
    }

    private void DrawChannelList(ref bool anyChanged, Vector2 childSize, IEnumerable<Guid> orderedChannels, Dictionary<Guid, int> channelOrder) {
        if (!ImGui.BeginChild("channel-list", childSize)) {
            return;
        }

        var first = true;
        foreach (var id in orderedChannels) {
            this.DrawChannel(ref anyChanged, channelOrder, id, ref first);
        }

        ImGui.EndChild();
    }

    private void DrawChannel(ref bool anyChanged, IReadOnlyDictionary<Guid, int> channelOrder, Guid id, ref bool first) {
        this.Plugin.ConfigInfo.Channels.TryGetValue(id, out var info);
        var name = info?.Name ?? "???";

        var order = "?";
        if (channelOrder.TryGetValue(id, out var o)) {
            order = (o + 1).ToString();
        }

        if (!this.Plugin.Client.ChannelRanks.TryGetValue(id, out var rank)) {
            rank = Rank.Member;
        }

        if (ImGui.Selectable($"{order}. {rank.Symbol()}{name}###{id}", this._selectedChannel == id)) {
            this._selectedChannel = id;

            Task.Run(async () => await this.Plugin.Client.ListMembers(id));
        }

        if (first) {
            first = false;
            anyChanged |= ImGuiUtil.Tutorial(this.Plugin, 2);
            anyChanged |= ImGuiUtil.Tutorial(this.Plugin, 3);
        }

        if (!ImGui.BeginPopupContextItem($"channel-list-{id}-context")) {
            return;
        }

        var invited = this.Plugin.Client.InvitedChannels.ContainsKey(id);
        if (invited) {
            if (ImGui.Selectable("Accept invite")) {
                Task.Run(async () => await this.Plugin.Client.Join(id));
            }

            if (ImGuiUtil.SelectableConfirm("Decline invite")) {
                Task.Run(async () => await this.Plugin.Client.Leave(id));
            }
        } else {
            if (ImGuiUtil.SelectableConfirm("Leave")) {
                Task.Run(async () => await this.Plugin.Client.Leave(id));
            }

            if (rank == Rank.Admin) {
                if (ImGuiUtil.SelectableConfirm("Disband")) {
                    Task.Run(async () => {
                        if (await this.Plugin.Client.Disband(id) is { } error) {
                            this.Plugin.ShowError($"Could not disband \"{name}\": {error}");
                        }
                    });
                }
            }

            if (rank == Rank.Admin && info != null && ImGui.BeginMenu($"Rename##{id}-rename")) {
                if (ImGui.IsWindowAppearing()) {
                    this._rename = string.Empty;
                }

                ImGui.SetNextItemWidth(350 * ImGuiHelpers.GlobalScale);
                ImGui.InputTextWithHint($"##{id}-rename-input", "New name", ref this._rename, 64);

                if (ImGui.IsWindowAppearing()) {
                    ImGui.SetKeyboardFocusHere(-1);
                }

                if (ImGui.Button($"Rename##{id}-rename-button") && !string.IsNullOrWhiteSpace(this._rename)) {
                    var newName = SecretBox.Encrypt(info.SharedSecret, Encoding.UTF8.GetBytes(this._rename));
                    Task.Run(async () => await this.Plugin.Client.UpdateToast(id, new UpdateKind.Name(newName)));
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu($"Invite##{id}-invite")) {
                if (ImGui.IsWindowAppearing()) {
                    this._inviteName = string.Empty;
                    this._inviteWorld = 0;
                }

                ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
                ImGui.InputTextWithHint("##invite-name", "Name", ref this._inviteName, 32);

                ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
                var preview = this._inviteWorld == 0 ? "World" : WorldUtil.WorldName(this._inviteWorld);
                if (ImGui.BeginCombo("##invite-world", preview)) {
                    foreach (var (dc, worlds) in this._worlds) {
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled]);
                        ImGui.TextUnformatted(dc);
                        ImGui.PopStyleColor();
                        ImGui.Separator();

                        foreach (var world in worlds) {
                            if (ImGui.Selectable(world.Name.ExtractText(), this._inviteWorld == world.RowId)) {
                                this._inviteWorld = (ushort) world.RowId;
                            }
                        }

                        ImGui.Spacing();
                    }

                    ImGui.EndCombo();
                }

                if (ImGui.Button($"Invite##{id}-invite-button") && !string.IsNullOrWhiteSpace(this._inviteName) && this._inviteWorld != 0) {
                    var inviteName = this._inviteName;
                    var inviteWorld = this._inviteWorld;

                    Task.Run(async () => await this.Plugin.Client.InviteToast(inviteName, inviteWorld, id));
                }

                ImGui.EndMenu();
            }

            ImGui.Separator();

            if (ImGui.BeginMenu("Change number")) {
                ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
                channelOrder.TryGetValue(id, out var refOrder);
                var old = refOrder;
                refOrder += 1;
                if (ImGui.InputInt($"##{id}-order", ref refOrder)) {
                    refOrder = Math.Max(1, refOrder) - 1;

                    if (this.Plugin.ConfigInfo.ChannelOrder.TryGetValue(refOrder, out var other) && other != id) {
                        // another channel already has this number, so swap
                        this.Plugin.ConfigInfo.ChannelOrder[old] = other;
                    } else {
                        this.Plugin.ConfigInfo.ChannelOrder.Remove(old);
                    }

                    this.Plugin.ConfigInfo.ChannelOrder[refOrder] = id;
                    this.Plugin.SaveConfig();
                    this.Plugin.Commands.ReregisterAll();
                }

                ImGui.EndMenu();
            }

            if (info == null) {
                if (ImGui.Selectable("Request secrets")) {
                    Task.Run(async () => await this.Plugin.Client.RequestSecrets(id));
                }
            }
        }

        ImGui.EndPopup();
    }

    private void DrawInfo(ref bool anyChanged) {
        if (!this.Plugin.Client.TryGetChannel(this._selectedChannel, out var channel)) {
            return;
        }

        Vector4 disabledColour;
        unsafe {
            disabledColour = *ImGui.GetStyleColorVec4(ImGuiCol.TextDisabled);
        }

        if (!this.Plugin.Client.ChannelRanks.TryGetValue(this._selectedChannel, out var rank)) {
            rank = Rank.Member;
        }

        var first = true;
        foreach (var member in channel.Members) {
            if (!member.Online) {
                ImGui.PushStyleColor(ImGuiCol.Text, disabledColour);
            }

            try {
                ImGui.TextUnformatted($"{member.Rank.Symbol()}{member.Name}{PluginUi.CrossWorld}{WorldUtil.WorldName(member.World)}");
            } finally {
                if (!member.Online) {
                    ImGui.PopStyleColor();
                }
            }

            this.DrawMemberContextMenu(member, rank);

            if (!first) {
                continue;
            }

            first = false;
            anyChanged |= ImGuiUtil.Tutorial(this.Plugin, 4);
            anyChanged |= ImGuiUtil.Tutorial(this.Plugin, 5);
        }
    }

    private void DrawMemberContextMenu(Member member, Rank rank) {
        if (!ImGui.BeginPopupContextItem($"{this._selectedChannel}-{member.Name}@{member.World}-context")) {
            return;
        }

        var cursor = ImGui.GetCursorPos();

        if (rank == Rank.Admin) {
            if (member.Rank is not (Rank.Admin or Rank.Invited)) {
                if (ImGuiUtil.SelectableConfirm("Promote to admin", tooltip: "This will demote you to moderator.")) {
                    Task.Run(async () => await this.Plugin.Client.Promote(this._selectedChannel, member.Name, member.World, Rank.Admin));
                }
            }

            if (member.Rank == Rank.Moderator && ImGuiUtil.SelectableConfirm("Demote")) {
                Task.Run(async () => await this.Plugin.Client.Promote(this._selectedChannel, member.Name, member.World, Rank.Member));
            }

            if (member.Rank == Rank.Member && ImGuiUtil.SelectableConfirm("Promote to moderator")) {
                Task.Run(async () => await this.Plugin.Client.Promote(this._selectedChannel, member.Name, member.World, Rank.Moderator));
            }
        }

        if (rank >= Rank.Moderator) {
            var canKick = member.Rank < rank && member.Rank != Rank.Invited;
            if (canKick && ImGuiUtil.SelectableConfirm("Kick")) {
                Task.Run(async () => {
                    if (await this.Plugin.Client.Kick(this._selectedChannel, member.Name, member.World) is { } error) {
                        this.Plugin.ShowError($"Could not kick {member.Name}: {error}");
                    }
                });
            }

            if (member.Rank == Rank.Invited && ImGuiUtil.SelectableConfirm("Cancel invite")) {
                Task.Run(async () => await this.Plugin.Client.Kick(this._selectedChannel, member.Name, member.World));
            }
        }

        if (rank == Rank.Invited && member.Rank == Rank.Invited) {
            if (member.Name == this.Plugin.LocalPlayer?.Name.TextValue && member.World == this.Plugin.LocalPlayer?.HomeWorld.RowId) {
                if (ImGui.Selectable("Accept invite")) {
                    Task.Run(async () => await this.Plugin.Client.Join(this._selectedChannel));
                }

                if (ImGuiUtil.SelectableConfirm("Decline invite")) {
                    Task.Run(async () => await this.Plugin.Client.Leave(this._selectedChannel));
                }
            }
        }

        if (cursor == ImGui.GetCursorPos()) {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted("No options available");
            ImGui.PopStyleColor();
        }

        ImGui.EndPopup();
    }
}
