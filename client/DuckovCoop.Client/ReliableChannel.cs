using System;
using System.Collections.Generic;

namespace DuckovCoop.Client
{
    public struct Delivered
    {
        public OpCode Op;
        public byte[] Payload;
    }

    /// <summary>Mirrors the C++ ReliableChannel, including fragmentation for large payloads.</summary>
    public sealed class ReliableChannel
    {
        public const int MaxChunk = 1100;
        public const long AssemblyTimeoutMs = 5000;

        private const uint MinRto = 100;
        private const uint MaxRto = 2000;
        private const int MaxRetries = 10;
        private const int FragmentHeaderSize = 8;
        private const int MaxAssemblies = 4;

        private sealed class Pending
        {
            public uint Seq;
            public FrameType Type;
            public byte[] Body; // bytes after the 20-byte header (includes the opcode byte)
            public long SentAtMs;
            public int Retries;
        }

        private sealed class Assembly
        {
            public OpCode Op;
            public ushort Count;
            public ushort Received;
            public long FirstSeenMs;
            public readonly Dictionary<ushort, byte[]> Chunks = new Dictionary<ushort, byte[]>();
        }

        private readonly byte _id;
        private readonly bool _reliable;
        private readonly bool _ordered;

        private uint _nextSeq;
        private ushort _nextMessageId = 1;
        private readonly List<Pending> _pending = new List<Pending>();

        private uint _recvNext = 1;
        private uint _lastUnreliableSeq;
        private uint _lastReliableSeq;
        private readonly Dictionary<uint, Delivered> _recvBuffer = new Dictionary<uint, Delivered>();
        private readonly Dictionary<ushort, Assembly> _assemblies = new Dictionary<ushort, Assembly>();

        private uint _ack;
        private uint _ackBits;

        private double _srtt;
        private double _rttvar;
        private uint _rto = 300;
        private bool _hasRtt;

        public ReliableChannel(byte id, bool reliable, bool ordered)
        {
            _id = id;
            _reliable = reliable;
            _ordered = ordered;
        }

        public uint RtoMs => _rto;
        public int PendingCount => _pending.Count;

        public byte[] BuildFrame(OpCode op, byte[] payload, uint sessionId, long nowMs, uint forceSeq = 0)
        {
            var frames = BuildFrames(op, payload, sessionId, nowMs);
            return frames.Count == 0 ? Array.Empty<byte>() : frames[0];
        }

        public List<byte[]> BuildFrames(OpCode op, byte[] payload, uint sessionId, long nowMs)
        {
            var frames = new List<byte[]>(1);
            payload ??= Array.Empty<byte>();

            if (payload.Length + 1 <= MaxChunk)
            {
                frames.Add(BuildSingle(op, payload, sessionId, nowMs));
                return frames;
            }

            // Fragmentation is only supported on unordered channels (see C++ counterpart).
            if (_ordered)
                return frames;

            var count = (payload.Length + MaxChunk - 1) / MaxChunk;
            if (count > ushort.MaxValue)
                return frames;

            var messageId = _nextMessageId++;

            for (var i = 0; i < count; i++)
            {
                var offset = i * MaxChunk;
                var size = Math.Min(MaxChunk, payload.Length - offset);
                frames.Add(BuildFragment(op, messageId, (ushort)i, (ushort)count, payload, offset, size, sessionId, nowMs));
            }

            return frames;
        }

        private byte[] BuildSingle(OpCode op, byte[] payload, uint sessionId, long nowMs)
        {
            var seq = ++_nextSeq;
            var header = Header.Create(_id, seq, _ack, _ackBits, sessionId);
            var frame = FrameCodec.Encode(header, op, payload);

            if (_reliable)
                _pending.Add(new Pending
                {
                    Seq = seq,
                    Type = FrameType.Data,
                    Body = Slice(frame, FrameCodec.HeaderSize),
                    SentAtMs = nowMs,
                });

            return frame;
        }

