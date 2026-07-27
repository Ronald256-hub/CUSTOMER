[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $SourceRoot).Path
$servicePath = Join-Path $root "src\Robo.Pos.Server\Administration\SystemAdministrationService.cs"
$modelsPath = Join-Path $root "src\Robo.Pos.Server\Administration\SystemAdministrationModels.cs"
$utf8 = [System.Text.UTF8Encoding]::new($false)

foreach ($path in @($servicePath, $modelsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required accounting settings source file was not found: $path"
    }
}

function Replace-RequiredText {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$OldText,
        [Parameter(Mandatory)][string]$NewText,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Text.Contains($NewText)) {
        Write-Host "$Description is already applied." -ForegroundColor Yellow
        return $Text
    }

    if (-not $Text.Contains($OldText)) {
        throw "Could not locate the source section for: $Description"
    }

    Write-Host "Applying: $Description" -ForegroundColor Cyan
    return $Text.Replace($OldText, $NewText)
}

$models = Get-Content -LiteralPath $modelsPath -Raw -Encoding UTF8
$oldModel = @'
    string Phone,
    string Email,
    string ReceiptFooter);
'@
$newModel = @'
    string Phone,
    string Email,
    string CurrencyCode,
    string ReceiptFooter);
'@

if (-not $models.Contains("string CurrencyCode,")) {
    $models = Replace-RequiredText `
        -Text $models `
        -OldText $oldModel `
        -NewText $newModel `
        -Description "business settings currency request"
    [System.IO.File]::WriteAllText($modelsPath, $models, $utf8)
}

$content = Get-Content -LiteralPath $servicePath -Raw -Encoding UTF8
if ($content.Contains("string currencyCode = NormalizeCurrencyCode(")) {
    Write-Host "Business currency handling is already configurable." -ForegroundColor Green
    exit 0
}

$oldDeclaration = @'
        string email = Optional(
            request.Email,
            200,
            "Business email");

        string receiptFooter = Required(
'@
$newDeclaration = @'
        string email = Optional(
            request.Email,
            200,
            "Business email");

        string currencyCode = NormalizeCurrencyCode(
            request.CurrencyCode);

        string receiptFooter = Required(
'@
$content = Replace-RequiredText `
    -Text $content `
    -OldText $oldDeclaration `
    -NewText $newDeclaration `
    -Description "currency validation"

$content = Replace-RequiredText `
    -Text $content `
    -OldText "            currency_code = 'UGX'," `
    -NewText '            currency_code = $currencyCode,' `
    -Description "currency SQL update"

$oldParameters = @'
        command.Parameters.AddWithValue(
            "$email",
            email);

        command.Parameters.AddWithValue(
            "$receiptFooter",
'@
$newParameters = @'
        command.Parameters.AddWithValue(
            "$email",
            email);

        command.Parameters.AddWithValue(
            "$currencyCode",
            currencyCode);

        command.Parameters.AddWithValue(
            "$receiptFooter",
'@
$content = Replace-RequiredText `
    -Text $content `
    -OldText $oldParameters `
    -NewText $newParameters `
    -Description "currency SQL parameter"

$oldAudit = @'
                phone,
                email,
                receiptVerificationEnabled = false
'@
$newAudit = @'
                phone,
                email,
                currencyCode,
                receiptVerificationEnabled = false
'@
$content = Replace-RequiredText `
    -Text $content `
    -OldText $oldAudit `
    -NewText $newAudit `
    -Description "currency audit detail"

$oldResult = @'
            phone,
            email,
            "UGX",
            receiptFooter,
'@
$newResult = @'
            phone,
            email,
            currencyCode,
            receiptFooter,
'@
$content = Replace-RequiredText `
    -Text $content `
    -OldText $oldResult `
    -NewText $newResult `
    -Description "currency settings response"

$methodAnchor = "    private static string Required("
$currencyMethod = @'
    private static string NormalizeCurrencyCode(
        string? value)
    {
        string currencyCode =
            value?.Trim().ToUpperInvariant() ?? string.Empty;

        if (currencyCode.Length != 3 ||
            currencyCode.Any(character =>
                !char.IsLetter(character)))
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_currency_code",
                "Enter a three-letter currency code such as UGX, USD or EUR.");
        }

        return currencyCode;
    }

'@

if (-not $content.Contains($methodAnchor)) {
    throw "Could not locate the settings validation method anchor."
}
$content = $content.Replace(
    $methodAnchor,
    $currencyMethod + $methodAnchor
)

[System.IO.File]::WriteAllText($servicePath, $content, $utf8)

$verification = Get-Content -LiteralPath $servicePath -Raw -Encoding UTF8
foreach ($required in @(
    "request.CurrencyCode",
    'currency_code = $currencyCode,',
    '"$currencyCode",',
    "currencyCode,",
    "NormalizeCurrencyCode("
)) {
    if (-not $verification.Contains($required)) {
        throw "Currency source verification failed. Missing: $required"
    }
}

Write-Host "Customer-selected three-letter currency handling was applied and verified." -ForegroundColor Green
