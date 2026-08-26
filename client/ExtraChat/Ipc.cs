using System.Numerics;
using Dalamud.Plugin.Ipc;
using Lumina.Excel.Sheets;
using Lumina.Extensions;

namespace ExtraChat;

internal class Ipc : IDisposable {
    [Serializable]
    private struct OverrideInfo {
        public string? Channel;
        public ushort UiColour;
        public uint Rgba;
    }

    private Plugin Plugin { get; }
    private ICallGateProvider<OverrideInfo, object> OverrideChannelColour { get; }
    private ICallGateProvider<Dictionary<string, uint>, Dictionary<string, uint>> ChannelCommandColours { get; }
    private ICallGateProvider<Dictionary<Guid, string>, Dictionary<Guid, string>> ChannelNames { get; }

    internal Ipc(Plugin plugin) {
        this.Plugin = plugin;

        this.OverrideChannelColour = this.Plugin.Interface.GetIpcProvider<OverrideInfo, object>("ExtraChat.OverrideChannelColour");
        this.ChannelCommandColours = this.Plugin.Interface.GetIpcProvider<Dictionary<string, uint>, Dictionary<string, uint>>("ExtraChat.ChannelCommandColours");
        this.ChannelNames = this.Plugin.Interface.GetIpcProvider<Dictionary<Guid, string>, Dictionary<Guid, string>>("ExtraChat.ChannelNames");

        this.ChannelCommandColours.RegisterFunc(_ => this.GetChannelColours());
        this.ChannelNames.RegisterFunc(_ => this.GetChannelNames());
    }

    public void Dispose() {
        this.ChannelNames.UnregisterFunc();
        this.ChannelCommandColours.UnregisterFunc();
    }

    private Dictionary<string, uint> GetChannelColours()
    {
        var info = this.Plugin.ConfigInfo;
        var dict = new Dictionary<string, uint>(info.ChannelOrder.Count + info.Aliases.Count);

        foreach (var (idx, id) in info.ChannelOrder)
        {
            this.Plugin.GetChannelColour(id, out _, out var rgba);

            dict[$"/ecl{idx + 1}"] = rgba;
        }

        foreach (var (alias, id) in info.Aliases)
        {
            this.Plugin.GetChannelColour(id, out _, out var rgba);

            dict[alias] = rgba;
        }

        return dict;
    }

    private Dictionary<Guid, string> GetChannelNames() {
        return this.Plugin.Client.Channels
            .Values
            .ToDictionary(
                channel => channel.Id,
                channel => this.Plugin.ConfigInfo.Channels.TryGetValue(channel.Id, out var info) ? info.Name : "???"
            );
    }

    internal void BroadcastChannelCommandColours() {
        this.ChannelCommandColours.SendMessage(this.GetChannelColours());
    }

    internal void BroadcastChannelNames() {
        this.ChannelNames.SendMessage(this.GetChannelNames());
    }

    internal void BroadcastOverride(Guid over) {
        if (over == Guid.Empty) {
            this.OverrideChannelColour.SendMessage(new OverrideInfo());
            return;
        }

        var name = this.Plugin.ConfigInfo.GetFullName(over);

        this.Plugin.GetChannelColour(over, out var colour, out var rgba);

        this.OverrideChannelColour.SendMessage(new OverrideInfo {
            Channel = name,
            UiColour = colour,
            Rgba = rgba,
        });
    }
}
