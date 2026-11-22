# TrailFollow

A [Dungeon Helper](https://dungeonhelper.com/) plugin for multiboxing **Dungeons & Dragons Online**. Your alts follow the exact path your main character walked, and repeat routine actions (buffs, heals, doors, chests, dialog choices) so you can play one character while the others keep up.

It is meant as an alternative to the Awesome AF auto-follow plugin. Turn Awesome AF off while using TrailFollow.

*Made by Madmachine. 한국어 안내는 [README.ko.md](README.ko.md)를 보세요.*

> **Unofficial and unsupported.** This plugin is not made, approved or endorsed by Standing Stone Games or by the Dungeon Helper developers. Third-party tools may conflict with the game's Code of Conduct. Use it at your own risk, and stay at the keyboard while it runs.

## What it does

- **Trail following** – the main's path is recorded and the alts walk it, including jumps. When an alt gets stuck it tries to free itself. Two distances (Near / Far) can be switched with one button; Near can be set down to 0 m.
- **Main window** – one row of buttons that controls every alt: `Follow` `Near/Far` `− distance +` `Target` `AFK` `Attack` `Buff`.
- **Alt hotbar windows** – one hotbar of each alt is shown on the main screen as icons. Click an icon and that alt uses the slot.
- **Heal/buff timers** – use chosen hotbar slots at chosen intervals, optionally only while enemies are nearby and never in town. An emergency heal spams chosen slots when the leader or a party member drops below a health threshold.
- **Attack** – alts select the main's target and toggle auto-attack, and can face the target.
- **Mimic the leader** – mount, recall, teleport items, doors/entrances/portals, Ctrl+use on levers, chests and NPCs, the dungeon entrance dialog, and quest dialog options.
- **Chest loot** – alts assign their share of chest loot to the leader; anything that cannot be passed is looted by the alt.
- **Auto-accept** – party invites, quest shares, resurrection, airship invites and Greater Teleport from your own characters.

The interface is available in **English and Korean**. It follows the Windows display language; you can force one in the settings (first tab), then restart Dungeon Helper.

## Requirements

- Windows 10/11 (x64)
- Dungeons & Dragons Online with Dungeon Helper installed in its default location (`%AppData%\Dungeon Helper`)
- .NET 8 SDK, only to build the plugin (the install script offers to install it)

## Install / update

1. Download this repository (Code → Download ZIP). **Before extracting**, right-click the ZIP → Properties → tick **Unblock** → OK, then extract it. (See [Windows security warnings](#windows-security-warnings).)
2. Double-click `build_and_install.bat`.
   - If Dungeon Helper is running it is closed, the plugin is built and installed, and Dungeon Helper is started again.
   - If Dungeon Helper runs as administrator, Windows asks for permission to close it. Click Yes.
3. In Dungeon Helper, enable **TrailFollow** in the plugin list.

Manual install: build with `dotnet build TrailFollow.csproj -c Release -o out`, then copy `out\TrailFollow.dll` and `plugin.metadata` to `%AppData%\Dungeon Helper\plugins\TrailFollow`.

To remove the plugin, delete that `TrailFollow` folder.

### Updating from inside Dungeon Helper

Open the TrailFollow settings page, go to the **About** tab and press **Check for updates**. If a newer version has been published in the `dist` folder of this repository, **Update now** downloads it and replaces the plugin files. Restart Dungeon Helper to apply. Nothing is checked or installed unless you press the button.

## Windows security warnings

The plugin is not code-signed, so Windows may warn about it. This is expected.

- **Unblock the ZIP before extracting it.** Files downloaded from the internet are marked by Windows, and Windows 11 Smart App Control refuses to run marked files. Right-click the ZIP → Properties → tick **Unblock** → OK, then extract. If you already extracted it, delete the extracted folder and extract again from the unblocked ZIP.
- **Blue "Windows protected your PC" window** when running `build_and_install.bat`: click **More info**, then **Run anyway**.
- **"Smart App Control blocked a file that may be unsafe"**: this window has no Run button. Unblock the ZIP as described above and extract again.
- **If Smart App Control still blocks the plugin** (this can happen with the ready-built file installed by the in-app updater, because it is unsigned): install by running `build_and_install.bat` instead, which builds the plugin on your own PC. The only other way is to turn Smart App Control off in Windows Security → App & browser control, which lowers protection for everything on the PC, so decide that for yourself.

## Quick start

1. Log in all characters with Dungeon Helper running. Each alt gets a small window on screen; the character you are playing gets the main window.
2. Click the footprint button on an alt's window (or `Follow` on the main window) to start following. The character you are playing at that moment becomes the leader.
3. Use `Near/Far` and `− +` to set how far behind the alts stay.
4. Open the TrailFollow page inside Dungeon Helper for settings. The first tab applies to all alts; the other tabs are saved separately for each character. Hover over any option for an explanation.

## Diagnostics (optional)

If the folder `%UserProfile%\Downloads\TrailFollow\trace` exists, the plugin writes movement, loot and cursor diagnostics there, one file per character. It records nothing unless you create that folder yourself. Delete the folder to turn it off.

## Notes

- Hotbar and slot numbers in the settings are the ones shown in the game, starting at 1.
- Input sent to a hotbar that is hidden in the game may be ignored, so keep the hotbars you use visible.
- The plugin needs `VoK.Sdk.dll` from your Dungeon Helper installation to build. It is referenced from there and is not part of this repository.
