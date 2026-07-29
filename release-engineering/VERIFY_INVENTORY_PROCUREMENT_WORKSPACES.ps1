$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root 'src/Robo.Pos.Server/wwwroot/inventory-procurement-workspaces.js'
$style = Join-Path $root 'src/Robo.Pos.Server/wwwroot/inventory-procurement-workspaces.css'
$index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
$browser = Join-Path $root 'release-engineering/VERIFY_OPERATOR_EXPERIENCE_BROWSER.mjs'

foreach ($path in @($script, $style, $index, $browser)) {
    if (-not (Test-Path $path -PathType Leaf)) { throw "Required workspace asset missing: $path" }
}

node --check $script
if ($LASTEXITCODE -ne 0) { throw 'Inventory/procurement JavaScript parsing failed.' }
node --check $browser
if ($LASTEXITCODE -ne 0) { throw 'Extended Edge browser script parsing failed.' }

$js = Get-Content $script -Raw
$css = Get-Content $style -Raw
$html = Get-Content $index -Raw
$browserText = Get-Content $browser -Raw

$requiredJs = @(
    'Inventory movement centre',
    'Procurement workspace',
    'inventoryWorkspaceFilter',
    'data-procurement-tab',
    'reorder-recommendations',
    'purchase-orders?limit=50',
    'goods-receipts?limit=50',
    'short_glass',
    'Math.floor'
)
foreach ($token in $requiredJs) {
    if (-not $js.Contains($token)) { throw "Workspace JavaScript missing required token: $token" }
}

foreach ($token in @('.ip-stock-card', '.ip-workflow-guide', '@media(max-width:620px)')) {
    if (-not $css.Contains($token)) { throw "Workspace CSS missing required token: $token" }
}

foreach ($token in @('/inventory-procurement-workspaces.css', '/inventory-procurement-workspaces.js')) {
    if (-not $html.Contains($token)) { throw "Workspace asset is not wired in index.html: $token" }
}

foreach ($token in @('Inventory movement centre', 'Procurement workspace', 'Stock view', 'Purchase orders', 'Goods receipts')) {
    if (-not $browserText.Contains($token)) { throw "Edge acceptance journey missing assertion: $token" }
}

Write-Host 'Inventory and procurement workspace source validation passed.'
