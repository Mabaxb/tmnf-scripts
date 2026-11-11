#:package GBX.NET@2.*
#:package GBX.NET.LZO@2.*

// tmmaze — build a random single-route maze from modules for TrackMania Nations Forever.
// Run (.NET 10 SDK):  dotnet run tmmaze.cs -- "RPG_modules_base.Challenge.Gbx" [options]
// See --help and modules.txt.

using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;

Gbx.LZO = new MiniLZO();

if (args.Length > 0 && args[0] is "-h" or "--help")
{
    PrintHelp();
    return 0;
}

// ------------------------------------------------------------------ options
// The base map is optional: by default RPG_Modules_Base.Challenge.Gbx in the current folder (any capitalisation).
const string DefaultBase = "RPG_Modules_Base.Challenge.Gbx";
bool baseGiven = args.Length > 0 && !args[0].StartsWith('-');
var basePath = baseGiven ? args[0]
    : Directory.GetFiles(Directory.GetCurrentDirectory(), "*.gbx")
               .FirstOrDefault(f => Path.GetFileName(f).Equals(DefaultBase, StringComparison.OrdinalIgnoreCase));
if (basePath is null)
{
    Console.Error.WriteLine($"No base map given and {DefaultBase} not found in {Directory.GetCurrentDirectory()}. Use --help for usage.");
    return 1;
}
var baseDir = Path.GetDirectoryName(Path.GetFullPath(basePath))!;
string? modulesPath = null, outPath = null, newName = null, blocksPath = null, whitelistPath = null;
string? roofBlock = "StadiumCircuitBase";
int length = 30, cps = 3, tries = 50, maxFloors = int.MaxValue;
bool test = false, fit = false, maxMode = false, lengthSet = false;
int cpGapMin = 3, cpGapMax = 6;
bool cpsSet = false;
long maxBudget = 1_000_000;
int? seed = null, gridW = null, gridD = null;
(int X, int Z)? startCell = null;

