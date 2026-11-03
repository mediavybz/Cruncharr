using System.Diagnostics;
using Cruncharr.CLI;
using Cruncharr.Core.Configuration;

namespace Cruncharr.Core.Tests;

// Audit F09: `config set` assigned the raw string through reflection (a bool setting threw), only
// knew top-level keys although almost every setting is nested, and ignored unknown keys with
// exit code 0.
public class CliConfigCommandTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cruncharr-cli-config-");
    private string ConfigPath => Path.Combine(_root.FullName, "cruncharr.yaml");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData("LogMode", "true")]
    [InlineData("download.output_dir", "/media/anime")]
    [InlineData("Download.OutputDirectory", "/media/anime")]
    [InlineData("queue.simultaneous_processing_jobs", "3")]
    [InlineData("Download.DubLanguages", "ja-JP,en-US")]
    public void Set_ConvertsTheValueAndSavesIt(string key, string value)
    {
        Assert.Equal(0, Set(key, value, out _));
        var reloaded = CruncharrConfig.Load(ConfigPath);
        Assert.True(ConfigEditor.TryGet(reloaded, key, out var saved, out _));
        Assert.Equal(value, saved);
    }

    [Fact]
    public void Set_StoresTypedValues()
    {
        Assert.Equal(0, Set("LogMode", "true", out _));
        Assert.Equal(0, Set("queue.simultaneous_processing_jobs", "3", out _));
        Assert.Equal(0, Set("download.dub_languages", "ja-JP, en-US", out _));
        Assert.Equal(0, Set("TrackedSeriesReleaseLastCheckUtc", "2026-10-01T12:00:00Z", out _));

        var config = CruncharrConfig.Load(ConfigPath);
        Assert.True(config.LogMode);
        Assert.Equal(3, config.Queue.SimultaneousProcessingJobs);
        Assert.Equal(["ja-JP", "en-US"], config.Download.DubLanguages);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), config.TrackedSeriesReleaseLastCheckUtc?.ToUniversalTime());
    }

    [Theory]
    [InlineData("NoSuchSetting", "1", "Unknown setting")]
    [InlineData("download.no_such_setting", "1", "Unknown setting")]
    [InlineData("LogMode.Extra", "1", "Unknown setting")]
    [InlineData("LogMode", "maybe", "not a valid true/false value")]
    [InlineData("queue.simultaneous_processing_jobs", "three", "not a valid whole number")]
    [InlineData("download", "x", "group of settings")]
    public void Set_RejectsUnknownKeysAndInvalidValuesWithoutWriting(string key, string value, string message)
    {
        File.WriteAllText(ConfigPath, "log_mode: false\n");

        Assert.Equal(1, Set(key, value, out var errors));

        Assert.Contains(message, errors);
        Assert.Equal("log_mode: false\n", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Get_PrintsNestedValuesAndFailsForUnknownKeys()
    {
        File.WriteAllText(ConfigPath, "download:\n  output_dir: /media/anime\n");
        var output = new StringWriter();

        Assert.Equal(0, ConfigEditor.Get(ConfigPath, "download.output_dir", output, TextWriter.Null));
        Assert.Equal("/media/anime", output.ToString().Trim());
        Assert.Equal(1, ConfigEditor.Get(ConfigPath, "download.nope", TextWriter.Null, TextWriter.Null));
    }

    // The real command line, so exit codes are checked end to end.
    [Fact]
    public async Task ConfigCommand_ExitCodesReflectTheOutcome()
    {
        Assert.Equal(0, await RunCliAsync("config", "set", "LogMode", "true"));
        Assert.Equal(0, await RunCliAsync("config", "set", "Download.OutputDirectory", "/media/anime"));
        Assert.Equal(1, await RunCliAsync("config", "set", "NoSuchSetting", "1"));
        Assert.Equal(1, await RunCliAsync("config", "set", "LogMode", "maybe"));

        var config = CruncharrConfig.Load(ConfigPath);
        Assert.True(config.LogMode);
        Assert.Equal("/media/anime", config.Download.OutputDirectory);
    }

    private int Set(string key, string value, out string errors)
    {
        var errorWriter = new StringWriter();
        var code = ConfigEditor.Set(ConfigPath, key, value, TextWriter.Null, errorWriter);
        errors = errorWriter.ToString();
        return code;
    }

    private async Task<int> RunCliAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        // Run the CLI from its own build output (src/Cruncharr.CLI/bin/<configuration>/<tfm>);
        // the copy beside the tests resolves its dependencies through the test project.
        var tests = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var cli = Path.Combine(tests.Parent!.Parent!.Parent!.Parent!.FullName, "Cruncharr.CLI", "bin", tests.Parent.Name, tests.Name, "cruncharr.dll");
        start.ArgumentList.Add(cli);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CRUNCHYROLL_CONFIG_PATH"] = ConfigPath;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(output, errors);
        return process.ExitCode;
    }
}
