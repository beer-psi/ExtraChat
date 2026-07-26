using System.Text;
using Dalamud.Configuration;
using Dalamud.Game.Text;
using ExtraChat.Protocol.Channels;
using ExtraChat.Util;

namespace ExtraChat;

[Serializable]
internal class Configuration : IPluginConfiguration {
    public int Version { get; set; } = 1;

    public bool UseNativeToasts = true;
    public bool ShowContextMenuItem = true;
    public XivChatType DefaultChannel = XivChatType.Debug;
    public Dictionary<ulong, ConfigInfo> Configs { get; } = new();

    internal ConfigInfo GetConfig(ulong id) {
        if (id == 0) {
            // just pretend
            return new ConfigInfo();
        }

        if (this.Configs.TryGetValue(id, out var config)) {
            return config;
        }

        var newConfig = new ConfigInfo();
        this.Configs[id] = newConfig;

        return newConfig;
    }
}

[Serializable]
internal class ConfigInfo {
    public string? Key;
    public Dictionary<Guid, ChannelInfo> Channels = new();
    public Guid CurrentChannel = Guid.Empty;
    public Dictionary<int, Guid> ChannelOrder = new();
    public Dictionary<string, Guid> Aliases = new();
    public Dictionary<Guid, ushort> ChannelColors = new();
    public Dictionary<Guid, string> ChannelMarkers = new();
    public Dictionary<Guid, XivChatType> ChannelChannels = new();
    public Dictionary<Guid, NotificationSound> ChannelSoundEffects = new();
    public Dictionary<Guid, bool> ChannelPlaySoundEffectMacros = new();
    public int TutorialStep;
    public bool AllowInvites = true;

    internal string GetName(Guid id) => this.Channels.TryGetValue(id, out var channel)
        ? channel.Name
        : "???";

    internal ushort GetUiColour(Guid id) => this.ChannelColors.TryGetValue(id, out var colour)
        ? colour
        : Plugin.DefaultColour;

    internal string? GetMarker(Guid id) {
        var order = this.GetOrder(id);
        if (order == null) {
            return null;
        }

        return this.ChannelMarkers.TryGetValue(id, out var custom)
            ? custom
            : $"ECLS{order.Value + 1}";
    }

    internal int? GetOrder(Guid id) {
        var pair = this.ChannelOrder
            .Select(entry => (entry.Key, entry.Value))
            .FirstOrDefault(entry => entry.Value == id);
        if (pair == default) {
            return null;
        }

        return pair.Key;
    }

    internal string GetFullName(Guid id) {
        var name = this.GetName(id);

        var order = "?";
        var orderEntry = this.ChannelOrder
            .Select(entry => (entry.Key, entry.Value))
            .FirstOrDefault(entry => entry.Value == id);
        if (orderEntry != default) {
            order = (orderEntry.Key + 1).ToString();
        }

        return $"ECLS [{order}]: {name}";
    }

    internal ChannelInfo GetOrInsertChannel(Guid id) {
        if (this.Channels.TryGetValue(id, out var channel)) {
            return channel;
        }

        var newChannel = new ChannelInfo();
        this.Channels[id] = newChannel;
        return newChannel;
    }

    internal int AddChannelIndex(Guid channelId) {
        var existing = this.ChannelOrder
            .Select(entry => (entry.Key, entry.Value))
            .FirstOrDefault(entry => entry.Value == channelId);
        if (existing != default) {
            return existing.Key;
        }

        var indices = this.ChannelOrder.Keys;
        var idx = indices.Count == 0 ? 0 : indices.Max() + 1;
        this.ChannelOrder[idx] = channelId;
        return idx;
    }

    internal void RemoveChannelIndex(Guid channelId) {
        var idx = this.ChannelOrder
            .Select(entry => (entry.Key, entry.Value))
            .FirstOrDefault(entry => entry.Value == channelId);

        if (idx != default) {
            this.ChannelOrder.Remove(idx.Key);
        }
    }

    internal void RegisterChannel(Channel channel, byte[] key) {
        var name = channel.DecryptName(key);
        this.Channels[channel.Id] = new ChannelInfo {
            Name = name,
            SharedSecret = key,
        };

        this.AddChannelIndex(channel.Id);
    }

    internal void UpdateChannel(Guid id, byte[] name) {
        if (this.Channels.TryGetValue(id, out var info)) {
            info.Name = Encoding.UTF8.GetString(SecretBox.Decrypt(info.SharedSecret, name));
        }
    }

    internal void UpdateChannel(Channel channel) {
        this.UpdateChannel(channel.Id, channel.Name);
    }

    internal void UpdateChannel(SimpleChannel channel) {
        this.UpdateChannel(channel.Id, channel.Name);
    }

    internal bool ShouldPlaySoundEffectMacros(Guid id)
    {
        return this.ChannelPlaySoundEffectMacros.TryGetValue(id, out var condition) && condition;
    }
}

[Serializable]
internal class ChannelInfo {
    public byte[] SharedSecret = Array.Empty<byte>();
    public string Name = "???";
}
