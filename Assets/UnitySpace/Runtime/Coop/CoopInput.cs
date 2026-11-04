// US-T43: the helm's input stream in co-op (new file; docs/MIGRATION.md "Co-op"). The pilot loop reads one frame of the
// original's InputState; in co-op the frame it reads is the helm holder's, carried in the lockstep frames, so every peer
// flies the same hull on the same numbers.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.SpaceGod.Input;
using UnityEngine;

namespace Unity.SpaceGod.Coop
{
    /// <summary>
    /// One frame of the helm holder's hands, as the pilot loop reads them: every public field of the original's
    /// <see cref="InputState"/> (by name, so a field added there is carried too), plus what of the holder's own machine
    /// the loop reads besides the keys: its look sensitivity, whether a click panel had the mouse, what it had selected and
    /// hovered (a lock picks among those), and its camera's up frame (a fresh helm is levelled on it).
    ///
    /// The mouse's vertical delta goes as the holder's "look up" (its invert-look already applied); each peer turns it back
    /// into its own convention before the loop applies its own (a sign flip, exact), so the loop sees the holder's look.
    /// </summary>
    public sealed class PilotFrame
    {
        static readonly FieldInfo[] Fields = Collect();

        /// <summary>Held from one message to the next, as a stick stays where it is put (the rest are edges, deltas or picks).</summary>
        static readonly HashSet<string> Held = new HashSet<string>
        {
            nameof(InputState.Move), nameof(InputState.Roll), nameof(InputState.Boost), nameof(InputState.Slow), nameof(InputState.Cruise),
            nameof(InputState.Fire), nameof(InputState.FireEmp), nameof(InputState.LookHeld), nameof(InputState.MousePosition),
        };

        /// <summary>Summed over the messages a host frame takes in.</summary>
        static readonly HashSet<string> Summed = new HashSet<string>
        {
            nameof(InputState.MouseDelta), nameof(InputState.LookDelta), nameof(InputState.Scroll),
        };

        static FieldInfo[] Collect()
        {
            var list = new List<FieldInfo>();
            foreach (var f in typeof(InputState).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var t = f.FieldType;
                if (t == typeof(bool) || t == typeof(int) || t == typeof(float) || t == typeof(Vector2) || t == typeof(Vector3)) list.Add(f);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list.ToArray();
        }

        public readonly object[] Values = new object[Fields.Length];
        public float Sensitivity;
        public bool PanelOpen;
        public string Selected = "n", Hovered = "n";
        public Quaternion UpFrame = Quaternion.identity;

        /// <summary>A frame of nothing pressed (what the helm flies on until its holder's first message).</summary>
        public static PilotFrame Idle()
        {
            var p = new PilotFrame();
            for (int i = 0; i < Fields.Length; i++) p.Values[i] = Default(Fields[i]);
            return p;
        }

        static object Default(FieldInfo f)
        {
            if (f.Name == nameof(InputState.NumberKey)) return -1;
            var t = f.FieldType;
            if (t == typeof(bool)) return false;
            if (t == typeof(int)) return 0;
            if (t == typeof(float)) return 0f;
            if (t == typeof(Vector2)) return Vector2.zero;
            return Vector3.zero;
        }

        /// <summary>This machine's hands this frame (<paramref name="lookY"/>: its invert-look, applied to the vertical delta).</summary>
        public static PilotFrame Of(InputState input, Func<float, float> lookY)
        {
            var p = new PilotFrame();
            for (int i = 0; i < Fields.Length; i++)
            {
                var v = Fields[i].GetValue(input);
                if (Fields[i].Name == nameof(InputState.MouseDelta) && lookY != null)
                {
                    var d = (Vector2)v;
                    v = new Vector2(d.x, lookY(d.y));
                }
                p.Values[i] = v;
            }
            return p;
        }

        /// <summary>Puts this frame into <paramref name="input"/> as this machine's poll would have, its invert-look turned back.</summary>
        public void ApplyTo(InputState input, Func<float, float> lookY)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                var v = Values[i];
                if (Fields[i].Name == nameof(InputState.MouseDelta) && lookY != null)
                {
                    var d = (Vector2)v;
                    v = new Vector2(d.x, lookY(d.y));
                }
                Fields[i].SetValue(input, v);
            }
        }

        /// <summary>A copy of every carried field of <paramref name="input"/> as it stands (to put back after the loop).</summary>
        public static object[] Snapshot(InputState input)
        {
            var v = new object[Fields.Length];
            for (int i = 0; i < Fields.Length; i++) v[i] = Fields[i].GetValue(input);
            return v;
        }

        public static void Restore(InputState input, object[] snapshot)
        {
            for (int i = 0; i < Fields.Length; i++) Fields[i].SetValue(input, snapshot[i]);
        }

