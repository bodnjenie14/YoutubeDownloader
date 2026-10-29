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
      --max-minutes N  skip videos longer than N minutes (they stay listed in the XML)
      --dry-run     only show what would be downloaded
      --dubs        include auto-dubbed audio tracks (much bigger files)
      --no-subs     don't embed subtitles
      --no-tags     don't embed tags and cover art
      --compact     shrink MP4 files for Apple devices (needs FFmpeg with libx265 for HEVC)
      --settings F  desktop app Settings.dat to read the saved login from
                    (default: ..\Settings.dat next to this tool)
      --cookies F   cookies.txt exported from a signed-in browser (instead of --settings)
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
TimeSpan? maxDuration = null;
string? historyFilePath = null;
var isDryRun = false;
var shouldIncludeDubs = false;
var shouldIncludeSubtitles = true;
var shouldInjectTags = true;
var shouldCompact = false;
var settingsFilePath = Path.Combine(AppContext.BaseDirectory, "..", "Settings.dat");
string? cookiesFilePath = null;

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
        case "--max-minutes":
            maxDuration = TimeSpan.FromMinutes(int.Parse(Next()));
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
        case "--cookies":
            cookiesFilePath = Next().Trim('"');
            break;
        default:
            Console.WriteLine($"Unknown option '{args[i]}'.");
            Console.WriteLine(Usage);
            return 1;
    }
}

var cookies = cookiesFilePath is not null
    ? AppSettingsCookies.LoadCookiesFile(cookiesFilePath)
    : AppSettingsCookies.TryLoad(settingsFilePath);
var ytDlpFilePath = FindYtDlp();
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

// Finds video links anywhere in a line, so Markdown and other exports work too
static IReadOnlyList<VideoId> ReadVideoIds(string filePath) =>
    File.ReadAllLines(filePath)
        .SelectMany(l =>
            Regex
                .Matches(
                    l,
                    @"(?:youtube\.com/(?:shorts/|live/|embed/)|youtu\.be/|[?&]v=)([A-Za-z0-9_-]{11})"
                )
                .Select(m => m.Groups[1].Value)
                .DefaultIfEmpty(l.Trim())
        )
        .Select(s => VideoId.TryParse(s))
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

// Per-video hiccups that usually clear on a quick retry, unlike throttling
static bool IsCipherError(Exception ex) =>
    ex is VideoUnplayableException and not VideoUnavailableException
    || ex.Message.Contains("cipher manifest", StringComparison.OrdinalIgnoreCase);

static string? FindYtDlp()
{
    var candidates = new[]
    {
        AppContext.BaseDirectory,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Links"
        ),
    }.Concat(
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries
        )
    );

    foreach (var dirPath in candidates)
    {
        var filePath = Path.Combine(dirPath, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        if (File.Exists(filePath))
            return filePath;
    }

    return null;
}

