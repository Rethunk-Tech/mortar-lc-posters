# Posters

Puts your own pictures on the ship's posters and tips board, and on paintings. Packs made for LethalPosters and
LethalPaintings still work with it.

- Who needs it: each player who wants to see them. Players without it see the game's own.
- Config: shared from the host through [ConfigSync](https://thunderstore.io/c/lethal-company/p/Mortar/ConfigSync/), so
  everyone sees the same pictures.
  - `Change`: `EachLanding` (the default) or `Never`, the same pictures every game.
  - `Include game posters`: keeps the game's own poster and tips pictures in the mix. Off by default.

The ship's posters change with every landing, and each painting's picture comes from its value. A painting in a save
keeps its picture when you add or remove packs, as long as its file is still there. Pictures you add while the game
runs show up straight away.

## Making a pack

Put PNG or JPG files in any of these folders in your package:

- `mortar/posters` for the posters
- `mortar/tips` for the tips board
- `mortar/paintings` for paintings

Leave out the ones you don't need. A pack can carry other mods' folders in `mortar` too, such as `mortar/boombox`.

The folders LethalPosters and LethalPaintings use (`LethalPosters/posters`, `LethalPosters/tips`,
`LethalPaintings/paintings`) are still read.
