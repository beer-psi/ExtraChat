using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ExtraChat.Util;

internal static class WorldUtil {
    private static readonly Dictionary<ushort, string> WorldNames = new();

    internal static void Initialise(IDataManager data) {
        WorldNames.Clear();

        var worlds = data.GetExcelSheet<World>();
        if (worlds == null) {
            return;
        }

        foreach (var world in worlds) {
            if (!world.IsPublic) {
                continue;
            }

            WorldNames[(ushort) world.RowId] = world.Name.ExtractText();
        }
    }

    internal static string WorldName(ushort id) {
        return WorldNames.TryGetValue(id, out var name) ? name : "???";
    }
}
