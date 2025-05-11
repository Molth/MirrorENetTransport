using System;
using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using enet;
using Mirror;
using NativeCollections;
using NativeSockets;
using UnityEngine;
using static enet.ENet;

#pragma warning disable CS8632

namespace ENetTransport
{
    public unsafe class ENetTransport : Transport, PortTransport
    {
        [SerializeField] public ENetTransportOptions Options;

        private int _state;
        private int _operations;
        private NativeArray<ENetTransportPeer> _peers;
        private NativeConcurrentQueue<ENetTransportCommand> _outgoingCommands;
        private NativeConcurrentQueue<ENetTransportCommand> _incomingCommands;
        private byte[] _transportBuffer;
        private int _clientConnected;
        private ushort _clientId;
        private ENetAddress _serverAddress;
        public static bool IsSupported { get; private set; }

        private new void Update()
        {
            if (!NetworkClient.active && !NetworkServer.active)
                return;

            Interlocked.Increment(ref _operations);
            try
            {
                while (_state == (int)ENetTransportState.Started && _incomingCommands.TryDequeue(out var command))
                {
                    ref var peer = ref _peers[command.PeerId];

                    switch ((ENetEventType)command.EventType)
                    {
                        case ENetEventType.ENET_EVENT_TYPE_CONNECT:

                            peer = new ENetTransportPeer
                            {
                                Address = *command.Address,
                                Token = command.Token
                            };

                            free(command.Address);

                            if (NetworkServer.active)
                            {
                                OnServerConnectedWithAddress?.Invoke(command.PeerId + 1, peer.Address.host.ToString());
                            }
                            else if (peer.Address == _serverAddress)
                            {
                                _clientId = command.PeerId;
                                Interlocked.Exchange(ref _clientConnected, 1);
                                OnClientConnected?.Invoke();
                            }

                            break;

                        case ENetEventType.ENET_EVENT_TYPE_DISCONNECT:

                            peer.Token = 0;

                            if (NetworkServer.active)
                                OnServerDisconnected?.Invoke(command.PeerId + 1);
                            else if (command.PeerId == _clientId)
                                OnClientDisconnected?.Invoke();

                            break;

                        case ENetEventType.ENET_EVENT_TYPE_RECEIVE:

                            var packet = command.Packet;
                            var channelId = (packet->flags & (int)ENetPacketFlag.ENET_PACKET_FLAG_RELIABLE) != 0 ? Channels.Reliable : Channels.Unreliable;

                            ReadOnlySpan<byte> buffer;
                            try
                            {
                                buffer = MemoryMarshal.CreateReadOnlySpan(ref *packet->data, (int)packet->dataLength);
                                buffer.CopyTo(_transportBuffer);
                            }
                            finally
                            {
                                enet_packet_destroy(packet);
                            }

                            if (NetworkServer.active)
                                OnServerDataReceived?.Invoke(command.PeerId + 1, new ArraySegment<byte>(_transportBuffer, 0, buffer.Length), channelId);
                            else if (command.PeerId == _clientId)
                                OnClientDataReceived?.Invoke(new ArraySegment<byte>(_transportBuffer, 0, buffer.Length), channelId);

                            break;
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override void OnApplicationQuit()
        {
            base.OnApplicationQuit();
            SocketPal.Cleanup();
        }

        public ushort Port { get; set; } = 7777;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                IsSupported = false;
                return;
            }

            var error = SocketPal.Initialize();
            IsSupported = error == SocketError.Success;
        }

        public override bool Available() => IsSupported;

        private void StartENetHost(bool isServer)
        {
            var options = Options;

            var hostOptions = options.HostOptions;

            ENetAddress address;
            enet_address_set_host_ip(&address, "::");
            address.port = isServer ? options.HostOptions.ServerPort : options.HostOptions.ClientPort;

            var host = enet_host_create(&address, isServer ? (nuint)NetworkManager.singleton.maxConnections : 1, 1, hostOptions.IncomingBandwidth, hostOptions.OutgoingBandwidth);

            if (host == null)
            {
                Interlocked.Exchange(ref _state, (int)ENetTransportState.None);
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }

            host->maximumPacketSize = options.TransportBufferSize;

            _peers = new NativeArray<ENetTransportPeer>((int)host->peerCount);
            _outgoingCommands = new NativeConcurrentQueue<ENetTransportCommand>(1, 1);
            _incomingCommands = new NativeConcurrentQueue<ENetTransportCommand>(1, 1);
            _transportBuffer = ArrayPool<byte>.Shared.Rent((int)options.TransportBufferSize);

            var thread = new Thread(Service) { IsBackground = true };
            thread.Start(new ENetTransportServiceParams
            {
                Host = host,
                Options = options
            });
        }

        private void Service(object? @params)
        {
            var serviceParams = (ENetTransportServiceParams)@params!;

            var options = serviceParams.Options;
            var peerOptions = options.PeerOptions;

            var host = serviceParams.Host;

            Interlocked.Exchange(ref _state, (int)ENetTransportState.Started);

            var @event = new ENetEvent();
            var spinWait = new NativeSpinWait();
            ENetTransportCommand command;
            ENetPeer* peer;

            while (_state == (int)ENetTransportState.Started)
            {
                while (_outgoingCommands.TryDequeue(out command))
                {
                    switch ((ENetEventType)command.EventType)
                    {
                        case ENetEventType.ENET_EVENT_TYPE_CONNECT:

                            peer = enet_host_connect(host, command.Address, 1, 0);

                            enet_peer_ping_interval(peer, peerOptions.PingInterval);
                            enet_peer_timeout(peer, peerOptions.TimeoutLimit, peerOptions.TimeoutMinimum, peerOptions.TimeoutMinimum);

                            free(command.Address);

                            break;
                        case ENetEventType.ENET_EVENT_TYPE_DISCONNECT:

                            peer = &host->peers[command.PeerId];
                            if (command.Token == peer->connectID)
                                enet_peer_disconnect(peer, 0);

                            break;
                        case ENetEventType.ENET_EVENT_TYPE_RECEIVE:

                            peer = &host->peers[command.PeerId];
                            if (command.Token == peer->connectID)
                                enet_peer_send(peer, 0, command.Packet);

                            break;
                    }
                }

                var serviced = false;
                while (!serviced)
                {
                    if (enet_host_check_events(host, &@event) <= 0)
                    {
                        if (enet_host_service(host, &@event, 0) <= 0)
                            break;
                        serviced = true;
                    }

                    peer = @event.peer;

                    command.PeerId = peer->incomingPeerID;
                    command.Token = peer->connectID;

                    switch (@event.type)
                    {
                        case ENetEventType.ENET_EVENT_TYPE_CONNECT:

                            enet_peer_ping_interval(peer, peerOptions.PingInterval);
                            enet_peer_timeout(peer, peerOptions.TimeoutLimit, peerOptions.TimeoutMinimum, peerOptions.TimeoutMinimum);

                            command.EventType = (ushort)ENetEventType.ENET_EVENT_TYPE_CONNECT;

                            command.Address = (ENetAddress*)malloc((nuint)sizeof(ENetAddress));
                            *command.Address = peer->address;

                            _incomingCommands.Enqueue(command);

                            break;

                        case ENetEventType.ENET_EVENT_TYPE_DISCONNECT:

                            command.EventType = (ushort)ENetEventType.ENET_EVENT_TYPE_DISCONNECT;

                            _incomingCommands.Enqueue(command);

                            break;

                        case ENetEventType.ENET_EVENT_TYPE_RECEIVE:

                            command.EventType = (ushort)ENetEventType.ENET_EVENT_TYPE_RECEIVE;

                            command.Packet = @event.packet;

                            _incomingCommands.Enqueue(command);

                            break;
                    }
                }

                spinWait.SpinOnce();
            }

            while (_operations != 0)
                spinWait.SpinOnce();

            for (nuint i = 0; i < host->peerCount; ++i)
            {
                peer = &host->peers[i];
                enet_peer_disconnect_now(peer, 0);
            }

            enet_host_destroy(host);

            _peers.Dispose();

            while (_outgoingCommands.TryDequeue(out command))
                command.Dispose();
            _outgoingCommands.Dispose();

            while (_incomingCommands.TryDequeue(out command))
                command.Dispose();
            _incomingCommands.Dispose();

            ArrayPool<byte>.Shared.Return(_transportBuffer);
            _transportBuffer = null;

            Interlocked.Exchange(ref _state, (int)ENetTransportState.None);
        }

        public override bool ClientConnected() => _clientConnected == 1;

        public override void ClientConnect(string address)
        {
            Interlocked.Increment(ref _operations);
            try
            {
                if (Interlocked.CompareExchange(ref _state, (int)ENetTransportState.Starting, (int)ENetTransportState.None) == (int)ENetTransportState.None)
                {
                    ENetAddress a;
                    if (enet_address_set_host_ip(&a, address) != 0)
                        OnClientDisconnected?.Invoke();

                    a.port = Port;

                    _serverAddress = a;

                    StartENetHost(false);

                    var addr = (ENetAddress*)malloc((nuint)sizeof(ENetAddress));
                    *addr = a;

                    var command = new ENetTransportCommand();
                    command.EventType = (ushort)ENetEventType.ENET_EVENT_TYPE_CONNECT;
                    command.Address = addr;

                    _outgoingCommands.Enqueue(command);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            Interlocked.Increment(ref _operations);
            try
            {
                if (_state == (int)ENetTransportState.Started && _clientConnected == 1)
                {
                    var command = new ENetTransportCommand();
                    command.EventType = (ushort)ENetEventType.ENET_EVENT_TYPE_RECEIVE;

                    ref var peer = ref _peers[_clientId];
                    command.PeerId = _clientId;
                    command.Token = peer.Token;

                    var flags = (uint)(channelId == Channels.Reliable ? ENetPacketFlag.ENET_PACKET_FLAG_RELIABLE : 0);
                    ENetPacket* packet;
                    fixed (byte* data = &MemoryMarshal.GetReference(segment.AsSpan()))
                    {
                        packet = enet_packet_create(data, (nuint)segment.Count, flags);
                    }

                    command.Packet = packet;
                    _outgoingCommands.Enqueue(command);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override void ClientDisconnect()
        {
            Interlocked.Increment(ref _operations);
            try
            {
                if (Interlocked.CompareExchange(ref _clientConnected, 0, 1) == 1)
                    OnClientDisconnected?.Invoke();
                Shutdown();
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override Uri ServerUri() => throw new NotImplementedException();

        public override bool ServerActive() => _state == (int)ENetTransportState.Started;

        public override void ServerStart()
        {
            Interlocked.Increment(ref _operations);
            try
            {
                if (Interlocked.CompareExchange(ref _state, (int)ENetTransportState.Starting, (int)ENetTransportState.None) == (int)ENetTransportState.None)
                {
                    StartENetHost(true);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            Interlocked.Increment(ref _operations);
            try
            {
                if (_state == (int)ENetTransportState.Started)
                {
                    var command = new ENetTransportCommand();
                    command.EventType = (ushort)ENetEventType.ENET_EVENT_TYPE_RECEIVE;

                    command.PeerId = (ushort)(connectionId - 1);

                    ref var peer = ref _peers[command.PeerId];
                    command.Token = peer.Token;

                    var flags = (uint)(channelId == Channels.Reliable ? ENetPacketFlag.ENET_PACKET_FLAG_RELIABLE : 0);
                    ENetPacket* packet;
                    fixed (byte* data = &MemoryMarshal.GetReference(segment.AsSpan()))
                    {
                        packet = enet_packet_create(data, (nuint)segment.Count, flags);
                    }

                    command.Packet = packet;
                    _outgoingCommands.Enqueue(command);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override void ServerDisconnect(int connectionId)
        {
            Interlocked.Increment(ref _operations);
            try
            {
                if (_state == (int)ENetTransportState.Started)
                {
                    var command = new ENetTransportCommand();
                    command.EventType = (ushort)ENetEventType.ENET_EVENT_TYPE_DISCONNECT;

                    command.PeerId = (ushort)(connectionId - 1);

                    ref var peer = ref _peers[command.PeerId];
                    command.Token = peer.Token;

                    _outgoingCommands.Enqueue(command);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            Interlocked.Increment(ref _operations);
            try
            {
                if (_state == (int)ENetTransportState.Started)
                {
                    ref var peer = ref _peers[connectionId - 1];
                    return peer.Address.host.ToString();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }

            return null;
        }

        public override void ServerStop()
        {
            Interlocked.Increment(ref _operations);
            try
            {
                Shutdown();
            }
            finally
            {
                Interlocked.Decrement(ref _operations);
            }
        }

        public override int GetMaxPacketSize(int channelId = Channels.Reliable) => (int)ENET_HOST_DEFAULT_MTU;

        public override void Shutdown()
        {
            if (Interlocked.CompareExchange(ref _state, (int)ENetTransportState.ShuttingDown, (int)ENetTransportState.Started) == (int)ENetTransportState.Started)
                return;

            Interlocked.CompareExchange(ref _state, (int)ENetTransportState.ShuttingDown, (int)ENetTransportState.Starting);
        }
    }
}