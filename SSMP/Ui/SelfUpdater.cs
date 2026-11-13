using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Ui;

/// <summary>
/// Updates SSMP from the main menu with the rolling build of the fork (release "latest-world-sync" of nfloch/SSMP).
/// <para>
/// At game start, compares the installed commit (SSMP-COMMIT.txt next to SSMP.dll) with the commit of the release
/// tag. On request, downloads the release zip and checks that it is the expected build, then replaces the files:
/// <list type="bullet">
/// <item>directly, by renaming the files in use to "*.old" (Windows allows renaming a loaded DLL) and writing the
/// new ones; the update applies at the next game start;</item>
/// <item>otherwise (plan B), by writing the new files as "*.new" and starting a detached PowerShell script that
/// swaps them once the game has closed. If that script did not run, it is started again at the next game start.</item>
/// </list>
/// Other files of the plugin folder (certificate, server lists, ssmp-upload.cfg) are never touched.
/// </para>
/// </summary>
internal static class SelfUpdater {
    private const string Repository = "nfloch/SSMP";
    private const string Branch = "world-sync";
    private const string CommitFile = "SSMP-COMMIT.txt";
    private const string HelperScript = "ssmp-update.ps1";

    /// <summary>
    /// The files of the release zip that are installed in the plugin folder.
    /// </summary>
    private static readonly string[] Files = ["SSMP.dll", "SSMP.pdb", "BouncyCastle.Cryptography.dll"];

    public enum UpdateState {
        Unknown,
        UpToDate,
        Available,
        Updating,
        RestartToApply,
        AppliesOnClose,
        Failed
    }

    /// <summary>
    /// The current state; read by the main menu button.
    /// </summary>
    public static UpdateState State { get; private set; } = UpdateState.Unknown;

    /// <summary>
    /// Incremented at each state change, so that the menu knows when to refresh.
    /// </summary>
    public static int StateVersion { get; private set; }

    /// <summary>
    /// The installed commit, or "" if unknown.
    /// </summary>
    public static string InstalledCommit { get; private set; } = "";

    /// <summary>
    /// The commit of the latest build, once checked.
    /// </summary>
    public static string LatestCommit { get; private set; } = "";

    private static string _pluginFolder = "";

    /// <summary>
    /// The text of the main menu button for the current state.
    /// </summary>
    public static string ButtonText => State switch {
        UpdateState.Available => $"Mettre à jour SSMP ({Short(LatestCommit)})",
        UpdateState.Updating => "Mise à jour de SSMP…",
        UpdateState.RestartToApply => "SSMP mis à jour : relance le jeu",
        UpdateState.AppliesOnClose => "Mise à jour de SSMP à la fermeture du jeu",
        UpdateState.Failed => "Échec de la mise à jour de SSMP (voir le log)",
        _ => "SSMP à jour"
    };

    /// <summary>
    /// Whether the main menu should show the update button.
    /// </summary>
    public static bool ShowButton => State is not (UpdateState.Unknown or UpdateState.UpToDate);

    /// <summary>
    /// Cleans up after a previous update and starts checking for a new build in the background.
    /// </summary>
    public static void Initialize() {
        try {
            _pluginFolder = Path.GetDirectoryName(typeof(SelfUpdater).Assembly.Location) ?? "";
            var commitPath = Path.Combine(_pluginFolder, CommitFile);
            InstalledCommit = File.Exists(commitPath) ? File.ReadAllText(commitPath).Trim() : "";
            Logger.Info($"[Update] Installed SSMP commit: {(InstalledCommit.Length > 0 ? InstalledCommit : "unknown")}");

            foreach (var old in Directory.GetFiles(_pluginFolder, "*.old")) {
                try {
                    File.Delete(old);
                } catch (Exception) {
                    // Still in use by another game instance; deleted next time
                }
            }

            var updateLog = Path.Combine(_pluginFolder, "ssmp-update.log");
            if (File.Exists(updateLog)) {
                Logger.Info($"[Update] Last deferred update: {File.ReadAllText(updateLog).Trim()}");
            }

            // A deferred update whose script did not run (killed with the game): start it again for this game
            if (Directory.GetFiles(_pluginFolder, "*.new").Length > 0) {
                Logger.Info("[Update] A downloaded update is still waiting, it will apply when the game closes");
                StartHelper();
                SetState(UpdateState.AppliesOnClose);
                return;
            }
        } catch (Exception e) {
            Logger.Warn($"[Update] Could not read the installed version: {e.Message}");
        }

        Task.Run(CheckLatest);
    }

    /// <summary>
    /// Starts the update in the background, if one is available.
    /// </summary>
    public static void StartUpdate() {
        if (State != UpdateState.Available) {
            return;
        }

        SetState(UpdateState.Updating);
        Task.Run(Update);
    }

    private static async Task CheckLatest() {
        try {
            using var client = CreateClient();
            var json = await client.GetStringAsync(
                $"https://api.github.com/repos/{Repository}/git/ref/tags/latest-{Branch}"
            );
            var sha = Regex.Match(json, "\"sha\"\\s*:\\s*\"([0-9a-f]{40})\"").Groups[1].Value;
            if (sha.Length == 0) {
                Logger.Warn("[Update] Could not read the latest build commit");
                return;
            }

            LatestCommit = sha;
            if (sha == InstalledCommit) {
                Logger.Info("[Update] SSMP is up to date");
                SetState(UpdateState.UpToDate);
            } else {
                Logger.Info($"[Update] New SSMP build available: {Short(InstalledCommit)} -> {Short(sha)}");
                SetState(UpdateState.Available);
            }
        } catch (Exception e) {
            // No network, GitHub unavailable: no button
            Logger.Info($"[Update] Could not check for a new SSMP build: {e.Message}");
        }
    }

