---
name: terminal-windows
description: Operate the AI-dedicated Windows VM (terminal-windows, on VirtualBox or VMware Workstation) through its MCP tools - take screenshots, click, type, read UI Automation trees, take snapshots, hand files to the user via the Outbox, and keep work notes. Use whenever a task must be done in a Windows GUI without touching the user's own PC.
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

- A GUI is slow and expensive (every step needs a screenshot). Use it for GUI-only apps and pages that
  need a real browser. If the task is really about files, installs or scripts, say so and suggest the
  person run it with an AI *inside* the VM (`terminal-windows claude --window`), which has a shell there.
- `ui_tree` is cheaper than a screenshot when you only need to find a control or read text.
- An AI may already be working inside the VM in a "Claude Code" / "Codex" / "Gemini CLI" terminal
  window. Do not type into, close or minimise that window; bring your target app to the front first.

## The loop: observe → act → verify

- Always take a `screenshot` before acting and again after acting to confirm the result.
  Never chain many clicks blindly; screens change (dialogs, loading, focus).
- Coordinates are **pixels of the latest screenshot image** (top-left is 0,0). The server converts
  them to the guest's real resolution, so use the numbers you see in the image directly.
- Prefer `ui_tree` when you need precise targets (small buttons, list items, text fields):
  each node has `name`, `controlType`, `rect = [x, y, width, height]` in the same screenshot pixels.
  Click the centre: `x + width/2`, `y + height/2`. Use `window: "desktop"` to list top-level windows.
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
