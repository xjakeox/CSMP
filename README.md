# CSMP: Cooking Simulator Multiplayer

An online multiplayer mod for Cooking Simulator, built on BepInEx 5 and LiteNetLib.

When the mod has loaded, the main menu shows "Cooking Simulator Multiplayer (CSMP) Active V<version>" in the top right corner. If it is missing, BepInEx didn't load the mod; check `BepInEx\LogOutput.log`. The version goes up with every build.

Only this mod's DLL and LiteNetLib.dll are ever distributed. Game files and decompiled game code never go in this repo.

## Build

1. Close every copy of the game (a running game locks the DLLs).
2. Open `CSMP.slnx` in Visual Studio and build. NuGet downloads LiteNetLib automatically.
3. The build copies `CookingSimMP.dll` and `LiteNetLib.dll` into `<GameDir>\BepInEx\plugins`.

## Test the connection on one PC (milestone 1)

1. Start one copy from Steam and a second by running `CookingSim.exe` directly (needs `steam_appid.txt` with `641320` in the game folder). Both share one save folder; from v0.9 the joining copy never saves while it plays the host's world.
2. In copy A load a save or start a game, then press **F10**, check the addresses it lists, and click **Host**. Hosting only works from inside a kitchen. In copy B stay in the main menu, press **F11**, leave `127.0.0.1` in the box and click **Connect** (or press Enter). Copy B then loads copy A's kitchen (see "Test joining the host's world" below).
3. Both logs (`BepInEx\LogOutput.log` and `LogOutput.log.1`) should show `Received from peer 0: "hello from ..."`. Both copies need the same CSMP version; otherwise the join is refused with a message on both screens.
4. **F12** disconnects. The port and the last joined address live in `BepInEx\config\com.jake.cookingsimmp.cfg`.

To play with a friend, they type your Tailscale/VPN address (shown in your F10 window) into their F11 window. Over the plain internet they need your public IP, and UDP port 9050 must be forwarded on your router. Type `address:port` to use a different port.

## Test the ghost player (milestone 2)

1. Connect as above. The client loads into the host's kitchen by itself.
2. Each copy should show a blue capsule with a head cube where the other player stands. The cube's "nose" points where they look, and the capsule gets shorter when they crouch.
3. The log shows `Receiving position updates from peer 0` once updates start arriving.

## Test joining the host's world (v0.9)

The host plays its own save; the client joins from the main menu and gets an exact copy of the host's kitchen: every item where the host left it, cut food, pan contents, doneness, money, doors and the time of day.

1. In the host, load a career save, move a few things, cut a vegetable and put a pan with oil on a lit stove. Then press **F10** and click **Host**.
2. In the client's main menu, press **F11** and connect. The client shows "Downloading the host's world..." and then the game's loading screen. The host shows "<name> is joining" and its game is paused until the client is in.
3. In the client, everything matches the host: positions, the cut pieces, the oil in the pan, the money and the clock. The host shows "<name> joined" and unpauses.
4. The client log shows `[World] Loaded the host's world` and `Matched N of M host items, K by saved ID`; the host log shows `[World] Sent the world (… KB …)`.
5. Hold something in the host while the client joins. The client sees it in the host's hands after joining, not in its own.
6. In the host, load a different save (pause menu → Load). The client shows "The host is loading another kitchen" and then loads the new one by itself.
7. Press **F12** in the client to disconnect, then open its pause menu and click Save. It doesn't save: "Saving is off while playing the host's world" shows instead. Saving comes back once the client loads one of its own saves or goes back to the main menu. The end-of-day quicksave is skipped the same way.
8. Press **F10** in the main menu: the window says to load a kitchen first.

If the client doesn't own the DLC the host's kitchen needs, the join is refused on both screens with the reason. Problems show up as `[World]` warnings.

## Test customer orders (v0.9.2)

The host owns the customer orders: it draws them, runs their timers and cancels late ones. The client copies the host's list twice a second and draws none of its own.

1. A new order in the host shows in the client too, with the same dish and timer.
2. Serve an order in either copy. On the other copy the order disappears and the plate leaves the checkout counter. Money goes up once and fame matches.
3. A late order goes into overtime and is cancelled on both screens at the same time.
4. When the host's kitchen closes for the day, the client skips to cleaning too.
5. In a sandbox kitchen, the client can start orders from its own tablet too: it asks the host, which starts the order for both (v0.9.3).

