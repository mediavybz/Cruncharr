using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Cruncharr.Core.Configuration;
using Cruncharr.Core.Models;
using Cruncharr.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Cruncharr.Core.Services;

public interface INotificationService{
    Task NotifyCompleteAsync(DownloadResult result, CruncharrConfig config);
    Task NotifyErrorAsync(DownloadResult result, CruncharrConfig config);
    Task NotifyQueueCompleteAsync(List<DownloadResult> results, CruncharrConfig config);
    Task NotifyQueueCompleteAsync(CruncharrConfig config);
}

public class NotificationService : INotificationService{
    private readonly ILogger<NotificationService>? _logger;
    private readonly HttpClient _httpClient;
    
    public NotificationService(IHttpClientFactory httpClientFactory, ILogger<NotificationService>? logger = null){
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }
    
    public async Task NotifyCompleteAsync(DownloadResult result, CruncharrConfig config){
        if (!config.Notifications.WebhookEnabled) return;
        if (string.IsNullOrEmpty(config.Notifications.WebhookUrl)) return;
        if (!config.Notifications.OnComplete) return;
        
        _logger?.LogInformation("Sending completion notification to {WebhookUrl}", config.Notifications.WebhookUrl);
        
        var payload = new{
            event_type = "download_complete",
            success = result.Success,
            episode = result.Episode,
            output_path = result.OutputPath,
            timestamp = DateTime.UtcNow
        };
        
        await SendWebhookAsync(config, payload);
    }
    
    public async Task NotifyErrorAsync(DownloadResult result, CruncharrConfig config){
        if (!config.Notifications.WebhookEnabled) return;
        if (string.IsNullOrEmpty(config.Notifications.WebhookUrl)) return;
        if (!config.Notifications.OnError) return;
        
        _logger?.LogError("Sending error notification to {WebhookUrl}: {Error}", config.Notifications.WebhookUrl, result.ErrorMessage);
        
        var payload = new{
            event_type = "download_error",
            success = false,
            error = result.ErrorMessage,
            episode = result.Episode,
            timestamp = DateTime.UtcNow
        };
        
        await SendWebhookAsync(config, payload);
    }
    
    public async Task NotifyQueueCompleteAsync(List<DownloadResult> results, CruncharrConfig config){
        if (!config.Notifications.WebhookEnabled) return;
        if (string.IsNullOrEmpty(config.Notifications.WebhookUrl)) return;
        if (!config.Notifications.NotifyQueueFinished) return;
        
        var successCount = results.Count(r => r.Success);
        var errorCount = results.Count - successCount;
        
        _logger?.LogInformation("Sending queue completion notification: {Success} succeeded, {Errors} failed", successCount, errorCount);
        
        var payload = new{
            event_type = "queue_complete",
            total = results.Count,
            succeeded = successCount,
            failed = errorCount,
            results = results.Select(r => new{
                success = r.Success,
                error = r.ErrorMessage,
                episode = r.Episode?.Title
            }),
            timestamp = DateTime.UtcNow
        };
        
        await SendWebhookAsync(config, payload);
    }
    
    public async Task NotifyQueueCompleteAsync(CruncharrConfig config){
        // Execute configured program on queue complete (ported from upstream NotificationDispatcher)
        if (config.Notifications?.DownloadFinishedExecute == true && 
            !string.IsNullOrWhiteSpace(config.Notifications.DownloadFinishedExecutePath)){
            try{
                var psi = new ProcessStartInfo{
                    FileName = config.Notifications.DownloadFinishedExecutePath,
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process != null){
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(cts.Token);
                }
                _logger?.LogInformation("Executed DownloadFinishedExecutePath: {Path}", config.Notifications.DownloadFinishedExecutePath);
            } catch (Exception ex){
                _logger?.LogError(ex, "Failed to execute DownloadFinishedExecutePath: {Path}", 
                    config.Notifications.DownloadFinishedExecutePath);
            }
        }
        
        // Also dispatch webhook if configured
        if (config.Notifications?.WebhookEnabled == true &&
            !string.IsNullOrEmpty(config.Notifications.WebhookUrl) &&
            config.Notifications.NotifyQueueFinished){
            var payload = new{
                event_type = "queue_complete",
                message = "All downloads completed",
                timestamp = DateTime.UtcNow
            };
            await SendWebhookAsync(config, payload);
        }
    }
    
    private async Task SendWebhookAsync(CruncharrConfig config, object payload){
        try{
            // SSRF protection: validate webhook URL before sending
            var webhookUrl = config.Notifications.WebhookUrl;
            if (string.IsNullOrEmpty(webhookUrl)){
                _logger?.LogWarning("Webhook URL is empty, skipping webhook");
                return;
            }
            if (!WebhookUrlValidator.IsValidWebhookUrl(webhookUrl, out var validationError)){
                _logger?.LogWarning("Webhook URL failed validation: {Error}", validationError);
                return;
            }
            
            var method = new HttpMethod(config.Notifications.WebhookMethod);
            var request = new HttpRequestMessage(method, config.Notifications.WebhookUrl);
            
            // Add configured headers
            if (config.Notifications.WebhookHeaders != null){
                foreach (var header in config.Notifications.WebhookHeaders){
                    if (!string.IsNullOrEmpty(header.Key) && !string.IsNullOrEmpty(header.Value)){
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
            
            // Use configured content type and body template if available
            var contentType = config.Notifications.WebhookContentType ?? "application/json";
            var bodyTemplate = config.Notifications.WebhookBodyTemplate;
            
            if (!string.IsNullOrEmpty(bodyTemplate)){
                // Simple template substitution
                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var substituted = bodyTemplate
                    .Replace("{{payload}}", json)
                    .Replace("{{timestamp}}", DateTime.UtcNow.ToString("O"));
                request.Content = new StringContent(substituted, Encoding.UTF8, contentType);
            } else {
                request.Content = JsonContent.Create(payload);
                if (contentType != "application/json"){
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                }
            }
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        } catch (Exception ex){
            _logger?.LogError(ex, "Failed to send webhook notification");
        }
    }
}