# CompareManager

Windows Forms desktop app (C#, .NET 10) that compares SQL objects between two sides, each a
folder of scripts or a SQL Server database, and finds duplicate scripts in a folder. See
README.md for what it does.

## Build & Run

```powershell
dotnet build CompareManager.sln -c Debug
.\CompareManager\bin\Debug\net10.0-windows\CompareManager.exe
dotnet test CompareManager.sln -c Debug
```

## How it fits together

- `ScriptParser` finds the object a script defines and cuts out its batch. Comments and strings
  are blanked first, character for character, so positions in the blanked text are positions in
  the real one. `GO` detection must allow `\r` before `$` — CRLF scripts are the norm.
- `FolderSource.ReadAll` reads every script; `Load` collapses them to one per object (the newest,
  with `OtherCopies`); `Duplicates.Find` groups them for the duplicates window.
- `DatabaseSource` only ever runs SELECTs. Tables have no stored text, so `TableScript` writes one
  from the columns in a fixed shape.
- `ObjectComparer` matches the sides on `SqlObject.Key` and compares after `Normalize`; the
  header is rewritten as `CREATE <kind> [schema].[name]` so CREATE vs ALTER never counts.
- Forms are code-only (no designer files). Colours and buttons come from `Classes/Ui.cs`.

## Configuration

- `%LOCALAPPDATA%\CompareManager\settings.json` — the two sides, options, recent folders and
  connections. Passwords via `Secret` (DPAPI, current user, this app's entropy).
- Issue Manager's `%LOCALAPPDATA%\IssueManager\sql_connections.json` is read, never written;
  `Secret.RevealIssueManager` uses that app's entropy, so do not change that string.

## Logo

`Tools/MakeLogo.ps1` draws `CompareManager.ico` (16–256 px) and `Resources/logo.png`. Change the
script and re-run it rather than editing the icon.

## This repo is public

Keep real server names, client databases, logins and issue numbers out of code, comments and
tests — use made-up examples (`SQLDEV01 · Payroll`).
