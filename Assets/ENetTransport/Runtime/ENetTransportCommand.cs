using System;
using System.Runtime.InteropServices;
using enet;
using UnityEngine;
using static enet.ENet;

namespace ENetTransport
{
    [Serializable]
    public struct ENetTransportOptions
    {
        [Range(4 * 1024, 32 * 1024)] public uint TransportBufferSize;
        public ENetTransportHostOptions HostOptions;
        public ENetTransportPeerOptions PeerOptions;
    }

    [Serializable]
    public struct ENetTransportHostOptions
    {
        public ushort ServerPort;
        public ushort ClientPort;
        public uint IncomingBandwidth;
        public uint OutgoingBandwidth;
    }

    [Serializable]
    public struct ENetTransportPeerOptions
    {
        public uint PingInterval;
        public uint TimeoutLimit;
        public uint TimeoutMinimum;
        public uint TimeoutMaximum;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct ENetTransportPeer
    {
        [FieldOffset(0)] public ENetAddress Address;
        [FieldOffset(20)] public uint Token;
    }

    public unsafe struct ENetTransportServiceParams
    {
        public ENetHost* Host;
        public ENetTransportOptions Options;
    }
    
    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct ENetTransportCommand : IDisposable
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(2)] public ushort PeerId;
        [FieldOffset(4)] public uint Token;

        [FieldOffset(8)] public ENetAddress* Address;
        [FieldOffset(8)] public ENetPacket* Packet;

        public void Dispose()
        {
            switch ((ENetEventType)EventType)
            {
                case ENetEventType.ENET_EVENT_TYPE_NONE:
                    break;
                case ENetEventType.ENET_EVENT_TYPE_CONNECT:
                case ENetEventType.ENET_EVENT_TYPE_DISCONNECT:
                    enet_free(Address);
                    break;
                case ENetEventType.ENET_EVENT_TYPE_RECEIVE:
                    enet_packet_destroy(Packet);
                    break;
            }
        }
    }
}