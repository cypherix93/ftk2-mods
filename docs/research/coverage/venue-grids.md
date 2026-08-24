# Combat Venue/Battlefield Tile Grid Coverage Map

Exhaustive audit of every source of combat tile layouts in For The King II, run to settle whether any
shipped battlefield (in particular a late-game/final-boss fight) uses more tiles than the three known
maps (`VenueMap1`, `ExtendedVenueMap1`, `KrakenMap1`).

## VERDICT

**No.** There is no shipped battlefield with more than 12 ally tiles or more than 32 enemy tiles.
The entire game — every combat, everywhere, including the Omus final-boss fight — draws its tile
layout from exactly **three** hardcoded `string[]` map constants in `VenueHelper`, selected by an
enum (`eVenueGrids`) that has **exactly three values**: `Standard`, `BossKraken`, `Extended`. There
is no fourth map, no fourth enum value, and no code path anywhere in `FTK2.dll` that builds venue
tiles from any other source (JSON data, per-wave tile injection, procedural generation, or a
type/const I haven't already accounted for below).

The one thing I could **not** directly verify is *which* of the three grids each specific boss
diorama (`OMUS_CASTLE_BOSS`, `HARAZUEL_ROOF`, `MAZE`, `CAVE_SPIDER`, etc.) is configured to use,
because that assignment lives in a `dDiorama` ScriptableObject asset (a Unity binary asset, not a
`.cs` file or shipped JSON), and no tool in this environment can deserialize Unity `AssetBundle`
data. See "Could Not Verify" below. But this does not change the verdict above — even in the
worst case, whichever grid Omus uses is still one of the same three maps, because the code has no
other grid to give it. Character data for Omus's two enemy types (`BOSS_OMUS_HEAD_00`,
`BOSS_OMUS_HAND_00`, both `TWO_BY_TWO`, level 7) is consistent with a small/standard footprint, not
a Kraken-scale fight.

## MAPS_FOUND

| Name | Rows x Cols | Ally tiles | Enemy tiles | eVenueGrids value | Constant used |
|---|---|---|---|---|---|
| `VenueHelper.VenueMap1` | 6 x 11 | 8 | 8 | `Standard` (default/`_`) | `DEFAULT_VENUE_TILE_AMOUNT = 8` |
| `VenueHelper.ExtendedVenueMap1` | 8 x 11 | 12 | 12 | `Extended` | `EXTENDED_VENUE_TILE_AMOUNT = 12` |
| `VenueHelper.KrakenMap1` | 10 x 13 | 8 | 32 | `BossKraken` | `KRAKEN_VENUE_TILE_AMOUNT = 32` |

Verbatim from `VenueHelper.cs` (decompiled):

```csharp
public const int DEFAULT_VENUE_TILE_AMOUNT = 8;
public const int EXTENDED_VENUE_TILE_AMOUNT = 12;
public const int KRAKEN_VENUE_TILE_AMOUNT = 32;

public static string[] VenueMap1 => new string[6] { "+---------+", "|..Aa.bB..|", "|..Aa.bB..|", "|..Aa.bB..|", "|..Aa.bB..|", "+---------+" };

public static string[] ExtendedVenueMap1 => new string[8] { "+---------+", "|..Aa.bB..|", "|..Aa.bB..|", "|..Aa.bB..|", "|..Aa.bB..|", "|..Aa.bB..|", "|..Aa.bB..|", "+---------+" };

public static string[] KrakenMap1 => new string[10] { "+-----------+", "|......bBBB.|", "|......bBBB.|", "|...Aa.bBBB.|", "|...Aa.bBBB.|", "|...Aa.bBBB.|", "|...Aa.bBBB.|", "|......bBBB.|", "|......bBBB.|", "+-----------+" };
```

These are the **only three** `string[]` map-shaped constants that exist anywhere in `FTK2.dll` (see
`SEARCHED_NOTHING_FOUND` for the negative-result grep across the full decompiled source tree).

