# Publisht die drei Client-Programme als self-contained Single-File (win-x64)
# nach installer\payload\{service,agent,setup}. Aufruf: pwsh tools\publish-client.ps1
param(
  [string]$Configuration = "Release",
  [string]$Rid = "win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root "installer\payload"

$projects = @{
  "service" = "client\FrohLock.Service\FrohLock.Service.csproj"
  "agent"   = "client\FrohLock.Agent\FrohLock.Agent.csproj"
  "setup"   = "client\FrohLock.Setup\FrohLock.Setup.csproj"
}

if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null

foreach ($name in $projects.Keys) {
  $proj = Join-Path $root $projects[$name]
  $out = Join-Path $payload $name
  Write-Host "Publish $name ..."
  dotnet publish $proj -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $out
  if ($LASTEXITCODE -ne 0) { throw "Publish $name fehlgeschlagen" }
}
Write-Host "Fertig. Payload unter $payload"
