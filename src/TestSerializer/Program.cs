using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Cruncharr.Core.Models;

class Program {
    static void Main() {
        var response = new {
            items = new List<QueueItem> {
                new QueueItem {
                    Id = "test-id",
                    Episode = new EpisodeInfo { Id = "ep1", Title = "Test" },
                    DownloadProgress = new DownloadProgress {
                        State = DownloadState.Downloading,
                        Percent = 50,
                        Time = 123.45,
                        DownloadSpeedBytes = 1000000,
                        Doing = "Testing"
                    }
                }
            },
            activeDownloads = 1,
            hasActiveDownloads = true
        };
        
        var json = JsonConvert.SerializeObject(response, new JsonSerializerSettings {
            Converters = { new StringEnumConverter() },
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        });
        
        Console.WriteLine(json);
    }
}