    private static async Task Update() {
        var temp = Path.Combine(Path.GetTempPath(), $"ssmp-update-{Guid.NewGuid():N}");
        try {
            Directory.CreateDirectory(temp);
            var zip = Path.Combine(temp, "ssmp.zip");

            using (var client = CreateClient()) {
                var bytes = await client.GetByteArrayAsync(
                    $"https://github.com/{Repository}/releases/download/latest-{Branch}/SSMP-{Branch}.zip"
                );
                File.WriteAllBytes(zip, bytes);
            }

            var extracted = Path.Combine(temp, "x");
            ZipFile.ExtractToDirectory(zip, extracted);

            var missing = Files.Concat(["COMMIT.txt"]).Where(f => !File.Exists(Path.Combine(extracted, f))).ToList();
            if (missing.Count > 0) {
                throw new InvalidOperationException($"the zip lacks {string.Join(", ", missing)}");
            }

            var zipCommit = File.ReadAllText(Path.Combine(extracted, "COMMIT.txt")).Trim();
            if (zipCommit != LatestCommit) {
                // The tag already moved but the new zip is still being published
                throw new InvalidOperationException(
                    $"the zip is build {Short(zipCommit)}, not {Short(LatestCommit)}; try again in a few minutes"
                );
            }

            var changed = Files.Where(f => !SameContent(Path.Combine(extracted, f), Path.Combine(_pluginFolder, f)))
                .ToList();

            if (TryReplaceInPlace(extracted, changed)) {
                File.WriteAllText(Path.Combine(_pluginFolder, CommitFile), zipCommit);
                Logger.Info($"[Update] renamed in place, build {Short(zipCommit)} applies at the next game start");
                SetState(UpdateState.RestartToApply);
                return;
            }

            // Plan B: swap the files once the game has closed
            foreach (var file in changed) {
                File.Copy(Path.Combine(extracted, file), Path.Combine(_pluginFolder, file + ".new"), true);
            }

            File.WriteAllText(Path.Combine(_pluginFolder, CommitFile + ".new"), zipCommit);
            StartHelper();
            Logger.Info($"[Update] deferred until the game closes, build {Short(zipCommit)}");
            SetState(UpdateState.AppliesOnClose);
        } catch (Exception e) {
            Logger.Warn($"[Update] Update failed: {e.Message}");
            SetState(UpdateState.Failed);
        } finally {
            try {
                Directory.Delete(temp, true);
            } catch (Exception) {
                // Left in the temp folder
            }
        }
    }

    /// <summary>
    /// Renames the files in use to "*.old" and writes the new ones. Restores everything if any step fails.
    /// </summary>
    private static bool TryReplaceInPlace(string extracted, System.Collections.Generic.List<string> changed) {
        var moved = new System.Collections.Generic.List<string>();
        try {
            foreach (var file in changed) {
                var target = Path.Combine(_pluginFolder, file);
                var old = target + ".old";
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(target)) {
                    File.Move(target, old);
                    moved.Add(file);
                }

                File.Copy(Path.Combine(extracted, file), target);
            }

            return true;
        } catch (Exception e) {
            Logger.Info($"[Update] Could not replace the files in place ({e.Message}), falling back to plan B");
            foreach (var file in changed) {
                var target = Path.Combine(_pluginFolder, file);
                try {
                    if (moved.Contains(file)) {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(target + ".old", target);
                    }
                } catch (Exception restoreError) {
                    Logger.Warn($"[Update] Could not restore {file}: {restoreError.Message}");
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Writes and starts the script that swaps the "*.new" files once this game process has exited.
    /// </summary>
    private static void StartHelper() {
        var script = Path.Combine(_pluginFolder, HelperScript);
        File.WriteAllText(script, HelperScriptContent);

        var pid = Process.GetCurrentProcess().Id;
        Process.Start(new ProcessStartInfo {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                        $"-GamePid {pid} -Dir \"{_pluginFolder}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private const string HelperScriptContent = @"param([int] $GamePid, [string] $Dir)
# Written by SSMP (SelfUpdater): swaps the downloaded *.new files once the game has closed.
Wait-Process -Id $GamePid -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$log = Join-Path $Dir 'ssmp-update.log'
try {
    foreach ($new in Get-ChildItem $Dir -Filter '*.new') {
        $target = Join-Path $Dir $new.BaseName
        if (Test-Path $target) { Move-Item $target ""$target.old"" -Force }
        Move-Item $new.FullName $target -Force
    }
    ""$(Get-Date -Format s) applied: $(Get-Content (Join-Path $Dir 'SSMP-COMMIT.txt'))"" | Set-Content $log
} catch {
    ""$(Get-Date -Format s) FAILED: $_"" | Set-Content $log
}
";

    private static bool SameContent(string a, string b) {
        if (!File.Exists(b)) return false;
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length) return false;
        try {
            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        } catch (IOException) {
            return false;
        }
    }

    private static HttpClient CreateClient() {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // Required by the GitHub API
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SSMP-updater");
        return client;
    }

    private static void SetState(UpdateState state) {
        State = state;
        StateVersion++;
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : (sha.Length > 0 ? sha : "?");
}