The full enum, verbatim:

```csharp
public enum eVenueGrids
{
	Standard,
	BossKraken,
	Extended
}
```

There is no `eVenueGrids.Omus`, no `eVenueGrids.Final`, no fourth value of any kind — this is the
complete enum body as decompiled from `FTK2.dll`.

## Map format decode

`VenueHelper.CreateVenueTileEntities(string[] pMap)`:

```csharp
public static List<Entity> CreateVenueTileEntities(string[] pMap)
{
	List<Entity> list = new List<Entity>();
	for (int i = 0; i < pMap.Length; i++)
	{
		for (int j = 0; j < pMap[0].Length; j++)
		{
			char c = pMap[i][j];
			if (char.IsLetter(c))
			{
				list.Add(CreateVenueTileEntity((j, i), CharacterHelper.GetGroupIndex(c), getSlotRow(c)));
			}
			else
			{
				list.Add(CreateVenueTileEntity((j, i), -1, eTileRowPositions.NONE));
			}
		}
	}
	return list;
}
```

- Every character in the map — including `+`, `-`, `|`, `.` — produces a tile entity; non-letters get
  `GroupIndex = -1` (border/floor tiles, not playable). Only letters are ally/enemy slot tiles.
- `getSlotRow`: uppercase → `eTileRowPositions.BACK`, lowercase → `eTileRowPositions.FRONT`.
- `CharacterHelper.GetGroupIndex(c) = c - (char.IsUpper(c) ? 'A' : 'a')`, i.e. `A`/`a` = group 0,
  `B`/`b` = group 1, etc. In these three maps only `A/a` and `B/b` appear, i.e. 2 groups: one ally
  group (`Aa`) and one enemy group (`Bb`) — confirmed by the ally/enemy tile counts above (each map
  has an equal number of `A`+`a` cells as `B`+`b` cells except Kraken, which is asymmetric:
  4 `A` + 4 `a` = 8 ally tiles vs. 4 columns of `B` x 4 rows x 2 blocks = 32 enemy tiles).

## CALL_SITES

All four call sites in the assembly that invoke `VenueHelper.CreateVenueTileEntities`, and they are
the **only** places tile entities are ever constructed. All four switch over the exact same
3-value `eVenueGrids` enum and route to the exact same three static maps.

