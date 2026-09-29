using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using ASodium;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using ExtraChat.Protocol;
using ExtraChat.Protocol.Channels;
using ExtraChat.Ui;
using ExtraChat.Util;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using Channel = ExtraChat.Protocol.Channels.Channel;
using SeStringBuilder = Lumina.Text.SeStringBuilder;

namespace ExtraChat;

internal class Client : IAsyncDisposable {
    private const int IsUpPingNumber = 42069;

    internal enum State {
        Disconnected,
        Connecting,
        NotAuthenticated,
        RetrievingChallenge,
        WaitingForVerification,
        Verifying,
        Authenticating,
        FailedAuthentication,
        Connected,
    }

    private Plugin Plugin { get; }
    private ClientWebSocket WebSocket { get; set; }

    internal State Status
    {
        get;
        private set
        {
            field = value;
            this.StatusChanged?.Invoke(this, field);
        }
    } = State.Disconnected;

    private bool _active = true;
    private uint _number = 1;
    private bool _wasConnected;
    private bool _wasImproperlyClosed;

    private KeyPair KeyPair { get; }

    private readonly SemaphoreSlim _waitersSemaphore = new(1, 1);
    private Dictionary<uint, ChannelWriter<ResponseKind>> Waiters { get; set; } = new();
    private Channel<(RequestContainer, ChannelWriter<ChannelReader<ResponseKind>>?)> ToSend { get; set; } = System.Threading.Channels.Channel.CreateUnbounded<(RequestContainer, ChannelWriter<ChannelReader<ResponseKind>>?)>();
    
    private Channel<MessageResponse> ReceivedMessages { get; set; } =
        System.Threading.Channels.Channel.CreateUnbounded<MessageResponse>();

    internal Dictionary<Guid, Channel> Channels { get; } = new();
    internal Dictionary<Guid, Channel> InvitedChannels { get; } = new();
    internal Dictionary<Guid, Rank> ChannelRanks { get; } = new();

    public event EventHandler<State>? StatusChanged;

    internal Client(Plugin plugin) {
        this.Plugin = plugin;
        this.WebSocket = new ClientWebSocket();
        this.KeyPair = SodiumKeyExchange.GenerateKeyPair();
    }

    private async ValueTask CloseAsync()
    {
        this._active = false;
        
        if (this.WebSocket.State is WebSocketState.Connecting or WebSocketState.Open)
            try
            {
                await this.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException e)
            {
                Plugin.Log.Debug(e, "Failed to close WebSocket before disabling plugin");
            }
            catch (OperationCanceledException e)
            {
                Plugin.Log.Debug(e, "Failed to close WebSocket before disabling plugin");
            }
    }

    public async ValueTask DisposeAsync()
    {
        await this.CloseAsync();
        
        this.WebSocket.Dispose();
        this._waitersSemaphore.Dispose();
    }

    internal bool TryGetChannel(Guid id, [MaybeNullWhen(false)] out Channel channel) {
        return this.Channels.TryGetValue(id, out channel) || this.InvitedChannels.TryGetValue(id, out channel);
    }

    internal void StopLoop() {
        Task.Run(async () =>
        {
            await this.CloseAsync();
            this.Status = State.Disconnected;
        });
    }

