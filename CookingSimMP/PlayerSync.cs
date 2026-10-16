using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CookingSimMP
{
    // Sends the local player's position 20 times per second and shows a ghost for each remote player.
    internal sealed class PlayerSync
    {
        private const float SendInterval = 0.05f;

        private readonly NetSession _session;
        private readonly ManualLogSource _log;
        private readonly Dictionary<int, RemotePlayerGhost> _ghosts = new Dictionary<int, RemotePlayerGhost>();
        private readonly Dictionary<int, int> _slots = new Dictionary<int, int>();   // Host: peer -> colour slot.
        private float _sendTimer;
        private GUIStyle _tagStyle;
        private GUIStyle _tagShadowStyle;

        public PlayerSync(NetSession session, ManualLogSource log)
        {
            _session = session;
            _log = log;
            _session.PlayerStateReceived += OnPlayerStateReceived;
            _session.PeerLeft += RemoveGhost;
            _session.Stopped += RemoveAllGhosts;
        }

        public void Tick(float deltaTime)
        {
            string scene = SceneManager.GetActiveScene().name;

            if (_session.IsConnected)
            {
                _sendTimer += deltaTime;
                if (_sendTimer >= SendInterval)
                {
                    _sendTimer = 0f;
                    if (TryCaptureLocalState(scene, out PlayerState state))
                        _session.SendPlayerState(state);
                }
            }

            foreach (RemotePlayerGhost ghost in _ghosts.Values)
                ghost.Tick(deltaTime, scene);
        }

        // Name tags (v0.11.3): each other player's Steam name, or "Player", drawn above their chef in their colour.
        // They're screen labels, so they show through walls and stay readable at any distance up to MaxTagDistance.
        private const float MaxTagDistance = 40f;

        public void OnGUI()
        {
            if (_ghosts.Count == 0 || Event.current.type != EventType.Repaint)
                return;
            FpsCamera fpsCamera = FpsCamera.Me;
            Camera camera = fpsCamera != null ? fpsCamera.normalCamera : null;
            if (camera == null)
                return;

            if (_tagStyle == null)
            {
                _tagStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
                _tagShadowStyle = new GUIStyle(_tagStyle);
                _tagShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            }

            foreach (KeyValuePair<int, RemotePlayerGhost> pair in _ghosts)
            {
                if (!pair.Value.TryGetTagPoint(out Vector3 point))
                    continue;
                Vector3 screen = camera.WorldToScreenPoint(point);
                if (screen.z <= 0f || screen.z > MaxTagDistance)
                    continue;

                // Slightly smaller further away, but never too small to read.
                int size = Mathf.RoundToInt(Mathf.Lerp(22f, 14f, screen.z / MaxTagDistance));
                _tagStyle.fontSize = size;
                _tagShadowStyle.fontSize = size;
                _tagStyle.normal.textColor = Color.Lerp(pair.Value.Accent, Color.white, 0.35f);

                string name = _session.PlayerNameOf(pair.Key);
                var rect = new Rect(screen.x - 150f, Screen.height - screen.y - 15f, 300f, 30f);
                GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), name, _tagShadowStyle);
                GUI.Label(rect, name, _tagStyle);
            }
        }

        private static bool TryCaptureLocalState(string scene, out PlayerState state)
        {
            state = default;
            // Player.Me and FpsCamera.Me only exist while a kitchen is loaded.
            Player player = Player.Me;
            FpsCamera fpsCamera = FpsCamera.Me;
            if (player == null || fpsCamera == null || fpsCamera.normalCamera == null)
                return false;

            Transform body = player.transform;
            Transform camera = fpsCamera.normalCamera.transform;
            CharacterController controller = player.m_CharacterController;

            // Same "bottom of the controller" formula Player.FixedUpdate uses for its ground check.
            Vector3 feet = controller != null
                ? body.position + controller.center - Vector3.up * (controller.height / 2f)
                : body.position;

            float pitch = camera.eulerAngles.x;
            if (pitch > 180f)
                pitch -= 360f;

            state = new PlayerState
            {
                Scene = scene,
                Feet = feet,
                Yaw = body.eulerAngles.y,
                Head = camera.position,
                Pitch = pitch,
            };
            return true;
        }

        private void OnPlayerStateReceived(int peerId, PlayerState state)
        {
            if (!_ghosts.TryGetValue(peerId, out RemotePlayerGhost ghost))
            {
                int slot = SlotFor(peerId);
                ghost = new RemotePlayerGhost(peerId, slot);
                _ghosts.Add(peerId, ghost);
                _log.LogInfo($"Receiving position updates from peer {peerId} (their scene: {state.Scene}); colour slot {slot}.");
            }
            ghost.SetTarget(state);
        }

        // Colour slot (v0.11.2): 0 is the host (red). A client only ever sees the host. The host gives each client the
        // lowest free slot from 1, so the first client is blue and the second green, in the order they joined.
        private int SlotFor(int peerId)
        {
            if (!_session.IsHost)
                return 0;
            if (_slots.TryGetValue(peerId, out int existing))
                return existing;
            int slot = 1;
            while (_slots.ContainsValue(slot))
                slot++;
            _slots[peerId] = slot;
            return slot;
        }

        private void RemoveGhost(int peerId)
        {
            _slots.Remove(peerId);
            if (_ghosts.TryGetValue(peerId, out RemotePlayerGhost ghost))
            {
                ghost.Destroy();
                _ghosts.Remove(peerId);
            }
        }

        private void RemoveAllGhosts()
        {
            foreach (RemotePlayerGhost ghost in _ghosts.Values)
                ghost.Destroy();
            _ghosts.Clear();
            _slots.Clear();
        }
    }
}
