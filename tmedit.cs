#:package GBX.NET@2.*
#:package GBX.NET.LZO@2.*

// tmedit — move / copy / place / remove / turn blocks in TrackMania Nations/United Forever maps.
// Run (.NET 10 SDK):  dotnet run tmedit.cs -- <command> <map.Challenge.Gbx> [options]
// Output is written to "<map> edited1.Challenge.Gbx" (edited2, ... if taken), in an "Output" folder next to
// the map if there is one. The input is never overwritten unless you pass the same path to -o.

using System.Text.RegularExpressions;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;

Gbx.LZO = new MiniLZO();

if (args.Length < 2 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return 1;
}

var cmd = args[0].ToLowerInvariant();
var inPath = args[1];
Options opt;
try { opt = Options.Parse(args.Skip(2).ToArray()); }
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Gbx<CGameCtnChallenge> gbx;
try
{
    gbx = Gbx.Parse<CGameCtnChallenge>(inPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not read map: {ex.Message}");
    return 2;
}

var map = gbx.Node;
if (map.Blocks is null)
{
    Console.Error.WriteLine("Map has no block list (unsupported or corrupted map).");
    return 2;
}

var size = map.Size;
Console.WriteLine($"Map: {Strip(map.MapName)}  env={map.Collection}  size={size.X}x{size.Y}x{size.Z}  blocks={map.Blocks.Count}");

// Flags == -1 are free/"skip" entries in TMF, not real blocks.
var selected = map.Blocks.Where(b => b.Flags != -1 && opt.Matches(b)).ToList();

switch (cmd)
{
    case "list":
        foreach (var g in selected.GroupBy(b => b.Name).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key}  x{g.Count()}");
        Console.WriteLine();
        foreach (var b in selected.OrderBy(b => b.Coord.Y).ThenBy(b => b.Coord.X).ThenBy(b => b.Coord.Z))
            Console.WriteLine($"  {Describe(b)}");
        Console.WriteLine($"{selected.Count} block(s) matched.");
        return 0;

    case "move":
    {
        var by = opt.Require(opt.By, "--by dx,dy,dz");
        if (!CheckBounds(selected.Select(b => b.Coord + by))) return 3;
        foreach (var b in selected)
        {
            b.Coord += by;
            if (opt.Ground is bool gr) b.IsGround = gr;
        }
        Console.WriteLine($"Moved {selected.Count} block(s) by {Fmt(by)}.");
        break;
    }

    case "set":
    {
        // set <map> [--x n] [--y n] [--z n] [filters] — put every matching block on the same coordinate(s)
        if (opt.SetX is null && opt.SetY is null && opt.SetZ is null)
        {
            Console.Error.WriteLine("Usage: set <map> [--x n] [--y n] [--z n] [filters]");
            return 1;
        }
        Int3 Target(Int3 c) => new(opt.SetX ?? c.X, opt.SetY ?? c.Y, opt.SetZ ?? c.Z);
        if (!CheckBounds(selected.Select(b => Target(b.Coord)))) return 3;
        foreach (var b in selected)
        {
            b.Coord = Target(b.Coord);
            if (opt.Ground is bool gr) b.IsGround = gr;
        }
        var stacked = selected.GroupBy(b => b.Coord).Count(g => g.Count() > 1);
        Console.WriteLine($"Set {selected.Count} block(s) to x={opt.SetX?.ToString() ?? "*"} y={opt.SetY?.ToString() ?? "*"} z={opt.SetZ?.ToString() ?? "*"}.");
        if (stacked > 0)
            Console.WriteLine($"  Warning: {stacked} coordinate(s) now hold more than one block (they overlap).");
        break;
    }

    case "mirror":
    {
        // mirror <map> --axis x|y|z [--around n] [--keep-dir] [filters]
        if (opt.Axis is not ('x' or 'y' or 'z'))
        {
            Console.Error.WriteLine("Usage: mirror <map> --axis x|y|z [--around n] [--keep-dir] [filters]");
            return 1;
        }
        int axisSize = opt.Axis switch { 'x' => size.X, 'y' => size.Y, _ => size.Z };
        double pivot = opt.Around ?? (axisSize - 1) / 2.0; // default: centre of the map on that axis
        int Flip(int c) => (int)Math.Round(2 * pivot - c);
        Int3 MirrorTarget(Int3 c) => opt.Axis switch
        {
            'x' => c with { X = Flip(c.X) },
            'y' => c with { Y = Flip(c.Y) },
            _ => c with { Z = Flip(c.Z) },
        };
        if (!CheckBounds(selected.Select(b => MirrorTarget(b.Coord)))) return 3;
        foreach (var b in selected)
        {
            b.Coord = MirrorTarget(b.Coord);
            if (opt.KeepDir) continue;
            b.Direction = (opt.Axis, b.Direction) switch
            {
                ('x', Direction.East) => Direction.West,
                ('x', Direction.West) => Direction.East,
                ('z', Direction.North) => Direction.South,
                ('z', Direction.South) => Direction.North,
                _ => b.Direction,
            };
        }
        Console.WriteLine($"Mirrored {selected.Count} block(s) on {opt.Axis} around {pivot}.");
        break;
    }

    case "shuffle":
    {
        // shuffle <map> [--pool file]... [--by-size [--tolerance n] [--scale f]] [--seed n] [filters]
        var rng = opt.Seed is int sd ? new Random(sd) : new Random();
        var pool = new HashSet<string>(map.Blocks.Where(b => b.Flags != -1).Select(b => b.Name), StringComparer.Ordinal);
        var sizes = new Dictionary<string, Int3>(StringComparer.Ordinal);
        // Variant counts: from pool .txt files (5th column), plus the highest variant actually seen in maps.
        var variantCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        NoteVariants(map.GetBlocks(), variantCounts);
        var placements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in opt.Pools)
            if (!LoadPool(p, pool, sizes, variantCounts, placements)) return 2;
        int groundFixed = 0;

        if (opt.KeepStartFinish)
            pool.RemoveWhere(IsStartFinish); // nothing else may become a start/finish either
        if (!opt.IncludeClips)
            pool.RemoveWhere(IsClipType);    // clips are invisible connectors: swapping to one looks like air

        var sources = selected.Select(b => b.Name)
            .Where(n => !(opt.KeepStartFinish && IsStartFinish(n)))
            .Where(n => opt.IncludeClips || !IsClipType(n))
            .Distinct(StringComparer.Ordinal).ToList();

        bool Compatible(string s, string t)
        {
            if (s == t) return false;
            if (opt.CpToCp && IsCheckpoint(s) != IsCheckpoint(t)) return false; // checkpoints <-> checkpoints only
            if (!opt.BySize) return true;
            if (!sizes.TryGetValue(s, out var a) || !sizes.TryGetValue(t, out var c)) return false;
            // Small blocks must match closely; big blocks (sculptures etc.) get more slack.
            int tol = opt.Tolerance + (int)Math.Floor(opt.Scale * Math.Max(a.X, Math.Max(a.Y, a.Z)));
            return Math.Abs(a.X - c.X) <= tol && Math.Abs(a.Y - c.Y) <= tol && Math.Abs(a.Z - c.Z) <= tol;
        }

        var mapping = RandomMatching(sources, pool.ToList(), Compatible, rng);

        int changed = 0;
        foreach (var b in selected)
        {
            if (!mapping.TryGetValue(b.Name, out var newName)) continue;
            changed++;
            var oldVariant = b.Variant;
            b.Name = newName;
            var known = Math.Min(variantCounts.TryGetValue(newName, out var vc) ? vc : 0, 64); // variant field is 6 bits
            b.Variant = opt.VariantMode switch
            {
                // keep: same shape index if the new type has that many, else the default shape
                "keep" => known > oldVariant ? oldVariant : (byte)0,
                // random: any shape the new type is known to have
                "random" => known > 0 ? (byte)rng.Next(known) : (byte)0,
                _ => (byte)0,
            };
            b.SubVariant = 0;
            b.IsClip = false;
            // Some blocks only exist on the ground or only in the air; the game refuses to load the wrong one.
            if (placements.TryGetValue(newName, out var where))
            {
                if (b.IsGround && !where.Contains('G')) { b.IsGround = false; groundFixed++; }
                else if (!b.IsGround && !where.Contains('A')) { b.IsGround = true; groundFixed++; }
            }
            b.Flags &= ~((1 << 15) | (1 << 17) | (1 << 18) | (1 << 19) | (1 << 20)); // drop skin/special refs
        }

        foreach (var (from, to) in mapping.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {from} -> {to}");
        var unmatched = sources.Where(s => !mapping.ContainsKey(s)).ToList();
        Console.WriteLine($"Replaced {mapping.Count} block type(s) across {changed} block(s). Pool: {pool.Count} types. Variants: {opt.VariantMode}.");
        if (opt.KeepStartFinish) Console.WriteLine("  Start/finish blocks kept as they were.");
        if (opt.CpToCp) Console.WriteLine("  Checkpoints only swapped with other checkpoints.");
        if (groundFixed > 0)
            Console.WriteLine($"  Switched ground/air on {groundFixed} block(s) whose new type only exists the other way.");
        var noCount = mapping.Values.Where(t => !variantCounts.ContainsKey(t)).ToList();
        if (opt.VariantMode != "zero" && noCount.Count > 0)
            Console.WriteLine($"  {noCount.Count} new type(s) have no known variant count, so they got variant 0 (add blocks.txt from tmblocks as --pool).");
        if (unmatched.Count > 0)
            Console.WriteLine($"  {unmatched.Count} type(s) left unchanged (no free compatible partner{(opt.BySize ? " or no size known" : "")}): {string.Join(", ", unmatched)}");
        break;
    }

    case "copy":
    {
        var by = opt.Require(opt.By, "--by dx,dy,dz");
        if (!CheckBounds(selected.Select(b => b.Coord + by))) return 3;
        foreach (var b in selected)
        {
            var c = new CGameCtnBlock
            {
                Name = b.Name,
                Direction = b.Direction,
                Coord = b.Coord + by,
                // Drop bits that reference sub-nodes (skin, special props, decals) so nodes aren't shared.
                Flags = b.Flags & ~((1 << 15) | (1 << 17) | (1 << 18) | (1 << 19) | (1 << 20)),
            };
            if (opt.Ground is bool gr) c.IsGround = gr;
            map.PlaceBlock(c);
        }
        Console.WriteLine($"Copied {selected.Count} block(s) offset by {Fmt(by)}. (Custom skins/signs are not copied.)");
        break;
    }

    case "place":
    {
        // place <map> <BlockName> <x,y,z> [--dir N] [--ground] [--variant n]
        if (opt.Positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: place <map> <BlockName> <x,y,z> [--dir N|E|S|W] [--ground] [--variant n]");
            return 1;
        }
        var name = opt.Positional[0];
        Int3 at;
        try { at = Options.ParseInt3(opt.Positional[1]); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        if (!CheckBounds([at])) return 3;
        var block = map.PlaceBlock(name, at, opt.Dir ?? Direction.North,
            isGround: opt.Ground ?? false, variant: opt.Variant ?? 0);
        Console.WriteLine($"Placed {Describe(block)}");
        if (!map.Blocks.Any(b => b != block && b.Name == name))
            Console.WriteLine($"  Note: no other '{name}' exists in this map — double-check the block name (use 'list' on a map that has it).");
        break;
    }

    case "remove":
    {
        if (!opt.HasFilter && !opt.All)
        {
            Console.Error.WriteLine("Refusing to remove every block. Add a filter (--name/--at/--box) or --all.");
            return 1;
        }
        var set = selected.ToHashSet();
        var n = map.RemoveBlocks(b => set.Contains(b));
        Console.WriteLine($"Removed {n} block(s).");
        break;
    }

    case "turn":
    {
        var dir = opt.Require(opt.Dir, "--dir N|E|S|W");
        foreach (var b in selected) b.Direction = dir;
        Console.WriteLine($"Set direction {dir} on {selected.Count} block(s). (Position is unchanged; multi-tile blocks may need a move afterwards.)");
        break;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{cmd}'.");
        PrintHelp();
        return 1;
}

if (opt.DryRun)
{
    Console.WriteLine("Dry run — nothing saved.");
    return 0;
}

// Name: --rn, else "<map> edited1", "<map> edited2", ... (first free number). The file is named after the map
// and saved in an "Output" folder next to the input map if there is one.
var outDir = OutputDir(inPath);
var fileStem = opt.NewName is null ? FreeStem(outDir, Stem(inPath) + " edited", true) : FreeStem(outDir, SafeFileName(opt.NewName), false);
map.MapName = opt.NewName ?? fileStem;
var outPath = opt.Out ?? Path.Combine(outDir, fileStem + ".Challenge.Gbx");
gbx.Save(outPath); // Gbx.Parse keeps TMF header params (PackDesc version etc.), so this saves in TMF format.
Console.WriteLine($"Saved: {outPath}");
return 0;

// ---------------------------------------------------------------- helpers

bool CheckBounds(IEnumerable<Int3> coords)
{
    var bad = coords.Where(c => c.X < 0 || c.Z < 0 || c.Y < 0 || c.X >= size.X || c.Y >= size.Y || c.Z >= size.Z).ToList();
    if (bad.Count == 0) return true;
    Console.Error.WriteLine($"{bad.Count} block(s) would end up outside the map ({size.X}x{size.Y}x{size.Z}), e.g. {Fmt(bad[0])}. Nothing changed.");
    return false;
}

// Random injective mapping source -> target (no target used twice, never maps to itself),
// maximised with augmenting paths so as many types as possible get a partner.
static Dictionary<string, string> RandomMatching(List<string> sources, List<string> targets,
    Func<string, string, bool> ok, Random rng)
{
    var adj = sources.ToDictionary(s => s, s => targets.Where(t => ok(s, t)).OrderBy(_ => rng.Next()).ToList());
    var owner = new Dictionary<string, string>(StringComparer.Ordinal); // target -> source

    bool Try(string s, HashSet<string> seen)
    {
        foreach (var t in adj[s])
        {
            if (!seen.Add(t)) continue;
            if (!owner.TryGetValue(t, out var o) || Try(o, seen))
            {
                owner[t] = s;
                return true;
            }
        }
        return false;
    }

    foreach (var s in sources.OrderBy(_ => rng.Next()))
        Try(s, new HashSet<string>(StringComparer.Ordinal));

    return owner.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
}

// Start/finish/multilap blocks (not ramps like "StadiumCircuitSlopeStart" or "...LoopStart").
static bool IsStartFinish(string name) =>
    Regex.IsMatch(name, "(StartLine|Finish|FinishLine|MultiLap)$") || name.EndsWith("RoadMainStart", StringComparison.Ordinal);

// Clip blocks (e.g. StadiumWaterClip, StadiumRoadTiltClipLeft): auto-placed connectors with no visible model.
static bool IsClipType(string name) => Regex.IsMatch(name, "Clip(Left|Right)?$");

static bool IsCheckpoint(string name) => name.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase);

// Records the highest variant seen per block type (count = max + 1). Clips/skip entries are ignored.
static void NoteVariants(IEnumerable<CGameCtnBlock> blocks, Dictionary<string, int> counts)
{
    foreach (var b in blocks)
    {
        if (b.Flags == -1 || b.IsClip) continue;
        counts[b.Name] = Math.Max(counts.GetValueOrDefault(b.Name), b.Variant + 1);
    }
}

// Pool file: another .Gbx map (all its block names are added), or a text file with one block per line:
//   BlockName            -> name only
//   BlockName 2 1 3      -> name + size in blocks (x y z), used by --by-size
//   BlockName 2 1 3 6    -> ... + number of shape variants, used by --variants
//   BlockName 2 1 3 6 GA -> ... + where it can be placed (G = ground, A = air)
// '#' starts a comment.
static bool LoadPool(string path, HashSet<string> pool, Dictionary<string, Int3> sizes, Dictionary<string, int>? variants = null,
    Dictionary<string, string>? placements = null)
{
    try
    {
        if (path.EndsWith(".gbx", StringComparison.OrdinalIgnoreCase))
        {
            var other = Gbx.ParseNode<CGameCtnChallenge>(path);
            foreach (var b in other.GetBlocks().Where(b => b.Flags != -1)) pool.Add(b.Name);
            if (variants is not null) NoteVariants(other.GetBlocks(), variants);
            return true;
        }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            // Maps use bare block names; drop any folder prefix like "ConstructionBlockInfoClassic\\".
            parts[0] = parts[0][(parts[0].LastIndexOfAny(['\\', '/']) + 1)..];
            pool.Add(parts[0]);
            if (parts.Length >= 4 && int.TryParse(parts[1], out var sx) && int.TryParse(parts[2], out var sy)
                && int.TryParse(parts[3], out var sz))
                sizes[parts[0]] = new Int3(sx, sy, sz); // "? ? ?" placeholders are skipped
            if (variants is not null && parts.Length >= 5 && int.TryParse(parts[4], out var vn) && vn > 0)
                variants[parts[0]] = Math.Max(vn, variants.GetValueOrDefault(parts[0]));
            if (placements is not null && parts.Length >= 6 && parts[5].Trim('?').Length > 0)
                placements[parts[0]] = parts[5].ToUpperInvariant(); // "G", "A" or "GA"
        }
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not load pool '{path}': {ex.Message}");
        return false;
    }
}

static string Describe(CGameCtnBlock b) =>
    $"{b.Name,-32} at {Fmt(b.Coord),-12} dir={b.Direction,-5} ground={(b.IsGround ? "y" : "n")} variant={b.Variant} flags=0x{b.Flags:X}";

static string Fmt(Int3 v) => $"{v.X},{v.Y},{v.Z}";

static string Strip(string? s) => Regex.Replace(s ?? "", @"\$([0-9a-fA-F]{3}|[wnoitsgzmlhp<>$])", ""); // TM text formatting codes

static string Stem(string p)
{
    var file = Path.GetFileName(p);
    var i = file.IndexOf(".Challenge.Gbx", StringComparison.OrdinalIgnoreCase);
    return i >= 0 ? file[..i] : Path.GetFileNameWithoutExtension(file);
}

// "Output" folder next to the input map if it exists, else the input map's folder.
static string OutputDir(string input)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(input))!;
    var output = Path.Combine(dir, "Output");
    return Directory.Exists(output) ? output : dir;
}

