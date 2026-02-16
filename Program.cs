using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks.Dataflow;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.MwFoundations;
using GBX.NET.LZO;
using GBX.NET.ZLib;
using ManiaAPI.NadeoAPI;

Gbx.LZO = new Lzo();
Gbx.ZLib = new ZLib();

var rootCmd = new RootCommand("TM2020 Replay Validator");
var shouldDownloadOpt = new Option<bool>("--download-missing-maps");
var additionalMapsStorage = new Option<string>("--additional-maps");
var prettyJsonOutputOpt = new Option<bool>("--pretty-output");
var verboseStderrOpt = new Option<bool>("--verbose-stderr");
var reportProgressOpt = new Option<bool>("--report-progress");
var dockerVolumeOpt = new Option<string>("--docker-volume")
{
    DefaultValueFactory = _ => "msm_data"
};
var replaysFolderArg = new Argument<string>("Replays folder");

Command validateDirCmd = new("validate-dir", "Validate all replays in directory") { };
validateDirCmd.Options.Add(shouldDownloadOpt);
validateDirCmd.Arguments.Add(replaysFolderArg);
validateDirCmd.Options.Add(dockerVolumeOpt);
validateDirCmd.Options.Add(additionalMapsStorage);
validateDirCmd.Options.Add(prettyJsonOutputOpt);
validateDirCmd.Options.Add(verboseStderrOpt);
validateDirCmd.Options.Add(reportProgressOpt);

bool VerboseStdErr;
bool ReportProgress;

validateDirCmd.SetAction(async (parseResult, cancellationToken) =>
{
    VerboseStdErr = parseResult.GetRequiredValue(verboseStderrOpt);
    ReportProgress = parseResult.GetRequiredValue(reportProgressOpt);
    var report = await Execute(new ValidateFolderOpts(
        parseResult.GetValue(shouldDownloadOpt),
        Environment.GetEnvironmentVariable("NADEO_LOGIN"),
        Environment.GetEnvironmentVariable("NADEO_PASSWORD"),
        parseResult.GetRequiredValue(replaysFolderArg),
        DockerVolume: parseResult.GetRequiredValue(dockerVolumeOpt),
        AdditionalMapsFolder: parseResult.GetValue(additionalMapsStorage)
    ), CancellationToken.None);

    var serOpts = new JsonSerializerOptions();
    serOpts.WriteIndented = parseResult.GetRequiredValue(prettyJsonOutputOpt);
    Console.WriteLine(JsonSerializer.Serialize(report, serOpts));
});

var mapIdArg = new Argument<string>("Map Id");
Option<int> topRecordsCntOpt = new("--max-records")
{
    DefaultValueFactory = _ => 10
};
Command validateMapRecordsCmd = new("validate-map", "Validate all replays in directory") { };
validateMapRecordsCmd.Arguments.Add(mapIdArg);
validateMapRecordsCmd.Options.Add(topRecordsCntOpt);
validateMapRecordsCmd.Options.Add(dockerVolumeOpt);
validateMapRecordsCmd.Options.Add(additionalMapsStorage);
validateMapRecordsCmd.Options.Add(prettyJsonOutputOpt);
validateMapRecordsCmd.Options.Add(reportProgressOpt);

validateMapRecordsCmd.SetAction(async (parseResult, cancellationToken) =>
{
    VerboseStdErr = parseResult.GetRequiredValue(verboseStderrOpt);
    ReportProgress = parseResult.GetRequiredValue(reportProgressOpt);
    var report = await ExecuteMapValidate(
        new ValidateOnlineMapOpts(
            parseResult.GetRequiredValue(mapIdArg),
            parseResult.GetRequiredValue(topRecordsCntOpt),
            Environment.GetEnvironmentVariable("NADEO_LOGIN")!,
            Environment.GetEnvironmentVariable("NADEO_PASSWORD")!,
            parseResult.GetRequiredValue(dockerVolumeOpt)
        ),
        CancellationToken.None
    );

    var serOpts = new JsonSerializerOptions();
    serOpts.WriteIndented = parseResult.GetRequiredValue(prettyJsonOutputOpt);
    Console.WriteLine(JsonSerializer.Serialize(report, serOpts));
});

rootCmd.Subcommands.Add(validateMapRecordsCmd);
rootCmd.Subcommands.Add(validateDirCmd);

return rootCmd.Parse(args).Invoke();

IEnumerable<string> EnumerateFiles(string? path, bool recursive)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        yield break;
    }

    if (File.Exists(path))
    {
        yield return path;
        yield break;
    }

    if (!Directory.Exists(path))
        yield break;

    var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    foreach (var file in Directory.EnumerateFiles(path, "*", opt))
        yield return file;
}