        /// <summary>
        /// What a host frame flies on from the holder's messages since the last one: the held fields as the latest left them,
        /// the edges (a key pressed) if any message had them, the deltas summed, a pick (a number key, a step) the latest
        /// made. With no message the stick stays where it was and nothing is pressed.
        /// </summary>
        public static PilotFrame Compose(PilotFrame last, List<PilotFrame> since)
        {
            var p = Idle();
            var basis = since.Count > 0 ? since[since.Count - 1] : last;
            if (basis != null)
            {
                p.Sensitivity = basis.Sensitivity;
                p.PanelOpen = basis.PanelOpen;
                p.Selected = basis.Selected;
                p.Hovered = basis.Hovered;
                p.UpFrame = basis.UpFrame;
                for (int i = 0; i < Fields.Length; i++)
                    if (Held.Contains(Fields[i].Name)) p.Values[i] = basis.Values[i];
            }
            foreach (var m in since)
                for (int i = 0; i < Fields.Length; i++)
                {
                    var f = Fields[i];
                    if (Held.Contains(f.Name)) continue;
                    var t = f.FieldType;
                    if (Summed.Contains(f.Name))
                    {
                        if (t == typeof(float)) p.Values[i] = (float)p.Values[i] + (float)m.Values[i];
                        else if (t == typeof(Vector2)) p.Values[i] = (Vector2)p.Values[i] + (Vector2)m.Values[i];
                    }
                    else if (t == typeof(bool)) p.Values[i] = (bool)p.Values[i] || (bool)m.Values[i];
                    else if (t == typeof(int))
                    {
                        if (!Equals(m.Values[i], Default(f))) p.Values[i] = m.Values[i];
                    }
                    else p.Values[i] = m.Values[i];
                }
            return p;
        }

        /// <summary>The same frame with its edges, deltas and picks dropped: what a second Update in one engine tick sends
        /// (a key pressed is pressed once, the mouse moved once).</summary>
        public PilotFrame HeldOnly()
        {
            var p = Idle();
            p.Sensitivity = Sensitivity;
            p.PanelOpen = PanelOpen;
            p.Selected = Selected;
            p.Hovered = Hovered;
            p.UpFrame = UpFrame;
            for (int i = 0; i < Fields.Length; i++)
                if (Held.Contains(Fields[i].Name)) p.Values[i] = Values[i];
            return p;
        }

        public bool AnyEdge
        {
            get
            {
                for (int i = 0; i < Fields.Length; i++)
                    if (!Held.Contains(Fields[i].Name) && !Equals(Values[i], Default(Fields[i]))) return true;
                return false;
            }
        }

        // ---------------------------------------------------------------- text

        public string Write()
        {
            var sb = new StringBuilder(256);
            sb.Append(Wire.F(Sensitivity)).Append(' ').Append(PanelOpen ? '1' : '0').Append(' ').Append(Esc(Selected)).Append(' ').Append(Esc(Hovered))
              .Append(' ').Append(Wire.F(UpFrame.x)).Append(',').Append(Wire.F(UpFrame.y)).Append(',').Append(Wire.F(UpFrame.z)).Append(',').Append(Wire.F(UpFrame.w));
            for (int i = 0; i < Fields.Length; i++)
            {
                sb.Append(' ');
                switch (Values[i])
                {
                    case bool b: sb.Append(b ? '1' : '0'); break;
                    case int n: sb.Append(Wire.I(n)); break;
                    case float f: sb.Append(Wire.F(f)); break;
                    case Vector2 v: sb.Append(Wire.F(v.x)).Append(',').Append(Wire.F(v.y)); break;
                    case Vector3 v: sb.Append(Wire.F(v.x)).Append(',').Append(Wire.F(v.y)).Append(',').Append(Wire.F(v.z)); break;
                }
            }
            return sb.ToString();
        }

        public static PilotFrame Read(string text)
        {
            var f = text.Split(' ');
            if (f.Length != 5 + Fields.Length) throw new FormatException("pilot frame has " + f.Length + " fields, " + (5 + Fields.Length) + " expected");
            var p = new PilotFrame
            {
                Sensitivity = Wire.ParseF(f[0]),
                PanelOpen = f[1] == "1",
                Selected = Unesc(f[2]),
                Hovered = Unesc(f[3]),
            };
            var q = f[4].Split(',');
            p.UpFrame = new Quaternion(Wire.ParseF(q[0]), Wire.ParseF(q[1]), Wire.ParseF(q[2]), Wire.ParseF(q[3]));
            for (int i = 0; i < Fields.Length; i++)
            {
                string s = f[5 + i];
                var t = Fields[i].FieldType;
                if (t == typeof(bool)) p.Values[i] = s == "1";
                else if (t == typeof(int)) p.Values[i] = Wire.ParseI(s);
                else if (t == typeof(float)) p.Values[i] = Wire.ParseF(s);
                else
                {
                    var c = s.Split(',');
                    p.Values[i] = t == typeof(Vector2) ? (object)new Vector2(Wire.ParseF(c[0]), Wire.ParseF(c[1]))
                        : new Vector3(Wire.ParseF(c[0]), Wire.ParseF(c[1]), Wire.ParseF(c[2]));
                }
            }
            return p;
        }

        static string Esc(string key) => string.IsNullOrEmpty(key) ? "n" : key.Replace(' ', '|');
        static string Unesc(string s) => s.Replace('|', ' ');
    }
}