## Test kitchen timers (v0.9.4)

The host owns the kitchen timers. Either player can set, cancel or silence one; only the host's copy decides when it rings.

1. Set a timer in either copy. The countdown popup and the "set" click show on both, and the popups count down together.
2. When it runs out, both copies ring at the same moment.
3. Silence it in either copy by picking it up. It stops ringing on both.

Problems show up as `[Timers]` warnings.

## Test breaking (v0.9.4)

1. Throw a plate (with food on it) or a bottle so it shatters. It breaks in the same spot on both screens, with the sound, and the food falls out on both.
2. Do it from both copies. A plate the other player is holding or throwing only breaks when it breaks on their screen.

Problems show up as `[Breaks]` warnings.

## Test food bought onto a held plate (v0.9.5)

1. Hold a plate and buy food from a shelf so it lands on the plate.
2. The other player sees the food on the plate in your hands, moving with it, not on the floor.

Problems show up as `[Orders]` warnings.

## Test held-object sync (milestone 3)

Everything you can pick up syncs while someone holds it: containers, loose food, knives and other tools, oil bottles, spice shakers. Cutting, pouring, seasoning and cooking have their own sections below.

1. Load a save in the host and host (F10). Join from the client's main menu (F11).
2. Once the client is in the kitchen, its log shows `Matched N of M host items, K by saved ID`. N should equal M or be very close; any type it couldn't match is listed below that line.
3. You can move things in the host before the client joins: the client gets them where the host left them.
4. Pick up a plate in one copy and walk around. It should follow in the other copy, then land where you put it.
5. Food on a carried plate moves with it.
6. Buying a plate, a tool or food creates a free copy for the other player; putting a plate back on its stack removes it on both.

## Test interactable sync (v0.5)

Doors, drawers and switches sync: fridge and cupboard doors, drawers, stove and oven knobs, oven doors, sink taps, light switches, microwave buttons, the pizza oven valve, Cooking Challenge oven knobs, machine push buttons (mixer, cutter, sauce machine) and fryer oil buttons. When the client joins, it copies the host's state for all of them.

1. Connect as above (the client joins from its main menu). Wait a few seconds after the client's kitchen appears.
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

1. Connect as above; the client loads into the host's kitchen.
2. In the host, press **Esc**. The client should freeze and show "Game paused by host". Food stops cooking on both.
3. Resume in the host. The client unfreezes. Doneness shouldn't jump backwards on the client.
4. In the client, press **Esc**. No pause menu opens; both screens show "<name> is asking to pause" for a few seconds. Pressing again within 5 seconds does nothing. Esc still closes other windows such as the recipe book.
5. Pause the host, then press F12 in the host. The client unfreezes and shows "Host disconnected".

## Test money sync (v0.7)

Both players share one wallet, kept by the host. Buying, dish payments, tips, repairs and recipe purchases all come out of (or go into) it, and each is counted once. Sandbox and the challenges don't use money, so test in career mode.