    internal void StartLoop() {
        this._active = true;

        Task.Run(async () => {
            while (this._active) {
                try
                {
                    await this.Loop();
                }
                catch (WebSocketClosure ex)
                {
                    Plugin.Log.Warning(ex, "WebSocket closed");

                    this._wasImproperlyClosed =
                        (this._active && ex.CloseStatus == WebSocketCloseStatus.NormalClosure)
                        || ex.CloseStatus == (WebSocketCloseStatus)1006;

                    if (!this._wasImproperlyClosed)
                        break;
                }
                catch (WebSocketException ex)
                {
                    Plugin.Log.Warning(ex, "WebSocket error occured");

                    this._wasImproperlyClosed =
                        this._active && ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely;

                    if (!this._wasImproperlyClosed)
                        break;
                }
                catch (OperationCanceledException ex) // this happens when logging out since ws is aborted
                {
                    Plugin.Log.Warning(ex, "WebSocket operation canceled");
                    
                    if (!this._active)
                        break;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Error in client loop");
                    if (this._wasConnected)
                    {
                        this.Plugin.ChatGui.Print(new XivChatEntry
                        {
                            Message = "Disconnected from ExtraChat. Trying to reconnect.",
                            Type = XivChatType.Urgent,
                        });
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            // ReSharper disable once FunctionNeverReturns
        });

        Task.Run(async () =>
        {
            while (this._active || this.ReceivedMessages.Reader is { CanCount: true, Count: > 0 })
            {
                this.HandleMessage(await this.ReceivedMessages.Reader.ReadAsync());
            }
        });
    }

    private async Task<ChannelReader<ResponseKind>> RegisterWaiter(uint number) {
        var channel = System.Threading.Channels.Channel.CreateBounded<ResponseKind>(1);
        await this._waitersSemaphore.WaitAsync();
        try {
            this.Waiters[number] = channel.Writer;
        } finally {
            this._waitersSemaphore.Release();
        }

        return channel.Reader;
    }

    private async Task QueueMessage(RequestKind request) {
        var container = new RequestContainer {
            Number = this._number++,
            Kind = request,
        };

        await this.ToSend.Writer.WriteAsync((container, null));
    }

    private async Task<ResponseKind> QueueMessageAndWait(RequestKind request) {
        var container = new RequestContainer {
            Number = this._number++,
            Kind = request,
        };

        var channel = System.Threading.Channels.Channel.CreateBounded<ChannelReader<ResponseKind>>(1);
        await this.ToSend.Writer.WriteAsync((container, channel.Writer));
        var what = await channel.Reader.ReadAsync();
        return await what.ReadAsync();
    }

    private byte[] GetPrivateKey() {
        var key = new byte[this.KeyPair.GetPrivateKeyLength()];
        SodiumGuardedHeapAllocation.Sodium_MProtect_ReadOnly(this.KeyPair.GetPrivateKey());
        Marshal.Copy(this.KeyPair.GetPrivateKey(), key, 0, this.KeyPair.GetPrivateKeyLength());
        SodiumGuardedHeapAllocation.Sodium_MProtect_NoAccess(this.KeyPair.GetPrivateKey());
        return key;
    }

    internal async Task Connect() {
        await this.WebSocket.ConnectAsync(new Uri("wss://extrachat.annaclemens.io/"), CancellationToken.None);
    }

    internal Task AuthenticateAndList() {
        return Task.Run(async () => {
            await this.SendVersion();

            if (await this.Authenticate()) {
                this._wasConnected = true;

                if (this._wasImproperlyClosed)
                    this._wasImproperlyClosed = false;
                else
                    this.Plugin.ChatGui.Print(new XivChatEntry
                    {
                        Message = "Connected to ExtraChat.",
                        Type = XivChatType.Notice,
                    });
                
                await this.ListAll();
            }
        });
    }

    /// <summary>
    /// Gets the challenge to put in the user's Lodestone profile.
    /// </summary>
    /// <returns>challenge or null if LocalPlayer is null</returns>
    /// <exception cref="Exception">if the server returns an error or unexpected output</exception>
    internal async Task<string?> GetChallenge() {
        this.Status = State.RetrievingChallenge;
        var response = await this.QueueMessageAndWait(new RequestKind.Register(new RegisterRequest {
            Name = this.Plugin.PlayerState.CharacterName,
            World = (ushort)this.Plugin.PlayerState.HomeWorld.RowId,
            ChallengeCompleted = false,
        }));

        switch (response) {
            case ResponseKind.Error { Response.Error: var error }:
                this.Status = State.NotAuthenticated;
                throw new Exception(error);
            case ResponseKind.Register { Response: RegisterResponse.Challenge { Text: var challenge } }:
                this.Status = State.WaitingForVerification;
                return challenge;
            default:
                this.Status = State.NotAuthenticated;
                throw new Exception("Unexpected response");
        }
    }

    internal async Task<(Channel, byte[])> Create(string name) {
        var shared = SodiumSecretBoxXChaCha20Poly1305.GenerateKey();
        var encryptedName = SecretBox.Encrypt(shared, Encoding.UTF8.GetBytes(name));

        var response = await this.QueueMessageAndWait(new RequestKind.Create(new CreateRequest {
            Name = encryptedName,
        }));

        var channelInfo = response switch {
            ResponseKind.Error { Response.Error: var error } => throw new Exception(error),
            ResponseKind.Create { Response.Channel: var channel } => (channel, shared),
            _ => throw new Exception("invalid response"),
        };

        this.Plugin.ConfigInfo.RegisterChannel(channelInfo.channel, channelInfo.shared);
        this.Channels[channelInfo.channel.Id] = channelInfo.channel;
        this.ChannelRanks[channelInfo.channel.Id] = Rank.Admin;
        this.Plugin.Commands.ReregisterAll();
        this.Plugin.SaveConfig();

        return channelInfo;
    }

    internal async Task<InviteResponse?> Invite(string name, ushort world, Guid channel) {
        // Invite requires three steps:
        // 1. Get the public key of the invitee
        // 2. Encrypt the shared key with the public key
        //    NOTE: in all cases, the party initiating the key exchange is
        //          considered the CLIENT
        // 3. Send the invite with the encrypted shared key

        // 0. Get the channel shared key
        if (!this.Plugin.ConfigInfo.Channels.TryGetValue(channel, out var channelInfo)) {
            return null;
        }

        // 1. Get the public key of the invitee
        var response = await this.QueueMessageAndWait(new RequestKind.PublicKey(new PublicKeyRequest {
            Name = name,
            World = world,
        }));

        var invitee = response switch {
            ResponseKind.Error { Response.Error: var error } => throw new Exception(error),
            ResponseKind.PublicKey { Response.PublicKey: var respKey } => respKey,
            _ => throw new Exception("invalid response"),
        };

        if (invitee == null) {
            return null;
        }

        // 2. Encrypt the shared key with the public key
        var kx = SodiumKeyExchange.CalculateClientSharedSecret(this.KeyPair.GetPublicKey(), this.GetPrivateKey(), invitee);
        var encryptedShared = SecretBox.Encrypt(kx.TransferSharedSecret, channelInfo.SharedSecret);

        // 3. Send the invite with the encrypted shared key
        response = await this.QueueMessageAndWait(new RequestKind.Invite(new InviteRequest {
            Channel = channel,
            Name = name,
            World = world,
            EncryptedSecret = encryptedShared,
        }));

        return response switch {
            ResponseKind.Error { Response.Error: var error } => throw new Exception(error),
            ResponseKind.Invite { Response: var invite } => invite,
            _ => throw new Exception("Unexpected response"),
        };
    }

    internal async Task InviteToast(string name, ushort world, Guid channel) {
        var worldName = WorldUtil.WorldName(world);
        var channelName = this.Plugin.ConfigInfo.GetName(channel);
        try {
            if (await this.Invite(name, world, channel) == null) {
                this.Plugin.ShowError($"Could not invite {name}{PluginUi.CrossWorld}{worldName} to \"{channelName}\": not logged into ExtraChat");
            } else {
                this.Plugin.ShowInfo($"Invited {name}{PluginUi.CrossWorld}{worldName} to \"{channelName}\"");
            }
        } catch (Exception ex) {
            this.Plugin.ShowError($"Could not invite {name}{PluginUi.CrossWorld}{worldName} to \"{channelName}\": {ex.Message}");
        }
    }

    internal async Task<string?> DeleteAccount() {
        var response = await this.QueueMessageAndWait(new RequestKind.DeleteAccount(new DeleteAccountRequest()));
        return response switch {
            ResponseKind.Error { Response.Error: var error } => error,
            ResponseKind.DeleteAccount => null,
            _ => throw new Exception("Unexpected response"),
        };
    }

    internal async Task DeleteAccountToast() {
        var message = await this.DeleteAccount();
        if (message != null) {
            this.Plugin.ShowError($"Could not delete account: {message}");
            return;
        }

        this.Plugin.Config.Configs.Remove(this.Plugin.PlayerState.ContentId);
        this.Plugin.SaveConfig();
        this.StopLoop();
        this.Status = State.NotAuthenticated;
    }

    /// <summary>
    /// Attempts to register the user after the challenge has been completed.
    /// </summary>
    /// <returns>authentication key or null if LocalPlayer was null or the challenge failed</returns>
    /// <exception cref="Exception">if the server returns an error or unexpected output</exception>
    internal async Task<string?> Register() {
        this.Status = State.Verifying;
        var response = await this.QueueMessageAndWait(new RequestKind.Register(new RegisterRequest {
            Name = this.Plugin.PlayerState.CharacterName,
            World = (ushort)this.Plugin.PlayerState.HomeWorld.RowId,
            ChallengeCompleted = true,
        }));

        switch (response) {
            case ResponseKind.Error { Response.Error: var error }:
                this.Status = State.WaitingForVerification;
                throw new Exception(error);
            case ResponseKind.Register { Response: RegisterResponse.Failure }:
                this.Status = State.WaitingForVerification;
                return null;
            case ResponseKind.Register { Response: RegisterResponse.Success { Key: var key } }:
                this.Status = State.NotAuthenticated;
                return key;
            default:
                throw new Exception("Unexpected response");
        }
    }

    internal async Task SendVersion() {
        await this.QueueMessage(new RequestKind.Version(new VersionRequest {
            Version = 1,
        }));
    }

    internal async Task<bool> Authenticate() {
        if (this.Plugin.ConfigInfo.Key is not { } key) {
            return false;
        }

        this.Status = State.Authenticating;

        var response = await this.QueueMessageAndWait(new RequestKind.Authenticate(new AuthenticateRequest {
            Key = key,
            PublicKey = this.KeyPair.GetPublicKey(),
            AllowInvites = this.Plugin.ConfigInfo.AllowInvites,
        }));

        var success = response switch {
            ResponseKind.Error => false,
            ResponseKind.Authenticate { Response.Error: null } => true,
            ResponseKind.Authenticate => false,
            _ => false,
        };

        this.Status = success ? State.Connected : State.FailedAuthentication;
        return success;
    }

    internal async Task SendMessage(Guid channel, byte[] message) {
        await this.QueueMessage(new RequestKind.Message(new MessageRequest {
            Channel = channel,
            Message = message,
        }));
    }

    internal async Task ListAll() {
        await this.QueueMessage(new RequestKind.List(new ListRequest.All()));
    }

    internal async Task ListMembers(Guid channelId) {
        await this.QueueMessage(new RequestKind.List(new ListRequest.Members(channelId)));
    }

    internal async Task Join(Guid channelId) {
        if (!this.Plugin.ConfigInfo.Channels.TryGetValue(channelId, out var info)) {
            return;
        }

        var response = await this.QueueMessageAndWait(new RequestKind.Join(new JoinRequest {
            Channel = channelId,
        }));

        switch (response) {
            case ResponseKind.Error { Response.Error: var error }: {
                this.Plugin.ShowError($"Failed to join \"{info.Name}\": {error}");
                break;
            }
            case ResponseKind.Join { Response: var resp }: {
                this.Plugin.ShowInfo($"Joined \"{info.Name}\"");
                this.InvitedChannels.Remove(channelId);
                this.Channels[channelId] = resp.Channel;
                this.ChannelRanks[channelId] = Rank.Member;

                this.Plugin.ConfigInfo.AddChannelIndex(resp.Channel.Id);
                this.Plugin.ConfigInfo.UpdateChannel(resp.Channel);

                this.Plugin.SaveConfig();
                this.Plugin.Commands.ReregisterAll();
                break;
            }
            default: {
                throw new Exception("Unexpected response");
            }
        }
    }

    internal async Task Leave(Guid channelId) {
        var response = await this.QueueMessageAndWait(new RequestKind.Leave(new LeaveRequest {
            Channel = channelId,
        }));

        if (response is ResponseKind.Leave { Response: { Error: null, Channel: var id } }) {
            this.ActuallyLeave(id);
        }
    }

    private void ActuallyLeave(Guid id) {
        this.Channels.Remove(id);
        this.InvitedChannels.Remove(id);

        var idx = this.Plugin.ConfigInfo.ChannelOrder
            .Select(entry => (entry.Key, entry.Value))
            .FirstOrDefault(entry => entry.Value == id);

        if (idx != default) {
            this.Plugin.ConfigInfo.ChannelOrder.Remove(idx.Key);
            this.Plugin.SaveConfig();
        }
    }

    internal async Task<string?> Kick(Guid id, string name, ushort world) {
        var response = await this.QueueMessageAndWait(new RequestKind.Kick(new KickRequest {
            Channel = id,
            Name = name,
            World = world,
        }));

        return response switch {
            ResponseKind.Error { Response.Error: var error } => error,
            _ => null,
        };
    }

    internal async Task<string?> Promote(Guid id, string name, ushort world, Rank rank) {
        var resp = await this.QueueMessageAndWait(new RequestKind.Promote(new PromoteRequest {
            Channel = id,
            Name = name,
            World = world,
            Rank = rank,
        }));

        return resp switch {
            ResponseKind.Error { Response.Error: var error } => error,
            _ => null,
        };
    }

    internal async Task<string?> Disband(Guid id) {
        var resp = await this.QueueMessageAndWait(new RequestKind.Disband(new DisbandRequest {
            Channel = id,
        }));

        return resp switch {
            ResponseKind.Error { Response.Error: var error } => error,
            _ => null,
        };
    }

    internal async Task<string?> Update(Guid id, UpdateKind kind) {
        var resp = await this.QueueMessageAndWait(new RequestKind.Update(new UpdateRequest {
            Channel = id,
            Kind = kind,
        }));

        return resp switch {
            ResponseKind.Error { Response.Error: var error } => error,
            ResponseKind.Update => null,
            _ => throw new Exception("Unexpected response"),
        };
    }

    internal async Task UpdateToast(Guid id, UpdateKind kind) {
        if (await this.Update(id, kind) is not { } error) {
            return;
        }

        var name = this.Plugin.ConfigInfo.GetName(id);
        this.Plugin.ShowError($"Could not update \"{name}\": {error}");
    }

    internal async Task RequestSecrets(Guid id) {
        await this.QueueMessage(new RequestKind.Secrets(new SecretsRequest {
            Channel = id,
        }));
    }

    internal async Task<bool> AllowInvites(bool allow) {
        var resp = await this.QueueMessageAndWait(new RequestKind.AllowInvites(new AllowInvitesRequest {
            Allowed = allow,
        }));


        return resp is ResponseKind.AllowInvites { Response.Allowed: var respAllowed } && respAllowed == allow;
    }

    internal async Task AllowInvitesToast(bool allow) {
        if (!await this.AllowInvites(allow)) {
            this.Plugin.ShowError("Could not set invite permissions.");
        }
    }

    private bool _up;

    #pragma warning disable CS4014
    private async Task Loop() {
        Start:
        this._wasConnected = false;
        this._up = false;
        this._number = 1;
        this.WebSocket.Abort();
        this.Status = State.Disconnected;

        if (!this._active) {
            return;
        }

        this.Channels.Clear();
        this.InvitedChannels.Clear();
        this.ChannelRanks.Clear();
        this.Waiters.Clear();
        this.ToSend = System.Threading.Channels.Channel.CreateUnbounded<(RequestContainer, ChannelWriter<ChannelReader<ResponseKind>>?)>();
        await this._waitersSemaphore.WaitAsync();
        try {
            this.Waiters = new Dictionary<uint, ChannelWriter<ResponseKind>>();
        } finally {
            this._waitersSemaphore.Release();
        }

        // If the websocket is closed, we need to reconnect
        this.WebSocket.Dispose();
        this.WebSocket = new ClientWebSocket();

        this.Status = State.Connecting;
        await this.Connect();

        Task.Run(async () => {
            while (this._active && !this._up) {
                await this.WebSocket.SendMessage(new RequestContainer {
                    Number = IsUpPingNumber,
                    Kind = new RequestKind.Ping(new PingRequest()),
                });

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            if (this._active && this.Plugin.ConfigInfo.Key != null) {
                this.AuthenticateAndList();
            }
        });

        if (this.Plugin.ConfigInfo.Key == null) {
            this.Status = State.NotAuthenticated;
        }

        var websocketMessage = this.WebSocket.ReceiveMessage();
        var toSend = this.ToSend.Reader.ReadAsync().AsTask();

        while (this._active && this.WebSocket.State == WebSocketState.Open) {
            var finished = await Task.WhenAny(websocketMessage, toSend);

            if (finished == websocketMessage) {
                var response = await websocketMessage;
                websocketMessage = this.WebSocket.ReceiveMessage();

                switch (response) {
                    case { Kind: ResponseKind.Ping, Number: IsUpPingNumber } when !this._up: {
                        this._up = true;

                        break;
                    }
                    case { Kind: ResponseKind.Message { Response: var resp } }: {
                        await this.ReceivedMessages.Writer.WriteAsync(resp);
                        break;
                    }
                    case { Kind: ResponseKind.Invited { Response: var resp } }: {
                        Task.Run(() => this.HandleInvited(resp));
                        break;
                    }
                    case { Kind: ResponseKind.List { Response: var resp } }: {
                        Task.Run(() => this.HandleList(resp));
                        break;
                    }
                    case { Kind: ResponseKind.MemberChange { Response: var resp } }: {
                        Task.Run(() => this.HandleMemberChange(resp));
                        break;
                    }
                    case { Kind: ResponseKind.Disband { Response: var resp }, Number: 0 }: {
                        // this is a disband notification, not a response to a command
                        Task.Run(() => this.HandleDisband(resp));
                        break;
                    }
                    case { Kind: ResponseKind.Updated { Response: var resp }, Number: 0 }: {
                        Task.Run(() => this.HandleUpdated(resp));
                        break;
                    }
                    case { Kind: ResponseKind.Secrets { Response: var resp } }: {
                        Task.Run(() => this.HandleSecrets(resp));
                        break;
                    }
                    case { Kind: ResponseKind.SendSecrets { Response: var resp }, Number: 0 }: {
                        Task.Run(async () => await this.HandleSendSecrets(resp));
                        break;
                    }
                    case { Kind: ResponseKind.Announce { Response: var resp }, Number: 0 }: {
                        Task.Run(() => this.HandleAnnounce(resp));
                        break;
                    }
                    default: {
                        await this._waitersSemaphore.WaitAsync();
                        try {
                            if (this.Waiters.Remove(response.Number, out var waiter)) {
                                await waiter.WriteAsync(response.Kind);
                            }
                        } finally {
                            this._waitersSemaphore.Release();
                        }

                        break;
                    }
                }
            } else if (finished == toSend) {
                var (req, update) = await toSend;
                toSend = this.ToSend.Reader.ReadAsync().AsTask();

                await this.WebSocket.SendMessage(req);
                if (update != null) {
                    await update.WriteAsync(await this.RegisterWaiter(req.Number));
                }
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        goto Start;
        // ReSharper disable once FunctionNeverReturns
    }
    #pragma warning restore CS4014

    private void HandleAnnounce(AnnounceResponse resp) {
        this.Plugin.ChatGui.Print(new XivChatEntry {
            Type = XivChatType.Notice,
            Message = $"[ExtraChat] {resp.Announcement}",
        });
    }

    private void HandleSecrets(SecretsResponse resp) {
        var kx = SodiumKeyExchange.CalculateClientSharedSecret(this.KeyPair.GetPublicKey(), this.GetPrivateKey(), resp.PublicKey);
        var shared = SecretBox.Decrypt(kx.ReadSharedSecret, resp.EncryptedSharedSecret);

        this.Plugin.ConfigInfo.GetOrInsertChannel(resp.Channel).SharedSecret = shared;
        this.Plugin.SaveConfig();
    }

    private async Task HandleSendSecrets(SendSecretsResponse resp) {
        if (!this.Plugin.ConfigInfo.Channels.TryGetValue(resp.Channel, out var info) || info.SharedSecret.Length == 0) {
            await this.QueueMessage(new RequestKind.SendSecrets(new SendSecretsRequest {
                RequestId = resp.RequestId,
                EncryptedSharedSecret = null,
            }));
            return;
        }

        var kx = SodiumKeyExchange.CalculateServerSharedSecret(this.KeyPair.GetPublicKey(), this.GetPrivateKey(), resp.PublicKey);
        var encrypted = SecretBox.Encrypt(kx.TransferSharedSecret, info.SharedSecret);
        await this.QueueMessage(new RequestKind.SendSecrets(new SendSecretsRequest {
            RequestId = resp.RequestId,
            EncryptedSharedSecret = encrypted,
        }));
    }

    private void HandleUpdated(UpdatedResponse resp) {
        switch (resp.Kind) {
            case UpdateKind.Name name: {
                if (this.Plugin.ConfigInfo.Channels.TryGetValue(resp.Channel, out var info)) {
                    var newName = Encoding.UTF8.GetString(SecretBox.Decrypt(info.SharedSecret, name.NewName));
                    info.Name = newName;
                    this.Plugin.SaveConfig();
                }

                break;
            }
            default: {
                Plugin.Log.Warning($"Unhandled update kind: {resp.Kind}");
                break;
            }
        }
    }

    private void HandleMemberChange(MemberChangeResponse resp) {
        if (!this.TryGetChannel(resp.Channel, out var channel)) {
            return;
        }

        var channelName = this.Plugin.ConfigInfo.GetName(resp.Channel);
        var isSelf = this.Plugin.PlayerState.CharacterName == resp.Name
                     && this.Plugin.PlayerState.HomeWorld.RowId == resp.World;

        switch (resp.Kind) {
            case MemberChangeKind.Invite: {
                channel.Members.Add(new Member {
                    Name = resp.Name,
                    World = resp.World,
                    Rank = Rank.Invited,
                    Online = true,
                });

                break;
            }
            case MemberChangeKind.InviteCancel: {
                channel.Members.RemoveAll(
                    member => member.Name == resp.Name
                              && member.World == resp.World
                              && member.Rank == Rank.Invited
                );

                if (isSelf) {
                    this.ChannelRanks.Remove(resp.Channel);
                    this.InvitedChannels.Remove(resp.Channel);
                }

                break;
            }
            case MemberChangeKind.InviteDecline: {
                channel.Members.RemoveAll(
                    member => member.Name == resp.Name
                              && member.World == resp.World
                              && member.Rank == Rank.Invited
                );

                if (isSelf) {
                    this.ChannelRanks.Remove(resp.Channel);
                    this.InvitedChannels.Remove(resp.Channel);
                }

                break;
            }
            case MemberChangeKind.Join: {
                var member = channel.Members.FirstOrDefault(member => member.Name == resp.Name && member.World == resp.World);
                if (member != null) {
                    member.Rank = Rank.Member;
                } else {
                    channel.Members.Add(new Member {
                        Name = resp.Name,
                        World = resp.World,
                        Rank = Rank.Member,
                    });
                }

                if (isSelf) {
                    this.ChannelRanks[resp.Channel] = Rank.Member;
                    this.Plugin.ShowInfo($"You have joined \"{channelName}\"");
                } else {
                    var worldName = WorldUtil.WorldName(resp.World);
                    this.Plugin.ShowInfo($"{resp.Name}{PluginUi.CrossWorld}{worldName} has joined \"{channelName}\"");
                }

                break;
            }
            case MemberChangeKind.Kick: {
                channel.Members.RemoveAll(member => member.Name == resp.Name && member.World == resp.World);

                if (isSelf) {
                    this.ChannelRanks.Remove(resp.Channel);
                    this.Plugin.ConfigInfo.RemoveChannelIndex(resp.Channel);
                    this.Plugin.SaveConfig();

                    this.Plugin.ShowInfo($"You have been kicked from \"{channelName}\"");
                } else {
                    var worldName = WorldUtil.WorldName(resp.World);
                    this.Plugin.ShowInfo($"{resp.Name}{PluginUi.CrossWorld}{worldName} has been kicked from \"{channelName}\"");
                }

                break;
            }
            case MemberChangeKind.Leave: {
                channel.Members.RemoveAll(member => member.Name == resp.Name && member.World == resp.World);

                if (isSelf) {
                    this.ChannelRanks.Remove(resp.Channel);
                    this.Plugin.ShowInfo($"You have left \"{channelName}\"");
                } else {
                    var worldName = WorldUtil.WorldName(resp.World);
                    this.Plugin.ShowInfo($"{resp.Name}{PluginUi.CrossWorld}{worldName} has left \"{channelName}\"");
                }

                break;
            }
            case MemberChangeKind.Promote promote: {
                bool wasPromotion;
                var member = channel.Members.FirstOrDefault(member => member.Name == resp.Name && member.World == resp.World);
                if (member != null) {
                    wasPromotion = promote.Rank >= member.Rank;
                    member.Rank = promote.Rank;
                } else {
                    wasPromotion = true;
                    channel.Members.Add(new Member {
                        Name = resp.Name,
                        World = resp.World,
                        Rank = promote.Rank,
                    });
                }

                var verb = wasPromotion ? "promoted" : "demoted";

                if (isSelf) {
                    this.ChannelRanks[resp.Channel] = promote.Rank;
                    this.Plugin.ShowInfo($"You have been {verb} to {promote.Rank} in \"{channelName}\"");
                } else {
                    var worldName = WorldUtil.WorldName(resp.World);
                    this.Plugin.ShowInfo($"{resp.Name}{PluginUi.CrossWorld}{worldName} has been {verb} to {promote.Rank} in \"{channelName}\"");
                }

                break;
            }
            default: {
                throw new ArgumentOutOfRangeException();
            }
        }
    }

    private void HandleDisband(DisbandResponse resp) {
        if (this.Plugin.ConfigInfo.Channels.TryGetValue(resp.Channel, out var info)) {
            this.Plugin.ShowInfo($"\"{info.Name}\" has been disbanded.");
        }

        this.ActuallyLeave(resp.Channel);
    }

    private void HandleList(ListResponse resp) {
        switch (resp) {
            case ListResponse.All all: {
                this.Channels.Clear();
                this.InvitedChannels.Clear();

                foreach (var channel in all.AllChannels) {
                    this.Channels[channel.Id] = channel;

                    var member = channel.Members
                        .FirstOrDefault(member => member.Name == this.Plugin.PlayerState.CharacterName
                                                  && member.World == this.Plugin.PlayerState.HomeWorld.RowId);
                    this.ChannelRanks.Remove(channel.Id);
                    if (member != null) {
                        this.ChannelRanks[channel.Id] = member.Rank;
                    }

                    this.Plugin.ConfigInfo.UpdateChannel(channel);
                }

                foreach (var channel in all.AllInvites) {
                    this.InvitedChannels[channel.Id] = channel;
                    this.ChannelRanks[channel.Id] = Rank.Invited;

                    this.Plugin.ConfigInfo.UpdateChannel(channel);
                    this.Plugin.SaveConfig();
                }

                this.Plugin.SaveConfig();
                break;
            }
            case ListResponse.Channels channels: {
                foreach (var channel in channels.SimpleChannels) {
                    this.Channels[channel.Id] = new Channel {
                        Id = channel.Id,
                        Name = channel.Name,
                        Members = new List<Member>(),
                    };

                    this.ChannelRanks[channel.Id] = channel.Rank;
                    this.Plugin.ConfigInfo.UpdateChannel(channel);
                }

                this.Plugin.SaveConfig();
                break;
            }
            case ListResponse.Invites invites: {
                foreach (var channel in invites.AllInvites) {
                    this.InvitedChannels[channel.Id] = new Channel {
                        Id = channel.Id,
                        Name = channel.Name,
                        Members = new List<Member>(),
                    };

                    this.ChannelRanks[channel.Id] = channel.Rank;
                    this.Plugin.ConfigInfo.UpdateChannel(channel);
                }

                this.Plugin.SaveConfig();
                break;
            }
            case ListResponse.Members members: {
                if (!this.Channels.TryGetValue(members.ChannelId, out var channel)) {
                    break;
                }

                channel.Members = members.AllMembers.ToList();

                var member = channel.Members
                    .FirstOrDefault(member => member.Name == this.Plugin.PlayerState.CharacterName
                                              && member.World == this.Plugin.PlayerState.HomeWorld.RowId);
                this.ChannelRanks.Remove(channel.Id);
                if (member != null) {
                    this.ChannelRanks[channel.Id] = member.Rank;
                }

                break;
            }
        }

        this.Plugin.Commands.ReregisterAll();
        this.Plugin.Ipc.BroadcastChannelNames();
    }

    private static readonly ReadOnlySeString LinkTerminator = new(RawPayload.LinkTerminator.Data);
    private readonly Dictionary<Guid, ReadOnlySeString> TagPayloads = new();

    private void HandleMessage(MessageResponse resp) {
        var config = this.Plugin.ConfigInfo;

        if (!config.Channels.TryGetValue(resp.Channel, out var info)) {
            return;
        }
        
        var outputChannel = this.Plugin.ConfigInfo.ChannelChannels.GetValueOrDefault(resp.Channel, XivChatType.Debug);
        var message = new ReadOnlySeStringSpan(this.Plugin.GameFunctions.ProcessFixedMacros(
            SecretBox.Decrypt(info.SharedSecret, resp.Message),
            this.Plugin.ConfigInfo.ShouldPlaySoundEffectMacros(resp.Channel)));

        if (!TagPayloads.TryGetValue(resp.Channel, out var tagPayload))
        {
            tagPayload = new ReadOnlySeString(PayloadUtil.CreateTagPayload(resp.Channel));

            TagPayloads[resp.Channel] = tagPayload;
        }

        var outputBuilder = SeStringBuilder.SharedPool.Get()
            // Add a tag payload for filtering
            .Append(tagPayload)
            .Append(LinkTerminator);

        var isOutputLinkshell = outputChannel.IsLinkshell();
        var isSelf = resp.Sender == this.Plugin.PlayerState.CharacterName
                     && resp.World == this.Plugin.PlayerState.HomeWorld.RowId;
        var homeWorldsSame = resp.World == this.Plugin.PlayerState.HomeWorld.RowId;
        var homeWorldsSameAndOnHomeWorld = homeWorldsSame && this.Plugin.PlayerState.CurrentWorld.RowId == resp.World;

        var senderBuilder = SeStringBuilder.SharedPool.Get();
       
        if (isSelf)
            senderBuilder.Append(resp.Sender);
        else
            senderBuilder.PushLinkCharacter(resp.Sender, resp.World).Append(resp.Sender).PopLink();
            
        if (!isSelf && !homeWorldsSameAndOnHomeWorld)
        {
            senderBuilder.AppendIcon((uint)BitmapFontIcon.CrossWorld);

            var world = this.Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(resp.World)?.Name;

            if (world != null)
                senderBuilder.Append(world);
            else
                senderBuilder.Append($"[Unknown {resp.World}]");
        }
            
        var sender = senderBuilder.ToReadOnlySeString();

        if (!isOutputLinkshell)
        {
            var colour = config.GetUiColour(resp.Channel);

            outputBuilder.BeginMacro(MacroCode.ColorType).AppendIntExpression(colour).EndMacro();

            var marker = config.GetMarker(resp.Channel) ?? "ECLS?";
            
            outputBuilder.Append($"[{marker}]<");
            outputBuilder.Append(sender);
            outputBuilder.Append("> ");
        }

        outputBuilder.Append(message);

        if (!isOutputLinkshell)
            outputBuilder.BeginMacro(MacroCode.ColorType).AppendIntExpression(0).EndMacro();
        
        this.Plugin.ChatGui.Print(new XivChatEntry {
            Type = outputChannel,
            Name = sender.ToDalamudString(),
            Message = outputBuilder.ToReadOnlySeString().ToDalamudString(),
            Silent = isSelf,
        });

        if (!isSelf && !isOutputLinkshell && this.Plugin.ConfigInfo.ChannelSoundEffects.TryGetValue(resp.Channel, out var notificationSound))
            UIGlobals.PlayChatSoundEffect((uint)notificationSound);
        
        SeStringBuilder.SharedPool.Return(senderBuilder);
        SeStringBuilder.SharedPool.Return(outputBuilder);
    }

    private void HandleInvited(InvitedResponse info) {
        // 1. Decrypt the shared key
        // 2. Decrypt the channel name

        var inviter = info.PublicKey;
        var kx = SodiumKeyExchange.CalculateServerSharedSecret(this.KeyPair.GetPublicKey(), this.GetPrivateKey(), inviter);
        var shared = SecretBox.Decrypt(kx.ReadSharedSecret, info.EncryptedSecret);
        var name = Encoding.UTF8.GetString(SecretBox.Decrypt(shared, info.Channel.Name));

        this.Plugin.ConfigInfo.Channels[info.Channel.Id] = new ChannelInfo {
            Name = name,
            SharedSecret = shared,
        };
        this.InvitedChannels[info.Channel.Id] = info.Channel;
        this.ChannelRanks[info.Channel.Id] = Rank.Invited;
        this.Plugin.SaveConfig();

        this.Plugin.ShowInfo($"Invited to join \"{name}\" by {info.Name}{PluginUi.CrossWorld}{WorldUtil.WorldName(info.World)}");
    }
}
