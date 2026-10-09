<#
Installs ScreenX from the latest GitHub release, or updates an installed copy.
Run it again to update; it changes nothing when the installed copy is current.

    irm https://github.com/BrandonDoster/ScreenX/releases/latest/download/installer.ps1 | iex
#>

# A scope of its own: run through iex, these settings would otherwise stay in
# the caller's session.
& {
    $ErrorActionPreference = 'Stop'
    # Windows PowerShell's progress bar makes the download many times slower.
    $ProgressPreference = 'SilentlyContinue'

    $dir = "$env:LOCALAPPDATA\Programs\ScreenX"
    $exe = "$dir\ScreenX.exe"

    $release = Invoke-RestMethod 'https://api.github.com/repos/BrandonDoster/ScreenX/releases/latest'
    $zip = $release.assets | Where-Object { $_.name -like 'ScreenX-*-win-x64.zip' }
    if ($null -eq $zip) {
        Write-Warning "Release $($release.tag_name) has no ScreenX-<version>-win-x64.zip."
        return
    }
    $latest = [version]$release.tag_name.TrimStart('v')

    $installed = $null
    if (Test-Path $exe) {
        $installed = [version](Get-Item $exe).VersionInfo.FileVersion
        # The exe says 2.0.1.0, which sorts above 2.0.1: only a newer release passes.
        if ($latest -le $installed) {
            Write-Host "ScreenX $installed is current." -ForegroundColor Green
            return
        }
    }

    # Other users' copies have their own install; only this session's matter.
    $session = (Get-Process -Id $PID).SessionId
    $all = @(Get-Process ScreenX -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
    # A copy started elevated, or seen from 32-bit PowerShell, has no readable
    # Path. Left running it keeps the files locked, and Expand-Archive's
    # rollback then deletes the ones it already replaced.
    if (@($all | Where-Object { $null -eq $_.Path }).Count -gt 0) {
        Write-Warning 'Quit ScreenX from the tray and run this again.'
        return
    }
    # Sorted now: a stopped process's Path reads empty, as if from elsewhere.
    $running = @($all | Where-Object { $_.Path -eq $exe })
    $other = @($all | Where-Object { $_.Path -ne $exe })
    # A visible window is an editor, the settings or a capture in progress.
    # Stopping the process then would drop unsaved annotations without asking.
    if (@($running | Where-Object { $_.MainWindowHandle -ne 0 }).Count -gt 0) {
        Write-Warning 'Close the ScreenX windows and run this again.'
        return
    }

    $file = Join-Path $env:TEMP $zip.name
    Write-Host "Downloading $($zip.name)..." -ForegroundColor Yellow
    Invoke-WebRequest $zip.browser_download_url -OutFile $file -UseBasicParsing

    # Only once the download is in hand, so a failed one leaves ScreenX running.
    $running | Stop-Process -Force
    $running | Wait-Process
    New-Item -ItemType Directory $dir -Force | Out-Null
    Expand-Archive $file $dir -Force
    Remove-Item $file

    if ($null -eq $installed) {
        # The same entry ScreenX's own "Start with Windows" writes.
        Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' ScreenX "`"$exe`""
    }
    Write-Host "ScreenX $latest is installed in $dir." -ForegroundColor Green

    # A copy started from elsewhere (a build folder) holds the single-instance
    # mutex, and starting this one would only open that copy's settings.
    if ($other.Count -gt 0) {
        Write-Warning "Quit the ScreenX running from $($other[0].Path), then start $exe."
    }elseif ($null -eq $installed -or $running.Count -gt 0) {
        # Through explorer, as from the Start menu: Start-Process would pass on
        # an admin terminal's elevation, and the terminal's folder as working
        # directory for as long as ScreenX runs. Quoted, see AGENTS.md rule 13.
        Start-Process "$env:WINDIR\explorer.exe" "`"$exe`""
    }
}
