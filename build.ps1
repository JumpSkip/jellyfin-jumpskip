[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore JumpSkip.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build JumpSkip.slnx -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $metadata = Get-Content -LiteralPath src/JumpSkip/bin/Release/net10.0/meta.json -Raw | ConvertFrom-Json
    $targetAbi = [version]$metadata.targetAbi
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts') -Force | Out-Null
    $packageFiles = @('src/JumpSkip/bin/Release/net10.0/JumpSkip.dll', 'src/JumpSkip/bin/Release/net10.0/meta.json', 'assets/icon.svg') |
        ForEach-Object { Join-Path $PSScriptRoot $_ }
    $zipPath = Join-Path $PSScriptRoot ('artifacts/JumpSkip_' + $metadata.version + '_jellyfin-' + $targetAbi.Major + '.' + $targetAbi.Minor + '.zip')
    Compress-Archive -LiteralPath $packageFiles -DestinationPath $zipPath -Force
    $md5 = (Get-FileHash -LiteralPath $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
    $sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zipPath + '.md5') -Value $md5 -Encoding utf8
    Set-Content -LiteralPath ($zipPath + '.sha256') -Value $sha256 -Encoding utf8

    Write-Output ('Install package: ' + $zipPath)
    Write-Output ('SHA256: ' + $sha256)
} finally { Pop-Location }
