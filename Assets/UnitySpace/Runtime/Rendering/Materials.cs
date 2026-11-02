using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod.Rendering
{
    /// <summary>
    /// The game's shaders, as the active render pipeline supplies them (U.SPC-T429: <see cref="RenderBackend"/> loads
    /// each from its pipeline's Resources folder, which also guarantees they ship in builds), and the materials made
    /// from them.
    /// </summary>
    public static class Materials
    {
        public static Shader Planet => Load(RenderShaders.Planet);
        public static Shader Sun => Load(RenderShaders.Sun);
        public static Shader Glow => Load(RenderShaders.Glow);
        public static Shader Ring => Load(RenderShaders.Ring);
        public static Shader Ship => Load(RenderShaders.Ship);
        public static Shader Line => Load(RenderShaders.Line);
        public static Shader Stars => Load(RenderShaders.Stars);
        public static Shader GasGiant => Load(RenderShaders.GasGiant);
        public static Shader Asteroids => Load(RenderShaders.Asteroids);
        public static Shader Fx => Load(RenderShaders.Fx);
        public static Shader Points => Load(RenderShaders.Points);
        public static Shader Sky => Load(RenderShaders.Sky);

        static Shader Load(string name) => RenderBackend.Active.LoadShader(name);

        public static Material Create(Shader shader, string name)
        {
            var m = new Material(shader) { name = name, hideFlags = HideFlags.DontSave };
            // Views that destroy their own material directly leave dead entries: sweep them out now and then.
            if (Created.Count >= 2048) Created.RemoveWhere(x => x == null);
            Created.Add(m);
            return m;
        }

        /// <summary>Every material made here and not yet destroyed: DontSave keeps them alive past their scene.</summary>
        static readonly System.Collections.Generic.HashSet<Material> Created = new System.Collections.Generic.HashSet<Material>();
        static readonly System.Collections.Generic.HashSet<Material> Shared = new System.Collections.Generic.HashSet<Material>();
        static readonly System.Collections.Generic.HashSet<Mesh> OwnedMeshes = new System.Collections.Generic.HashSet<Mesh>();

        /// <summary>A mesh built for one view alone (a line, an effects mesh): destroyed with that view's system.</summary>
        public static Mesh Own(Mesh mesh)
        {
            if (mesh != null) OwnedMeshes.Add(mesh);
            return mesh;
        }

        /// <summary>Destroys one view-owned mesh now (a fighter's trail when it dies).</summary>
        public static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null) return;
            OwnedMeshes.Remove(mesh);
            Object.Destroy(mesh);
        }

        /// <summary>A material cached for every view to share (it outlives any one star system's views).</summary>
        public static Material MarkShared(Material m)
        {
            if (m != null) Shared.Add(m);
            return m;
        }

        /// <summary>
        /// Destroys the materials used under <paramref name="root"/> that belong only to those views
        /// (DontSave materials outlive their GameObjects otherwise), leaving the shared caches alone.
        /// </summary>
        public static void DestroyOwned(Transform root)
        {
            var owned = new System.Collections.Generic.HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in r.sharedMaterials)
                    if (mat != null && !Shared.Contains(mat)) owned.Add(mat);
            foreach (var mat in owned)
            {
                Created.Remove(mat);
                Object.Destroy(mat);
            }
            // Meshes too, but only those built for these views (cached hull meshes and imported models stay).
            var meshes = new System.Collections.Generic.HashSet<Mesh>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && OwnedMeshes.Contains(mf.sharedMesh)) meshes.Add(mf.sharedMesh);
            foreach (var mesh in meshes) DestroyMesh(mesh);
        }

        /// <summary>
        /// The game scene is going (back to the main menu): every material and view mesh made for it goes too, all but
        /// the shared caches, which the next game reuses. Unity destroys neither on a scene change (DontSave, and the
        /// meshes are held here), so each round trip through the menu would otherwise leave a whole system's worth behind.
        /// </summary>
        public static void DestroyAllOwned()
        {
            foreach (var mat in Created)
                if (mat != null && !Shared.Contains(mat)) Object.Destroy(mat);
            Created.RemoveWhere(m => m == null || !Shared.Contains(m));
            foreach (var mesh in OwnedMeshes)
                if (mesh != null) Object.Destroy(mesh);
            OwnedMeshes.Clear();
        }
    }
}
