using System.Collections.Generic;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.SpaceGod.Views
{
    /// <summary>
    /// Boarding pods and Breacher rounds (U.SPC-T466) stuck in hulls. Each is parented to the model of the ship (or platform) it holed,
    /// drill buried and tail standing out of the plating, so it rides along as the hull manoeuvres,
    /// and it stays there after the fight until the ship is lost or the battle is long over. Breacher rounds still in flight,
    /// or tumbling adrift after a miss, are drawn here too, as their own round model (U.SPC-T517): a Breacher is a fired
    /// round, never a ship, so it has no ship model of its own.
    /// </summary>
    public sealed class EmbeddedPods
    {
        readonly UnitySpaceApp app;
        readonly Transform root;
        readonly Dictionary<Embedded, GameObject> pods = new Dictionary<Embedded, GameObject>();
        readonly Dictionary<Missile, GameObject> rounds = new Dictionary<Missile, GameObject>();

        public EmbeddedPods(UnitySpaceApp app, Transform root)
        {
            this.app = app;
            this.root = root;
        }

        public void Update()
        {
            var live = new HashSet<Embedded>();
            var flying = new HashSet<Missile>();
            var war = app.World.War;
            foreach (var b in war.Battles)
            {
                Track(b, live);
                TrackRounds(b, flying);
            }
            foreach (var b in war.Ended) Track(b, live);
            var gone = new List<Embedded>();
            foreach (var kv in pods)
                if (!live.Contains(kv.Key) || kv.Value == null) gone.Add(kv.Key);
            foreach (var e in gone)
            {
                if (pods[e] != null) Object.Destroy(pods[e]);
                pods.Remove(e);
            }
            var landed = new List<Missile>();
            foreach (var kv in rounds)
                if (!flying.Contains(kv.Key) || kv.Value == null) landed.Add(kv.Key);
            foreach (var m in landed)
            {
                if (rounds[m] != null) Object.Destroy(rounds[m]);
                rounds.Remove(m);
            }
        }

        void Track(Battle b, HashSet<Embedded> live)
        {
            foreach (var e in b.Embedded)
            {
                if (e.Unit.Dead || (e.Unit.Ship != null && e.Unit.Ship.Destroyed)) continue;
                var parent = app.UnitTransform(e.Unit);
                if (parent == null) continue;
                live.Add(e);
                if (pods.TryGetValue(e, out var go) && go != null && go.transform.parent == parent) continue;
                if (go != null) Object.Destroy(go);
                pods[e] = Create(e, parent, b);
            }
        }

        /// <summary>Every Breacher round of <paramref name="b"/> in space: its model at the round, nose along its track, or
        /// tumbling end over end once it has gone adrift (U.SPC-T519).</summary>
        void TrackRounds(Battle b, HashSet<Missile> flying)
        {
            if (root == null) return;
            float now = Time.unscaledTime;
            foreach (var m in b.Missiles)
            {
                if (m.Gone || m.Kind != MissileKind.Breacher) continue;
                var owner = m.Owner ?? (m.Side >= 0 && m.Side < 2 ? b.SideLeaders[m.Side] : null);
                if (owner == null) continue;
                flying.Add(m);
                if (!rounds.TryGetValue(m, out var go) || go == null)
                    rounds[m] = go = CreateRound(owner);
                var t = go.transform;
                t.localPosition = app.Relative(app.BattleScene(b, m.Pos));
                var v = m.Vel.SwapYZ;
                var track = new Vector3((float)v.X, (float)v.Y, (float)v.Z);
                if (track.sqrMagnitude < 1e-12f) track = Vector3.forward;
                track.Normalize();
                var along = Quaternion.LookRotation(track, Mathf.Abs(track.y) > 0.95f ? Vector3.right : Vector3.up);
                t.localRotation = m.Adrift ? along * Quaternion.Euler(now * 40f, now * 23f, now * 11f) : along;
            }
        }

        GameObject CreateRound(Faction owner)
        {
            var go = new GameObject("Breacher round");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = ShipMeshFactory.GetBreacherRound(owner);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ShipModels.ProceduralMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            go.transform.localScale = Vector3.one * (float)app.Scale.ShipLength(BreacherBays.RoundLengthKm);
            return go;
        }

        GameObject Create(Embedded e, Transform parent, Battle b)
        {
            var owner = e.Side >= 0 && e.Side < 2 ? b.SideLeaders[e.Side] : e.Unit.Faction;
            var go = new GameObject(e.Breacher ? "Breacher round" : "Boarding pod");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = e.Breacher ? ShipMeshFactory.GetBreacherRound(owner ?? e.Unit.Faction)
                : ShipMeshFactory.GetPod(owner ?? e.Unit.Faction);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ShipModels.ProceduralMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            // Pods are drawn a little large so they read on a capital ship: a few percent of its length.
            // A Breacher round is drawn at its own length, a good deal bigger than a pod.
            float size = e.Breacher
                ? Mathf.Clamp((float)BreacherBays.RoundLengthKm / Mathf.Max((float)e.Unit.LengthKm, 0.01f), 0.08f, 0.35f)
                : Mathf.Clamp(0.025f / Mathf.Max((float)e.Unit.LengthKm, 0.01f), 0.04f, 0.15f);
            var inward = new Vector3((float)e.Inward.X, (float)e.Inward.Y, (float)e.Inward.Z);
            if (inward.sqrMagnitude < 1e-6f) inward = Vector3.forward;
            inward.Normalize();
            var at = new Vector3((float)e.Local.X, (float)e.Local.Y, (float)e.Local.Z);
            // The drill tip is buried: the pod's centre sits half a pod out along the way it came in.
            go.transform.localPosition = at - inward * size * 0.25f;
            go.transform.localRotation = Quaternion.LookRotation(inward, Mathf.Abs(inward.y) > 0.95f ? Vector3.right : Vector3.up);
            go.transform.localScale = Vector3.one * size;
            return go;
        }
    }
}
