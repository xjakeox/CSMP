using LiteNetLib.Utils;
using UnityEngine;

namespace CookingSimMP
{
    // One snapshot of where a player is and where they are looking.
    internal struct PlayerState
    {
        public string Scene;   // Only show a ghost when both players are in the same scene.
        public Vector3 Feet;   // Bottom of the player's CharacterController.
        public float Yaw;      // Body facing, in degrees.
        public Vector3 Head;   // Camera position; it drops when the player crouches.
        public float Pitch;    // Camera up/down angle, in degrees (negative = looking up).

        public void Write(NetDataWriter writer)
        {
            writer.Put(Scene ?? string.Empty);
            WriteVector(writer, Feet);
            writer.Put(Yaw);
            WriteVector(writer, Head);
            writer.Put(Pitch);
        }

        public static PlayerState Read(NetDataReader reader)
        {
            return new PlayerState
            {
                Scene = reader.GetString(128),
                Feet = ReadVector(reader),
                Yaw = reader.GetFloat(),
                Head = ReadVector(reader),
                Pitch = reader.GetFloat(),
            };
        }

        private static void WriteVector(NetDataWriter writer, Vector3 v)
        {
            writer.Put(v.x);
            writer.Put(v.y);
            writer.Put(v.z);
        }

        private static Vector3 ReadVector(NetDataReader reader)
        {
            return new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }
    }
}