try
{
    for (int i = baseGiven ? 1 : 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        switch (args[i].ToLowerInvariant())
        {
            case "-m": case "--modules": modulesPath = Next(); break;
            case "--blocks": blocksPath = Next(); break;
            case "-w": case "--whitelist": whitelistPath = Next(); break;
            case "-l": case "--length": length = int.Parse(Next()); lengthSet = true; break;
            case "-c": case "--cps": cps = int.Parse(Next()); cpsSet = true; break;
            case "--cp-gap":
                var gp = Next().Split('-');
                cpGapMin = int.Parse(gp[0]); cpGapMax = int.Parse(gp[^1]);
                if (cpGapMin < 1 || cpGapMax < cpGapMin) throw new ArgumentException("--cp-gap needs a range like 3-6");
                break;
            case "-s": case "--seed": seed = int.Parse(Next()); break;
            case "-f": case "--floors": maxFloors = int.Parse(Next()); break;
            case "-g": case "--grid":
                var wd = Next().ToLowerInvariant().Split('x', ',');
                gridW = int.Parse(wd[0]); gridD = int.Parse(wd[1]); break;
            case "--start":
                var sc = Next().Split(',');
                startCell = (int.Parse(sc[0]), int.Parse(sc[1])); break;
            case "--roof": roofBlock = Next(); break;
            case "--nr": case "--no-roof": roofBlock = null; break;
            case "-t": case "--test": test = true; break;
            case "--fit": fit = true; break;
            case "--max": maxMode = true; fit = true; break;
            case "--cp-every":
                var every = int.Parse(Next());
                if (every <= 0) { cpGapMin = cpGapMax = int.MaxValue; } else { cpGapMin = cpGapMax = every; }
                break;
            case "--budget": maxBudget = long.Parse(Next()); break;
            case "--tries": tries = int.Parse(Next()); break;
            case "-o": case "--out": outPath = Next(); break;
            case "--rn": case "--rename": newName = Next(); break;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

if (!test && !maxMode && cpsSet && length < cps + 2)
{
    Console.Error.WriteLine($"--length must be at least {cps + 2} (start + {cps} checkpoints + finish).");
    return 1;
}

modulesPath ??= FindFile("modules.txt");
blocksPath ??= FindFile("blocks.txt") ?? FindFile("block.txt");
whitelistPath ??= FindFile("Block_Whitelist.Challenge.Gbx") ?? FindFile("block whitelist.Challenge.Gbx");
if (modulesPath is null || !File.Exists(modulesPath))
{
    Console.Error.WriteLine("modules.txt not found (put it next to the base map or pass -m <file>).");
    return 1;
}

// Block footprints (x, z in blocks) from tmblocks' list. Needed to rotate blocks bigger than 1x1:
// a block's coordinate is the lowest corner of the area it covers.
var footprint = new Dictionary<string, (int X, int Y, int Z)>(StringComparer.Ordinal);
if (blocksPath is not null && File.Exists(blocksPath))
{
    foreach (var raw in File.ReadLines(blocksPath))
    {
        var p = raw.Split('#')[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 4 && int.TryParse(p[1], out var fx) && int.TryParse(p[2], out var fy) && int.TryParse(p[3], out var fz))
            footprint[p[0][(p[0].LastIndexOfAny(['\\', '/']) + 1)..]] = (fx, fy, fz);
    }
}
else Console.WriteLine("Warning: blocks.txt not found; blocks bigger than 1x1 may be misplaced in rotated modules.");

// Block types in the whitelist map are never removed from underneath a module.
var underWhitelist = new HashSet<string>(StringComparer.Ordinal);
if (whitelistPath is not null && File.Exists(whitelistPath))
{
    try
    {
        foreach (var b in Gbx.ParseNode<CGameCtnChallenge>(whitelistPath).GetBlocks())
            if (b.Flags != -1) underWhitelist.Add(b.Name);
    }
    catch (Exception ex) { Console.Error.WriteLine($"Could not read whitelist map: {ex.Message}"); return 2; }
}

// ------------------------------------------------------------------ base map + modules
Gbx<CGameCtnChallenge> gbx;
try { gbx = Gbx.Parse<CGameCtnChallenge>(basePath); }
catch (Exception ex) { Console.Error.WriteLine($"Could not read map: {ex.Message}"); return 2; }

var map = gbx.Node;
var size = map.Size;
var all = map.GetBlocks().Where(b => b.Flags != -1).ToList();
if (all.Count == 0) { Console.Error.WriteLine("Base map has no blocks."); return 2; }

List<Module> modules;
try { modules = ReadModules(modulesPath); }
catch (Exception ex) { Console.Error.WriteLine($"modules.txt: {ex.Message}"); return 1; }

// Modules written as x,z sit on the bottom layer. Its floor height = the most common lowest block
// of the columns inside module bodies (blocks hanging below a module are "under" blocks, not floor).
if (modules.Any(m => m.FloorY is null))
{
    var colMin = new Dictionary<(int, int), int>();
    foreach (var b in all)
    {
        if (!modules.Any(m => m.InBody(b.Coord.X, b.Coord.Z))) continue;
        var key = (b.Coord.X, b.Coord.Z);
        colMin[key] = Math.Min(colMin.GetValueOrDefault(key, int.MaxValue), b.Coord.Y);
    }
    if (colMin.Count == 0) { Console.Error.WriteLine("No blocks found inside the modules listed in modules.txt."); return 1; }
    int bottom = colMin.Values.GroupBy(y => y).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
    foreach (var m in modules) m.FloorY ??= bottom;
}
int floorY = modules.Min(m => m.FloorY!.Value); // floor 0 of the maze is built at this height

// Assign every block to a module: the highest layer at or below the block (under blocks are 1 below
// the floor), inside the module's body or within 1 block of it (side/under extensions).
int unassigned = 0;
foreach (var b in all)
{
    int x = b.Coord.X, y = b.Coord.Y, z = b.Coord.Z;
    var near = modules.Where(m => m.InMargin(x, z) && m.FloorY - 1 <= y).ToList();
    if (near.Count == 0) { unassigned++; continue; }
    int layer = near.Max(m => m.FloorY!.Value);
    near = near.Where(m => m.FloorY == layer).ToList();
    var owner = near.FirstOrDefault(m => m.InBody(x, z)) ?? near[0];
    int lx = x - owner.X, lz = z - owner.Z, ly = y - layer;
    // Side = outside the module's body; under = inside the body columns but below the floor.
    var ext = !owner.InBody(x, z) ? Ext.Side : ly < 0 ? Ext.Under : Ext.None;
    owner.Blocks.Add(new Tpl(b.Name, lx, ly, lz, (int)b.Direction, b.Flags, ext));
}

foreach (var m in modules)
{
    for (int cx = 0; cx < m.WC; cx++)
        for (int cz = 0; cz < m.DC; cz++)
            if (m.Blocks.Any(t => t.Ext == Ext.None && t.X / 3 == cx && t.Z / 3 == cz)) m.Cells.Add((cx, cz));
    if (m.Cells.Count == 0) { Console.Error.WriteLine($"Module {m.Id}: no blocks found in the base map."); return 1; }
    foreach (var o in m.Routes.SelectMany(r => new[] { r.In, r.Out }))
        if (o is Opening op && !m.Cells.Contains((op.CX, op.CZ)))
            { Console.Error.WriteLine($"Module {m.Id}: opening on cell {op.CX},{op.CZ}, but that cell has no blocks."); return 1; }
    m.MaxRelY = m.Blocks.Max(t => t.Y);
}

foreach (var kind in new[] { "start", "finish" })
    if (!modules.Any(m => m.Kind == kind)) { Console.Error.WriteLine($"modules.txt has no {kind} module."); return 1; }
if (!test && (cpsSet ? cps > 0 : cpGapMin < int.MaxValue) && !modules.Any(m => m.Kind == "checkpoint")) { Console.Error.WriteLine("modules.txt has no checkpoint module (use -c 0)."); return 1; }

// ------------------------------------------------------------------ grid
// --fit: build on a big grid (block coordinates go up to 255 in the file), then move the maze to the
// map corner and make the map just big enough (for TMUnlimiter, which allows bigger maps).
const int MaxCoord = 255;
int limX = fit ? MaxCoord + 1 : size.X, limY = fit ? MaxCoord + 1 : size.Y, limZ = fit ? MaxCoord + 1 : size.Z;
int W = gridW ?? (limX - 2) / 3, D = gridD ?? (limZ - 2) / 3;
if (W < 1 || D < 1 || W * 3 > limX || D * 3 > limZ)
{
    Console.Error.WriteLine($"Grid {W}x{D} doesn't fit a {limX}x{limZ} map (max {limX / 3}x{limZ / 3}).");
    return 1;
}
int ox = (limX - 3 * W) / 2, oz = (limZ - 3 * D) / 2;

// Highest floor a module may sit on: everything, including the roof above it, must stay in the map.
int TopFloor(Module m) => Math.Min(Math.Min((limY - 1 - floorY - m.MaxRelY) / 3, (limY - 1 - floorY) / 3 - m.Span),
                                   maxFloors - m.Span);

// ------------------------------------------------------------------ route search
var usedSeed = seed ?? Random.Shared.Next();
var rng = new Random(usedSeed);
// Checkpoint positions along the route: -c n spreads n evenly; by default one every 3-6 modules (--cp-gap).
HashSet<int> cpAt;
if (cpsSet) cpAt = Enumerable.Range(1, cps).Select(i => (int)Math.Round((double)length * i / (cps + 1))).ToHashSet();
else
{
    cpAt = [];
    int limit = maxMode ? (lengthSet ? length : 2000) : length - 1; // never on the finish
    for (long p = rng.Next(cpGapMin, (int)Math.Min(cpGapMax + 1L, int.MaxValue)); p < limit; p += rng.Next(cpGapMin, (int)Math.Min(cpGapMax + 1L, int.MaxValue)))
        cpAt.Add((int)p);
}
string KindAt(int i) => i == 0 ? "start" : i == length - 1 ? "finish" : cpAt.Contains(i) ? "checkpoint" : "normal";

// Test mode: one route through every module in modules.txt (repeats only where needed to connect them).
var uses = modules.ToDictionary(m => m, _ => 0);
int maxTestLength = 3 * modules.Count;
// Max mode: keep going as long as possible (checkpoints from cpAt); the finish is added
// where the longest finishable route ends.
int maxLength = maxMode && lengthSet ? length : 2000;
bool Allowed(Module m, int i)
{
    if (maxMode) return m.Kind == (i == 0 ? "start" : cpAt.Contains(i) ? "checkpoint" : "normal");
    if (!test) return m.Kind == KindAt(i);
    if (i == 0) return m.Kind == "start";
    if (m.Kind == "start") return false;
    if (m.Kind == "finish") return modules.All(x => x.Kind is "start" or "finish" || uses[x] > 0);
    return i < maxTestLength - 1;
}

var occupied = new HashSet<(int, int, int)>();
var route = new List<Step>();
long budget = 0;

List<(Module M, int R, int Floor, (int X, int Z) Origin, List<(int, int)> Cells, Opening? Out, bool Rev)>
    Options(int cx, int cz, (int Side, int Level, bool Narrow)? entry, Func<Module, bool> allowed)
{
    var options = new List<(Module M, int R, int Floor, (int X, int Z) Origin, List<(int, int)> Cells, Opening? Out, bool Rev)>();
    foreach (var m in modules.Where(allowed))
        foreach (var (inp, outp, rev) in m.Routes)
            for (int r = 0; r < 4; r++)
            {
                int floor;
                (int X, int Z) origin;
                if (entry is null)
                {
                    floor = 0; // the start sits on the bottom floor
                    var (fx, fz) = m.RotCell(m.Cells[0], r);
                    origin = (cx - fx, cz - fz);
                }
                else
                {
                    if (inp is not Opening a) continue;
                    var ra = m.RotOpening(a, r);
                    if (ra.Side != entry.Value.Side) continue;
                    if (ra.Narrow && !entry.Value.Narrow) continue; // a normal exit can't lead into a narrow entrance
                    floor = entry.Value.Level - ra.Up;
                    origin = (cx - ra.CX, cz - ra.CZ);
                }
                if (floor < 0 || floor > TopFloor(m)) continue;

                var cells = m.Cells.Select(c => m.RotCell(c, r)).Select(c => (origin.X + c.Item1, origin.Z + c.Item2)).ToList();
                bool ok = true;
                foreach (var (ax, az) in cells)
                {
                    if (ax < 0 || az < 0 || ax >= W || az >= D) { ok = false; break; }
                    int lo = m.NoBelow ? Math.Max(0, floor - 1) : floor, hi = floor + m.Span + (m.NoAbove ? 1 : 0);
                    for (int k = lo; k < hi && ok; k++) ok = !occupied.Contains((ax, az, k));
                    if (!ok) break;
                }
                if (!ok) continue;
                options.Add((m, r, floor, origin, cells, outp is Opening o ? m.RotOpening(o, r) : null, rev));
            }
    return options;
}

var best = new List<Step>(); // max mode: longest route found that ends in a finish

bool Place(int i, int cx, int cz, (int Side, int Level, bool Narrow)? entry)
{
    if (--budget < 0) return false;
    var options = Options(cx, cz, entry, m => Allowed(m, i));

    var ordered = test ? options.OrderBy(o => uses[o.M]).ThenBy(_ => rng.Next()) : options.OrderBy(_ => rng.Next());
    foreach (var (m, r, floor, origin, cells, outp, rev) in ordered)
    {
        // "nb" / "na" modules also reserve the floor directly below / above their cells.
        int low = m.NoBelow ? Math.Max(0, floor - 1) : floor, high = floor + m.Span + (m.NoAbove ? 1 : 0);
        foreach (var (ax, az) in cells)
            for (int k = low; k < high; k++) occupied.Add((ax, az, k));
        route.Add(new Step(m, r, origin.X, origin.Z, floor, cells, rev));
        uses[m]++;
        if (!maxMode && (test ? m.Kind == "finish" : i == length - 1)) return true;

        var o = outp!.Value;
        var (dx, dz) = Vec(o.Side);
        int nx = origin.X + o.CX + dx, nz = origin.Z + o.CZ + dz;
        var next = ((o.Side + 2) % 4, floor + o.Up, o.Narrow);

        if (maxMode)
        {
            // Could the route end here? Remember it if it's the longest so far.
            if (route.Count + 1 > best.Count)
            {
                var fin = Options(nx, nz, next, x => x.Kind == "finish");
                if (fin.Count > 0)
                {
                    var f = fin[rng.Next(fin.Count)];
                    best = [.. route, new Step(f.M, f.R, f.Origin.X, f.Origin.Z, f.Floor, f.Cells, f.Rev)];
                }
            }
            if (route.Count >= maxLength - 1) goto undo;
        }

        if (Place(i + 1, nx, nz, next)) return true;
        undo:

        route.RemoveAt(route.Count - 1);
        uses[m]--;
        foreach (var (ax, az) in cells)
            for (int k = low; k < high; k++) occupied.Remove((ax, az, k));
    }
    return false;
}

bool found = false;
if (maxMode)
{
    // Deep recursion: run on a thread with a big stack.
    var worker = new Thread(() =>
    {
        budget = maxBudget;
        var (sx, sz) = startCell ?? (W / 2, D / 2);
        Place(0, sx, sz, null);
    }, 1024 * 1024 * 1024);
    worker.Start();
    worker.Join();
    route = best;
    found = route.Count > 0;
}
else
for (int t = 0; t < tries && !found; t++)
{
    occupied.Clear(); route.Clear(); budget = 200_000;
    foreach (var m in modules) uses[m] = 0;
    var (sx, sz) = startCell ?? (fit ? (W / 2, D / 2) : (rng.Next(W), rng.Next(D)));
    found = Place(0, sx, sz, null);
}
if (!found)
{
    Console.Error.WriteLine(maxMode ? $"No finishable route found (seed {usedSeed}). Try another seed."
        : test
        ? $"No route through every module found (seed {usedSeed}). Try another seed or a bigger --grid."
        : $"No route of length {length} found (seed {usedSeed}). Try a shorter --length, fewer --cps or another seed.");
    return 3;
}

// Outer edges of the maze in grid cells.
int minCX = int.MaxValue, maxCX = int.MinValue, minCZ = int.MaxValue, maxCZ = int.MinValue;
foreach (var q in route)
    foreach (var (qx, qz) in q.Cells)
    {
        minCX = Math.Min(minCX, qx); maxCX = Math.Max(maxCX, qx);
        minCZ = Math.Min(minCZ, qz); maxCZ = Math.Max(maxCZ, qz);
    }
if (fit)
{
    // Leave 1 block around the maze for side extensions; never make the map smaller than the base map.
    ox = 1 - 3 * minCX; oz = 1 - 3 * minCZ;
    int topY = floorY + 3 * route.Max(q => q.Floor + q.M.Span) + 1;
    size = new Int3(Math.Max(size.X, 3 * (maxCX - minCX + 1) + 2), Math.Max(size.Y, topY + 1), Math.Max(size.Z, 3 * (maxCZ - minCZ + 1) + 2));
    map.Size = size;
}

// ------------------------------------------------------------------ build the map
// 1. module bodies
// 2. extensions: side blocks are dropped if they land in another module's cells (even in its air);
//    under blocks are only dropped if another module already has a block on that exact spot
// 3. roofs on empty spots above modules
const int RefBits = (1 << 15) | (1 << 17) | (1 << 18) | (1 << 19) | (1 << 20); // skins/special props: not copied

var volume = new Dictionary<Int3, int>(); // every coordinate inside a module's cells and floors -> route index
for (int s = 0; s < route.Count; s++)
{
    var st = route[s];
    foreach (var (ax, az) in st.Cells)
        for (int lx = 0; lx < 3; lx++)
            for (int lz = 0; lz < 3; lz++)
                for (int y = floorY + 3 * st.Floor; y < floorY + 3 * (st.Floor + st.M.Span); y++)
                    volume[new Int3(ox + 3 * ax + lx, y, oz + 3 * az + lz)] = s;
}

map.Blocks!.Clear();
var placedBy = new Dictionary<Int3, int>(); // coordinate -> route index of the module that placed a block there
int bodies = 0, exts = 0, extDropped = 0, outside = 0, roofs = 0;

foreach (var phaseExt in new[] { false, true })
{
    for (int s = 0; s < route.Count; s++)
    {
        var st = route[s];
        foreach (var t in st.M.Blocks)
        {
            if ((t.Ext != Ext.None) != phaseExt) continue;
            // Rotate the block's whole footprint and take its new lowest corner.
            var (fx, fy, fz) = footprint.GetValueOrDefault(t.Name, (1, 1, 1));
            if (t.Dir % 2 == 1) (fx, fz) = (fz, fx); // footprint as placed in the base map
            var (x1, z1) = st.M.RotPoint(t.X, t.Z, st.R);
            var (x2, z2) = st.M.RotPoint(t.X + fx - 1, t.Z + fz - 1, st.R);
            int rx = Math.Min(x1, x2), rz = Math.Min(z1, z2);
            var coord = new Int3(ox + 3 * st.OX + rx, floorY + 3 * st.Floor + t.Y, oz + 3 * st.OZ + rz);
            if (coord.X < 0 || coord.Y < 0 || coord.Z < 0 || coord.X >= size.X || coord.Y >= size.Y || coord.Z >= size.Z)
            { outside++; continue; }
            bool clash = placedBy.TryGetValue(coord, out var p) && p != s;
            if (t.Ext == Ext.Side && volume.TryGetValue(coord, out var v) && v != s) clash = true;
            // Under-blocks that are taller than 1 or whitelisted are never removed.
            if (t.Ext == Ext.Under && (fy > 1 || underWhitelist.Contains(t.Name))) clash = false;
            if (clash) { extDropped++; continue; }

            // Checkpoint modules driven against their arrow get the checkpoint turned around.
            int turn = st.Rev && t.Name.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase) ? 2 : 0;

            map.PlaceBlock(new CGameCtnBlock
            {
                Name = t.Name,
                Direction = (Direction)((t.Dir + st.R + turn) % 4),
                Coord = coord,
                Flags = t.Flags & ~RefBits,
            });
            placedBy.TryAdd(coord, s);
            if (t.Ext != Ext.None) exts++; else bodies++;
        }
    }
}

if (roofBlock is not null)
{
    for (int s = 0; s < route.Count; s++)
    {
        var st = route[s];
        int y = floorY + 3 * (st.Floor + st.M.Span);
        if (y >= size.Y) continue;
        foreach (var (ax, az) in st.Cells)
            for (int lx = 0; lx < 3; lx++)
                for (int lz = 0; lz < 3; lz++)
                {
                    var coord = new Int3(ox + 3 * ax + lx, y, oz + 3 * az + lz);
                    if (!placedBy.TryAdd(coord, s)) continue;
                    map.PlaceBlock(roofBlock, coord, Direction.North);
                    roofs++;
                }
    }
}

// Name: --rn, else maze1, maze2, ... (first number without an existing file). File is named after the map.
// Saved in an "Output" folder next to the base map if there is one.
var outDir = Directory.Exists(Path.Combine(baseDir, "Output")) ? Path.Combine(baseDir, "Output") : baseDir;
string FreeFile(string stem, bool numberFirst)
{
    if (!numberFirst && !File.Exists(Path.Combine(outDir, stem + ".Challenge.Gbx"))) return stem;
    for (int n = numberFirst ? 1 : 2; ; n++)
    {
        var candidate = numberFirst ? $"{stem}{n}" : $"{stem} {n}";
        if (!File.Exists(Path.Combine(outDir, candidate + ".Challenge.Gbx"))) return candidate;
    }
}
var fileStem = FreeFile(newName is null ? "maze" : SafeFileName(newName), numberFirst: newName is null);
map.MapName = newName ?? fileStem;
map.MapUid = NewUid(); // so the maze doesn't clash with the base map in the game's track list

outPath ??= Path.Combine(outDir, fileStem + ".Challenge.Gbx");
gbx.Save(outPath);

// ------------------------------------------------------------------ report
int floors = route.Max(s => s.Floor + s.M.Span);
int cpCount = route.Where(s => s.M.Kind == "checkpoint").Count();
Console.WriteLine($"Maze: {route.Count} modules, {cpCount} checkpoint(s), {floors} floor(s), grid {W}x{D}, seed {usedSeed}");
if (underWhitelist.Count > 0) Console.WriteLine($"Whitelist: {underWhitelist.Count} block type(s) kept under modules ({string.Join(", ", underWhitelist)}).");
if (fit) Console.WriteLine($"Map size set to {size.X}x{size.Y}x{size.Z} (needs TMUnlimiter if bigger than 32).");
if (test) Console.WriteLine($"Test mode: all {modules.Count} modules used, {route.Count - modules.Count} repeat(s) to connect them.");
Console.WriteLine($"Blocks: {bodies} module, {exts} extension ({extDropped} dropped for overlapping), {roofs} roof"
                  + (outside > 0 ? $", {outside} outside the map skipped" : "")
                  + (unassigned > 0 ? $". {unassigned} base-map block(s) not part of any module" : ""));
for (int f = 0; f < floors; f++)
{
    Console.WriteLine($"\nFloor {f}:   (rows = X, high X at top; columns = Z)");
    for (int cx = maxCX; cx >= minCX; cx--)
    {
        var line = new char[maxCZ - minCZ + 1];
        for (int cz = minCZ; cz <= maxCZ; cz++)
        {
            var hit = route.FirstOrDefault(q => q.Cells.Contains((cx, cz)) && f >= q.Floor && f < q.Floor + q.M.Span);
            line[cz - minCZ] = hit is null ? '.' : hit.M.Kind switch
            {
                "start" => 'S', "finish" => 'F', "checkpoint" => 'C',
                _ => hit.M.Span > 1 ? (f == hit.Floor ? 'v' : '^') : '#',
            };
        }
        Console.WriteLine("  " + string.Join(' ', line));
    }
}
Console.WriteLine($"\nSaved: {outPath}");
return 0;

// ------------------------------------------------------------------ helpers

string? FindFile(string name)
{
    foreach (var dir in new[] { baseDir, Directory.GetCurrentDirectory() })
    {
        var p = Path.Combine(dir, name);
        if (File.Exists(p)) return p;
    }
    return null;
}

// Sides: 0 = +x, 1 = +z, 2 = -x, 3 = -z. One rotation step turns +x into +z, matching Direction + 1.
static (int, int) Vec(int side) => side switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };

static List<Module> ReadModules(string path)
{
    var list = new List<Module>();
    int lineNo = 0;
    foreach (var raw in File.ReadLines(path))
    {
        lineNo++;
        var line = raw.Split('#')[0].Trim();
        if (line.Length == 0) continue;
        // Allow a space after "in:", "out:", "both:" and "cp:" by joining it with the next word.
        var rawParts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var joined = new List<string>();
        for (int k = 0; k < rawParts.Length; k++)
            joined.Add(rawParts[k].EndsWith(':') && k + 1 < rawParts.Length ? rawParts[k] + rawParts[++k] : rawParts[k]);
        var parts = joined.ToArray();
        if (parts.Length < 3) throw new FormatException($"line {lineNo}: expected <x>,[<y>,]<z> [cells] <kind> <openings>");

        var xz = parts[0].Split(',');
        if (xz.Length is not (2 or 3)) throw new FormatException($"line {lineNo}: expected <x>,<z> or <x>,<y>,<z>, got '{parts[0]}'");
        int idx = 1, wc = 1, dc = 1;
        if (char.IsDigit(parts[1][0]) && parts[1].Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            var cd = parts[1].ToLowerInvariant().Split('x');
            wc = int.Parse(cd[0]); dc = int.Parse(cd[1]); idx = 2;
        }
        var kind = parts[idx].ToLowerInvariant();
        if (kind is not ("start" or "finish" or "checkpoint" or "normal"))
            throw new FormatException($"line {lineNo}: unknown kind '{parts[idx]}'");

        Opening? inp = null, outp = null;
        var routes = new List<(Opening? In, Opening? Out, bool Rev)>();
        bool noBelow = false, noAbove = false;
        Opening? cpFaces = null;
        foreach (var spec in parts.Skip(idx + 1))
        {
            if (spec.Equals("nb", StringComparison.OrdinalIgnoreCase)) { noBelow = true; continue; }
            if (spec.Equals("na", StringComparison.OrdinalIgnoreCase)) { noAbove = true; continue; }
            var kv = spec.Split(':', 2);
            if (kv.Length != 2) throw new FormatException($"line {lineNo}: bad opening '{spec}'");
            switch (kv[0].ToLowerInvariant())
            {
                case "in": inp = ParseOpening(kv[1], wc, dc, lineNo); break;
                case "out": outp = ParseOpening(kv[1], wc, dc, lineNo); break;
                case "cp": cpFaces = ParseOpening(kv[1], wc, dc, lineNo); break;
                case "both":
                    var ab = kv[1].Split(',');
                    if (ab.Length != 2) throw new FormatException($"line {lineNo}: both: needs two sides");
                    Opening a = ParseOpening(ab[0], wc, dc, lineNo), b = ParseOpening(ab[1], wc, dc, lineNo);
                    routes.Add((a, b, false)); routes.Add((b, a, false));
                    break;
                default: throw new FormatException($"line {lineNo}: unknown '{kv[0]}' (use in/out/both)");
            }
        }
        if (kind == "start") routes = [(null, outp ?? throw new FormatException($"line {lineNo}: start needs out:"), false)];
        else if (kind == "finish") routes = [(inp ?? throw new FormatException($"line {lineNo}: finish needs in:"), null, false)];
        else if (inp is not null || outp is not null)
        {
            if (inp is null || outp is null) throw new FormatException($"line {lineNo}: needs both in: and out:");
            routes.Add((inp, outp, false));
        }
        if (routes.Count == 0) throw new FormatException($"line {lineNo}: no openings");

        // cp:<side> = the side the checkpoint faces. Driving out through any other side means the
        // module is used against its arrow, so the checkpoint gets turned around.
        if (kind == "checkpoint")
        {
            if (cpFaces is null)
            {
                if (routes.Count > 1) throw new FormatException($"line {lineNo}: two-way checkpoint needs cp:<side> (the side the checkpoint faces)");
                cpFaces = routes[0].Out ?? throw new FormatException($"line {lineNo}: checkpoint needs an out: side");
            }
            Opening f = cpFaces.Value;
            if (!routes.Any(r => r.Out is Opening o && Same(o, f)))
                throw new FormatException($"line {lineNo}: cp: side is not one of the module's openings");
            routes = routes.Select(r => (r.In, r.Out, r.Out is Opening o && !Same(o, f))).ToList();
        }
        else if (cpFaces is not null) throw new FormatException($"line {lineNo}: cp: is only for checkpoint modules");

        int span = 1 + routes.SelectMany(r => new[] { r.In, r.Out }).Max(o => o?.Up ?? 0);
        int mx = int.Parse(xz[0]), mz = int.Parse(xz[^1]);
        int? my = xz.Length == 3 ? int.Parse(xz[1]) : null;
        list.Add(new Module(parts[0], mx, mz, wc, dc, kind, routes, span) { NoBelow = noBelow, NoAbove = noAbove, FloorY = my });
    }
    return list;
}

static bool Same(Opening a, Opening b) => a.CX == b.CX && a.CZ == b.CZ && a.Side == b.Side;

// "<side>[@n][^][~]" -> which cell of the module the opening is on, the side, whether it's one floor up
// (^) and whether it's narrow (~).
static Opening ParseOpening(string s, int wc, int dc, int lineNo)
{
    int up = s.Contains('^') ? 1 : 0;
    bool narrow = s.Contains('~');
    s = s.Replace("^", "").Replace("~", "");
    int n = 0;
    var at = s.IndexOf('@');
    if (at >= 0) { n = int.Parse(s[(at + 1)..]); s = s[..at]; }
    int side = s.ToLowerInvariant() switch
    {
        "+x" => 0, "+z" => 1, "-x" => 2, "-z" => 3,
        _ => throw new FormatException($"line {lineNo}: bad side '{s}' (use +x, -x, +z, -z)"),
    };
    var (cx, cz) = side switch { 0 => (wc - 1, n), 2 => (0, n), 1 => (n, dc - 1), _ => (n, 0) };
    if (cx < 0 || cz < 0 || cx >= wc || cz >= dc)
    {
        int max = (side is 0 or 2 ? dc : wc) - 1;
        throw new FormatException($"line {lineNo}: {s}@{n} is outside the module. A {wc}x{dc} module's {s} side has cells @0" +
            (max > 0 ? $" to @{max}" : " only") + (side is 0 or 2 ? " (counted along Z)" : " (counted along X)"));
    }
    return new Opening(cx, cz, side, up, narrow);
}

static string NewUid()
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Range(0, 27).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
}

