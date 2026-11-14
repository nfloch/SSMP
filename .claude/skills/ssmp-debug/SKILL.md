---
name: ssmp-debug
description: Run a co-op SSMP debug session end to end. Wait for the end of the session (SSMP sends each player's log by itself), rebuild one log per player and session, split them per F8/"/bug" marker, open one issue per bug in the private nfloch/ssmp-bugs tracker, dispatch one agent per bug in a git worktree, then integrate the fixes on world-sync and report. Use when the user says "lance le debug", "surveille les logs", "/ssmp-debug", or gives the end-of-session go.
---

# SSMP debug session

Players press **F8** (or type `/bug <text>`) in game. The server announces `[BUG #n] by <player> (id) in <scene>[: text]`,
and every game logs a `[BUG]` snapshot. `/bug encore <issue>` means that the bug of issue `<issue>` came back.

The server already merges some duplicates (`SSMP/Game/Command/Server/BugCommand.cs`):
- a second F8 from the same player within 5 s is ignored;
- `/bug <text>` within 60 s of the same player's marker becomes its description (`[BUG #n] description from …`);
- a marker in the same scene within 20 s of the previous one joins it (`[BUG #n] also reported by …`, with a new
  snapshot);
- `/bug nouveau [text]` always starts a new bug.

The splitter groups everything with the same number in one folder: all snapshots, `reports`, and `description`.
SSMP log lines carry the local time, and `[Inventory]` lines record each player's inventory at game entry and at benches.

Paths (run from the SSMP repo root, `world-sync` branch):
- Tools: `tools/bugs/attendre-logs.sh`, `tools/bugs/assembler-logs.py`, `tools/bugs/decouper-logs.py`,
  `tools/gokapi/recuperer-logs.sh`,
  `tools/jojo/envoyer-logs.ps1`
- Tracker clone: `../ssmp-bugs` (`git@github.com:nfloch/ssmp-bugs.git`, **private**). Issues: `gh ... -R nfloch/ssmp-bugs`
- Processed markers: `~/.local/state/ssmp/markers.json`
- Build check: `CI=true DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PATH=$HOME/.dotnet:$PATH dotnet build SSMP/SSMP.csproj -c Release -o <out dir>`

Rules:
- The user adds details as comments on the issues, at any time, including on issues already fixed or integrated.
  Every bot comment (yours and the agents') starts with "🤖 ", so any other comment is the user's: read them at each
  step (`gh issue view <n> -R nfloch/ssmp-bugs --comments`, and before each report), and act on them: resume the
  issue's agent (SendMessage) or start a new one, even if the issue is `correctif-pret`; answer with a "🤖 " comment.
- Never take Papaours' log while his game is running: wait until it is closed.
- No push to `fork` and no install unless the user asks. Push only to `fork` (nfloch/SSMP), never to `origin`.
- The public repo must never receive log excerpts. Issues and extracts go to the private tracker only.
- Keep the user's task list updated at each step, and finish with a report per issue.

## 1. Wait for the end of a session

SSMP sends each player's log by itself (`SSMP/Game/Client/LogUploader.cs`) when the player is disconnected or quits,
but only if another player was connected at the same time. The upload name is
`ssmp_<session>_<player>_<host|client>_<game start>_<n>.log.gz`, and the session id is generated at each server start.

Run `tools/bugs/attendre-logs.sh upload` with `run_in_background`. It exits once the host's upload is on Gokapi (the
host stopped the server or quit), plus 2 minutes for the others. It costs nothing while it waits, and its exit wakes
you up. Don't poll it. Exit 2 means it gave up after 8 h: tell the user.

If the user gave the end-of-session go directly, skip the wait.

## 2. Collect and rebuild the logs

1. Fetch every pending upload: `tools/gokapi/recuperer-logs.sh -n all`. Logs land in Windows `Downloads\ssmp-logs\`,
   and `.log.gz` files are extracted.
2. Rebuild one log per player and session:
   `python3 -I tools/bugs/assembler-logs.py --logs /mnt/c/Users/Papaours/Downloads/ssmp-logs --out <scratchpad>/sessions`.
   Several uploads of one game start keep the largest. Several game starts (restarts, reconnections) are joined in
   order, with a `===== game restarted` separator. Only the part after `[Session] Joined session <id>` is kept. The
   JSON summary gives each player's role (host/client), their game starts and the merged log path.
3. Fallback for logs sent by hand (`LogOutput-…` from envoyer-logs, or an SSMP build without LogUploader): use them as
   they are. Never take Papaours' log while his game is running: wait with `tools/bugs/attendre-logs.sh jeu-ferme`,
   then send it with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w tools/jojo/envoyer-logs.ps1)" -Profil SSMP`.

## 3. Split per marker

Use the session id from step 2, one `--log <player>=<merged log>` per player of the session (there can be more than
two: take every player of the step 2 summary, or of the `connected to the server` lines of the host's log):

```bash
python3 -I tools/bugs/decouper-logs.py --out ../ssmp-bugs/sessions/<session> \
  --state ~/.local/state/ssmp/markers.json --log Papaours=<…/Papaours.log> --log Jojo=<…/Jojo.log> [--log <player>=<…>]
```

It prints `new_bugs` as JSON: number, reporter, scene, description, the marker's time in each log, and its folder.
Each folder has one `<player>.txt` per player (snapshot + about 90 s of filtered log) and `meta.json`. Also read
`anomalies.txt`: it lists known error patterns found even without a marker. Create an `[ANOMALIE]` issue for any that
looks serious, for example an exception repeated hundreds of times or timeouts.

If `new_bugs` is empty and there's no serious anomaly, say so and go back to step 1.

## 4. Open the issues

1. In `../ssmp-bugs`, write `sessions/<session>/resume.md` (date, builds, players, list of bugs), commit, and push to
   `origin main`. Issues link to these files.
2. Create the milestone `Session <dd/mm hh:mm>` if it doesn't exist:
   `gh api repos/nfloch/ssmp-bugs/milestones -f title=...`.
3. **Merge the remaining duplicates before opening anything.** Compare the new bugs with each other and with the
   tracker's open issues (`gh issue list -R nfloch/ssmp-bugs --state open`). Treat two bugs as one when they have
   the same scene and the same entity or object in the snapshots, and the same errors or symptoms around them.
   Typical case: the same bug reported again a minute later, past the server's 20 s window.
   - Open a single issue that links every folder.
   - If an issue already exists, comment on it with the new folder instead.
   - If in doubt, keep them separate and cross-link them ("peut-être lié à #…").
4. Handle each remaining bug:
   - **Description `encore <issue>`** (a regression): reopen that issue, add the label `rouvert`, and comment with
     the new folder.
   - **Any other marker**: create an issue in the private tracker.
     - Title: `[BUG #n] <short summary> (<scene>)`.
     - Labels: `nouveau`, plus one theme (`ennemis`, `boss-arene`, `objets-sauvegarde`, `reseau`, `joueur`).
     - Milestone: the session's.
     - Body: who reported it, when and where, the description, the build commit, a 2–4 line summary of both snapshots,
       and a link to `sessions/<session>/bug-<n>/`.

Only one agent per issue: merged duplicates are never analysed twice.

## 5. One agent per bug

For each issue, switch the label to `en-analyse`, then start an agent with:
- `isolation: "worktree"`
- `run_in_background: true`
- **at most 3 at a time**

The agents use the default model. Their prompt (fill in the placeholders):

> You investigate one SSMP co-op bug (Hollow Knight: Silksong multiplayer mod, C#/BepInEx). You work in your own
> git worktree of the SSMP repo, on branch `bug/<issue>`, created from `world-sync`.
> - **Inputs:**
>   - Bug folder: `<folder>` (one `<player>.txt` per player, `meta.json`). Players of this session: <players with
>     their ids, from the `(id N)` of the markers>. Papaours hosts the server (id 0), and the scene host is the first
>     player in the room.
>   - Read `PRINCIPES.md` first: the co-op rules in force (what is shared or per player, scene host, players
>     entering late must get the current state, bosses and arenas). If your fix would contradict a rule, don't
>     decide: say so in your report. Then `SUIVI.md` for the history of the fixes.
>   - Full logs, if the extract is not enough: the merged logs `<one path per player>`. Use `grep -n` and `sed -n` on small
>     ranges only, never read them whole.
>   - Decompiled game code, read only: `/home/papaours/claude-projects/ssmp-build/game-src`.
>   - Game FSMs: `tools/fsm/README.md`.
> - **Goal:** find the cause, with evidence (log lines, `file:line`). If you have a fix you are confident in,
>   implement it and build it with `<build command> -o <scratch dir>`. Commit it on `bug/<issue>` with a message that
>   ends with `(nfloch/ssmp-bugs#<issue>)`.
> - **Report:** post one comment on the issue with `gh issue comment <issue> -R nfloch/ssmp-bugs`, starting with
>   "🤖 ", in French: cause,
>   evidence, confidence, and branch/commit or "pas de correctif". Then return the same report.
> - **Never:** push, touch the game install, run `rm -rf`, or edit `SUIVI.md`.

## 6. Integrate

For each finished agent:
1. Check its evidence against the logs and code. Don't take the report on trust.
2. Cherry-pick the good commits onto `world-sync`, resolve any conflicts, and build the whole branch.
3. Comment on the issue `Repris dans nfloch/SSMP@<sha>` and set the label `correctif-pret`.
4. If the fix is rejected, comment why and leave the issue `nouveau`, or set `sans-suite`.
5. Add an entry to `SUIVI.md` that links the issue, and commit. When the user takes a design decision (in the chat or
   an issue comment), update `PRINCIPES.md` too.

Then report to the user, one line per issue: cause, status, and link.

## 7. Afterwards

- **When the user asks to push and install:** push to `fork`, wait for the `Build DLL` run, install with
  `tools/jojo/installer-ssmp.ps1 -Profil SSMP`, set `a-retester` on the `correctif-pret` issues, and comment which build
  they are in.
- **When the user says a bug is fixed in game:** close its issue.
- **When the session goes on:** go back to step 1.
