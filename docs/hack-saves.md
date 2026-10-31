# ROM hack saves (pokeemerald-expansion): how they differ and how to find out

> Why this exists: PKHeX reads Gen 3 saves as vanilla Ruby/Sapphire/Emerald/FireRed/LeafGreen. Many popular hacks are
> built on [pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion), which keeps the save
> container but changes what is inside the Pokémon records, so PKHeX reads them wrongly. This page records what
> differs, how each fact was found, and a checklist for examining the next hack. Credit: the format was first
> documented by Ehsan516's [Seaglass Save Editor](https://github.com/Ehsan516/seaglass-save-editor) (MIT, see
> [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)). Facts below marked *(verified)* were checked here against real saves
> and ROMs of Emerald Seaglass, Pokémon Heart and Soul and Pokémon Lazarus; the rest are not yet confirmed.

## What stays the same *(verified on all three)*

- The 128 KB flash container: two slots (0x0000, 0xE000) of fourteen 4 KB sections, a footer at 0xFF4 with section id,
  checksum, signature `0x08012025` and save counter. The slot with the higher counter is current.
- SaveBlock1 = sections 1-4 joined; PC storage = sections 5-13 joined. PC: current box (u32), then 14 boxes of 30 80-byte records from offset 4.
- The Pokémon record: PV, OT id, nickname, OT name, checksum, then a 48-byte body XOR-encrypted with PV^OTID and
  shuffled into the substructures G, A, E, M by PV % 24. Body checksum = sum of its 24 u16.
- Trainer name (8 bytes), TID and SID in section 0 at 0x0, 0xA and 0xC.

## What differs

Not the same in every hack: **where the party is.** Vanilla Emerald has the party count (u32) at SaveBlock1+0x234 and six
100-byte records after it. A hack that adds a field to SaveBlock1 moves it: Heart and Soul's count is at 0x238 *(verified:
its party is Gastly, Totodile, Pidgey, Bellsprout, Dunsparse, Hoothoot)*. Reading the vanilla offset there gives a count of
0 and an empty party, which looks like "the save has no party". The reader therefore finds the party: a count of 1-6 followed
by that many valid records and then only empty ones.

