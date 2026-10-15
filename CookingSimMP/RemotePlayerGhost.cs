using UnityEngine;

namespace CookingSimMP
{
    // A placeholder avatar for another player: a capsule body plus a head cube with a "nose" showing where they look.
    // It has no colliders, so it can't block the local player or be clicked.
    internal sealed class RemotePlayerGhost
    {
        private const float BodyRadius = 0.3f;
        private const float HeadSize = 0.25f;
        private const float SmoothingSpeed = 15f;

        private static readonly Color GhostColor = new Color(0.2f, 0.6f, 1f);

        private readonly int _peerId;
        private GameObject _root;
        private Transform _body;
        private Transform _head;

        private PlayerState _target;
        private bool _hasTarget;
        private Vector3 _feet;
        private Vector3 _headPos;
        private float _yaw;
        private float _pitch;

        public RemotePlayerGhost(int peerId)
        {
            _peerId = peerId;
        }

        public void SetTarget(PlayerState state)
        {
            if (!_hasTarget)
            {
                _feet = state.Feet;
                _headPos = state.Head;
                _yaw = state.Yaw;
                _pitch = state.Pitch;
            }
            _target = state;
            _hasTarget = true;
        }

        public void Tick(float deltaTime, string localScene)
        {
            bool visible = _hasTarget && _target.Scene == localScene && Player.Me != null;
            if (!visible)
            {
                if (_root != null)
                    _root.SetActive(false);
                return;
            }

            // The ghost lives in the kitchen scene, so a scene change destroys it; rebuild it when needed.
            if (_root == null)
                Build();
            _root.SetActive(true);

            // Ease toward the latest snapshot so 20 updates per second still look smooth.
            float t = 1f - Mathf.Exp(-SmoothingSpeed * deltaTime);
            _feet = Vector3.Lerp(_feet, _target.Feet, t);
            _headPos = Vector3.Lerp(_headPos, _target.Head, t);
            _yaw = Mathf.LerpAngle(_yaw, _target.Yaw, t);
            _pitch = Mathf.LerpAngle(_pitch, _target.Pitch, t);

            // Body stretches from the feet to just under the head, so crouching makes it shorter.
            float bodyHeight = Mathf.Max(0.5f, _headPos.y - _feet.y - HeadSize * 0.5f);
            _body.position = new Vector3(_feet.x, _feet.y + bodyHeight * 0.5f, _feet.z);
            _body.rotation = Quaternion.Euler(0f, _yaw, 0f);
            // Unity's capsule is 2 units tall and 1 wide at scale 1.
            _body.localScale = new Vector3(BodyRadius * 2f, bodyHeight * 0.5f, BodyRadius * 2f);

            _head.position = _headPos;
            _head.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
            _root = null;
        }

        private void Build()
        {
            _root = new GameObject($"CSMP_Ghost_{_peerId}");
            _body = CreatePart(PrimitiveType.Capsule, "Body", _root.transform).transform;

            _head = CreatePart(PrimitiveType.Cube, "Head", _root.transform).transform;
            _head.localScale = Vector3.one * HeadSize;

            // Nose sticks out of the front of the head; its scale is relative to the head.
            Transform nose = CreatePart(PrimitiveType.Cube, "Nose", _head).transform;
            nose.localScale = new Vector3(0.3f, 0.3f, 1.2f);
            nose.localPosition = new Vector3(0f, 0f, 0.6f);
        }

        private static GameObject CreatePart(PrimitiveType type, string name, Transform parent)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.GetComponent<Renderer>().material.color = GhostColor;
            return part;
        }
    }
}
