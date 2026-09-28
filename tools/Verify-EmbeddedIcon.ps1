param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$IconPath
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'EXE missing for icon inspection.' }
if (-not (Test-Path -LiteralPath $IconPath -PathType Leaf)) { throw 'Generated ICO missing for inspection.' }
Add-Type -Path (Join-Path $PSScriptRoot 'IconResourceInspector.cs')
[RptEmbeddedIconInspector]::Verify(
    (Resolve-Path -LiteralPath $ExecutablePath).Path,
    (Resolve-Path -LiteralPath $IconPath).Path)
