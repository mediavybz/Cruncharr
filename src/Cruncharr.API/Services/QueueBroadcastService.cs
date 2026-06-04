using System.Collections.Concurrent;
using System.Threading.Channels;
using Cruncharr.API.Controllers;
using Cruncharr.Core.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.API.Services;

public class QueueBroadcastService : IDisposable
{
    private readonly ConcurrentDictionary<Guid, ChannelWriter<string>> _clients = new();
    private readonly IQueueService _queueService;
    private readonly ILogger<QueueBroadcastService> _logger;
    private readonly JsonSerializerSettings _sseJsonSettings;

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

    public void Dispose()
    {
        _queueService.QueueStateChanged -= OnQueueStateChanged;
    }

    public ChannelReader<string> Subscribe(Guid clientId)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _clients[clientId] = channel.Writer;
        return channel.Reader;
    }

    public void Unsubscribe(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var writer))
        {
            writer.Complete();
        }
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
                HasActiveDownloads = _queueService.HasActiveDownloads,
                IsGloballyPaused = _queueService.IsGloballyPaused
            };
            var json = JsonConvert.SerializeObject(response, _sseJsonSettings);
            
            foreach (var client in _clients.Values){
                client.TryWrite(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast queue update");
        }
    }
}
