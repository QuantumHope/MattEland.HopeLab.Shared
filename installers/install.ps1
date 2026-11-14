<#
.SYNOPSIS
  Install or update a HopeLab app for this user on Windows (x64), from its GitHub releases.

.DESCRIPTION
  app        %LOCALAPPDATA%\Programs\<Name>   (-InstallDir to change)
  commands   that folder on your user PATH, if the app has a CLI
  menu       Start menu shortcut "<Name>"

  Running it again updates in place. The folder never moves, so anything holding an absolute path into it keeps
  working. After the first install, the app updates itself. (Each release also has a setup.exe that installs to the
  same folder.)

  This script is public; the app's releases may not be. For an app with a private repository it uses, in order:
  <APP>_GITHUB_TOKEN / GH_TOKEN / GITHUB_TOKEN, the token the app saved on this machine, the GitHub CLI's sign-in, and
  finally asks. A token it had to ask for, or took from the environment, is saved for the app's own updates unless
  -NoSaveToken.

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/QuantumHope/MattEland.HopeLab.Shared/main/installers/install.ps1))) -Repo QuantumHope/OneRing

  The copy attached to each release already knows its app: .\install.ps1

.PARAMETER Repo
  The app's repository, owner/name (not needed for a release's own copy).
.PARAMETER Version
  Install that release (0.1.412) instead of the latest.
.PARAMETER NoLaunch
  Don't open the app at the end.
.PARAMETER Uninstall
  Remove the app, the shortcut and the PATH entry. Keeps the app's data folder.
.PARAMETER NoSaveToken
  Don't keep the token (from the environment or the prompt) for the app's own updates.
#>
[CmdletBinding()]
param(
    [string]$Repo,
    [string]$Version,
    [switch]$NoLaunch,
    [switch]$Uninstall,
    [switch]$NoSaveToken,
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Expand-Archive's progress bar is very slow in Windows PowerShell

# Filled in when the script is attached to a release; the generic copy leaves them as they are.
$BakedRepo = '__HOPELAB_REPO__'
$Name = '__HOPELAB_NAME__'
$Id = '__HOPELAB_ID__'
if ($Repo) { $Name = '__'; $Id = '__' } elseif (-not $BakedRepo.StartsWith('__')) { $Repo = $BakedRepo }
if (-not $Repo) { throw 'Which app? Pass -Repo owner/name (QuantumHope/OneRing, QuantumHope/Hymnal).' }
if ($Name.StartsWith('__')) { $Name = ($Repo -split '/')[1] }
if ($Id.StartsWith('__')) { $Id = $Name.ToLowerInvariant() }
$InstallDirGiven = [bool]$InstallDir
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$Name" }
$Version = "$Version".TrimStart('v')
$Rid = 'win-x64'
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$Name.lnk"

function Say($text) { Write-Host $text -ForegroundColor Cyan }

# The app's install settings: from a release's latest.json, or the copy each install keeps in its folder.
function Read-Settings($json) {
    $s = if ($json.install) { $json.install } else { $json }
    $data = $null
    if ($s.dataDirectoryVariable -and [Environment]::GetEnvironmentVariable($s.dataDirectoryVariable)) { $data = [Environment]::GetEnvironmentVariable($s.dataDirectoryVariable) }
    elseif ($s.dataDirectory) { $data = Join-Path $HOME $s.dataDirectory }
    [pscustomobject]@{
        Id = $s.id
        Name = $s.name
        TokenVariable = $s.tokenVariable
        Exe = $(if ($s.executable) { $s.executable } else { $Name })
        AddToPath = [bool]$s.addToPath
        Processes = @($s.processes | Where-Object { $_ })
        Stop = @($s.stop | Where-Object { $_ })
        BeforeUninstall = @($s.beforeUninstall | Where-Object { $_ })
        DataDir = $data
        FirstRun = $s.firstRun
        UninstallNote = $s.uninstallNote
        Description = $s.description
    }
}

function Get-Settings {
    $f = Join-Path $InstallDir 'hopelab-install.json'
    if (Test-Path $f) { Read-Settings (Get-Content $f -Raw | ConvertFrom-Json) } else { $null }
}

function Get-InstallProcesses($names) {
    # Processes running from the install folder (short-lived CLI runs from hooks are left alone unless named).
    if (-not $names) { return @() }
    Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path)).TrimEnd('\') -ieq $InstallDir.TrimEnd('\') } catch { $false }
    }
}

