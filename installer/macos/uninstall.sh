#!/bin/bash
# Removes OneDesk from the current user. Configuration and logs are kept unless
# --purge is passed.
#
# Usage:
#   ./installer/macos/uninstall.sh
#   ./installer/macos/uninstall.sh --purge

set -euo pipefail

LABEL="com.onedesk.client"
APP="/Applications/OneDesk.app"
PURGE=0

if [ "${1:-}" = "--purge" ]; then
    PURGE=1
fi

uid="$(/usr/bin/id -u)"
/bin/launchctl bootout "gui/$uid/$LABEL" >/dev/null 2>&1 || true
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"

# The agent may have been started outside launchd (for example from a terminal).
/usr/bin/pkill -f "$APP/Contents/MacOS/OneDesk" >/dev/null 2>&1 || true

if [ -w /Applications ]; then
    rm -rf "$APP"
else
    sudo rm -rf "$APP"
fi

if [ "$PURGE" = "1" ]; then
    rm -rf "$HOME/Library/Application Support/OneDesk"
    rm -rf "$HOME/Library/Logs/OneDesk"
    echo "Removed OneDesk, configuration, and logs."
else
    echo "Removed OneDesk. Configuration and logs were kept."
    echo "Use --purge to remove them as well."
fi

echo "If OneDesk still appears in System Settings > Privacy & Security > Accessibility,"
echo "select it and press the minus button to remove the stale entry."
