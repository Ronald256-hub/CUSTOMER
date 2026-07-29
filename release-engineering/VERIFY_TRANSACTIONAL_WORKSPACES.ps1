[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-Text {
    param([string]$Path, [string]$Pattern, [string]$Message)
    if (-not (Test-Path $Path -PathType Leaf)) { throw "Missing required file: $Path" }
    $content = Get-Content $Path -Raw
    if ($content -notmatch $Pattern) { throw $Message }
}

$web = Join-Path $SourceRoot 'src/Robo.Pos.Server/wwwroot'
$script = Join-Path $web 'transactional-workspaces.js'
$style = Join-Path $web 'transactional-workspaces.css'
$index = Join-Path $web 'index.html'

Require-Text $index 'transactional-workspaces\.css' 'The transactional workspace stylesheet is not loaded.'
Require-Text $index 'transactional-workspaces\.js' 'The transactional workspace script is not loaded.'
Require-Text $script 'Dispense one glass' 'The measured short-glass sale control is missing.'
Require-Text $script 'remainingGlasses' 'Short-glass sellable quantity calculation is missing.'
Require-Text $script 'data-cart-step' 'Touch quantity controls are missing.'
Require-Text $script 'Complete sale and issue receipt' 'The transactional checkout action is missing.'
Require-Text $style 'transactional-sales-grid' 'The responsive sales workspace grid is missing.'
Require-Text $style '@media \(max-width: 760px\)' 'Tablet/mobile workspace rules are missing.'

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js is required to parse transactional-workspaces.js.' }
& $node.Source --check $script
if ($LASTEXITCODE -ne 0) { throw 'transactional-workspaces.js failed JavaScript parsing.' }

Write-Host 'Nexus transactional workspace source verification passed.'
