#:package GBX.NET@2.*
#:package GBX.NET.LZO@2.*
#:package GBX.NET.PAK@2.*

// tmblocks — list every block in TMNF's game files (.pak) with its size in blocks.
// Run (.NET 10 SDK):  dotnet run tmblocks.cs -- "<TMNF install>\GameData" [-o blocks.txt] [--all-files]
// Output works directly as a pool/size file for tmedit:  shuffle <map> --by-size --pool blocks.txt

using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
using GBX.NET.PAK;

Gbx.LZO = new MiniLZO();

if (args.Length < 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("""
        Usage: dotnet run tmblocks.cs -- "<TMNF install folder>" [-o blocks.txt] [--all-files]
          e.g. "C:\Program Files (x86)\TmNationsForever"  (or its Packs / GameData folder)
          --all-files   also print every file name in the paks to pakfiles.txt (for exploring)
        """);
    return 1;
}

var gameData = args[0];
var outPath = "blocks.txt";
var dumpAll = false;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] is "-o" && i + 1 < args.Length) outPath = args[++i];
    else if (args[i] is "--all-files") dumpAll = true;
}

// TMNF keeps its .pak files (and packlist.dat) in "Packs", next to "GameData".
// Accept the install folder, GameData or Packs — search the folder and its parent's Packs.
var searchDirs = new List<string> { gameData };
var parent = Directory.GetParent(Path.GetFullPath(gameData))?.FullName;
if (parent is not null && Directory.Exists(Path.Combine(parent, "Packs"))) searchDirs.Add(Path.Combine(parent, "Packs"));

var pakPaths = searchDirs
    .SelectMany(d => Directory.GetFiles(d, "*.pak", SearchOption.AllDirectories))
    .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

if (pakPaths.Count == 0)
{
    Console.Error.WriteLine($"No .pak files found in {string.Join(" or ", searchDirs)}. Point it at the TMNF install folder or its Packs folder.");
    return 2;
}

// packlist.dat holds the decryption keys for the .pak files.
var keys = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
foreach (var dir in pakPaths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase))
{
    var packlist = Path.Combine(dir, PakList.FileName);
    if (!File.Exists(packlist)) continue;
    foreach (var (name, key) in (await PakList.ParseAsync(packlist, PakListGame.TM)).ToKeyInfoDictionary())
        keys[name] = key;
    Console.WriteLine($"Loaded keys from {packlist}");
}
if (keys.Count == 0)
    Console.WriteLine($"Warning: no {PakList.FileName} found next to the paks; encrypted paks will fail.");

var results = new List<(string Name, string Env, string Kind, Int3? Ground, Int3? Air, bool Pillar, int? Variants, string Note)>();
var allFiles = new List<string>();

foreach (var pakPath in pakPaths)
{
    var pakId = Path.GetFileNameWithoutExtension(pakPath);
    Pak pak;
    try
    {
        pak = keys.TryGetValue(pakId, out var key) ? await Pak.ParseAsync(pakPath, key) : await Pak.ParseAsync(pakPath);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Skipping {pakId}.pak: {ex.Message}");
        continue;
    }

    await using (pak)
    {
        int found = 0;
        foreach (var file in pak.Files.Values)
        {
            var path = Path.Combine(file.FolderPath, file.Name);
            if (dumpAll) allFiles.Add($"{pakId}: {path}");

            // Block infos live under ...BlockInfo... folders and use .TMEDxxx.Gbx extensions in TMF.
            bool looksLikeBlock = path.Contains("BlockInfo", StringComparison.OrdinalIgnoreCase)
                               || file.Name.Contains(".TMED", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeBlock || !file.Name.EndsWith(".Gbx", StringComparison.OrdinalIgnoreCase)) continue;

            // PakFile.Name can include sub-folders (e.g. "ConstructionBlockInfoClassic\\StadiumX.TMEDClassic.Gbx");
            // maps reference blocks by the bare name only.
            var bare = file.Name.Replace('/', '\\');
            bare = bare[(bare.LastIndexOf('\\') + 1)..];
            var blockName = bare[..bare.IndexOf('.')];
            var env = FirstWord(blockName);

            try
            {
                var gbx = await pak.OpenGbxFileAsync(file, importExternalNodesFromRefTable: true);
                if (gbx.Node is not CGameCtnBlockInfo info) continue;

                // In TMF, Ground/AirMobils are indexed [variant][subvariant], so the outer length = variant count.
                int? variants = Math.Max(info.GroundMobils?.Length ?? 0, info.AirMobils?.Length ?? 0) is > 0 and var vc ? vc : null;
                results.Add((blockName, env, info.GetType().Name.Replace("CGameCtnBlockInfo", ""),
                    SizeOf(info.GroundBlockUnitInfos), SizeOf(info.AirBlockUnitInfos), info.IsPillar, variants, path));
                found++;
            }
            catch (Exception ex)
            {
                // Still list it — the name is valid even if the details couldn't be read.
                results.Add((blockName, env, "?", null, null, false, null, $"{path} (unreadable: {ex.GetType().Name})"));
                found++;
            }
        }
        Console.WriteLine($"{pakId}.pak: {found} block(s)");
    }
}

