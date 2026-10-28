using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ExtraChat.Util;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace ExtraChat.Ui;

public class ConfigWindow : Window
{
    internal const string CrossWorld = "\ue05d";
    
    private readonly Plugin _plugin;
    private readonly ChannelList _channelList;
    private readonly List<(uint Id, Vector4 Abgr)> _uiColours;
    private readonly NotificationSound[] _notificationSounds = Enum.GetValues<NotificationSound>();
    private readonly string[] _notificationSoundNames = Enum.GetValues<NotificationSound>()
        .Select(s => s == NotificationSound.None ? "Disabled" : $"Type {(uint)s}")
        .ToArray();

    internal volatile bool Busy;
    private string _getChallengeError = string.Empty;
    
    private string? _challenge;
    private readonly Channel<string?> _challengeChannel = Channel.CreateUnbounded<string?>();
    
    public ConfigWindow(Plugin plugin)
        : base(Plugin.Name, ImGuiWindowFlags.None)
    {
        _plugin = plugin;
        _channelList = new ChannelList(_plugin);

        _uiColours = _plugin.DataManager.GetExcelSheet<UIColor>()!
            .Where(row => row.Dark is not (0 or 0x000000FF))
            .Select(row => (row.RowId, row.Dark, ColourUtil.Step(row.Dark)))
            .GroupBy(row => row.Dark)
            .Select(grouping => grouping.First())
            .OrderBy(row => row.Item3.Item1)
            .ThenBy(row => row.Item3.Item2)
            .ThenBy(row => row.Item3.Item3)
            .Select(row => (row.RowId, ImGui.ColorConvertU32ToFloat4(ColourUtil.RgbaToAbgr(row.Dark))))
            .ToList();
        
        Size = new Vector2(500, 325) * ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw()
    {
        if (_challengeChannel.Reader.TryRead(out var challenge))
            _challenge = challenge;
    }

    public override void Draw()
    {
        if (!_plugin.ClientState.IsLoggedIn)
        {
            ImGui.TextUnformatted("Please log in to a character.");
            return;
        }

        using var tabs = ImRaii.TabBar("tabs");

        if (!tabs.Success)
            return;

        using (var lsTabItem = ImRaii.TabItem("Linkshells"))
        {
            if (lsTabItem.Success)
            {
                var status = _plugin.Client.Status;
                
                ImGui.TextUnformatted($"Status: {status}");
                ImGui.SameLine();
                
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Wifi, tooltip: "Reconnect") && !Busy) {
                    Busy = true;

                    Task.Run(async () => {
                        _plugin.Client.StopLoop();
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        _plugin.Client.StartLoop();
                        Busy = false;
                    });
                }

                switch (status) {
                    case Client.State.Connected:
                        _channelList.Draw();
                        break;
                    case Client.State.NotAuthenticated:
                    case Client.State.RetrievingChallenge:
                    case Client.State.WaitingForVerification:
                    case Client.State.Verifying:
                        DrawRegistrationPanel();
                        break;
                    default:
                        break;
                }
            }
        }

        using (var settingsTabItem = ImRaii.TabItem("Settings"))
        {
            if (settingsTabItem.Success)
                DrawSettings();
        }