async Task<Dictionary<int, MSMValidationResult?>> ExecuteMapValidate(ValidateOnlineMapOpts opts,
    CancellationToken cancellationToken)
{
    using var tmp = new TempDir();

    var nsLive = new NadeoLiveServices();
    await nsLive.AuthorizeAsync(opts.NadeoLogin, opts.NadeoPassword, AuthorizationMethod.DedicatedServer);

    var mapInfo = await nsLive.GetMapInfoAsync(opts.MapUid);

    if (mapInfo is null)
    {
        throw new InvalidOperationException("Map not found");
    }

    var leaderboard = await nsLive.GetTopLeaderboardAsync(opts.MapUid, length: opts.RecordsCnt);
    var accounts = new Dictionary<Guid, int>();
    foreach (var record in leaderboard.Top.Top)
    {
        accounts[record.AccountId] = record.Position;
    }

    var ns = new NadeoServices();
    await ns.AuthorizeAsync(opts.NadeoLogin, opts.NadeoPassword, AuthorizationMethod.DedicatedServer);

    var replays = new Dictionary<string, int>();

    foreach (var record in await ns.GetMapRecordsAsync(accounts.Keys.AsEnumerable(), mapInfo.MapId))
    {
        var fPath = Path.Combine(tmp.Path, Guid.NewGuid() + ".Ghost.Gbx");
        await DownloadFile(record.Url, fPath, cancellationToken);
        replays[fPath] = accounts[record.AccountId];
    }

    var validationReport = await Execute(new ValidateFolderOpts(
        true,
        opts.NadeoLogin,
        opts.NadeoPassword,
        tmp.Path,
        opts.DockerVolume,
        null
    ), cancellationToken);

    var report = new Dictionary<int, MSMValidationResult?>();
    foreach (var (vPath, validation) in validationReport)
    {
        report[replays[vPath]] = validation;
    }

    return report;
}

async Task DownloadFile(string url, string outPath, CancellationToken cancellationToken)
{
    var proc = new Process()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "wget",
            Arguments = $""" "{url}" -U "unbeaten-ats project; teggot@proton.me" -O {outPath} """,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    proc.Start();

    await Task.WhenAll(
        proc.WaitForExitAsync(cancellationToken),
        DrainStream(proc.StandardOutput, cancellationToken),
        DrainStream(proc.StandardError, cancellationToken)
    );
}

const string NadeoServerHost = "https://nadeo-download.cdn.ubi.com/trackmania";
const string ManiaPlanetServerHost = "http://files.v04.maniaplanet.com/server";