1. Connect as above (the client joins from its main menu). A few seconds after the client's kitchen appears, the client's money changes to the host's and its log shows `[Money] Sharing the host's wallet: ...`.
2. Buy a plate in the host. The money goes down by the plate's price on both screens, not twice.
3. Buy a plate, a tool and some food in the client. Again the money goes down once, on both screens.
4. Buy something in both copies at nearly the same moment. Both screens should end on the same amount.
5. Serve a dish in either copy. The payment and tip show up on both screens.
6. Put a plate back on its stack in one copy, then take a plate in the other. It's free there (v0.10).
7. Call a repair with the phone in either copy. The cost comes out of the shared wallet once.

## Test cutting and pan contents (v0.8)

Cutting food on one copy cuts it the same way on the other, and whatever goes into a pan or pot (oil, water, sauce, spices) shows up on both. Seasoning sprinkled on food is synced too; it has nothing to see, so check it in the hover info. The pouring stream itself only shows for the player pouring.

1. Connect as above (the client joins from its main menu). Wait a few seconds after the client's kitchen appears.
2. In the client, put a vegetable on a cutting board and cut it a few times. The host should show the same slices in the same places. Pick up a slice in the host and move it; it should follow in the client.
3. Do the same from the host side, and with a piece of meat.
4. Put some food in the automatic cutter and press its button in either copy. Both copies should end up with the same pieces. Only the copy that pressed the button actually cuts. Changing the cutter's mode with E lights the same mode light on both copies (v0.8.1).
5. Put a pan on a stove in the host. In the client, pour oil into it. The oil should appear in the host's pan a moment later. Add salt or pepper from either side.
6. Fill a pot with water in the host, turn on the stove and let it heat. The client's pot should stay at the same temperature. Pour some of it into another pot or pan from the client; both pots should change on both screens.
7. Salt a steak in one copy. Hovering over it in the other copy should show the same seasoning. Fry it in the oiled pan; its doneness should match on both.
8. Problems show up as `[Cut]` or `[Contents]` warnings.

How full bottles and shakers are, and whether a bottle's cap is on, sync too (v0.10). When both players pour into the same pot at once, the host adds both pours together, so neither is lost (v0.10). Known gaps: the hand blender and the pizza, sushi and Cooking Ninja cuts are untested.

## Test dirt and cleaning (v0.8)

Stains (spills, splashes, broken eggs) and cleaning them with the sponge or mop show up on both copies.

1. Connect as above.
2. In the client, spill some oil or water on a counter, or tilt a full pot until it spills. The same stain should appear in the host.
3. In the host, pick up the sponge and clean half of that stain. The client should see the same half cleaned. Then finish it from the client.
4. Spill something on the floor and clean it with the mop from the other copy. The other copy should see the mop head slide across the floor with the stick pointing up to the player's hand (v0.8.1), and can't grab the mop while it's in use.
5. Break an egg. It breaks on both copies by itself; from v0.10 its stain is painted once, so it's no darker than with the mod off. Cleaning it once clears it on both.
6. Problems show up as `[Clean]` warnings.

## Test grab conflicts (v0.8)

Something the other player is holding can't be taken out of their hands.

1. Connect as above; the client loads into the host's kitchen.
2. In the host, pick up a plate and hold it. In the client, click the same plate. Nothing should happen, and "<host name> is holding that" should show at the top of the client's screen.
3. In the client, carry a pan or tray with food in it (try a fish or a piece of meat). The food should ride along in the pan on the host's screen and stay there after it's put down (v0.8.2 fixed food with bones vanishing; move it around 5 or 6 times to be sure). In the host, try to take the food out of the pan. It should be blocked the same way.
4. Drop the plate in the host. The client can pick it up right away.
5. Both click the same plate at the same moment (count down out loud). The host should end up with it. If the client grabbed it too, the client's copy drops it straight away and shows "<host name> grabbed that first".


## v0.10 additions

- **RESYNC:** the host's pause menu has a RESYNC button in the top right corner while someone is connected. Every client reloads the host's world in place.
- **Connection errors** show in a window with the reason (and Try again from the main menu), not only in the log.
- **Breakables:** windows, shelves, the TV, lamps, lights, the fan and the power box break and get fixed on both screens.
- **Appliance wear and repairs:** the host runs wear and breakdowns, the client copies each appliance's condition, and a phone repair started by either player runs on both. Problems show up as `[Stands]` and `[Props]` warnings.
- **Day clock:** the client follows the host's time and phase. The client's TV skip buttons ask the host ("<name> is asking to skip ahead"). Problems show up as `[Clock]` warnings.
- **Serving:** the first player to check out an order gets it; the other sees "<name> is serving that order" and keeps the plate. A dish either player serves counts in both copies' recipe history and cooking points (so in the host's save), and both get the achievement progress.

v0.10.1 fixes: the TV's cracked screen syncs (the first knock cracks it, the second breaks it), broken pieces are rebuilt on the client after joining or RESYNC (the game doesn't save them), and bottle amounts match to the ml after a pour.

v0.10.2 fix: a bottle poured by one player no longer pours on the other player's copy too (which doubled what went into the pot). The other player still sees the stream.

See `v0.10-testing.md` for the full test plan.

| Key | Action |
|---|---|
| F10 | Open the host window from inside a kitchen (lists your addresses; Host or Cancel) |
| F11 | Open the join window, usually from the main menu (type the host's address; Connect or Cancel) |
| F12 | Disconnect |
| F9 | Client only: turn its own cooking off/on to test cooking sync |
| F8 | Write a numbered marker into both players' logs (for testing) |
