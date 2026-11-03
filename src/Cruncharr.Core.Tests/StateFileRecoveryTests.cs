using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Services;

namespace Cruncharr.Core.Tests;

// Audit F05: a damaged History or queue file was loaded as an empty list, and the next write
// replaced it (for History, also pushing the damaged file into today's backup slot).
public class StateFileRecoveryTests : IDisposable
{
    private const string Garbage = "{\"broken\": [";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-state-recovery-");
    private string HistoryPath => Path.Combine(_root.FullName, "history.json");
    private string FlatPath => Path.Combine(_root.FullName, "history-flat.json");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task DamagedHistory_IsRestoredFromTheNewestBackup()
    {
        await SeedHistoryAsync("E1");
        var (richE1, flatE1) = (File.ReadAllBytes(HistoryPath), File.ReadAllBytes(FlatPath));
        await SeedHistoryAsync("E2");
        foreach (var bak in Directory.GetFiles(_root.FullName, ".*.bak")) File.Delete(bak);
        File.WriteAllBytes(Backup(HistoryPath, "2026-10-08"), richE1);
        File.WriteAllBytes(Backup(FlatPath, "2026-10-08"), flatE1);
        File.Copy(HistoryPath, Backup(HistoryPath, "2026-10-09"));
        File.Copy(FlatPath, Backup(FlatPath, "2026-10-09"));
        // The newest backups are damaged too and must be skipped.
        File.WriteAllText(Backup(HistoryPath, "2026-10-10"), Garbage);
        File.WriteAllText(Backup(FlatPath, "2026-10-10"), Garbage);
        File.WriteAllText(HistoryPath, Garbage);
        File.WriteAllText(FlatPath, Garbage);

        using var history = new HistoryService(HistoryPath, config: new CruncharrConfig());

        Assert.Equal(["E1", "E2"], await RichEpisodeIdsAsync(history));
        Assert.Equal(["E1", "E2"], (await history.GetAllAsync(0, 100)).Select(h => h.EpisodeId).Order().ToList());
        Assert.Equal(Garbage, File.ReadAllText(Assert.Single(Directory.GetFiles(_root.FullName, "history.json.corrupt-*"))));
        Assert.Equal(Garbage, File.ReadAllText(Assert.Single(Directory.GetFiles(_root.FullName, "history-flat.json.corrupt-*"))));
    }

    [Fact]
    public async Task DamagedHistoryWithoutBackup_FailsAndIsNeverOverwritten()
    {
        File.WriteAllText(HistoryPath, Garbage);
        File.WriteAllText(FlatPath, Garbage);
        using var history = new HistoryService(HistoryPath, config: new CruncharrConfig());

        await Assert.ThrowsAsync<InvalidDataException>(() => history.GetHistorySeriesAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => history.UpdateWithSeasonDataAsync([Episode("E9")]));
        await Assert.ThrowsAsync<InvalidDataException>(() => history.GetAllAsync(0, 100));
        await Assert.ThrowsAsync<InvalidDataException>(() => history.AddAsync(new DownloadHistory { EpisodeId = "E9" }));

        Assert.Equal(Garbage, File.ReadAllText(HistoryPath));
        Assert.Equal(Garbage, File.ReadAllText(FlatPath));
        Assert.Equal(2, _root.GetFiles().Length);
    }

    [Fact]
    public async Task UnreadableHistory_FailsWithoutTouchingTheFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs mandatory file locking");
        await SeedHistoryAsync("E1");
        File.Copy(HistoryPath, Backup(HistoryPath, "2026-10-08"));
        var original = File.ReadAllBytes(HistoryPath);
        using var history = new HistoryService(HistoryPath, config: new CruncharrConfig());

        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => history.GetHistorySeriesAsync());

        Assert.Equal(original, File.ReadAllBytes(HistoryPath));
        Assert.Empty(Directory.GetFiles(_root.FullName, "*.corrupt-*"));
        // Once the file is readable again, the same instance loads it.
        Assert.Equal(["E1"], await RichEpisodeIdsAsync(history));
    }

    [Fact]
    public void DamagedQueue_IsKeptAsideBeforeAnySave()
    {
        var path = Path.Combine(_root.FullName, "queue.json");
        File.WriteAllText(path, Garbage);
        var persistence = new QueuePersistenceService(path);

        Assert.Null(persistence.LoadQueue());
        Assert.Equal(Garbage, File.ReadAllText(Assert.Single(Directory.GetFiles(_root.FullName, "queue.json.corrupt-*"))));

        Assert.True(persistence.SaveQueue([Item("NEW")]));
        Assert.Equal("NEW", Assert.Single(persistence.LoadQueue()!).Episode.Id);
    }

    [Fact]
    public void UnreadableQueue_RefusesSavesAndDeletes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs mandatory file locking");
        var path = Path.Combine(_root.FullName, "queue.json");
        var saved = new QueuePersistenceService(path);
        Assert.True(saved.SaveQueue([Item("SAVED")]));
        var original = File.ReadAllText(path);

        var persistence = new QueuePersistenceService(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(persistence.LoadQueue());

        Assert.False(persistence.SaveQueue([Item("NEW")]));
        Assert.False(persistence.SaveQueue([]));
        persistence.DeleteQueue();
        Assert.Equal(original, File.ReadAllText(path));
    }

    private async Task SeedHistoryAsync(string episodeId)
    {
        using var history = new HistoryService(HistoryPath, config: new CruncharrConfig());
        await history.UpdateWithSeasonDataAsync([Episode(episodeId)]);
        await history.AddAsync(new DownloadHistory { EpisodeId = episodeId, SeriesId = "SER" });
    }

    private static string Backup(string path, string date) =>
        Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{date}.bak");

    private static async Task<List<string?>> RichEpisodeIdsAsync(HistoryService history) =>
        (await history.GetHistorySeriesAsync())
            .SelectMany(s => s.Seasons).SelectMany(s => s.EpisodesList)
            .Select(e => e.EpisodeId).Order(StringComparer.Ordinal).ToList();

    private static EpisodeInfo Episode(string id) => new()
    {
        Id = id,
        SeriesId = "SER",
        SeriesTitle = "Recovery Show",
        SeasonId = "SEA",
        SeasonTitle = "Season 1",
        SeasonNumber = 1,
        EpisodeNumber = 1,
        Title = id
    };

    private static QueueItem Item(string id) => new() { Episode = new EpisodeInfo { Id = id, Title = id } };
}
