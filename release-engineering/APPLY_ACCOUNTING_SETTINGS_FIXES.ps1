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

$models = Get-Content -LiteralPath $modelsPath -Raw -Encoding UTF8
if ($models -notmatch '(?s)UpdateBusinessSettingsRequest\(.*?CurrencyCode') {
    $models = [regex]::Replace(
        $models,
        'string Email,\r?\n\s*string ReceiptFooter\);',
        "string Email,`r`n    string CurrencyCode,`r`n    string ReceiptFooter);",
        1
    )
    [System.IO.File]::WriteAllText($modelsPath, $models, $utf8)
}

$content = Get-Content -LiteralPath $servicePath -Raw -Encoding UTF8
if ($content.Contains("string currencyCode = NormalizeCurrencyCode(")) {
    Write-Host "Business currency handling is already configurable." -ForegroundColor Green
    exit 0
}

$declarationPattern = [regex]::new(
    '(?s)(string email = Optional\(\s*request\.Email,\s*200,\s*"Business email"\);\s*)(string receiptFooter)')
if (-not $declarationPattern.IsMatch($content)) {
    throw "Could not locate the business email/receipt settings section."
}
$content = $declarationPattern.Replace(
    $content,
    {
        param($match)
        return $match.Groups[1].Value +
            "string currencyCode = NormalizeCurrencyCode(`r`n" +
            "            request.CurrencyCode);`r`n`r`n        " +
            $match.Groups[2].Value
    },
    1
)

if (-not $content.Contains("currency_code = 'UGX',")) {
    throw "Could not locate the hard-coded UGX update assignment."
}
$content = $content.Replace(
    "currency_code = 'UGX',",
    'currency_code = $currencyCode,'
)

$parameterPattern = [regex]::new(
    '(?s)(command\.Parameters\.AddWithValue\(\s*"\$email",\s*email\);\s*)(command\.Parameters\.AddWithValue\(\s*"\$receiptFooter")')
if (-not $parameterPattern.IsMatch($content)) {
    throw "Could not locate the business email SQL parameter section."
}
$content = $parameterPattern.Replace(
    $content,
    {
        param($match)
        return $match.Groups[1].Value +
            "command.Parameters.AddWithValue(`r`n" +
            "            \"`$currencyCode\",`r`n" +
            "            currencyCode);`r`n`r`n        " +
            $match.Groups[2].Value
    },
    1
)

$auditPattern = [regex]::new(
    '(?s)(new\s*\{\s*businessName,\s*address,\s*phone,\s*email,)(\s*receiptVerificationEnabled)')
if (-not $auditPattern.IsMatch($content)) {
    throw "Could not locate the settings audit payload."
}
$content = $auditPattern.Replace(
    $content,
    {
        param($match)
        return $match.Groups[1].Value +
            "`r`n                currencyCode," +
            $match.Groups[2].Value
    },
    1
)

$returnPattern = [regex]::new(
    '(?s)(return new BusinessSettingsResult\(\s*businessName,\s*address,\s*phone,\s*email,\s*)"UGX",')
if (-not $returnPattern.IsMatch($content)) {
    throw "Could not locate the hard-coded settings response currency."
}
$content = $returnPattern.Replace(
    $content,
    {
        param($match)
        return $match.Groups[1].Value + "currencyCode,"
    },
    1
)

$methodAnchor = "    private static string Required("
if (-not $content.Contains($methodAnchor)) {
    throw "Could not locate the settings validation method anchor."
}

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

$content = $content.Replace(
    $methodAnchor,
    $currencyMethod + $methodAnchor
)

[System.IO.File]::WriteAllText($servicePath, $content, $utf8)
Write-Host "Customer-selected three-letter currency handling was applied." -ForegroundColor Green
