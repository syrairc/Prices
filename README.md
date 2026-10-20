# Prices

EmberHUD plugin for Torchlight: Infinite. FE price tags on bag and stash cells, bag / stash totals, a local price database built from your own trade house scans.

- Price tags per cell with colour, corner, offset and text style settings, hide-below threshold, per-rarity and per-bag toggles.
- Sources: TITrack and Etor community prices plus your own local trade house scans, merged by priority. Legendaries are priced by their own listings.
- Totals for the open bag and stash, optionally as a low-high range. Moves with Alt through `EmberHUD.Ui.Overlay`.
- Local database tab: scanned listings, purge, share / import between friends.

## Layout

`Main.cs` render and loop, `Settings.cs` + `SettingsUI.cs` settings, `Tags.cs` tag text and anchoring, `Sources.cs` / `PriceSync.cs` / `Exchange.cs` / `Contribute.cs` price sources, `Local.cs` / `LocalListings.cs` / `LegendaryPrices.cs` local book, `Share.cs` import / export, `ItemNames.cs`, `defaults.json`, `Tests\`.

## Use

Lives at `E:\EmberHUD\src\Plugins\Prices`, a separate repo inside the `src` working tree (`src` ignores it). The host compiles it from there, `Ember.exe --check` runs its tests. Plugin-only change: copy the files to `E:\EmberHUD\test\Plugins\Prices\` and the host reloads it. Host / Api side (`Game.State.Trade`, `TradeGoods`, `Items`, `Game.WantTradeGoods`) stays in the `src` repo.
