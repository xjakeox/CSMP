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

Only containers (plates, bowls, pots, pans) sync so far, and only while someone holds one.

1. Back up the save folder first: both copies share it.
2. Load the **same save** in both copies and don't move anything, then connect.
3. The client log shows `Matched N of M host containers`. N should be close to M.
4. Pick up a plate in one copy and walk around. It should follow in the other copy, then land where you put it.
5. Food on a carried plate moves with it.
6. Buying a plate from a stack creates a free copy for the other player; putting it back on the stack removes it on both.

## Keys

| Key | Action |
|---|---|
| F10 | Host on the configured port |
| F11 | Join the configured address and port |
| F12 | Disconnect |
