using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Gress;
using PowerKit.Extensions;
using YoutubeDownloader.Cli;
using YoutubeDownloader.Core.Downloading;
using YoutubeDownloader.Core.Resolving;
using YoutubeDownloader.Core.Tagging;
using YoutubeExplode;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

const string Usage = """
    Usage: YoutubeDownloader.Cli <playlist-url | playlist.xml> <parent-folder> [options]

    Downloads every video into <parent-folder>\<playlist title>, in playlist order,
    and saves <playlist title>.xml next to them. Already downloaded files are skipped,
    so running it again resumes where it left off.

    Options:
      --quality lowest|360|480|720|1080|highest   (default: lowest)
      --format mp4|webm|mp3|ogg                   (default: mp4)
      --parallel N                                (default: 2)
      --retries N                                 (default: 10)
      --dubs        include auto-dubbed audio tracks (much bigger files)
      --no-subs     don't embed subtitles
      --no-tags     don't embed tags and cover art
      --compact     shrink MP4 files for Apple devices (needs FFmpeg with libx265 for HEVC)
      --settings F  desktop app Settings.dat to read the saved login from
                    (default: ..\Settings.dat next to this tool)
    """;

if (args.Length < 2 || args.Any(a => a is "-h" or "--help" or "/?"))
{
    Console.WriteLine(Usage);
    return 1;
}

var input = args[0].Trim().Trim('"');
var parentDirPath = args[1].Trim().Trim('"');
var quality = VideoQualityPreference.Lowest;
var container = Container.Mp4;
var parallelLimit = 2;
var retryLimit = 10;
var shouldIncludeDubs = false;
var shouldIncludeSubtitles = true;
var shouldInjectTags = true;
var shouldCompact = false;
var settingsFilePath = Path.Combine(AppContext.BaseDirectory, "..", "Settings.dat");

for (var i = 2; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i]);

    switch (args[i].ToLowerInvariant())
    {
        case "--quality":
            quality = Next().ToLowerInvariant() switch
            {
                "lowest" => VideoQualityPreference.Lowest,
                "360" or "360p" => VideoQualityPreference.UpTo360p,
                "480" or "480p" => VideoQualityPreference.UpTo480p,
                "720" or "720p" => VideoQualityPreference.UpTo720p,
                "1080" or "1080p" => VideoQualityPreference.UpTo1080p,
                "highest" => VideoQualityPreference.Highest,
                var q => throw new ArgumentException($"Unknown quality '{q}'."),
            };
            break;
        case "--format":
            container = new Container(Next().ToLowerInvariant());
            break;
        case "--parallel":
            parallelLimit = Math.Clamp(int.Parse(Next()), 1, 10);
            break;
        case "--retries":
            retryLimit = Math.Clamp(int.Parse(Next()), 0, 50);
            break;
        case "--dubs":
            shouldIncludeDubs = true;
            break;
        case "--no-subs":
            shouldIncludeSubtitles = false;
            break;
        case "--no-tags":
            shouldInjectTags = false;
            break;
        case "--compact":
            shouldCompact = true;
            break;
        case "--settings":
            settingsFilePath = Next();
            break;
        default:
            Console.WriteLine($"Unknown option '{args[i]}'.");
            Console.WriteLine(Usage);
            return 1;
    }
}

var cookies = AppSettingsCookies.TryLoad(settingsFilePath);
var logLock = new Lock();
string? logFilePath = null;

void Log(string message, ConsoleColor? color = null)
{
    var line = $"{DateTime.Now:HH:mm:ss} {message}";
    lock (logLock)
    {
        if (color is not null)
            Console.ForegroundColor = color.Value;

        Console.WriteLine(line);
        Console.ResetColor();

        if (logFilePath is not null)
            File.AppendAllText(logFilePath, line + Environment.NewLine);
    }
}

// Resolve the list of videos
string title;
string outputDirPath;
List<(int Position, string Id, string Title, IVideo? Video)> items;
ResolvedPlaylist? resolvedPlaylist = null;

if (File.Exists(input) && input.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
{
    var root = XDocument.Load(input).Root!;
    title = (string?)root.Element("Title") ?? Path.GetFileNameWithoutExtension(input);
    items = root.Element("Videos")!
        .Elements("Video")
        .Select(v =>
            (
                (int)v.Attribute("Position")!,
                (string)v.Attribute("Id")!,
                (string?)v.Element("Title") ?? "",
                (IVideo?)null
            )
        )
        .ToList();
}
else
{
    Console.WriteLine(
        cookies is not null
            ? "Reading the playlist using the desktop app's saved login..."
            : "Reading the playlist..."
    );

    using var resolver = new QueryResolver(cookies);
    var result = await resolver.ResolveAsync(input);

    resolvedPlaylist = result.Playlists.FirstOrDefault();
    title = resolvedPlaylist?.Playlist.Title ?? result.Title;
    items = result.Videos.Select((v, i) => (i + 1, v.Id.Value, v.Title, (IVideo?)v)).ToList();
}

if (items.Count == 0)
{
    Console.WriteLine("No videos found.");
    return 2;
}

outputDirPath = Path.Combine(parentDirPath, Path.EscapeFileName(title.Trim()));
Directory.CreateDirectory(outputDirPath);
logFilePath = Path.Combine(outputDirPath, "_download-log.txt");

// Pick file names up front so they're stable between runs
var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var filePaths = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var item in items)
{
    var fileName = Path.EscapeFileName($"{item.Title.Trim()}.{container.Name}");
    if (!usedFileNames.Add(fileName))
        fileName = Path.EscapeFileName($"{item.Title.Trim()} [{item.Id}].{container.Name}");

    usedFileNames.Add(fileName);
    filePaths[item.Id] = Path.Combine(outputDirPath, fileName);
}

