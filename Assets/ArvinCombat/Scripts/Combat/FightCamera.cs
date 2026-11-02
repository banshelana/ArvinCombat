using UnityEngine;

namespace ArvinCombat
{
    /// <summary>
    /// Third-person fight camera controlled by the mouse:
    ///   move the mouse = turn around your fighter (left / right) and look up / down, scroll = zoom.
    /// At every round start it snaps behind the player, facing the opponent.
    /// Mouse look works while the cursor is locked in the game (during fights).
    /// </summary>
    public class FightCamera : MonoBehaviour
    {
        public Fighter player;
        public Fighter target;

        [Header("Mouse")]
        public float sensitivity = 2.2f;
        public bool invertY;
        public float minPitch = -5f;
        public float maxPitch = 55f;

        [Header("Framing")]
        public float distance = 4.3f;
        public float minDistance = 2.5f;
        public float maxDistance = 8f;
        public float height = 1.45f;
        public float sideOffset = 0.55f;
        public float followSharpness = 18f;
        public float maxRadius = 11.8f;

        float _yaw;
        float _pitch = 14f;
        float _shake;
        Vector3 _pivot;

        public void Shake(float amount) { _shake = Mathf.Max(_shake, amount); }

        /// <summary>Behind the player, looking at the opponent.</summary>
        public void Snap()
        {
            if (player == null) return;
            Vector3 dir = target != null ? target.transform.position - player.transform.position : player.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = player.transform.forward;
            _yaw = Quaternion.LookRotation(dir).eulerAngles.y;
            _pitch = 14f;
            _pivot = Pivot();
            Place(1f);
        }

        Vector3 Pivot() => player.transform.position + Vector3.up * height;

        void LateUpdate()
        {
            if (player == null) return;

            // mouse look only while the cursor is captured by the game (not over menus)
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                _yaw += Input.GetAxis("Mouse X") * sensitivity;
                _pitch += Input.GetAxis("Mouse Y") * sensitivity * (invertY ? 1f : -1f);
                _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
                float scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.01f) distance = Mathf.Clamp(distance - scroll * 0.6f, minDistance, maxDistance);
            }

            float k = 1f - Mathf.Exp(-followSharpness * Time.unscaledDeltaTime);
            _pivot = Vector3.Lerp(_pivot, Pivot(), k);
            Place(1f);

            if (_shake > 0f)
            {
                transform.position += Random.insideUnitSphere * _shake * 0.12f;
                _shake = Mathf.MoveTowards(_shake, 0f, Time.unscaledDeltaTime * 3f);
            }
        }

        void Place(float t)
        {
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 right = rot * Vector3.right;
            Vector3 look = _pivot + right * sideOffset;
            Vector3 pos = look - rot * Vector3.forward * distance;
            // stay above the floor and inside the ring of ruins (no clipping into walls)
            if (pos.y < 0.3f) pos.y = 0.3f;
            var flat = new Vector2(pos.x, pos.z);
            if (flat.magnitude > maxRadius) { flat = flat.normalized * maxRadius; pos.x = flat.x; pos.z = flat.y; }
            transform.position = pos;
            transform.rotation = Quaternion.LookRotation(look - pos, Vector3.up);
        }
    }
}
