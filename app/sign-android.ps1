[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UnsignedApk,
    [Parameter(Mandatory)][string]$OutputApk,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$AndroidSdk = (Join-Path (Split-Path -Parent $PSScriptRoot) 'toolchain\android-sdk')
)

$ErrorActionPreference = 'Stop'
if (-not $JavaHome) { throw 'Set JavaHome to the JDK used to build Vape Meter Android.' }
$inputPath = (Resolve-Path -LiteralPath $UnsignedApk).ProviderPath
$outputPath = [IO.Path]::GetFullPath($OutputApk)
if ($inputPath -eq $outputPath) { throw 'Choose a different path for the signed APK.' }
$tools = Join-Path $AndroidSdk 'build-tools\36.0.0'
$keytool = Join-Path $JavaHome 'bin\keytool.exe'
$apksigner = Join-Path $tools 'apksigner.bat'
$zipalign = Join-Path $tools 'zipalign.exe'
foreach ($program in @($keytool, $apksigner, $zipalign)) {
    if (-not (Test-Path -LiteralPath $program -PathType Leaf)) { throw "Tool missing: $program" }
}

$signing = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VapeMeter\android-signing'
$keystore = Join-Path $signing 'vapemeter-android.p12'
$passwordFile = Join-Path $signing 'password.dpapi'
$alias = 'vapemeter-android'
[IO.Directory]::CreateDirectory($signing) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null

if (-not (Test-Path -LiteralPath $passwordFile)) {
    if (Test-Path -LiteralPath $keystore) { throw 'Signing keystore exists but its Windows password vault is missing.' }
    $random = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $password = [Convert]::ToBase64String($random) | ConvertTo-SecureString -AsPlainText -Force
    [Array]::Clear($random, 0, $random.Length)
    [IO.File]::WriteAllText($passwordFile, (ConvertFrom-SecureString $password), [Text.UTF8Encoding]::new($false))
}
$password = ConvertTo-SecureString ([IO.File]::ReadAllText($passwordFile))
$oldJavaHome = $env:JAVA_HOME
$env:JAVA_HOME = $JavaHome
$env:VAPEMETER_SIGN_PASSWORD = [Net.NetworkCredential]::new('', $password).Password
$aligned = Join-Path ([IO.Path]::GetTempPath()) ('vapemeter-align-' + [Guid]::NewGuid().ToString('N') + '.apk')
try {
    if (-not (Test-Path -LiteralPath $keystore)) {
        $stagedKeystore = $keystore + '.new'
        & $keytool -genkeypair -noprompt -storetype PKCS12 -keystore $stagedKeystore -alias $alias `
            -storepass:env VAPEMETER_SIGN_PASSWORD -keyalg RSA -keysize 3072 -validity 10000 `
            -dname 'CN=Vape Meter Android,O=Vape Meter'
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the dedicated signing key.' }
        [IO.File]::Move($stagedKeystore, $keystore)
    }
    & $zipalign -P 16 -f 4 $inputPath $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed.' }
    & $apksigner sign --ks $keystore --ks-key-alias $alias --ks-pass env:VAPEMETER_SIGN_PASSWORD `
        --key-pass env:VAPEMETER_SIGN_PASSWORD --out $outputPath $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }
    & $apksigner verify --verbose --print-certs $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
    & $zipalign -c -P 16 4 $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Signed APK has incorrect alignment.' }
}
finally {
    [Environment]::SetEnvironmentVariable('VAPEMETER_SIGN_PASSWORD', $null, 'Process')
    $env:JAVA_HOME = $oldJavaHome
    [IO.File]::Delete($aligned)
}
