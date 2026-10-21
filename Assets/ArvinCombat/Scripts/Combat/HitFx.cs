using System.Collections.Generic;
using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Tiny, dependency-free hit effects (no textures, no particle assets → nothing extra to download).
    /// A burst of small flying cubes + a flash sphere.
    /// </summary>
    public class HitFx : MonoBehaviour
    {
        class Bit { public Transform t; public Vector3 v; public float life; public float maxLife; public Vector3 baseScale; }

        readonly List<Bit> _active = new List<Bit>();
        readonly Stack<Transform> _poolCubes = new Stack<Transform>();
        readonly Stack<Transform> _poolFlash = new Stack<Transform>();
        Material _blood, _spark, _flash;

        void Awake()
        {
            var sh = Shader.Find("Standard");
            _blood = new Material(sh) { color = new Color(0.6f, 0.02f, 0.02f) };
            _spark = new Material(sh) { color = new Color(1f, 0.85f, 0.3f) };
            _spark.EnableKeyword("_EMISSION");
            _spark.SetColor("_EmissionColor", new Color(1f, 0.7f, 0.2f) * 2f);
            _flash = new Material(sh) { color = Color.white };
            _flash.EnableKeyword("_EMISSION");
            _flash.SetColor("_EmissionColor", Color.white * 1.5f);
        }

        Transform Get(Stack<Transform> pool, PrimitiveType type)
        {
            Transform t;
            if (pool.Count > 0) { t = pool.Pop(); t.gameObject.SetActive(true); return t; }
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            var r = go.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        public void Spawn(Vector3 pos, bool blocked, float strength)
        {
            int count = blocked ? 6 : Mathf.Clamp(Mathf.RoundToInt(6 + strength * 0.08f), 6, 18);
            for (int i = 0; i < count; i++)
            {
                var t = Get(_poolCubes, PrimitiveType.Cube);
                t.GetComponent<Renderer>().sharedMaterial = blocked ? _spark : _blood;
                t.position = pos + Random.insideUnitSphere * 0.1f;
                t.rotation = Random.rotation;
                float s = Random.Range(0.03f, 0.07f);
                var b = new Bit
                {
                    t = t, v = (Random.insideUnitSphere + Vector3.up * 0.8f) * Random.Range(2f, 4.5f),
                    life = 0f, maxLife = Random.Range(0.3f, 0.6f), baseScale = Vector3.one * s
                };
                t.localScale = b.baseScale;
                _active.Add(b);
            }

            var f = Get(_poolFlash, PrimitiveType.Sphere);
            f.GetComponent<Renderer>().sharedMaterial = blocked ? _spark : _flash;
            f.position = pos;
            _active.Add(new Bit { t = f, v = Vector3.zero, life = 0f, maxLife = 0.12f, baseScale = Vector3.one * (blocked ? 0.25f : 0.4f) });
        }

        void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var b = _active[i];
                b.life += dt;
                float k = b.life / b.maxLife;
                if (k >= 1f)
                {
                    b.t.gameObject.SetActive(false);
                    if (b.v == Vector3.zero) _poolFlash.Push(b.t); else _poolCubes.Push(b.t);
                    _active.RemoveAt(i);
                    continue;
                }
                if (b.v != Vector3.zero)
                {
                    b.v += Vector3.down * 9.8f * dt;
                    b.t.position += b.v * dt;
                    b.t.localScale = b.baseScale * (1f - k);
                }
                else b.t.localScale = b.baseScale * (0.5f + k);
            }
        }
    }
}
