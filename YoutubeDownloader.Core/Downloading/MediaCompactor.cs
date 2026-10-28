using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace YoutubeDownloader.Core.Downloading;

public static class MediaCompactor
{
    private static async Task RunFFmpegAsync(
        string ffmpegPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start FFmpeg.");

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed: {await errorTask}");
    }

    private static IEnumerable<string> GetArguments(
        string inputFilePath,
        string outputFilePath,
        bool shouldEncodeVideo
    )
    {
        yield return "-y";
        yield return "-nostdin";
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-i";
        yield return inputFilePath;
        yield return "-map";
        yield return "0";
        yield return "-c";
        yield return "copy";

        if (shouldEncodeVideo)
        {
            yield return "-c:v";
            yield return "libx265";
            yield return "-crf";
            yield return "28";
            yield return "-preset";
            yield return "fast";
            yield return "-tag:v";
            yield return "hvc1";
        }

        yield return "-c:a";
        yield return "aac";
        yield return "-b:a";
        yield return "64k";
        yield return "-movflags";
        yield return "+faststart";
        yield return outputFilePath;
    }

    public static async Task CompactForAppleAsync(
        string filePath,
        bool shouldEncodeVideo,
        string? ffmpegPath = null,
        CancellationToken cancellationToken = default
    )
    {
        ffmpegPath ??= FFmpeg.TryGetCliFilePath() ?? "ffmpeg";
        var tempFilePath = filePath + ".compact.mp4";

        try
        {
            try
            {
                await RunFFmpegAsync(
                    ffmpegPath,
                    GetArguments(filePath, tempFilePath, shouldEncodeVideo),
                    cancellationToken
                );
            }
            catch (InvalidOperationException) when (shouldEncodeVideo)
            {
                await RunFFmpegAsync(
                    ffmpegPath,
                    GetArguments(filePath, tempFilePath, false),
                    cancellationToken
                );
            }

            if (new FileInfo(tempFilePath).Length < new FileInfo(filePath).Length)
                File.Move(tempFilePath, filePath, true);
        }
        finally
        {
            if (File.Exists(tempFilePath))
                File.Delete(tempFilePath);
        }
    }
}
