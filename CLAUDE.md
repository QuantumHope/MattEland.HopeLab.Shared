# CLAUDE.md

Shared libraries and release tooling for the HopeLab apps (OneRing, Hymnal). `README.md` covers what is here and how an
app adopts it. The packages are published to nuget.org on every push to `main`, so a change here reaches the apps only
when they move their `PackageReference` versions forward.

## Commands

```bash
dotnet build MattEland.HopeLab.Shared.slnx
dotnet test MattEland.HopeLab.Shared.slnx
dotnet pack MattEland.HopeLab.Shared.slnx -c Release -o artifacts
bash -n installers/install.sh && bash -n installers/linux/build-deb.sh
python3 installers/release/hopelab_release.py   # usage
dotnet run --project tools/ThemeTokens -- generate   # after editing tokens/
```

## Rules

- **App-neutral.** Nothing here names OneRing or Hymnal in code. An app's specifics come in through
  `AppUpdateOptions` (including the `StopServices` hook), `UpdatesViewModel`'s text properties, or `hopelab-app.json`.
- **Compatible changes.** Every app pins a version, but a breaking change still means a major or minor bump in
  `Directory.Build.props` and a note in the PR. `latest.json` and `release.json` are read by installed copies of every
  app, old and new: add fields, never rename or remove them.
- **AOT.** `Common`, `Git` and `Updates` stay `IsAotCompatible` (source-generated JSON, no reflection): OneRing's
  `ring` CLI is NativeAOT.
- **ReactiveUI version** in `Directory.Packages.props` matches the apps', so one copy loads.
- **The installers are public and run from a pipe.** They must work as `irm … | iex` / `curl … | bash` with no other
  files, on Windows PowerShell 5.1 and PowerShell 7, and never print a token. Placeholders `__HOPELAB_REPO__`,
  `__HOPELAB_NAME__`, `__HOPELAB_ID__` are filled in for each release's attached copy.
- **The install folder never moves**, on any path (script, setup, in-app update): hooks and login entries hold absolute
  paths into it.
- **Themes come from `tokens/`.** Edit `tokens/contract.json` or `tokens/themes/*.json` and run the generator; never
  edit `tokens/css/` or `src/MattEland.HopeLab.Themes/Themes/` by hand (a test fails when they drift). Renaming or
  removing a token, or a theme id, is a breaking change for every app that uses it. See `tokens/README.md`.
- Tests that touch files use temp folders; nothing writes to the real profile, PATH or registry.