async Task<bool> TryDownloadWithYtDlpAsync(string videoId, string filePath)
{
    if (ytDlpFilePath is null || container != Container.Mp4)
        return false;

    var format = quality switch
    {
        VideoQualityPreference.Lowest => "wv*[ext=mp4]+ba[ext=m4a]/w[ext=mp4]/w",
        VideoQualityPreference.UpTo360p => "bv*[height<=360][ext=mp4]+ba[ext=m4a]/b[height<=360]",
        VideoQualityPreference.UpTo480p => "bv*[height<=480][ext=mp4]+ba[ext=m4a]/b[height<=480]",
        VideoQualityPreference.UpTo720p => "bv*[height<=720][ext=mp4]+ba[ext=m4a]/b[height<=720]",
        VideoQualityPreference.UpTo1080p =>
            "bv*[height<=1080][ext=mp4]+ba[ext=m4a]/b[height<=1080]",
        _ => "bv*[ext=mp4]+ba[ext=m4a]/b",
    };

    var startInfo = new ProcessStartInfo(ytDlpFilePath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    foreach (
        var argument in new[]
        {
            "--no-playlist",
            "--no-progress",
            "--quiet",
            "--no-warnings",
            "--force-overwrites",
            "--match-filter",
            "!is_live",
            "-f",
            format,
            "--merge-output-format",
            "mp4",
            "-o",
            filePath,
        }
    )
        startInfo.ArgumentList.Add(argument);

    if (shouldIncludeSubtitles)
    {
        startInfo.ArgumentList.Add("--write-subs");
        startInfo.ArgumentList.Add("--embed-subs");
    }

    startInfo.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

    try
    {
        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await process.WaitForExitAsync(timeoutCts.Token);
        await Task.WhenAll(outputTask, errorTask);

        return process.ExitCode == 0 && File.Exists(filePath) && new FileInfo(filePath).Length > 0;
    }
    catch
    {
        return false;
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

// Downloads one list of videos into its own folder and returns the number of failures
async Task<int> DownloadListAsync(
    string title,
    IReadOnlyList<(string Id, string Title, IVideo? Video)> items,
    Func<string, IReadOnlyDictionary<string, string>, Task>? writeXmlAsync,
    string? folderName = null
)
{
    var outputDirPath = Path.Combine(
        parentDirPath,
        Path.EscapeFileName((folderName ?? title).Trim())
    );

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

    // Live streams never end, so they stay listed without a file
    var liveItems = items.Where(i => i.Video is not null && i.Video.Duration is null).ToArray();
    if (liveItems.Length > 0)
    {
        Console.WriteLine($"'{title}': skipping {liveItems.Length} live streams");
        items = items.Except(liveItems).ToArray();
        foreach (var item in liveItems)
            filePaths.Remove(item.Id);
    }

    // Long videos stay listed in the XML, just without a file (unless downloaded earlier)
    if (maxDuration is { } limit)
    {
        var skippedLong = items
            .Where(i =>
                i.Video?.Duration > limit
                && !(File.Exists(filePaths[i.Id]) && new FileInfo(filePaths[i.Id]).Length > 0)
            )
            .ToArray();

        if (skippedLong.Length > 0)
        {
            Console.WriteLine(
                $"'{title}': skipping {skippedLong.Length} videos longer than {limit.TotalMinutes:F0} minutes"
            );
            items = items.Except(skippedLong).ToArray();
            foreach (var item in skippedLong)
                filePaths.Remove(item.Id);
        }
    }

    if (isDryRun)
    {
        // "new" lines are what a real run would download: id, minutes, title
        var newItems = items
            .Where(i => !(File.Exists(filePaths[i.Id]) && new FileInfo(filePaths[i.Id]).Length > 0))
            .ToArray();
        var newMinutes = newItems.Sum(i => i.Video?.Duration?.TotalMinutes ?? 0);

        Console.WriteLine(
            $"'{title}': {items.Count} videos, {newItems.Length} new ({newMinutes:F0} min) -> {outputDirPath}"
        );
        foreach (var item in newItems)
            Console.WriteLine(
                $"  new {item.Id} {item.Video?.Duration?.TotalMinutes ?? 0:F1} {item.Title}"
            );

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
            catch (Exception ex) when (IsCipherError(ex) && attempt >= 3)
            {
                TryDelete(filePath);

                if (ytDlpFilePath is not null && await TryDownloadWithYtDlpAsync(item.Id, filePath))
                {
                    if (shouldInjectTags && item.Video is not null)
                    {
                        try
                        {
                            await new MediaTagInjector().InjectTagsAsync(filePath, item.Video);
                        }
                        catch { }
                    }

                    var doneCount = Interlocked.Increment(ref completedCount);
                    Log(
                        $"OK    {label} (via yt-dlp, {new FileInfo(filePath).Length / 1024} KB) [{doneCount + skippedCount + failedCount}/{items.Count}]",
                        ConsoleColor.Green
                    );

                    return;
                }

                TryDelete(filePath);
                Interlocked.Increment(ref failedCount);

                var message =
                    $"{label} ({item.Id}): {ex.Message.Split('\n')[0]}"
                    + (
                        ytDlpFilePath is null
                            ? " (install yt-dlp to download these)"
                            : " (yt-dlp failed too)"
                    );
                lock (failures)
                    failures.Add(message);

                Log($"FAIL  {message}", ConsoleColor.Red);
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
            catch (Exception ex) when (IsCipherError(ex))
            {
                TryDelete(filePath);

                // Not throttling: specific videos YoutubeExplode can't decipher, so don't pause the queue
                Log(
                    $"RETRY {label} attempt {attempt}/3: {ex.Message.Split('\n')[0]}",
                    ConsoleColor.Yellow
                );

                await Task.Delay(TimeSpan.FromSeconds(5));
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

// Channel pictures for every channel referenced in the library's XMLs
if (string.Equals(input, "avatars", StringComparison.OrdinalIgnoreCase))
{
    var avatarsDirPath = Path.Combine(parentDirPath, "avatars");
    Directory.CreateDirectory(avatarsDirPath);

    var channelIds = new HashSet<string>(StringComparer.Ordinal);
    var channelFolders = new List<(string ChannelId, string DirPath)>();

    foreach (
        var xmlFilePath in Directory.EnumerateFiles(
            parentDirPath,
            "*.xml",
            SearchOption.AllDirectories
        )
    )
    {
        if (xmlFilePath.StartsWith(avatarsDirPath, StringComparison.OrdinalIgnoreCase))
            continue;

        XDocument document;
        try
        {
            document = XDocument.Load(xmlFilePath);
        }
        catch
        {
            continue;
        }

        // Channel and subscriber folders use the channel ID as the list ID
        if (
            (string?)document.Root?.Attribute("Id") is { } listId
            && ChannelId.TryParse(listId) is not null
        )
        {
            channelIds.Add(listId);
            channelFolders.Add((listId, Path.GetDirectoryName(xmlFilePath)!));
        }

        foreach (var author in document.Descendants("Author"))
        {
            if ((string?)author.Attribute("ChannelId") is { Length: > 0 } channelId)
                channelIds.Add(channelId);
        }
    }

    Console.WriteLine(
        $"{channelIds.Count} channels referenced, {channelFolders.Count} channel folders"
    );

    using var youtube = new YoutubeClient();
    using var avatarGate = new SemaphoreSlim(4);
    var channels = new List<Channel>();
    var channelsLock = new Lock();
    var downloadedCount = 0;

    await Task.WhenAll(
        channelIds.Select(async channelId =>
        {
            await avatarGate.WaitAsync();
            try
            {
                var channel = await youtube.Channels.GetAsync(channelId);
                lock (channelsLock)
                    channels.Add(channel);

                var avatarFilePath = Path.Combine(avatarsDirPath, $"{channelId}.jpg");
                if (File.Exists(avatarFilePath) && new FileInfo(avatarFilePath).Length > 0)
                    return;

                if (channel.Thumbnails.TryGetWithHighestResolution() is not { } thumbnail)
                    return;

                var bytes = await YoutubeDownloader.Core.Utils.Http.Client.GetByteArrayAsync(
                    thumbnail.Url
                );
                await File.WriteAllBytesAsync(avatarFilePath, bytes);
                Interlocked.Increment(ref downloadedCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipping {channelId}: {ex.Message.Split('\n')[0]}");
            }
            finally
            {
                avatarGate.Release();
            }
        })
    );

    foreach (var (channelId, dirPath) in channelFolders)
    {
        var avatarFilePath = Path.Combine(avatarsDirPath, $"{channelId}.jpg");
        if (File.Exists(avatarFilePath))
            File.Copy(avatarFilePath, Path.Combine(dirPath, "folder.jpg"), true);
    }

    new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement(
            "Channels",
            channels
                .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .Select(c => new XElement(
                    "Channel",
                    new XAttribute("Id", c.Id.Value),
                    new XAttribute("Url", c.Url),
                    new XElement("Title", c.Title),
                    new XElement("Avatar", $"{c.Id}.jpg")
                ))
        )
    ).Save(Path.Combine(avatarsDirPath, "channels.xml"));

    Console.WriteLine(
        $"Saved {downloadedCount} new channel pictures to {avatarsDirPath}, folder.jpg in {channelFolders.Count} folders"
    );
    return 0;
}

var inputLines = File.Exists(input) ? File.ReadAllLines(input) : [];
var isChannelsList = inputLines.Any(l => Regex.IsMatch(l.Trim(), @"^#\d+$"));
var mostWatchedHeader = inputLines
    .Select(l => l.Trim())
    .FirstOrDefault(l =>
        l.StartsWith("Most-Watched Videos on ", StringComparison.OrdinalIgnoreCase)
    );

static string NormalizeTitle(string title) =>
    Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");

// Most-watched list: a channel's top uploads by title, ranked
if (mostWatchedHeader is not null)
{
    var channelName = mostWatchedHeader["Most-Watched Videos on ".Length..].Trim();

    // Rows look like: rank, title, "12:10 · 4 yr ago", views
    var titles = inputLines
        .Select((line, i) => (line: line.Trim(), i))
        .Where(x =>
            Regex.IsMatch(x.line, @"^\d+$")
            && x.i + 2 < inputLines.Length
            && inputLines[x.i + 2].Contains(" · ")
        )
        .Select(x => inputLines[x.i + 1].Trim())
        .ToArray();

    using var youtube = new YoutubeClient();

    var channelResults = await youtube.Search.GetChannelsAsync(channelName).CollectAsync(5);
    var channel =
        channelResults.FirstOrDefault(c => NormalizeTitle(c.Title) == NormalizeTitle(channelName))
        ?? channelResults.FirstOrDefault();

    if (channel is null)
    {
        Console.WriteLine($"Channel '{channelName}' not found.");
        return 2;
    }

    Console.WriteLine($"Reading uploads of '{channel.Title}' to match {titles.Length} titles...");

    var uploads = new List<IVideo>();
    await foreach (var upload in youtube.Channels.GetUploadsAsync(channel.Id))
        uploads.Add(upload);

    var uploadsByTitle = uploads
        .GroupBy(v => NormalizeTitle(v.Title))
        .ToDictionary(g => g.Key, g => g.First());

    var videos = new List<IVideo>();
    foreach (var title in titles)
    {
        var key = NormalizeTitle(title);
        var video =
            uploadsByTitle.GetValueOrDefault(key)
            ?? uploads.FirstOrDefault(v =>
                NormalizeTitle(v.Title).StartsWith(key, StringComparison.Ordinal)
            );

        if (video is null)
            Console.WriteLine($"Not found among uploads: {title}");
        else if (videos.All(v => v.Id != video.Id))
            videos.Add(video);
    }

    var listTitle = $"{channelName} - Most Watched";
    var failedCount = await DownloadListAsync(
        listTitle,
        videos.Select(v => (v.Id.Value, v.Title, (IVideo?)v)).ToArray(),
        WriteXml(channel.Id.Value, channel.Url, listTitle, channel.Title, videos)
    );

    return failedCount > 0 ? 3 : 0;
}

// Skips live streams, very long replays and re-uploads of the same title
async Task<IReadOnlyList<IVideo>> GetNewestUploadsAsync(
    YoutubeClient youtube,
    ChannelId channelId,
    IReadOnlyList<IVideo> excluded,
    int count
)
{
    if (count <= 0)
        return [];

    var excludedIds = excluded.Select(v => v.Id.Value).ToHashSet(StringComparer.Ordinal);
    var seenTitles = excluded.Select(v => NormalizeTitle(v.Title)).ToHashSet();

    return (
        await youtube.Channels.GetUploadsAsync(channelId).CollectAsync(count * 5 + excluded.Count)
    )
        .Where(v =>
            !excludedIds.Contains(v.Id.Value)
            && v.Duration is { } duration
            && duration > TimeSpan.Zero
            && duration <= TimeSpan.FromHours(3)
            && seenTitles.Add(NormalizeTitle(v.Title))
        )
        .Take(count)
        .Cast<IVideo>()
        .ToArray();
}

static string[] GetTitleWords(string title) =>
    Regex.Split(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToArray();

// Exact title, then prefix, then "every word of the given title appears in the video title"
// (lists often shorten titles or drop a middle section)
static IVideo? MatchTitle(IEnumerable<IVideo> candidates, string title)
{
    var key = NormalizeTitle(title);
    var words = GetTitleWords(title);
    var list = candidates.ToArray();

    return list.FirstOrDefault(v => NormalizeTitle(v.Title) == key)
        ?? list.FirstOrDefault(v =>
            NormalizeTitle(v.Title).StartsWith(key, StringComparison.Ordinal)
            || key.StartsWith(NormalizeTitle(v.Title), StringComparison.Ordinal)
        )
        ?? (
            words.Length >= 3
                ? list.FirstOrDefault(v => !words.Except(GetTitleWords(v.Title)).Any())
                : null
        );
}

async Task<IVideo?> FindChannelVideoAsync(YoutubeClient youtube, Channel channel, string title)
{
    var results = await youtube.Search.GetVideosAsync($"{title} {channel.Title}").CollectAsync(30);

    if (MatchTitle(results.Where(v => v.Author.ChannelId == channel.Id), title) is { } match)
        return match;

    var uploads = new List<IVideo>();
    await foreach (var upload in youtube.Channels.GetUploadsAsync(channel.Id))
        uploads.Add(upload);

    return MatchTitle(uploads, title);
}

// Top subscriptions list: each channel's most popular video and Short, added to its channel folder
if (inputLines.Any(l => l.Trim() == "Top video"))
{
    var entries = new List<(string Name, string Handle, string? TopVideo, string? TopShort)>();
    for (var i = 0; i < inputLines.Length; i++)
    {
        if (inputLines[i].Trim() != "Top video")
            continue;

        var handleIndex = Enumerable
            .Range(0, i)
            .Reverse()
            .FirstOrDefault(j => inputLines[j].Trim().StartsWith('@'), -1);

        if (handleIndex < 1)
            continue;

        var shortIndex = Enumerable
            .Range(i, inputLines.Length - i)
            .FirstOrDefault(j => inputLines[j].Trim() == "Top Short", -1);

        var topShort =
            shortIndex >= 0 && shortIndex + 1 < inputLines.Length
                ? inputLines[shortIndex + 1].Trim()
                : null;

        entries.Add(
            (
                inputLines[handleIndex - 1].Trim(),
                inputLines[handleIndex].Trim(),
                i + 1 < inputLines.Length ? inputLines[i + 1].Trim() : null,
                topShort?.StartsWith("No Shorts", StringComparison.OrdinalIgnoreCase) == true
                    ? null
                    : topShort
            )
        );
    }

    // Channel folders made earlier are recognized by the channel ID in their XML
    var existingFolders = new Dictionary<string, string>(StringComparer.Ordinal);
    if (Directory.Exists(parentDirPath))
    {
        foreach (
            var xmlFilePath in Directory.EnumerateFiles(
                parentDirPath,
                "*.xml",
                SearchOption.AllDirectories
            )
        )
        {
            try
            {
                if ((string?)XDocument.Load(xmlFilePath).Root?.Attribute("Id") is { } id)
                    existingFolders.TryAdd(
                        id,
                        Path.GetFileName(Path.GetDirectoryName(xmlFilePath)!)
                    );
            }
            catch { }
        }
    }

    var totalFailedCount = 0;
    using var youtube = new YoutubeClient();
    foreach (var (i, entry) in entries.Index())
    {
        Channel channel;
        try
        {
            channel = await youtube.Channels.GetByHandleAsync(
                ChannelHandle.Parse($"https://www.youtube.com/{entry.Handle}")
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[{i + 1}/{entries.Count}] '{entry.Name}': channel not found ({ex.Message.Split('\n')[0]})"
            );
            continue;
        }

        var videos = new List<IVideo>();
        foreach (var title in new[] { entry.TopVideo, entry.TopShort }.OfType<string>())
        {
            var video = await FindChannelVideoAsync(youtube, channel, title);
            if (video is null)
                Console.WriteLine($"  not found on the channel: {title}");
            else if (videos.All(v => v.Id != video.Id))
                videos.Add(video);
        }

        var existingFolder = existingFolders.GetValueOrDefault(channel.Id.Value);
        if (existingFolder is null)
            videos.AddRange(await GetNewestUploadsAsync(youtube, channel.Id, videos, extraCount));

        if (videos.Count == 0)
        {
            Console.WriteLine($"[{i + 1}/{entries.Count}] '{entry.Name}': nothing found, skipping");
            continue;
        }

        var listTitle = existingFolder is not null
            ? $"{existingFolder} - Most Popular"
            : entry.Name;

        Console.WriteLine(
            existingFolder is not null
                ? $"[{i + 1}/{entries.Count}] '{entry.Name}': {videos.Count} most popular into existing folder '{existingFolder}'"
                : $"[{i + 1}/{entries.Count}] '{entry.Name}': new folder with {videos.Count} videos"
        );

        totalFailedCount += await DownloadListAsync(
            listTitle,
            videos.Select(v => (v.Id.Value, v.Title, (IVideo?)v)).ToArray(),
            WriteXml(channel.Id.Value, channel.Url, listTitle, channel.Title, videos),
            existingFolder ?? entry.Name
        );
    }

    return totalFailedCount > 0 ? 3 : 0;
}

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
    foreach (var (i, channelLine) in channelNames.Index())
    {
        // "Name > https://www.youtube.com/channel/UC..." names the channel exactly, no search
        var linkMatch = Regex.Match(channelLine, @"^(.*?)\s*>\s*\S*/channel/(UC[\w-]{22})");
        var channelName = linkMatch.Success ? linkMatch.Groups[1].Value.Trim() : channelLine;
        ChannelId? linkedChannelId = null;
        if (linkMatch.Success)
            linkedChannelId = ChannelId.Parse(linkMatch.Groups[2].Value);

        var watched = historyVideos
            .Where(v =>
                linkedChannelId is { } id
                    ? v.Author.ChannelId == id
                    : string.Equals(
                        NormalizeName(v.Author.ChannelTitle),
                        NormalizeName(channelName),
                        StringComparison.OrdinalIgnoreCase
                    )
            )
            .ToList();

        var channelId = linkedChannelId ?? watched.FirstOrDefault()?.Author.ChannelId;
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

        IReadOnlyList<IVideo> extras;
        try
        {
            extras = await GetNewestUploadsAsync(youtube, resolvedChannelId, watched, extraCount);
        }
        catch (YoutubeExplodeException ex)
        {
            // Some channels' upload lists can't be read; still keep the watched videos
            Console.WriteLine(
                $"[{i + 1}/{channelNames.Length}] '{channelName}': couldn't read newest uploads ({ex.Message.Split('\n')[0].Trim()})"
            );
            extras = [];
        }

        var videos = watched.Concat(extras).ToArray();
        Console.WriteLine(
            $"[{i + 1}/{channelNames.Length}] '{channelName}': {watched.Count} watched + {extras.Count} newest uploads"
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
if (
    File.Exists(input)
    && (
        input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || input.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
    )
)
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

    // A link that can't be opened (e.g. a private playlist) falls back to a search for its
    // text, which would download unrelated videos
    if (result.Kind == QueryResultKind.Search)
    {
        Console.WriteLine(
            "Couldn't open this link (private or unavailable), so nothing was downloaded."
        );
        return 2;
    }

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
