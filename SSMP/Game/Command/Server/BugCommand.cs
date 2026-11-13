using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SSMP.Api.Command;
using SSMP.Api.Command.Server;
using SSMP.Game.Server;
using SSMP.Logging;
using SSMP.Networking.Server;

namespace SSMP.Game.Command.Server;

/// <summary>
/// Command that marks a bug seen while playing: "/bug [description]", also sent by the bug report key (F8).
/// <para>
/// Announces a numbered marker naming the reporting player and their scene to all players. Each game then writes a
/// snapshot of its own state to its log. To avoid duplicates:
/// <list type="bullet">
/// <item>a second F8 from the same player within <see cref="DoublePressWindow"/> is ignored;</item>
/// <item>"/bug text" within <see cref="DescriptionWindow"/> of the same player's marker becomes its description;</item>
/// <item>a marker in the same scene within <see cref="SameBugWindow"/> of the previous one there joins it
/// ("also reported by"), from either player;</item>
/// <item>"/bug nouveau [text]" always starts a new bug.</item>
/// </list>
/// </para>
/// </summary>
internal class BugCommand : IServerCommand, ICommandWithDescription {
    /// <summary>
    /// Prefix of every bug marker message.
    /// </summary>
    public const string MessagePrefix = "[BUG #";

    /// <summary>
    /// Text after "[BUG #n] " of a new marker; clients write a snapshot for it.
    /// </summary>
    public const string NewMarker = "by ";

    /// <summary>
    /// Text after "[BUG #n] " of another report of the same bug; clients write a snapshot for it too.
    /// </summary>
    public const string AlsoReportedMarker = "also reported by ";

    /// <summary>
    /// Text after "[BUG #n] " of a description added to a bug; clients only log it.
    /// </summary>
    public const string DescriptionMarker = "description from ";

    /// <summary>
    /// Keyword to force a new bug: "/bug nouveau [text]".
    /// </summary>
    private const string ForceNewKeyword = "nouveau";

    private static readonly TimeSpan DoublePressWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DescriptionWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SameBugWindow = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public string Trigger => "/bug";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description =>
        "Mark a bug in every player's log. '/bug <text>' just after F8 describes it, '/bug nouveau' forces a new one.";

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
    /// The number of the last bug of this server session.
    /// </summary>
    private int _lastBugNumber;

    /// <summary>
    /// The last marker of each player: bug number and time.
    /// </summary>
    private readonly Dictionary<ushort, (int Number, DateTime Time)> _lastByPlayer = new();

    /// <summary>
    /// The last marker in each scene: bug number and time.
    /// </summary>
    private readonly Dictionary<string, (int Number, DateTime Time)> _lastByScene = new();

    /// <summary>
    /// Commands can come from several threads.
    /// </summary>
    private readonly object _lock = new();

    public BugCommand(ConcurrentDictionary<ushort, ServerPlayerData> playerData, NetServer netServer) {
        _playerData = playerData;
        _netServer = netServer;
    }

    /// <inheritdoc />
    public void Execute(ICommandSender commandSender, string[] args) {
        ushort? id = null;
        var reporter = "server";
        var scene = "";
        if (commandSender is IPlayerCommandSender playerSender &&
            _playerData.TryGetValue(playerSender.Id, out var reporterData)) {
            id = reporterData.Id;
            scene = reporterData.CurrentScene;
            reporter = $"{reporterData.Username} (id {reporterData.Id}) in {scene}";
        }

        var text = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1).Trim() : "";
        var forceNew = text == ForceNewKeyword || text.StartsWith(ForceNewKeyword + " ", StringComparison.Ordinal);
        if (forceNew) {
            text = text[ForceNewKeyword.Length..].Trim();
        }

        string message;
        lock (_lock) {
            var now = DateTime.UtcNow;
            (int Number, DateTime Time) own = default;
            var hasOwn = id.HasValue && _lastByPlayer.TryGetValue(id.Value, out own);

            if (!forceNew && hasOwn && text.Length == 0 && now - own.Time < DoublePressWindow) {
                // Unintended double press: tell only the reporter
                commandSender.SendMessage($"[BUG] #{own.Number} already reported a moment ago, ignored");
                return;
            }

            if (!forceNew && hasOwn && text.Length > 0 && now - own.Time < DescriptionWindow) {
                message = $"{MessagePrefix}{own.Number}] {DescriptionMarker}{reporter}: {text}";
            } else if (!forceNew && _lastByScene.TryGetValue(scene, out var inScene) &&
                       now - inScene.Time < SameBugWindow) {
                message = $"{MessagePrefix}{inScene.Number}] {AlsoReportedMarker}{reporter}";
                if (text.Length > 0) message += $": {text}";
                Remember(id, scene, inScene.Number, now);
            } else {
                var number = ++_lastBugNumber;
                message = $"{MessagePrefix}{number}] {NewMarker}{reporter}";
                if (text.Length > 0) message += $": {text}";
                Remember(id, scene, number, now);
            }
        }

        Logger.Info(message);

        foreach (var playerData in _playerData.Values) {
            _netServer.GetUpdateManagerForClient(playerData.Id)?.AddChatMessage(message);
        }
    }

    private void Remember(ushort? id, string scene, int number, DateTime time) {
        if (id.HasValue) {
            _lastByPlayer[id.Value] = (number, time);
        }

        _lastByScene[scene] = (number, time);
    }
}
