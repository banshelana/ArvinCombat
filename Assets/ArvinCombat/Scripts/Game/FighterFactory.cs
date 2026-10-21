using UnityEngine;

namespace ArvinCombat
{
    /// <summary>Creates a fighter (root + generated warrior model + Fighter components).</summary>
    public static class FighterFactory
    {
        public static Fighter Create(WarriorDef def, RuntimeStats stats, EnemyTrait[] traits, string displayName,
            bool isPlayer, IFighterBrain brain, Vector3 pos, Quaternion rot, bool remote = false)
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

            var fa = root.AddComponent<FighterAnimator>();
            fa.Init(model.GetComponentInChildren<Animator>(), vis);

            var f = root.AddComponent<Fighter>();
            f.isRemote = remote;
            f.Setup(def, stats, traits, displayName, isPlayer, brain, fa);
            return f;
        }
    }
}
