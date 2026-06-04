using System.Globalization;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Cruncharr.Core.Services;

public interface IChapterService
{
    Task<List<string>> GetChaptersAsync(string mediaId, string? accessToken, CancellationToken cancellationToken = default);
    Task<string?> WriteChapterFileAsync(List<string> chapters, string outputPath, CancellationToken cancellationToken = default);
}

public class ChapterService : IChapterService
{
    private readonly HttpClientWrapper _httpClient;
    private readonly ILogger<ChapterService>? _logger;

    public ChapterService(HttpClientWrapper httpClient, ILogger<ChapterService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<string>> GetChaptersAsync(string mediaId, string? accessToken, CancellationToken cancellationToken = default)
    {
        var compiledChapters = new List<string>();
        await TryGetNewApiChaptersAsync(mediaId, accessToken, compiledChapters, cancellationToken);
        return compiledChapters;
    }

    private async Task<bool> TryGetNewApiChaptersAsync(string mediaId, string? accessToken, List<string> compiledChapters, CancellationToken cancellationToken)
    {
        try
        {
            var request = HttpClientWrapper.CreateRequest(
                $"https://static.crunchyroll.com/skip-events/production/{mediaId}.json",
                HttpMethod.Get,
                true,
                accessToken);

            var (isOk, content, error) = await _httpClient.SendRequestAsync(request, false);

            if (!isOk)
            {
                _logger?.LogWarning("Skip-events API failed for {MediaId}: {Error}", mediaId, error);
                return false;
            }

            var chapterData = new CrunchyChapters { Chapters = [] };

            try
            {
                var jObject = JObject.Parse(content);

                if (jObject.TryGetValue("lastUpdate", out var lastUpdateToken))
                {
                    chapterData.lastUpdate = lastUpdateToken.ToObject<DateTime>();
                }

                if (jObject.TryGetValue("mediaId", out var mediaIdToken))
                {
                    chapterData.mediaId = mediaIdToken.ToObject<string>();
                }

                foreach (var property in jObject.Properties())
                {
                    if (property.Value.Type == JTokenType.Object &&
                        property.Name != "lastUpdate" &&
                        property.Name != "mediaId")
                    {
                        try
                        {
                            var chapter = property.Value.ToObject<CrunchyChapter>() ?? new CrunchyChapter();
                            chapterData.Chapters.Add(chapter);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Error parsing chapter property");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error parsing skip-events JSON for {MediaId}", mediaId);
                return false;
            }

            if (chapterData.Chapters.Count == 0)
            {
                return false;
            }

            // Sort chapters by start time
            chapterData.Chapters.Sort((a, b) =>
            {
                if (a.start.HasValue && b.start.HasValue)
                {
                    return a.start.Value.CompareTo(b.start.Value);
                }
                return 0;
            });

            // Add default Episode chapter if no intro/recap
            if (!(chapterData.Chapters.Any(c => c.type == "intro") || chapterData.Chapters.Any(c => c.type == "recap")))
            {
                int chapterNumber = (compiledChapters.Count / 2) + 1;
                compiledChapters.Add($"CHAPTER{chapterNumber}=00:00:00.00");
                compiledChapters.Add($"CHAPTER{chapterNumber}NAME=Episode");
            }

            // Process each chapter
            foreach (var chapter in chapterData.Chapters)
            {
                if (chapter.start == null || chapter.end == null) continue;

                var startTime = TimeSpan.FromSeconds(chapter.start.Value);
                var endTime = TimeSpan.FromSeconds(chapter.end.Value);

                var startFormatted = startTime.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                var endFormatted = endTime.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

                int chapterNumber = (compiledChapters.Count / 2) + 1;

                if (chapter.type == "intro")
                {
                    if (chapter.start > 0)
                    {
                        compiledChapters.Add($"CHAPTER{chapterNumber}=00:00:00.00");
                        compiledChapters.Add($"CHAPTER{chapterNumber}NAME=Prologue");
                    }

                    chapterNumber = (compiledChapters.Count / 2) + 1;
                    compiledChapters.Add($"CHAPTER{chapterNumber}={startFormatted}");
                    compiledChapters.Add($"CHAPTER{chapterNumber}NAME=Opening");
                    chapterNumber = (compiledChapters.Count / 2) + 1;
                    compiledChapters.Add($"CHAPTER{chapterNumber}={endFormatted}");
                    compiledChapters.Add($"CHAPTER{chapterNumber}NAME=Episode");
                }
                else
                {
                    string formattedChapterType = char.ToUpper(chapter.type[0]) + chapter.type.Substring(1);
                    chapterNumber = (compiledChapters.Count / 2) + 1;
                    compiledChapters.Add($"CHAPTER{chapterNumber}={startFormatted}");
                    compiledChapters.Add($"CHAPTER{chapterNumber}NAME={formattedChapterType} Start");
                    chapterNumber = (compiledChapters.Count / 2) + 1;
                    compiledChapters.Add($"CHAPTER{chapterNumber}={endFormatted}");
                    compiledChapters.Add($"CHAPTER{chapterNumber}NAME={formattedChapterType} End");
                }
            }

            _logger?.LogInformation("Found {Count} chapters for {MediaId}", chapterData.Chapters.Count, mediaId);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error fetching skip-events for {MediaId}", mediaId);
            return false;
        }
    }

    public async Task<string?> WriteChapterFileAsync(List<string> chapters, string outputPath, CancellationToken cancellationToken = default)
    {
        if (chapters.Count == 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllLinesAsync(outputPath, chapters, cancellationToken);
            _logger?.LogDebug("Wrote chapter file to {Path}", outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to write chapter file to {Path}", outputPath);
            return null;
        }
    }
}