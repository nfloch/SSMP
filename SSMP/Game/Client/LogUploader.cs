using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Sends the BepInEx log to the session's Gokapi file request at the end of a co-op session.
/// <para>
/// A session counts only if another player was connected at the same time. When the local player disconnects (left,
/// kicked, server stopped) or the game quits during such a session, the log is first copied, gzipped, to a pending
/// folder, which is instant, then uploaded in the background. Pending files that could not be sent (the game closed
/// too early, no network) are sent at the next game start. BepInEx overwrites its log at each start, so the copy is
/// what keeps a session's log when the game is restarted.
/// </para>
/// <para>
/// Upload names: <c>ssmp_&lt;session&gt;_&lt;player&gt;_&lt;host|client&gt;_&lt;game start&gt;_&lt;n&gt;.log.gz</c>,
/// so that the uploads of a session can be grouped per player and game start.
/// </para>
/// <para>
/// Needs <c>ssmp-upload.cfg</c> next to SSMP.dll, with a line <c>UploadUrl=https://…/publicUpload?id=…&amp;key=…</c>
/// (installed by tools/jojo/installer-ssmp.ps1). Without it, nothing is uploaded.
/// </para>
/// </summary>
internal class LogUploader {
    /// <summary>
    /// Name of the config file next to SSMP.dll.
    /// </summary>
    private const string ConfigFileName = "ssmp-upload.cfg";

    /// <summary>
    /// The time this game was started, part of the upload names.
    /// </summary>
    private static readonly string GameStart = DateTime.Now.ToString("yyyyMMdd-HHmmss");

    /// <summary>
    /// The folder holding the logs waiting to be sent.
    /// </summary>
    private static string PendingFolder = "";

    /// <summary>
    /// The folder holding SSMP.dll and its upload config. Both are read on the main thread, since the uploads run
    /// on a background thread.
    /// </summary>
    private static string? PluginFolder;

    /// <summary>
    /// Only one upload pass at a time, so that a file is never sent twice.
    /// </summary>
    private static readonly System.Threading.SemaphoreSlim UploadLock = new(1, 1);

    /// <summary>
    /// The session we are connected to, or null if not connected.
    /// </summary>
    private string? _sessionId;

    /// <summary>
    /// Whether we host the server of the current session.
    /// </summary>
    private bool _isHost;

    /// <summary>
    /// The local player name.
    /// </summary>
    private string _playerName = "player";

    /// <summary>
    /// Whether another player was connected during the current session.
    /// </summary>
    private bool _otherPlayerSeen;

    /// <summary>
    /// The number of logs saved in this game start, part of the upload names.
    /// </summary>
    private int _uploadCount;

    /// <summary>
    /// Sends the logs left by a previous game and listens for the game quitting.
    /// </summary>
    public void Initialize() {
        PendingFolder = Path.Combine(FileUtil.GetConfigPath(), "pending-logs");
        PluginFolder = Path.GetDirectoryName(typeof(LogUploader).Assembly.Location);

        Application.quitting += () => SaveSessionLog("game quit", uploadNow: false);
        Task.Run(UploadPending);
    }

    /// <summary>
    /// Called when connected to a server.
    /// </summary>
    public void OnConnected(string sessionId, bool isHost, string playerName, bool otherPlayersPresent) {
        _sessionId = string.IsNullOrEmpty(sessionId) ? $"unknown-{DateTime.Now:yyyyMMdd-HHmm}" : sessionId;
        _isHost = isHost;
        _playerName = playerName;
        _otherPlayerSeen = otherPlayersPresent;

        Logger.Info(
            $"[Session] Joined session {_sessionId} as {_playerName} ({(isHost ? "host" : "client")}), " +
            $"game started {GameStart}"
        );
    }

    /// <summary>
    /// Called when another player connects to the server.
    /// </summary>
    public void OnOtherPlayerConnected() {
        if (_sessionId != null && !_otherPlayerSeen) {
            _otherPlayerSeen = true;
            Logger.Info($"[Session] Another player joined session {_sessionId}, its log will be sent at the end");
        }
    }

    /// <summary>
    /// Called when disconnected from the server, for any reason.
    /// </summary>
    public void OnDisconnected() {
        SaveSessionLog("disconnected", uploadNow: true);
        _sessionId = null;
        _otherPlayerSeen = false;
    }

