using System.Text.Json.Serialization;

namespace Cruncharr.Core.Models;

[JsonSerializable(typeof(List<HistorySeries>))]
[JsonSerializable(typeof(HistorySeries))]
[JsonSerializable(typeof(HistorySeason))]
[JsonSerializable(typeof(HistoryEpisode))]
[JsonSerializable(typeof(List<DownloadHistory>))]
[JsonSerializable(typeof(DownloadHistory))]
public partial class HistoryJsonContext : JsonSerializerContext
{
}
