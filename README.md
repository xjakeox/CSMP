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

Everything you can pick up syncs while someone holds it: containers, loose food, knives and other tools, oil bottles, spice shakers. Cutting, pouring, seasoning and cooking have their own sections below.

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
4. Cut pieces cook in sync too (v0.8). If a piece has no network ID, the host logs a `[Cook]` warning when it starts cooking.

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

## Test cutting and pan contents (v0.8)

Cutting food on one copy cuts it the same way on the other, and whatever goes into a pan or pot (oil, water, sauce, spices) shows up on both. Seasoning sprinkled on food is synced too; it has nothing to see, so check it in the hover info. The pouring stream itself only shows for the player pouring.

1. Load the same save in both copies and connect. Wait a few seconds after the kitchen appears.
2. In the client, put a vegetable on a cutting board and cut it a few times. The host should show the same slices in the same places. Pick up a slice in the host and move it; it should follow in the client.
3. Do the same from the host side, and with a piece of meat.
4. Put some food in the automatic cutter and press its button in either copy. Both copies should end up with the same pieces. Only the copy that pressed the button actually cuts. Changing the cutter's mode with E lights the same mode light on both copies (v0.8.1).
5. Put a pan on a stove in the host. In the client, pour oil into it. The oil should appear in the host's pan a moment later. Add salt or pepper from either side.
6. Fill a pot with water in the host, turn on the stove and let it heat. The client's pot should stay at the same temperature. Pour some of it into another pot or pan from the client; both pots should change on both screens.
7. Salt a steak in one copy. Hovering over it in the other copy should show the same seasoning. Fry it in the oiled pan; its doneness should match on both.
8. Problems show up as `[Cut]` or `[Contents]` warnings.

Known gaps: the hand blender, how full bottles and shakers are, and the pizza, sushi and Cooking Ninja cuts are untested. If both players pour into the same pot at the same moment, one of the pours can be lost (TODO #4).

## Test dirt and cleaning (v0.8)

Stains (spills, splashes, broken eggs) and cleaning them with the sponge or mop show up on both copies.

1. Connect as above.
2. In the client, spill some oil or water on a counter, or tilt a full pot until it spills. The same stain should appear in the host.
3. In the host, pick up the sponge and clean half of that stain. The client should see the same half cleaned. Then finish it from the client.
4. Spill something on the floor and clean it with the mop from the other copy. The other copy should see the mop head slide across the floor with the stick pointing up to the player's hand (v0.8.1), and can't grab the mop while it's in use.
5. Break an egg. It breaks on both copies by itself, so its stain gets painted twice and may look a little darker. Cleaning it once should still clear it on both.
6. Problems show up as `[Clean]` warnings.

## Test grab conflicts (v0.8)

Something the other player is holding can't be taken out of their hands.

1. Connect and load into the kitchen in both copies.
2. In the host, pick up a plate and hold it. In the client, click the same plate. Nothing should happen, and "<host name> is holding that" should show at the top of the client's screen.
3. In the client, carry a pan or tray with food in it (try a fish or a piece of meat). The food should ride along in the pan on the host's screen and stay there after it's put down (v0.8.2 fixed food with bones vanishing; move it around 5 or 6 times to be sure). In the host, try to take the food out of the pan. It should be blocked the same way.
4. Drop the plate in the host. The client can pick it up right away.
5. Both click the same plate at the same moment (count down out loud). The host should end up with it. If the client grabbed it too, the client's copy drops it straight away and shows "<host name> grabbed that first".


| Key | Action |
|---|---|
| F10 | Open the host window (lists your addresses; Host or Cancel) |
| F11 | Open the join window (type the host's address; Connect or Cancel) |
| F12 | Disconnect |
| F9 | Client only: turn its own cooking off/on to test cooking sync |
| F8 | Write a numbered marker into both players' logs (for testing) |
