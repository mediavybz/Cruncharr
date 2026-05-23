using Cruncharr.Core.Configuration;
using Xunit;

namespace Cruncharr.Core.Tests;

public class ConfigurationTests{
    [Fact]
    public void LoadConfig_WithMissingFile_ReturnsDefaults(){
        var config = CruncharrConfig.Load("/nonexistent/path.json");
        
        Assert.Equal("/downloads", config.Download.OutputDirectory);
        Assert.Equal("best", config.Download.Quality);
        Assert.Equal("ja-JP", config.Download.DefaultAudio);
    }
    
    [Fact]
    public void SaveAndLoadConfig_PreservesValues(){
        var tempFile = Path.GetTempFileName();
        var original = new CruncharrConfig{
            Crunchyroll = new CrunchyrollConfig{
                Email = "test@example.com",
                Password = "secret"
            },
            Download = new DownloadConfig{
                OutputDirectory = "/custom/downloads",
                Quality = "1080p"
            }
        };
        
        original.Save(tempFile);
        var loaded = CruncharrConfig.Load(tempFile);
        
        Assert.Equal("test@example.com", loaded.Crunchyroll.Email);
        Assert.Equal("secret", loaded.Crunchyroll.Password);
        Assert.Equal("/custom/downloads", loaded.Download.OutputDirectory);
        Assert.Equal("1080p", loaded.Download.Quality);
        
        File.Delete(tempFile);
    }
    
    [Fact]
    public void ApplyEnvironmentVariables_OverridesConfig(){
        var config = new CruncharrConfig{
            Crunchyroll = new CrunchyrollConfig{
                Email = "old@example.com"
            },
            Download = new DownloadConfig{
                OutputDirectory = "/old/path"
            }
        };
        
        Environment.SetEnvironmentVariable("CRUNCHYROLL_EMAIL", "new@example.com");
        Environment.SetEnvironmentVariable("CRUNCHYROLL_OUTPUT_DIR", "/new/path");
        
        config.ApplyEnvironmentVariables();
        
        Assert.Equal("new@example.com", config.Crunchyroll.Email);
        Assert.Equal("/new/path", config.Download.OutputDirectory);
        
        // Cleanup
        Environment.SetEnvironmentVariable("CRUNCHYROLL_EMAIL", null);
        Environment.SetEnvironmentVariable("CRUNCHYROLL_OUTPUT_DIR", null);
    }
    
    [Fact]
    public void YamlConfig_LoadsCorrectly(){
        var tempFile = Path.GetTempFileName() + ".yml";
        var yaml = @"
crunchyroll:
  email: yaml-test@example.com
download:
  output_dir: /yaml/output
  quality: 720p
  dub_languages:
    - ja-JP
    - en-US
";
        File.WriteAllText(tempFile, yaml);
        
        var config = CruncharrConfig.Load(tempFile);
        
        Assert.Equal("yaml-test@example.com", config.Crunchyroll.Email);
        Assert.Equal("/yaml/output", config.Download.OutputDirectory);
        Assert.Equal("720p", config.Download.Quality);
        Assert.Contains("ja-JP", config.Download.DubLanguages);
        Assert.Contains("en-US", config.Download.DubLanguages);
        
        File.Delete(tempFile);
    }
}
