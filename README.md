# uSync Git Edition

🚨 **EXPERIMENTAL CODE - USE WITH CAUTION** 🚨

This project is experimental and should not be used in production environments without thorough testing.

Makes an Umbraco development site aware of the git checkout it runs from. Each git worktree (for example a parallel Claude Code session) gets its own database, ports and shared media, and uSync imports when the commit changes.

## Setup

Reference the `uSync.GitEdition` project (or package) from the site. That's it for worktrees: the composer wires everything up and `Program.cs` needs no changes.

For the main checkout, one line turns on the same uSync defaults, so a fresh clone imports on its first boot:

```json
"Git": {
  "uSyncDefaults": true
}
```

If worktrees live inside the repo (Claude Code puts them in `.claude/worktrees/`), add that folder to `.gitignore`.

A fresh install (`Seed: Empty`, `NameByBranch`, or nothing to copy) needs an unattended user: `Umbraco:CMS:Unattended:UnattendedUserName`, `UnattendedUserEmail` and `UnattendedUserPassword` (environment variables work). Without one the site shows Umbraco's installer instead of failing to boot.

Calling `.AdduSyncGit()` on the Umbraco builder is still supported, but not needed.

## Requirements

- .NET 10
- Umbraco CMS 17.3+
- uSync 17.5+
- A git repository

## How it works

Everything happens inside the site at startup. Nothing is written to `appsettings.json`: the values go into an in-memory configuration source, so they work however the site is started (Visual Studio, Rider, `dotnet run`, an AI agent).

### Development sites only

None of this runs unless the site is a development site:

- Umbraco's runtime mode (`Umbraco:CMS:Runtime:Mode`) is not `Production`, **and**
- `Umbraco:CMS:Hosting:Debug` is `true`, or the ASP.NET environment is `Development`.

A site deployed from a git clone, or run locally as Production, does nothing: no database changes, no imports, no clean up. It logs `uSync Git: off` with the reason. `Git:Enabled` overrides this either way.

### In a linked worktree

1. **Local settings**: settings files the site loads but git ignores (for example `appsettings.Local.json` holding the connection string) don't exist in a new worktree. They are read from the main checkout instead, in the same place in the configuration order.
2. **uSync defaults**: `uSync:Settings:ImportOnFirstBoot` is turned on, unless the site sets it.
3. **Database**
   - **SQLite**: the database file lives in the worktree's own `umbraco/Data` folder, so it is already separate. If it doesn't exist, it is copied from the main checkout's database with SQLite's backup API, which is safe while the main site is running.
   - **SQL Server**: the worktree uses `{database}_wt_{id}_{worktree}` on the same server. `{id}` is a short id for the clone, kept in `.git/usync-git-id`, so two clones (or two sites) sharing a server never mix up each other's worktree databases. If it doesn't exist, the main database is copied with `BACKUP ... WITH COPY_ONLY` and `RESTORE ... WITH MOVE` into the server's default data folders.
   - If there is nothing to copy (or `Seed` is `Empty`), `InstallUnattended` is switched on so Umbraco installs a fresh database and uSync's `ImportOnFirstBoot` fills it. This only happens when an unattended user is configured; without one, Umbraco shows its installer.
4. **Media**: `Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath` points at the main checkout's `wwwroot/media`, which is gitignored and so missing from the worktree. Skipped if you've set that path yourself.
5. **Ports**: a stable https/http pair in the 45000 range, picked from a hash of the worktree name. The same worktree gets the same ports on every run unless something else holds them. This overrides `launchSettings.json`. If no pair is free, the site keeps its own urls.

If any of this fails, it's logged as an error and the site boots without the git setup. The setup is logged as it happens, so the reason is in the log even when Umbraco then fails to boot.

### In every checkout

