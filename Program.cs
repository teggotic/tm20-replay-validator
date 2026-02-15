using System.CommandLine;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
using GBX.NET.ZLib;
using ManiaAPI.NadeoAPI;

Gbx.LZO = new Lzo();
Gbx.ZLib = new ZLib();

var rootCmd = new RootCommand("TM2020 Replay Validator");
var shouldDownloadOpt = new Option<bool>("--download-missing-maps");
var additionalMapsStorage = new Option<string>("--additional-maps");
var dockerVolumeOpt = new Option<string>("--docker-volume")
{
    DefaultValueFactory = _ => "msm_data"
};
var replaysFolderArg = new Argument<string>("Replays folder");

rootCmd.Options.Add(dockerVolumeOpt);
rootCmd.Options.Add(additionalMapsStorage);

Command validateDirCmd = new("validate-dir", "Validate all replays in directory") { };
validateDirCmd.Options.Add(shouldDownloadOpt);
validateDirCmd.Arguments.Add(replaysFolderArg);

validateDirCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var report = await Execute(new ValidateFolderOpts(
        parseResult.GetValue(shouldDownloadOpt),
        Environment.GetEnvironmentVariable("NADEO_LOGIN"),
        Environment.GetEnvironmentVariable("NADEO_PASSWORD"),
        parseResult.GetRequiredValue(replaysFolderArg),
        DockerVolume: parseResult.GetRequiredValue(dockerVolumeOpt),
        AdditionalMapsFolder: parseResult.GetValue(additionalMapsStorage)
    ), CancellationToken.None);

    var serOpts = new JsonSerializerOptions();
    serOpts.WriteIndented = true;
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

