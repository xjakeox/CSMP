# CSMP: Cooking Simulator Multiplayer

An online multiplayer mod for **Cooking Simulator** (Steam). One player hosts from their own save, friends join from the main menu, and everyone cooks in the same kitchen.

CSMP is a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin. Players connect through **Steam** (no VPN or port forwarding), or by IP address. Some network functionality is handled by [LiteNetLib](https://github.com/RevenantX/LiteNetLib) and is required for this project to run.

## Features

- **Join the host's world.** The host plays their own save. A joining player gets an exact copy of the host's kitchen: item positions, cut food, pan contents, doneness, money, doors and the time of day.
- **See each other.** Other players show up as a chef in their own color.
- **Shared items.** Everything you can pick up syncs, including plates, pans, tools, bottles and food.
- **Shared kitchen.** Doors, drawers, fridges, stove and oven knobs, taps, switches, machines and appliance wear and repairs stay in step on every screen.
- **Shared cooking.** Cooking doneness, cutting, pan and pot contents, seasoning, pouring, eggs, spills and cleaning (sponge and mop) all match across players.
- **Shared orders and money.** The host runs the customer orders, and all players serve the same list. Everyone shares one wallet, and the kitchen takes more orders at once with more players (the host can switch this off).
- **Shared time.** The host controls pausing, the kitchen timers and the day clock. Career end-of-day, once-per-day perks and bankruptcy follow the host.
- **Resync.** The host can reload the world for everyone from the pause menu if a client drifts out of sync.

Everything is host-authoritative: the host's game is the source of truth, and clients copy it.

## Requirements

- **Cooking Simulator**, on Steam. Every player needs their own copy, plus any DLC the host's kitchen needs. Otherwise the join is refused with the reason.
- **BepInEx 5, Windows x64**, installed in each player's game folder (see below). This mod targets BepInEx 5.x on .NET Framework 4.7.2. BepInEx 6 and x86 builds are not supported.
- **The same CSMP version on every PC.** A mismatch is refused on join. The version shows in the main menu's top right corner.
- **Steam running and online** on every PC, to connect through Steam (the default). Direct IP works without it. See "Connecting with friends" below.

## Install

### 1. Install BepInEx 5

1. Download BepInEx 5 from the official releases page: https://github.com/BepInEx/BepInEx/releases
2. Pick the **BepInEx 5** file for **Windows x64**, named like `BepInEx_win_x64_5.4.23.x.zip`. Any 5.4.23 build works (the mod is developed against 5.4.23.5). Do **not** use BepInEx 6 or an x86 build.
3. Find your game folder: in Steam, right-click Cooking Simulator, choose **Manage**, then **Browse local files**. It is usually `C:\Program Files (x86)\Steam\steamapps\common\CookingSimulator`.
4. Extract the zip **straight into the game folder**, so that `winhttp.dll`, `doorstop_config.ini` and the `BepInEx` folder sit next to `CookingSim.exe`.
5. Start the game once from Steam, get to the main menu, then quit. This creates `BepInEx\plugins`, `BepInEx\config` and `BepInEx\LogOutput.log`.

### 2. Install CSMP

1. Download `CookingSimMP.dll` from the releases page, or download the source code and compile it yourself.
2. Put `CookingSimMP.dll` and [`LiteNetLib.dll`](https://github.com/RevenantX/LiteNetLib) directly into `<game folder>\BepInEx\plugins`. Both files are needed.
3. If Windows blocked the downloaded files, right-click each DLL, open **Properties**, tick **Unblock** if it is shown, and click OK.
4. Start the game from Steam. The main menu's top right corner should say **Cooking Simulator Multiplayer (CSMP) Active V...**. Every player's version must match.

If the line is missing, BepInEx did not load the mod. Check that `winhttp.dll` is next to `CookingSim.exe` and that you used the x64 BepInEx 5 build, then look in `BepInEx\LogOutput.log`.

### Building from source

1. Close every copy of the game (a running game locks the DLLs).
2. Install BepInEx 5 as above, then open `CSMP.slnx` in Visual Studio and build. NuGet restores LiteNetLib.
3. The build copies `CookingSimMP.dll` and `LiteNetLib.dll` into `<GameDir>\BepInEx\plugins`. `GameDir` is set at the top of `CookingSimMP\CookingSimMP.csproj`; change it if your game is installed elsewhere.

## Connecting with friends

The F10 and F11 windows have two tabs. The one you pick is remembered.

### Steam (default, v0.12+)

Players connect through Steam's own network, so there is nothing to install or set up: no VPN, no port forwarding, and the host's IP address is never shared. The friend joins with the host's **Steam ID**, a 17-digit number starting with `7656`.

1. The host loads a kitchen, presses **F10**, stays on the **Steam** tab, clicks **Copy Steam ID** and sends it to the friend (Steam chat, Discord, ...). Then they click **Host**.
2. The friend presses **F11** in the main menu, stays on the **Steam** tab, clicks **Paste** (or presses Ctrl+V) and clicks **Connect**. A `steamcommunity.com/profiles/...` link works too.

**Steam friends can skip the ID.** While you host through Steam, friends see **Join Game** on you in their Steam friends list. It works even when their game is closed: Steam starts it, and it joins once the main menu is up. You can also click **Host and invite** in the F10 window, or press **F10** again while hosting, to send invites from the Steam overlay. A friend who is in a kitchen when they click Join Game or an invite joins once they are back in the main menu.

Both players need Steam running and online, and the game started from Steam. Invites need the Steam overlay turned on. The first connection can take a few seconds while Steam finds a route.

### Direct IP (LAN, Tailscale, port forwarding)

The original way is still available on the **Direct IP** tab. It uses **UDP port 9050** (changeable in the config), and the host's PC has to be reachable from the friend's PC:

- **Same network:** use the host's address marked "(same network)" in the F10 window.
- **A VPN such as Tailscale.** Both players install [Tailscale](https://tailscale.com/download) (the free plan is enough), and the host shares their PC with the friend. The host's address looks like `100.x.x.x`, marked "(Tailscale / VPN)".
- **Port forwarding.** The host forwards UDP port 9050 on their router, and friends use the host's public IP.

The first time the host clicks **Host** on this tab, Windows may ask to allow Cooking Simulator through the firewall. Tick both **Private** and **Public**.

## How to play

**Host**

1. Load a save or start a game, so you are standing in a kitchen. Hosting only works from inside a kitchen.
2. Press **F10**. On the **Steam** tab, click **Copy Steam ID** and send it to the friend. Click **Host**.
3. With **Direct IP** instead, send the friend one of the listed addresses.

**Join**

1. Friends / connecting clients stay in the main menu and press **F11**.
2. On the **Steam** tab, paste the host's Steam ID and click **Connect** (or press Enter). On **Direct IP**, type the host's address instead (`address:port` if the host changed the port).
3. You see "Downloading the host's world..." and then load into the host's kitchen. The host's game is paused until you are in. The Steam ID or address is remembered for next time.

### Keybinds

| Key | Action |
|---|---|
| F10 | Open the host window (from inside a kitchen). While hosting through Steam: invite friends |
| F11 | Open the join window (usually from the main menu) |
| F12 | Disconnect |

### While you play

- Only the host can open the pause menu. If a client presses **Esc**, both screens show a "XX is asking to pause" notice.
- The host's pause menu has a **RESYNC** button (while someone is connected) that reloads the world for every client, and a button under it to turn "more orders with more players" on or off.
- Client players cannot save while playing the host's world. Saving comes back once they load one of their own saves.
- Career progress is saved on the host.
- Achievements are shared across hosts and clients.

## Configuration

The config file is created on first run at `BepInEx\config\cookingsimmp.cfg`.

| Setting | Default | Meaning |
|---|---|---|
| `Connection` | `Steam` | `Steam` or `Direct`: the tab the F10 and F11 windows open on. The windows save your last choice. |
| `HostSteamId` | (empty) | The Steam ID the F11 window's Steam tab starts with. It saves whatever you joined last. |
| `HostAddress` | `127.0.0.1` | Direct IP only. The address the F11 window starts with. It saves whatever you joined last. |
| `Port` | `9050` | Direct IP only. The UDP port the host listens on and joiners connect to. |
| `MoreOrdersWithMorePlayers` | `true` | Host only. With more players the kitchen takes more orders at once. |

## Troubleshooting

- **No CSMP line in the main menu:** BepInEx is not loading. See the install section above.
- **"Different CSMP versions":** one PC has an old DLL. Install the same build on both.
- **Join refused for DLC:** the friend does not own the DLC the host's kitchen needs. Load a different save.
- **Can't connect through Steam:** make sure the host is in a kitchen and clicked Host on the Steam tab, that you pasted the host's Steam ID (not your own), and that Steam is online on both PCs. Both need the same version of CSMP (0.12 or newer.)
- **"Steam isn't running" or "Steam is offline":** start the game from Steam and go online in Steam, or use Direct IP.
- **Can't connect with Direct IP ("Nobody answered"):** make sure the host is in a kitchen and has clicked Host, that the address is right, that the VPN shows connected on both PCs, and that the firewall allows UDP 9050.
- **Logs:** `BepInEx\LogOutput.log` on each PC. A working connection shows `Received from peer 0: "hello from ..."`. Through Steam, the log also says whether the connection is direct or goes through Steam's relay.
- **Uninstall:** delete the two DLLs from `BepInEx\plugins` to remove CSMP, or delete `winhttp.dll` to turn all mods off.

## Notes

Only this mod's DLL is distributed. Game files and decompiled game code are never part of this repo and must not be sent to friends. Each player installs BepInEx themselves.

## AI Disclaimer
Yes, this was built using AI. No, (for the most part) I do not know what I am doing. I know minimal coding, enough to just get by, but couldn't have made this on my own. I made this for me, but figured some other people might find use in it.
