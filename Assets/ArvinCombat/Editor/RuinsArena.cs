using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArvinCombat.EditorTools
{
    /// <summary>
    /// Builds the "abandoned ruins" arena (Mortal Kombat mood) from the Tower Defense and MOBA props:
    /// cracked stone floor, broken brick/stone walls, broken columns with banners, braziers with fire,
    /// skulls and bones, dead trees, rocks, red fog and moonlight.
    /// The fighting circle (radius 9.5) stays empty; props start at radius ~10.5.
    /// Called by ArvinCombatSetup.BuildScene when the props are in the project.
    /// </summary>
    public static class RuinsArena
    {
        const string P = "Assets/MOBA and Tower Defense/Prefabs_props/";
        const string FX = "Assets/MOBA and Tower Defense/Prefabs_Particles/";
        const string TEX = "Assets/MOBA and Tower Defense/Prefabs_props/Textures/";
        const string MatDir = "Assets/ArvinCombat/Generated/Materials/";

        public const float FightRadius = 9.5f;
        const float WallRadius = 13.2f;

        static System.Random _rng;
        static Transform _root;

        public static bool Available() => File.Exists(P + "Brickwall1.prefab") && File.Exists(P + "Column1.prefab");

        static float R(float a, float b) => a + (float)_rng.NextDouble() * (b - a);
        static T Pick<T>(IList<T> list) => list[_rng.Next(list.Count)];
        static Vector3 Polar(float deg, float radius) => new Vector3(Mathf.Sin(deg * Mathf.Deg2Rad) * radius, 0f, Mathf.Cos(deg * Mathf.Deg2Rad) * radius);

        public static void Build(Transform parent, Light moon, Camera cam)
        {
            _rng = new System.Random(1337);  // same layout every time
            _root = parent;

            Mood(moon, cam);
            Ground();
            Walls();
            Columns();
            Braziers();
            Remains();
            Outside();
        }

        // ------------------------------------------------------------ atmosphere

        static void Mood(Light moon, Camera cam)
        {
            Color fog = new Color(0.12f, 0.045f, 0.04f);          // dark blood-red haze
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 12f;
            RenderSettings.fogEndDistance = 42f;
            RenderSettings.fogColor = fog;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.20f, 0.15f, 0.20f);
            RenderSettings.ambientEquatorColor = new Color(0.20f, 0.09f, 0.07f);
            RenderSettings.ambientGroundColor = new Color(0.05f, 0.03f, 0.03f);
            RenderSettings.skybox = null;
            if (cam != null) cam.backgroundColor = fog;

            // cold moonlight from behind the ruins
            moon.color = new Color(0.58f, 0.64f, 0.88f);
            moon.intensity = 0.6f;
            moon.shadows = LightShadows.Soft;
            moon.shadowStrength = 0.85f;
            moon.transform.rotation = Quaternion.Euler(38f, 155f, 0f);

            // faint red glow over the fighting circle
            var glow = new GameObject("ArenaGlow").AddComponent<Light>();
            glow.transform.SetParent(_root, false);
            glow.transform.position = new Vector3(0f, 7f, 0f);
            glow.type = LightType.Point;
            glow.color = new Color(0.85f, 0.12f, 0.08f);
            glow.intensity = 1.1f;
            glow.range = 16f;
            glow.shadows = LightShadows.None;
            glow.renderMode = LightRenderMode.ForcePixel;

            QualitySettings.pixelLightCount = Mathf.Max(QualitySettings.pixelLightCount, 6);
        }

        // ------------------------------------------------------------ ground

        static Material TexMat(string name, string texFile, Color tint, float tiling, float smooth = 0.08f)
        {
            string path = MatDir + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, path); }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TEX + texFile);
            m.mainTexture = tex;
            m.mainTextureScale = new Vector2(tiling, tiling);
            m.color = tint;
            m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static GameObject Prim(PrimitiveType t, string name, Vector3 pos, Vector3 scale, Material m)
        {
            var go = GameObject.CreatePrimitive(t);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(_root, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = m;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        static void Ground()
        {
            var earth = TexMat("Ruins_Earth", "Rocks.tif", new Color(0.28f, 0.22f, 0.20f), 14f);
            var paving = TexMat("Ruins_Paving", "Wall_stone.tif", new Color(0.62f, 0.55f, 0.52f), 7f, 0.12f);
            var rim = TexMat("Ruins_Rim", "Stone_elements.tif", new Color(0.45f, 0.40f, 0.38f), 10f);

            Prim(PrimitiveType.Plane, "Earth", new Vector3(0f, -0.05f, 0f), new Vector3(9f, 1f, 9f), earth);
            Prim(PrimitiveType.Cylinder, "ArenaRim", new Vector3(0f, -0.14f, 0f), new Vector3(25f, 0.15f, 25f), rim);
            Prim(PrimitiveType.Cylinder, "FightFloor", new Vector3(0f, -0.13f, 0f), new Vector3(21.5f, 0.15f, 21.5f), paving);

            // dirt, leaves and stains on the floor (flat decals)
            string[] decals = { "Ground_Decal1", "Ground_Decal2", "Ground_Decal3", "Ground_Decal_Leaves1", "Ground_Decal_Leaves2" };
            for (int i = 0; i < 14; i++)
            {
                var pos = Polar(R(0f, 360f), R(1.5f, 11.5f)) + Vector3.up * (0.03f + i * 0.001f);
                Spawn(Pick(decals), pos, R(0f, 360f), R(2f, 4f), byHeight: false, snap: false);
            }
            // a few loose floor slabs pushed up at the edge
            string[] floors = { "Floor1", "Floor2", "Floor3", "Floor4", "Floor5", "Floor6" };
            for (int i = 0; i < 8; i++)
                Spawn(Pick(floors), Polar(i * 45f + R(-12f, 12f), R(10.2f, 11.2f)), R(0f, 360f), R(1.6f, 2.4f), byHeight: false);
        }

        // ------------------------------------------------------------ walls

        static void Walls()
        {
            string[] walls = { "Brickwall1", "Brickwall2", "Brickwall3", "Brickwall4", "Wall_stone_grup2x", "Stone_wall1", "Wall_Stone_el1", "Wall_Stone_el2", "Wall_sf_grup2x" };
            string[] debris = { "Brickwall_debris1", "Brickwall_debris2", "Block1", "Block2", "Block3", "Block4" };
            string[] ivy = { "Ivy1", "Ivy2", "Ivy3", "Ivy4", "Ivy5" };

            int segments = 16;
            for (int i = 0; i < segments; i++)
            {
                float deg = i * 360f / segments + R(-4f, 4f);
                var pos = Polar(deg, WallRadius + R(-0.4f, 0.6f));
                // every few segments: a gap with rubble (broken wall)
                if (i % 5 == 2)
                {
                    for (int d = 0; d < 3; d++)
                        Spawn(Pick(debris), pos + Polar(R(0, 360), R(0.3f, 1.6f)), R(0f, 360f), R(0.6f, 1.3f));
                    continue;
                }
                var w = SpawnAlongRing(Pick(walls), pos, deg, R(2.4f, 3.6f), 5.2f);
                if (w != null && _rng.NextDouble() < 0.45)
                    Spawn(Pick(ivy), pos - pos.normalized * 0.35f + Vector3.up * 0.2f, deg + 180f, R(1.6f, 2.6f), snap: true);
                if (_rng.NextDouble() < 0.5)
                    Spawn(Pick(debris), Polar(deg + R(-5f, 5f), WallRadius - R(1.0f, 1.8f)), R(0f, 360f), R(0.35f, 0.8f));
            }
        }

        /// <summary>Puts a wall-like prefab on the ring with its long side along the ring.</summary>
        static GameObject SpawnAlongRing(string name, Vector3 pos, float deg, float height, float maxLength)
        {
            var go = Spawn(name, pos, 0f, height);
            if (go == null) return null;
            var b = WorldBounds(go);
            float len = Mathf.Max(b.size.x, b.size.z);
            if (len > maxLength) go.transform.localScale *= maxLength / len;   // keep huge pieces reasonable
            bool longX = b.size.x >= b.size.z;
            go.transform.rotation = Quaternion.Euler(0f, longX ? deg : deg + 90f, 0f);
            SnapToGround(go, 0f);
            return go;
        }

        // ------------------------------------------------------------ columns + banners

        static void Columns()
        {
            string[] whole = { "Column1", "Column2" };
            string[] broken = { "Column3A", "Column3B", "Column3C" };
            for (int i = 0; i < 8; i++)
            {
                float deg = i * 45f + 22.5f;
                var pos = Polar(deg, 11.6f);
                bool intact = i % 3 == 0;
                var col = Spawn(intact ? Pick(whole) : Pick(broken), pos, R(0f, 360f), intact ? R(5.5f, 6.5f) : R(2.2f, 3.8f));
                if (col != null && intact)
                    Spawn("Banner", pos - pos.normalized * 0.65f, deg + 180f, R(3.2f, 3.8f), snap: true);
                // fallen column pieces at the base of broken ones
                if (!intact && _rng.NextDouble() < 0.6)
                {
                    var piece = Spawn(Pick(broken), pos + Polar(deg + 90f, R(1.2f, 1.8f)), R(0f, 360f), R(1.2f, 1.8f));
                    if (piece != null) piece.transform.rotation = Quaternion.Euler(90f, R(0f, 360f), 0f);
                    if (piece != null) SnapToGround(piece, -0.1f);
                }
            }
        }

        // ------------------------------------------------------------ fire

        static void Braziers()
        {
            for (int i = 0; i < 6; i++)
            {
                float deg = i * 60f;
                var pos = Polar(deg, 10.9f);
                // stone pedestal
                var block = Spawn(i % 2 == 0 ? "Block1" : "Block3", pos, R(0f, 360f), 1.0f);
                float top = block != null ? WorldBounds(block).max.y : 0f;
                var firePos = new Vector3(pos.x, top, pos.z);
                var camp = Spawn("Campfire", firePos, R(0f, 360f), 0.9f, byHeight: false, staticFlag: false);
                if (camp == null) Spawn("Fire_and_Light", firePos, 0f, -1f, folder: FX, snap: false, staticFlag: false);
                if (i % 2 == 0) Spawn("Sparks", firePos + Vector3.up * 0.3f, 0f, -1f, folder: FX, snap: false, staticFlag: false);

                // our own controlled fire light (flickers at runtime)
                var lgo = new GameObject("BrazierLight" + i);
                lgo.transform.SetParent(_root, false);
                lgo.transform.position = firePos + Vector3.up * 0.9f;
                var l = lgo.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.45f, 0.15f);
                l.intensity = 2.4f;
                l.range = 9f;
                l.shadows = LightShadows.None;
                lgo.AddComponent<FireFlicker>();
            }
            // remove the extra lights shipped inside fire prefabs (we placed our own)
            foreach (var l in _root.GetComponentsInChildren<Light>(true))
                if (!l.name.StartsWith("BrazierLight") && l.name != "ArenaGlow") l.enabled = false;
        }

        // ------------------------------------------------------------ skulls, bones, junk

        static void Remains()
        {
            string[] bones = { "Bones_01", "Bones_02", "Bones_03", "Bones_04", "Bones_05" };
            for (int i = 0; i < 18; i++)
                Spawn(Pick(bones), Polar(R(0f, 360f), R(10.0f, 12.4f)), R(0f, 360f), R(0.5f, 1.1f), byHeight: false);

            string[] junk = { "Prop_Barrel", "Prop_Cargo1", "Prop_Cargo2", "Prop_Chest2", "Jar1B", "Jar2B", "Prop_shield", "Prop_Sword" };
            for (int i = 0; i < 9; i++)
                Spawn(Pick(junk), Polar(R(0f, 360f), R(10.4f, 12.2f)), R(0f, 360f), R(0.6f, 1.1f));

            string[] runes = { "Rune_01", "Rune_03", "Rune_05" };
            for (int i = 0; i < 4; i++)
                Spawn(Pick(runes), Polar(i * 90f + 45f, 12.4f), i * 90f + 225f, R(2.2f, 2.8f), staticFlag: false);
        }

        // ------------------------------------------------------------ outside the walls

        static void Outside()
        {
            string[] dead = { "Tree_Dead1", "Tree_Dead2", "Tree_Dead3" };
            for (int i = 0; i < 12; i++)
                Spawn(Pick(dead), Polar(i * 30f + R(-10f, 10f), R(16f, 24f)), R(0f, 360f), R(6.5f, 10f));

            string[] fallen = { "Tree_Fall1", "Tree_Fall2", "Tree_Fall3", "Tree_Fall4", "Tree_Fall5", "Trunk_01", "Trunk_02" };
            for (int i = 0; i < 7; i++)
                Spawn(Pick(fallen), Polar(R(0f, 360f), R(15f, 20f)), R(0f, 360f), R(3f, 6f), byHeight: false);

            string[] rocks = { "Rock Group 01", "Rock Group 02", "Rock_05", "Rock_07", "Rock_10", "Rock_14A", "Rock_15", "Rock_17A" };
            for (int i = 0; i < 16; i++)
                Spawn(Pick(rocks), Polar(R(0f, 360f), R(14.5f, 26f)), R(0f, 360f), R(1.2f, 3.5f));

            string[] thorns = { "Bush_thorned1", "Fern1", "Plant_05", "Plant_06" };
            for (int i = 0; i < 10; i++)
                Spawn(Pick(thorns), Polar(R(0f, 360f), R(14f, 19f)), R(0f, 360f), R(0.8f, 1.6f));

            // a ruined watchtower, a broken wagon, old fences and stairs going nowhere
            Spawn("Tower_Base2_3", Polar(205f, 19f), 30f, 6f);
            Spawn("Tower_Base1_3", Polar(70f, 20f), 160f, 5f);
            Spawn("Wagon_wagon", Polar(300f, 17f), 75f, 2.2f);
            Spawn("Fence_elements1", Polar(130f, 16.5f), 40f, 1.4f);
            Spawn("Fence_elements2", Polar(340f, 16f), 110f, 1.4f);
            Spawn("Stair_el3", Polar(0f, 15.5f), 0f, 2f);
            Spawn("Stair_el5", Polar(180f, 15.5f), 180f, 2f);
            Spawn("Lantern1", Polar(95f, 14.3f), 0f, 2.2f, staticFlag: false);
        }

        // ------------------------------------------------------------ helpers

        static GameObject Spawn(string name, Vector3 pos, float yaw, float size, bool byHeight = true, bool snap = true,
                                string folder = P, bool staticFlag = true)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(folder + name + ".prefab");
            if (asset == null) { Debug.LogWarning("ArvinCombat ruins: missing prefab " + name); return null; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, _root);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            // no physics needed (fighters don't use colliders) and no stray scripts
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

            if (size > 0f)
            {
                var b = WorldBounds(go);
                float s = byHeight ? b.size.y : Mathf.Max(b.size.x, b.size.z);
                if (s > 0.01f) go.transform.localScale *= size / s;
            }
            if (snap) SnapToGround(go, pos.y);

            if (staticFlag && go.GetComponentInChildren<ParticleSystem>(true) == null)
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                if (r is MeshRenderer) r.shadowCastingMode = ShadowCastingMode.On;
            return go;
        }

        static void SnapToGround(GameObject go, float groundY)
        {
            var b = WorldBounds(go);
            if (b.size == Vector3.zero) return;
            go.transform.position += Vector3.up * (groundY - b.min.y - 0.03f);
        }

        static Bounds WorldBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            Bounds b = new Bounds(go.transform.position, Vector3.zero);
            bool any = false;
            foreach (var r in rs)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }
    }
}
