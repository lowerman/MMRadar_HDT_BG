# Code signing policy

MMRadar's Windows app (`MMRadar.exe`) is built from this repository by GitHub Actions ([`.github/workflows/build-app.yml`](.github/workflows/build-app.yml)); only those CI builds are signed. We have applied for free code signing from [SignPath Foundation](https://signpath.org); once approved, release builds will carry this notice:

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)

## Team roles

- **Committers and reviewers:** [lowerman](https://github.com/lowerman) (repository owner). Changes from other contributors are reviewed before they are merged.
- **Approvers:** [lowerman](https://github.com/lowerman). Every signing request is approved manually.

## What gets signed

Only `MMRadar.exe`, built by CI from the source in this repository. Third-party files shipped next to it (`Newtonsoft.Json.dll`) are unmodified upstream releases and keep their own publisher's signature.

## Privacy policy

MMRadar collects no personal data and sends no telemetry. It connects to other systems only for its stated purpose:

- **wallii.gg** (public Supabase API): the in-game names and region of the players in your current Battlegrounds lobby, to look up their ratings and recent games.
- **Official leaderboard mirrors** ([bgrank.fly.dev](https://github.com/IBM5100o/BGrank_bot) and a [GitHub-hosted mirror](https://github.com/lowerman/bg-board-mirror)): the public leaderboard of your region is downloaded; nothing about you is sent.
- **GitHub** (api.github.com): checks for new MMRadar versions, automatically if "Check for updates automatically" is on (it can be turned off in the MMRadar window), or when you click "Check for updates".

Settings and logs stay on your computer in `%AppData%\MMRadar`. MMRadar reads Hearthstone's memory only to find the players in your lobby; it never writes to the game and nothing is injected into it.
