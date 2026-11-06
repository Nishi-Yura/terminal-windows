---
name: terminal-windows-desktop
description: You are running inside a disposable Windows 11 VM made for AI work (terminal-windows). Use the "desktop" MCP tools to see and operate this VM's own screen (screenshot, click, type, key, ui_tree, wait), and the shell for files, installs and anything scriptable. Use whenever a task needs a Windows GUI app, a browser, or touches this machine's desktop.
---

# Working inside the terminal-windows VM

You run **inside** a Windows 11 VM (Japanese UI, user `agent`, administrator) that exists only for AI
work. The person talks to you from their own PC through `terminal-windows`. Their PC is not reachable
from here and you cannot touch it. They can watch this VM's screen live, so work tidily.

## Choose the right tool (cheapest that works)

1. **Shell first.** PowerShell / Git Bash for files, installs (`winget install --id X -e --accept-source-agreements --accept-package-agreements`),
   builds, downloads, registry, services, scheduled tasks. It is faster, exact and leaves a log.
2. **Program interfaces next**: an app's CLI, COM automation (Excel/Word via PowerShell), web APIs,
   `Invoke-WebRequest`. Use them instead of clicking through a GUI when they exist.
3. **GUI last**, with the `desktop` MCP tools, when there is no other way (a GUI-only app, a web page
   that needs a real browser, checking how something looks).

## Your own window — do not type into yourself

You may be running in a **Windows Terminal window on this same desktop** (title "Claude Code",
"Codex" or "Gemini CLI"). Keys and text from the `desktop` tools go to whatever window has focus.

- Before `type` / `key`, make sure the **target** window is in front: click inside it (or its taskbar
  button), then check with a `screenshot` or `status` (`foregroundWindow`).
- Never close, minimise or type into your own terminal window. Never press `alt+f4` unless the
  window you mean is verified to be in front.
- To get your window out of the way, open the target app maximised (`Start-Process ... -WindowStyle Maximized`),
  or click the target app; do not move your own window.

## GUI loop: observe → act → verify

- `screenshot` before acting and after, to confirm the result. Screens change (dialogs, loading, focus).
- Coordinates are **pixels of the latest screenshot image** (top-left 0,0); use them directly.
- Prefer `ui_tree` for precise targets: each node has `name`, `controlType`, `rect = [x, y, w, h]` in the
  same pixels; click the centre. `window: "desktop"` lists the top-level windows. It is cheaper than a
  screenshot when you only need to find a button or read text.
- `type` sends Unicode directly (Japanese works without the IME). Click the field first.
- `key` takes chords: `enter`, `ctrl+s`, `win+r`, `ctrl+l`, `ctrl+shift+esc`; several separated by spaces.
- After starting something slow, `wait` with `until_change: true` instead of sleeping blindly.
- Do several obvious steps between screenshots only when nothing can pop up in between.
- If `status` says the session is not `desktop` (lock / logon / UAC secure screen), stop and tell the person.
  UAC prompts do not appear for you (you are already elevated); if one shows, report it.

## Windows specifics that save time

- Windows PowerShell 5.1 is the default shell. Set `$ProgressPreference = 'SilentlyContinue'` before big
  `Invoke-WebRequest` downloads (the progress bar makes them many times slower).
- Output with Japanese text: `[Console]::OutputEncoding = [Text.Encoding]::UTF8`. Paths may contain Japanese;
  always quote paths.
- After installing something, refresh PATH in the same shell:
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- Long jobs: run them in the background with output to a log file (`Start-Process ... -RedirectStandardOutput`),
  then check the log; do not block for many minutes.
- Microsoft Edge is installed (first-run screens are off). Open a URL with `Start-Process msedge 'https://…'`.

## Safety

- You have administrator rights; that is intended (the VM is disposable). Before destructive or
  hard-to-undo changes, **ask the person to take a snapshot first** (`terminal-windows snapshot` on their PC).
  You cannot snapshot or restore from inside.
- Do not bypass CAPTCHAs, bot detection, anti-cheat, licence checks, or services' terms.
- Never type passwords, card numbers or other secrets the person did not give you for this task;
  ask them to do sign-ins themselves (they can type into this VM through the viewer).
- The VM has internet access. Do not send the person's data anywhere they did not ask for.
- Your AI service sign-in is stored in this VM; never print or copy credential files.

## Talking to the person

- Answer in the person's language (Japanese if they write Japanese).
- Say what you are about to do before long or risky steps, and summarise what you did at the end:
  results, where files are, anything left undone or uncertain.

## Handing results over

- Put deliverables in `%USERPROFILE%\Desktop\Outbox` and tell the person to run `terminal-windows pull`
  on their PC (it copies the Outbox to their machine).
- Files they send arrive in `%USERPROFILE%\Desktop\Inbox`.

## Notes for the next session

Keep `%USERPROFILE%\NOTES.md` up to date: what you did, where deliverables are, what is next and open
problems. Read it at the start of every session.
