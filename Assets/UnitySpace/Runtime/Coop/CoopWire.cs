// US-T39: co-op's wire format (new file; docs/MIGRATION.md "Co-op"). Text, invariant culture, doubles as their IEEE bits so
// every peer steps on exactly the host's numbers.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Unity.SpaceGod.Core;

namespace Unity.SpaceGod.Coop
{
    /// <summary>
    /// The sim clock's whole state at one moment (U.SPC's <see cref="SimClock"/>: the date, the warp, and the eased pause's
    /// three flags and speed). The host sends the state its clock had just before it advanced a frame; each client puts its
    /// own clock in exactly that state and advances by the same real seconds, so the dates agree bit for bit. The pause's
    /// flags and speed have no public setter in the Core (unchanged), so they are set by reflection.
    /// </summary>
    public struct ClockState
    {
        public double Jd;
        public int Warp;
        public bool Paused, Pausing, Resuming;
        public double Speed;

        public bool PauseHeld => Paused || Pausing;

        static readonly FieldInfo PausedField = typeof(SimClock).GetField("paused", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo PausingField = typeof(SimClock).GetField("pausing", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo ResumingField = typeof(SimClock).GetField("resuming", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo SpeedSetter = typeof(SimClock).GetProperty(nameof(SimClock.Speed))?.GetSetMethod(true);

        public static ClockState Of(SimClock c) => new ClockState
        {
            Jd = c.Jd, Warp = c.WarpIndex, Paused = c.Paused, Pausing = c.Pausing, Resuming = c.Resuming, Speed = c.Speed,
        };

        public void ApplyTo(SimClock c)
        {
            c.SetJd(Jd);
            c.SetWarp(Warp);
            PausedField?.SetValue(c, Paused);
            PausingField?.SetValue(c, Pausing);
            ResumingField?.SetValue(c, Resuming);
            SpeedSetter?.Invoke(c, new object[] { Speed });
        }

        public string Write() =>
            Wire.D(Jd) + " " + Wire.I(Warp) + " " + (Paused ? "1" : "0") + (Pausing ? "1" : "0") + (Resuming ? "1" : "0") + " " + Wire.D(Speed);

        public static ClockState Read(string[] f, int at) => new ClockState
        {
            Jd = Wire.ParseD(f[at]),
            Warp = Wire.ParseI(f[at + 1]),
            Paused = f[at + 2][0] == '1',
            Pausing = f[at + 2][1] == '1',
            Resuming = f[at + 2][2] == '1',
            Speed = Wire.ParseD(f[at + 3]),
        };
    }

    /// <summary>
    /// One host frame as every client replays it: the real seconds the host's Update ran on, its clock just before and
    /// just after it advanced, whether (and from where) it ran the world's adventures, the commands applied at the start of
    /// the frame and what the frame did after that a client must do too (<see cref="Post"/>: a system made the active one).
    /// <code>
    /// frame 1234 3C888889 &lt;jdBefore&gt; 4 000 &lt;speed&gt; &lt;jdAfter&gt; 1 &lt;x&gt; &lt;y&gt; &lt;z&gt;
    /// e s take 1 0 @S 3        a session command (CommandCodec's line, unchanged), here with the seat's key
    /// e a 1 select b 3         player 2 selected body 3
    /// p activate Alpha Centauri
    /// </code>
    /// </summary>
    public sealed class FrameRecord
    {
        public long Frame;
        public float Dt;
        public ClockState Before;
        public double JdAfter;
        public bool Adventures;
        public DVec3 AdventurePoint;
        public readonly List<string> Entries = new List<string>();
        public readonly List<string> Post = new List<string>();

        public string Write()
        {
            var sb = new StringBuilder();
            sb.Append("frame ").Append(Wire.L(Frame)).Append(' ').Append(Wire.F(Dt)).Append(' ').Append(Before.Write()).Append(' ')
              .Append(Wire.D(JdAfter)).Append(' ').Append(Adventures ? '1' : '0').Append(' ')
              .Append(Wire.D(AdventurePoint.X)).Append(' ').Append(Wire.D(AdventurePoint.Y)).Append(' ').Append(Wire.D(AdventurePoint.Z));
            foreach (var e in Entries) sb.Append("\ne ").Append(e);
            foreach (var p in Post) sb.Append("\np ").Append(p);
            return sb.ToString();
        }

        public static FrameRecord Read(string text)
        {
            var lines = text.Split('\n');
            var f = lines[0].Split(' ');
            if (f.Length < 12 || f[0] != "frame") throw new FormatException("Not a frame: '" + lines[0] + "'");
            var r = new FrameRecord
            {
                Frame = Wire.ParseL(f[1]),
                Dt = Wire.ParseF(f[2]),
                Before = ClockState.Read(f, 3),
                JdAfter = Wire.ParseD(f[7]),
                Adventures = f[8] == "1",
                AdventurePoint = new DVec3(Wire.ParseD(f[9]), Wire.ParseD(f[10]), Wire.ParseD(f[11])),
            };
            for (int i = 1; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.StartsWith("e ", StringComparison.Ordinal)) r.Entries.Add(l.Substring(2));
                else if (l.StartsWith("p ", StringComparison.Ordinal)) r.Post.Add(l.Substring(2));
            }
            return r;
        }
    }

    /// <summary>Number formats and the save's packing for the wire.</summary>
    public static class Wire
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string D(double v) => BitConverter.DoubleToInt64Bits(v).ToString("X16", Inv);
        public static double ParseD(string s) => BitConverter.Int64BitsToDouble(long.Parse(s, NumberStyles.HexNumber, Inv));
        public static string F(float v) => BitConverter.SingleToInt32Bits(v).ToString("X8", Inv);
        public static float ParseF(string s) => BitConverter.Int32BitsToSingle(int.Parse(s, NumberStyles.HexNumber, Inv));
        public static string I(int v) => v.ToString(Inv);
        public static int ParseI(string s) => int.Parse(s, NumberStyles.Integer, Inv);
        public static string L(long v) => v.ToString(Inv);
        public static long ParseL(string s) => long.Parse(s, NumberStyles.Integer, Inv);
        public static string R(double v) => v.ToString("R", Inv);
        public static double ParseR(string s) => double.Parse(s, NumberStyles.Float, Inv);

        /// <summary>Characters of the packed save per chunk message (well under the engine's 64 KB partial-bunch limit).</summary>
        public const int ChunkChars = 16000;

        /// <summary>A save (Core/Persistence's SaveGame bytes, already compressed) base64'd and cut into chunk strings.</summary>
        public static List<string> Pack(byte[] save)
        {
            string b64 = Convert.ToBase64String(save);
            var chunks = new List<string>((b64.Length + ChunkChars - 1) / ChunkChars);
            for (int i = 0; i < b64.Length; i += ChunkChars) chunks.Add(b64.Substring(i, Math.Min(ChunkChars, b64.Length - i)));
            return chunks;
        }

        public static byte[] Unpack(IEnumerable<string> chunks) => Convert.FromBase64String(string.Concat(chunks));

        /// <summary>64-bit FNV-1a over the UTF-8 bytes of <paramref name="text"/> (Bench/CSharp's digest hash).</summary>
        public static ulong Fnv1a64(string text)
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                h ^= b;
                h *= 0x100000001B3UL;
            }
            return h;
        }

        public static string Hex(ulong h) => h.ToString("X16", Inv);
    }
}
