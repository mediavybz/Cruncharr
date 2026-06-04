namespace Cruncharr.Core.Services;

public static class LogManager{
    private static StreamWriter? _logFile;
    private static bool _isLogModeEnabled;
    private static readonly object _lock = new();
    
    public static void EnableLogMode(string logPath = "logfile.txt"){
        lock (_lock){
            if (!_isLogModeEnabled){
                try{
                    var fileStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    try{
                        _logFile = new StreamWriter(fileStream);
                        _logFile.AutoFlush = true;
                        Console.SetError(_logFile);
                        _isLogModeEnabled = true;
                        Console.Error.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Log mode enabled.");
                    } catch{
                        fileStream.Dispose();
                        throw;
                    }
                } catch (Exception e){
                    Console.Error.WriteLine($"Couldn't enable logging: {e}");
                }
            }
        }
    }
    
    public static void DisableLogMode(){
        lock (_lock){
            if (_isLogModeEnabled){
                try{
                    _logFile?.Close();
                    var standardError = new StreamWriter(Console.OpenStandardError());
                    standardError.AutoFlush = true;
                    Console.SetError(standardError);
                    _isLogModeEnabled = false;
                } catch (Exception e){
                    Console.Error.WriteLine($"Couldn't disable logging: {e}");
                }
            }
        }
    }
    
    public static void LogInfo(string message){
        if (_isLogModeEnabled){
            Console.Error.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: {message}");
        }
    }
    
    public static void LogError(string message){
        if (_isLogModeEnabled){
            Console.Error.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}");
        }
    }
    
    public static void LogDebug(string message){
        if (_isLogModeEnabled){
            Console.Error.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DEBUG: {message}");
        }
    }
}
