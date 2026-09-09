[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [Security.SecureString]$Password,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\test-signing'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$pfxPath = Join-Path $output 'KeyPilot-Local-Test.pfx'
$cerPath = Join-Path $output 'KeyPilot-Local-Test.cer'
if ((Test-Path -LiteralPath $pfxPath) -or (Test-Path -LiteralPath $cerPath)) {
    if (-not $Force) {
        throw "Test certificate output already exists. Nothing was overwritten: $output"
    }
}
if (-not $Password) {
    $Password = Read-Host 'Password for the local test PFX' -AsSecureString
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$rsa = [Security.Cryptography.RSA]::Create()
$rsa.KeySize = 3072
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=KeyPilot Local Test Driver',
    $rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
$ekuOids = [Security.Cryptography.OidCollection]::new()
[void]$ekuOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuOids, $true))
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))

$certificate = $request.CreateSelfSigned(
    [DateTimeOffset]::UtcNow.AddDays(-1),
    [DateTimeOffset]::UtcNow.AddYears(1))
$passwordPointer = [IntPtr]::Zero
try {
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password)
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringUni($passwordPointer)
    [IO.File]::WriteAllBytes(
        $pfxPath,
        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $plainPassword))
    [IO.File]::WriteAllBytes(
        $cerPath,
        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
} finally {
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($passwordPointer)
    }
    $certificate.Dispose()
    $rsa.Dispose()
}

Write-Host "Created local-only test certificate: $cerPath"
Write-Host "Created password-protected private key: $pfxPath"
Write-Warning 'Nothing was imported into a certificate store. Never commit or share the PFX.'
