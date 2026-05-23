using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Cruncharr.Core.Utils.DRM;

public class WidevineCdm{
    private byte[] _privateKey = Array.Empty<byte>();
    private byte[] _identifierBlob = Array.Empty<byte>();

    public bool CanDecrypt { get; private set; }
    public string? WidevineDirectory { get; private set; }

    public WidevineCdm(string? widevineDirectory = null){
        WidevineDirectory = widevineDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "cruncharr", "widevine");
        LoadCdmFiles();
    }

    public void Reload(string? widevineDirectory = null){
        if (widevineDirectory != null){
            WidevineDirectory = widevineDirectory;
        }
        LoadCdmFiles();
    }

    private void LoadCdmFiles(){
        try{
            if (Directory.Exists(WidevineDirectory)){
                foreach (var file in Directory.EnumerateFiles(WidevineDirectory)){
                    var fileInfo = new FileInfo(file);

                    if (fileInfo.Length >= 1024 * 8 || fileInfo.Attributes.HasFlag(FileAttributes.Directory))
                        continue;

                    string fileContents = File.ReadAllText(file, Encoding.UTF8);

                    if (IsPrivateKey(fileContents)){
                        _privateKey = File.ReadAllBytes(file);
                    } else if (IsWidevineIdentifierBlob(fileContents)){
                        _identifierBlob = File.ReadAllBytes(file);
                    }
                }
            }

            if (_privateKey.Length > 0 && _identifierBlob.Length > 0){
                CanDecrypt = true;
            } else{
                CanDecrypt = false;
                if (_privateKey.Length == 0){
                    Console.Error.WriteLine("Private key missing");
                }
                if (_identifierBlob.Length == 0){
                    Console.Error.WriteLine("Identifier blob missing");
                }
            }
        } catch (IOException ioEx){
            Console.Error.WriteLine("I/O error accessing Widevine files: " + ioEx);
            CanDecrypt = false;
        } catch (UnauthorizedAccessException uaEx){
            Console.Error.WriteLine("Permission error accessing Widevine files: " + uaEx);
            CanDecrypt = false;
        } catch (Exception ex){
            Console.Error.WriteLine("Unexpected Widevine error: " + ex);
            CanDecrypt = false;
        }

        Console.WriteLine($"CDM available: {CanDecrypt}");
    }

    private bool IsPrivateKey(string content){
        return content.Contains("-BEGIN RSA PRIVATE KEY-", StringComparison.Ordinal) ||
               content.Contains("-BEGIN PRIVATE KEY-", StringComparison.Ordinal);
    }

    private bool IsWidevineIdentifierBlob(string content){
        return content.Contains("widevine_cdm_version", StringComparison.Ordinal);
    }

    public async Task<List<ContentKey>> GetKeysAsync(string? pssh, string licenseServer, Dictionary<string, string> authData, HttpClient httpClient){
        if (pssh == null || !CanDecrypt){
            Console.Error.WriteLine("Missing pssh or cdm files");
            return new List<ContentKey>();
        }

        try{
            byte[] psshBuffer = Convert.FromBase64String(pssh);

            Session ses = new Session(new ContentDecryptionModule{ identifierBlob = _identifierBlob, privateKey = _privateKey }, psshBuffer);

            var playbackRequest = new HttpRequestMessage(HttpMethod.Post, licenseServer);
            foreach (var kvp in authData){
                playbackRequest.Headers.Add(kvp.Key, kvp.Value);
            }

            var licenceReq = ses.GetLicenseRequest();
            var content = new ByteArrayContent(licenceReq);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            playbackRequest.Content = content;

            var (isOk, responseContent, error) = await SendWithRetryAsync(httpClient, playbackRequest);

            if (!isOk){
                Console.Error.WriteLine("Failed to get Keys!");
                return new List<ContentKey>();
            }

            var resp = JsonConvert.DeserializeObject<LicenceReqResp>(responseContent) ?? new LicenceReqResp();

            ses.ProvideLicense(Convert.FromBase64String(resp.license));

            return ses.ContentKeys;
        } catch (Exception e){
            Console.Error.WriteLine(e);
            return new List<ContentKey>();
        }
    }

    private static async Task<(bool IsOk, string ResponseContent, string Error)> SendWithRetryAsync(HttpClient httpClient, HttpRequestMessage request){
        string content = string.Empty;
        for (var attempt = 0; attempt < 4; attempt++){
            try{
                using var requestClone = await CloneHttpRequestAsync(request);
                var response = await httpClient.SendAsync(requestClone);
                content = await response.Content.ReadAsStringAsync();
                response.EnsureSuccessStatusCode();
                return (true, content, "");
            } catch (Exception ex){
                if (ex.Message.Contains("SocketException") && attempt < 3){
                    Console.Error.WriteLine($"Key Request Attempt {attempt + 1} failed.");
                    await Task.Delay(1000);
                } else{
                    return (false, content, ex.Message);
                }
            }
        }
        return (false, content, "Max retries exceeded");
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestAsync(HttpRequestMessage request){
        var clone = new HttpRequestMessage(request.Method, request.RequestUri){
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers){
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null){
            var contentBytes = await request.Content.ReadAsByteArrayAsync();
            var newContent = new ByteArrayContent(contentBytes);
            foreach (var header in request.Content.Headers){
                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = newContent;
        }

        return clone;
    }
}

public class LicenceReqResp{
    public string status{ get; set; } = "";
    public string license{ get; set; } = "";
    public string platform{ get; set; } = "";
    public string message_type{ get; set; } = "";
}
