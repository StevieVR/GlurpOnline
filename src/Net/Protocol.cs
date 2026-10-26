using System.Collections.Generic;

namespace GlurpOnline.Net
{
    internal enum Msg : byte
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        LobbyState = 4,
        MemberUpdate = 5,
        Kick = 6,
        Ping = 7,
        Pong = 8,
        Bye = 9,
        UpdateRequest = 10,
        UpdateData = 11,

        StartGame = 20,
        SceneReady = 21,
        TurnBegin = 22,
        StreamChunk = 23,
        TurnResult = 24,
        Continue = 25,
        PlayerDropped = 26,
        MoreRounds = 27,
        BackToLobby = 28,
        StreamEnd = 29,
        GhostState = 30,
    }

    internal static class MsgInfo
    {
        public static bool IsRelayed(Msg m)
        {
            switch (m)
            {
                case Msg.SceneReady:
                case Msg.TurnBegin:
                case Msg.StreamChunk:
                case Msg.TurnResult:
                case Msg.StreamEnd:
                case Msg.GhostState:
                    return true;
                default:
                    return false;
            }
        }
    }

    internal sealed class Member
    {
        public ulong Id;
        public string Name = "Player";
        public int ColorIndex = -1;
        public string HatId;
        public int LocalPlayers = 1;
        public bool Ready;
        public int PingMs = -1;
        public bool IsHost;

        public void Write(PacketWriter w)
        {
            w.Write(Id);
            w.Write(Name);
            w.Write((short)ColorIndex);
            w.Write(HatId);
            w.Write((byte)LocalPlayers);
            w.Write(Ready);
            w.Write((short)PingMs);
            w.Write(IsHost);
        }

        public static Member Read(PacketReader r) => new Member
        {
            Id = r.ReadULong(),
            Name = r.ReadString(),
            ColorIndex = r.ReadShort(),
            HatId = r.ReadString(),
            LocalPlayers = r.ReadByte(),
            Ready = r.ReadBool(),
            PingMs = r.ReadShort(),
            IsHost = r.ReadBool(),
        };
    }

    internal sealed class LobbySettings
    {
        public int Rounds = 5;
        public int Difficulty = 1;
        public int Privacy;
        public int MaxPlayers = 8;
        public int Mode;
        public bool SeeEachOther = true;

        public bool AllAtOnce => Mode == 1;

        public void Write(PacketWriter w)
        {
            w.Write((byte)Rounds);
            w.Write((byte)Difficulty);
            w.Write((byte)Privacy);
            w.Write((byte)MaxPlayers);
            w.Write((byte)Mode);
            w.Write(SeeEachOther);
        }

        public static LobbySettings Read(PacketReader r) => new LobbySettings
        {
            Rounds = r.ReadByte(),
            Difficulty = r.ReadByte(),
            Privacy = r.ReadByte(),
            MaxPlayers = r.ReadByte(),
            Mode = r.ReadByte(),
            SeeEachOther = r.ReadBool(),
        };

        public LobbySettings Clone() => (LobbySettings)MemberwiseClone();
    }

    internal sealed class EventSpec
    {
        public string Id;
        public string Variation;
        public string Mutator;
        public string SecondVariation;

        public void Write(PacketWriter w)
        {
            w.Write(Id);
            w.Write(Variation);
            w.Write(Mutator);
            w.Write(SecondVariation);
        }

        public static EventSpec Read(PacketReader r) => new EventSpec
        {
            Id = r.ReadString(),
            Variation = r.ReadString(),
            Mutator = r.ReadString(),
            SecondVariation = r.ReadString(),
        };

        public static EventSpec From(EventConfiguration e) => e == null ? null : new EventSpec
        {
            Id = e.id,
            Variation = e.variation,
            Mutator = e.mutator,
            SecondVariation = e.secondVariation,
        };

        public override string ToString() => $"{Id}/{Variation}/{Mutator}/{SecondVariation}";
    }

    internal sealed class RosterEntry
    {
        public int PlayerNumber;
        public ulong Owner;
        public string Name;
        public int ColorIndex;
        public string HatId;

        public void Write(PacketWriter w)
        {
            w.Write((short)PlayerNumber);
            w.Write(Owner);
            w.Write(Name);
            w.Write((short)ColorIndex);
            w.Write(HatId);
        }

        public static RosterEntry Read(PacketReader r) => new RosterEntry
        {
            PlayerNumber = r.ReadShort(),
            Owner = r.ReadULong(),
            Name = r.ReadString(),
            ColorIndex = r.ReadShort(),
            HatId = r.ReadString(),
        };
    }

    internal static class RosterIO
    {
        public static void Write(PacketWriter w, List<RosterEntry> roster)
        {
            w.Write((byte)roster.Count);
            foreach (var e in roster) e.Write(w);
        }

        public static List<RosterEntry> Read(PacketReader r)
        {
            int n = r.ReadByte();
            var list = new List<RosterEntry>(n);
            for (int i = 0; i < n; i++) list.Add(RosterEntry.Read(r));
            return list;
        }
    }
}
