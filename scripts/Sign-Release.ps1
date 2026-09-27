param(
    [ValidateSet("Inspect", "Preflight", "Sign")]
    [string]$Mode = "Inspect"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
trap {
    Write-Host "[ERROR] $($_.Exception.Message)"
    exit 1
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$exePath = Join-Path $repoRoot "dist\turtle-ai-quartet-hub\TurtleAIQuartetHub.exe"

if ($Mode -eq "Inspect") {
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Release executable was not found: $exePath"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $exePath
    if ($signature.Status -eq "Valid" -and $signature.SignerCertificate -and
        $signature.SignerCertificate.Subject -ne $signature.SignerCertificate.Issuer) {
        Write-Host "[SIGNATURE] Authenticode is valid: $($signature.SignerCertificate.Subject)"
    } else {
        Write-Warning "Release EXE has no valid CA-issued signature (status: $($signature.Status)). Smart App Control can block it. For signed releases, configure a trusted code-signing certificate and run publish.bat --sign."
    }
    return
}

$thumbprint = $env:TURTLE_CODE_SIGN_THUMBPRINT
if ([string]::IsNullOrWhiteSpace($thumbprint)) {
    throw "TURTLE_CODE_SIGN_THUMBPRINT is required for publish.bat --sign. Use a CA-issued code-signing certificate, not a development self-signed certificate."
}
$thumbprint = ($thumbprint -replace '\s', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[A-F0-9]{40}$') {
    throw "TURTLE_CODE_SIGN_THUMBPRINT must be a 40-character certificate thumbprint."
}

$timestampUrl = $env:TURTLE_CODE_SIGN_TIMESTAMP_URL
$parsedTimestampUrl = $null
if ([string]::IsNullOrWhiteSpace($timestampUrl) -or
    -not [Uri]::TryCreate($timestampUrl, [UriKind]::Absolute, [ref]$parsedTimestampUrl) -or
    $parsedTimestampUrl.Scheme -notin @('http', 'https')) {
    throw "TURTLE_CODE_SIGN_TIMESTAMP_URL must be the certificate provider's RFC 3161 timestamp URL."
}

$certRecord = $null
foreach ($storeName in @('CurrentUser', 'LocalMachine')) {
    $cert = Get-Item -LiteralPath "Cert:\$storeName\My\$thumbprint" -ErrorAction SilentlyContinue
    if ($cert) {
        $certRecord = [pscustomobject]@{ Certificate = $cert; StoreName = $storeName }
        break
    }
}
if (-not $certRecord) {
    throw "The code-signing certificate was not found in CurrentUser/My or LocalMachine/My."
}

$cert = $certRecord.Certificate
if (-not $cert.HasPrivateKey) {
    throw "The selected certificate has no accessible private key."
}
if ($cert.Subject -eq $cert.Issuer) {
    throw "The selected certificate is self-signed. Smart App Control requires a trusted CA-issued certificate."
}
if (-not ($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })) {
    throw "The selected certificate is not authorized for code signing."
}
if ($cert.NotBefore -gt (Get-Date) -or $cert.NotAfter -le (Get-Date)) {
    throw "The selected code-signing certificate is not currently valid."
}

$chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
try {
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(20)
    if (-not $chain.Build($cert)) {
        $reasons = ($chain.ChainStatus | ForEach-Object { $_.Status.ToString() }) -join ', '
        throw "The signing certificate's trusted chain or revocation status could not be verified: $reasons"
    }
} finally {
    $chain.Dispose()
}

$windowsKitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signTool = $null
if (Test-Path -LiteralPath $windowsKitsBin) {
    $signTool = Get-ChildItem -LiteralPath $windowsKitsBin -Recurse -Filter 'signtool.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signTool) {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) { $signTool = $command.Source }
}
if (-not $signTool) {
    throw "signtool.exe was not found. Install the Windows SDK."
}

Write-Host "[SIGNATURE] Certificate: $($cert.Subject)"
Write-Host "[SIGNATURE] Timestamp URL: $timestampUrl"
if ($Mode -eq "Preflight") { return }

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Release executable was not found: $exePath"
}

$signArguments = @('sign', '/fd', 'SHA256', '/sha1', $thumbprint,
    '/tr', $timestampUrl, '/td', 'SHA256')
if ($certRecord.StoreName -eq 'LocalMachine') { $signArguments += '/sm' }
$signArguments += @($exePath)

& $signTool @signArguments
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed (exit code $LASTEXITCODE)." }

& $signTool verify /pa $exePath
if ($LASTEXITCODE -ne 0) { throw "signtool verify failed (exit code $LASTEXITCODE)." }

$signature = Get-AuthenticodeSignature -LiteralPath $exePath
if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
    throw "The release executable does not have the expected valid Authenticode signature."
}

Write-Host "[SIGNATURE] Verified: $exePath"
