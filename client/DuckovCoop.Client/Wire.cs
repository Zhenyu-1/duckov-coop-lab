using System;
using System.Collections.Generic;
using System.Text;

namespace DuckovCoop.Client
{
    public enum OpCode : byte
    {
        CLogin = 1,
        CResume = 2,
        CJoin = 3,
        CInput = 4,
        CAnim = 5,
        CFire = 6,
        CHit = 7,
        CInteract = 8,
        CLoot = 9,
        CExtract = 10,
        CAiState = 11,
        CContainerView = 12,
        CStartRaid = 13,
        CPlayerSpawn = 14,
        CHitReport = 15,
        CLeaveRaid = 17,
        CLoadout = 18,
        CMirrorAck = 19,
        CHitEvent = 20,
        CAiAnim = 21,
        CAiLoadout = 22,
        CPlayerStats = 23,
        CShootFact = 24,
        CUseConsumable = 25,
        CAiAim = 26,
        CAiStateSample = 27,
        CAiLoadoutV7 = 28,
        CAiShot = 29,
        CPlayerStateSample = 30,
        CPlayerLoadoutV8 = 31,
        CPlayerShot = 32,
        CPlayerFaceV8 = 33,

        // v9 item contract (SPEC-2026-11-13-item-protocol-v9).
        CItemTransfer = 34,
        CContainerOpen = 35,
        CWorldItemPickup = 36,
        CWorldItemDrop = 37,
        CInventorySync = 38,
        // P1 item management (contract 2.2.0): server-side sort/merge/reorder.
        CItemArrange = 39,
        CItemConsume = 40,
        CItemReload = 41,
        CItemWeaponFire = 42,
        CItemDurability = 43,
        CBackpackCapacity = 44,
        // M2-v10 scene objects + explosion facts.
        CSceneObjectState = 45,
        CExplosion = 46,
        // M2-v12 raid lobby: per-player ready state and the host's force reset.
        CPlayerReady = 47,
        CForceResetRaid = 48,
        /// <summary>Contract 2.15.0: detach one attachment into the backpack.</summary>
        CItemModDetach = 49,
        /// <summary>Contract 2.17.0: plug one backpack item into an attachment slot.</summary>
        CItemModAttach = 50,
        /// <summary>Contract 2.18.0 (M2-v12): presentation-only bullet impact fact.</summary>
        CImpactFx = 51,

        SLogin = 101,
        SResume = 102,
        SJoin = 103,
        SFire = 106,
        SHit = 107,
        SInteract = 108,
        SLoot = 109,
        SExtract = 110,

        SLoadout = 118,
        SMirror = 119,

        SSnapshot = 150,
        SAiState = 151,
        SPlayerEvent = 152,
        SObjectState = 153,
        SAiEvent = 154,
        SKick = 155,
        SShutdown = 156,
        SRaidPhase = 157,
        SContainerView = 158,
        SRaidStart = 159,
        SSpawnAssign = 160,
        SCombatEvent = 161,
        SAiSpawn = 162,
        SAiTarget = 163,
        SAiDespawn = 164,
        SAiAnim = 165,
        SAiHealth = 166,
        SPlayerBuff = 167,
        SRaidRules = 168,
        SAiLoadout = 169,
        SAiAim = 170,
        SAiStateSample = 171,
        SAiLoadoutV7 = 172,
        SAiShot = 173,
        SPlayerStateSample = 174,
        SPlayerLoadoutV8 = 175,
        SPlayerShot = 176,
        SPlayerFaceV8 = 177,

        // v9 item contract (SPEC-2026-11-13-item-protocol-v9).
        SInitialInventory = 178,
        SItemTransfer = 179,
        SContainerContents = 180,
        SWorldItemPickup = 181,
        SWorldItemDrop = 182,
        SWorldItemSnapshot = 183,
        SWorldItemSpawn = 184,
        SWorldItemDespawn = 185,
        SInventorySnapshot = 186,
        // P1 item management (contract 2.2.0).
        SItemArrange = 187,
        SItemConsume = 188,
        // P3/D6 corpse containers (contract 2.3.0).
        SCorpseSpawn = 189,
        SCorpseDespawn = 190,
        // P1 phase 3 item detail side channel (contract 2.4.0).
        SItemDetail = 191,
        SItemReloadResult = 192,
        // M2-v10 explosion broadcast (presentation for non-throwers).
        SExplosion = 193,
        SRespawn = 194,
        // M2-v12 raid lobby: ready-state broadcast, force-reset notice and control ack.
        SPlayerReady = 195,
        SForceResetRaid = 196,
        SRaidControlResult = 197,
        // Contract 2.14.0 / wire 12: slot-keyed item attachments.
        SItemMods = 198,
        /// <summary>Contract 2.15.0: result of <see cref="CItemModDetach"/>.</summary>
        SItemModDetachResult = 199,
        /// <summary>Contract 2.17.0: result of <see cref="CItemModAttach"/>.</summary>
        SItemModAttachResult = 200,
        /// <summary>Contract 2.18.0 (M2-v12): relayed bullet impact fact (no gameplay effect).</summary>
        SImpactFx = 201,
    }

    public enum FrameType : byte
    {
        Data = 0,
        AckOnly = 1,
        Handshake = 2,
        Fragment = 3,
        Ping = 4,
        Pong = 5,
        Close = 6,
    }

    public struct Header
    {
        public const byte ProtocolVersion = 16;

        public byte Version;
        public FrameType Type;
        public byte Channel;
        public byte Flags;
        public uint SessionId;
        public uint Seq;
        public uint Ack;
        public uint AckBits;

