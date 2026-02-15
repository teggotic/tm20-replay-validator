using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
using GBX.NET.ZLib;
using ManiaAPI.NadeoAPI;

Gbx.LZO = new Lzo();
Gbx.ZLib = new ZLib();

var rootCmd = new RootCommand("Example CLI");
var shouldDownloadOpt = new Option<bool>("--download-missing-maps");
var dockerVolumeOpt = new Option<string>("--docker-volume")
{
    DefaultValueFactory = _ => "msm_data"
};
var replaysFolderArg = new Argument<string>("Replays folder");

rootCmd.Options.Add(shouldDownloadOpt);
rootCmd.Options.Add(dockerVolumeOpt);
rootCmd.Arguments.Add(replaysFolderArg);
rootCmd.SetAction(async (parseResult, cancellationToken) =>
{
    await Execute(new Opts(
        parseResult.GetValue(shouldDownloadOpt),
        Environment.GetEnvironmentVariable("NADEO_LOGIN"),
        Environment.GetEnvironmentVariable("NADEO_PASSWORD"),
        parseResult.GetRequiredValue(replaysFolderArg),
        DockerVolume: parseResult.GetRequiredValue(dockerVolumeOpt)
    ), CancellationToken.None);
});

return rootCmd.Parse(args).Invoke();

IEnumerable<string> EnumerateFiles(string path, bool recursive)
{
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

async Task Execute(Opts opts, CancellationToken cancellationToken)
{
    // try
    // {
    //     Process.Start("docker", "volume create msm_data");
    // } catch {}
    // Dictionary<string, string> overrides = new();
    // try
    // {
    //     var x = JsonSerializer.Deserialize<Dictionary<string, string>>(
    //         File.ReadAllText("/tmp/ttt/replays/mappings.json"));
    //     if (x is not null) overrides = x;
    // }
    // catch
    // {
    // }

    var ghosts = new Dictionary<string, string>();
    var requiredMaps = new HashSet<string>();
    var availableMaps = new Dictionary<string, string>();

    using var tmp = new TempDir();

    Directory.CreateDirectory(Path.Combine(tmp.Path, "Replays"));
    Directory.CreateDirectory(Path.Combine(tmp.Path, "Maps"));

    foreach (var file in EnumerateFiles(opts.ReplaysFolder, true)
                 .Where(x => x.EndsWith(".Replay.Gbx") || x.EndsWith(".Ghost.Gbx") || x.EndsWith(".Map.Gbx")))
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

                    if (ghost.Validate_ChallengeUid is null)
                    {
                        Console.Error.WriteLine($"Found ghost without mapUid inside of {file} replay");
                    }
                    else
                    {
                        requiredMaps.Add(ghost.Validate_ChallengeUid);
                        var fileName = Guid.NewGuid() + ".Replay.Gbx";
                        File.Copy(file, Path.Combine(tmp.Path, "Replays", fileName));
                        ghosts[fileName] = file;
                    }
                }

                break;
            }
            case CGameCtnGhost ghost:
            {
                if (ghost.Validate_ChallengeUid is null)
                {
                    Console.Error.WriteLine($"Found ghost without mapUid inside of {file} replay");
                }
                else
                {
                    requiredMaps.Add(ghost.Validate_ChallengeUid);
                    var fileName = Guid.NewGuid() + ".Replay.Gbx";
                    File.Copy(file, Path.Combine(tmp.Path, "Replays", fileName));
                    ghosts[fileName] = file;
                }

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

    var args = string.Join(' ', "run", "--rm", "-e", "MSM_ONLY_STDOUT=True", "-e", "MSM_VALIDATE_PATH=.", "-e",
        "MSM_SERVER_TYPE=TM2020", "-e", "MSM_SERVER_IDENTIFIER=MyServer", "-v", $"{opts.DockerVolume}:/app/data", "-v",
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

    // var tasks = new Task[]{
    //     process.WaitForExitAsync(cancellationToken),
    // };

    await ProcessStdoutAsync(results, process, cancellationToken);
    await process.WaitForExitAsync(cancellationToken);

    var report = new Dictionary<string, MSMValidationResult?>();

    foreach (var ghostFname in ghosts.Keys) report[ghostFname] = results.GetValueOrDefault(ghostFname, null);

    var serOpts = new JsonSerializerOptions();
    serOpts.WriteIndented = true;
    Console.WriteLine(JsonSerializer.Serialize(report, serOpts));
}

async Task ProcessStdoutAsync(Dictionary<string, MSMValidationResult> results, Process process,
    CancellationToken cancellationToken)
{
    await foreach (var validateResult in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                       process.StandardOutput.BaseStream, new JsonSerializerOptions(),
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

internal record Opts(
    bool DownloadMissingMaps,
    string? NadeoLogin,
    string? NadeoPassword,
    string? ReplaysFolder,
    string DockerVolume
);