if (dumpAll)
{
    File.WriteAllLines("pakfiles.txt", allFiles);
    Console.WriteLine($"Wrote {allFiles.Count} file names to pakfiles.txt");
}

if (results.Count == 0)
{
    Console.Error.WriteLine("No block infos found. Try --all-files and check pakfiles.txt for where blocks are stored.");
    return 2;
}

// One line per block: "Name x y z   # details". Size = the larger of the ground/air footprints.
var unique = results.GroupBy(r => r.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
var width = unique.Max(r => r.Name.Length) + 2;
var lines = new List<string>
{
    "# TMNF blocks read from the game files (" + gameData + ")",
    "# Format: Name x y z variants placement  (size in blocks, number of shape variants, G=ground A=air; ? = couldn't be read)",
    "# Usable as: tmedit shuffle <map> --by-size --pool blocks.txt",
};

foreach (var env in unique.GroupBy(r => r.Env).OrderByDescending(g => g.Count()))
{
    lines.Add("");
    lines.Add($"# ===== {env.Key} ({env.Count()} blocks) =====");
    foreach (var r in env.OrderBy(r => r.Name, StringComparer.Ordinal))
    {
        var size = Max(r.Ground, r.Air);
        var placement = (r.Ground is null ? "" : "G") + (r.Air is null ? "" : "A");
        var sizeText = (size is Int3 s ? $"{s.X} {s.Y} {s.Z}" : "? ? ?") + " " + (r.Variants?.ToString() ?? "?")
                     + " " + (placement.Length > 0 ? placement : "?");
        var details = $"{r.Kind}{(r.Pillar ? ", pillar" : "")}; ground {Fmt(r.Ground)}, air {Fmt(r.Air)}";
        lines.Add($"{r.Name.PadRight(width)}{sizeText,-15}# {details}");
    }
}

File.WriteAllLines(outPath, lines);
Console.WriteLine($"Wrote {unique.Count} blocks to {Path.GetFullPath(outPath)}");
return 0;

// Size of a block = bounding box of its block units' offsets.
static Int3? SizeOf(NodeRefList<CGameCtnBlockUnitInfo>? units)
{
    if (units is null || units.Count == 0) return null;
    int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
    int read = 0;
    for (int i = 0; i < units.Count; i++)
    {
        CGameCtnBlockUnitInfo? u;
        try { u = units[i]; } catch { continue; }
        if (u is null) continue;
        var o = u.RelativeOffset;
        minX = Math.Min(minX, o.X); minY = Math.Min(minY, o.Y); minZ = Math.Min(minZ, o.Z);
        maxX = Math.Max(maxX, o.X); maxY = Math.Max(maxY, o.Y); maxZ = Math.Max(maxZ, o.Z);
        read++;
    }
    return read == 0 ? null : new Int3(maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1);
}

static Int3? Max(Int3? a, Int3? b) => (a, b) switch
{
    (null, null) => null,
    (Int3 x, null) => x,
    (null, Int3 y) => y,
    (Int3 x, Int3 y) => new Int3(Math.Max(x.X, y.X), Math.Max(x.Y, y.Y), Math.Max(x.Z, y.Z)),
};

static string Fmt(Int3? v) => v is Int3 s ? $"{s.X}x{s.Y}x{s.Z}" : "-";

static string FirstWord(string s)
{
    int i = 1;
    while (i < s.Length && !char.IsUpper(s[i])) i++;
    return s[..i];
}
