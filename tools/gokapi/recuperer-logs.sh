#!/usr/bin/env bash
# Downloads the logs uploaded through the Gokapi file request (tools/jojo/envoyer-logs.ps1) and extracts them.
#
# usage: tools/gokapi/recuperer-logs.sh [-l] [-k] [-n <count>] [-o <dir>]
#   -l  only list the uploads (most recent first), without downloading
#   -n  number of most recent uploads to fetch (default 1; "all" for every upload)
#   -o  output directory (default: ssmp-logs in the Windows Downloads under WSL, else ~/Downloads/ssmp-logs)
#   -k  keep the uploads on Gokapi (by default, each upload is deleted once its local copy is in place)
#
# Configuration, outside the repo, in ~/.config/ssmp/gokapi.env:
#   GOKAPI_URL=https://files.example.org
#   GOKAPI_REQUEST_ID=<file request id, the "id" of the publicUpload link>
#   GOKAPI_API_KEY=<admin API key with the "view", "download" and "delete" permissions>
# Already downloaded uploads are not downloaded again (but still deleted from Gokapi, unless -k).
set -euo pipefail

count=1
out=""
list_only=false
keep=false
while getopts "kln:o:h" opt; do
    case "$opt" in
        k) keep=true ;;
        l) list_only=true ;;
        n) count="$OPTARG" ;;
        o) out="$OPTARG" ;;
        *) sed -n '2,16p' "$0"; exit 1 ;;
    esac
done

config="${XDG_CONFIG_HOME:-$HOME/.config}/ssmp/gokapi.env"
if [[ ! -f "$config" ]]; then
    echo "Missing $config (see the header of this script)" >&2
    exit 1
fi
# shellcheck source=/dev/null
source "$config"
: "${GOKAPI_URL:?missing in $config}" "${GOKAPI_REQUEST_ID:?missing in $config}" "${GOKAPI_API_KEY:?missing in $config}"

if [[ -z "$out" ]]; then
    if command -v cmd.exe >/dev/null && command -v wslpath >/dev/null; then
        out="$(wslpath "$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')")/Downloads/ssmp-logs"
    else
        out="$HOME/Downloads/ssmp-logs"
    fi
fi
mkdir -p "$out"

api="${GOKAPI_URL%/}/api"
list="$(curl -sSf -H "apikey: $GOKAPI_API_KEY" -H "showFileRequests: true" "$api/files/list")"

# Uploads of this file request, most recent first: "<id>\t<name>" (a variable, so that a failure stops the script)
selected="$(python3 -I -c '
import json, sys
files = json.loads(sys.argv[1]) or []
request_id, count = sys.argv[2], sys.argv[3]
files = [f for f in files if f.get("FileRequestId") == request_id and not f.get("IsPendingDeletion")]
files.sort(key=lambda f: f.get("UploadDate", 0), reverse=True)
if count != "all":
    files = files[:int(count)]
for f in files:
    print(f["Id"] + "\t" + f["Name"] + "\t" + str(f.get("UploadDate", 0)) + "\t" + str(f.get("Size", "")))
' "$list" "$GOKAPI_REQUEST_ID" "$count")"
uploads=()
[[ -n "$selected" ]] && mapfile -t uploads <<< "$selected"

if [[ ${#uploads[@]} -eq 0 ]]; then
    echo "No upload found for this file request."
    exit 0
fi

if $list_only; then
    for line in "${uploads[@]}"; do
        IFS=$'\t' read -r id name date size <<< "$line"
        printf '%s  %-9s %s  (%s)\n' "$(date -d "@$date" '+%Y-%m-%d %H:%M')" "$size" "$name" "$id"
    done
    exit 0
fi

# Deletes an upload from Gokapi, once its local copy is in place
remove_upload() {
    local id="$1" code
    $keep && return 0
    code="$(curl -sS -o /dev/null -w '%{http_code}' -X DELETE \
        -H "apikey: $GOKAPI_API_KEY" -H "id: $id" "$api/files/delete")" || code="error"
    if [[ "$code" == 200 ]]; then
        echo "  deleted from Gokapi"
    else
        echo "  NOT deleted from Gokapi (HTTP $code; the API key needs the Delete permission)" >&2
    fi
}

for line in "${uploads[@]}"; do
    IFS=$'\t' read -r id name _ _ <<< "$line"
    # Prefixed with the Gokapi id: uploads often share a name (LogOutput.log)
    name="${id}_$(basename "$name")"
    zip="$out/$name"
    if [[ -f "$zip" ]]; then
        echo "Already downloaded: $zip"
        remove_upload "$id"
        continue
    fi

    curl -sSf -H "apikey: $GOKAPI_API_KEY" -o "$zip" "$api/files/download/$id"

    if [[ "$name" == *.zip ]]; then
        log="$out/${name%.zip}.log"
        # Exit code 2: the zip holds no .log file (another upload), it is kept as is
        rc=0
        python3 -I -c '
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    member = next((n for n in z.namelist() if n.lower().endswith(".log")), None)
    if member is None:
        sys.exit(2)
    with z.open(member) as src, open(sys.argv[2], "wb") as dst:
        dst.write(src.read())
' "$zip" "$log" || rc=$?
        case "$rc" in
            0) echo "$log ($(wc -l < "$log") lines)" ;;
            2) echo "$zip (no log inside)" ;;
            *) echo "$zip: could not extract it, kept on Gokapi" >&2; continue ;;
        esac
    elif [[ "$name" == *.log.gz ]]; then
        # Sent by SSMP itself at the end of a session (SSMP/Game/Client/LogUploader.cs)
        log="$out/${name%.gz}"
        if gzip -dc "$zip" > "$log"; then
            echo "$log ($(wc -l < "$log") lines)"
        else
            echo "$zip: could not extract it, kept on Gokapi" >&2
            continue
        fi
    else
        echo "$zip"
    fi
    remove_upload "$id"
done