function Invoke-InApp($argv) {
    if (-not $argv -or $argv.Count -eq 0) { return }
    $exe = Join-Path $InstallDir ($argv[0] + '.exe')
    if (Test-Path $exe) { & $exe @($argv | Select-Object -Skip 1) *> $null }
}

function Stop-App($settings) {
    if (-not $settings) { return [pscustomobject]@{ Running = @(); App = $false } }
    $names = @($settings.Processes + $settings.Exe | Select-Object -Unique)
    $running = @(Get-InstallProcesses $names)
    $state = [pscustomobject]@{
        Running = @($running | Where-Object Name -ne $settings.Exe | ForEach-Object Name | Select-Object -Unique)
        App = [bool]($running | Where-Object Name -eq $settings.Exe)
    }
    if ($state.Running.Count -gt 0) { Invoke-InApp $settings.Stop }   # a clean stop first
    $deadline = (Get-Date).AddSeconds(10)
    while (@(Get-InstallProcesses $settings.Processes).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Get-InstallProcesses $names | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $state
}

function Set-UserPath([switch]$Remove) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($current -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ine $InstallDir.TrimEnd('\')) })
    if (-not $Remove) { $parts += $InstallDir }
    $new = $parts -join ';'
    if ($new -ne $current) { [Environment]::SetEnvironmentVariable('Path', $new, 'User') }
}

if ($Uninstall) {
    $settings = Get-Settings
    Say "Removing $Name from $InstallDir"
    if ($settings) { Invoke-InApp $settings.BeforeUninstall }
    Stop-App $settings | Out-Null
    Remove-Item $Shortcut -ErrorAction SilentlyContinue
    Set-UserPath -Remove
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed.$(if ($settings.DataDir) { " Your data in $($settings.DataDir) is untouched." })"
    if ($settings.UninstallNote) { Write-Host $settings.UninstallNote }
    return
}

if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
    throw "There is no $Name build for this processor yet (only x64)."
}

# ---- a token, for a private repository ----
$previous = Get-Settings
$appVar = ($Id.ToUpperInvariant() -replace '-', '_') + '_GITHUB_TOKEN'
# The app's configured variable first, when a previous install named one; then the derived name and the usual ones.
$token = @($previous.TokenVariable, $appVar, 'GH_TOKEN', 'GITHUB_TOKEN') | Where-Object { $_ } |
    ForEach-Object { [Environment]::GetEnvironmentVariable($_) } | Where-Object { $_ } | Select-Object -First 1
$tokenFrom = 'environment'
foreach ($f in @($(if ($previous.DataDir) { Join-Path $previous.DataDir 'github-token' }), (Join-Path $HOME ".$Id\github-token"))) {
    if (-not $token -and $f -and (Test-Path $f)) { $token = (Get-Content $f -Raw).Trim(); $tokenFrom = 'saved' }
}
if (-not $token -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $tokenFrom = 'gh'
    $token = (& gh auth token 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $token) {
        # gh before 2.17 has no `auth token`; its status line shows the token instead.
        $line = (& gh auth status --show-token 2>&1 | Out-String) -split "`n" | Where-Object { $_ -match 'Token: (\S+)\s*$' -and $Matches[1] -notmatch '^\*+$' } | Select-Object -First 1
        $token = if ($line -and $line -match 'Token: (\S+)\s*$') { $Matches[1] } else { $null }
    }
}

Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false   # asset downloads redirect to storage, which must not get the token
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromMinutes(15)

function Invoke-GitHub([string]$Url, [string]$Accept, [string]$OutFile) {
    $auth = $true
    for ($hop = 0; $hop -lt 5; $hop++) {
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $Url)
        $request.Headers.UserAgent.ParseAdd('HopeLab-installer')
        $request.Headers.Accept.ParseAdd($Accept)
        $request.Headers.Add('X-GitHub-Api-Version', '2022-11-28')
        if ($auth -and $token) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token) }
        $response = $http.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $code = [int]$response.StatusCode
        if ($code -ge 300 -and $code -lt 400 -and $response.Headers.Location) {
            $Url = $response.Headers.Location.ToString(); $auth = $false; $response.Dispose(); continue
        }
        if (-not $response.IsSuccessStatusCode) { $response.Dispose(); return [pscustomobject]@{ Failed = $code } }
        if ($OutFile) {
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $file = [IO.File]::Create($OutFile)
            try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose(); $response.Dispose() }
            return [pscustomobject]@{ Failed = 0 }
        }
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $response.Dispose()
        return [pscustomobject]@{ Failed = 0; Text = $text }
    }
    throw "Too many redirects for $Url."
}