ServerVersion GetServerVersion(CGameCtnGhost ghost)
{
    var latestServerVersion = new ServerVersion("Latest", ManiaPlanetServerHost);

    if (string.IsNullOrWhiteSpace(ghost.Validate_ExeVersion))
    {
        return latestServerVersion;
    }

    var matchExeVersion = Util.ExeVersionRegex().Match(ghost.Validate_ExeVersion);

    if (!DateTimeOffset.TryParseExact(
            matchExeVersion.Groups[2].Value,
            "yyyy-MM-dd_HH_mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var exeDate))
    {
        return latestServerVersion;
    }

    if (exeDate < new DateTimeOffset(2021, 6, 9, 0, 0, 0, TimeSpan.Zero))
    {
        return new ServerVersion("2021-05-31", ManiaPlanetServerHost);
    }

    if (exeDate < new DateTimeOffset(2021, 9, 30, 0, 0, 0, TimeSpan.Zero))
    {
        return new ServerVersion("2021-07-07", ManiaPlanetServerHost);
    }

    if (exeDate < new DateTimeOffset(2021, 12, 14, 0, 0, 0, TimeSpan.Zero))
    {
        return new ServerVersion("2021-09-29", ManiaPlanetServerHost);
    }

    if (exeDate < new DateTimeOffset(2022, 9, 7, 0, 0, 0, TimeSpan.Zero))
    {
        return new ServerVersion("2022-06-21", ManiaPlanetServerHost);
    }

    if (exeDate < new DateTimeOffset(2022, 9, 9, 0, 0, 0, TimeSpan.Zero))
    {
        return new ServerVersion("2022-09-06b", NadeoServerHost);
    }

    if (exeDate < new DateTimeOffset(2022, 9, 29, 0, 0, 0, TimeSpan.Zero))
    {
        return new ServerVersion("2022-09-08b", NadeoServerHost);
    }

    if (exeDate < new DateTimeOffset(2023, 07, 3, 0, 0, 0, TimeSpan.Zero))
    {
        return new ServerVersion("2023-05-04", NadeoServerHost);
    }

    return latestServerVersion;
}

async Task<Dictionary<string, MSMValidationResult>> Execute(ValidateFolderOpts opts,
    CancellationToken cancellationToken)
{
    Dictionary<string, string> overrides = new();
    try
    {
        var x = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(opts.ReplaysFolder, "/mappings.json")));
        if (x is not null) overrides = x;
    }
    catch
    {
    }

    var ghosts = new Dictionary<string, (ServerVersion, string)>();
    var requiredMaps = new HashSet<string>();
    var availableMaps = new Dictionary<string, string>();

    using var tmp = new TempDir();

    Directory.CreateDirectory(Path.Combine(tmp.Path, "Replays"));
    Directory.CreateDirectory(Path.Combine(tmp.Path, "Maps"));

    void ProcessGhost(string file, CGameCtnGhost ghost, bool isGhost)
    {
        if (ghost.Validate_ChallengeUid is null)
        {
            Console.Error.WriteLine($"Found ghost without mapUid inside of {file} replay");
        }
        else
        {
            var fileName = Guid.NewGuid() + (isGhost ? ".Ghost.Gbx" : ".Replay.Gbx");
            var serverVersion = GetServerVersion(ghost);
            var outDir = Path.Combine(tmp.Path, "Replays", serverVersion.Version);
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, fileName);
            if (overrides.ContainsKey(fileName))
            {
                ghost.Validate_ChallengeUid = overrides[fileName];
                ghost.Save(outPath);
            }
            else
            {
                File.Copy(file, outPath);
            }

            requiredMaps.Add(ghost.Validate_ChallengeUid);
            ghosts[fileName] = (serverVersion, file);
        }
    }

    foreach (var file in
             EnumerateFiles(opts.ReplaysFolder, true)
                 .Where(x => x.EndsWith(".Replay.Gbx", StringComparison.OrdinalIgnoreCase) ||
                             x.EndsWith(".Ghost.Gbx", StringComparison.OrdinalIgnoreCase) ||
                             x.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase))
                 .Concat(EnumerateFiles(opts.AdditionalMapsFolder, true)
                     .Where(x => x.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase))))
    {
        CMwNod node;
        try
        {
            node = Gbx.ParseHeaderNode(file);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{file} is cannot be parsed as gbx: {e}");
            continue;
        }
        switch (node)
        {
            case CGameCtnReplayRecord _:
            {
                var replay = Gbx.ParseNode<CGameCtnReplayRecord>(file);
                foreach (var (i, ghost) in replay.GetGhosts().Index())
                {
                    if (i > 0)
                    {
                        Console.Error.WriteLine($"Replay ${file} has more then one ghost. Ignoring");
                        break;
                    }

                    ProcessGhost(file, ghost, false);
                }

                break;
            }
            case CGameCtnGhost _:
            {
                var ghost = Gbx.ParseNode<CGameCtnGhost>(file);
                ProcessGhost(file, ghost, true);
                break;
            }
            case CGameCtnChallenge map:
            {
                availableMaps[map.MapUid] = file;
                break;
            }
        }
    }

    if (opts.DownloadMissingMaps)
    {
        var missingMaps = new List<string>();
        foreach (var requiredUid in requiredMaps)
            if (!availableMaps.ContainsKey(requiredUid))
                missingMaps.Add(requiredUid);

        if (missingMaps.Count > 0)
        {
            Directory.CreateDirectory(Path.Combine(opts.ReplaysFolder, "maps"));
            var ns = new NadeoLiveServices();
            Debug.Assert(opts.NadeoLogin is not null);
            Debug.Assert(opts.NadeoPassword is not null);

            await ns.AuthorizeAsync(opts.NadeoLogin, opts.NadeoPassword, AuthorizationMethod.DedicatedServer);

            foreach (var mapBatch in missingMaps.Chunk(50))
            {
                var maps = await ns.GetMapInfosAsync(mapBatch, cancellationToken);
                foreach (var map in maps)
                {
                    var mapPath = Path.Combine(opts.ReplaysFolder, "maps", $"{map.MapId}.Map.Gbx");
                    try
                    {
                        await DownloadFile($"https://core.trackmania.nadeo.live/maps/{map.MapId}/file", mapPath,
                            cancellationToken);
                        availableMaps[map.Uid] = mapPath;
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    foreach (var requiredUid in requiredMaps)
    {
        if (!availableMaps.ContainsKey(requiredUid)) continue;
        var mapPath = availableMaps[requiredUid];
        var outPath = Path.Combine(tmp.Path, "Maps", requiredUid + ".Map.Gbx");
        File.Copy(mapPath, outPath);
    }

    var report = new Dictionary<string, MSMValidationResult?>();
    foreach (var (_, ghostf) in ghosts.Values)
    {
        report[ghostf] = null;
    }

    foreach (var group in ghosts.Values.GroupBy(x => x.Item1))
    {
        var items = group.ToImmutableArray();
        // Console.WriteLine(group.Key);

        var args = string.Join(' ', "run",
            "-u", "0",
            "--rm", "-e", "MSM_ONLY_STDOUT=True", "-e", "MSM_VALIDATE_PATH=.", "-e",
            "MSM_SERVER_TYPE=TM2020", "-e",
            $"MSM_SERVER_VERSION={group.Key.Version}", "-e",
            $"\"MSM_SERVER_DOWNLOAD_HOST_TM2020={group.Key.DownloadHost}\"",
            "-e", $"MSM_SERVER_IDENTIFIER=MyServer{group.Key.Version}", "-v", $"{opts.DockerVolume}:/app/data", "-v",
            $"\"{tmp.Path}/Replays/{group.Key.Version}:/app/data/servers/MyServer{group.Key.Version}/UserData/Replays\"",
            "-v",
            $"\"{tmp.Path}/Maps:/app/data/servers/MyServer{group.Key.Version}/UserData/Maps\"",
            "bigbang1112/mania-server-manager:alpine");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Console.Error.WriteLine($"Started processing replays on {group.Key} server version");

        process.Start();

        var results = new Dictionary<string, MSMValidationResult>();

        using var reader = process.StandardOutput;
        var pipe = new Pipe();
        var writer = pipe.Writer.AsStream();
        var readerStream = pipe.Reader.AsStream();

        await Task.WhenAll(
            FixMalformedJsonOutput(reader, writer),
            ProcessStdoutAsync(results, readerStream, items.Length, cancellationToken),
            DrainStream(process.StandardError, cancellationToken),
            process.WaitForExitAsync(cancellationToken)
        );

        foreach (var (tmpGhostName, _) in results)
        {
            report[ghosts[tmpGhostName].Item2] = results[tmpGhostName];
        }
    }

    return report;
}

async Task DrainStream(StreamReader stream, CancellationToken cancellationToken)
{
    string? line;
    while ((line = await stream.ReadLineAsync(cancellationToken)) is not null)
    {
        if (VerboseStdErr) Console.Error.WriteLine(line);
    }
}

async Task FixMalformedJsonOutput(StreamReader reader, Stream writer)
{
    string? prevLine = null;
    bool firstLine = true;

    while (await reader.ReadLineAsync() is { } line)
    {
        // Console.Error.WriteLine(line);
        if (!firstLine)
            await writer.WriteAsync(Encoding.UTF8.GetBytes("\n"));
        firstLine = false;

        if (prevLine is not null &&
            prevLine.TrimEnd().EndsWith('}') &&
            line.TrimStart().StartsWith('{'))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("," + line));
        }
        else
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes(line));
        }

        prevLine = line;
    }

    writer.Dispose();
}

