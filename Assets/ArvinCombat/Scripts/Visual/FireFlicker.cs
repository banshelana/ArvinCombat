using UnityEngine;

namespace ArvinCombat
{
    /// <summary>Makes a brazier light flicker like real fire (cheap, no shadows).</summary>
    [RequireComponent(typeof(Light))]
    public class FireFlicker : MonoBehaviour
    {
        public float baseIntensity = 2.4f;
        public float amount = 0.6f;
        public float speed = 9f;

        Light _light;
        float _seed;
        Vector3 _basePos;

        void Awake()
        {
            _light = GetComponent<Light>();
            baseIntensity = _light.intensity;
            _seed = Random.value * 100f;
            _basePos = transform.localPosition;
        }

        void Update()
        {
            float t = Time.time * speed + _seed;
            float n = Mathf.PerlinNoise(t, _seed) * 2f - 1f;
            _light.intensity = baseIntensity + n * amount;
            transform.localPosition = _basePos + new Vector3(Mathf.PerlinNoise(t * 0.5f, 3f) - 0.5f, Mathf.PerlinNoise(5f, t * 0.5f) - 0.5f, 0f) * 0.08f;
        }
    }
}
