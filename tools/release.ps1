# Notch <version> surumunu GitHub Releases'e yayinlar.
#
#   tools\release.ps1 1.4.0            surum yaz, derle, imzala, commit, etiket, push, release
#   tools\release.ps1 1.4.0 -DryRun    yalnizca derle ve imzala (dist\); git ve GitHub'a dokunmaz
#
# Onkosullar: commit edilmis surum notlari (docs\release-notes\<version>.md), temiz git agaci,
# "gh auth login" ve imzalama anahtari (varsayilan %USERPROFILE%\.notch\update-signing-key.pem).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$DryRun,
    [string]$Key
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

function Step($text) { Write-Host "==> $text" }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what basarisiz" } }

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "'$Version' gecerli bir surum degil (or. 1.4.0)" }
$notes = "docs/release-notes/$Version.md"

# Inno Setup derleyicisi (winget install JRSoftware.InnoSetup)
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 kurulu degil: winget install JRSoftware.InnoSetup" }

if (-not $DryRun) {
    foreach ($tool in 'git', 'gh', 'dotnet') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' kurulu degil" }
    }
    gh auth status | Out-Null; Check "GitHub oturumu ('gh auth login')"
    if (git status --porcelain) { throw "Commit edilmemis degisiklikler var" }
    if (git tag --list "v$Version") { throw "v$Version etiketi zaten var" }
    git ls-files --error-unmatch $notes | Out-Null; Check "Surum notlarini yazip commit et: $notes"
}

$csproj = Join-Path (Get-Location) 'Notch.csproj'
$original = [IO.File]::ReadAllText($csproj)
$committed = $false
try {
    Step "Surum $Version"
    $updated = $original -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    if ($updated -ne $original) { [IO.File]::WriteAllText($csproj, $updated) }

    Step "Tek dosyalik surum derleniyor"
    $out = Join-Path ([IO.Path]::GetTempPath()) "notch-publish"
    dotnet publish Notch.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
    Check "Derleme"
    New-Item -ItemType Directory -Force dist | Out-Null
    Copy-Item (Join-Path $out 'Notch.exe') dist\Notch.exe -Force

    Step "Kurulum dosyasi derleniyor"
    & $iscc /Q "/DAppVersion=$Version" tools\setup.iss
    Check "Kurulum dosyasi"

    Step "Imzalaniyor"
    $signArgs = @('dist\Notch.exe')
    if ($Key) { $signArgs += $Key }
    dotnet run --file tools\sign.cs -- @signArgs
    Check "Imzalama"
    # version.txt en son yazilir: yarim kalan yayin guncelleme olarak gorunmesin
    [IO.File]::WriteAllText((Join-Path (Get-Location) 'dist\version.txt'), $Version)

    if ($DryRun) { Step "Deneme: dist\ hazir, git ve GitHub degistirilmedi"; return }

    Step "Commit, etiket ve push"
    if ($updated -ne $original) {
        git add Notch.csproj; git commit -m "chore: release v$Version"; Check "Commit"
    }
    $committed = $true
    git tag -a "v$Version" -m "Notch $Version"; Check "Etiket"
    git push --follow-tags; Check "Push"

    Step "GitHub release olusturuluyor"
    gh release create "v$Version" dist\NotchSetup.exe dist\Notch.exedist\Notch.exe.sig dist\version.txt `
        --title "NotchHub for Windows $Version" --notes-file $notes
    Check "Release"
    Step "Tamam: v$Version"
}
finally {
    if (-not $committed) { [IO.File]::WriteAllText($csproj, $original) }
}
