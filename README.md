# CSMP: Cooking Simulator Multiplayer

An online multiplayer mod for Cooking Simulator, built on BepInEx 5 and LiteNetLib.

Only this mod's DLL and LiteNetLib.dll are ever distributed. Game files and decompiled game code never go in this repo.

## Build

1. Close every copy of the game (a running game locks the DLLs).
2. Open `CSMP.slnx` in Visual Studio and build. NuGet downloads LiteNetLib automatically.
3. The build copies `CookingSimMP.dll` and `LiteNetLib.dll` into `<GameDir>\BepInEx\plugins`.

## Test the connection on one PC (milestone 1)

1. Start one copy from Steam and a second by running `CookingSim.exe` directly (needs `steam_appid.txt` with `641320` in the game folder). Don't save in the second copy, since both share one save folder.
2. In copy A press **F10**, check the addresses it lists, and click **Host**. In copy B press **F11**, leave `127.0.0.1` in the box and click **Connect** (or press Enter).
3. Both logs (`BepInEx\LogOutput.log` and `LogOutput.log.1`) should show `Received from peer 0: "hello from ..."`.
4. **F12** disconnects. The port and the last joined address live in `BepInEx\config\com.jake.cookingsimmp.cfg`.

To play with a friend, they type your Tailscale/VPN address (shown in your F10 window) into their F11 window. Over the plain internet they need your public IP, and UDP port 9050 must be forwarded on your router. Type `address:port` to use a different port.

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

## Test pause sync (v0.6)

The host is in charge of game time. Clients can't open the pause menu while connected.

1. Connect and load into the kitchen in both copies.
2. In the host, press **Esc**. The client should freeze and show "Game paused by host". Food stops cooking on both.
3. Resume in the host. The client unfreezes. Doneness shouldn't jump backwards on the client.
4. In the client, press **Esc**. No pause menu opens; both screens show "<name> is asking to pause" for a few seconds. Pressing again within 5 seconds does nothing. Esc still closes other windows such as the recipe book.
5. Pause the host, then press F12 in the host. The client unfreezes and shows "Host disconnected".

## Test money sync (v0.7)

Both players share one wallet, kept by the host. Buying, dish payments, tips, repairs and recipe purchases all come out of (or go into) it, and each is counted once. Sandbox and the challenges don't use money, so test in career mode.

1. Load the same save in both copies and connect. A few seconds after the kitchen appears, the client's money changes to the host's and its log shows `[Money] Sharing the host's wallet: ...`.
2. Buy a plate in the host. The money goes down by the plate's price on both screens, not twice.
3. Buy a plate, a tool and some food in the client. Again the money goes down once, on both screens.
4. Buy something in both copies at nearly the same moment. Both screens should end on the same amount.
5. Serve a dish in either copy. The payment and tip show up on both screens.
6. Known gap: putting a plate back on its stack makes the next plate free only on that player's screen, so the other player still pays for theirs (TODO #5).

## Keys

| Key | Action |
|---|---|
| F10 | Open the host window (lists your addresses; Host or Cancel) |
| F11 | Open the join window (type the host's address; Connect or Cancel) |
| F12 | Disconnect |
| F9 | Client only: turn its own cooking off/on to test cooking sync |
