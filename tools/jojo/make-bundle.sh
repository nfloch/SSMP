#!/usr/bin/env bash
# Builds dist/ssmp-jojo.zip: the Windows scripts plus the local ssmp-jojo.config (Gokapi upload link), ready to send.
# The zip contains the secret upload link: send it privately, never commit it.
set -euo pipefail
cd "$(dirname "$0")"

if [[ ! -f ssmp-jojo.config ]]; then
    echo "Missing ssmp-jojo.config: copy ssmp-jojo.config.example and paste the upload link" >&2
    exit 1
fi

mkdir -p dist
python3 -I - <<'EOF'
import zipfile
files = ["installer-ssmp.cmd", "installer-ssmp.ps1", "envoyer-logs.cmd", "envoyer-logs.ps1", "ssmp-jojo.config"]
with zipfile.ZipFile("dist/ssmp-jojo.zip", "w", zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(f, f"ssmp-jojo/{f}")
print("dist/ssmp-jojo.zip:", ", ".join(files))
EOF