6. **Import on commit change**: when Umbraco starts, the HEAD commit is compared with the commit that was last imported into *this database* (stored in Umbraco's key-value table, so a copied database brings it along). If they differ, uSync imports. A plain restart doesn't import. Only a full import (group `All`, or the configured `Import:Group`) with no errors records the commit; importing one group from the dashboard doesn't. While the uSync folder has uncommitted changes the import is skipped, because those are normally exports from this site.
7. **Branch databases** (off by default): with `NameByBranch`, the database name comes from `Template`, so switching branch switches database. A branch with no database yet gets a fresh unattended install. The default branch keeps the configured name, so turning this on doesn't move your existing database.

### In the main checkout

8. **Tidy up**: SQL Server databases carrying this clone's `_wt_{id}_` marker, whose worktree git no longer knows about, are dropped in the background after startup. A worktree counts until git removes it from `.git/worktrees` (`git worktree remove` or `git worktree prune`), so a moved worktree, or one on a drive that isn't connected, keeps its database. SQLite worktree databases go when the worktree folder is deleted.

### The default branch

The default branch is, in order: `Git:DefaultBranch` if set, the branch `origin/HEAD` points at (set by `git clone`), then `main` or `master` if there's a local branch with that name. It decides two things:

- With `NameByBranch`, it keeps the configured database name. If it can't be worked out, every branch is named and a warning is logged.
- A worktree copies the default branch's database, whichever branch the main checkout is on. If that database doesn't exist, it copies the main checkout's current one.

Only the parts that are added (branch, environment, worktree) have characters other than letters, digits, `_` and `.` changed to `_`. The configured name is never touched.

### uSync settings the defaults rely on

Anything the site sets itself always wins. These are the uSync settings that matter, with uSync's own defaults, which already suit a git workflow apart from `ImportOnFirstBoot`:

| uSync setting | uSync default | Here |
|---|---|---|
| `ImportOnFirstBoot` | `false` | `true` when unset (worktrees, or `uSyncDefaults`) |
| `ImportAtStartup` | `None` | Leave it `None`. If it's on, uSync imports every boot and the commit check stands aside. |
| `ExportAtStartup` | `None` | Leave it `None`, or a copied database can overwrite the branch's files. |
| `ExportOnSave` | `All` | Backoffice changes in a worktree land as files on its branch. |

### Claude Code hooks

None are needed. The site sets itself up when it starts, and logs the URL it runs on:

```
uSync Git: worktree site will run on https://localhost:45590;http://localhost:45591
```

With SQL Server, a deleted worktree's database stays until the main checkout's site next starts (and until `git worktree prune` if you deleted the folder by hand). If you want it gone straight away, a `WorktreeRemove` hook can drop `{database}_wt_{id}_{name}` (`id` is in `.git/usync-git-id`, `name` is the worktree folder, with anything other than letters, digits, `_` and `.` changed to `_`).

## Configuration

All optional. The defaults are shown.

```json
"Git": {
  "Enabled": null,
  "uSyncDefaults": null,
  "DefaultBranch": null,
  "Db": {
    "NameByBranch": false,
    "Template": "{db}.{branch}"
  },
  "Worktree": {
    "Enabled": true,
    "Seed": "Copy",
    "ShareMedia": true,
    "ShareLocalSettings": true,
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
| `Enabled` | Turn everything on or off. Unset means on for development sites only (see above). |
| `uSyncDefaults` | Fill in unset uSync settings (`ImportOnFirstBoot`). Unset means on in a worktree, off in the main checkout. |
| `DefaultBranch` | The default branch. Unset means work it out (see above). |
| `Db:NameByBranch` | Name the database after the branch, except on the default branch. |
| `Db:Template` | Database name. Placeholders: `{db}` (the configured name), `{branch}`, `{env}`. In the branch and environment, characters other than letters, digits, `_` and `.` become `_`, so `feature/x` becomes `feature_x`. |
| `Worktree:Enabled` | Set the site up for worktrees at all. |
| `Worktree:Seed` | `Copy` the main checkout's database, start `Empty` (unattended install + first boot import), or `None` (leave it to you). |
| `Worktree:ShareMedia` | Serve media from the main checkout. |
| `Worktree:ShareLocalSettings` | Read gitignored settings files the site loads from the main checkout. |
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
- On a fresh database both uSync's first boot import and the commit import are requested. uSync skips the second as a duplicate.

## Changes from the v15 version

- Targets Umbraco 17, uSync 17 and .NET 10.
- The connection string is no longer written to `appsettings.json`; it is set in memory on every boot, so branch naming follows branch switches, not just the first install.
- `DeleteOnBranchChange` and the `{ticks}` placeholder are gone (`{ticks}` gave a new database on every install and is now ignored).
- The last imported commit is kept in the database instead of `uSync/git.meta`.
- The commit-based startup import is switched back on, and only looks at the uSync folders for uncommitted changes.

## Project structure

```
uSyncGitRules/
├── uSync.GitEdition/
│   ├── Configuration/     # uSyncGitSettings ("Git" section)
│   ├── Git/               # GitContext: reads .git files, works before Umbraco is up
│   ├── Worktrees/         # configuration source, database copy/drop, port picking
│   ├── Notifications/     # commit-aware import, orphan clean up
│   ├── uSyncGitService.cs # LibGit2Sharp: commit, status
│   └── Composer.cs        # applies the git setup and logs what it did
└── uSync.Site/            # example site
```

## License

This project follows the same licensing as the main uSync project. Please refer to the uSync license for details.
