#!/usr/bin/env bash
# Waits, without using any model, for something to happen during a co-op debug session, then exits so that the
# Claude session running it in the background wakes up.
#
# usage: tools/bugs/attendre-logs.sh upload [-i <seconds>] [-t <hours>]
#          waits until an upload from another player than Papaours is on Gokapi (exit 0)
#        tools/bugs/attendre-logs.sh jeu-ferme [-i <seconds>] [-t <hours>]
#          waits until Silksong is no longer running on this PC (exit 0)
#   -i  check interval (default 120 s for upload, 20 s for jeu-ferme)
#   -t  give up after this many hours (default 8), with exit code 2
# Network errors are retried, not reported as a result.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mode="${1:-}"
shift || true

interval=""
timeout_hours=8
while getopts "i:t:" opt; do
    case "$opt" in
        i) interval="$OPTARG" ;;
        t) timeout_hours="$OPTARG" ;;
        *) sed -n '2,11p' "$0"; exit 64 ;;
    esac
done

case "$mode" in
    upload) interval="${interval:-120}" ;;
    jeu-ferme) interval="${interval:-20}" ;;
    *) sed -n '2,11p' "$0"; exit 64 ;;
esac

deadline=$(( $(date +%s) + timeout_hours * 3600 ))

game_running() {
    tasklist.exe /FI "IMAGENAME eq Hollow Knight Silksong.exe" /NH 2>/dev/null | grep -qi "silksong"
}

other_upload() {
    # "<id>\t<name>" lines; Papaours' own uploads are made and fetched by Claude, they don't count
    local list
    list="$("$here/../gokapi/recuperer-logs.sh" -l -n all 2>/dev/null)" || return 1
    grep -v -- "-Papaours-" <<<"$list" | grep -q "LogOutput-"
}

while (( $(date +%s) < deadline )); do
    case "$mode" in
        upload)
            if other_upload; then
                echo "New upload on Gokapi:"
                "$here/../gokapi/recuperer-logs.sh" -l -n all | grep -v -- "-Papaours-"
                exit 0
            fi
            ;;
        jeu-ferme)
            if ! game_running; then
                echo "Silksong is closed"
                exit 0
            fi
            ;;
    esac
    sleep "$interval"
done

echo "Gave up after ${timeout_hours} h"
exit 2
