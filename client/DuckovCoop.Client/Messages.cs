using System;
using System.Collections.Generic;
using System.Text;

namespace DuckovCoop.Client
{
    public sealed class LoginRequest
    {
        public string Account = string.Empty;
        public string PasswordHash = string.Empty;
        public string ClientVersion = string.Empty;
        public byte[] ContentHash = new byte[32]; // ADR-020 option B: monster catalog hash

        public void Write(Writer w)
        {
            w.Str(Account);
            w.Str(PasswordHash);
            w.Str(ClientVersion);
            w.Raw(ContentHash);
        }

        public static LoginRequest Read(Reader r) => new LoginRequest
        {
            Account = r.Str(),
            PasswordHash = r.Str(),
            ClientVersion = r.Str(),
            ContentHash = r.Bytes(32),
        };
    }

    public sealed class LoginResult
    {
        public byte Code;
        public string Token = string.Empty;
        public long ExpiresAtMs;

        public void Write(Writer w)
        {
            w.U8(Code);
            w.Str(Token);
            w.I64(ExpiresAtMs);
        }

        public static LoginResult Read(Reader r) => new LoginResult
        {
            Code = r.U8(),
            Token = r.Str(),
            ExpiresAtMs = r.I64(),
        };
    }

    public sealed class JoinRequest
    {
        public string Token = string.Empty;

        public void Write(Writer w) => w.Str(Token);

        public static JoinRequest Read(Reader r) => new JoinRequest { Token = r.Str() };
    }

    public sealed class JoinResult
    {
        public byte Code;
        public ushort PlayerId;
        public ulong RaidId;
        public string SceneId = string.Empty;
        public int StartTick;
        public byte TickRate;

        public void Write(Writer w)
        {
            w.U8(Code);
            w.U16(PlayerId);
            w.U64(RaidId);
            w.Str(SceneId);
            w.I32(StartTick);
            w.U8(TickRate);
        }

        public static JoinResult Read(Reader r) => new JoinResult
        {
            Code = r.U8(),
            PlayerId = r.U16(),
            RaidId = r.U64(),
            SceneId = r.Str(),
            StartTick = r.I32(),
            TickRate = r.U8(),
        };
    }

