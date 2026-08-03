$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Old,
        [Parameter(Mandatory)][string]$New,
        [Parameter(Mandatory)][string]$Label
    )

    $content = [System.IO.File]::ReadAllText($Path)
    $count = ([regex]::Matches($content, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "$Label expected one match but found $count."
    }

    $updated = $content.Replace($Old, $New)
    [System.IO.File]::WriteAllText(
        $Path,
        $updated,
        [System.Text.UTF8Encoding]::new($false))
}

$root = Split-Path $PSScriptRoot -Parent

Replace-ExactlyOnce `
    -Path (Join-Path $root 'src/Robo.Pos.Server/Sales/AuditDocumentWriter.cs') `
    -Old '            html.Append("<div class="customer"><strong>Payment breakdown</strong>");' `
    -New '            html.Append("<div class=\"customer\"><strong>Payment breakdown</strong>");' `
    -Label 'Receipt HTML class quoting'

Replace-ExactlyOnce `
    -Path (Join-Path $root 'src/Robo.Pos.Server/Sales/ShopReceiptService.cs') `
    -Old '            header.PaymentMethod,
            header.Notes,' `
    -New '            payments.Count > 1 ? "split" : header.PaymentMethod,
            header.Notes,' `
    -Label 'Receipt payment summary'

Write-Host 'Focused split-payment compile and receipt-summary corrections applied.'