    /// <summary>
    /// Copies the log of the current session to the pending folder and, if asked, sends it in the background.
    /// </summary>
    private void SaveSessionLog(string reason, bool uploadNow) {
        if (_sessionId == null || !_otherPlayerSeen) {
            return;
        }

        try {
            Logger.Info($"[Session] End of session {_sessionId} for this game ({reason}), saving the log");

            var log = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
            if (!File.Exists(log)) {
                Logger.Warn($"[Session] No log to send at {log}");
                return;
            }

            Directory.CreateDirectory(PendingFolder);
            var name = $"ssmp_{Sanitize(_sessionId)}_{Sanitize(_playerName)}_{(_isHost ? "host" : "client")}_" +
                       $"{GameStart}_{++_uploadCount}.log.gz";

            // The log is still open for writing by BepInEx, hence the shared read
            using (var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = File.Create(Path.Combine(PendingFolder, name)))
            using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal)) {
                input.CopyTo(gzip);
            }

            if (uploadNow) {
                Task.Run(UploadPending);
            }
        } catch (Exception e) {
            Logger.Warn($"[Session] Could not save the log: {e.Message}");
        }
    }

    /// <summary>
    /// Sends every pending log, oldest first, and deletes each one once sent.
    /// </summary>
    private static async Task UploadPending() {
        await UploadLock.WaitAsync();
        try {
            if (!Directory.Exists(PendingFolder)) {
                return;
            }

            var files = Directory.GetFiles(PendingFolder, "ssmp_*.log.gz").OrderBy(File.GetLastWriteTimeUtc).ToList();
            if (files.Count == 0) {
                return;
            }

            var uploadUrl = ReadUploadUrl();
            if (uploadUrl == null) {
                Logger.Info($"[Session] {files.Count} log(s) waiting, but no {ConfigFileName}: not sent");
                return;
            }

            foreach (var file in files) {
                await Upload(uploadUrl, file);
                File.Delete(file);
                Logger.Info($"[Session] Log sent: {Path.GetFileName(file)}");
            }
        } catch (Exception e) {
            Logger.Warn($"[Session] Could not send the logs, will retry at the next disconnect or game start: {e.Message}");
        } finally {
            UploadLock.Release();
        }
    }

    /// <summary>
    /// Uploads a file to a Gokapi file request, the same way as tools/jojo/envoyer-logs.ps1.
    /// </summary>
    private static async Task Upload(string uploadUrl, string file) {
        var match = Regex.Match(uploadUrl, @"^(https?://[^/]+(?:/.*?)?)/publicUpload\?id=([^&]+)&key=([^&]+)$");
        if (!match.Success) {
            throw new InvalidOperationException("UploadUrl is not a Gokapi publicUpload link");
        }

        var api = $"{match.Groups[1].Value}/api/uploadrequest/chunk";
        var requestId = match.Groups[2].Value;
        var apiKey = match.Groups[3].Value;

        var bytes = File.ReadAllBytes(file);
        var name = Path.GetFileName(file);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        var reserve = await Send(client, $"{api}/reserve", null, ("id", requestId), ("apikey", apiKey));
        var uuid = Regex.Match(reserve, "\"Uuid\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        if (uuid.Length == 0) {
            throw new InvalidOperationException($"Unexpected reserve answer: {reserve}");
        }

        using (var form = new MultipartFormDataContent()) {
            form.Add(new ByteArrayContent(bytes), "file", "blob");
            form.Add(new StringContent(uuid), "uuid");
            form.Add(new StringContent(bytes.Length.ToString()), "filesize");
            form.Add(new StringContent("0"), "offset");
            await Send(client, $"{api}/add", form, ("apikey", apiKey), ("fileRequestId", requestId));
        }

        await Send(
            client, $"{api}/complete", null,
            ("uuid", uuid), ("fileRequestId", requestId),
            ("filename", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name))),
            ("filesize", bytes.Length.ToString()), ("nonblocking", "true"),
            ("contenttype", "application/gzip"), ("apikey", apiKey)
        );
    }

    private static async Task<string> Send(
        HttpClient client,
        string url,
        HttpContent? content,
        params (string Name, string Value)[] headers
    ) {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (headerName, value) in headers) {
            request.Headers.TryAddWithoutValidation(headerName, value);
        }

        request.Content = content;

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) {
            throw new HttpRequestException($"{(int) response.StatusCode} on {url}: {body}");
        }

        return body;
    }

    /// <summary>
    /// Reads the upload link from the config file next to SSMP.dll, or null if there is none.
    /// </summary>
    private static string? ReadUploadUrl() {
        var folder = PluginFolder;
        if (folder == null) {
            return null;
        }

        var config = Path.Combine(folder, ConfigFileName);
        if (!File.Exists(config)) {
            return null;
        }

        foreach (var line in File.ReadAllLines(config)) {
            var match = Regex.Match(line, @"^\s*UploadUrl\s*=\s*(\S+)\s*$");
            if (match.Success) {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static string Sanitize(string text) => Regex.Replace(text, "[^A-Za-z0-9-]", "-");
}