static string SafeFileName(string name)
{
    var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
    return clean.Length > 0 ? clean : "maze";
}

static void PrintHelp() => Console.WriteLine("""
tmmaze - build a random single-route maze from modules

Usage:  dotnet run tmmaze.cs -- [base map] [options]

The base map holds the modules (default: RPG_Modules_Base.Challenge.Gbx in the current folder);
modules.txt says where each one is and how the road goes through it (see the comments at its top).
The maze is saved as maze1, maze2, ... (first free number) or as --rn's name, in an "Output" folder
next to the base map if there is one, else next to the base map.

ROUTE
  -l, --length n            Modules in the route, incl. start and finish (default 30)
  -s, --seed n              Same seed = same maze (the seed used is always printed)
      --start x,z           Grid cell for the start (default: random)
      --tries n             Start positions to try before giving up (default 50)

CHECKPOINTS
      --cp-gap a-b          A checkpoint every a to b modules, picked at random (default 3-6)
      --cp-every n          A checkpoint exactly every n modules (0 = no checkpoints)
  -c, --cps n               Exactly n checkpoints, spread evenly along the route

SIZE
  -g, --grid WxD            Grid size in 3x3 cells (default: as big as the map allows, 10x10)
  -f, --floors n            Max number of floors, 3 blocks each (default: as many as fit)
      --fit                 Ignore the map size: build freely, then size the map to fit the maze
                            (needs TMUnlimiter for maps bigger than 32; coordinates up to 255)
      --max                 As long a maze as possible (turns on --fit); -l caps it (default 2000)
      --budget n            --max: placement attempts to search (default 1000000)

MODES
  -t, --test                One route through every module in modules.txt (ignores -l and checkpoints)

BUILDING
      --roof <block>        Block covering open spots above modules (default StadiumCircuitBase)
      --nr, --no-roof       Don't add roofs
  -w, --whitelist <map>     Block types in this map are never removed from under a module
                            (default: Block_Whitelist.Challenge.Gbx)
      --blocks <file>       Block sizes from tmblocks, for rotating big blocks (default: blocks.txt)
  -m, --modules <file>      Module list (default: modules.txt)
                            (files default to the base map's folder, then the current folder)

OUTPUT
  -o, --out <path>          Output file (default: mazeN.Challenge.Gbx in Output\ or next to the base map)
      --rn, --rename <name> Map name, also used as the file name (a number is added if it's taken)
  -h, --help                Show this help

EXAMPLES
  dotnet run tmmaze.cs
  dotnet run tmmaze.cs -- -l 60 --cp-gap 4-8 -s 1234
  dotnet run tmmaze.cs -- -l 40 -f 1 --nr --rn "Flat maze"
  dotnet run tmmaze.cs -- -t --rn "Module test"
  dotnet run tmmaze.cs -- --max -l 500
""");

