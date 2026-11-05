---
name: terminal-windows-desktop
description: You are running inside a disposable Windows VM made for AI work (terminal-windows). Use the "desktop" MCP tools to see and operate this VM's own screen (screenshot, click, type, key, ui_tree), and the shell for files and commands. Use whenever a task needs a Windows GUI app or touches this machine's desktop.
---

# Working inside the terminal-windows VM

You run **inside** a Windows 11 VM that exists only for AI work. The person talks to you from their
own PC through `terminal-windows claude`; their PC is not reachable from here and you cannot touch it.

## What you have

- **Shell** (PowerShell / Git Bash): use it for files, installs, builds and anything scriptable. It is
  usually faster and more reliable than clicking.
- **`desktop` MCP tools** for the GUI of this same VM:
  `screenshot`, `click`, `drag`, `scroll`, `type`, `key`, `ui_tree`, `wait`, `status`.
  Your own terminal has no window on this desktop, so GUI actions never type into your terminal.

## GUI loop: observe → act → verify

- `screenshot` before acting and again after, to confirm the result. Screens change (dialogs, loading, focus).
- Coordinates are **pixels of the latest screenshot image** (top-left 0,0); use them directly.
- For small targets use `ui_tree`: each node has `name`, `controlType` and `rect = [x, y, width, height]`
  in the same pixels. Click the centre. `window: "desktop"` lists top-level windows.
- `type` sends Unicode text directly (Japanese works without the IME). Click the field first.
- `key` takes chords: `enter`, `ctrl+s`, `win+r`, `alt+f4`, `ctrl+shift+esc`.
- After starting something slow, use `wait` with `until_change: true`.
- If `status` says the session is not `desktop` (lock/logon/UAC secure screen), stop and tell the person.

## Safety

- You have administrator rights in this VM. That is intended: it is disposable. Still, before
  destructive or hard-to-undo changes, **ask the person to take a snapshot first**
  (`terminal-windows snapshot NAME` on their PC). You cannot snapshot or restore from inside.
- Do not try to bypass CAPTCHAs, bot detection, anti-cheat, licence checks, or services' terms.
- The VM has internet access. Do not send the person's data anywhere they did not ask for.
- Your Claude sign-in is stored in this VM; never print or copy credential files.

## Handing results to the person

- Put deliverables in `%USERPROFILE%\Desktop\Outbox`. Tell the person to run
  `terminal-windows pull NAME` on their PC (it copies the Outbox to their machine).
- Files they send arrive in `%USERPROFILE%\Desktop\Inbox`.

## Notes for the next session

Keep `%USERPROFILE%\NOTES.md` up to date: what you did, where the deliverables are, what is next and
open problems. Read it at the start of every session.
