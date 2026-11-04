param(
    [string]$ResultsDirectory = 'artifacts/test-results',
    [string]$DomainDirectory = 'src/el.Domain',
    [ValidateRange(0, 100)][int]$MinimumPercent = 85
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter coverage.cobertura.xml -Recurse -File)
if ($reports.Count -eq 0) { throw 'Coverage reports are missing. Run tests with XPlat Code Coverage.' }

# Merge hits by source line; summing report percentages would count the same code twice.
$lines = [System.Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
foreach ($report in $reports) {
    [xml]$document = Get-Content -LiteralPath $report.FullName -Raw
    foreach ($class in $document.SelectNodes("/coverage/packages/package[@name='el.Domain']/classes/class")) {
        $file = $class.GetAttribute('filename').Replace('\', '/')
        foreach ($line in $class.SelectNodes('lines/line')) {
            $key = $file + ':' + $line.GetAttribute('number')
            $hit = [long]$line.GetAttribute('hits') -gt 0
            if ($lines.ContainsKey($key)) { $lines[$key] = $lines[$key] -or $hit }
            else { $lines.Add($key, $hit) }
        }
    }
}

$covered = @($lines.Values | Where-Object { $_ }).Count
$total = $lines.Count
if ($total -eq 0) {
    $sources = @(Get-ChildItem -LiteralPath $DomainDirectory -Filter '*.cs' -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    if ($sources.Count -gt 0) {
        throw 'Domain sources exist but no executable Domain lines were reported. Check collector filters and test execution.'
    }
    $summary = 'Domain coverage: N/A (no Domain implementation yet; empty scaffolding is not counted as covered).'
}
else {
    $percent = 100.0 * $covered / $total
    $display = $percent.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
    $summary = "Domain line coverage: $display% ($covered/$total); required: $MinimumPercent%."
}

$summaryPath = Join-Path $ResultsDirectory 'domain-coverage-summary.md'
$summary | Set-Content -LiteralPath $summaryPath -Encoding utf8
Write-Output $summary
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
if ($total -gt 0 -and 100.0 * $covered -lt $MinimumPercent * $total) {
    throw "Domain coverage is below $MinimumPercent%. Add tests for the implemented rules."
}
