using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using ExtraChat.Protocol.Channels;
using ExtraChat.Util;
using Lumina.Excel.Sheets;
using Channel = System.Threading.Channels.Channel;

namespace ExtraChat.Ui;

internal class PluginUi : IDisposable {
    internal const string CrossWorld = "\ue05d";

    private Plugin Plugin { get; }
    private ChannelList ChannelList { get; }

    internal bool Visible;

    private readonly List<(uint Id, Vector4 Abgr)> _uiColours;

    internal PluginUi(Plugin plugin) {
        this.Plugin = plugin;
        this.ChannelList = new ChannelList(this.Plugin);

        this._uiColours = this.Plugin.DataManager.GetExcelSheet<UIColor>()!
            .Where(row => row.Dark is not (0 or 0x000000FF))
            .Select(row => (row.RowId, row.Dark, ColourUtil.Step(row.Dark)))
            .GroupBy(row => row.Dark)
            .Select(grouping => grouping.First())
            .OrderBy(row => row.Item3.Item1)
            .ThenBy(row => row.Item3.Item2)
            .ThenBy(row => row.Item3.Item3)
            .Select(row => (row.RowId, ImGui.ColorConvertU32ToFloat4(ColourUtil.RgbaToAbgr(row.Dark))))
            .ToList();

        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
        this.Plugin.Interface.UiBuilder.OpenConfigUi += this.OpenConfigUi;

        if (this.Plugin.Interface.Reason == PluginLoadReason.Installer && this.Plugin.ConfigInfo.Key == null) {
            this.Visible = true;
        }
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        this.Plugin.Interface.UiBuilder.Draw -= this.Draw;
    }

    private void OpenConfigUi() {
        this.Visible ^= true;
    }

    internal (string, ushort)? InviteInfo;

    internal volatile bool Busy;
    private string? _challenge;
    private Guid? _inviteId;
    private readonly Channel<string?> _challengeChannel = Channel.CreateUnbounded<string?>();

    private void Draw() {
        if (this._challengeChannel.Reader.TryRead(out var challenge)) {
            this._challenge = challenge;
        }

        this.DrawConfigWindow();
        this.DrawInviteWindow();
    }

