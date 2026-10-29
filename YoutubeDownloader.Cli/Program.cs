using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
using YoutubeExplode.Channels;
using YoutubeExplode.Common;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

const string Usage = """
    Usage: YoutubeDownloader.Cli <input> <parent-folder> [options]

    <input> can be:
      a playlist link        downloads into <parent-folder>\<playlist title>
      a playlist .xml        re-downloads a list saved earlier by this tool or the app
      a .txt of video links  treated as a playlist named after the file
      a channels .txt        a "Most Watched Channels" list (#1, channel name, ...) used
                             with --history: each channel gets its own folder with the
                             history videos from that channel plus its newest uploads

    Every folder gets <title>.xml with the videos in order. Already downloaded files are
    skipped, so running it again resumes where it left off.

    Options:
      --quality lowest|360|480|720|1080|highest   (default: lowest)
      --format mp4|webm|mp3|ogg                   (default: mp4)
      --parallel N                                (default: 2)
      --retries N                                 (default: 10)
      --history F   .txt of watched video links, used by a channels list
      --extra N     newest uploads to add per channel (default: 10)
      --dry-run     only show what would be downloaded
      --dubs        include auto-dubbed audio tracks (much bigger files)
      --no-subs     don't embed subtitles
      --no-tags     don't embed tags and cover art
      --compact     shrink MP4 files for Apple devices (needs FFmpeg with libx265 for HEVC)
      --settings F  desktop app Settings.dat to read the saved login from
                    (default: ..\Settings.dat next to this tool)
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;

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
var extraCount = 10;
string? historyFilePath = null;
var isDryRun = false;
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
        case "--history":
            historyFilePath = Next().Trim('"');
            break;
        case "--extra":
            extraCount = Math.Clamp(int.Parse(Next()), 0, 500);
            break;
        case "--dry-run":
            isDryRun = true;
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
            settingsFilePath = Next().Trim('"');
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

static string NormalizeName(string name) =>
    Regex.Replace(name.Replace('’', '\'').Replace('–', '-'), @"\s+", " ").Trim();

static IReadOnlyList<VideoId> ReadVideoIds(string filePath) =>
    File.ReadAllLines(filePath)
        .Select(l => VideoId.TryParse(l.Trim()))
        .OfType<VideoId>()
        .DistinctBy(id => id.Value)
        .ToArray();

async Task<IReadOnlyList<IVideo>> GetVideosAsync(IReadOnlyList<VideoId> videoIds)
{
    Console.WriteLine($"Looking up {videoIds.Count} videos...");

    using var youtube = new YoutubeClient();
    using var lookupGate = new SemaphoreSlim(4);

    var videos = await Task.WhenAll(
        videoIds.Select(async id =>
        {
            await lookupGate.WaitAsync();
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        return (IVideo?)await youtube.Videos.GetAsync(id);
                    }
                    catch (Exception ex) when (ex is not VideoUnavailableException && attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Skipping {id}: {ex.Message.Split('\n')[0]}");
                        return null;
                    }
                }
            }
            finally
            {
                lookupGate.Release();
            }
        })
    );

    return videos.OfType<IVideo>().ToArray();
}

static TimeSpan GetRetryDelay(int failureCount) =>
    failureCount switch
    {
        1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(1),
        4 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5),
    };

static void TryDelete(string filePath)
{
    try
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
    catch { }
}

// Downloads one list of videos into its own folder and returns the number of failures
async Task<int> DownloadListAsync(
    string title,
    IReadOnlyList<(string Id, string Title, IVideo? Video)> items,
    Func<string, IReadOnlyDictionary<string, string>, Task>? writeXmlAsync
)
{
    var outputDirPath = Path.Combine(parentDirPath, Path.EscapeFileName(title.Trim()));

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

    if (isDryRun)
    {
        Console.WriteLine($"'{title}': {items.Count} videos -> {outputDirPath}");
        foreach (var (i, item) in items.Index())
            Console.WriteLine($"  {i + 1, 3}. {item.Title}");

        return 0;
    }

    Directory.CreateDirectory(outputDirPath);
    logFilePath = Path.Combine(outputDirPath, "_download-log.txt");

    if (writeXmlAsync is not null)
        await writeXmlAsync(outputDirPath, filePaths);

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

    async Task DownloadAsync(int position, (string Id, string Title, IVideo? Video) item)
    {
        var filePath = filePaths[item.Id];
        var label = $"#{position}/{items.Count} {item.Title}";

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

    var totalStopwatch = Stopwatch.StartNew();
    await Task.WhenAll(items.Select((item, i) => DownloadAsync(i + 1, item)));

    Log(
        $"Finished '{title}' in {totalStopwatch.Elapsed:hh\\:mm\\:ss}: {completedCount} downloaded, {skippedCount} already there, {failedCount} failed",
        ConsoleColor.Cyan
    );

    foreach (var failure in failures)
        Log($"  failed: {failure}", ConsoleColor.Red);

    return failedCount;
}

Func<string, IReadOnlyDictionary<string, string>, Task> WriteXml(
    string id,
    string url,
    string title,
    string author,
    IReadOnlyList<IVideo> videos
) =>
    (outputDirPath, filePaths) =>
        PlaylistXmlWriter.WriteAsync(
            Path.Combine(outputDirPath, Path.EscapeFileName(title.Trim()) + ".xml"),
            id,
            url,
            title,
            author,
            videos,
            filePaths
        );

var inputLines = File.Exists(input) ? File.ReadAllLines(input) : [];
var isChannelsList = inputLines.Any(l => Regex.IsMatch(l.Trim(), @"^#\d+$"));

// Channels list: one folder per channel with the watched videos plus the newest uploads
if (isChannelsList)
{
    if (historyFilePath is null)
    {
        Console.WriteLine("A channels list needs --history <watched links .txt>.");
        return 1;
    }

    var channelNames = inputLines
        .Select((line, i) => (line: line.Trim(), i))
        .Where(x => Regex.IsMatch(x.line, @"^#\d+$"))
        .Select(x =>
            inputLines.Skip(x.i + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
        )
        .OfType<string>()
        .ToArray();

    var historyVideos = await GetVideosAsync(ReadVideoIds(historyFilePath));
    var totalFailedCount = 0;

    using var youtube = new YoutubeClient();
    foreach (var (i, channelName) in channelNames.Index())
    {
        var watched = historyVideos
            .Where(v =>
                string.Equals(
                    NormalizeName(v.Author.ChannelTitle),
                    NormalizeName(channelName),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        var channelId = watched.FirstOrDefault()?.Author.ChannelId;
        if (channelId is null)
        {
            var results = await youtube.Search.GetChannelsAsync(channelName).CollectAsync(5);
            channelId =
                results
                    .FirstOrDefault(c =>
                        string.Equals(
                            NormalizeName(c.Title),
                            NormalizeName(channelName),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    ?.Id
                ?? results.FirstOrDefault()?.Id;
        }

        if (channelId is not { } resolvedChannelId)
        {
            Console.WriteLine(
                $"[{i + 1}/{channelNames.Length}] '{channelName}': channel not found"
            );
            continue;
        }

        var watchedIds = watched.Select(v => v.Id.Value).ToHashSet(StringComparer.Ordinal);
        var extras =
            extraCount > 0
                ? (
                    await youtube
                        .Channels.GetUploadsAsync(resolvedChannelId)
                        .CollectAsync(extraCount + watched.Count)
                )
                    .Where(v => !watchedIds.Contains(v.Id.Value))
                    .Take(extraCount)
                    .Cast<IVideo>()
                    .ToArray()
                : [];

        var videos = watched.Concat(extras).ToArray();
        Console.WriteLine(
            $"[{i + 1}/{channelNames.Length}] '{channelName}': {watched.Count} watched + {extras.Length} newest uploads"
        );

        totalFailedCount += await DownloadListAsync(
            channelName,
            videos.Select(v => (v.Id.Value, v.Title, (IVideo?)v)).ToArray(),
            WriteXml(
                resolvedChannelId.Value,
                $"https://www.youtube.com/channel/{resolvedChannelId}",
                channelName,
                channelName,
                videos
            )
        );
    }

    return totalFailedCount > 0 ? 3 : 0;
}

// Plain list of video links: a playlist named after the file
if (File.Exists(input) && input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
{
    var title = Path.GetFileNameWithoutExtension(input);
    var videos = await GetVideosAsync(ReadVideoIds(input));

    var failedCount = await DownloadListAsync(
        title,
        videos.Select(v => (v.Id.Value, v.Title, (IVideo?)v)).ToArray(),
        WriteXml(title, Path.GetFullPath(input), title, "", videos)
    );

    return failedCount > 0 ? 3 : 0;
}

// Playlist saved earlier as XML
if (File.Exists(input) && input.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
{
    var root = XDocument.Load(input).Root!;
    var title = (string?)root.Element("Title") ?? Path.GetFileNameWithoutExtension(input);
    var items = root.Element("Videos")!
        .Elements("Video")
        .Select(v => ((string)v.Attribute("Id")!, (string?)v.Element("Title") ?? "", (IVideo?)null))
        .ToArray();

    var failedCount = await DownloadListAsync(title, items, null);
    return failedCount > 0 ? 3 : 0;
}

// Playlist, channel or video link
{
    Console.WriteLine(
        cookies is not null
            ? "Reading the playlist using the desktop app's saved login..."
            : "Reading the playlist..."
    );

    using var resolver = new QueryResolver(cookies);
    var result = await resolver.ResolveAsync(input);

    if (result.Videos.Count == 0)
    {
        Console.WriteLine("No videos found.");
        return 2;
    }

    var playlist = result.Playlists.FirstOrDefault();
    var title = playlist?.Playlist.Title ?? result.Title;

    var failedCount = await DownloadListAsync(
        title,
        result.Videos.Select(v => (v.Id.Value, v.Title, (IVideo?)v)).ToArray(),
        playlist is not null
            ? (outputDirPath, filePaths) =>
                PlaylistXmlWriter.WriteAsync(
                    Path.Combine(outputDirPath, PlaylistXmlWriter.GetFileName(playlist)),
                    playlist,
                    filePaths
                )
            : null
    );

    return failedCount > 0 ? 3 : 0;
}
