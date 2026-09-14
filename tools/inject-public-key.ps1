# Ersetzt den eingebetteten Signatur-Public-Key im Client durch den PRODUKTIVEN Key.
# Aufruf (z. B. in der CI):  pwsh tools\inject-public-key.ps1 -PemPath prod_public.pem
param(
  [Parameter(Mandatory=$true)][string]$PemPath
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "client\FrohLock.Core\Crypto\EmbeddedKeys.cs"
$pem = (Get-Content $PemPath -Raw).TrimEnd("`r","`n")

$content = @"
namespace FrohLock.Core.Crypto;

/// <summary>Fest eingebauter Public Key des Signaturservers (PRODUKTIV injiziert).</summary>
public static class EmbeddedKeys
{
    public const string SigningPublicKeyPem = @"$pem";
}
"@
Set-Content -Path $target -Value $content -Encoding UTF8
Write-Host "Public Key injiziert in $target"