if (resolvedPlaylist is not null)
{
    await PlaylistXmlWriter.WriteAsync(
        Path.Combine(outputDirPath, PlaylistXmlWriter.GetFileName(resolvedPlaylist)),
        resolvedPlaylist,
        filePaths
    );
}

Log($"'{title}': {items.Count} videos -> {outputDirPath}", ConsoleColor.Cyan);

var gate = new SemaphoreSlim(parallelLimit);
var stateLock = new Lock();
var cooldownUntil = DateTimeOffset.MinValue;
var consecutiveFailureCount = 0;
int completedCount = 0,
    skippedCount = 0,
    failedCount = 0;
var failures = new List<string>();
var stallTimeout = TimeSpan.FromSeconds(90);

static TimeSpan GetRetryDelay(int failureCount) =>
    failureCount switch
    {
        1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(1),
        4 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5),
    };

async Task DownloadAsync((int Position, string Id, string Title, IVideo? Video) item)
{
    var filePath = filePaths[item.Id];
    var label = $"#{item.Position}/{items.Count} {item.Title}";

    if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
    {
        Interlocked.Increment(ref skippedCount);
        return;
    }

    for (var attempt = 1; ; attempt++)
    {
        await gate.WaitAsync();
        try
        {
            TimeSpan wait;
            lock (stateLock)
                wait = cooldownUntil - DateTimeOffset.Now;

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);

            await Task.Delay(Random.Shared.Next(1000, 2500));

            var stopwatch = Stopwatch.StartNew();

            // Videos are downloaded logged out: YouTube sometimes rejects requests made with a saved login
            using var youtube = new YoutubeClient();
            using var downloader = new VideoDownloader();

            var video = item.Video ?? await youtube.Videos.GetAsync(item.Id);
            var downloadOption = await downloader.GetBestDownloadOptionAsync(
                video.Id,
                new VideoDownloadPreference(container, quality),
                shouldIncludeDubs
            );

            using var stallCts = new CancellationTokenSource();
            var lastActivity = Environment.TickCount64;
            using var stallTimer = new Timer(
                _ =>
                {
                    if (
                        Environment.TickCount64 - Interlocked.Read(ref lastActivity)
                        > stallTimeout.TotalMilliseconds
                    )
                        stallCts.Cancel();
                },
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5)
            );

            try
            {
                await downloader.DownloadVideoAsync(
                    filePath,
                    video,
                    downloadOption,
                    shouldIncludeSubtitles,
                    null,
                    new DelegateProgress<Percentage>(_ =>
                        Interlocked.Exchange(ref lastActivity, Environment.TickCount64)
                    ),
                    stallCts.Token
                );
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Download stalled: no data received for {stallTimeout.TotalSeconds:F0} seconds."
                );
            }

            if (shouldCompact && container == Container.Mp4)
            {
                try
                {
                    await MediaCompactor.CompactForAppleAsync(
                        filePath,
                        downloadOption.VideoQuality?.MaxHeight >= 360
                    );
                }
                catch { }
            }

            if (shouldInjectTags)
            {
                try
                {
                    await new MediaTagInjector().InjectTagsAsync(filePath, video);
                }
                catch { }
            }

            lock (stateLock)
                consecutiveFailureCount = 0;

            var doneCount = Interlocked.Increment(ref completedCount);
            Log(
                $"OK    {label} ({new FileInfo(filePath).Length / 1024} KB, {stopwatch.Elapsed.TotalSeconds:F0}s) [{doneCount + skippedCount + failedCount}/{items.Count}]",
                ConsoleColor.Green
            );

            return;
        }
        catch (Exception ex)
            when (ex is VideoUnavailableException or VideoRequiresPurchaseException
                || attempt > retryLimit
            )
        {
            TryDelete(filePath);
            Interlocked.Increment(ref failedCount);

            var message = $"{label} ({item.Id}): {ex.Message.Split('\n')[0]}";
            lock (failures)
                failures.Add(message);

            Log($"FAIL  {message}", ConsoleColor.Red);
            return;
        }
        catch (Exception ex)
        {
            TryDelete(filePath);

            TimeSpan delay;
            lock (stateLock)
            {
                consecutiveFailureCount++;

                var until = DateTimeOffset.Now + GetRetryDelay(consecutiveFailureCount);
                if (until > cooldownUntil)
                    cooldownUntil = until;

                delay = cooldownUntil - DateTimeOffset.Now;
            }

            Log(
                $"RETRY {label} attempt {attempt}/{retryLimit}, pausing {delay.TotalSeconds:F0}s: {ex.Message.Split('\n')[0]}",
                ConsoleColor.Yellow
            );
        }
        finally
        {
            gate.Release();
        }
    }
}

static void TryDelete(string filePath)
{
    try
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
    catch { }
}

var totalStopwatch = Stopwatch.StartNew();
await Task.WhenAll(items.Select(DownloadAsync));

Log(
    $"Finished in {totalStopwatch.Elapsed:hh\\:mm\\:ss}: {completedCount} downloaded, {skippedCount} already there, {failedCount} failed",
    ConsoleColor.Cyan
);

foreach (var failure in failures)
    Log($"  failed: {failure}", ConsoleColor.Red);

return failedCount > 0 ? 3 : 0;
