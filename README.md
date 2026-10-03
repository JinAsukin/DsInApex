# Ds in Apex — Supported Games Database

Official supported-games database consumed by **Ds in Apex (DIA)** — the native
Chinese client that virtualizes a Flydigi Apex 4 controller as a native
PlayStation 5 DualSense on Windows.

This repository currently hosts the **cloud data source only**. The application
source code will be published here after the first release (DIA is a
GPL-3.0-or-later derivative of
[ReynArts/ApexSenseBridge](https://github.com/ReynArts/ApexSenseBridge)).

## Layout

- `data/supported_games.json` — game catalogue: per-title support flags
  (adaptive triggers / haptic feedback), recommended touchpad profile,
  verified Steam AppID, executable names and cover art URL.

## Consumed by

- https://raw.githubusercontent.com/JinAsukin/DsInApex/main/data/supported_games.json
- https://cdn.jsdelivr.net/gh/JinAsukin/DsInApex@main/data/supported_games.json

Clients keep a local cache and fall back to an embedded copy (215 titles) when
offline.

## License

The application is GPL-3.0-or-later; this data file is distributed under the
same terms.
