# uSync Git Edition

🚨 **EXPERIMENTAL CODE - USE WITH CAUTION** 🚨

This project is experimental and should not be used in production environments without thorough testing.

## Overview

uSync Git Edition makes an Umbraco site aware of the git checkout it runs from. It covers two workflows:

- **Branch switching**: optionally give each branch its own database, and import from uSync when the commit changes.
- **Git worktrees** (for example parallel Claude Code sessions, each in its own worktree): every worktree gets its own database, copied from the main checkout, its own ports, and the main checkout's media.

Everything happens inside the site at startup. Nothing is written to `appsettings.json`: the values go into an in-memory configuration source, so they work however the site is started (Visual Studio, Rider, `dotnet run`, an AI agent).

## What happens at startup

### In a linked worktree

1. **Database**
   - **SQLite**: the database file lives in the worktree's own `umbraco/Data` folder, so it is already separate. If it doesn't exist, it is copied from the main checkout's database with SQLite's backup API, which is safe while the main site is running.
   - **SQL Server**: the worktree uses `{database}_wt_{worktree}` on the same server. If it doesn't exist, the main database is copied with `BACKUP ... WITH COPY_ONLY` and `RESTORE ... WITH MOVE` into the server's default data folders.
   - If there is nothing to copy (or `Seed` is `Empty`), `InstallUnattended` is switched on so Umbraco installs a fresh database and uSync's `ImportOnFirstBoot` fills it.
2. **Media**: `Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath` points at the main checkout's `wwwroot/media`, which is gitignored and so missing from the worktree. Skipped if you've set that path yourself.
3. **Ports**: a stable https/http pair in the 45000 range, picked from a hash of the worktree name. The same worktree gets the same ports on every run unless something else holds them. This overrides `launchSettings.json`.

### In every checkout

4. **Import on commit change**: when Umbraco starts, the HEAD commit is compared with the commit that was last imported into *this database* (stored in Umbraco's key-value table, so a copied database brings it along). If they differ, uSync imports. A plain restart doesn't import. While the uSync folder has uncommitted changes the import is skipped, because those are normally exports from this site.
5. **Branch databases** (off by default): with `NameByBranch`, the database name comes from `Template`, so switching branch switches database. A branch with no database yet gets a fresh unattended install.

### In the main checkout

6. **Tidy up**: SQL Server databases named `{database}_wt_*` whose worktree no longer exists are dropped, in the background, after startup. SQLite worktree databases go when the worktree folder is deleted.

## Setup

1. Reference the `uSync.GitEdition` project (or package) from the site. The composer wires everything up, so `Program.cs` needs no changes. Calling `.AdduSyncGit()` on the Umbraco builder is still supported.
2. Set uSync up so git is the source of truth:

```json
"uSync": {
  "Settings": {
    "ImportOnFirstBoot": true,
    "ImportAtStartup": "None",
    "ExportAtStartup": "None",
    "ExportOnSave": "All"
  }
}
```

`ImportAtStartup` must be `None`: if uSync's own startup import is on, it runs every boot and the commit check here stands aside. `ExportOnSave` means changes made in a worktree's backoffice land as files on that worktree's branch.

3. For `Seed: Empty`, or `NameByBranch`, the unattended install needs a user: set `Umbraco:CMS:Unattended:UnattendedUserName`, `UnattendedUserEmail` and `UnattendedUserPassword` (environment variables work).

4. If worktrees live inside the repo (Claude Code puts them in `.claude/worktrees/`), add that folder to `.gitignore`.

### Claude Code hooks

None are needed. The site sets itself up when it starts, and logs the URL it runs on:

```
uSync Git: worktree site will run on https://localhost:45590;http://localhost:45591
```

With SQL Server, a deleted worktree's database stays until the main checkout's site next starts. If you want it gone straight away, a `WorktreeRemove` hook can drop `{database}_wt_{name}` (`name` is the worktree folder, with anything other than letters, digits, `_` and `.` changed to `_`).

## Configuration

All optional. The defaults are shown.

```json
"Git": {
  "Db": {
    "NameByBranch": false,
    "Template": "{db}.{branch}"
  },
  "Worktree": {
    "Enabled": true,
    "Seed": "Copy",
    "ShareMedia": true,
    "Ports": true,
    "PortRangeStart": 45000,
    "RemoveOrphanDatabases": true
  },
  "Import": {
    "OnCommitChange": true,
    "Group": "All",
    "SkipWhenDirty": true
  }
}
```

| Setting | |
|---|---|
| `Db:NameByBranch` | Name the database after the branch. |
| `Db:Template` | Database name. Placeholders: `{db}` (the configured name), `{branch}`, `{env}`. Characters other than letters, digits, `_` and `.` become `_`, so `feature/x` becomes `feature_x`. |
| `Worktree:Enabled` | Set the site up for worktrees at all. |
| `Worktree:Seed` | `Copy` the main checkout's database, start `Empty` (unattended install + first boot import), or `None` (leave it to you). |
| `Worktree:ShareMedia` | Serve media from the main checkout. |
| `Worktree:Ports` | Pick ports per worktree. |
| `Worktree:PortRangeStart` | Start of the range ports are picked from (500 pairs). |
| `Worktree:RemoveOrphanDatabases` | Drop SQL Server databases left by deleted worktrees. |
| `Import:OnCommitChange` | Import at startup when the commit has changed. |
| `Import:Group` | uSync handler group to import. |
| `Import:SkipWhenDirty` | Don't import while the uSync folder has uncommitted changes. |

## Limitations

- SQL Server copies need `BACKUP` and `RESTORE` permissions, so not Azure SQL. LocalDB with `AttachDbFilename` isn't copied (the file is attached); the worktree gets an empty install instead.
- One site per worktree: ports are picked per worktree, not per site.
- The commit check uses HEAD only, so `SkipWhenDirty: false` is the way to import uncommitted uSync files you edited by hand.
- On a fresh database both uSync's first boot import and the commit import run. The second finds nothing to change.

## Changes from the v15 version

- Targets Umbraco 17, uSync 17 and .NET 10.
- The connection string is no longer written to `appsettings.json`; it is set in memory on every boot, so branch naming follows branch switches, not just the first install.
- `DeleteOnBranchChange` and the `{ticks}` placeholder are gone (`{ticks}` gave a new database on every install and is now ignored).
- The last imported commit is kept in the database instead of `uSync/git.meta`.
- The commit-based startup import is switched back on, and only looks at the uSync folders for uncommitted changes.

## Requirements

- .NET 10
- Umbraco CMS 17.3+
- uSync 17.5+
- A git repository

## Project structure

```
uSyncGitRules/
├── uSync.GitEdition/
│   ├── Configuration/     # uSyncGitSettings ("Git" section)
│   ├── Git/               # GitContext: reads .git files, works before Umbraco is up
│   ├── Worktrees/         # configuration source, database copy/drop, port picking
│   ├── Notifications/     # commit-aware import, startup logging, orphan clean up
│   ├── uSyncGitService.cs # LibGit2Sharp: commit, status
│   └── Composer.cs
└── uSync.Site/            # example site
```

## License

This project follows the same licensing as the main uSync project. Please refer to the uSync license for details.
