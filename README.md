# Glurp Online

Online multiplayer for **Drink More Glurp**. Host a game and play with friends over Steam, or connect directly over your network. Everything lives inside the game's own menus.

> Unofficial fan mod. Not affiliated with or endorsed by the developers of Drink More Glurp.

## Features

- **ONLINE** on the main menu: host a game, browse games (friends' games first), or use Direct Connect
- **Steam invites**: invite friends from the lobby, or join from their profile with *Join Game*
- **Direct Connect**: games on your local network show up automatically, and you can also type an address (port 27420)
- **Two ways to play**, picked under **Game Settings** in the lobby
  - **Take turns**: everyone watches each turn live as it happens
  - **All at once**: everyone plays the same event at the same moment, with no waiting on a ready screen
    - **See each other**: the other players appear in your level, with their hat, their sponsor and a see-through copy of their throwing object
    - **Shared objects**: *None*, *Props* (boxes, balls and other loose objects are shared) or *Everything* (targets and pickups too, and players appear solid)
    - **Players collide**: with everything shared, players bump into and grab each other (your own throwing object stays yours alone)
- **Voice chat**: talk to everyone in the lobby; while playing a round together you hear each other louder the closer you are. A dot in a player's colour beside their name shows who's talking, and the host can turn it off under **Game Settings**
- Up to 16 players, with several players per PC
- **Updates itself**: checks for new releases when the game starts, and passes updates between friends when you join each other

## Install

1. Download **GlurpOnline-x.y.z.zip** from [Releases](../../releases/latest).
2. In Steam, right-click **Drink More Glurp** → **Manage** → **Browse local files**.
3. Extract the zip into that folder. `winhttp.dll` should end up next to `Drink More Glurp.exe`.
4. Start the game. The **ONLINE** card appears on the main menu.

Already using BepInEx 5? Just put `GlurpOnline.dll` into `BepInEx/plugins/GlurpOnline/`.

Everyone you play with needs Glurp Online too. If your versions differ, the game that's behind is offered the newer version when you join each other.

## Updates

- When the game starts, Glurp Online checks this page for a newer release and asks before updating.
- If a friend has a newer version, you're offered it as soon as you try to join them. The game restarts and puts you back in their lobby.
- Every release is digitally signed. Only signed official builds are ever installed, wherever they come from.

You can turn either kind of update off in `BepInEx/config/com.glurponline.mod.cfg` under `[Updates]`.

## Uninstall

Delete the `BepInEx/plugins/GlurpOnline` folder. To remove BepInEx as well, also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and the `BepInEx` folder.

## Building from source

Requirements: the .NET SDK and Drink More Glurp with BepInEx 5 installed.

```
dotnet build -c Release
```

The build copies the DLL into the game's `BepInEx/plugins/GlurpOnline` folder. If the game isn't in the default Steam location, pass `-p:GameDir="path\to\Drink More Glurp"`. Add `-p:Deploy=false` to skip the copy.

Builds you make yourself are unsigned. They play normally and can receive updates, but they won't hand updates to other players.

## Credits

The release zip includes [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23 (LGPL-2.1), which loads the mod.
