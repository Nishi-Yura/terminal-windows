---
name: terminal-windows
description: Operate the AI-dedicated Windows VM (terminal-windows, on VirtualBox or VMware Workstation) through its MCP tools - screenshots, clicking controls by name, filling fields, reading window text, opening apps, running PowerShell in the VM, snapshots, handing files to the user via the Outbox, and work notes. Use whenever a task must be done in a Windows GUI without touching the user's own PC.
---

# terminal-windows: operating the AI Windows VM

You control a separate Windows 11 VM. Everything you do happens **inside the VM only** — the user's
own mouse, keyboard and clipboard are never touched, so they keep working on their PC while you work.

## Start of every session

1. `read_notes` — the notes on the host tell you what earlier sessions did, where deliverables are and what is next.
2. `status` — check the VM is running, the helper is reachable and `session` is `desktop`.
   - If `session` is not `desktop` (lock screen, logon screen, UAC secure desktop): **stop and tell the user**.
     Do not try to type passwords or unlock anything.
3. `screenshot` to see where things are.

## Choose the cheapest way that works

1. **`run_powershell`** for files, installs (`winget install --id X -e --accept-source-agreements --accept-package-agreements`),
   settings, registry, services and checks. Exact, fast, and it returns the output.
2. **Element tools** for GUI apps — they work by the control's name, so no pixel guessing:
   - `open` starts an app / file / folder / URL (`notepad`, `ms-settings:display`, `https://…`).
   - `click_element` (name / control_type / automation_id) clicks a button, menu item, tab, link, list item.
   - `set_text` fills an input box (replaces its content; refuses password fields).
   - `wait_for` waits for a dialog, label or button to appear (`gone: true` to disappear).
   - `read_text` reads a window's text (results, error messages, forms) without a screenshot.
   - `find_element` / `ui_tree` when you need to see what is there; `list_windows` / `focus_window` for windows.
   - `clipboard_set` + `key ctrl+v` for long or multi-line text; `clipboard_get` to read what an app copied.
3. **Pixels** (`screenshot`, `click`, `drag`, `scroll`, `type`, `key`) when a control has no usable name
   (canvases, games, custom-drawn UI) or to check how something looks.
- An AI may already be working inside the VM in a "Claude Code" / "Codex" / "Gemini CLI" terminal
  window. Do not type into, close or minimise that window; bring your target app to the front first.

## The loop: observe → act → verify

- Always take a `screenshot` before acting and again after acting to confirm the result.
  Never chain many clicks blindly; screens change (dialogs, loading, focus).
- Coordinates are **pixels of the latest screenshot image** (top-left is 0,0). The server converts
  them to the guest's real resolution, so use the numbers you see in the image directly.
- Element results carry `rect = [x, y, width, height]` and `center` in the same screenshot pixels.
  When several controls match, the visible ones come first; pass `index` or narrow with `control_type` / `window`.
- If an element tool says the helper is older than the tool, ask the person to run `terminal-windows update-helper`.
- `type` sends Unicode text directly (Japanese works without the IME). Click the field first.
- `key` takes chords: `enter`, `ctrl+s`, `win+r`, `alt+f4`, `ctrl+shift+esc`, several separated by spaces.
- After launching something slow, use `wait` with `until_change: true` instead of guessing a delay.
- Windows shortcuts that are handy: `win+r` (Run dialog), `win+e` (Explorer), `win` (Start),
  `ctrl+l` in Explorer/browsers (address bar).

## Talking to the person

- Answer in their language (Japanese if they write Japanese). Before long or risky steps say what you
  will do; at the end summarise the result, where files are, and anything left undone.

## Safety

- **Before destructive or hard-to-undo actions** (installing/uninstalling software, deleting files,
  changing system settings, running unknown scripts) call `snapshot` with a descriptive label.
  If things go wrong, `restore` it; the current state is saved automatically as `pre-restore-…`.
- Do not try to bypass CAPTCHAs, bot detection, anti-cheat, licence checks, or the terms of any service.
- Never type passwords, card numbers or other secrets the person did not give you for this task; sign-ins are theirs to do.
- The VM can reach the internet through NAT. Do not exfiltrate the user's data or download untrusted software unless the user asked for it.

## Handing results to the user

- Put deliverables in `C:\Users\agent\Desktop\Outbox` inside the VM.
- Call `export_file` (optionally with a path relative to the Outbox). It copies the files to the host
  and returns the host folder. **Tell the user that host path** — do not just say "it's in the VM".
- Files the user gives you arrive in `C:\Users\agent\Desktop\Inbox`; see them with `list_inbox`.

## Keeping notes (continuity)

At each milestone and before you finish, `append_notes` with:
- what you did,
- where the deliverables are (guest and host paths),
- what to do next,
- unresolved problems.

Notes live on the host, so they survive VM restores.
