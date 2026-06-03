using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cruncharr.Core.Models;
using Microsoft.Win32;

namespace Cruncharr.Core.Utils.Muxing;

public static class MuxingHelpers{
    public static async Task<(bool IsOk, int ErrorCode)> ExecuteCommandAsync(string bin, string command, CancellationToken cancellationToken = default){
        try{
            using (var process = new Process()){
                process.StartInfo.FileName = bin;
                process.StartInfo.Arguments = command;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;

                process.OutputDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data)){
                        if (e.Data.StartsWith("Error:")){
                            Console.Error.WriteLine(e.Data);
                        } else{
                            Console.WriteLine(e.Data);
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data)){
                        Console.Error.WriteLine($"{e.Data}");
                    }
                };

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await using var registration = cancellationToken.Register(() => {
                    try{
                        if (!process.HasExited){
                            process.Kill(true);
                        }
                    } catch{
                        // ignored
                    }
                });

                await process.WaitForExitAsync(cancellationToken);

                bool isSuccess = process.ExitCode == 0;

                return (IsOk: isSuccess, ErrorCode: process.ExitCode);
            }
        } catch (Exception ex){
            Console.Error.WriteLine($"An error occurred: {ex.Message}");
            return (IsOk: false, ErrorCode: -1);
        }
    }

    public static bool DeleteFile(string filePath, int maxRetries = 5, int delayMs = 150){
        if (string.IsNullOrEmpty(filePath)){
            return false;
        }

        for (int attempt = 0; attempt < maxRetries; attempt++){
            try{
                if (!File.Exists(filePath)){
                    return true;
                }

                File.Delete(filePath);
                return true;
            } catch (Exception ex) when (attempt < maxRetries - 1 && (ex is IOException || ex is UnauthorizedAccessException)){
                Thread.Sleep(delayMs * (attempt + 1));
            } catch (Exception ex){
                Console.Error.WriteLine($"Failed to delete file {filePath}. Error: {ex.Message}");
                return false;
            }
        }

        Console.Error.WriteLine($"Failed to delete file {filePath}. Error: file remained locked after {maxRetries} attempts.");
        return false;
    }

    public static string AddUncPrefixIfNeeded(string path){
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !IsLongPathEnabled()){
            if (!string.IsNullOrEmpty(path) && !path.StartsWith(@"\\?\")){
                return $@"\\?\{Path.GetFullPath(path)}";
            }
        }

        return path;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsLongPathEnabled(){
        try{
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem")){
                if (key != null){
                    var value = key.GetValue("LongPathsEnabled", 0);
                    return value is int intValue && intValue == 1;
                }
            }
        } catch (Exception ex){
            Console.Error.WriteLine($"Failed to check if long paths are enabled: {ex.Message}");
        }

        return false;
    }
}
