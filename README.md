# tmnf-scripts

C# scripts that use [GBX.NET](https://github.com/BigBang1112/gbx-net) by BigBang1112 to edit TrackMania Nations Forever map files (`.Challenge.Gbx`).

| Script | What it does |
|---|---|
| `tmedit.cs` | Edit blocks in a map: list, move, copy, place, remove, align, mirror, turn and shuffle |
| `tmmaze.cs` | Build a random maze from hand-made 3x3 modules |
| `tmblocks.cs` | List every block in the game files with its size, variants and ground/air placement |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`)
- The scripts are single-file .NET apps. GBX.NET is downloaded automatically the first time you run one.

Run any script with `--help` to see all of its options:

```
dotnet run tmedit.cs -- --help
```

Edited and generated maps go into an `Output` folder next to the input map if that folder exists, otherwise next to the input map. The input map is never overwritten.

## tmedit

```
dotnet run tmedit.cs -- <command> <map> [options]
```

| Command | Example | Effect |
|---|---|---|
| `list` | `list map.Challenge.Gbx -n "*Checkpoint*"` | Show blocks with position, direction and flags |
| `move` | `move map.Challenge.Gbx --by 0,2,0` | Shift blocks |
| `copy` | `copy map.Challenge.Gbx --by 5,0,0 -n "StadiumRoad*"` | Duplicate blocks with an offset |
| `set` | `set map.Challenge.Gbx --x 10 --y 1` | Put blocks on the same coordinate |
| `mirror` | `mirror map.Challenge.Gbx --ax x` | Flip block positions around an axis |
| `turn` | `turn map.Challenge.Gbx -d S -a 16,1,16` | Change facing direction |
| `place` | `place map.Challenge.Gbx StadiumRoadMain 16,1,16 -d E` | Add one block |
| `remove` | `remove map.Challenge.Gbx -a 3,1,7` | Delete blocks |
| `shuffle` | `shuffle map.Challenge.Gbx -p blocks.txt --bs --ksf --ctp` | Replace every block type with a different random one |

Filters choose which blocks a command affects: `-n` (name, `*` wildcard), `-a x,y,z` (one coordinate), `-b x1,y1,z1:x2,y2,z2` (region).

`shuffle` options:

- `-p <file>` adds block types to pick from (a `.txt` list or another map)
- `--bs` only swaps blocks of similar size. `--tol` and `--sc` set how strict that is: the allowed difference per axis is `tol + floor(scale × longest side)`
- `--ksf` keeps start/finish blocks
- `--ctp` only swaps checkpoints with other checkpoints
- `--v keep|random|zero` decides the shape variant of the new blocks
- `--ic` also shuffles clips (skipped by default)

Output is named `<map> edited1`, `<map> edited2`, … (`--rn` sets your own name).

## tmmaze

Builds a single winding route from modules: start → checkpoints → finish. Routes can climb several floors.

```
dotnet run tmmaze.cs                              # uses RPG_Modules_Base.Challenge.Gbx
dotnet run tmmaze.cs -- -l 60 --cp-gap 4-8 -s 1234
dotnet run tmmaze.cs -- -t                        # test map using every module
dotnet run tmmaze.cs -- --max -l 500              # as big as possible (needs TMUnlimiter)
```

How it works:

- **Modules** are built in a base map (`RPG_Modules_Base.Challenge.Gbx`) as 3x3-block cells. Big modules can span several cells, including L-shapes. Modules can be stored on several layers of the base map.
- **`modules.txt`** says where each module is, its type (start, finish, checkpoint, normal) and how the road goes through it. That covers one-way or two-way, floor changes (`^`), narrow openings (`~`), checkpoint direction (`cp:`) and nothing below/above (`nb`/`na`). The format is explained at the top of the file.
- **Rotation:** modules are rotated to fit the route. Checkpoints are turned around when a two-way checkpoint module is driven backwards. Blocks bigger than 1x1 are rotated using their size from `blocks.txt`.
- **Blocks just outside a module:** blocks up to 1 outside a module's body belong to it. Side blocks are dropped where they would land in another module. Blocks underneath are only dropped where they clash with an actual block, and never if they are taller than 1 or listed in `Block_Whitelist.Challenge.Gbx`.
- **Roofs:** open spots above modules get a roof (`--nr` to turn this off).
- **Checkpoints** are placed every 3–6 modules by default (`--cp-gap`, `--cp-every`, `-c`).
- **Output** is named `maze1`, `maze2`, … (`--rn` sets your own name).

Map size: by default the maze fits the standard 32×32×32 Stadium. `--fit` ignores that, builds freely, then sizes the map to the maze. Block coordinates can go up to 255. Maps bigger than 32 need TMUnlimiter.

## tmblocks

Reads TMNF's `.pak` files and writes a list of all blocks:

```
dotnet run tmblocks.cs -- "C:\Program Files (x86)\TmNationsForever" -o blocks.txt
```

Each line is `Name x y z variants placement`. The size is in blocks, variants is the number of shape variants, and placement is `G` (ground), `A` (air) or `GA`. `tmedit shuffle --bs` and `tmmaze` both use this file.

## Files

| File | Used by |
|---|---|
| `RPG_Modules_Base.Challenge.Gbx` | tmmaze: the module base map |
| `modules.txt` | tmmaze: module definitions |
| `Block_Whitelist.Challenge.Gbx` | tmmaze: block types never removed from under modules |
| `blocks.txt` | tmmaze / tmedit: block sizes and placement (made with tmblocks) |

## License note

The scripts use GBX.NET (MIT) and GBX.NET.LZO, which is GPL-3.0 licensed. Distributing software that uses GBX.NET.LZO means it has to be GPL-3.0 as well.