record struct Opening(int CX, int CZ, int Side, int Up, bool Narrow);
enum Ext { None, Side, Under }
record Tpl(string Name, int X, int Y, int Z, int Dir, int Flags, Ext Ext);
record Step(Module M, int R, int OX, int OZ, int Floor, List<(int, int)> Cells, bool Rev);

class Module(string id, int x, int z, int wc, int dc, string kind, List<(Opening? In, Opening? Out, bool Rev)> routes, int span)
{
    public string Id = id, Kind = kind;
    public int X = x, Z = z, WC = wc, DC = dc, Span = span, MaxRelY;
    public bool NoBelow, NoAbove;
    public int? FloorY; // height of the module's floor in the base map
    public List<(Opening? In, Opening? Out, bool Rev)> Routes = routes;
    public List<Tpl> Blocks = [];
    public List<(int, int)> Cells = [];

    public bool InBody(int bx, int bz) => bx >= X && bx < X + 3 * WC && bz >= Z && bz < Z + 3 * DC;
    public bool InMargin(int bx, int bz) => bx >= X - 1 && bx <= X + 3 * WC && bz >= Z - 1 && bz <= Z + 3 * DC;

    // Rotate a point inside a w x d box by r quarter turns (+x -> +z); the box swaps to d x w each turn.
    static (int, int) Rot(int px, int pz, int w, int d, int r)
    {
        for (int i = 0; i < r; i++) (px, pz, w, d) = (d - 1 - pz, px, d, w);
        return (px, pz);
    }
    public (int, int) RotPoint(int px, int pz, int r) => Rot(px, pz, 3 * WC, 3 * DC, r);
    public (int, int) RotCell((int, int) c, int r) => Rot(c.Item1, c.Item2, WC, DC, r);
    public Opening RotOpening(Opening o, int r)
    {
        var (cx, cz) = RotCell((o.CX, o.CZ), r);
        return o with { CX = cx, CZ = cz, Side = (o.Side + r) % 4 };
    }
}
