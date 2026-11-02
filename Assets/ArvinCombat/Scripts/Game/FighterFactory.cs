using UnityEngine;

namespace ArvinCombat
{
    /// <summary>Creates a fighter (root + generated warrior model + Fighter components).</summary>
    public static class FighterFactory
    {
        public static Fighter Create(WarriorDef def, RuntimeStats stats, EnemyTrait[] traits, string displayName,
            bool isPlayer, IFighterBrain brain, Vector3 pos, Quaternion rot, bool remote = false, string skin = "")
        {
            var root = new GameObject((isPlayer ? "Player_" : remote ? "Remote_" : "Enemy_") + def.displayName);
            root.transform.SetPositionAndRotation(pos, rot);

            var vis = WarriorRegistry.Instance != null ? WarriorRegistry.Instance.Get(def.id) : null;
            GameObject model;
            if (vis != null && vis.prefab != null)
            {
                model = Object.Instantiate(vis.prefab, root.transform);
                model.transform.localPosition = Vector3.zero;
            }
            else
            {
                Debug.LogWarning("ArvinCombat: no prefab for " + def.id + ". Run menu ArvinCombat > Setup Project. Using a capsule.");
                model = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.Destroy(model.GetComponent<Collider>());
                model.transform.SetParent(root.transform, false);
                model.transform.localPosition = Vector3.up;
            }
            model.transform.localRotation = Quaternion.identity;
            ApplySkin(model, skin);

            var fa = root.AddComponent<FighterAnimator>();
            fa.Init(model.GetComponentInChildren<Animator>(), vis);

            var f = root.AddComponent<Fighter>();
            f.isRemote = remote;
            f.Setup(def, stats, traits, displayName, isPlayer, brain, fa);
            return f;
        }

        /// <summary>
        /// Cosmetic skins from the shop: a tint over the warrior's own materials (no extra textures to download).
        /// gold = shiny gold armour look, shadow = dark smoky look.
        /// </summary>
        public static void ApplySkin(GameObject model, string skin)
        {
            if (model == null || string.IsNullOrEmpty(skin)) return;
            Color tint; float metal, gloss; Color emit;
            if (skin == "gold") { tint = new Color(1f, 0.78f, 0.32f); metal = 0.75f; gloss = 0.7f; emit = new Color(0.16f, 0.1f, 0.02f); }
            else if (skin == "shadow") { tint = new Color(0.22f, 0.2f, 0.3f); metal = 0.2f; gloss = 0.35f; emit = new Color(0.07f, 0.02f, 0.12f); }
            else return;

            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                var mats = r.materials; // instances: the shared warrior materials stay untouched
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    if (m.HasProperty("_Color"))
                    {
                        var c = m.color;
                        float lum = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                        var g = new Color(lum, lum, lum, c.a);
                        m.color = Color.Lerp(c, g * tint * 1.6f, skin == "gold" ? 0.75f : 0.85f);
                    }
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
                    if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emit); }
                }
                r.materials = mats;
            }
        }
    }
}