| What | Vanilla Gen 3 | pokeemerald-expansion hacks |
| --- | --- | --- |
| Species | internal index (252-276 unused, then Gen 3 species shifted) | low 11 bits of the u16 are the species, **mostly national dex order**; ids reach 1294+ with forms *(verified)*. The top 5 bits are not species |
| Moves, items | ≤ 354 moves, ≤ 376 items | far beyond (moves ≥ 799, items ≥ 521 seen) *(verified)* |
| Experience word (G+4) | u32, max 1,640,000 | low **21 bits** experience; the **top 11 bits hold other data** and are non-zero in normal saves (255 is common) *(verified)*. Anything that writes the word must keep them |
| Section checksum lengths | 3884 (section 0), 3848 (section 4), 3968, 2000 (section 13) | SaveBlock2 (section 0) is bigger; Heart and Soul's section 4 is too. The checksum covers the bigger structure, so a vanilla-length checksum fails *(verified)* |
| Species name table | 11-byte names | 12-byte names in a struct per species: stride 208 (Seaglass, mixed case) and 268 (Heart and Soul, upper case) *(verified)*; Lazarus: stride 212, found by vote |
| Level | derived from experience | party records store it at +0x54 (box records don't); PKHeX derives it from the 32-bit word and shows 100 *(verified)* |

## Symptoms that a save is a hack's

- PKHeX shows the wrong game (Seaglass reads as plain Emerald, Lazarus as Ruby/Sapphire), an empty party, or level 100.
- Species outside the vanilla set, or "unused" 252-276, in a valid record.
- Experience above 2^21, or a checksum that fits only a longer section.
- The Pokédex flag PKHeX looks for isn't set even though the save is well into the game.

None of these alone proves it. The save container of a hack is valid, so the reading is deterministic; deciding which
*hack* it is takes the ROM.

## Checklist for a new hack

1. **Container.** Parse the footers; confirm the signature, 14 unique section ids and the counter. Check each
   checksum with the vanilla length first. If one fails, find the shortest length that matches (the extra bytes are zero
   up to the real size) and note which sections grew.
2. **Records.** Decode the party at SaveBlock1+0x238. A good decode has a matching body checksum for every record; if the
   party is empty, look in the boxes (Heart and Soul's party was empty while its boxes held Pokémon).
3. **Field widths.** Take a record with a known level (party +0x54) and see whether the experience word's low 21 bits
   equal the growth curve's value for it, and what the top bits are. Check the species word the same way.
4. **ROM.** The game code is usually `BPEE` for all of them, so it doesn't tell hacks apart. Search the ROM for
   "Bulbasaur", "Ivysaur", "Venusaur" in both capitalisations: three hits at an equal spacing are the species name table;
   the spacing is the entry stride. Do the same for move and item names once the species table is solid.
5. **Compare.** Fill the table above for the new hack; record only what you checked.
6. **Tests.** Build a synthetic save with the new widths (see `tests/PokemonManager.Tests/ExpansionSaveTests.cs`) rather
   than committing a real save.

## Per-hack notes

| Hack | Base | Species names in ROM | Section checksum lengths | Notes |
| --- | --- | --- | --- | --- |
| Emerald Seaglass | expansion, national dex order | stride 208, mixed case, at 0x8f087c in the v3.0 ROM *(verified)* | section 0 > 3884 | Matches Ehsan516's documented addresses |
| Pokémon Heart and Soul | expansion | stride 268, upper case *(verified)* | sections 0 and 4 grown | party count at SaveBlock1+0x238 (+4); full party of 6 and 2 boxed Pokémon read correctly |
| Pokémon Lazarus | expansion-style ids (species up to 1356+) | stride 212, mixed case, names inside each species' data; found by vote (see below) *(verified)* | section 0 grown more than the others | full party of 6 and 131 boxed Pokémon read correctly, every name resolved |

### Finding a table that has no Bulbasaur, Ivysaur and Venusaur in a row (Lazarus)

A hack that drops species, or keeps the name inside a bigger per-species struct with unused (all-zero) entries between
them, breaks the three-names-in-a-row search. The fallback is a vote: look up well-known national dex names in the ROM
(mixed case, 0xFF terminated), and let every pair of hits vote for the stride and start that would put them at their dex
distance apart. The table is where most pairs agree. For Lazarus that gives stride 212; 122 of the 128 distinct Pokémon in
its boxes (the save nicknames are the species names) sit at exactly `start + id * 212`, which is the check to repeat on a
new hack.

### Known limit: a ROM where neither search finds a table

Both searches need species names stored as plain Gen 3 text. For a ROM where they aren't, the reader falls back to the
**national dex order**, which the ROM tables confirm for Seaglass. What you will see because of that:

- Species up to 1025 are named from the national dex. This is an assumption for that hack, not a measurement. (Lazarus once relied on it and got names wrong: a regional form showed as Cetitan where the ROM table says Ponyta.)
- Form and regional-variant ids above that show as `#<id>`. The dex has no entry
  for them; their names would come from the hack's own tables, which haven't been located.
- If a hack numbers species differently from the national dex, the fallback names are wrong without any error. Check a
  known Pokémon against the game before trusting names for a new hack.

Moves, items and abilities have the same limit (no names, only ids) until their tables are located.

## What this app does with them today

`pkmgr hack-list <save> [rom]` lists the party and boxes read by `src/PokemonManager/Core/Hacks/`, without PKHeX and
without writing anything. Editing and moving Pokémon in and out of these saves is not supported yet: the ids differ
from the games PKHeX models, so doing it through PKHeX would corrupt the save.
