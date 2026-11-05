#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
if [ ! -x .venv/bin/python ]; then
    echo 'Mac dependencies have not been installed in .venv.'
    exit 1
fi
if [ ! -f data/windows-cert.pem ]; then
    echo 'Copy data/windows-cert.pem from Windows into this project data folder first.'
    exit 1
fi
windows_host="${1:-}"
if [ -z "$windows_host" ]; then
    read -r -p 'Windows LAN IP: ' windows_host
fi
if [ -z "$windows_host" ]; then
    echo 'Windows LAN IP is required.'
    exit 1
fi
windows_side="${2:-right}"
exec .venv/bin/python mousharing.py send --host "$windows_host" --cert data/windows-cert.pem --windows-side "$windows_side"
