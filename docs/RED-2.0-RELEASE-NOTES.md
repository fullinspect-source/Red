# RED 2.0 Release Notes

RED 2.0 is the next major RED update for field inspectors. It is focused on faster tablet work, cleaner navigation, and fewer taps while keeping the classic RED workflow available during the transition.

## Highlights

- My List redesigned with bigger touch-friendly rows.
- My List remembers window placement, column sizing, hidden/sortable columns, grouping, and search visibility.
- Group My List by builder, subdivision, inspection type, or no grouping.
- New inspection editor layout with a full-width running checklist instead of the old left checklist/right dashboard split.
- Only one checklist item expands at a time.
- Swipe/tap item tools: swipe or tap right to open item tools, swipe left to close.
- Better touch scrolling across the editor.
- Inline status/value controls are right-justified for easier right-handed tablet use.
- Section/category headers can collapse temporarily and include bulk NI/Pass buttons.
- Comments drawer now includes prefixes, suffixes, quick comments, saved comments, clear/trash, and the red-flag escalation marker.
- Photos, camera, and file tools are built into inline item tools.
- AI tools carried forward from classic RED: Get 3, transcription, tone options, and fact-checking.
- EC report and foundation/slab PDF design extraction can surface values beside checklist items where applicable.
- Extracted design values can be clicked into report fields.
- Experimental numberpad tool includes a touch-friendly keypad and slider.
- User preferences for tool drawers, font size, window placement, and other UI settings are remembered.
- Classic UI remains available inside the editor as a fallback during transition.

## Deployment notes

- Version: `2.0.0`
- Main executable: `Red.exe`
- Install folder: `C:\Red`
- User data folder: `%LOCALAPPDATA%\RED`
- Release type: standalone/self-contained win-x64 build.
- Installer/updater BAT: `update_red.bat`

## Safety / migration

The updater backs up existing RED files before install, including:

- `C:\Red`
- `%LOCALAPPDATA%\RED`
- `%LOCALAPPDATA%\InspectionEditor`
- `%LOCALAPPDATA%\RED-2.0-Dev`

Backup/log location on each machine:

`%LOCALAPPDATA%\RED_Backups\before-red2-YYYYMMDD-HHMMSS\`

The updater is designed to preserve:

- RED license file
- RED settings/preferences
- saved comments
- custom prefixes/suffixes
- user templates/data under RED userdata folders
- Dropbox inspection data

## Manual test required before broad rollout

Because RED is a Windows WPF app and the updater is a Windows BAT, final launch/install proof must be completed on a Windows machine before sending to all users.
