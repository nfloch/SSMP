#!/usr/bin/env bash
# Downloads the logs uploaded through the Gokapi file request (tools/jojo/envoyer-logs.ps1) and extracts them.
#
# usage: tools/gokapi/recuperer-logs.sh [-n <count>] [-o <dir>]
#   -n  number of most recent uploads to fetch (default 1; "all" for every upload)
#   -o  output directory (default: Windows Downloads under WSL, else ~/Downloads)
#
# Configuration, outside the repo, in ~/.config/ssmp/gokapi.env:
#   GOKAPI_URL=https://files.example.org
#   GOKAPI_REQUEST_ID=<file request id, the "id" of the publicUpload link>
#   GOKAPI_API_KEY=<admin API key with the "view" and "download" permissions>
# Already downloaded uploads are skipped.
set -euo pipefail

count=1
out=""
while getopts "n:o:h" opt; do
    case "$opt" in
        n) count="$OPTARG" ;;
        o) out="$OPTARG" ;;
        *) sed -n '2,13p' "$0"; exit 1 ;;
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
        out="$(wslpath "$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')")/Downloads"
    else
        out="$HOME/Downloads"
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
    print(f["Id"] + "\t" + f["Name"])
' "$list" "$GOKAPI_REQUEST_ID" "$count")"
uploads=()
[[ -n "$selected" ]] && mapfile -t uploads <<< "$selected"

if [[ ${#uploads[@]} -eq 0 ]]; then
    echo "No upload found for this file request."
    exit 0
fi

for line in "${uploads[@]}"; do
    id="${line%%$'\t'*}"
    name="${line#*$'\t'}"
    name="$(basename "$name")"
    zip="$out/$name"
    if [[ -f "$zip" ]]; then
        echo "Already downloaded: $zip"
        continue
    fi

    curl -sSf -H "apikey: $GOKAPI_API_KEY" -o "$zip" "$api/files/download/$id"

    if [[ "$name" == *.zip ]]; then
        log="$out/${name%.zip}.log"
        python3 -I -c '
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    member = next(n for n in z.namelist() if n.lower().endswith(".log"))
    with z.open(member) as src, open(sys.argv[2], "wb") as dst:
        dst.write(src.read())
' "$zip" "$log"
        echo "$log ($(wc -l < "$log") lines)"
    else
        echo "$zip"
    fi
done
