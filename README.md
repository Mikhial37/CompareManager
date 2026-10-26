<p align="center"><img src="CompareManager/Resources/logo.png" width="96" alt="Compare Manager logo"></p>

# Compare Manager

A Windows desktop app for comparing SQL objects between two places. Each side can be a
**folder of scripts** or a **SQL Server database**. You tick the objects you care about, and it
lists the ones whose text differs, the ones whose change dates differ, and the ones that exist
on only one side.

## What it does

- **Two sides, any mix:** folder vs database, database vs database, or folder vs folder.
- **Pick what to compare:** every procedure, function, view, trigger and table found on either side
  is listed with a tick box. You can search it and filter it by type and by side. The left side is the
  primary set and the right is checked against it, so the right's objects start ticked, and only what
  differs or has changed is listed (the other kinds are counted, and can be shown).
- **Compares the object, not how it was written:** `ALTER PROCEDURE dbo.x` in a script matches
  `CREATE PROCEDURE [dbo].[x]` on the server, and comments above the `CREATE` (Change Control
  lines, history blocks) don't count. Whitespace, case and comments can each be ignored.
- **Checks three dates, each on its own:** the date written in the script's header comments, the
  last-changed date (`modify_date` or the file's last write time) and the created date
  (`create_date` or the file's creation time).
- **Shows the difference:** a built-in side-by-side viewer with the changed lines tinted, or
  WinMerge if it is installed. The results can be exported to CSV.
- **Finds duplicate scripts in either folder or both:** objects defined by more than one script (and whether
  those copies have drifted apart, and which copy runs last when the folder is applied — an older one
  running last overwrites the newer), and scripts saved twice under different names. The newest copy
  is the one compared.
- **Runs a folder's scripts on a database (Run scripts…):** every `.sql` below the folder, in the
  order Explorer lists them, each aimed at the chosen database (its opening `USE` line, or a
  `--USE []` placeholder, as Issue Manager's publish does). **Check scripts** compiles them all with
  `SET NOEXEC ON` and runs nothing. **Run ticked scripts** applies them, stopping at the first
  failure; a transaction a failed script leaves open is rolled back. Issue Manager's SQL connections
  decide where scripts may run: a connection it marks Read only is refused, and one it doesn't know
  needs its database name typed. Every run is logged to `%LOCALAPPDATA%\CompareManager\run_logs`.
- **Shows what has already run, and who wrote what:** each script's Change Control header
  (`@IssueNumber`, `@ScriptName`, `@User`) is read, and its runs are looked up in the target's
  `[Symplexity Change Control]` — how many times, when, and whether each completed, failed or never
  finished. Scripts are counted per person, and a script without a working
  `SYMsp_SymplexityChangeCTRL` call (start and end calls, or the one-call `@Identity = -1` form) is
  flagged. Running a script that already completed there asks first.
- **Comparing only reads:** the compare runs `SELECT`s on `sys.objects`, `sys.sql_modules` and
  `sys.columns`, nothing else.

Saved passwords are encrypted with Windows DPAPI for the current user. If
[Issue Manager](#issue-manager) is installed, its saved SQL connections are offered in the
connection picker.

## Build and run

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build CompareManager.sln -c Debug
.\CompareManager\bin\Debug\net10.0-windows\CompareManager.exe
dotnet test CompareManager.sln -c Debug
```

## Layout

```
CompareManager/
  Program.cs, MainForm.cs        the main window: sides → load → tick → compare
  SourceForm.cs                  picks a side (folder, or server + login + database)
  DiffForm.cs                    side-by-side diff of one object
  DuplicatesForm.cs              duplicate scripts in a folder
  ScriptRunnerForm.cs            Run scripts: pick a folder and a database, check, run
  Classes/
    ScriptParser.cs              which object a script defines; the batch that defines it
    FolderSource.cs              reads a folder of .sql files
    DatabaseSource.cs            reads a database's objects (and writes table scripts from columns)
    ObjectComparer.cs            pairs both sides up and decides what differs
    Duplicates.cs                groups duplicate scripts
    LineDiff.cs                  Myers line diff for the viewer
    ScriptBatches.cs             splits scripts on GO, aims their USE at the chosen database
    ScriptRunner.cs              runs or checks (NOEXEC) a folder of scripts, logs the run
    RunPermission.cs             where scripts may run, by Issue Manager's SQL connections
    ChangeControlHistory.cs      a script's CC header, its runs in [Symplexity Change Control], scripts per person
    AppSettings.cs, Secret.cs    settings in %LOCALAPPDATA%\CompareManager, DPAPI passwords
  Tools/MakeLogo.ps1             draws the logo (CompareManager.ico, Resources/logo.png)
CompareManager.Tests/            xUnit tests for the logic above
```

## Issue Manager

Compare Manager was built alongside Issue Manager, a separate internal tool. It reads that app's
`%LOCALAPPDATA%\IssueManager\sql_connections.json` if it exists, never writes it, and works the same
without it.
