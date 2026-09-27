param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('KRX_API_KEY', 'OPENDART_API_KEY', 'OPENAI_API_KEY')]
    [string]$Name
)

$ErrorActionPreference = 'Stop'
$apiSecret = Read-Host "$Name (input hidden)" -AsSecureString
$apiSecretPointer = [IntPtr]::Zero
try {
    $apiSecretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($apiSecret)
    $apiSecretValue = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($apiSecretPointer)
    if ([string]::IsNullOrWhiteSpace($apiSecretValue) -or $apiSecretValue -match '\p{C}') {
        throw 'Key is empty or contains control characters.'
    }
    if ($Name -eq 'OPENDART_API_KEY' -and $apiSecretValue -notmatch '^[A-Za-z0-9]{40}$') {
        throw 'OpenDART key must contain 40 letters/digits.'
    }
    [Environment]::SetEnvironmentVariable($Name, $apiSecretValue, 'User')
    [Environment]::SetEnvironmentVariable($Name, $apiSecretValue, 'Process')
    Write-Output "$Name configured for this Windows user. The value was not printed."
} finally {
    if ($apiSecretPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($apiSecretPointer) }
    $apiSecretValue = $null
    $apiSecret.Dispose()
}
