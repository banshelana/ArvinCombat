using UnityEngine;

namespace ArvinCombat
{
    /// <summary>Lock-on camera: sits behind the player, looks at the middle of both fighters.</summary>
    public class FightCamera : MonoBehaviour
    {
        public Fighter player;
        public Fighter target;
        public float distance = 4.2f;
        public float height = 2.1f;
        public float sideOffset = 0.9f;
        public float smooth = 6f;
        public float zoomPerMeter = 0.35f;

        float _shake;
        Vector3 _vel;
        float _orbit; // small manual orbit with the mouse X (optional)

        public void Shake(float amount) { _shake = Mathf.Max(_shake, amount); }

        public void Snap()
        {
            if (player == null) return;
            transform.position = DesiredPos();
            transform.rotation = Quaternion.LookRotation(LookPoint() - transform.position);
        }

        Vector3 LookPoint()
        {
            Vector3 a = player.transform.position;
            Vector3 b = target != null ? target.transform.position : a + player.transform.forward * 3f;
            return Vector3.Lerp(a, b, 0.45f) + Vector3.up * 1.2f;
        }

        Vector3 DesiredPos()
        {
            Vector3 a = player.transform.position;
            Vector3 b = target != null ? target.transform.position : a + player.transform.forward * 3f;
            Vector3 dir = b - a; dir.y = 0f;
            float dist = dir.magnitude;
            dir = dist > 0.1f ? dir / dist : player.transform.forward;
            dir = Quaternion.Euler(0f, _orbit, 0f) * dir;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            float back = distance + Mathf.Max(0f, dist - 2f) * zoomPerMeter;
            return a - dir * back + Vector3.up * (height + Mathf.Max(0f, dist - 2f) * 0.12f) + right * sideOffset;
        }

        void LateUpdate()
        {
            if (player == null) return;
            if (Input.GetMouseButton(2)) _orbit += Input.GetAxis("Mouse X") * 3f;
            _orbit = Mathf.Lerp(_orbit, 0f, Time.unscaledDeltaTime * 0.5f);

            Vector3 desired = DesiredPos();
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref _vel, 1f / smooth, Mathf.Infinity, Time.unscaledDeltaTime);
            var look = Quaternion.LookRotation(LookPoint() - transform.position);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.unscaledDeltaTime * smooth * 1.5f);

            if (_shake > 0f)
            {
                transform.position += Random.insideUnitSphere * _shake * 0.15f;
                _shake = Mathf.MoveTowards(_shake, 0f, Time.unscaledDeltaTime * 3f);
            }
        }
    }
}