// numbered: stem1, stem2, ...; otherwise stem, then "stem 2", "stem 3", ... - first one without a file.
static string FreeStem(string dir, string stem, bool numbered)
{
    if (!numbered && !File.Exists(Path.Combine(dir, stem + ".Challenge.Gbx"))) return stem;
    for (int n = numbered ? 1 : 2; ; n++)
    {
        var candidate = numbered ? $"{stem}{n}" : $"{stem} {n}";
        if (!File.Exists(Path.Combine(dir, candidate + ".Challenge.Gbx"))) return candidate;
    }
}

static string SafeFileName(string name)
{
    var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
    return clean.Length > 0 ? clean : "map";
}

static void PrintHelp() => Console.WriteLine("""
tmedit - edit blocks in TMNF/TMUF .Challenge.Gbx maps

Usage:  dotnet run tmedit.cs -- <command> <map> [options]

COMMANDS
  list     <map>                     Show blocks: name, position, direction, flags
  move     <map> --by dx,dy,dz       Shift blocks by an offset
  copy     <map> --by dx,dy,dz       Duplicate blocks with an offset
  set      <map> --x n --y n --z n   Put blocks on the same coordinate (any of the three)
  mirror   <map> --axis x|y|z        Flip block positions around an axis
  turn     <map> --dir N|E|S|W       Change which way blocks face
  place    <map> <Block> <x,y,z>     Add one block
  remove   <map>                     Delete blocks (needs a filter or --all)
  shuffle  <map>                     Give every block type a different random type

  All commands except place accept filters to choose which blocks they affect.

FILTERS  (combine freely; no filter = all blocks)
  -n, --name <pattern>          Block name, * as wildcard, e.g. "StadiumRoad*"
  -a, --at x,y,z                One exact coordinate
  -b, --box x1,y1,z1:x2,y2,z2   Everything inside a region (inclusive)

MIRROR
  --ax, --axis x|y|z            Axis to flip on
  --ar, --around n              Pivot line (default: map centre, e.g. 15.5)
  --kd, --keep-dir              Don't change which way blocks face

PLACE
  -d, --dir N|E|S|W             Facing direction (default N)
  -g, --ground / --air          Ground or air version
      --variant n               Shape variant number

SHUFFLE
  -p, --pool <file>             Extra block types to pick from: a .txt list or another map
                                (can be repeated)
  -s, --seed n                  Same seed = same result
  --bs, --by-size               Only swap types of similar size (sizes from the pool .txt)
  --tol, --tolerance n          Size slack for every block (default 0)
  --sc, --scale f               Extra slack per block size (default 0.25)
                                  allowed difference per axis = tol + floor(scale * longest side)
  --ksf, --keep-start-finish    Leave start/finish/multilap alone and never create new ones
  --ctp, --cp-to-cp             Checkpoints only swap with checkpoints
  --ic, --include-clips         Also shuffle clips (invisible connectors; skipped by default)
  --v, --var, --variants mode   keep   - keep shape number if the new type has it (default)
                                random - pick any shape the new type has
                                zero   - always shape 0

GENERAL
  -o, --out <path>              Output file (default: "<map> edited1", edited2, ... in an Output folder
                                next to the map if there is one, else next to the map)
  --rn, --rename <name>         Map name, also used as the file name (a number is added if taken)
  --dr, --dry-run               Show what would happen without saving
  -g, --ground / --air          Set ground/air on moved, copied or set blocks
  -h, --help                    Show this help

EXAMPLES
  dotnet run tmedit.cs -- list MyTrack.Challenge.Gbx -n "*Checkpoint*"
  dotnet run tmedit.cs -- move MyTrack.Challenge.Gbx --by 0,2,0 -b 10,1,10:15,5,15
  dotnet run tmedit.cs -- set MyTrack.Challenge.Gbx --y 1 -g
  dotnet run tmedit.cs -- mirror MyTrack.Challenge.Gbx --ax x
  dotnet run tmedit.cs -- shuffle MyTrack.Challenge.Gbx -p block.txt --bs --ksf --ctp --v random
""");

