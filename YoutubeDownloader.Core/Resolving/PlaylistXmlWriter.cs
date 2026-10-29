using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using PowerKit.Extensions;
using YoutubeExplode.Videos;

namespace YoutubeDownloader.Core.Resolving;

public static class PlaylistXmlWriter
{
    public static string GetFileName(ResolvedPlaylist playlist) =>
        Path.EscapeFileName(playlist.Playlist.Title.Trim()) + ".xml";

    public static Task WriteAsync(
        string filePath,
        ResolvedPlaylist playlist,
        IReadOnlyDictionary<string, string>? videoFilePaths = null,
        CancellationToken cancellationToken = default
    ) =>
        WriteAsync(
            filePath,
            playlist.Playlist.Id.Value,
            playlist.Playlist.Url,
            playlist.Playlist.Title,
            playlist.Playlist.Author?.ChannelTitle ?? "",
            playlist.Videos,
            videoFilePaths,
            cancellationToken
        );

    public static async Task WriteAsync(
        string filePath,
        string id,
        string url,
        string title,
        string author,
        IReadOnlyList<IVideo> videos,
        IReadOnlyDictionary<string, string>? videoFilePaths = null,
        CancellationToken cancellationToken = default
    )
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "Playlist",
                new XAttribute("Id", id),
                new XAttribute("Url", url),
                new XAttribute("Count", videos.Count),
                new XAttribute("SavedAt", DateTimeOffset.Now.ToString("o")),
                new XElement("Title", title),
                new XElement("Author", author),
                new XElement(
                    "Videos",
                    videos.Select(
                        (video, i) =>
                            new XElement(
                                "Video",
                                new XAttribute("Position", i + 1),
                                new XAttribute("Id", video.Id.Value),
                                new XElement("Title", video.Title),
                                new XElement("Author", video.Author.ChannelTitle),
                                new XElement("Duration", video.Duration?.ToString() ?? ""),
                                new XElement("Url", video.Url),
                                videoFilePaths?.TryGetValue(video.Id.Value, out var videoFilePath)
                                == true
                                    ? new XElement("FileName", Path.GetFileName(videoFilePath))
                                    : null
                            )
                    )
                )
            )
        );

        Directory.CreateForFile(filePath);

        await using var stream = File.Create(filePath);
        await document.SaveAsync(stream, SaveOptions.None, cancellationToken);
    }
}