function Get-OrThrow($Url, $Accept, $OutFile) {
    $r = Invoke-GitHub $Url $Accept $OutFile
    if ($r.Failed) { throw "GitHub answered $($r.Failed) for $Url." }
    $r
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("hopelab-install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
try {
    $releaseUrl = if ($Version) { "https://api.github.com/repos/$Repo/releases/tags/v$Version" } else { "https://api.github.com/repos/$Repo/releases/latest" }
    Say ("Finding the {0} {1} release" -f $(if ($Version) { "v$Version" } else { 'latest' }), $Name)
    $r = Invoke-GitHub $releaseUrl 'application/vnd.github+json'
    if ($r.Failed -and -not $token -and [Environment]::UserInteractive) {
        Write-Host "$Repo's releases aren't public. Paste a GitHub token that can read them (a fine-grained token with"
        Write-Host "read-only Contents access to $Repo is enough). It isn't shown as you type."
        $token = ([Net.NetworkCredential]::new('', (Read-Host 'GitHub token' -AsSecureString)).Password).Trim()
        $tokenFrom = 'prompt'
        $r = Invoke-GitHub $releaseUrl 'application/vnd.github+json'
    }
    if ($r.Failed) {
        if (-not $token) { throw "Couldn't read $Repo's releases. If the repository is private, run 'gh auth login' or set GH_TOKEN, and try again." }
        throw "Couldn't read $releaseUrl with the token found ($tokenFrom): GitHub answered $($r.Failed). Check it can read $Repo, and that the release exists."
    }
    $release = $r.Text | ConvertFrom-Json
    $manifestAsset = $release.assets | Where-Object name -eq 'latest.json' | Select-Object -First 1
    if (-not $manifestAsset) { throw "Release $($release.tag_name) has no latest.json." }
    $manifest = (Get-OrThrow $manifestAsset.url 'application/octet-stream').Text | ConvertFrom-Json
    $settings = Read-Settings $manifest
    # The release says who the app is. A script fetched by repository name guessed from the repository; if the release
    # disagrees, use its name, so this updates the same install setup.exe or the release's own script made.
    $canonical = if ($settings.Name) { $settings.Name } elseif ($manifest.app) { $manifest.app } else { $Name }
    if ($canonical -ne $Name) {
        $Name = $canonical
        if (-not $InstallDirGiven) { $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$Name" }
        $Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$Name.lnk"
        $previous = Get-Settings
    }
    $entry = $manifest.assets.$Rid
    if (-not $entry) { throw "Release $($release.tag_name) has no $Rid build." }
    $archiveAsset = $release.assets | Where-Object name -eq $entry.name | Select-Object -First 1
    if (-not $archiveAsset) { throw "Release $($release.tag_name) lists $($entry.name) but doesn't have it." }

    Say ("Downloading {0} {1} ({2}, {3:N0} MB)" -f $Name, $manifest.version, $entry.name, ($entry.size / 1MB))
    $zip = Join-Path $work $entry.name
    Get-OrThrow $archiveAsset.url 'application/octet-stream' $zip | Out-Null
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($sha -ine $entry.sha256) { throw "$($entry.name) didn't match its checksum; nothing was installed." }

    $staged = Join-Path $work 'new'
    Expand-Archive $zip -DestinationPath $staged
    if (-not (Test-Path (Join-Path $staged "$($settings.Exe).exe"))) { throw "$($entry.name) is missing $($settings.Exe).exe." }

    # ---- replace the files in place ----
    # The folder itself never goes away: hooks, a login entry and shortcuts hold paths into it, and a setup.exe install
    # keeps its uninstaller (unins*.exe, not in the zip) there. Each file being replaced is renamed into .update-old\
    # first (Windows allows renaming a file in use, not overwriting it), then the new one moved in; a failure part way
    # puts every old file back. The same scheme as the apps' own updater.
    $installedBefore = Test-Path (Join-Path $InstallDir 'release.json')
    $was = Stop-App $(if ($previous) { $previous } else { $settings })
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    $aside = Join-Path $InstallDir (".update-old\install-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $moved = New-Object System.Collections.Generic.List[string]
    # What was stopped comes back if anything below fails, from whichever copy is in place by then.
    function Start-Previous {
        foreach ($p in $was.Running) {
            $exe = Join-Path $InstallDir "$p.exe"
            if (Test-Path $exe) { Start-Process $exe -WorkingDirectory $InstallDir -WindowStyle Hidden }
        }
    }
    function Move-WithRetry($from, $to) {
        for ($try = 1; ; $try++) {
            # A short-lived CLI run (a Claude Code hook, say) can hold a file for a moment; try again shortly.
            try { Move-Item -LiteralPath $from -Destination $to -Force; return }
            catch { if ($try -ge 10) { throw }; Start-Sleep -Milliseconds 500 }
        }
    }
    try {
        $root = (Resolve-Path $staged).Path.TrimEnd('\')
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
            $rel = $file.FullName.Substring($root.Length + 1)
            $target = Join-Path $InstallDir $rel
            New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
            if (Test-Path -LiteralPath $target) {
                $old = Join-Path $aside $rel
                New-Item -ItemType Directory -Force (Split-Path $old -Parent) | Out-Null
                Move-WithRetry $target $old
            }
            $moved.Add($rel)
            Move-WithRetry $file.FullName $target
        }
    } catch {
        # Roll back: every new file out, every old one back where everything expects it.
        $why = $_.Exception.Message
        for ($i = $moved.Count - 1; $i -ge 0; $i--) {
            $target = Join-Path $InstallDir $moved[$i]
            $old = Join-Path $aside $moved[$i]
            if (Test-Path -LiteralPath $old) {
                if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target "$target.failed-$i" -Force -ErrorAction SilentlyContinue }
                Move-Item -LiteralPath $old $target -Force -ErrorAction SilentlyContinue
            } else {
                Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
            }
        }
        Start-Previous
        throw "Couldn't put the new $Name in place ($why). The previous version is back as it was."
    }
    Remove-Item (Join-Path $InstallDir '.update-old') -Recurse -Force -ErrorAction SilentlyContinue   # in use? the app cleans up next start

    # A setup.exe install: Apps & features shows the version now in the folder.
    Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | ForEach-Object {
        $entry = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($entry.InstallLocation -and $entry.InstallLocation.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) {
            Set-ItemProperty $_.PSPath -Name DisplayVersion -Value $manifest.version
        }
    }

    # ---- shortcut and PATH ----
    $exePath = Join-Path $InstallDir "$($settings.Exe).exe"
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Shortcut)
    $lnk.TargetPath = $exePath
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = "$exePath,0"
    if ($settings.Description) { $lnk.Description = $settings.Description }
    $lnk.Save()
    if ($settings.AddToPath) { Set-UserPath }

    if ($installedBefore) { Say "Updated $Name to $($manifest.version) in $InstallDir" } else { Say "Installed $Name $($manifest.version) in $InstallDir" }
    if ($settings.AddToPath) { Write-Host "Open a new terminal to use $Name's commands (the folder is now on your PATH)." }
    if ($token -and -not $NoSaveToken -and $settings.DataDir -and $tokenFrom -in 'prompt', 'environment') {
        New-Item -ItemType Directory -Force $settings.DataDir | Out-Null
        # The profile folder is already private to this user; the file inherits that.
        [IO.File]::WriteAllText((Join-Path $settings.DataDir 'github-token'), $token + "`n")
        Write-Host "Saved the GitHub token to $(Join-Path $settings.DataDir 'github-token') so $Name can update itself."
    }

    if (-not $NoLaunch -or $was.App) {
        Write-Host "Opening $Name.$(if ($settings.FirstRun) { ' ' + $settings.FirstRun })"
        Start-Process $exePath -WorkingDirectory $InstallDir
    } else {
        foreach ($p in $was.Running) { Start-Process (Join-Path $InstallDir "$p.exe") -WorkingDirectory $InstallDir -WindowStyle Hidden }
        Write-Host "Open $Name from the Start menu.$(if ($settings.FirstRun) { ' ' + $settings.FirstRun })"
    }
}
finally {
    $http.Dispose()
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
