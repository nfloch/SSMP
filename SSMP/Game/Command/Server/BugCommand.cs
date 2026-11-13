using System.Collections.Concurrent;
using System.Threading;
using SSMP.Api.Command;
using SSMP.Api.Command.Server;
using SSMP.Game.Server;
using SSMP.Logging;
using SSMP.Networking.Server;

namespace SSMP.Game.Command.Server;

/// <summary>
/// Command that marks a bug seen while playing ("/bug [description]", also sent by the bug report key). Announces a
/// numbered marker with the reporting player and their scene to all players, so that each game writes a snapshot
/// of its own state to its log at that moment.
/// </summary>
internal class BugCommand : IServerCommand, ICommandWithDescription {
    /// <summary>
    /// Prefix of the announced message; clients recognize it to write their snapshot.
    /// </summary>
    public const string MessagePrefix = "[BUG #";

    /// <inheritdoc />
    public string Trigger => "/bug";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Mark a bug in every player's log, with an optional description.";

    /// <inheritdoc />
    public bool AuthorizedOnly => false;

    /// <summary>
    /// A reference to the server player data dictionary.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, ServerPlayerData> _playerData;

    /// <summary>
    /// The net server instance.
    /// </summary>
    private readonly NetServer _netServer;

    /// <summary>
    /// The number of the last bug marker of this server session.
    /// </summary>
    private int _lastBugNumber;

    public BugCommand(ConcurrentDictionary<ushort, ServerPlayerData> playerData, NetServer netServer) {
        _playerData = playerData;
        _netServer = netServer;
    }

    /// <inheritdoc />
    public void Execute(ICommandSender commandSender, string[] args) {
        var reporter = "server";
        if (commandSender is IPlayerCommandSender playerSender &&
            _playerData.TryGetValue(playerSender.Id, out var reporterData)) {
            reporter = $"{reporterData.Username} (id {reporterData.Id}) in {reporterData.CurrentScene}";
        }

        var description = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
        var number = Interlocked.Increment(ref _lastBugNumber);

        var message = $"{MessagePrefix}{number}] by {reporter}";
        if (description.Length > 0) {
            message += $": {description}";
        }

        Logger.Info(message);

        foreach (var playerData in _playerData.Values) {
            _netServer.GetUpdateManagerForClient(playerData.Id)?.AddChatMessage(message);
        }
    }
}
