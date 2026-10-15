# CSMP: Cooking Simulator Multiplayer

An online multiplayer mod for Cooking Simulator, built on BepInEx 5 and LiteNetLib.

Only this mod's DLL and LiteNetLib.dll are ever distributed. Game files and decompiled game code never go in this repo.

## Build

1. Close every copy of the game (a running game locks the DLLs).
2. Open `CSMP.slnx` in Visual Studio and build. NuGet downloads LiteNetLib automatically.
3. The build copies `CookingSimMP.dll` and `LiteNetLib.dll` into `<GameDir>\BepInEx\plugins`.

## Test the connection on one PC (milestone 1)

1. Start one copy from Steam and a second by running `CookingSim.exe` directly (needs `steam_appid.txt` with `641320` in the game folder). Don't save in the second copy, since both share one save folder.
2. In copy A press **F10** to host. In copy B press **F11** to join `127.0.0.1:9050`.
3. Both logs (`BepInEx\LogOutput.log` and `LogOutput.log.1`) should show `Received from peer 0: "hello from ..."`.
4. **F12** disconnects. The address and port live in `BepInEx\config\com.jake.cookingsimmp.cfg`.

## Test the ghost player (milestone 2)

1. Connect as above, then load into the same kitchen in both copies.
2. Each copy should show a blue capsule with a head cube where the other player stands. The cube's "nose" points where they look, and the capsule gets shorter when they crouch.
3. The log shows `Receiving position updates from peer 0` once updates start arriving.

## Test held-object sync (milestone 3)

Everything you can pick up syncs while someone holds it: containers, loose food, knives and other tools, oil bottles, spice shakers. What happens *to* items (cutting, pouring, seasoning, cooking) doesn't sync yet.

1. Back up the save folder first: both copies share it.
2. Load the **same save** in both copies and don't move anything, then connect.
3. The client log shows `Matched N of M host items`. N should be close to M; any type it couldn't match is listed below that line.
4. Pick up a plate in one copy and walk around. It should follow in the other copy, then land where you put it.
5. Food on a carried plate moves with it.
6. Buying a plate, a tool or food creates a free copy for the other player; putting a plate back on its stack removes it on both.

## Test interactable sync (v0.5)

Doors, drawers and switches sync: fridge and cupboard doors, drawers, stove and oven knobs, oven doors, sink taps, light switches, microwave buttons, the pizza oven valve, Cooking Challenge oven knobs, machine push buttons (mixer, cutter, sauce machine) and fryer oil buttons. When the client joins, it copies the host's state for all of them.

1. Load the same save in both copies and connect as above. Wait a few seconds after the kitchen appears.
2. The client log shows `[Interact] Snapshot done: ...`. "missing here" and "only here" should both be 0.
3. Open a fridge door, turn on a stove, switch on the oven and run a tap in one copy. The other copy should do the same. Problems show up as `[Interact]` warnings.

## Test cooking sync (v0.5.1)

The host is in charge of how cooked food is. Both copies still cook on their own, and the host sends the doneness of anything that changed four times a second, which the client copies.

1. Connect, turn on a stove or the grill in either copy and put a steak on it.
2. The steak should cook at the same rate on both copies. Problems show up as `[Cook]` warnings.
3. To prove the client really takes the host's values, press **F9** in the client. That stops the client's own cooking. Food on the client should keep cooking at the same rate, now only from the host's updates. Press F9 again to turn it back on.
4. Cut pieces aren't synced yet (TODO #1); the host logs a warning when one is cooking.

## Keys

| Key | Action |
|---|---|
| F10 | Host on the configured port |
| F11 | Join the configured address and port |
| F12 | Disconnect |
| F9 | Client only: turn its own cooking off/on to test cooking sync |