validateMapRecordsCmd.SetAction(async (parseResult, cancellationToken) =>
{
    await ExecuteMapValidate(
        new ValidateOnlineMapOpts(
            parseResult.GetRequiredValue(mapIdArg),
            parseResult.GetRequiredValue(topRecordsCntOpt),
            Environment.GetEnvironmentVariable("NADEO_LOGIN")!,
            Environment.GetEnvironmentVariable("NADEO_PASSWORD")!,
            parseResult.GetRequiredValue(dockerVolumeOpt)
        ),
        CancellationToken.None
    );
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

async Task ExecuteMapValidate(ValidateOnlineMapOpts opts, CancellationToken cancellationToken)
{
    using var tmp = new TempDir();
    
    var nsLive = new NadeoLiveServices();
    await nsLive.AuthorizeAsync(opts.NadeoLogin, opts.NadeoPassword, AuthorizationMethod.DedicatedServer);
    
    var mapInfo = await nsLive.GetMapInfoAsync(opts.MapUid);

    if (mapInfo is null)
    {
        Console.Error.WriteLine("Map not found");
        return;
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
        // replays
        var proc = Process.Start("wget",
            $""" "{record.Url}" -U "unbeaten-ats project; teggot@proton.me" -O {fPath} """);
        await proc.WaitForExitAsync(cancellationToken);
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
    
    var serOpts = new JsonSerializerOptions();
    serOpts.WriteIndented = true;
    Console.WriteLine(JsonSerializer.Serialize(report, serOpts));
}

async Task<Dictionary<string, MSMValidationResult>> Execute(ValidateFolderOpts opts, CancellationToken cancellationToken)
{
    // try
    // {
    //     Process.Start("docker", "volume create msm_data");
    // } catch {}
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

    var ghosts = new Dictionary<string, string>();
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
            if (overrides.ContainsKey(fileName))
            {
                ghost.Validate_ChallengeUid = overrides[fileName];
                ghost.Save(Path.Combine(tmp.Path, "Replays", fileName));
            }
            else
            {
                File.Copy(file, Path.Combine(tmp.Path, "Replays", fileName));
            }

            requiredMaps.Add(ghost.Validate_ChallengeUid);
            ghosts[fileName] = file;
        }
    }

    foreach (var file in
             EnumerateFiles(opts.ReplaysFolder, true)
                 .Where(x => x.EndsWith(".Replay.Gbx", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".Ghost.Gbx", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase))
                 .Concat(EnumerateFiles(opts.AdditionalMapsFolder, true).Where(x => x.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase))))
    {
        var node = Gbx.ParseNode(file);
        switch (node)
        {
            case CGameCtnReplayRecord replay:
            {
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
            case CGameCtnGhost ghost:
            {
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
            var ns = new NadeoLiveServices();
            Debug.Assert(opts.NadeoLogin is not null);
            Debug.Assert(opts.NadeoPassword is not null);

            await ns.AuthorizeAsync(opts.NadeoLogin, opts.NadeoPassword, AuthorizationMethod.DedicatedServer);

            var maps = await ns.GetMapInfosAsync(missingMaps, cancellationToken);
            foreach (var map in maps)
            {
                Directory.CreateDirectory(Path.Combine(opts.ReplaysFolder, "maps"));
                var mapPath = Path.Combine(opts.ReplaysFolder, "maps", $"{map.MapId}.Map.Gbx");
                try
                {
                    var proc = Process.Start("wget",
                        $""" "https://core.trackmania.nadeo.live/maps/{map.MapId}/file" -U "unbeaten-ats project; teggot@proton.me" -O {mapPath} """);
                    await proc.WaitForExitAsync(cancellationToken);
                    availableMaps[map.Uid] = mapPath;
                }
                catch
                {
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

    var args = string.Join(' ', "run",
        "-u", "0",
        "--rm", "-e", "MSM_ONLY_STDOUT=True", "-e", "MSM_VALIDATE_PATH=.", "-e",
        "MSM_SERVER_TYPE=TM2020", "-e", "\"MSM_SERVER_DOWNLOAD_HOST_TM2020=http://files.v04.maniaplanet.com/server\"",
        "-e", "MSM_SERVER_IDENTIFIER=MyServer", "-v", $"{opts.DockerVolume}:/app/data", "-v",
        $"\"{tmp.Path}/Replays:/app/data/servers/MyServer/UserData/Replays\"", "-v",
        $"\"{tmp.Path}/Maps:/app/data/servers/MyServer/UserData/Maps\"", "bigbang1112/mania-server-manager:alpine");

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

    process.Start();

    var results = new Dictionary<string, MSMValidationResult>();
    
    using var reader = process.StandardOutput;
    var pipe = new System.IO.Pipelines.Pipe();
    var writer = pipe.Writer.AsStream();
    var readerStream = pipe.Reader.AsStream();

    Task.WaitAll(new Task[]
    {
        FixMalformedJsonOutput(reader, writer),
        ProcessStdoutAsync(results, readerStream, cancellationToken),
        // DrainStdout(process, cancellationToken),
        DrainStderr(process, cancellationToken),
        process.WaitForExitAsync(cancellationToken)
    });

    var report = new Dictionary<string, MSMValidationResult?>();

    foreach (var ghostFname in ghosts.Keys) report[ghosts[ghostFname]] = results.GetValueOrDefault(ghostFname, null);
    return report;
}
async Task DrainStdout(Process process, CancellationToken cancellationToken)
{
    string? line;
    while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
    {
        Console.WriteLine(line);
    }
}


async Task DrainStderr(Process process, CancellationToken cancellationToken)
{
    string? line;
    while ((line = await process.StandardError.ReadLineAsync(cancellationToken)) is not null)
    {
        Console.Error.WriteLine(line);
    }
}

async Task FixMalformedJsonOutput(StreamReader reader, Stream writer)
{
    string? prevLine = null;
    bool firstLine = true;

    while (await reader.ReadLineAsync() is { } line)
    {
        Console.WriteLine(line);
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

async Task ProcessStdoutAsync(Dictionary<string, MSMValidationResult> results, Stream stdout,
    CancellationToken cancellationToken)
{
    await foreach (var validateResult in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                       stdout, new JsonSerializerOptions(),
                       cancellationToken))
    {
        var res = validateResult.Deserialize<MSMValidationResult>();
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