sealed class Options
{
    public string? NamePattern, Out, NewName;
    public Int3? At, By, BoxMin, BoxMax;
    public Direction? Dir;
    public bool? Ground;
    public byte? Variant;
    public int? SetX, SetY, SetZ;
    public char? Axis;
    public double? Around;
    public bool KeepDir;
    public List<string> Pools = [];
    public bool BySize;
    public int Tolerance;
    public double Scale = 0.25;
    public int? Seed;
    public string VariantMode = "keep";
    public bool KeepStartFinish, CpToCp, IncludeClips;
    public bool All, DryRun;
    public List<string> Positional = [];
    Regex? nameRx;

    public bool HasFilter => NamePattern is not null || At is not null || BoxMin is not null;

    public bool Matches(CGameCtnBlock b) =>
        (nameRx is null || nameRx.IsMatch(b.Name)) &&
        (At is null || b.Coord == At) &&
        (BoxMin is null || (b.Coord.X >= BoxMin.Value.X && b.Coord.X <= BoxMax!.Value.X &&
                            b.Coord.Y >= BoxMin.Value.Y && b.Coord.Y <= BoxMax.Value.Y &&
                            b.Coord.Z >= BoxMin.Value.Z && b.Coord.Z <= BoxMax.Value.Z));

    public T Require<T>(T? v, string what) where T : struct
    {
        if (v is T val) return val;
        Console.Error.WriteLine($"Missing {what}");
        Environment.Exit(1);
        return default;
    }

