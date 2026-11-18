# NKeeper SAP ASE plugin

The official `ase` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: SAP (Sybase) ASE, with a query window, object explorer and blocking manager. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9). Its design is §7.35 there.

## What it does

It's a database engine for `db-core` (§7.32), which it depends on: db-core runs the session and draws the tab, and this plugin brings ASE's driver (AdoNetCore.AseClient) and SQL. It needs SDK 1.10 (`>=1.10.0 <2.0.0`) and db-core 1.x.

It contributes the **`ase` service type** ("SAP ASE", port 5000, health by TCP), and its database tab (`ase.query`), ported from 1.x:
- **Signing in.** Core opens the login with AdoNetCore.AseClient and the credential's password (SDK 1.7). The password is sent encrypted when the server supports it; the rest of the connection is as the server is set up (ASE's own SSL isn't used), as in 1.x. A credential with a domain is refused.
- **The password.** AdoNetCore.AseClient keeps the password in its connection string whatever it's told, so this plugin's code could read it. Its manifest says so (`credentialRelease`): administrators see it on NKeeper's plugins page, core hands the connection over only because it's declared, and each login is audited as `credential.reveal` (SDK 1.10, NKeeper's `docs/plugin-security.md` §12.3).
- **Query windows**, each on a connection of its own: batches split at `GO` (with counts), results, row counts, `PRINT` and the server's messages, and errors at their line. The toolbar chooses the window's database.
- **The object explorer:** databases (system ones in their own folder), tables, views, procedures, functions, columns, keys, indexes, triggers, parameters, users, groups, logins and roles.
- **Scripts:** Select Top 1000 Rows, Script as CREATE (from `syscomments`, and tables from `syscolumns`) and Script as EXECUTE.
- **The blocking manager**, under Management, from `master..sysprocesses`, with the sessions' SQL when the login has `mon_role` and monitoring is on; and kill, audited as `ase.kill`.

**Not yet:** Replication Server, 1.x's Management reports (Space, Replication Drift, Backups, Error Log, Resources, Logins, Maintenance, License), Copy Database and the insurance survey.

The plugin's ID is `ase`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper.

## Layout

```text
backend/        the plugin: AsePlugin.cs, the engine (AseEngine.cs), the dialect and GO splitter ported from 1.x (AseDialect.cs, SqlBatches.cs), nkeeper-plugin.json (the manifest), the project
frontend/       its tab: db-core's database tab, told it's SAP ASE
tests/          the engine and db-core's session against a real SAP ASE 16.0 in Docker
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK and db-core's package, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugins.Databases` | GitHub Packages (NuGet) | db-core's assembly: the engine contract and the session |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `AdoNetCore.AseClient` | nuget.org | the driver, which the package carries |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |

The plugin is built for `linux-x64`, where NKeeper runs, as the other engines.

## Building

You need the .NET 10 SDK (see `global.json`), Node.js 24 (see `.nvmrc`), and Docker for the tests.

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck)
dotnet restore NKeeper.Plugins.Ase.slnx
dotnet build NKeeper.Plugins.Ase.slnx --configuration Release
```

Building the plugin builds its frontend, packs `ase-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load it in a development NKeeper, put db-core's package beside it.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.Ase.slnx --configuration Release
```

The tests start SAP ASE 16.0 in Docker (Testcontainers, `datagrip/sybase:16.0`, as 1.x's live tests used), and are skipped when Docker isn't there. A stand-in for core opens the login, as core does. They cover:
- signing in, saying the driver keeps the password;
- a wrong password, without echoing it, and a domain refused;
- batches with results, `PRINT` and an error at its line;
- a window's database, the explorer and scripts;
- the blocking manager's chain, and kill with its audit.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints and type-checks the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v1.1.0`, and push the tag.
3. `.github/workflows/release.yml` runs. It checks that the tag matches the manifest's version and is on `main`, builds and tests, and then, in the protected `release` environment, which needs an owner's approval, packs `ase-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
