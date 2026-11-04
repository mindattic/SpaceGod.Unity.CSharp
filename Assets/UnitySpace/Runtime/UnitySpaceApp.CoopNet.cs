// US-T39: the Unity side of co-op's client loop (new file, SpaceGod.Unity.CSharp). Under Unreal a client's frames were
// replayed by running the facade's player loop with the host's seconds (UnityBridge.Tick(rec.Dt)); Unity runs Update on
// its own clock, so on a co-op client the app's Update runs only when CoopNet replays a host frame, on that frame's seconds.
using Unity.SpaceGod.Coop;
using UnityEngine;

namespace Unity.SpaceGod
{
    public sealed partial class UnitySpaceApp
    {
        bool coopReplaying;

        /// <summary>True on a co-op client outside a replay: Unity's own call of Update does nothing there (no frame of
        /// the host's to run). False in single-player and on the host: the original's Update.</summary>
        bool CoopUpdateHeld()
        {
            var peer = CoopPeer.Current;
            return peer != null && peer.IsClient && !coopReplaying;
        }

        /// <summary>Time.unscaledDeltaTime, or on a co-op client the host's seconds for the frame being replayed.</summary>
        float CoopUnscaledDeltaTime()
        {
            if (Audit.AuditRunner.Dt is float audit) return audit; // US-T70: the scripted smoke/audit's fixed 1/60 s step, as under Unreal
            var peer = CoopPeer.Current;
            return peer != null && peer.IsClient && peer.ClientRecord != null ? peer.ClientRecord.Dt : Time.unscaledDeltaTime;
        }

        /// <summary>One host frame, replayed: the app's Update on that frame's record (<see cref="CoopNet"/>).</summary>
        internal void CoopReplay()
        {
            coopReplaying = true;
            try { Update(); }
            finally { coopReplaying = false; }
        }
    }
}
