# Vendored: UnitySpy (Unity Mono memory reader)

Source: https://github.com/Zero-to-Heroes/unity-spy-.net4.5 (commit 92b2c611b569472c97a233bdb71debba4a5f5f98),
a .NET Framework port of https://github.com/hackf5/unityspy (Copyright (c) 2019 brian t).

Both are distributed under the MIT License — see `LICENSE` in this folder.

Only the core reader (`UnitySpy/`) is included, without the GUI crawler. The
Hearthstone-specific field chains MMRadar reads (lobby names, leaderboard tiles,
region, battletag) were taken from that project's `UnitySpy.HearthstoneLib`
and re-implemented in `src/MMRadar.App/Hs/HearthstoneReader.cs`.

To update: copy the `UnitySpy/` folder of a newer commit over this one
(excluding `Crawler/` and `Properties/`) — the Unity offsets live in
`Offsets/MonoLibraryOffsets.cs`.