    public sealed class Input
    {
        public uint Seq;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;
        public short Pitch;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U32(Seq);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Yaw);
            w.I16(Pitch);
            w.U8(Flags);
        }

        public static Input Read(Reader r) => new Input
        {
            Seq = r.U32(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Yaw = r.U16(),
            Pitch = r.I16(),
            Flags = r.U8(),
        };
    }

    /// <summary>Client -> server: simulator reports AI transform only (never health).</summary>
    public sealed class AiState
    {
        public uint AiId;
        public ushort OwnerPlayerId;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U16(OwnerPlayerId);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Yaw);
            w.U8(Flags);
        }

        public static AiState Read(Reader r) => new AiState
        {
            AiId = r.U32(),
            OwnerPlayerId = r.U16(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Yaw = r.U16(),
            Flags = r.U8(),
        };
    }

    public struct PlayerSnapshot
    {
        public ushort PlayerId;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;
        public byte Flags;
        public ushort Hp;
        /// <summary>Contract 2.16.0 / wire 14: server max HP (0 = unknown, keep the local one).</summary>
        public ushort MaxHp;
        public string Scene;
    }

    public sealed class LootRequest
    {
        public ulong CorrelationId;
        public uint ContainerId;
        public ushort Slot;
        public byte Action; // 0 take, 1 put
        public uint ItemUid;
        public uint Count;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ContainerId);
            w.U16(Slot);
            w.U8(Action);
            w.U32(ItemUid);
            w.U32(Count);
        }

        public static LootRequest Read(Reader r) => new LootRequest
        {
            CorrelationId = r.U64(),
            ContainerId = r.U32(),
            Slot = r.U16(),
            Action = r.U8(),
            ItemUid = r.U32(),
            Count = r.U32(),
        };
    }

    public sealed class LootResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint ItemUid;
        public uint ItemId;
        public uint Count;
        public ushort Slot;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(ItemUid);
            w.U32(ItemId);
            w.U32(Count);
            w.U16(Slot);
        }

        public static LootResult Read(Reader r) => new LootResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
            ItemUid = r.U32(),
            ItemId = r.U32(),
            Count = r.U32(),
            Slot = r.U16(),
        };
    }

    public sealed class ExtractRequest
    {
        public ulong CorrelationId;

        public void Write(Writer w) => w.U64(CorrelationId);

        public static ExtractRequest Read(Reader r) => new ExtractRequest { CorrelationId = r.U64() };
    }

    public sealed class ExtractResult
    {
        public ulong CorrelationId;
        public byte Code;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
        }

        public static ExtractResult Read(Reader r) => new ExtractResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
        };
    }

    /// <summary><c>CPlayerReady</c> (opcode 47): one non-host player toggles readiness.</summary>
    public sealed class PlayerReady
    {
        public bool Ready;

        public void Write(Writer w) => w.U8(Ready ? (byte)1 : (byte)0);

        public static PlayerReady Read(Reader r) => new PlayerReady { Ready = r.U8() != 0 };
    }

    /// <summary><c>SPlayerReady</c> (opcode 195): the server's ready flag for one player.</summary>
    public sealed class PlayerReadyState
    {
        public ushort PlayerId;
        public bool Ready;

        public void Write(Writer w)
        {
            w.U16(PlayerId);
            w.U8(Ready ? (byte)1 : (byte)0);
        }

        public static PlayerReadyState Read(Reader r) => new PlayerReadyState
        {
            PlayerId = r.U16(),
            Ready = r.U8() != 0,
        };
    }

    /// <summary><c>CForceResetRaid</c> (opcode 48): empty payload; host-only.</summary>
    public sealed class ForceResetRaid
    {
        public void Write(Writer w)
        {
        }

        public static ForceResetRaid Read(Reader r) => new ForceResetRaid();
    }

    /// <summary><c>SForceResetRaid</c> (opcode 196): the raid was force-ended.</summary>
    public sealed class ForceResetRaidState
    {
        public ushort ByPlayerId;

        public void Write(Writer w) => w.U16(ByPlayerId);

        public static ForceResetRaidState Read(Reader r) => new ForceResetRaidState
        {
            ByPlayerId = r.U16(),
        };
    }

    /// <summary>Action field values for <see cref="RaidControlResult"/>.</summary>
    public enum RaidControlAction : byte
    {
        StartRaid = 0,
        ForceReset = 1,
    }

    /// <summary><c>SRaidControlResult</c> (opcode 197): explicit accept/reject for a host command.</summary>
    public sealed class RaidControlResult
    {
        public byte Action;
        public byte Code;
        public ushort PlayerId;

        public void Write(Writer w)
        {
            w.U8(Action);
            w.U8(Code);
            w.U16(PlayerId);
        }

        public static RaidControlResult Read(Reader r) => new RaidControlResult
        {
            Action = r.U8(),
            Code = r.U8(),
            PlayerId = r.U16(),
        };
    }

    public sealed class PlayerEvent
    {
        public ulong EventId;
        public ushort PlayerId;
        public byte Type;

        public void Write(Writer w)
        {
            w.U64(EventId);
            w.U16(PlayerId);
            w.U8(Type);
        }

        public static PlayerEvent Read(Reader r) => new PlayerEvent
        {
            EventId = r.U64(),
            PlayerId = r.U16(),
            Type = r.U8(),
        };
    }

    public sealed class RaidPhaseMessage
    {
        public byte Phase;
        public int Tick;

        public void Write(Writer w)
        {
            w.U8(Phase);
            w.I32(Tick);
        }

        public static RaidPhaseMessage Read(Reader r) => new RaidPhaseMessage
        {
            Phase = r.U8(),
            Tick = r.I32(),
        };
    }

    public sealed class ContainerViewRequest
    {
        public ulong CorrelationId;
        public uint ContainerId;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ContainerId);
        }

        public static ContainerViewRequest Read(Reader r) => new ContainerViewRequest
        {
            CorrelationId = r.U64(),
            ContainerId = r.U32(),
        };
    }

    public struct ContainerViewEntry
    {
        public ushort Slot;
        public uint ItemUid;
        public uint ItemId;
        public uint Count;
    }

    public sealed class ContainerView
    {
        public ulong CorrelationId;
        public uint ContainerId;
        public ushort Capacity;
        public ContainerViewEntry[] Entries = Array.Empty<ContainerViewEntry>();

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ContainerId);
            w.U16(Capacity);
            var count = (byte)Math.Min(Entries.Length, byte.MaxValue);
            w.U8(count);
            for (var i = 0; i < count; i++)
            {
                var entry = Entries[i];
                w.U16(entry.Slot);
                w.U32(entry.ItemUid);
                w.U32(entry.ItemId);
                w.U32(entry.Count);
            }
        }

        public static ContainerView Read(Reader r)
        {
            var view = new ContainerView
            {
                CorrelationId = r.U64(),
                ContainerId = r.U32(),
                Capacity = r.U16(),
            };

            var count = r.U8();
            var entries = new ContainerViewEntry[count];
            for (var i = 0; i < count; i++)
            {
                entries[i] = new ContainerViewEntry
                {
                    Slot = r.U16(),
                    ItemUid = r.U32(),
                    ItemId = r.U32(),
                    Count = r.U32(),
                };
            }

            view.Entries = entries;
            return view;
        }
    }

    public sealed class RaidStart
    {
        public uint SceneIndex;
        public string SceneName = string.Empty;
        public uint RaidEpoch;

        public void Write(Writer w)
        {
            w.U32(SceneIndex);
            w.Str(SceneName);
            w.U32(RaidEpoch);
        }

        public static RaidStart Read(Reader r) => new RaidStart
        {
            SceneIndex = r.U32(),
            SceneName = r.Str(),
            RaidEpoch = r.U32(),
        };
    }

    public sealed class PlayerSpawn
    {
        public string SceneName = string.Empty;
        public int X;
        public int Y;
        public int Z;
        public uint RaidEpoch;

        public void Write(Writer w)
        {
            w.Str(SceneName);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U32(RaidEpoch);
        }

        public static PlayerSpawn Read(Reader r) => new PlayerSpawn
        {
            SceneName = r.Str(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            RaidEpoch = r.U32(),
        };
    }

    public sealed class LoadoutEntry
    {
        public byte Slot;
        public uint ItemId;
    }

    public sealed class LoadoutRequest
    {
        public ulong CorrelationId;
        public LoadoutEntry[] Entries = Array.Empty<LoadoutEntry>();

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            var count = (byte)Math.Min(Entries.Length, byte.MaxValue);
            w.U8(count);
            for (var i = 0; i < count; i++)
            {
                w.U8(Entries[i].Slot);
                w.U32(Entries[i].ItemId);
            }
        }

        public static LoadoutRequest Read(Reader r)
        {
            var request = new LoadoutRequest { CorrelationId = r.U64() };
            var count = r.U8();
            var entries = new List<LoadoutEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(new LoadoutEntry { Slot = r.U8(), ItemId = r.U32() });
            request.Entries = entries.ToArray();
            return request;
        }
    }

    public sealed class LoadoutState
    {
        public ulong CorrelationId;
        public byte Code;
        public ushort PlayerId;
        public LoadoutEntry[] Entries = Array.Empty<LoadoutEntry>();

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U16(PlayerId);
            var count = (byte)Math.Min(Entries.Length, byte.MaxValue);
            w.U8(count);
            for (var i = 0; i < count; i++)
            {
                w.U8(Entries[i].Slot);
                w.U32(Entries[i].ItemId);
            }
        }

        public static LoadoutState Read(Reader r)
        {
            var state = new LoadoutState
            {
                CorrelationId = r.U64(),
                Code = r.U8(),
                PlayerId = r.U16(),
            };
            var count = r.U8();
            var entries = new List<LoadoutEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(new LoadoutEntry { Slot = r.U8(), ItemId = r.U32() });
            state.Entries = entries.ToArray();
            return state;
        }
    }

    public sealed class MirrorEntry
    {
        public uint ItemId;
        public uint Count;
    }

    public sealed class MirrorState
    {
        public ulong Version;
        public ushort PlayerId;
        public MirrorEntry[] Entries = Array.Empty<MirrorEntry>();

        public void Write(Writer w)
        {
            w.U64(Version);
            w.U16(PlayerId);
            var count = (byte)Math.Min(Entries.Length, byte.MaxValue);
            w.U8(count);
            for (var i = 0; i < count; i++)
            {
                w.U32(Entries[i].ItemId);
                w.U32(Entries[i].Count);
            }
        }

        public static MirrorState Read(Reader r)
        {
            var mirror = new MirrorState { Version = r.U64(), PlayerId = r.U16() };
            var count = r.U8();
            var entries = new List<MirrorEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(new MirrorEntry { ItemId = r.U32(), Count = r.U32() });
            mirror.Entries = entries.ToArray();
            return mirror;
        }
    }

    public sealed class MirrorAck
    {
        public ulong Version;

        public void Write(Writer w) => w.U64(Version);

        public static MirrorAck Read(Reader r) => new MirrorAck { Version = r.U64() };
    }

    public sealed class AiTarget
    {
        public uint AiId;
        public ushort PlayerId;
        public int Tick;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U16(PlayerId);
            w.U16(0);
            w.I32(Tick);
        }

        public static AiTarget Read(Reader r) => new AiTarget
        {
            AiId = r.U32(),
            PlayerId = r.U16(),
            Tick = r.I32(),
        };
    }

    public sealed class SpawnAssign
    {
        public int X;
        public int Y;
        public int Z;
        public ushort Seat;

        public void Write(Writer w)
        {
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Seat);
        }

        public static SpawnAssign Read(Reader r) => new SpawnAssign
        {
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Seat = r.U16(),
        };
    }

    public sealed class HitReport
    {
        public ulong CorrelationId;
        public byte TargetKind; // 0 player, 1 ai
        public uint TargetId;
        public int Damage;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(TargetKind);
            w.U32(TargetId);
            w.I32(Damage);
        }

        public static HitReport Read(Reader r) => new HitReport
        {
            CorrelationId = r.U64(),
            TargetKind = r.U8(),
            TargetId = r.U32(),
            Damage = r.I32(),
        };
    }

    public sealed class CombatEvent
    {
        public ulong EventId;
        public byte Type; // 0 damaged, 1 died
        public byte TargetKind;
        public uint TargetId;
        public ushort PlayerId;
        public int Value;

        public void Write(Writer w)
        {
            w.U64(EventId);
            w.U8(Type);
            w.U8(TargetKind);
            w.U32(TargetId);
            w.U16(PlayerId);
            w.I32(Value);
        }

        public static CombatEvent Read(Reader r) => new CombatEvent
        {
            EventId = r.U64(),
            Type = r.U8(),
            TargetKind = r.U8(),
            TargetId = r.U32(),
            PlayerId = r.U16(),
            Value = r.I32(),
        };
    }

    public sealed class AiSpawn
    {
        public uint AiId;
        public ushort OwnerPlayerId;
        public uint MonsterId;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;
        public ushort Hp;
        public uint RaidEpoch;
        /// <summary>M2-v11 spawn AOI: streaming radii in cm (0 = client default).</summary>
        public ushort ActivationRadius;
        public ushort DeactivationRadius;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U16(OwnerPlayerId);
            w.U32(MonsterId);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Yaw);
            w.U16(Hp);
            w.U32(RaidEpoch);
            w.U16(ActivationRadius);
            w.U16(DeactivationRadius);
        }

        public static AiSpawn Read(Reader r) => new AiSpawn
        {
            AiId = r.U32(),
            OwnerPlayerId = r.U16(),
            MonsterId = r.U32(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Yaw = r.U16(),
            Hp = r.U16(),
            RaidEpoch = r.U32(),
            ActivationRadius = r.U16(),
            DeactivationRadius = r.U16(),
        };
    }

    public sealed class AiDespawn
    {
        public uint AiId;
        public uint RaidEpoch;
        public byte Reason;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.U8(Reason);
        }

        public static AiDespawn Read(Reader r) => new AiDespawn
        {
            AiId = r.U32(),
            RaidEpoch = r.U32(),
            Reason = r.U8(),
        };
    }

    public sealed class ResumeRequest
    {
        public string Token = string.Empty;

        public void Write(Writer w) => w.Str(Token);

        public static ResumeRequest Read(Reader r) => new ResumeRequest { Token = r.Str() };
    }

    public sealed class ResumeResult
    {
        public byte Code;
        public ushort PlayerId;
        public string SceneName = string.Empty;
        public int X;
        public int Y;
        public int Z;
        public uint RaidEpoch;

        public void Write(Writer w)
        {
            w.U8(Code);
            w.U16(PlayerId);
            w.Str(SceneName);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U32(RaidEpoch);
        }

        public static ResumeResult Read(Reader r) => new ResumeResult
        {
            Code = r.U8(),
            PlayerId = r.U16(),
            SceneName = r.Str(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            RaidEpoch = r.U32(),
        };
    }

    public sealed class Snapshot
    {
        public int Tick;
        public PlayerSnapshot[] Players = Array.Empty<PlayerSnapshot>();

        public void Write(Writer w)
        {
            w.I32(Tick);
            var count = (byte)Math.Min(Players.Length, byte.MaxValue);
            w.U8(count);
            for (var i = 0; i < count; i++)
            {
                var p = Players[i];
                w.U16(p.PlayerId);
                w.I32(p.X);
                w.I32(p.Y);
                w.I32(p.Z);
                w.U16(p.Yaw);
                w.U8(p.Flags);
                w.U16(p.Hp);
                w.U16(p.MaxHp);
                w.Str(p.Scene ?? string.Empty);
            }
        }

        public static Snapshot Read(Reader r)
        {
            var snapshot = new Snapshot { Tick = r.I32() };
            var count = r.U8();
            var players = new List<PlayerSnapshot>(count);
            for (var i = 0; i < count; i++)
            {
                players.Add(new PlayerSnapshot
                {
                    PlayerId = r.U16(),
                    X = r.I32(),
                    Y = r.I32(),
                    Z = r.I32(),
                    Yaw = r.U16(),
                    Flags = r.U8(),
                    Hp = r.U16(),
                    MaxHp = r.U16(),
                    Scene = r.Str(),
                });
            }

            snapshot.Players = players.ToArray();
            return snapshot;
        }
    }

    // ---------------------------------------------------------------------
    // wire v4 / D5 (ADR-021). Field order is frozen; keep in sync with
    // server/src/protocol/Messages.cpp and the golden tests.
    // ---------------------------------------------------------------------

    /// <summary><c>CHitEvent</c> (C→S, opcode 20): hit facts only, no damage.</summary>
    public sealed class HitEvent
    {
        public const byte FlagExplosion = 1 << 0;
        public const byte FlagMelee = 1 << 1;
        public const byte FlagHasImpactCenter = 1 << 2;
        public const byte FlagHasRadius = 1 << 3;
        public const byte FlagHeadshot = 1 << 4;
        public const byte FlagRealDamage = 1 << 5;

        public uint HitId;
        public byte SourceKind; // 0 player, 1 ai, 2 environment
        public uint SourceId;
        public ushort ReporterPlayerId;
        public byte TargetKind; // 0 player, 1 ai
        public uint TargetId;
        public uint WeaponId;
        public uint AmmoId;
        public uint AttackId;
        public int HitX;
        public int HitY;
        public int HitZ;
        public byte Flags;
        public int ImpactX;
        public int ImpactY;
        public int ImpactZ;
        public ushort Radius;

        public void Write(Writer w)
        {
            w.U32(HitId);
            w.U8(SourceKind);
            w.U32(SourceId);
            w.U16(ReporterPlayerId);
            w.U8(TargetKind);
            w.U32(TargetId);
            w.U32(WeaponId);
            w.U32(AmmoId);
            w.U32(AttackId);
            w.I32(HitX);
            w.I32(HitY);
            w.I32(HitZ);
            w.U8(Flags);
            if ((Flags & FlagHasImpactCenter) != 0)
            {
                w.I32(ImpactX);
                w.I32(ImpactY);
                w.I32(ImpactZ);
            }
            if ((Flags & FlagHasRadius) != 0)
                w.U16(Radius);
        }

        public static HitEvent Read(Reader r)
        {
            var hit = new HitEvent
            {
                HitId = r.U32(),
                SourceKind = r.U8(),
                SourceId = r.U32(),
                ReporterPlayerId = r.U16(),
                TargetKind = r.U8(),
                TargetId = r.U32(),
                WeaponId = r.U32(),
                AmmoId = r.U32(),
                AttackId = r.U32(),
                HitX = r.I32(),
                HitY = r.I32(),
                HitZ = r.I32(),
                Flags = r.U8(),
            };
            if ((hit.Flags & FlagHasImpactCenter) != 0)
            {
                hit.ImpactX = r.I32();
                hit.ImpactY = r.I32();
                hit.ImpactZ = r.I32();
            }
            if ((hit.Flags & FlagHasRadius) != 0)
                hit.Radius = r.U16();
            return hit;
        }
    }

    /// <summary><c>SCombatEvent</c> v4 (S→C, opcode 161): server-authoritative result.</summary>
    public sealed class CombatEventV4
    {
        public const byte FlagExplosion = 1 << 0;
        public const byte FlagHpClamped = 1 << 1;
        public const byte FlagDied = 1 << 2;
        public const byte FlagAoe = 1 << 3;
        public const byte FlagHeadshot = 1 << 4;
        public const byte FlagRealDamage = 1 << 5;

        public ulong EventId;
        public uint CauseId;
        public byte Type; // 0 damaged, 1 died, 2 healed
        public byte SourceKind;
        public uint SourceId;
        public ushort ReporterPlayerId;
        public byte TargetKind;
        public uint TargetId;
        public uint Damage;
        public int HpAfter;
        public int MaxHp;
        public byte Element;
        public byte Crit;
        public int HitX;
        public int HitY;
        public int HitZ;
        public uint WeaponId;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U64(EventId);
            w.U32(CauseId);
            w.U8(Type);
            w.U8(SourceKind);
            w.U32(SourceId);
            w.U16(ReporterPlayerId);
            w.U8(TargetKind);
            w.U32(TargetId);
            w.U32(Damage);
            w.I32(HpAfter);
            w.I32(MaxHp);
            w.U8(Element);
            w.U8(Crit);
            w.I32(HitX);
            w.I32(HitY);
            w.I32(HitZ);
            w.U32(WeaponId);
            w.U8(Flags);
        }

        public static CombatEventV4 Read(Reader r) => new CombatEventV4
        {
            EventId = r.U64(),
            CauseId = r.U32(),
            Type = r.U8(),
            SourceKind = r.U8(),
            SourceId = r.U32(),
            ReporterPlayerId = r.U16(),
            TargetKind = r.U8(),
            TargetId = r.U32(),
            Damage = r.U32(),
            HpAfter = r.I32(),
            MaxHp = r.I32(),
            Element = r.U8(),
            Crit = r.U8(),
            HitX = r.I32(),
            HitY = r.I32(),
            HitZ = r.I32(),
            WeaponId = r.U32(),
            Flags = r.U8(),
        };
    }

    /// <summary><c>CAiAnim</c>/<c>SAiAnim</c> (opcodes 21/165): mirrored animator sample.</summary>
    public sealed class AiAnim
    {
        public const byte FlagDie = 1 << 0;
        public const byte FlagReloading = 1 << 1;
        public const byte FlagDashing = 1 << 2;
        public const byte FlagGunReady = 1 << 3;
        public const byte FlagAttackTrigger = 1 << 4;
        public const byte FlagAiming = 1 << 5;

        public uint AiId;
        public uint RaidEpoch;
        public ushort Seq;
        public ushort MoveSpeed;
        public sbyte MoveDirX;
        public sbyte MoveDirY;
        public byte HandState;
        public byte AttackIndex;
        public byte Flags;
        public byte AttackSeq;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.U16(Seq);
            w.U16(MoveSpeed);
            w.U8((byte)MoveDirX);
            w.U8((byte)MoveDirY);
            w.U8(HandState);
            w.U8(AttackIndex);
            w.U8(Flags);
            w.U8(AttackSeq);
        }

        public static AiAnim Read(Reader r) => new AiAnim
        {
            AiId = r.U32(),
            RaidEpoch = r.U32(),
            Seq = r.U16(),
            MoveSpeed = r.U16(),
            MoveDirX = (sbyte)r.U8(),
            MoveDirY = (sbyte)r.U8(),
            HandState = r.U8(),
            AttackIndex = r.U8(),
            Flags = r.U8(),
            AttackSeq = r.U8(),
        };
    }

    /// <summary><c>SAiHealth</c> (S→C, opcode 166): non-damage HP updates only.</summary>
    public sealed class AiHealth
    {
        public const byte FlagDead = 1 << 0;
        public const byte FlagHpClamped = 1 << 1;
        public const byte FlagBuffChanged = 1 << 2;

        public uint AiId;
        public uint RaidEpoch;
        public int Hp;
        public int MaxHp;
        public byte Flags;
        public ushort Seq;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.I32(Hp);
            w.I32(MaxHp);
            w.U8(Flags);
            w.U16(Seq);
        }

        public static AiHealth Read(Reader r) => new AiHealth
        {
            AiId = r.U32(),
            RaidEpoch = r.U32(),
            Hp = r.I32(),
            MaxHp = r.I32(),
            Flags = r.U8(),
            Seq = r.U16(),
        };
    }

    /// <summary><c>CAiAim</c>/<c>SAiAim</c> (opcodes 26/170): host-authored AI aim.</summary>
    public sealed class AiAim
    {
        public uint AiId;
        public uint RaidEpoch;
        public ushort Seq;
        public ushort AimYaw;   // raw 0..65535
        public short AimPitch;  // 0.01 degrees, -9000..9000
        public byte Flags;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.U16(Seq);
            w.U16(AimYaw);
            w.I16(AimPitch);
            w.U8(Flags);
        }

        public static AiAim Read(Reader r) => new AiAim
        {
            AiId = r.U32(),
            RaidEpoch = r.U32(),
            Seq = r.U16(),
            AimYaw = r.U16(),
            AimPitch = r.I16(),
            Flags = r.U8(),
        };
    }

    /// <summary><c>CAiStateSample</c>/<c>SAiStateSample</c> (opcodes 27/171).</summary>
    public sealed class AiStateSample
    {
        public const byte FlagValid = 1 << 0;
        public const byte FlagInTransition = 1 << 1;

        public uint AiId;
        public uint RaidEpoch;
        public ushort Seq;
        public uint StateHash;
        public ushort NormTime;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.U16(Seq);
            w.U32(StateHash);
            w.U16(NormTime);
            w.U8(Flags);
        }

        public static AiStateSample Read(Reader r) => new AiStateSample
        {
            AiId = r.U32(),
            RaidEpoch = r.U32(),
            Seq = r.U16(),
            StateHash = r.U32(),
            NormTime = r.U16(),
            Flags = r.U8(),
        };
    }

    public sealed class AiLoadoutSlot
    {
        public byte Slot;
        public byte[] Blob = Array.Empty<byte>();
    }

    /// <summary><c>CAiLoadoutV7</c>/<c>SAiLoadoutV7</c> (opcodes 28/172): opaque item snapshots.</summary>
    public sealed class AiLoadoutV7
    {
        public uint AiId;
        public uint RaidEpoch;
        public byte ActiveSlot;
        public List<AiLoadoutSlot> Slots = new List<AiLoadoutSlot>();

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.U8(ActiveSlot);
            w.U8((byte)Slots.Count);
            foreach (var slot in Slots)
            {
                w.U8(slot.Slot);
                w.U16((ushort)slot.Blob.Length);
                w.Raw(slot.Blob);
            }
        }

        public static AiLoadoutV7 Read(Reader r)
        {
            var message = new AiLoadoutV7
            {
                AiId = r.U32(),
                RaidEpoch = r.U32(),
                ActiveSlot = r.U8(),
            };
            var count = r.U8();
            for (var i = 0; i < count; i++)
            {
                var slot = new AiLoadoutSlot
                {
                    Slot = r.U8(),
                };
                var length = r.U16();
                slot.Blob = length == 0 ? Array.Empty<byte>() : r.Bytes(length);
                message.Slots.Add(slot);
            }
            return message;
        }
    }

    /// <summary><c>CAiShot</c>/<c>SAiShot</c> (opcodes 29/173).</summary>
    public sealed class AiShot
    {
        public uint AiId;
        public uint RaidEpoch;
        public uint ShotId;
        public uint WeaponId;
        public uint BulletId;
        public int OriginX;
        public int OriginY;
        public int OriginZ;
        public short DirX;
        public short DirY;
        public short DirZ;
        public uint Seed;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.U32(ShotId);
            w.U32(WeaponId);
            w.U32(BulletId);
            w.I32(OriginX);
            w.I32(OriginY);
            w.I32(OriginZ);
            w.I16(DirX);
            w.I16(DirY);
            w.I16(DirZ);
            w.U32(Seed);
            w.U8(Flags);
        }

        public static AiShot Read(Reader r) => new AiShot
        {
            AiId = r.U32(),
            RaidEpoch = r.U32(),
            ShotId = r.U32(),
            WeaponId = r.U32(),
            BulletId = r.U32(),
            OriginX = r.I32(),
            OriginY = r.I32(),
            OriginZ = r.I32(),
            DirX = r.I16(),
            DirY = r.I16(),
            DirZ = r.I16(),
            Seed = r.U32(),
            Flags = r.U8(),
        };
    }

    /// <summary><c>CPlayerStateSample</c> (opcode 30); S adds playerId.</summary>
    public sealed class PlayerStateSample
    {
        public uint RaidEpoch;
        public ushort Seq;
        public uint StateHash;
        public ushort NormTime;
        public byte Flags;
        public byte AttackSeq;
        public byte HandState;

        public void Write(Writer w)
        {
            w.U32(RaidEpoch);
            w.U16(Seq);
            w.U32(StateHash);
            w.U16(NormTime);
            w.U8(Flags);
            w.U8(AttackSeq);
            w.U8(HandState);
        }

        public static PlayerStateSample Read(Reader r) => new PlayerStateSample
        {
            RaidEpoch = r.U32(),
            Seq = r.U16(),
            StateHash = r.U32(),
            NormTime = r.U16(),
            Flags = r.U8(),
            AttackSeq = r.U8(),
            HandState = r.U8(),
        };
    }

    /// <summary><c>CPlayerLoadoutV8</c> (opcode 31); S adds playerId.</summary>
    public sealed class PlayerLoadoutV8
    {
        public uint RaidEpoch;
        public byte ActiveSlot;
        public byte Slot;
        public byte[] Blob = Array.Empty<byte>();

        public void Write(Writer w)
        {
            w.U32(RaidEpoch);
            w.U8(ActiveSlot);
            w.U8(Slot);
            w.U16((ushort)Blob.Length);
            w.Raw(Blob);
        }

        public static PlayerLoadoutV8 Read(Reader r)
        {
            var message = new PlayerLoadoutV8
            {
                RaidEpoch = r.U32(),
                ActiveSlot = r.U8(),
                Slot = r.U8(),
            };
            var length = r.U16();
            message.Blob = length == 0 ? Array.Empty<byte>() : r.Bytes(length);
            return message;
        }
    }

    /// <summary><c>CPlayerShot</c> (opcode 32); S adds playerId.</summary>
    public sealed class PlayerShot
    {
        public uint RaidEpoch;
        public uint ShotId;
        public uint WeaponId;
        public uint BulletId;
        public int OriginX;
        public int OriginY;
        public int OriginZ;
        public short DirX;
        public short DirY;
        public short DirZ;
        public uint Seed;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U32(RaidEpoch);
            w.U32(ShotId);
            w.U32(WeaponId);
            w.U32(BulletId);
            w.I32(OriginX);
            w.I32(OriginY);
            w.I32(OriginZ);
            w.I16(DirX);
            w.I16(DirY);
            w.I16(DirZ);
            w.U32(Seed);
            w.U8(Flags);
        }

        public static PlayerShot Read(Reader r) => new PlayerShot
        {
            RaidEpoch = r.U32(),
            ShotId = r.U32(),
            WeaponId = r.U32(),
            BulletId = r.U32(),
            OriginX = r.I32(),
            OriginY = r.I32(),
            OriginZ = r.I32(),
            DirX = r.I16(),
            DirY = r.I16(),
            DirZ = r.I16(),
            Seed = r.U32(),
            Flags = r.U8(),
        };
    }

    /// <summary><c>CPlayerFaceV8</c> (opcode 33); S adds playerId.</summary>
    public sealed class PlayerFaceV8
    {
        public uint RaidEpoch;
        public byte[] Face = Array.Empty<byte>();

        public void Write(Writer w)
        {
            w.U32(RaidEpoch);
            w.U16((ushort)Face.Length);
            w.Raw(Face);
        }

        public static PlayerFaceV8 Read(Reader r)
        {
            var message = new PlayerFaceV8
            {
                RaidEpoch = r.U32(),
            };
            var length = r.U16();
            message.Face = length == 0 ? Array.Empty<byte>() : r.Bytes(length);
            return message;
        }
    }

    /// <summary><c>CAiLoadout</c> (C→S, opcode 22) and <c>SAiLoadout</c> (S→C, 169).</summary>
    public sealed class AiLoadout
    {
        public uint AiId;
        public uint RaidEpoch;
        public uint WeaponId;
        public uint BulletId;
        public uint ArmorId;
        public uint HelmetId;

        public void Write(Writer w)
        {
            w.U32(AiId);
            w.U32(RaidEpoch);
            w.U32(WeaponId);
            w.U32(BulletId);
            w.U32(ArmorId);
            w.U32(HelmetId);
        }

        public static AiLoadout Read(Reader r) => new AiLoadout
        {
            AiId = r.U32(),
            RaidEpoch = r.U32(),
            WeaponId = r.U32(),
            BulletId = r.U32(),
            ArmorId = r.U32(),
            HelmetId = r.U32(),
        };
    }

    /// <summary><c>CPlayerStats</c> (C→S, opcode 23): player stat snapshot on spawn/equipment change.</summary>
    public sealed class PlayerStats
    {
        public float GunDamageMultiplier;
        public float GunCritRateGain;
        public float GunCritDamageGain;
        public float MeleeDamageMultiplier;
        public float MeleeCritRateGain;
        public float MeleeCritDamageGain;
        public float[] ElementFactor = new float[7];
        public float BodyArmor;
        public float HeadArmor;
        public float MaxHealth = 100f;

        public void Write(Writer w)
        {
            w.F32(GunDamageMultiplier);
            w.F32(GunCritRateGain);
            w.F32(GunCritDamageGain);
            w.F32(MeleeDamageMultiplier);
            w.F32(MeleeCritRateGain);
            w.F32(MeleeCritDamageGain);
            for (var i = 0; i < 7; i++)
                w.F32(ElementFactor[i]);
            w.F32(BodyArmor);
            w.F32(HeadArmor);
            w.F32(MaxHealth);
        }

        public static PlayerStats Read(Reader r)
        {
            var stats = new PlayerStats
            {
                GunDamageMultiplier = r.F32(),
                GunCritRateGain = r.F32(),
                GunCritDamageGain = r.F32(),
                MeleeDamageMultiplier = r.F32(),
                MeleeCritRateGain = r.F32(),
                MeleeCritDamageGain = r.F32(),
            };
            for (var i = 0; i < 7; i++)
                stats.ElementFactor[i] = r.F32();
            stats.BodyArmor = r.F32();
            stats.HeadArmor = r.F32();
            stats.MaxHealth = r.F32();
            return stats;
        }
    }

    /// <summary><c>CShootFact</c> (C→S, opcode 24): one trigger pull, never one per bullet.</summary>
    public sealed class ShootFact
    {
        public byte SourceKind;
        public uint SourceId;
        public uint WeaponId;
        public uint AmmoId;
        public uint AttackId;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U8(SourceKind);
            w.U32(SourceId);
            w.U32(WeaponId);
            w.U32(AmmoId);
            w.U32(AttackId);
            w.U8(Flags);
        }

        public static ShootFact Read(Reader r) => new ShootFact
        {
            SourceKind = r.U8(),
            SourceId = r.U32(),
            WeaponId = r.U32(),
            AmmoId = r.U32(),
            AttackId = r.U32(),
            Flags = r.U8(),
        };
    }

    /// <summary><c>CUseConsumable</c> (C→S, opcode 25): use-completed fact; inventory is not deducted.</summary>
    public sealed class UseConsumable
    {
        /// <summary>
        /// Contract 2.16.0: the same use was also reported as <c>CItemConsume</c>, so its HP is
        /// settled there; the server must not heal twice.
        /// </summary>
        public const byte FlagSettledByItemConsume = 1 << 0;

        public ulong CorrelationId;
        public uint ItemId;
        public byte Slot;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ItemId);
            w.U8(Slot);
            w.U8(Flags);
        }

        public static UseConsumable Read(Reader r) => new UseConsumable
        {
            CorrelationId = r.U64(),
            ItemId = r.U32(),
            Slot = r.U8(),
            Flags = r.U8(),
        };
    }

    /// <summary><c>SPlayerBuff</c> (S→C, opcode 167).</summary>
    public sealed class PlayerBuff
    {
        public const byte OpAdd = 0;
        public const byte OpUpdate = 1;
        public const byte OpRemove = 2;

        public ushort PlayerId;
        public uint BuffId;
        public byte Layers;
        public uint RemainingMs;
        public byte Op;

        public void Write(Writer w)
        {
            w.U16(PlayerId);
            w.U32(BuffId);
            w.U8(Layers);
            w.U32(RemainingMs);
            w.U8(Op);
        }

        public static PlayerBuff Read(Reader r) => new PlayerBuff
        {
            PlayerId = r.U16(),
            BuffId = r.U32(),
            Layers = r.U8(),
            RemainingMs = r.U32(),
            Op = r.U8(),
        };
    }

    /// <summary><c>SRaidRules</c> (S→C, opcode 168), 66 bytes.</summary>
    public sealed class RaidRules
    {
        public byte RulesetId;
        public float DamageFactorToPlayer;
        public float EnemyHealthFactor;
        public float EnemyAttackTimeFactor;
        public float EnemyAttackTimeSpaceFactor;
        public float EnemyReactionTimeFactor;
        public byte Flags;
        public float RecoilMultiplier;
        public byte Weather;
        public ushort TimeOfDayMinutes;
        public byte SaveDeadbodyCount;
        public uint WeatherSeed;
        public byte[] BaseStatsHash = new byte[32];

        public void Write(Writer w)
        {
            w.U8(RulesetId);
            w.F32(DamageFactorToPlayer);
            w.F32(EnemyHealthFactor);
            w.F32(EnemyAttackTimeFactor);
            w.F32(EnemyAttackTimeSpaceFactor);
            w.F32(EnemyReactionTimeFactor);
            w.U8(Flags);
            w.F32(RecoilMultiplier);
            w.U8(Weather);
            w.U16(TimeOfDayMinutes);
            w.U8(SaveDeadbodyCount);
            w.U32(WeatherSeed);
            w.Raw(BaseStatsHash);
        }

        public static RaidRules Read(Reader r) => new RaidRules
        {
            RulesetId = r.U8(),
            DamageFactorToPlayer = r.F32(),
            EnemyHealthFactor = r.F32(),
            EnemyAttackTimeFactor = r.F32(),
            EnemyAttackTimeSpaceFactor = r.F32(),
            EnemyReactionTimeFactor = r.F32(),
            Flags = r.U8(),
            RecoilMultiplier = r.F32(),
            Weather = r.U8(),
            TimeOfDayMinutes = r.U16(),
            SaveDeadbodyCount = r.U8(),
            WeatherSeed = r.U32(),
            BaseStatsHash = r.Bytes(32),
        };
    }

    // -----------------------------------------------------------------------
    // v9 item contract (SPEC-2026-11-13-item-protocol-v9).
    //
    // Bounded fields are clamped to the SPEC maxima on write and on read, so a decoded
    // message never exceeds the documented limits and frames stay under
    // FrameCodec.MaxPayload without relying on automatic fragmentation.
    // -----------------------------------------------------------------------

    /// <summary>SPEC §5 limits shared by the v9 item messages.</summary>
    public static class ItemProtocolLimits
    {
        public const int MaxTicketIdBytes = 32;
        public const int MaxSceneBytes = 64;
        public const int MaxInitialInventoryEntries = 64;
        public const int MaxInventorySnapshotEntries = 64;
        public const int MaxContainerEntries = 32;
        public const int MaxWorldItemChunkEntries = 8;
    }

    internal static class ItemWire
    {
        /// <summary>UTF-8-aware clamp: never cut a multi-byte sequence in half.</summary>
        public static string ClampUtf8(string value, int maxBytes)
        {
            value ??= string.Empty;
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length <= maxBytes)
                return value;

            var size = maxBytes;
            while (size > 0 && (bytes[size] & 0xC0) == 0x80)
                size--;
            return Encoding.UTF8.GetString(bytes, 0, size);
        }

        public static ushort ClampCount(int size, int maxEntries)
            => (ushort)Math.Min(size, maxEntries);

        public static int ReadClampedCount(Reader r, int maxEntries)
            => Math.Min((int)r.U16(), maxEntries);
    }

    /// <summary>Inventory kinds on the wire (SPEC §3).</summary>
    public enum InventoryKind : byte
    {
        PlayerEquipment = 0,
        PlayerBackpack = 1,
        PlayerPetBag = 2,
        MapContainer = 3,
        WorldItem = 4,
        CorpseContainer = 5,
    }

    /// <summary><c>WorldItemInfo.source</c> (SPEC §3).</summary>
    public enum WorldItemSource : byte
    {
        Spawned = 0,
        Dropped = 1,
        Corpse = 2,
    }

    /// <summary>14B wire entry (SPEC §3).</summary>
    public struct InventoryEntry
    {
        public ushort Slot;
        public uint ItemUid;
        public uint ItemId;
        public uint Count;

        public void Write(Writer w)
        {
            w.U16(Slot);
            w.U32(ItemUid);
            w.U32(ItemId);
            w.U32(Count);
        }

        public static InventoryEntry Read(Reader r) => new InventoryEntry
        {
            Slot = r.U16(),
            ItemUid = r.U32(),
            ItemId = r.U32(),
            Count = r.U32(),
        };
    }

    /// <summary>Inventory snapshot (SPEC §3): <c>count</c> is derived from <see cref="Entries"/>.</summary>
    public sealed class InventorySnapshot
    {
        public byte Kind;
        public uint OwnerId;
        public uint Revision;
        public ushort Capacity;
        public InventoryEntry[] Entries = Array.Empty<InventoryEntry>();

        public void Write(Writer w)
        {
            w.U8(Kind);
            w.U32(OwnerId);
            w.U32(Revision);
            w.U16(Capacity);
            var count = ItemWire.ClampCount(Entries.Length, ItemProtocolLimits.MaxInventorySnapshotEntries);
            w.U16(count);
            for (var i = 0; i < count; i++)
                Entries[i].Write(w);
        }

        public static InventorySnapshot Read(Reader r)
        {
            var snapshot = new InventorySnapshot
            {
                Kind = r.U8(),
                OwnerId = r.U32(),
                Revision = r.U32(),
                Capacity = r.U16(),
            };
            var count = ItemWire.ReadClampedCount(r, ItemProtocolLimits.MaxInventorySnapshotEntries);
            snapshot.Entries = new InventoryEntry[count];
            for (var i = 0; i < count; i++)
                snapshot.Entries[i] = InventoryEntry.Read(r);
            return snapshot;
        }
    }

    /// <summary>One flat row of <c>SInitialInventory</c>.</summary>
    public struct InitialInventoryEntry
    {
        public byte Kind;
        public InventoryEntry Entry;

        public void Write(Writer w)
        {
            w.U8(Kind);
            Entry.Write(w);
        }

        public static InitialInventoryEntry Read(Reader r) => new InitialInventoryEntry
        {
            Kind = r.U8(),
            Entry = InventoryEntry.Read(r),
        };
    }

    /// <summary><c>SInitialInventory</c> (opcode 178): one flat message, explicit capacities.</summary>
    public sealed class InitialInventory
    {
        public string TicketId = string.Empty;
        public uint Revision;
        public ushort EquipmentCapacity;
        public ushort BackpackCapacity;
        public ushort PetBagCapacity;
        public InitialInventoryEntry[] Entries = Array.Empty<InitialInventoryEntry>();

        public void Write(Writer w)
        {
            w.Str(ItemWire.ClampUtf8(TicketId, ItemProtocolLimits.MaxTicketIdBytes));
            w.U32(Revision);
            w.U16(EquipmentCapacity);
            w.U16(BackpackCapacity);
            w.U16(PetBagCapacity);
            var count = ItemWire.ClampCount(Entries.Length, ItemProtocolLimits.MaxInitialInventoryEntries);
            w.U16(count);
            for (var i = 0; i < count; i++)
                Entries[i].Write(w);
        }

        public static InitialInventory Read(Reader r)
        {
            var message = new InitialInventory
            {
                TicketId = ItemWire.ClampUtf8(r.Str(), ItemProtocolLimits.MaxTicketIdBytes),
                Revision = r.U32(),
                EquipmentCapacity = r.U16(),
                BackpackCapacity = r.U16(),
                PetBagCapacity = r.U16(),
            };
            var count = ItemWire.ReadClampedCount(r, ItemProtocolLimits.MaxInitialInventoryEntries);
            message.Entries = new InitialInventoryEntry[count];
            for (var i = 0; i < count; i++)
                message.Entries[i] = InitialInventoryEntry.Read(r);
            return message;
        }
    }

    /// <summary><c>CItemTransfer</c> (opcode 34).</summary>
    public sealed class ItemTransfer
    {
        public ulong CorrelationId;
        public byte SourceKind;
        public uint SourceId;
        public ushort SourceSlot;
        public byte TargetKind;
        public uint TargetId;
        public ushort TargetSlot;
        public uint ItemUid;
        public uint Count;
        public uint ExpectedItemId;
        public uint ExpectedRevision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(SourceKind);
            w.U32(SourceId);
            w.U16(SourceSlot);
            w.U8(TargetKind);
            w.U32(TargetId);
            w.U16(TargetSlot);
            w.U32(ItemUid);
            w.U32(Count);
            w.U32(ExpectedItemId);
            w.U32(ExpectedRevision);
        }

        public static ItemTransfer Read(Reader r) => new ItemTransfer
        {
            CorrelationId = r.U64(),
            SourceKind = r.U8(),
            SourceId = r.U32(),
            SourceSlot = r.U16(),
            TargetKind = r.U8(),
            TargetId = r.U32(),
            TargetSlot = r.U16(),
            ItemUid = r.U32(),
            Count = r.U32(),
            ExpectedItemId = r.U32(),
            ExpectedRevision = r.U32(),
        };
    }

    /// <summary><c>SItemTransfer</c> (opcode 179).</summary>
    public sealed class ItemTransferResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint ItemUid;
        public uint SourceRevision;
        public uint TargetRevision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(ItemUid);
            w.U32(SourceRevision);
            w.U32(TargetRevision);
        }

        public static ItemTransferResult Read(Reader r) => new ItemTransferResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
            ItemUid = r.U32(),
            SourceRevision = r.U32(),
            TargetRevision = r.U32(),
        };
    }

    /// <summary><c>CItemArrange</c> (opcode 39): final layout for a container sort/merge/reorder.</summary>
    public sealed class ItemArrange
    {
        public ulong CorrelationId;
        public byte Kind;
        public uint OwnerId;
        public uint ExpectedRevision;
        public InventoryEntry[] Entries = Array.Empty<InventoryEntry>();

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Kind);
            w.U32(OwnerId);
            w.U32(ExpectedRevision);
            var count = ItemWire.ClampCount(Entries.Length, ItemProtocolLimits.MaxInventorySnapshotEntries);
            w.U16(count);
            for (var i = 0; i < count; i++)
                Entries[i].Write(w);
        }

        public static ItemArrange Read(Reader r)
        {
            var message = new ItemArrange
            {
                CorrelationId = r.U64(),
                Kind = r.U8(),
                OwnerId = r.U32(),
                ExpectedRevision = r.U32(),
            };
            var count = ItemWire.ReadClampedCount(r, ItemProtocolLimits.MaxInventorySnapshotEntries);
            message.Entries = new InventoryEntry[count];
            for (var i = 0; i < count; i++)
                message.Entries[i] = InventoryEntry.Read(r);
            return message;
        }
    }

    /// <summary><c>SItemArrange</c> (opcode 187).</summary>
    public sealed class ItemArrangeResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint Revision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(Revision);
        }

        public static ItemArrangeResult Read(Reader r) => new ItemArrangeResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
            Revision = r.U32(),
        };
    }

    /// <summary><c>CItemConsume</c> (opcode 40): consume/use a stack in place.</summary>
    public sealed class ItemConsume
    {
        public ulong CorrelationId;
        public byte Kind;
        public uint OwnerId;
        public ushort Slot;
        public uint ItemUid;
        public uint ItemId;
        public uint Count;
        public uint ExpectedRevision;
        public byte Reason;
        /// <summary>Contract 2.16.0 / wire 14: HP restored by this use (0 = not healing).</summary>
        public uint HealAmount;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Kind);
            w.U32(OwnerId);
            w.U16(Slot);
            w.U32(ItemUid);
            w.U32(ItemId);
            w.U32(Count);
            w.U32(ExpectedRevision);
            w.U8(Reason);
            w.U32(HealAmount);
        }

        public static ItemConsume Read(Reader r) => new ItemConsume
        {
            CorrelationId = r.U64(),
            Kind = r.U8(),
            OwnerId = r.U32(),
            Slot = r.U16(),
            ItemUid = r.U32(),
            ItemId = r.U32(),
            Count = r.U32(),
            ExpectedRevision = r.U32(),
            Reason = r.U8(),
            HealAmount = r.U32(),
        };
    }

    /// <summary><c>SItemConsume</c> (opcode 188).</summary>
    public sealed class ItemConsumeResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint ItemUid;
        public uint Remaining;
        public uint Revision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(ItemUid);
            w.U32(Remaining);
            w.U32(Revision);
        }

        public static ItemConsumeResult Read(Reader r) => new ItemConsumeResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
            ItemUid = r.U32(),
            Remaining = r.U32(),
            Revision = r.U32(),
        };
    }

    /// <summary><c>SCorpseSpawn</c> (opcode 189): one player corpse container.</summary>
    public sealed class CorpseSpawn
    {
        public uint ContainerId;
        public ushort PlayerId;
        public string Scene = string.Empty;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;

        public void Write(Writer w)
        {
            w.U32(ContainerId);
            w.U16(PlayerId);
            w.Str(ItemWire.ClampUtf8(Scene, ItemProtocolLimits.MaxSceneBytes));
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Yaw);
        }

        public static CorpseSpawn Read(Reader r) => new CorpseSpawn
        {
            ContainerId = r.U32(),
            PlayerId = r.U16(),
            Scene = ItemWire.ClampUtf8(r.Str(), ItemProtocolLimits.MaxSceneBytes),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Yaw = r.U16(),
        };
    }

    /// <summary><c>SCorpseDespawn</c> (opcode 190).</summary>
    public sealed class CorpseDespawn
    {
        public uint ContainerId;

        public void Write(Writer w) => w.U32(ContainerId);

        public static CorpseDespawn Read(Reader r) => new CorpseDespawn
        {
            ContainerId = r.U32(),
        };
    }

    /// <summary><c>SItemDetail</c> (opcode 191): durability / loaded ammo side channel.</summary>
    public sealed class ItemDetail
    {
        public uint ItemUid;
        public uint Durability;
        public uint MaxDurability;
        public uint LoadedAmmoItemId;
        public uint LoadedAmmoCount;

        public void Write(Writer w)
        {
            w.U32(ItemUid);
            w.U32(Durability);
            w.U32(MaxDurability);
            w.U32(LoadedAmmoItemId);
            w.U32(LoadedAmmoCount);
        }

        public static ItemDetail Read(Reader r) => new ItemDetail
        {
            ItemUid = r.U32(),
            Durability = r.U32(),
            MaxDurability = r.U32(),
            LoadedAmmoItemId = r.U32(),
            LoadedAmmoCount = r.U32(),
        };
    }

    /// <summary>One attachment: native <c>Item.Slots</c> key plus the attachment item id.</summary>
    public struct ItemMod
    {
        public string SlotKey;
        public uint ItemId;
    }

    /// <summary><c>SItemMods</c> (opcode 198): slot-keyed attachments owned by the server.</summary>
    public sealed class ItemMods
    {
        public const int MaxMods = 8;
        public const int MaxSlotKeyBytes = 64;

        public uint ItemUid;
        public ItemMod[] Mods = Array.Empty<ItemMod>();

        public void Write(Writer w)
        {
            w.U32(ItemUid);
            var count = Math.Min(Mods.Length, MaxMods);
            w.U16((ushort)count);
            for (var i = 0; i < count; i++)
            {
                var slotKey = Mods[i].SlotKey ?? string.Empty;
                if (Encoding.UTF8.GetByteCount(slotKey) > MaxSlotKeyBytes)
                    slotKey = ItemWire.ClampUtf8(slotKey, MaxSlotKeyBytes);
                w.Str(slotKey);
                w.U32(Mods[i].ItemId);
            }
        }

        public static ItemMods Read(Reader r)
        {
            var message = new ItemMods { ItemUid = r.U32() };
            var count = r.U16();
            var stored = Math.Min((int)count, MaxMods);
            message.Mods = new ItemMod[stored];
            for (var i = 0; i < count; i++)
            {
                var slotKey = r.Str();
                var itemId = r.U32();
                if (i < stored)
                {
                    message.Mods[i] = new ItemMod
                    {
                        SlotKey = slotKey,
                        ItemId = itemId,
                    };
                }
            }
            return message;
        }
    }

    /// <summary><c>CItemModDetach</c> (opcode 49): pull one attachment off a server-owned item.</summary>
    public sealed class ItemModDetach
    {
        public const int MaxSlotKeyBytes = 64;

        public ulong CorrelationId;
        public uint ParentUid;
        public string SlotKey = string.Empty;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ParentUid);
            var slotKey = SlotKey ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(slotKey) > MaxSlotKeyBytes)
                slotKey = ItemWire.ClampUtf8(slotKey, MaxSlotKeyBytes);
            w.Str(slotKey);
        }

        public static ItemModDetach Read(Reader r)
        {
            return new ItemModDetach
            {
                CorrelationId = r.U64(),
                ParentUid = r.U32(),
                SlotKey = r.Str() ?? string.Empty,
            };
        }
    }

    /// <summary><c>SItemModDetachResult</c> (opcode 199): ItemUid is the new backpack item.</summary>
    public sealed class ItemModDetachResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint ItemUid;
        public uint ParentUid;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(ItemUid);
            w.U32(ParentUid);
        }

        public static ItemModDetachResult Read(Reader r)
        {
            return new ItemModDetachResult
            {
                CorrelationId = r.U64(),
                Code = r.U8(),
                ItemUid = r.U32(),
                ParentUid = r.U32(),
            };
        }
    }

    /// <summary><c>CItemModAttach</c> (opcode 50): plug one backpack item into an attachment slot.</summary>
    public sealed class ItemModAttach
    {
        public const int MaxSlotKeyBytes = ItemModDetach.MaxSlotKeyBytes;

        public ulong CorrelationId;
        public uint ParentUid;
        public uint ItemUid;
        public string SlotKey = string.Empty;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ParentUid);
            w.U32(ItemUid);
            var slotKey = SlotKey ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(slotKey) > MaxSlotKeyBytes)
                slotKey = ItemWire.ClampUtf8(slotKey, MaxSlotKeyBytes);
            w.Str(slotKey);
        }

        public static ItemModAttach Read(Reader r)
        {
            return new ItemModAttach
            {
                CorrelationId = r.U64(),
                ParentUid = r.U32(),
                ItemUid = r.U32(),
                SlotKey = r.Str() ?? string.Empty,
            };
        }
    }

    /// <summary><c>SItemModAttachResult</c> (opcode 200): ItemId is the attached item (0 on failure).</summary>
    public sealed class ItemModAttachResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint ParentUid;
        public uint ItemUid;
        public uint ItemId;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(ParentUid);
            w.U32(ItemUid);
            w.U32(ItemId);
        }

        public static ItemModAttachResult Read(Reader r)
        {
            return new ItemModAttachResult
            {
                CorrelationId = r.U64(),
                Code = r.U8(),
                ParentUid = r.U32(),
                ItemUid = r.U32(),
                ItemId = r.U32(),
            };
        }
    }

    /// <summary><c>CItemReload</c> (opcode 41).</summary>
    public sealed class ItemReload
    {
        public ulong CorrelationId;
        public uint GunUid;
        public uint AmmoItemId;
        public uint Count;
        public uint LoadedAfter;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(GunUid);
            w.U32(AmmoItemId);
            w.U32(Count);
            w.U32(LoadedAfter);
        }

        public static ItemReload Read(Reader r) => new ItemReload
        {
            CorrelationId = r.U64(),
            GunUid = r.U32(),
            AmmoItemId = r.U32(),
            Count = r.U32(),
            LoadedAfter = r.U32(),
        };
    }

    /// <summary><c>SItemReloadResult</c> (opcode 192).</summary>
    public sealed class ItemReloadResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint GunUid;
        public uint AmmoItemId;
        public uint LoadedCount;
        public uint BackpackRevision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(GunUid);
            w.U32(AmmoItemId);
            w.U32(LoadedCount);
            w.U32(BackpackRevision);
        }

        public static ItemReloadResult Read(Reader r) => new ItemReloadResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
            GunUid = r.U32(),
            AmmoItemId = r.U32(),
            LoadedCount = r.U32(),
            BackpackRevision = r.U32(),
        };
    }

    /// <summary><c>CItemWeaponFire</c> (opcode 42): fire-and-forget magazine count.</summary>
    public sealed class ItemWeaponFire
    {
        public uint GunUid;
        public uint AmmoItemId;
        public uint LoadedCount;

        public void Write(Writer w)
        {
            w.U32(GunUid);
            w.U32(AmmoItemId);
            w.U32(LoadedCount);
        }

        public static ItemWeaponFire Read(Reader r) => new ItemWeaponFire
        {
            GunUid = r.U32(),
            AmmoItemId = r.U32(),
            LoadedCount = r.U32(),
        };
    }

    /// <summary><c>CItemDurability</c> (opcode 43): fixed-point x1000 durability.</summary>
    public sealed class ItemDurability
    {
        public uint ItemUid;
        public uint Durability;
        public uint MaxDurability;

        public void Write(Writer w)
        {
            w.U32(ItemUid);
            w.U32(Durability);
            w.U32(MaxDurability);
        }

        public static ItemDurability Read(Reader r) => new ItemDurability
        {
            ItemUid = r.U32(),
            Durability = r.U32(),
            MaxDurability = r.U32(),
        };
    }

    /// <summary><c>CBackpackCapacity</c> (opcode 44).</summary>
    public sealed class BackpackCapacity
    {
        public uint Capacity;

        public void Write(Writer w) => w.U32(Capacity);

        public static BackpackCapacity Read(Reader r) => new BackpackCapacity
        {
            Capacity = r.U32(),
        };
    }

    /// <summary><c>SRespawn</c> (opcode 194): revive pose and hp.</summary>
    public sealed class Respawn
    {
        public ushort PlayerId;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;
        public ushort Hp;

        public void Write(Writer w)
        {
            w.U16(PlayerId);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Yaw);
            w.U16(Hp);
        }

        public static Respawn Read(Reader r) => new Respawn
        {
            PlayerId = r.U16(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Yaw = r.U16(),
            Hp = r.U16(),
        };
    }

    /// <summary><c>CContainerOpen</c> (opcode 35).</summary>
    public sealed class ContainerOpen
    {
        public ulong CorrelationId;
        public uint ContainerId;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ContainerId);
        }

        public static ContainerOpen Read(Reader r) => new ContainerOpen
        {
            CorrelationId = r.U64(),
            ContainerId = r.U32(),
        };
    }

    /// <summary><c>SContainerContents</c> (opcode 180): one complete snapshot.</summary>
    public sealed class ContainerContents
    {
        public ulong CorrelationId;
        public uint ContainerId;
        public uint Revision;
        public ushort Capacity;
        public InventoryEntry[] Entries = Array.Empty<InventoryEntry>();

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(ContainerId);
            w.U32(Revision);
            w.U16(Capacity);
            var count = ItemWire.ClampCount(Entries.Length, ItemProtocolLimits.MaxContainerEntries);
            w.U16(count);
            for (var i = 0; i < count; i++)
                Entries[i].Write(w);
        }

        public static ContainerContents Read(Reader r)
        {
            var message = new ContainerContents
            {
                CorrelationId = r.U64(),
                ContainerId = r.U32(),
                Revision = r.U32(),
                Capacity = r.U16(),
            };
            var count = ItemWire.ReadClampedCount(r, ItemProtocolLimits.MaxContainerEntries);
            message.Entries = new InventoryEntry[count];
            for (var i = 0; i < count; i++)
                message.Entries[i] = InventoryEntry.Read(r);
            return message;
        }
    }

    /// <summary><c>CInventorySync</c> (opcode 38).</summary>
    public sealed class InventorySyncRequest
    {
        public ulong CorrelationId;
        public byte Kind;
        public uint OwnerId;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Kind);
            w.U32(OwnerId);
        }

        public static InventorySyncRequest Read(Reader r) => new InventorySyncRequest
        {
            CorrelationId = r.U64(),
            Kind = r.U8(),
            OwnerId = r.U32(),
        };
    }

    /// <summary><c>SInventorySnapshot</c> (opcode 186); correlationId is 0 for pushes.</summary>
    public sealed class InventoryState
    {
        public ulong CorrelationId;
        public InventorySnapshot Snapshot = new InventorySnapshot();

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            Snapshot.Write(w);
        }

        public static InventoryState Read(Reader r) => new InventoryState
        {
            CorrelationId = r.U64(),
            Snapshot = InventorySnapshot.Read(r),
        };
    }

    /// <summary>Ground item payload shared by spawn/drop/snapshot (SPEC §3).</summary>
    public sealed class WorldItemInfo
    {
        public uint WorldItemId;
        public uint ItemUid;
        public uint ItemId;
        public uint Count;
        public string Scene = string.Empty;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;
        public byte Source;

        public void Write(Writer w)
        {
            w.U32(WorldItemId);
            w.U32(ItemUid);
            w.U32(ItemId);
            w.U32(Count);
            w.Str(ItemWire.ClampUtf8(Scene, ItemProtocolLimits.MaxSceneBytes));
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Yaw);
            w.U8(Source);
        }

        public static WorldItemInfo Read(Reader r) => new WorldItemInfo
        {
            WorldItemId = r.U32(),
            ItemUid = r.U32(),
            ItemId = r.U32(),
            Count = r.U32(),
            Scene = ItemWire.ClampUtf8(r.Str(), ItemProtocolLimits.MaxSceneBytes),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Yaw = r.U16(),
            Source = r.U8(),
        };
    }

    /// <summary><c>CWorldItemPickup</c> (opcode 36); target is always the backpack.</summary>
    public sealed class WorldItemPickup
    {
        public ulong CorrelationId;
        public uint WorldItemId;
        public uint ExpectedItemUid;
        public uint ExpectedItemId;
        public uint Count;
        public uint ExpectedBackpackRevision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U32(WorldItemId);
            w.U32(ExpectedItemUid);
            w.U32(ExpectedItemId);
            w.U32(Count);
            w.U32(ExpectedBackpackRevision);
        }

        public static WorldItemPickup Read(Reader r) => new WorldItemPickup
        {
            CorrelationId = r.U64(),
            WorldItemId = r.U32(),
            ExpectedItemUid = r.U32(),
            ExpectedItemId = r.U32(),
            Count = r.U32(),
            ExpectedBackpackRevision = r.U32(),
        };
    }

    /// <summary><c>SWorldItemPickup</c> (opcode 181).</summary>
    public sealed class WorldItemPickupResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint ItemUid;
        public uint BackpackRevision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(ItemUid);
            w.U32(BackpackRevision);
        }

        public static WorldItemPickupResult Read(Reader r) => new WorldItemPickupResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
            ItemUid = r.U32(),
            BackpackRevision = r.U32(),
        };
    }

    /// <summary><c>CWorldItemDrop</c> (opcode 37).</summary>
    public sealed class WorldItemDrop
    {
        public ulong CorrelationId;
        public byte SourceKind;
        public uint SourceId;
        public ushort SourceSlot;
        public uint ItemUid;
        public uint Count;
        public string Scene = string.Empty;
        public int X;
        public int Y;
        public int Z;
        public ushort Yaw;
        public uint ExpectedRevision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(SourceKind);
            w.U32(SourceId);
            w.U16(SourceSlot);
            w.U32(ItemUid);
            w.U32(Count);
            w.Str(ItemWire.ClampUtf8(Scene, ItemProtocolLimits.MaxSceneBytes));
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(Yaw);
            w.U32(ExpectedRevision);
        }

        public static WorldItemDrop Read(Reader r) => new WorldItemDrop
        {
            CorrelationId = r.U64(),
            SourceKind = r.U8(),
            SourceId = r.U32(),
            SourceSlot = r.U16(),
            ItemUid = r.U32(),
            Count = r.U32(),
            Scene = ItemWire.ClampUtf8(r.Str(), ItemProtocolLimits.MaxSceneBytes),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            Yaw = r.U16(),
            ExpectedRevision = r.U32(),
        };
    }

    /// <summary><c>SWorldItemDrop</c> (opcode 182).</summary>
    public sealed class WorldItemDropResult
    {
        public ulong CorrelationId;
        public byte Code;
        public uint WorldItemId;
        public uint SourceRevision;

        public void Write(Writer w)
        {
            w.U64(CorrelationId);
            w.U8(Code);
            w.U32(WorldItemId);
            w.U32(SourceRevision);
        }

        public static WorldItemDropResult Read(Reader r) => new WorldItemDropResult
        {
            CorrelationId = r.U64(),
            Code = r.U8(),
            WorldItemId = r.U32(),
            SourceRevision = r.U32(),
        };
    }

    /// <summary><c>SWorldItemSpawn</c> (opcode 184): payload is a bare WorldItemInfo.</summary>
    public sealed class WorldItemSpawn
    {
        public WorldItemInfo Info = new WorldItemInfo();

        public void Write(Writer w) => Info.Write(w);

        public static WorldItemSpawn Read(Reader r) => new WorldItemSpawn
        {
            Info = WorldItemInfo.Read(r),
        };
    }

    /// <summary><c>SWorldItemDespawn</c> (opcode 185).</summary>
    public sealed class WorldItemDespawn
    {
        public uint WorldItemId;

        public void Write(Writer w) => w.U32(WorldItemId);

        public static WorldItemDespawn Read(Reader r) => new WorldItemDespawn
        {
            WorldItemId = r.U32(),
        };
    }

    /// <summary><c>SWorldItemSnapshot</c> (opcode 183): 0-based chunkIndex, <= 8 entries.</summary>
    public sealed class WorldItemSnapshotChunk
    {
        public uint SnapshotId;
        public ushort ChunkIndex;
        public ushort ChunkCount;
        public WorldItemInfo[] Items = Array.Empty<WorldItemInfo>();

        public void Write(Writer w)
        {
            w.U32(SnapshotId);
            w.U16(ChunkIndex);
            w.U16(ChunkCount);
            var count = ItemWire.ClampCount(Items.Length, ItemProtocolLimits.MaxWorldItemChunkEntries);
            w.U16(count);
            for (var i = 0; i < count; i++)
                Items[i].Write(w);
        }

        public static WorldItemSnapshotChunk Read(Reader r)
        {
            var chunk = new WorldItemSnapshotChunk
            {
                SnapshotId = r.U32(),
                ChunkIndex = r.U16(),
                ChunkCount = r.U16(),
            };
            var count = ItemWire.ReadClampedCount(r, ItemProtocolLimits.MaxWorldItemChunkEntries);
            chunk.Items = new WorldItemInfo[count];
            for (var i = 0; i < count; i++)
                chunk.Items[i] = WorldItemInfo.Read(r);
            return chunk;
        }
    }

    /// <summary>
    /// M2-v10 scene object state: <c>CSceneObjectState</c> (45) and <c>SObjectState</c> (153).
    /// <c>Key</c> is an opaque client-computed id (kind + quantized position hash).
    /// </summary>
    public sealed class SceneObjectState
    {
        public const byte KindDoor = 0;
        public const byte KindBreakable = 1;
        public const byte KindCover = 2;

        public uint Key;
        public byte Kind;
        /// <summary>Door: 0 closed / 1 open. Breakable: 1 breaked.</summary>
        public byte State;
        public uint RaidEpoch;

        public void Write(Writer w)
        {
            w.U32(Key);
            w.U8(Kind);
            w.U8(State);
            w.U32(RaidEpoch);
        }

        public static SceneObjectState Read(Reader r) => new SceneObjectState
        {
            Key = r.U32(),
            Kind = r.U8(),
            State = r.U8(),
            RaidEpoch = r.U32(),
        };
    }

    /// <summary>
    /// M2-v10 explosion fact: <c>CExplosion</c> (46) and <c>SExplosion</c> (193).
    /// Carries the native DamageInfo values; the server uses it both for presentation relay
    /// and for the damage of the explosion-flagged hits that follow it.
    /// </summary>
    public sealed class ExplosionEvent
    {
        public uint ExplosionId;
        public int X;
        public int Y;
        public int Z;
        public ushort RadiusCm;
        public ushort Damage;
        public uint WeaponId;
        public byte SourceKind;
        public uint SourceId;
        public byte FxType;
        public uint RaidEpoch;

        public void Write(Writer w)
        {
            w.U32(ExplosionId);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.U16(RadiusCm);
            w.U16(Damage);
            w.U32(WeaponId);
            w.U8(SourceKind);
            w.U32(SourceId);
            w.U8(FxType);
            w.U32(RaidEpoch);
        }

        public static ExplosionEvent Read(Reader r) => new ExplosionEvent
        {
            ExplosionId = r.U32(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            RadiusCm = r.U16(),
            Damage = r.U16(),
            WeaponId = r.U32(),
            SourceKind = r.U8(),
            SourceId = r.U32(),
            FxType = r.U8(),
            RaidEpoch = r.U32(),
        };
    }

    /// <summary>
    /// <c>CImpactFx</c> (51) / <c>SImpactFx</c> (201), contract 2.18.0 / M2-v12.
    /// Presentation only: the server validates ownership/epoch/monotonic shotId and relays;
    /// it never settles damage from this message.
    /// </summary>
    public sealed class ImpactFx
    {
        public const byte FlagBlocked = 1 << 0;

        public uint RaidEpoch;
        public byte SourceKind; // 0 player, 1 ai
        public uint SourceId;
        public uint WeaponId;
        public uint ShotId;
        public int X;
        public int Y;
        public int Z;
        public short NX;
        public short NY;
        public short NZ;
        public byte Flags;

        public void Write(Writer w)
        {
            w.U32(RaidEpoch);
            w.U8(SourceKind);
            w.U32(SourceId);
            w.U32(WeaponId);
            w.U32(ShotId);
            w.I32(X);
            w.I32(Y);
            w.I32(Z);
            w.I16(NX);
            w.I16(NY);
            w.I16(NZ);
            w.U8(Flags);
        }

        public static ImpactFx Read(Reader r) => new ImpactFx
        {
            RaidEpoch = r.U32(),
            SourceKind = r.U8(),
            SourceId = r.U32(),
            WeaponId = r.U32(),
            ShotId = r.U32(),
            X = r.I32(),
            Y = r.I32(),
            Z = r.I32(),
            NX = r.I16(),
            NY = r.I16(),
            NZ = r.I16(),
            Flags = r.U8(),
        };
    }
}

