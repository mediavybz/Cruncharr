using System.Threading.Channels;
using Cruncharr.API.Controllers;
using Cruncharr.Core.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.API.Services;

public class QueueBroadcastService
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100) {
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly IQueueService _queueService;
    private readonly ILogger<QueueBroadcastService> _logger;
    private readonly JsonSerializerSettings _sseJsonSettings;

    public ChannelReader<string> Reader => _channel.Reader;

    public QueueBroadcastService(IQueueService queueService, ILogger<QueueBroadcastService> logger)
    {
        _queueService = queueService;
        _logger = logger;
        _sseJsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore
        };

        _queueService.QueueStateChanged += OnQueueStateChanged;
    }

    private void OnQueueStateChanged(object? sender, EventArgs e)
    {
        try
        {
            var queue = _queueService.GetQueue();
            var response = new QueueResponse
            {
                Items = queue,
                ActiveDownloads = _queueService.ActiveDownloads,
                HasActiveDownloads = _queueService.HasActiveDownloads
            };
            var json = JsonConvert.SerializeObject(response, _sseJsonSettings);
            _channel.Writer.TryWrite(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast queue update");
        }
    }
}