        public static Header Create(byte channel, uint seq, uint ack, uint ackBits, uint sessionId)
            => new Header
            {
                Version = ProtocolVersion,
                Type = FrameType.Data,
                Channel = channel,
                Seq = seq,
                Ack = ack,
                AckBits = ackBits,
                SessionId = sessionId,
            };
    }

    public static class FrameCodec
    {
        public const int HeaderSize = 20;
        public const int MaxPayload = 1200;

        public static byte[] EncodeHeader(in Header header)
        {
            var buffer = new byte[HeaderSize];
            buffer[0] = header.Version;
            buffer[1] = (byte)header.Type;
            buffer[2] = header.Channel;
            buffer[3] = header.Flags;
            WriteU32(buffer, 4, header.SessionId);
            WriteU32(buffer, 8, header.Seq);
            WriteU32(buffer, 12, header.Ack);
            WriteU32(buffer, 16, header.AckBits);
            return buffer;
        }

        public static byte[] Encode(in Header header, OpCode op, byte[] payload)
        {
            var bodyLength = 1 + (payload?.Length ?? 0);
            var frame = new byte[HeaderSize + bodyLength];
            var headerBytes = EncodeHeader(header);
            Buffer.BlockCopy(headerBytes, 0, frame, 0, HeaderSize);
            frame[HeaderSize] = (byte)op;
            if (payload != null && payload.Length > 0)
                Buffer.BlockCopy(payload, 0, frame, HeaderSize + 1, payload.Length);
            return frame;
        }

        public static bool TryDecode(byte[] data, out Header header, out OpCode op, out ArraySegment<byte> payload)
        {
            header = default;
            op = default;
            payload = default;

            if (data == null || data.Length < HeaderSize + 1)
                return false;

            header.Version = data[0];
            if (header.Version != Header.ProtocolVersion)
                return false;

            header.Type = (FrameType)data[1];
            header.Channel = data[2];
            header.Flags = data[3];
            header.SessionId = ReadU32(data, 4);
            header.Seq = ReadU32(data, 8);
            header.Ack = ReadU32(data, 12);
            header.AckBits = ReadU32(data, 16);

            op = (OpCode)data[HeaderSize];
            payload = new ArraySegment<byte>(data, HeaderSize + 1, data.Length - HeaderSize - 1);
            return true;
        }

        internal static void WriteU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static uint ReadU32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                          | (buffer[offset + 1] << 8)
                          | (buffer[offset + 2] << 16)
                          | (buffer[offset + 3] << 24));
        }
    }

    /// <summary>Little-endian writer; strings are u16 length + UTF-8 (NOT the .NET 7-bit format).</summary>
    public sealed class Writer
    {
        private readonly List<byte> _buffer = new List<byte>(64);

        public byte[] ToArray() => _buffer.ToArray();
        public int Length => _buffer.Count;

        public void U8(byte v) => _buffer.Add(v);

        public void U16(ushort v)
        {
            _buffer.Add((byte)v);
            _buffer.Add((byte)(v >> 8));
        }

        public void U32(uint v)
        {
            for (var i = 0; i < 4; i++)
                _buffer.Add((byte)(v >> (8 * i)));
        }

        public void U64(ulong v)
        {
            U32((uint)v);
            U32((uint)(v >> 32));
        }

        public void I16(short v) => U16((ushort)v);
        public void I32(int v) => U32((uint)v);
        public void I64(long v) => U64((ulong)v);

        public void F32(float v)
        {
            var bytes = BitConverter.GetBytes(v);
            _buffer.AddRange(bytes);
        }

        public void Str(string value)
        {
            value ??= string.Empty;
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException("string too long");
            U16((ushort)bytes.Length);
            _buffer.AddRange(bytes);
        }

        public void Raw(byte[] value)
        {
            if (value != null && value.Length > 0)
                _buffer.AddRange(value);
        }
    }

    /// <summary>Bounds-checked reader; Ok becomes false on overflow.</summary>
    public sealed class Reader
    {
        private readonly byte[] _data;
        private readonly int _offset;
        private readonly int _length;
        private int _position;
        private bool _ok = true;

        public Reader(byte[] data, int offset, int length)
        {
            _data = data;
            _offset = offset;
            _length = length;
        }

        public bool Ok => _ok;
        public int Remaining => _ok ? _length - _position : 0;

        private bool Need(int n)
        {
            if (!_ok || _position + n > _length)
            {
                _ok = false;
                return false;
            }
            return true;
        }

        public byte U8()
        {
            if (!Need(1)) return 0;
            return _data[_offset + _position++];
        }

        public ushort U16()
        {
            if (!Need(2)) return 0;
            var p = _offset + _position;
            _position += 2;
            return (ushort)(_data[p] | (_data[p + 1] << 8));
        }

        public uint U32()
        {
            if (!Need(4)) return 0;
            var p = _offset + _position;
            _position += 4;
            return FrameCodec.ReadU32(_data, p);
        }

        public ulong U64()
        {
            var lo = U32();
            var hi = U32();
            return lo | ((ulong)hi << 32);
        }

        public short I16() => (short)U16();
        public int I32() => (int)U32();
        public long I64() => (long)U64();

        public float F32()
        {
            if (!Need(4)) return 0f;
            var p = _offset + _position;
            _position += 4;
            return BitConverter.ToSingle(_data, p);
        }

        public string Str()
        {
            var len = U16();
            if (!Need(len)) return string.Empty;
            var s = Encoding.UTF8.GetString(_data, _offset + _position, len);
            _position += len;
            return s;
        }

        public byte[] Bytes(int length)
        {
            var result = new byte[Math.Max(0, length)];
            if (!Need(length)) return result;
            Buffer.BlockCopy(_data, _offset + _position, result, 0, length);
            _position += length;
            return result;
        }
    }
}