        private byte[] BuildFragment(OpCode op, ushort messageId, ushort index, ushort count,
                                     byte[] payload, int offset, int size, uint sessionId, long nowMs)
        {
            var seq = ++_nextSeq;
            var header = Header.Create(_id, seq, _ack, _ackBits, sessionId);
            header.Type = FrameType.Fragment;

            var fragment = new byte[FragmentHeaderSize + size];
            fragment[0] = (byte)messageId;
            fragment[1] = (byte)(messageId >> 8);
            fragment[2] = (byte)index;
            fragment[3] = (byte)(index >> 8);
            fragment[4] = (byte)count;
            fragment[5] = (byte)(count >> 8);
            Buffer.BlockCopy(payload, offset, fragment, FragmentHeaderSize, size);

            var frame = FrameCodec.Encode(header, op, fragment);

            if (_reliable)
                _pending.Add(new Pending
                {
                    Seq = seq,
                    Type = FrameType.Fragment,
                    Body = Slice(frame, FrameCodec.HeaderSize),
                    SentAtMs = nowMs,
                });

            return frame;
        }

        private static byte[] Slice(byte[] source, int offset)
        {
            var result = new byte[source.Length - offset];
            Buffer.BlockCopy(source, offset, result, 0, result.Length);
            return result;
        }

        public void OnFrame(OpCode op, in Header header, byte[] payload, long nowMs, List<Delivered> output)
        {
            ProcessAck(header.Ack, header.AckBits, nowMs);
            PruneAssemblies(nowMs);

            if (header.Seq > 0)
            {
                if (header.Seq > _ack)
                {
                    var shift = header.Seq - _ack;
                    _ackBits = shift >= 32 ? 0u : (_ackBits << (int)shift);
                    if (shift >= 1 && shift <= 32)
                        _ackBits |= 1u << (int)(shift - 1);
                    _ack = header.Seq;
                }
                else if (_ack - header.Seq <= 32)
                {
                    _ackBits |= 1u << (int)(_ack - header.Seq - 1);
                }
            }

            if (header.Seq == 0)
                return;

            if (header.Type == FrameType.Fragment)
            {
                HandleFragment(op, header, payload, nowMs, output);
                return;
            }

            if (!_reliable)
            {
                if (header.Seq > _lastUnreliableSeq)
                {
                    _lastUnreliableSeq = header.Seq;
                    output.Add(new Delivered { Op = op, Payload = payload });
                }
                return;
            }

            if (_ordered)
            {
                DeliverOrdered(op, header.Seq, payload, output);
                return;
            }

            if (header.Seq <= _lastReliableSeq)
                return;

            _lastReliableSeq = header.Seq;
            output.Add(new Delivered { Op = op, Payload = payload });
        }

        private void HandleFragment(OpCode op, in Header header, byte[] payload, long nowMs, List<Delivered> output)
        {
            if (payload == null || payload.Length < FragmentHeaderSize)
                return;

            var messageId = (ushort)(payload[0] | (payload[1] << 8));
            var index = (ushort)(payload[2] | (payload[3] << 8));
            var count = (ushort)(payload[4] | (payload[5] << 8));

            if (count == 0 || index >= count)
                return;

            if (!_assemblies.TryGetValue(messageId, out var assembly))
            {
                if (_assemblies.Count >= MaxAssemblies)
                    return;

                assembly = new Assembly { Count = count, Op = op, FirstSeenMs = nowMs };
                _assemblies[messageId] = assembly;
            }

            if (!assembly.Chunks.ContainsKey(index))
            {
                var chunk = new byte[payload.Length - FragmentHeaderSize];
                Buffer.BlockCopy(payload, FragmentHeaderSize, chunk, 0, chunk.Length);
                assembly.Chunks[index] = chunk;
                assembly.Received++;
            }

            if (assembly.Received != assembly.Count)
                return;

            var total = 0;
            for (ushort i = 0; i < assembly.Count; i++)
            {
                if (!assembly.Chunks.TryGetValue(i, out var chunk))
                {
                    _assemblies.Remove(messageId);
                    return;
                }
                total += chunk.Length;
            }

            var message = new byte[total];
            var offset = 0;
            for (ushort i = 0; i < assembly.Count; i++)
            {
                var chunk = assembly.Chunks[i];
                Buffer.BlockCopy(chunk, 0, message, offset, chunk.Length);
                offset += chunk.Length;
            }

            _assemblies.Remove(messageId);

            if (!_reliable)
            {
                if (header.Seq > _lastUnreliableSeq)
                {
                    _lastUnreliableSeq = header.Seq;
                    output.Add(new Delivered { Op = op, Payload = message });
                }
                return;
            }

            if (_ordered)
                DeliverOrdered(op, header.Seq, message, output);
            else
                output.Add(new Delivered { Op = op, Payload = message });
        }