async Task ProcessStdoutAsync(Dictionary<string, MSMValidationResult> results, Stream stdout, int total,
    CancellationToken cancellationToken)
{
    await foreach (var (i, validateResult) in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                       stdout, new JsonSerializerOptions(),
                       cancellationToken).Index())
    {
        var res = validateResult.Deserialize<MSMValidationResult>();
        if (ReportProgress) Console.Error.WriteLine($"Got validation result ({i}/{total}): {res.FileName}");
        results[res.FileName] = res;
    }
}

internal record MSMValidationResult(
    JsonElement? ValidatedResult,
    bool? IsValid,
    string? Desc,
    string? FileName,
    string? MapUid
);

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        while (true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                System.IO.Path.GetRandomFileName()
            );
            if (File.Exists(Path)) continue;
            Directory.CreateDirectory(Path);
            break;
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
        }
    }
}

internal record ValidateFolderOpts(
    bool DownloadMissingMaps,
    string? NadeoLogin,
    string? NadeoPassword,
    string ReplaysFolder,
    string DockerVolume,
    string? AdditionalMapsFolder
);

internal record ValidateOnlineMapOpts(
    string MapUid,
    int RecordsCnt,
    string NadeoLogin,
    string NadeoPassword,
    string DockerVolume
);

partial class Util
{
    [GeneratedRegex(@"(Trackmania|ManiaPlanet) date=([0-9-_]+) (git|Svn)=([0-9a-f-]+) GameVersion=([0-9.]+)")]
    public static partial Regex ExeVersionRegex();
}

record ServerVersion(string Version, string DownloadHost);