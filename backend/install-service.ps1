<#
.SYNOPSIS
    Installs MTCVision as a Windows service so the console keeps running unattended.

.DESCRIPTION
    Without this the application only lives as long as the console window that started it, and
    closing that window stops the line. As a service it starts with the machine, survives logoff,
    and the browser becomes a viewer that can be opened and closed at will.

    The built frontend is served by the same process, so one service covers everything and the
    console is reached at the URL below. Run from an elevated PowerShell prompt.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'MTCVision',
    [string]$Url = 'http://localhost:5080',
    [switch]$SkipFrontend,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# The port the service must actually end up listening on, derived from the URL it is given.
$Port = ([uri]$Url).Port

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Bu betik yönetici olarak çalıştırılmalı.'
}

$projectDir = $PSScriptRoot
$frontendDir = Join-Path (Split-Path $projectDir -Parent) 'frontend'
$publishDir = Join-Path $projectDir 'publish'
$exePath = Join-Path $publishDir 'MTCVision.Backend.exe'

function Wait-ForPortFree {
    param([int]$Port, [int]$TimeoutSeconds = 30)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $holder = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if (-not $holder) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Remove-ExistingService {
    $existed = $false

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        $existed = $true
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
    }

    # Stopping the service is not the same as the process being gone, and a process that is gone is
    # not the same as the port being free. A previous version of this script waited two seconds and
    # hoped: when that was not enough the old build kept serving, every later install bound to
    # nothing and failed silently, and the console went on answering from stale models for hours.
    # Nothing here is allowed to proceed on hope.
    Get-Process -Name 'MTCVision.Backend' -ErrorAction SilentlyContinue | ForEach-Object {
        $existed = $true
        try { $_.CloseMainWindow() | Out-Null } catch { }
        if (-not $_.WaitForExit(5000)) {
            Write-Host "  kalan surec durduruluyor (pid $($_.Id))"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            $_.WaitForExit(10000) | Out-Null
        }
    }

    if (-not (Wait-ForPortFree -Port $Port)) {
        $holder = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $name = if ($holder) { (Get-Process -Id $holder.OwningProcess -ErrorAction SilentlyContinue).ProcessName } else { 'bilinmeyen' }
        throw "$Port portu hala kullanimda ($name). Kurulum durduruldu; eski surum calismaya devam ediyor."
    }

    return $existed
}

if ($Uninstall) {
    if (Remove-ExistingService) { Write-Host "'$ServiceName' servisi kaldırıldı." }
    else { Write-Host "'$ServiceName' servisi zaten yok." }
    return
}

if (-not $SkipFrontend) {
    Write-Host 'Arayüz derleniyor...'
    Push-Location $frontendDir
    try {
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Arayüz derlemesi başarısız.' }
    }
    finally { Pop-Location }
}

Write-Host 'Sunucu yayınlanıyor...'
Remove-ExistingService | Out-Null
& dotnet publish $projectDir -c Release -o $publishDir
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
    throw "Yayınlama başarısız: $exePath bulunamadı."
}

New-Service -Name $ServiceName `
    -BinaryPathName "`"$exePath`" --urls $Url" `
    -DisplayName 'MTCVision Görüntü İşleme Sunucusu' `
    -Description 'Kamera bağlantısını, sürekli denetimi ve operatör konsolunu yürütür.' `
    -StartupType Automatic | Out-Null

# Restart on failure rather than leaving the line blind: 5 s, then 15 s, then every 60 s.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Start-Service -Name $ServiceName

# Started is not the same as serving: if the new process cannot bind it dies here rather than
# leaving the caller to believe a deploy landed when it did not.
$deadline = (Get-Date).AddSeconds(60)
$serving = $false
while ((Get-Date) -lt $deadline) {
    try {
        Invoke-WebRequest -Uri "http://localhost:$Port/api/camera/status" -TimeoutSec 5 -UseBasicParsing | Out-Null
        $serving = $true
        break
    } catch {
        Start-Sleep -Milliseconds 750
    }
}

if (-not $serving) {
    throw "Servis basladi ama http://localhost:$Port yanit vermiyor. Gunluk: $env:ProgramData\MTCVision\mtcvision.log"
}
Write-Host ''
Write-Host "'$ServiceName' servisi kuruldu ve başlatıldı."
Write-Host "Konsol adresi: $Url"
Write-Host 'Tarayıcı kapatılıp açılabilir; denetim arka planda sürer ve makine yeniden başlasa da servis kendi kalkar.'