        using (var helpTabItem = ImRaii.TabItem("Help"))
        {
            if (helpTabItem.Success)
                DrawHelp();
        }
    }
    
    private void DrawRegistrationPanel()
    {
        var player = _plugin.PlayerState;
        var state = _plugin.Client.Status;
        
        if (state == Client.State.NotAuthenticated) {
            if (_plugin.ConfigInfo.Key != null) {
                ImGui.TextUnformatted("Please wait...");
            } else {
                if (ImGui.Button($"Register {player.CharacterName}") && !Busy) {
                    Busy = true;
                    _getChallengeError = string.Empty;
                    
                    Task.Run(async () =>
                    {
                        string? challenge;
                        try
                        {
                            challenge = await _plugin.Client.GetChallenge();
                        }
                        catch (Exception e)
                        {
                            _getChallengeError = e.Message;
                            throw;
                        }
                        
                        await _challengeChannel.Writer.WriteAsync(challenge);
                    }).ContinueWith(_ => Busy = false);
                }

                if (_getChallengeError.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.ErrorForeground, _getChallengeError);
                }

                using var _ = ImRaii.TextWrapPos(0.0f);
                ImGui.TextUnformatted("ExtraChat is a third-party service that allows for functionally unlimited extra linkshells that work across data centres.");
                ImGui.TextUnformatted("In order to use ExtraChat, characters must be registered and verified using their Lodestone profile.");
                ImGui.TextUnformatted("ExtraChat stores your character's name, home world, and Lodestone ID, as well as what ExtraChat linkshells your character is a part of and has been invited to.");
                ImGui.TextUnformatted("Messages and linkshell names are end-to-end encrypted; the server cannot decrypt them and does not store messages.");
                ImGui.TextUnformatted("In the event of a legal subpoena, ExtraChat will provide any information available to the legal system.");
            }
        }

        if (state == Client.State.RetrievingChallenge) {
            ImGui.TextUnformatted("Waiting...");
        }

        if (state == Client.State.WaitingForVerification) {
            using var _ = ImRaii.TextWrapPos(0.0f);
           
            if (_challenge == null) {
                ImGui.TextUnformatted("Waiting for verification but no challenge present. This is a bug.");
            } else {
                ImGui.TextUnformatted("Copy the challenge below and save it in your Lodestone profile. After saving, click the button below to verify. After successfully verifying, you can delete the challenge from your profile if desired.");

                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##challenge", ref _challenge, _challenge.Length, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.ReadOnly);

                if (ImGui.Button("Copy")) {
                    ImGui.SetClipboardText(_challenge);
                }

                ImGui.SameLine();

                if (ImGui.Button("Open profile"))
                {
                    var region = _plugin.PlayerState.HomeWorld.Value.DataCenter.Value.Region.RowId;
                    var sub = _plugin.ClientState.ClientLanguage switch {
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

                if (ImGui.Button("Verify") && !Busy) {
                    Busy = true;
                    Task.Run(async () => {
                        var key = await _plugin.Client.Register();
                        _plugin.ConfigInfo.Key = key;
                        _plugin.SaveConfig();
                        await _plugin.Client.AuthenticateAndList();
                    }).ContinueWith(_ => Busy = false);
                }
            }
        }
    }
    
    private void DrawSettings() {
        var anyChanged = false;

        using var tabs = ImRaii.TabBar("settings-tabs");

        if (!tabs.Success)
            return;

        using (var general = ImRaii.TabItem("General"))
        {
            if (general.Success)
                DrawSettingsGeneral(ref anyChanged);
        }

        using (var linkshells = ImRaii.TabItem("Linkshells"))
        {
            if (linkshells.Success)
                DrawSettingsLinkshells(ref anyChanged);
        }

        if (anyChanged) {
            _plugin.SaveConfig();
            _plugin.Ipc.BroadcastChannelCommandColours();
        }
    }

    private void DrawSettingsGeneral(ref bool anyChanged) {
        anyChanged |= ImGui.Checkbox("Use native toasts", ref _plugin.Config.UseNativeToasts);
        anyChanged |= ImGui.Checkbox("Add invite context menu item", ref _plugin.Config.ShowContextMenuItem);
        // ImGui.Spacing();
        //
        // ImGui.TextUnformatted("Default channel");
        // ImGui.SetNextItemWidth(-1);
        // if (ImGui.BeginCombo("##default-channel", $"{_plugin.Config.DefaultChannel}")) {
        //     foreach (var channel in Enum.GetValues<XivChatType>()) {
        //         if (ImGui.Selectable($"{channel}", _plugin.Config.DefaultChannel == channel)) {
        //             _plugin.Config.DefaultChannel = channel;
        //             anyChanged = true;
        //         }
        //     }
        //
        //     ImGui.EndCombo();
        // }

        var player = _plugin.PlayerState;

        using (var treeNode = ImRaii.TreeNode($"Settings for {player.CharacterName}{CrossWorld}{player.HomeWorld.Value.Name}"))
        {
            if (treeNode.Success)
            {
                if (ImGui.Checkbox("Allow receiving invites", ref _plugin.ConfigInfo.AllowInvites)) {
                    anyChanged = true;
                    Task.Run(async () => await _plugin.Client.AllowInvitesToast(_plugin.ConfigInfo.AllowInvites));
                }
            }
        }

        if (_plugin.Client.Status == Client.State.Connected)
        {
            using var treeNode = ImRaii.TreeNode("Delete account");
          
            if (treeNode.Success)
            {
                using var _ = ImRaii.TextWrapPos(0.0f);
                
                if (_plugin.Client.Channels.Count > 0) {
                    ImGui.TextUnformatted("You must leave or disband all ExtraChat linkshells you are currently in before you can delete your account.");
                } else {
                    ImGui.TextUnformatted("Clicking the button below will permanently and irreversibly delete your account from ExtraChat's servers.");

                    if (ImGui.Button("Delete account##actual-delete")) {
                        Task.Run(async () => await _plugin.Client.DeleteAccountToast());
                    }
                }
            }
        }
    }

    private void DrawSettingsLinkshells(ref bool anyChanged) {
        var channelOrder = _plugin.ConfigInfo.ChannelOrder.ToDictionary(
            entry => entry.Value,
            entry => entry.Key
        );

        var orderedChannels = _plugin.Client.Channels.Keys
            .OrderBy(id => channelOrder.GetValueOrDefault(id, int.MaxValue))
            .Concat(_plugin.Client.InvitedChannels.Keys);

        foreach (var id in orderedChannels) {
            var name = _plugin.ConfigInfo.GetName(id);

            if (ImGui.CollapsingHeader($"{name}###{id}-settings")) {
                using var _ = ImRaii.PushId($"{id}-settings");
                
                var contained = _plugin.ConfigInfo.ChannelChannels.TryGetValue(id, out var output);
                var preview = contained ? $"{output}" : "Default";
                var isLinkshellOutput = contained && output.IsLinkshell();

                if (isLinkshellOutput)
                    ImGui.TextColored(
                        ImGuiColors.WarningForeground,
                        "When using a Linkshell as an output channel, color, chat marker\nand notification sound follow vanilla Log Window Settings");
                
                ImGui.TextUnformatted("Output channel");
                ImGui.SetNextItemWidth(-1);

                using (var combo = ImRaii.Combo("##output-channel", preview))
                {
                    if (combo.Success)
                    {
                        if (ImGui.Selectable("Default", !contained)) {
                            _plugin.ConfigInfo.ChannelChannels.Remove(id);
                            anyChanged = true;
                        }
                
                        foreach (var channel in Enum.GetValues<XivChatType>())
                        {
                            if (channel != XivChatType.Debug
                                && channel != XivChatType.Echo
                                && channel != XivChatType.CrossLinkShell1
                                && channel is (< XivChatType.CrossLinkShell2 or > XivChatType.CrossLinkShell8)
                                    and (< XivChatType.Ls1 or > XivChatType.Ls8)
                               )
                                continue;

                            if (ImGui.Selectable($"{channel}", contained && output == channel)) {
                                _plugin.ConfigInfo.ChannelChannels[id] = channel;
                                anyChanged = true;
                            }
                        }
                    }
                }

                ImGui.Spacing();

                ImGui.TextUnformatted("Number");
                channelOrder.TryGetValue(id, out var refOrder);
                var old = refOrder;
                refOrder += 1;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##order", ref refOrder)) {
                    refOrder = Math.Max(1, refOrder) - 1;

                    if (_plugin.ConfigInfo.ChannelOrder.TryGetValue(refOrder, out var other) && other != id) {
                        // another channel already has this number, so swap
                        _plugin.ConfigInfo.ChannelOrder[old] = other;
                    } else {
                        _plugin.ConfigInfo.ChannelOrder.Remove(old);
                    }

                    _plugin.ConfigInfo.ChannelOrder[refOrder] = id;
                    anyChanged = true;
                    _plugin.Commands.ReregisterAll();
                }

                _plugin.ConfigInfo.ChannelPlaySoundEffectMacros.TryGetValue(id, out var playSe);

                if (ImGui.Checkbox("Enable <se.#> sound effects", ref playSe))
                {
                    _plugin.ConfigInfo.ChannelPlaySoundEffectMacros[id] = playSe;
                    anyChanged = true;
                }

                ImGui.Spacing();
                
                using (ImRaii.Disabled(isLinkshellOutput))
                {
                    if (ImGuiUtil.IconButton(FontAwesomeIcon.Undo, "colour-reset", "Reset")) {
                        anyChanged = true;
                        _plugin.ConfigInfo.ChannelColors.Remove(id);
                    }

                    ImGui.SameLine();

                    var colourKey = _plugin.ConfigInfo.GetUiColour(id);
                    var colour = _plugin.DataManager.GetExcelSheet<UIColor>()!.GetRowOrDefault(colourKey)?.Dark ?? 0xff5ad0ff;
                    var vec = ImGui.ColorConvertU32ToFloat4(ColourUtil.RgbaToAbgr(colour));
                    
                    const string colourPickerId = "linkshell-colour-picker";

                    if (ImGui.ColorButton("Linkshell colour", vec, ImGuiColorEditFlags.NoTooltip)) {
                        ImGui.OpenPopup(colourPickerId);
                    }

                    ImGui.SameLine();

                    ImGui.TextUnformatted("Linkshell colour");

                    using (var popup = ImRaii.Popup(colourPickerId))
                    {
                        if (popup.Success)
                        {
                            var i = 0;

                            foreach (var (uiColour, fg) in _uiColours) {
                                if (ImGui.ColorButton($"Colour {uiColour}", fg, ImGuiColorEditFlags.NoTooltip)) {
                                    _plugin.ConfigInfo.ChannelColors[id] = (ushort) uiColour;
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
                        }
                    }
                    
                    ImGui.Spacing();

                    var hint = $"ECLS{refOrder}";
                    if (!_plugin.ConfigInfo.ChannelMarkers.TryGetValue(id, out var marker)) {
                        marker = string.Empty;
                    }

                    ImGui.TextUnformatted("Chat marker");
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputTextWithHint("##marker", hint, ref marker, 16)) {
                        anyChanged = true;
                        if (string.IsNullOrWhiteSpace(marker)) {
                            _plugin.ConfigInfo.ChannelMarkers.Remove(id);
                        } else {
                            _plugin.ConfigInfo.ChannelMarkers[id] = marker;
                        }
                    }
                }

                ImGui.Spacing();
                
                ImGui.TextUnformatted("Notification sound");
                ImGui.SetNextItemWidth(-1);

                _plugin.ConfigInfo.ChannelSoundEffects.TryGetValue(id, out var notificationSound);

                var soundIdx = _notificationSounds.IndexOf(notificationSound);

                using (ImRaii.Disabled(isLinkshellOutput))
                {
                    if (ImGui.Combo("##sound", ref soundIdx, _notificationSoundNames))
                    {
                        notificationSound = _notificationSounds[soundIdx];
                        _plugin.ConfigInfo.ChannelSoundEffects[id] = notificationSound;
                        anyChanged = true;

                        if (notificationSound != NotificationSound.None)
                            UIGlobals.PlayChatSoundEffect((uint)notificationSound);
                    }
                }
            }
        }
    }
    
    private void DrawHelp()
    {
        using var _ = ImRaii.TextWrapPos(0.0f);

        if (ImGui.Button("Reset tutorial"))
        {
            _plugin.ConfigInfo.TutorialStep = 0;
            _plugin.SaveConfig();
        }
    }
}