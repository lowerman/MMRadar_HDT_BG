MMRadar
=======

Shows the leaderboard rating and average placement of every player in your
Hearthstone Battlegrounds lobby. Click a player to open their recent games
and MMR chart. Data comes from wallii.gg. Hearthstone Deck Tracker is not needed.

Getting started
---------------
1. Extract this zip into any folder (for example Documents\MMRadar).
   Do not run it from inside the zip.
2. Run MMRadar.exe.
   Windows may show "Windows protected your PC" because the app is not signed:
   click "More info", then "Run anyway".
   To avoid the warning: right-click MMRadar.exe -> Properties -> check
   "Unblock" -> OK (or do the same with the zip before extracting it).
3. MMRadar lives in the system tray, next to the clock. If Windows hides the
   icon behind ^, drag it onto the taskbar. Click the icon to open settings.
4. In settings, click "Show test panel" to place and size the panel before
   you play: drag its header to move it, scroll over it to zoom.
5. Start Hearthstone. The panel appears by itself in every Battlegrounds match.

Good to know
------------
- Do not run Hearthstone as administrator, or MMRadar cannot read the game.
- Players who are not on the leaderboard show "-" instead of a rating.
- Closing the window keeps MMRadar running. Use Exit in the tray menu to quit.
- "Start with Windows" in settings launches MMRadar when you log in.
- Settings and logs are kept in %AppData%\MMRadar.
- Some antivirus programs may flag the app because it reads the game's memory.
  It only reads, never writes. It goes online only to look up players on
  wallii.gg and to check GitHub for updates.

Uninstall
---------
Uncheck "Start with Windows", choose Exit in the tray menu, then delete the
folder. To remove settings too, delete %AppData%\MMRadar.

Project page: https://github.com/lowerman/MMRadar_HDT_BG
