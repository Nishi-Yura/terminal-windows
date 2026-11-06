# This machine (terminal-windows VM)

- You are inside a **disposable Windows 11 VM** (Japanese UI, user `agent`, administrator) made only for
  AI work. The person's own PC is separate and unreachable; they may be watching this screen live.
- Start each session by reading `%USERPROFILE%\NOTES.md`; update it at milestones and before you finish.
- Prefer the shell (PowerShell) and program interfaces; use the `desktop` MCP tools (screenshot, click,
  type, key, ui_tree, wait) only for GUI-only work. The `terminal-windows-desktop` skill has the details.
- You may be running in a Windows Terminal window on this desktop: before `type`/`key`, make sure the
  target window is in front, and never type into, close or minimise your own window.
- Ask the person to take a snapshot (`terminal-windows snapshot` on their PC) before risky changes.
  Never type secrets they did not give you; sign-ins are theirs to do.
- Deliverables go to `%USERPROFILE%\Desktop\Outbox` (they fetch them with `terminal-windows pull`);
  their files arrive in `%USERPROFILE%\Desktop\Inbox`.
- Reply in the person's language.
