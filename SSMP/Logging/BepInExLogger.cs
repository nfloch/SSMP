using SSMP;

namespace SSMP.Logging;

/// <summary>
/// Logger class for logging to BepInEx.
/// </summary>
public class BepInExLogger : BaseLogger {
    /// <summary>
    /// The BepInEx manual log source to log information to.
    /// </summary>
    private readonly BepInEx.Logging.ManualLogSource _logSource;

    public BepInExLogger() {
        _logSource = BepInEx.Logging.Logger.CreateLogSource(SSMPPlugin.Name);
    }
    
    /// <summary>
    /// Prefixes the message with the local wall-clock time, since BepInEx writes no timestamps to its log file, and
    /// with the name of the class that logged it.
    /// </summary>
    private string Format(string message) =>
        $"{System.DateTime.Now:HH:mm:ss.fff} [{GetOriginClassName()}] {message}";

    /// <inheritdoc />
    public override void Info(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogInfo(Format(message));
    }

    /// <inheritdoc />
    public override void Message(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogMessage(Format(message));
    }

    /// <inheritdoc />
    public override void Debug(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogDebug(Format(message));
    }

    /// <inheritdoc />
    public override void Warn(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogWarning(Format(message));
    }

    /// <inheritdoc />
    public override void Error(string message) {
        if (!ShouldLogMessage(message)) {
            return;
        }
        _logSource.LogError(Format(message));
    }
}
