# MattEland.HopeLab.Shared

Shared .NET libraries and release tooling for the HopeLab desktop apps ([OneRing](https://github.com/QuantumHope/OneRing),
[Hymnal](https://github.com/QuantumHope/Hymnal)), so every app installs, releases and updates the same way.

| Package (nuget.org) | What it is |
|---|---|
| `MattEland.HopeLab.Common` | `Result<T>` and `Unit`, a clock abstraction (`IClock`), atomic file writes |
| `MattEland.HopeLab.Git` | The git CLI wrapper: porcelain v2 status, worktrees, merge preview, commit graph layout, fetch/pull/rebase, per-repo write lock |
| `MattEland.HopeLab.Updates` | Updates from GitHub releases: `latest.json`, version checks, GitHub tokens for private repos, verified downloads, in-place file swap with rollback, Debian-package installs (`AppUpdater`, `AppUpdateService`) |
| `MattEland.HopeLab.Updates.ReactiveUI` | `UpdatesViewModel`: the check timer, banner, tray text, Settings status and token handling every app shows the same way |
| `MattEland.HopeLab.Themes` | The shared themes for Avalonia: one resource dictionary per theme (`Lab.*` keys), `ThemeCatalog`, and the faces they use. Generated from [`tokens/`](tokens/README.md) |
| `MattEland.HopeLab.Themes.Tokens` | The theme token model: the contract, theme files, contrast and ΔE maths, and the CSS and Avalonia generators |

All target .NET 10. `Common`, `Git`, `Updates` and both theme packages are NativeAOT-compatible. Versions are `0.1.<commits on main>`:
every merge to `main` publishes.

| Release tooling | What it is |
|---|---|
| `.github/workflows/app-release.yml` | A reusable workflow: version, self-contained Windows and Linux builds, zip, tar.gz, per-user `setup.exe`, `.deb`, `latest.json`, draft → check → publish, then an install-and-update smoke test on fresh runners |
| `installers/install.ps1`, `installers/install.sh` | Install or update any HopeLab app for the current user, from its releases (private repositories too) |
| `installers/windows/app.iss` | The Inno Setup template for the per-user `setup.exe` |
| `installers/linux/build-deb.sh` | Builds the `.deb` (`/opt/<id>`, `/usr/bin` links, menu entry) |
| `installers/hopelab-app.schema.json` | What an app's `hopelab-app.json` holds |

## Installing an app

From each app's latest release: the `setup.exe` (Windows, no admin) or `.deb` (Debian, Ubuntu, Pop!_OS), or one command:

```powershell
# Windows: %LOCALAPPDATA%\Programs\<App>, Start menu, PATH if the app has commands
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/QuantumHope/MattEland.HopeLab.Shared/main/installers/install.ps1))) -Repo QuantumHope/OneRing
```

```bash
# Linux, no sudo: ~/.local/share/<id>/app, commands in ~/.local/bin, an app-menu entry
curl -fsSL https://raw.githubusercontent.com/QuantumHope/MattEland.HopeLab.Shared/main/installers/install.sh | bash -s -- --repo QuantumHope/OneRing
```

The scripts are public; an app's releases may not be. For a private repository they use, in order, `<APP>_GITHUB_TOKEN`
/ `GH_TOKEN` / `GITHUB_TOKEN`, the token the app saved on this machine, the GitHub CLI's sign-in, and then ask (hidden
input). A token that was asked for, or came from the environment, is saved in the app's data folder (`github-token`,
user-only) so the app can update itself. A fine-grained token with read-only *Contents* access to the one repository is
enough. Running a script again updates in place; `-Uninstall` / `--uninstall` removes the app and keeps its data.

| How it was installed | Where | How it updates |
|---|---|---|
| `setup.exe`, `install.ps1`, zip | `%LOCALAPPDATA%\Programs\<App>` | in place, silently (files moved aside and replaced; Apps & features follows) |
| `install.sh`, tar.gz | `~/.local/share/<id>/app` | in place, silently |
| `.deb` | `/opt/<id>` | the app downloads the next `.deb` and installs it with apt-get: `pkexec` (a password dialog) from the app, `sudo` from a terminal |

The install folder never moves, so anything that holds a path into it (Claude Code hooks, a login entry, shortcuts)
keeps working across updates. Only a copy built by the release workflow (it has `release.json`) updates itself.

## Adopting it in an app

1. **Describe the app** in `hopelab-app.json` at its root ([schema](installers/hopelab-app.schema.json), and OneRing's
   and Hymnal's as examples): name, id, the desktop project and executable, icons, a Windows setup `AppId` (a new GUID,
   never changed), and the install settings (commands on PATH, the data folder, processes to stop, an update command).
2. **Release from CI**: after the app's own tests, call the shared workflow (see the top of
   [`app-release.yml`](.github/workflows/app-release.yml)). The version is `<major>.<minor>` from the app's
   `Directory.Build.props` plus the commit count.
3. **Update from the app**: reference `MattEland.HopeLab.Updates.ReactiveUI`, create an `AppUpdater` with the app's
   `AppUpdateOptions` (repository, data folder, executable, token variable, setup AppId, and a `StopServices` hook if it
   runs anything beside itself), wrap it in `AppUpdateService`, and give that to an `UpdatesViewModel`. Bind a banner and
   a Settings card to it in the app's own theme, and on `RestartRequested` start the new executable and exit.

## Building

```bash
dotnet build MattEland.HopeLab.Shared.slnx
dotnet test MattEland.HopeLab.Shared.slnx
dotnet pack MattEland.HopeLab.Shared.slnx -c Release -o artifacts
```

To try a change in an app before it's published, pack with a higher version into a folder and add that folder as a
package source in the app (a `nuget.config` you don't commit), or temporarily swap the `PackageReference` for a
`ProjectReference` into a checkout of this repository.

## Publishing

Every push to `main` that passes CI packs `0.1.<commits on main>` and pushes it to nuget.org with
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): no API key is stored anywhere.
The publish job gets a GitHub OIDC token, `NuGet/login` trades it for an API key that lasts an hour, and nuget.org
accepts it because of a Trusted Publishing policy on the owning account (nuget.org → your profile → **Trusted
Publishing**):

| Field | Value |
|---|---|
| Repository Owner | `QuantumHope` |
| Repository | `MattEland.HopeLab.Shared` |
| Workflow File | `ci.yml` |
| Environment | (empty) |

The job logs in as the nuget.org profile `MattEland`; a repository variable `NUGET_USER` overrides it. To publish
again by hand (after setting the policy up, say), run the CI workflow on `main` from the Actions tab; versions that
already exist are skipped.

## Licence

MIT
