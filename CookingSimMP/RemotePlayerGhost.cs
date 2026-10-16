using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CookingSimMP
{
    // The avatar for another player: a low-poly chef built from Unity primitives, so no model files ship with the mod.
    // Each player gets their own neckerchief and hat band colour (host red, then blue, green; see SlotColors). If building the chef fails, it falls back to the
    // old blue capsule with a head cube. Nothing has colliders, so it can't block the local player or be clicked.
    internal sealed class RemotePlayerGhost
    {
        // Flip to false to always use the old capsule placeholder.
        private const bool UseChefModel = true;

        private const float SmoothingSpeed = 15f;

        // Old placeholder sizes.
        private const float BodyRadius = 0.3f;
        private const float HeadSize = 0.25f;
        private static readonly Color GhostColor = new Color(0.2f, 0.6f, 1f);

        // The chef's body is modelled with feet at y = 0 and the neck at y = 1, then stretched to the real
        // feet-to-neck height every frame, so crouching squashes it. Widths and depths are in metres.
        // RefBodyHeight is roughly a standing player's feet-to-neck height; it turns metres into body units.
        private const float RefBodyHeight = 1.45f;
        private const float ChefHeadRadius = 0.13f;
        private const float MinBodyHeight = 0.5f;
        // Name tag height above the head's centre; the top of the hat is about 0.34 m up.
        private const float NameTagHeight = 0.5f;

        // Walk animation: limbs swing faster with distance covered and wider with speed.
        private const float StridePerMetre = 4f;
        private const float FullSwingSpeed = 2.5f;
        private const float MaxSwingDegrees = 35f;

        private static readonly Color Skin = new Color(0.94f, 0.76f, 0.62f);
        private static readonly Color Jacket = new Color(0.95f, 0.95f, 0.93f);
        private static readonly Color Apron = new Color(0.86f, 0.86f, 0.84f);
        private static readonly Color Trousers = new Color(0.18f, 0.18f, 0.2f);
        private static readonly Color Shoes = new Color(0.08f, 0.08f, 0.08f);
        private static readonly Color Dark = new Color(0.05f, 0.05f, 0.05f);

        private readonly int _peerId;
        private readonly Color _accent;
        private GameObject _root;
        private bool _isChef;

        // Placeholder parts.
        private Transform _body;
        private Transform _head;

        // Chef parts; the head is shared with the placeholder field above.
        private Transform _leftLeg;
        private Transform _rightLeg;
        private Transform _leftArm;
        private Transform _rightArm;

        // Materials are shared by every part of one colour and outlive scene changes, so they're freed in Destroy.
        private readonly Dictionary<Color, Material> _materials = new Dictionary<Color, Material>();

        private PlayerState _target;
        private bool _hasTarget;
        private Vector3 _feet;
        private Vector3 _headPos;
        private float _yaw;
        private float _pitch;
        private float _walkPhase;
        private float _swing;

        // Player colours by slot (v0.11.2): the host is always red, then clients in the order they joined: blue, green,
        // then a few more for bigger sessions.
        private static readonly Color[] SlotColors =
        {
            new Color(0.85f, 0.15f, 0.15f),   // Host: red.
            new Color(0.15f, 0.4f, 0.9f),     // First client: blue.
            new Color(0.2f, 0.7f, 0.25f),     // Second client: green.
            new Color(0.95f, 0.75f, 0.1f),    // Yellow.
            new Color(0.6f, 0.25f, 0.8f),     // Purple.
            new Color(0.95f, 0.5f, 0.1f),     // Orange.
        };

        public RemotePlayerGhost(int peerId, int slot)
        {
            _peerId = peerId;
            _accent = SlotColors[Math.Max(0, slot) % SlotColors.Length];
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
            Vector3 previousFeet = _feet;
            _feet = Vector3.Lerp(_feet, _target.Feet, t);
            _headPos = Vector3.Lerp(_headPos, _target.Head, t);
            _yaw = Mathf.LerpAngle(_yaw, _target.Yaw, t);
            _pitch = Mathf.LerpAngle(_pitch, _target.Pitch, t);

            if (_isChef)
                PoseChef(deltaTime, t, previousFeet);
            else
                PoseBlob();
        }

        // Colour of this player's neckerchief and hat stripe; their name tag uses it too.
        public Color Accent => _accent;

        // Where the name tag goes (v0.11.3): just above the hat. False while the chef isn't shown.
        public bool TryGetTagPoint(out Vector3 point)
        {
            point = _headPos + Vector3.up * NameTagHeight;
            return _root != null && _root.activeSelf;
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
            _root = null;
            foreach (Material material in _materials.Values)
            {
                if (material != null)
                    Object.Destroy(material);
            }
            _materials.Clear();
        }

        private void Build()
        {
            if (UseChefModel)
            {
                try
                {
                    BuildChef();
                    _isChef = true;
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CSMP] Couldn't build the chef model, using the placeholder instead: {e}");
                    if (_root != null)
                        Object.Destroy(_root);
                }
            }
            BuildBlob();
            _isChef = false;
        }

        private void PoseBlob()
        {
            // Body stretches from the feet to just under the head, so crouching makes it shorter.
            float bodyHeight = Mathf.Max(MinBodyHeight, _headPos.y - _feet.y - HeadSize * 0.5f);
            _body.position = new Vector3(_feet.x, _feet.y + bodyHeight * 0.5f, _feet.z);
            _body.rotation = Quaternion.Euler(0f, _yaw, 0f);
            // Unity's capsule is 2 units tall and 1 wide at scale 1.
            _body.localScale = new Vector3(BodyRadius * 2f, bodyHeight * 0.5f, BodyRadius * 2f);

            _head.position = _headPos;
            _head.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void PoseChef(float deltaTime, float t, Vector3 previousFeet)
        {
            // The neck sits just under the head sphere; a little overlap hides the seam.
            float bodyHeight = Mathf.Max(MinBodyHeight, _headPos.y - _feet.y - ChefHeadRadius + 0.03f);
            _body.position = _feet;
            _body.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _body.localScale = new Vector3(1f, bodyHeight, 1f);

            _head.position = _headPos;
            _head.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            // Swing arms and legs in opposite pairs while moving, and settle them when standing still.
            Vector3 step = _feet - previousFeet;
            step.y = 0f;
            float distance = step.magnitude;
            float speed = deltaTime > 0f ? distance / deltaTime : 0f;
            _walkPhase = (_walkPhase + distance * StridePerMetre * Mathf.PI) % (Mathf.PI * 2f);
            _swing = Mathf.Lerp(_swing, Mathf.Clamp01(speed / FullSwingSpeed) * MaxSwingDegrees, t);

            float angle = Mathf.Sin(_walkPhase) * _swing;
            _leftLeg.localRotation = Quaternion.Euler(angle, 0f, 0f);
            _rightLeg.localRotation = Quaternion.Euler(-angle, 0f, 0f);
            _leftArm.localRotation = Quaternion.Euler(-angle, 0f, 4f);
            _rightArm.localRotation = Quaternion.Euler(angle, 0f, -4f);
        }

        private void BuildBlob()
        {
            _root = new GameObject($"CSMP_Ghost_{_peerId}");
            _body = CreatePart(PrimitiveType.Capsule, "Body", _root.transform, GhostColor).transform;

            _head = CreatePart(PrimitiveType.Cube, "Head", _root.transform, GhostColor).transform;
            _head.localScale = Vector3.one * HeadSize;

            // Nose sticks out of the front of the head; its scale is relative to the head.
            Transform nose = CreatePart(PrimitiveType.Cube, "Nose", _head, GhostColor).transform;
            nose.localScale = new Vector3(0.3f, 0.3f, 1.2f);
            nose.localPosition = new Vector3(0f, 0f, 0.6f);
        }

        private void BuildChef()
        {
            _root = new GameObject($"CSMP_Ghost_{_peerId}");

            // Body: everything from the feet to the neck, in body units vertically (see RefBodyHeight).
            _body = new GameObject("Body").transform;
            _body.SetParent(_root.transform, false);

            _leftLeg = BuildLeg("LeftLeg", -0.09f);
            _rightLeg = BuildLeg("RightLeg", 0.09f);

            // Primitive capsules and cylinders are 2 units tall at scale 1, so their y scale is half their height.
            AddPart(PrimitiveType.Capsule, "Jacket", _body, Jacket, new Vector3(0f, 0.72f, 0f), new Vector3(0.40f, 0.30f, 0.26f));
            AddPart(PrimitiveType.Cube, "Apron", _body, Apron, new Vector3(0f, 0.52f, 0.13f), new Vector3(0.30f, 0.32f, 0.02f));
            AddPart(PrimitiveType.Cylinder, "Neckerchief", _body, _accent, new Vector3(0f, 0.98f, 0f), new Vector3(0.19f, 0.03f, 0.17f));
            for (int i = 0; i < 2; i++)
            {
                float y = 0.86f - i * 0.09f;
                AddPart(PrimitiveType.Sphere, "ButtonL", _body, Dark, new Vector3(-0.06f, y, 0.125f), Metres(0.03f, 0.03f, 0.03f));
                AddPart(PrimitiveType.Sphere, "ButtonR", _body, Dark, new Vector3(0.06f, y, 0.125f), Metres(0.03f, 0.03f, 0.03f));
            }

            _leftArm = BuildArm("LeftArm", -0.24f);
            _rightArm = BuildArm("RightArm", 0.24f);

            // Head: placed at the camera and turned by yaw and pitch, so it's built in plain metres.
            _head = new GameObject("Head").transform;
            _head.SetParent(_root.transform, false);
            float d = ChefHeadRadius * 2f;
            AddPart(PrimitiveType.Sphere, "Face", _head, Skin, Vector3.zero, new Vector3(d, d * 1.05f, d));
            AddPart(PrimitiveType.Sphere, "Nose", _head, Skin, new Vector3(0f, -0.015f, 0.125f), Vector3.one * 0.05f);
            AddPart(PrimitiveType.Sphere, "LeftEye", _head, Dark, new Vector3(-0.045f, 0.03f, 0.11f), Vector3.one * 0.035f);
            AddPart(PrimitiveType.Sphere, "RightEye", _head, Dark, new Vector3(0.045f, 0.03f, 0.11f), Vector3.one * 0.035f);
            AddPart(PrimitiveType.Cylinder, "HatBand", _head, Jacket, new Vector3(0f, 0.12f, 0f), new Vector3(0.25f, 0.05f, 0.25f));
            AddPart(PrimitiveType.Cylinder, "HatStripe", _head, _accent, new Vector3(0f, 0.1f, 0f), new Vector3(0.255f, 0.012f, 0.255f));
            AddPart(PrimitiveType.Sphere, "HatPuff", _head, Jacket, new Vector3(0f, 0.24f, 0f), new Vector3(0.32f, 0.2f, 0.32f));
        }

        // A leg swings from the hip; the pivot is the hip and the leg hangs below it.
        private Transform BuildLeg(string name, float x)
        {
            Transform hip = new GameObject(name).transform;
            hip.SetParent(_body, false);
            hip.localPosition = new Vector3(x, 0.47f, 0f);
            AddPart(PrimitiveType.Capsule, "Trousers", hip, Trousers, new Vector3(0f, -0.235f, 0f), new Vector3(0.13f, 0.245f, 0.13f));
            AddPart(PrimitiveType.Cube, "Shoe", hip, Shoes, new Vector3(0f, -0.455f, 0.04f), new Vector3(0.12f, 0.03f, 0.22f));
            return hip;
        }

        // An arm swings from the shoulder; the sleeve is the jacket colour with a bare hand at the end.
        private Transform BuildArm(string name, float x)
        {
            Transform shoulder = new GameObject(name).transform;
            shoulder.SetParent(_body, false);
            shoulder.localPosition = new Vector3(x, 0.92f, 0f);
            AddPart(PrimitiveType.Capsule, "Sleeve", shoulder, Jacket, new Vector3(0f, -0.2f, 0f), new Vector3(0.11f, 0.2f, 0.11f));
            AddPart(PrimitiveType.Sphere, "Hand", shoulder, Skin, new Vector3(0f, -0.42f, 0f), Metres(0.09f, 0.09f, 0.09f));
            return shoulder;
        }

        // Converts a size in metres to the body's stretched space, so round parts like buttons stay round.
        private static Vector3 Metres(float x, float y, float z)
        {
            return new Vector3(x, y / RefBodyHeight, z);
        }

        private void AddPart(PrimitiveType type, string name, Transform parent, Color color, Vector3 position, Vector3 scale)
        {
            Transform part = CreatePart(type, name, parent, color).transform;
            part.localPosition = position;
            part.localScale = scale;
        }

        private GameObject CreatePart(PrimitiveType type, string name, Transform parent, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            Renderer renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = GetMaterial(color, renderer.sharedMaterial);
            return part;
        }

        // One material per colour, copied from the primitive's default material so it uses the game's shader setup.
        private Material GetMaterial(Color color, Material template)
        {
            // Unity can unload these after a scene change, which leaves a destroyed entry behind.
            if (_materials.TryGetValue(color, out Material material) && material != null)
                return material;
            material = new Material(template) { color = color };
            _materials[color] = material;
            return material;
        }
    }
}
