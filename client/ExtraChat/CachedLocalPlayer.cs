using Dalamud.Game.Text.SeStringHandling;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ExtraChat;

public class CachedLocalPlayer
{
    public required SeString Name { get; init; }
    public RowRef<World> HomeWorld { get; init; }
    public RowRef<World> CurrentWorld { get; init; }
}