        private void PruneAssemblies(long nowMs)
        {
            if (_assemblies.Count == 0)
                return;

            List<ushort> expired = null;
            foreach (var pair in _assemblies)
            {
                if (nowMs - pair.Value.FirstSeenMs <= AssemblyTimeoutMs)
                    continue;
                (expired ??= new List<ushort>()).Add(pair.Key);
            }

            if (expired == null)
                return;

            foreach (var id in expired)
                _assemblies.Remove(id);
        }

        private void DeliverOrdered(OpCode op, uint seq, byte[] payload, List<Delivered> output)
        {
            if (seq < _recvNext)
                return;

            if (seq == _recvNext)
            {
                output.Add(new Delivered { Op = op, Payload = payload });
                _recvNext++;

                while (_recvBuffer.TryGetValue(_recvNext, out var buffered))
                {
                    output.Add(buffered);
                    _recvBuffer.Remove(_recvNext);
                    _recvNext++;
                }
                return;
            }

            if (_recvBuffer.Count < 256 && !_recvBuffer.ContainsKey(seq))
                _recvBuffer[seq] = new Delivered { Op = op, Payload = payload };
        }

        private void ProcessAck(uint ack, uint ackBits, long nowMs)
        {
            if (ack == 0)
                return;

            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];
                var acked = false;

                if (pending.Seq == ack)
                    acked = true;
                else if (pending.Seq < ack && ack - pending.Seq <= 32)
                {
                    var bit = ack - pending.Seq - 1;
                    acked = (ackBits & (1u << (int)bit)) != 0;
                }

                if (!acked)
                    continue;

                if (nowMs >= pending.SentAtMs)
                    UpdateRtt(nowMs - pending.SentAtMs);
                _pending.RemoveAt(i);
            }
        }

        private void UpdateRtt(long sampleMs)
        {
            var sample = (double)sampleMs;
            if (!_hasRtt)
            {
                _srtt = sample;
                _rttvar = sample / 2.0;
                _hasRtt = true;
            }
            else
            {
                _rttvar = 0.75 * _rttvar + 0.25 * Math.Abs(_srtt - sample);
                _srtt = 0.875 * _srtt + 0.125 * sample;
            }

            var rto = _srtt + 4.0 * _rttvar;
            _rto = (uint)Math.Max(MinRto, Math.Min(MaxRto, rto));
        }

        public void CollectRetransmits(uint sessionId, long nowMs, List<byte[]> output)
        {
            if (!_reliable)
                return;

            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];
                if (nowMs - pending.SentAtMs < _rto)
                    continue;

                if (++pending.Retries > MaxRetries)
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                pending.SentAtMs = nowMs;

                var header = Header.Create(_id, pending.Seq, _ack, _ackBits, sessionId);
                header.Type = pending.Type;

                var headerBytes = FrameCodec.EncodeHeader(header);
                var frame = new byte[headerBytes.Length + pending.Body.Length];
                Buffer.BlockCopy(headerBytes, 0, frame, 0, headerBytes.Length);
                Buffer.BlockCopy(pending.Body, 0, frame, headerBytes.Length, pending.Body.Length);
                output.Add(frame);
            }
        }
    }
}
