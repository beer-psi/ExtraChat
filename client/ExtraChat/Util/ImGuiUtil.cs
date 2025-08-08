using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace ExtraChat.Util;

internal static class ImGuiUtil {
    private static readonly Dictionary<int, string?[]> Tutorials = new() {
        [0] = new[] {
            "Create a linkshell",

            "You can use this button to create a new linkshell.",
            "Alternatively, you can be invited by a friend to join a linkshell.",
        },
        [1] = new[] {
            "Refresh data",

            "This button will refresh all data about your linkshells.",
            "Generally, you shouldn't need to press this. Clicking on a linkshell refreshes the member list.",
        },
        [2] = new[] {
            "Manage linkshells you're in",

            "Clicking on a linkshell in this list will show you its members in the pane to the right.",
            "You can also right-click the linkshell name to open a menu with various options.",
            "If you were invited to a linkshell, you can accept the invitation in this menu.",
        },
        [3] = new[] {
            "Talking in a linkshell",

            "The number displayed before the linkshell name is the linkshell's number.",
            "You can change this number by right-clicking.",
            "This number is used to determine the command you should use to talk in the linkshell.",
            "For example, linkshell 1 would use the command /ecl1, linkshell 2 would use /ecl2, etc.",
            null,
            "Click on this linkshell to see the member list.",
        },
        [4] = new[] {
            "Members in a linkshell",

            "Inside the member list, each member is shown with an optional symbol indicating their rank.",
            null,
            "Admins have this symbol: ★",
            "Moderators have this symbol: ☆",
            "Normal members have no symbol.",
            "Invited members have this symbol: ?",
            null,
            "Members also appear dimmed when they are offline.",
        },
        [5] = new[] {
            "Managing members in a linkshell",

            "Moderators and admins of a linkshell can right-click on members in the member list to open a menu with various options.",
            "Many options require holding the Control key to enable so that they aren't accidentally activated.",
        },
    };

    internal static bool IconButton(FontAwesomeIcon icon, string? id = null, string? tooltip = null) {
        var label = icon.ToIconString();
        if (id != null) {
            label += $"##{id}";
        }

        ImGui.PushFont(UiBuilder.IconFont);
        var ret = ImGui.Button(label);
        ImGui.PopFont();

        if (tooltip != null && ImGui.IsItemHovered()) {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(tooltip);
            ImGui.EndTooltip();
        }

        return ret;
    }

    internal static bool SelectableConfirm(string label, ConfirmKey keys = ConfirmKey.Ctrl, string? tooltip = null) {
        var selectable = ImGui.Selectable(label);
        var hovered = ImGui.IsItemHovered();

        var confirmHeld = true;
        var mods = hovered ? new StringBuilder() : null;
        foreach (var key in Enum.GetValues<ConfirmKey>()) {
            if (!keys.HasFlag(key)) {
                continue;
            }

            if (hovered) {
                if (mods!.Length != 0) {
                    mods.Append('+');
                }

                mods.Append(key.ToString());
            }

            var held = key switch {
                ConfirmKey.Ctrl => ImGui.GetIO().KeyCtrl,
                ConfirmKey.Alt => ImGui.GetIO().KeyAlt,
                ConfirmKey.Shift => ImGui.GetIO().KeyShift,
                _ => false,
            };
            confirmHeld &= held;
        }

        if (!confirmHeld && hovered) {
            ImGui.BeginTooltip();
            var explainer = $"Hold {mods} to enable this option.";
            var tip = tooltip == null ? explainer : $"{tooltip}\n{explainer}";
            ImGui.TextUnformatted(tip);
            ImGui.EndTooltip();
        }

        return selectable && confirmHeld;
    }

    internal static bool Tutorial(Plugin plugin, int step) {
        var save = false;
        ref var current = ref plugin.ConfigInfo.TutorialStep;
        if (current < 0 || current != step) {
            return save;
        }

        if (!Tutorials.TryGetValue(step, out var strings)) {
            return save;
        }

        var max = Tutorials.Keys.Max();

        const string popupId = "extrachat-tutorial";
        ImGui.OpenPopup(popupId);

        ImGui.GetForegroundDrawList().AddRect(
            ImGui.GetItemRectMin() - new Vector2(2) * ImGuiHelpers.GlobalScale,
            ImGui.GetItemRectMax() + new Vector2(2) * ImGuiHelpers.GlobalScale,
            ImGui.GetColorU32(new Vector4(1, 0, 0, 1))
        );

        ImGui.SetNextWindowPos(ImGui.GetItemRectMax() + new Vector2(2) * ImGuiHelpers.GlobalScale);
        ImGui.SetNextWindowSize(new Vector2(350, 0) * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginPopup(popupId, ImGuiWindowFlags.AlwaysAutoResize)) {
            return save;
        }

        ImGui.PushFont(UiBuilder.DefaultFont);
        ImGui.PushTextWrapPos();

        ImGui.TextUnformatted(strings[0]);
        ImGui.Separator();

        foreach (var body in strings[1..]) {
            if (body == null) {
                ImGui.Spacing();
                continue;
            }

            ImGui.TextUnformatted(body);
        }

        if (step == max) {
            if (ImGui.Button("Finish")) {
                current = -1;
                save = true;
            }
        } else {
            if (ImGui.Button("Next")) {
                current += 1;
                save = true;
            }
        }

        ImGui.SameLine();

        if (ImGui.Button("Skip tutorial")) {
            current = -1;
            save = true;
        }

        ImGui.PopTextWrapPos();
        ImGui.PopFont();
        ImGui.EndPopup();

        return save;
    }
}

[Flags]
internal enum ConfirmKey {
    Ctrl = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
}