    // Short forms -> full option names.
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-n"] = "--name", ["-a"] = "--at", ["-b"] = "--box",
        ["-d"] = "--dir", ["-g"] = "--ground",
        ["--ax"] = "--axis", ["--ar"] = "--around", ["--kd"] = "--keep-dir",
        ["-p"] = "--pool", ["--bs"] = "--by-size", ["--tol"] = "--tolerance", ["--sc"] = "--scale", ["-s"] = "--seed",
        ["--ksf"] = "--keep-start-finish", ["--ctp"] = "--cp-to-cp", ["--ic"] = "--include-clips",
        ["--v"] = "--variants", ["--var"] = "--variants",
        ["--dr"] = "--dry-run", ["--rn"] = "--rename",
    };

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
            if (Aliases.TryGetValue(a[i], out var full)) a[i] = full;
            switch (a[i])
            {
                case "--name": o.NamePattern = Next(); break;
                case "--at": o.At = ParseInt3(Next()); break;
                case "--by": o.By = ParseInt3(Next()); break;
                case "--box":
                    var parts = Next().Split(':');
                    if (parts.Length != 2) throw new ArgumentException("--box expects x1,y1,z1:x2,y2,z2");
                    Int3 p = ParseInt3(parts[0]), q = ParseInt3(parts[1]);
                    o.BoxMin = new(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), Math.Min(p.Z, q.Z));
                    o.BoxMax = new(Math.Max(p.X, q.X), Math.Max(p.Y, q.Y), Math.Max(p.Z, q.Z));
                    break;
                case "--dir": o.Dir = ParseDir(Next()); break;
                case "--ground": o.Ground = true; break;
                case "--air": o.Ground = false; break;
                case "--variant": o.Variant = byte.Parse(Next()); break;
                case "--x": o.SetX = int.Parse(Next()); break;
                case "--y": o.SetY = int.Parse(Next()); break;
                case "--z": o.SetZ = int.Parse(Next()); break;
                case "--axis": o.Axis = char.ToLowerInvariant(Next()[0]); break;
                case "--around": o.Around = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--keep-dir": o.KeepDir = true; break;
                case "--pool": o.Pools.Add(Next()); break;
                case "--by-size": o.BySize = true; break;
                case "--tolerance": o.Tolerance = int.Parse(Next()); break;
                case "--scale": o.Scale = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next()); break;
                case "--keep-start-finish": o.KeepStartFinish = true; break;
                case "--cp-to-cp": o.CpToCp = true; break;
                case "--include-clips": o.IncludeClips = true; break;
                case "--variants":
                    o.VariantMode = Next().ToLowerInvariant();
                    if (o.VariantMode is not ("keep" or "random" or "zero"))
                        throw new ArgumentException("--variants must be keep, random or zero");
                    break;
                case "--all": o.All = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--rename": o.NewName = Next(); break;
                case "-o": case "--out": o.Out = Next(); break;
                default:
                    if (a[i].StartsWith("--")) throw new ArgumentException($"Unknown option {a[i]}");
                    o.Positional.Add(a[i]); break;
            }
        }
        if (o.NamePattern is not null)
            o.nameRx = new Regex("^" + Regex.Escape(o.NamePattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
        return o;
    }

    public static Int3 ParseInt3(string s)
    {
        var p = s.Split(',').Select(int.Parse).ToArray();
        if (p.Length != 3) throw new ArgumentException($"Expected x,y,z but got '{s}'");
        return new Int3(p[0], p[1], p[2]);
    }

    static Direction ParseDir(string s) => s.ToUpperInvariant() switch
    {
        "N" or "NORTH" or "0" => Direction.North,
        "E" or "EAST" or "1" => Direction.East,
        "S" or "SOUTH" or "2" => Direction.South,
        "W" or "WEST" or "3" => Direction.West,
        _ => throw new ArgumentException($"Bad direction '{s}' (use N/E/S/W)")
    };
}