    private void DrawConfigWindow() {
        if (!this.Visible) {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(500, 325) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);

        if (!ImGui.Begin(Plugin.Name, ref this.Visible)) {
            ImGui.End();
            return;
        }

        if (!this.Plugin.ClientState.IsLoggedIn) {
            ImGui.TextUnformatted("Please log in to a character.");
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("tabs")) {
            if (ImGui.BeginTabItem("Linkshells")) {
                var status = this.Plugin.Client.Status;
                ImGui.TextUnformatted($"Status: {status}");
                ImGui.SameLine();
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Wifi, tooltip: "Reconnect") && !this.Busy) {
                    this.Busy = true;

                    Task.Run(async () => {
                        this.Plugin.Client.StopLoop();
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        this.Plugin.Client.StartLoop();
                        this.Busy = false;
                    });
                }

                switch (status) {
                    case Client.State.Connected:
                        this.ChannelList.Draw();
                        break;
                    case Client.State.NotAuthenticated:
                    case Client.State.RetrievingChallenge:
                    case Client.State.WaitingForVerification:
                    case Client.State.Verifying:
                        this.DrawRegistrationPanel();
                        break;
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings")) {
                this.DrawSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Help")) {
                this.DrawHelp();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void DrawHelp() {
        ImGui.PushTextWrapPos();

        if (ImGui.Button("Reset tutorial")) {
            this.Plugin.ConfigInfo.TutorialStep = 0;
            this.Plugin.SaveConfig();
        }

        ImGui.PopTextWrapPos();
    }

    private void DrawSettings() {
        var anyChanged = false;

        if (ImGui.BeginTabBar("settings-tabs")) {
            if (ImGui.BeginTabItem("General")) {
                this.DrawSettingsGeneral(ref anyChanged);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Linkshells")) {
                this.DrawSettingsLinkshells(ref anyChanged);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (anyChanged) {
            this.Plugin.SaveConfig();
            this.Plugin.Ipc.BroadcastChannelCommandColours();
        }
    }

    private void DrawSettingsGeneral(ref bool anyChanged) {
        anyChanged |= ImGui.Checkbox("Use native toasts", ref this.Plugin.Config.UseNativeToasts);
        anyChanged |= ImGui.Checkbox("Add invite context menu item", ref this.Plugin.Config.ShowContextMenuItem);
        // ImGui.Spacing();
        //
        // ImGui.TextUnformatted("Default channel");
        // ImGui.SetNextItemWidth(-1);
        // if (ImGui.BeginCombo("##default-channel", $"{this.Plugin.Config.DefaultChannel}")) {
        //     foreach (var channel in Enum.GetValues<XivChatType>()) {
        //         if (ImGui.Selectable($"{channel}", this.Plugin.Config.DefaultChannel == channel)) {
        //             this.Plugin.Config.DefaultChannel = channel;
        //             anyChanged = true;
        //         }
        //     }
        //
        //     ImGui.EndCombo();
        // }

        if (this.Plugin.LocalPlayer is { } player) {
            if (ImGui.TreeNodeEx($"Settings for {player.Name}{CrossWorld}{player.HomeWorld.Value.Name}")) {
                if (ImGui.Checkbox("Allow receiving invites", ref this.Plugin.ConfigInfo.AllowInvites)) {
                    anyChanged = true;
                    Task.Run(async () => await this.Plugin.Client.AllowInvitesToast(this.Plugin.ConfigInfo.AllowInvites));
                }

                ImGui.TreePop();
            }
        }

        if (this.Plugin.Client.Status == Client.State.Connected && ImGui.TreeNodeEx("Delete account")) {
            ImGui.PushTextWrapPos();

            if (this.Plugin.Client.Channels.Count > 0) {
                ImGui.TextUnformatted("You must leave or disband all ExtraChat linkshells you are currently in before you can delete your account.");
            } else {
                ImGui.TextUnformatted("Clicking the button below will permanently and irreversibly delete your account from ExtraChat's servers.");

                if (ImGui.Button("Delete account##actual-delete")) {
                    Task.Run(async () => await this.Plugin.Client.DeleteAccountToast());
                }
            }

            ImGui.PopTextWrapPos();

            ImGui.TreePop();
        }
    }

    private void DrawSettingsLinkshells(ref bool anyChanged) {
        var channelOrder = this.Plugin.ConfigInfo.ChannelOrder.ToDictionary(
            entry => entry.Value,
            entry => entry.Key
        );

        var orderedChannels = this.Plugin.Client.Channels.Keys
            .OrderBy(id => channelOrder.ContainsKey(id) ? channelOrder[id] : int.MaxValue)
            .Concat(this.Plugin.Client.InvitedChannels.Keys);

        foreach (var id in orderedChannels) {
            var name = this.Plugin.ConfigInfo.GetName(id);

            if (ImGui.CollapsingHeader($"{name}###{id}-settings")) {
                ImGui.PushID($"{id}-settings");

                ImGui.TextUnformatted("Number");
                channelOrder.TryGetValue(id, out var refOrder);
                var old = refOrder;
                refOrder += 1;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##order", ref refOrder)) {
                    refOrder = Math.Max(1, refOrder) - 1;

                    if (this.Plugin.ConfigInfo.ChannelOrder.TryGetValue(refOrder, out var other) && other != id) {
                        // another channel already has this number, so swap
                        this.Plugin.ConfigInfo.ChannelOrder[old] = other;
                    } else {
                        this.Plugin.ConfigInfo.ChannelOrder.Remove(old);
                    }

                    this.Plugin.ConfigInfo.ChannelOrder[refOrder] = id;
                    anyChanged = true;
                    this.Plugin.Commands.ReregisterAll();
                }

                ImGui.Spacing();

                if (ImGuiUtil.IconButton(FontAwesomeIcon.Undo, "colour-reset", "Reset")) {
                    anyChanged = true;
                    this.Plugin.ConfigInfo.ChannelColors.Remove(id);
                }

                ImGui.SameLine();

                var colourKey = this.Plugin.ConfigInfo.GetUiColour(id);
                var colour = this.Plugin.DataManager.GetExcelSheet<UIColor>()!.GetRowOrDefault(colourKey)?.Dark ?? 0xff5ad0ff;
                var vec = ImGui.ColorConvertU32ToFloat4(ColourUtil.RgbaToAbgr(colour));

                const string colourPickerId = "linkshell-colour-picker";

                if (ImGui.ColorButton("Linkshell colour", vec, ImGuiColorEditFlags.NoTooltip)) {
                    ImGui.OpenPopup(colourPickerId);
                }

                ImGui.SameLine();

                ImGui.TextUnformatted("Linkshell colour");

                if (ImGui.BeginPopup(colourPickerId)) {
                    var i = 0;

                    foreach (var (uiColour, fg) in this._uiColours) {
                        if (ImGui.ColorButton($"Colour {uiColour}", fg, ImGuiColorEditFlags.NoTooltip)) {
                            this.Plugin.ConfigInfo.ChannelColors[id] = (ushort) uiColour;
                            anyChanged = true;
                            ImGui.CloseCurrentPopup();
                        }

                        if (i >= 11) {
                            i = 0;
                        } else {
                            ImGui.SameLine();
                            i += 1;
                        }
                    }

                    ImGui.EndPopup();
                }

                ImGui.Spacing();

                var hint = $"ECLS{refOrder}";
                if (!this.Plugin.ConfigInfo.ChannelMarkers.TryGetValue(id, out var marker)) {
                    marker = string.Empty;
                }

                ImGui.TextUnformatted("Chat marker");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##marker", hint, ref marker, 16)) {
                    anyChanged = true;
                    if (string.IsNullOrWhiteSpace(marker)) {
                        this.Plugin.ConfigInfo.ChannelMarkers.Remove(id);
                    } else {
                        this.Plugin.ConfigInfo.ChannelMarkers[id] = marker;
                    }
                }

                // ImGui.Spacing();
                //
                // ImGui.TextUnformatted("Output channel");
                // ImGui.SetNextItemWidth(-1);
                //
                // var contained = this.Plugin.ConfigInfo.ChannelChannels.TryGetValue(id, out var output);
                // var preview = contained ? $"{output}" : "Default";
                //
                // if (ImGui.BeginCombo("##output-channel", preview)) {
                //     if (ImGui.Selectable("Default", !contained)) {
                //         this.Plugin.ConfigInfo.ChannelChannels.Remove(id);
                //         anyChanged = true;
                //     }
                //
                //     foreach (var channel in Enum.GetValues<XivChatType>()) {
                //         if (ImGui.Selectable($"{channel}", contained && output == channel)) {
                //             this.Plugin.ConfigInfo.ChannelChannels[id] = channel;
                //             anyChanged = true;
                //         }
                //     }
                //
                //     ImGui.EndCombo();
                // }

                ImGui.PopID();
            }
        }
    }

    private void DrawInviteWindow() {
        if (this.InviteInfo == null) {
            return;
        }

        var (name, world) = this.InviteInfo.Value;

        var open = true;
        if (!ImGui.Begin($"Invite: {name}###ec-linkshell-invite", ref open, ImGuiWindowFlags.AlwaysAutoResize)) {
            if (!open) {
                this.InviteInfo = null;
            }

            ImGui.End();
            return;
        }

        if (!open) {
            this.InviteInfo = null;
        }

        if (ImGui.IsWindowAppearing()) {
            ImGui.SetWindowPos(ImGui.GetMousePos());
        }

        var preview = this._inviteId == null ? "Choose a linkshell" : "???";
        if (this._inviteId != null && this.Plugin.ConfigInfo.Channels.TryGetValue(this._inviteId.Value, out var selectedInfo)) {
            preview = selectedInfo.Name;
        }

        if (ImGui.BeginCombo("##ec-linkshell-invite-linkshell", preview)) {
            foreach (var (id, _) in this.Plugin.Client.Channels) {
                if (!this.Plugin.Client.ChannelRanks.TryGetValue(id, out var rank) || rank < Rank.Moderator) {
                    continue;
                }

                if (!this.Plugin.ConfigInfo.Channels.TryGetValue(id, out var info)) {
                    continue;
                }

                if (ImGui.Selectable($"{info.Name}##{id}", id == this._inviteId)) {
                    this._inviteId = id;
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("Invite") && this._inviteId != null) {
            var id = this._inviteId.Value;
            this._inviteId = null;

            Task.Run(async () => await this.Plugin.Client.InviteToast(name, world, id));
            this.InviteInfo = null;
        }

        ImGui.End();
    }

    private void DrawRegistrationPanel() {
        if (this.Plugin.LocalPlayer is not { } player) {
            return;
        }

        var state = this.Plugin.Client.Status;
        if (state == Client.State.NotAuthenticated) {
            if (this.Plugin.ConfigInfo.Key != null) {
                ImGui.TextUnformatted("Please wait...");
            } else {
                if (ImGui.Button($"Register {player.Name}") && !this.Busy) {
                    this.Busy = true;
                    Task.Run(async () => {
                        var challenge = await this.Plugin.Client.GetChallenge();
                        await this._challengeChannel.Writer.WriteAsync(challenge);
                    }).ContinueWith(_ => this.Busy = false);
                }

                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted("ExtraChat is a third-party service that allows for functionally unlimited extra linkshells that work across data centres.");
                ImGui.TextUnformatted("In order to use ExtraChat, characters must be registered and verified using their Lodestone profile.");
                ImGui.TextUnformatted("ExtraChat stores your character's name, home world, and Lodestone ID, as well as what ExtraChat linkshells your character is a part of and has been invited to.");
                ImGui.TextUnformatted("Messages and linkshell names are end-to-end encrypted; the server cannot decrypt them and does not store messages.");
                ImGui.TextUnformatted("In the event of a legal subpoena, ExtraChat will provide any information available to the legal system.");
                ImGui.PopTextWrapPos();
            }
        }

        if (state == Client.State.RetrievingChallenge) {
            ImGui.TextUnformatted("Waiting...");
        }

        if (state == Client.State.WaitingForVerification) {
            ImGui.PushTextWrapPos();
            if (this._challenge == null) {
                ImGui.TextUnformatted("Waiting for verification but no challenge present. This is a bug.");
            } else {
                ImGui.TextUnformatted("Copy the challenge below and save it in your Lodestone profile. After saving, click the button below to verify. After successfully verifying, you can delete the challenge from your profile if desired.");

                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##challenge", ref this._challenge, this._challenge.Length, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.ReadOnly);

                if (ImGui.Button("Copy")) {
                    ImGui.SetClipboardText(this._challenge);
                }

                ImGui.SameLine();

                if (ImGui.Button("Open profile")) {
                    var region = this.Plugin.LocalPlayer?.HomeWorld.Value.DataCenter.Value.Region.RowId ?? 2;
                    var sub = this.Plugin.ClientState.ClientLanguage switch {
                        ClientLanguage.Japanese => "jp",
                        ClientLanguage.English when region != 2 => "eu",
                        ClientLanguage.English => "na",
                        ClientLanguage.German => "de",
                        ClientLanguage.French => "fr",
                        _ => "na",
                    };

                    Process.Start(new ProcessStartInfo {
                        FileName = $"https://{sub}.finalfantasyxiv.com/lodestone/my/setting/profile/",
                        UseShellExecute = true,
                    });
                }

                ImGui.SameLine();

                if (ImGui.Button("Verify") && !this.Busy) {
                    this.Busy = true;
                    Task.Run(async () => {
                        var key = await this.Plugin.Client.Register();
                        this.Plugin.ConfigInfo.Key = key;
                        this.Plugin.SaveConfig();
                        await this.Plugin.Client.AuthenticateAndList();
                    }).ContinueWith(_ => this.Busy = false);
                }
            }

            ImGui.PopTextWrapPos();
        }
    }
}
