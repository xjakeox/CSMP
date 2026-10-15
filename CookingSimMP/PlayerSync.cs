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
        private float _sendTimer;

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
                ghost = new RemotePlayerGhost(peerId);
                _ghosts.Add(peerId, ghost);
                _log.LogInfo($"Receiving position updates from peer {peerId} (their scene: {state.Scene}).");
            }
            ghost.SetTarget(state);
        }

        private void RemoveGhost(int peerId)
        {
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
        }
    }
}