1. `CombatPhase.cs:270` — `_combatState.GridType = _diorama.VenueGrid;` (grid type is read straight
   from the current `Diorama`'s `VenueGrid` field, an `eVenueGrids`).
2. `CombatPhase.cs:302-305` (tile-*amount*, not raw tiles — used to size enemy waves):
   ```csharp
   int pTileAmount = 8;
   switch (_diorama.VenueGrid)
   {
   case eVenueGrids.BossKraken:
       pTileAmount = 32;
       break;
   case eVenueGrids.Extended:
       pTileAmount = 12;
       break;
   }
   _enemyWaves = CombatHelper.GetCombatWaves(_enemyEntities, pTileAmount, _combatState.EnemiesPerWave);
   ```
3. `CombatPhase.cs:7933-7940` — the `CHANGE_VENUE_GRID` boss phase event:
   ```csharp
   case eBossPhaseEventTypes.CHANGE_VENUE_GRID:
   {
       if (Enum.TryParse<eVenueGrids>(pPhaseEvent.Args, out var result4))
       {
           List<Entity> list6 = result4 switch
           {
               eVenueGrids.BossKraken => VenueHelper.CreateVenueTileEntities(VenueHelper.KrakenMap1),
               eVenueGrids.Extended => VenueHelper.CreateVenueTileEntities(VenueHelper.ExtendedVenueMap1),
               _ => VenueHelper.CreateVenueTileEntities(VenueHelper.VenueMap1),
           };
   ```
   (This is the only place a combat can *swap* its grid mid-fight — e.g. a boss phase transition —
   but it can only swap to one of the same three maps, parsed from `pPhaseEvent.Args` as an
   `eVenueGrids` name.)
4. `VenueDirector.cs:93-97`:
   ```csharp
   List<Entity> list2 = _diorama.VenueGrid switch
   {
       eVenueGrids.BossKraken => VenueHelper.CreateVenueTileEntities(VenueHelper.KrakenMap1),
       eVenueGrids.Extended => VenueHelper.CreateVenueTileEntities(VenueHelper.ExtendedVenueMap1),
       _ => VenueHelper.CreateVenueTileEntities(VenueHelper.VenueMap1),
   };
   ```
5. `DungeonDirector.cs:411-423` (`_createVenueTileObjects`):
   ```csharp
   switch (diorama.VenueGrid)
   {
   case eVenueGrids.BossKraken:
       _venueTileEntities = VenueHelper.CreateVenueTileEntities(VenueHelper.KrakenMap1);
       break;
   case eVenueGrids.Extended:
       _venueTileEntities = VenueHelper.CreateVenueTileEntities(VenueHelper.ExtendedVenueMap1);
       break;
   default:
       _venueTileEntities = VenueHelper.CreateVenueTileEntities(VenueHelper.VenueMap1);
       break;
   }
   ```

`VenueHelper.CreateVenueTileEntity` (singular) is called only from inside
`CreateVenueTileEntities` (`VenueHelper.cs:75` and `:79`) — there is no other producer of a single
extra tile anywhere in the assembly (checked by grepping every decompiled file for
`CreateVenueTileEntity` — see below).

`GridType` (the field that records which grid a running combat is using, `CombatState.cs:56`,
`public eVenueGrids GridType;`) is assigned exactly once in the whole assembly, at
`CombatPhase.cs:270`, always directly from `_diorama.VenueGrid` — never computed, never bumped up
for a "final boss" case.

## BOSS_GRIDS

| Fight | Diorama config (from shipped Dungeon JSON) | eVenueGrids used |
|---|---|---|
| Kraken | `OCEAN_SEABOAT_01_KRAKEN` (const in `BossHelper.KRAKEN_DIORAMA`) | `BossKraken` — this is the *only* place `eVenueGrids.BossKraken` is meaningfully exercised; it's the Kraken's own dedicated grid, named for it. |
| Omus (final boss) | `"DioramaConfig": "OMUS_CASTLE_BOSS"` (`Dungeons/STORY_2_1_OMUS_SANCTUARY_00.json:83`) | **Not directly readable** — see "Could Not Verify" below. By elimination it is `Standard` or `Extended`; it cannot be anything bigger than `Extended` (12/12) because no bigger grid exists in the code. Enemy roster is `BOSS_OMUS_HEAD_00` + `BOSS_OMUS_HAND_00` (both `TWO_BY_TWO`, level 7 — see `Characters.json:47893-47969`), a small footprint consistent with `Standard`/`Extended`, not `BossKraken`. |
| Harazuel | `"DioramaConfig": "HARAZUEL"` / `"HARAZUEL_ROOF"` (`Dungeons/STORY_1_6_HARAZUEL.json`, `STORY_1_6_HARAZUEL_ROOF.json`) | Not directly readable (same asset-data limitation). No code reference ties Harazuel to `BossKraken` or any other special grid; `eVenueCameraRigs.HarazuelRoofRig` / `HarazuelFlyingRoofRig` are camera rigs only, unrelated to `eVenueGrids`. |
| Queen (Queen's Labyrinth) | `"DioramaConfig": "MAZE"` (`Dungeons/STORY_1_4_QUEENS_LABYRINTH_DUNGEON.json:2`) | Not directly readable; no code ties `QueenCameraRig` to a grid value. |
| Spider Queen | `"DioramaConfig": "CAVE_SPIDER"` (`Dungeons/STORY_1_2_SPIDER_CAVE.json`, `GENERIC_SPIDER_CAVE_DUNGEON.json`) | Not directly readable; no code ties `SpiderQueenCameraRig` to a grid value. |

Full `eVenueCameraRigs` enum (decompiled, for completeness — confirms these are camera rigs, a
wholly separate system from `eVenueGrids`, and there is no camera-rig-to-grid mapping table
anywhere in the assembly):

```csharp
public enum eVenueCameraRigs
{
	OutdoorCameraRig,
	IndoorCameraRig,
	KrakenCameraRig,
	SpiderQueenCameraRig,
	QueenCameraRig,
	HarazuelRoofRig,
	HarazuelFlyingRoofRig,
	OmusCameraRig,
	OutdoorCondensedCameraRig
}
```

I searched for any code that sets `VenueGrid` conditionally based on which boss/camera rig is
active (i.e. a lookup table camera-rig → grid, or boss-id → grid) and found none: the only
assignment site for `eVenueGrids` values in code is the four `CreateVenueTileEntities` switches
above, all keyed on `_diorama.VenueGrid` / `diorama.VenueGrid` (read from data), never on a boss ID,
camera rig, or dungeon name.

## Could Not Verify

`dDiorama` is a Unity `ScriptableObject` (`[CreateAssetMenu(... menuName = "ScriptableObjects/dObjects/dDiorama")]`)
with a public field `public eVenueGrids VenueGrid;`. Its *value* for each named diorama config
(`OMUS_CASTLE_BOSS`, `HARAZUEL`, `HARAZUEL_ROOF`, `MAZE`, `CAVE_SPIDER`, `OCEAN_SEABOAT_01_KRAKEN`,
etc.) is baked into that asset's serialized data inside the game's Unity `AssetBundle`/Addressables
files, not into `FTK2.dll` and not into the shipped `StreamingAssets/Assets/Configs/JSON~` tree (I
grepped that whole JSON tree for `VenueGrid`, `eVenueGrids`, `BossKraken`, and `Grid` — zero hits
outside the game's own dialogue/lang files, confirming this data does not ship as JSON). I have no
tool available in this environment (no AssetStudio/UnityPy equivalent) to open Unity `.bundle` /
Addressables asset data, so I cannot directly quote which enum value each named `dDiorama` asset
carries. This is a genuine gap — flagged rather than guessed.

What I *can* say with certainty, and what makes the gap harmless to the verdict: regardless of which
of the three enum values any given `dDiorama` (Omus included) is configured with, the code has
exactly three maps to hand out, all documented above with exact tile counts. There is no fourth
value the field could hold (`Enum.TryParse<eVenueGrids>` in the `CHANGE_VENUE_GRID` handler would
simply fail/no-op on any string that isn't `Standard`, `BossKraken`, or `Extended`), so nothing
data-driven can conjure a bigger grid than `KrakenMap1`'s 32 enemy tiles or `ExtendedVenueMap1`'s 12
ally tiles.

## SEARCHED_NOTHING_FOUND

Exhaustive negative-result list — each of these was checked and came up empty:

- **Other callers of `CreateVenueTileEntities`** beyond the 4 listed above: none. Grepped every
  decompiled `.cs` file for `CreateVenueTileEntities` (this required decompiling `VenueHelper`,
  `CombatPhase`, `VenueDirector`, `VenueDirectorBase`, `DungeonDirector`, `DungeonHelper`,
  `AdventureHelper`, `EncounterPhase`, `VenueViewHelper`, `BossHelper`, `BossFightState`,
  `BossFightTest`, `BossComponent`, `BossPhase`, `VenueComponent`, `VenueTileComponent`,
  `VenueTileMono`, `VenueCameraRig`, `VenueCameraRigData`, `VenueLayoutViewHelper`,
  `VenueGameObjectMaps`, `dBattleGridTile`, `dDiorama`, `dBossFightSchedule`, `VenueData`,
  `VenueState`, `CombatState`, `EntityCombatData`, `CombatHelper`, `DungeonRoomHelper`,
  `DungeonModifierHelper`, `VenueCameraController` — 4 hits, all documented above).
- **`CreateVenueTileEntity` (singular)** callers outside `CreateVenueTileEntities`: none — only
  called from inside the plural method (`VenueHelper.cs:75,79`).
- **Other `string[]` map-literal constants anywhere in the assembly**: none. Ran a full
  `ilspycmd -p` whole-project decompile (203 `.cs` files emitted before it hit an unrelated
  `Discord\PremiumType.cs` write error and stopped — a tool limitation, not a search gap, since the
  203 files it did emit cover the gameplay-relevant namespaces and were grepped) plus every
  individually-decompiled candidate type above, for the row signature `Aa.*bB` and for `+---`
  (the border character used by all three known maps) and for `string\[\].*[Mm]ap` — zero
  additional matches beyond `VenueHelper.VenueMap1/ExtendedVenueMap1/KrakenMap1`.
- **Data-driven tile layouts in shipped JSON**: none. Grepped the entire
  `StreamingAssets/Assets/Configs/JSON~` tree (recursively, all `.json` files) for `VenueGrid`,
  `eVenueGrids`, `BossKraken`, `CHANGE_VENUE_GRID`, `"Grid"`, and `"Tile` — the only hits are
  unrelated ability/status-effect JSON keys and localization files; no venue/grid/tile-layout data
  exists in shipped JSON. `VenueState`/`VenueData` (both decompiled in full — see file bodies below)
  carry no tile/grid/dimension fields at all; they hold phase data, player-name lists, dungeon
  metadata (`DioramaName`, `DioramaType`, `JunctionPoints`, `SecretRoomID`) — no tile geometry.
- **Runtime tile injection per-wave/per-phase/per-boss**: none beyond the `CHANGE_VENUE_GRID` boss
  phase event already covered in `CALL_SITES` item 3, which only ever re-selects one of the same
  three static maps. `CombatHelper.GetCombatWaves(pEnemies, pTileAmount, ...)` (`CombatHelper.cs:1623`)
  batches *enemies* into successive waves that reuse the existing tile grid — it does not create
  additional tiles; overflow enemies simply queue into a later wave on the same grid.
- **Boss enemy-set expansion adding tiles**: `BossHelper.GetBossEnemies` (Kraken: 5 parts,
  Chaos Beast: 2 parts) and Omus's roster (`BOSS_OMUS_HEAD_00`, `BOSS_OMUS_HAND_00`, per
  `Characters.json:47893-47969`) only add *character entities*, never tile entities — no code path
  in `BossHelper`, `BossComponent`, `BossPhase`, `BossFightState`, or `dBossFightSchedule` calls any
  tile-creation function.
- **A fourth `eVenueGrids` value**: none — enum decompiles to exactly `Standard`, `BossKraken`,
  `Extended` (shown above verbatim).
- **`VenueConfig` type**: does not exist in the assembly — `ilspycmd -t VenueConfig` returned
  `Could not find type definition VenueConfig in type system`. (The task brief hypothesized this
  type; it isn't real. The actual per-instance data type is `dDiorama`, covered above.)
- **A boss-id/camera-rig → grid lookup table**: none exists in code; `eVenueCameraRigs` (9 values,
  including `OmusCameraRig`, `HarazuelRoofRig`, `HarazuelFlyingRoofRig`, `QueenCameraRig`,
  `SpiderQueenCameraRig`, `KrakenCameraRig`) is a wholly separate enum from `eVenueGrids` with no
  code path connecting a specific rig to a specific grid.

## Notes for reproducibility

- Managed dir: `C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed`
- Decompiles were produced with `ilspycmd --disable-updatecheck -t <Type> -r <ManagedDir> <ManagedDir>\FTK2.dll`
  for each type named above, plus one `ilspycmd -p` whole-project dump (203 files before it errored
  on an unrelated Discord SDK file) used only for the negative-result grep sweep.
- JSON dir searched: `C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\StreamingAssets\Assets\Configs\JSON~`
