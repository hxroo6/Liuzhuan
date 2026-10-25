param([string]$OutputDirectory = 'F:/appdata/Temp/liuzhuan-auto-receipt-checks')
$ErrorActionPreference = 'Stop'
$receiptRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$receiptCache = 'F:/gradle-home/caches/modules-2/files-2.1'
$receiptJava = 'F:/jdk-21.0.12+8/bin/java.exe'
function Find-ReceiptJar([string]$Relative) {
    $receiptJar = Get-ChildItem -LiteralPath (Join-Path $receiptCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1
    if (-not $receiptJar) { throw "Cached dependency missing: $Relative" }
    return $receiptJar.FullName
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
& F:/dotnet10/dotnet.exe build (Join-Path $PSScriptRoot 'AutoReceiptChecks.csproj') --nologo 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'dotnet-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Isolated production WsHub compilation failed' }
& F:/dotnet10/dotnet.exe (Join-Path $PSScriptRoot 'bin/Debug/net10.0/AutoReceiptChecks.dll') 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'websocket-checks.log')
if ($LASTEXITCODE -ne 0) { throw 'Production websocket checks failed' }
$receiptStdlib = Find-ReceiptJar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$receiptCoroutines = Find-ReceiptJar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$receiptAnnotations = Find-ReceiptJar 'org.jetbrains/annotations/23.0.0'
$receiptCompilerClasspath = @(
    (Find-ReceiptJar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'), $receiptStdlib,
    (Find-ReceiptJar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),
    (Find-ReceiptJar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Find-ReceiptJar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'), $receiptAnnotations, $receiptCoroutines
) -join ';'
$receiptRuntimeClasspath = @($receiptStdlib, $receiptCoroutines, $receiptAnnotations,
    (Find-ReceiptJar 'com.squareup.okhttp3/okhttp/4.12.0'),
    (Find-ReceiptJar 'com.squareup.okio/okio-jvm/3.6.0'),
    'F:/gradle-8.11.1/lib/groovy-3.0.22.jar', 'F:/gradle-8.11.1/lib/groovy-json-3.0.22.jar') -join ';'
$receiptJavaRoot = Join-Path $receiptRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app'
$receiptSources = @('LanHub.kt', 'TransferTasks.kt', 'TransferWork.kt', 'PowerSignals.kt', 'ClipReceiver.kt', 'net/LanClient.kt', 'net/Proto.kt', 'net/HttpClients.kt', 'net/RetryBackoff.kt', 'core/RecentFileEvents.kt', 'core/Hex.kt', 'MaterialLibrary.kt', 'data/MaterialItem.kt', 'data/MaterialRepository.kt', 'net/LanDiscovery.kt', 'net/IdentityProof.kt') | ForEach-Object { Join-Path $receiptJavaRoot $_ }
$receiptSources += Join-Path $receiptJavaRoot 'HookSenderIdentity.kt'
$receiptSources += Join-Path $receiptJavaRoot 'TransferTaskStates.kt'
$receiptSources += Join-Path $receiptJavaRoot 'TransferPolicy.kt'
$receiptSources += @((Get-ChildItem -LiteralPath (Join-Path $receiptJavaRoot 'clipboard') -Filter '*.kt' | Where-Object Name -ne 'ClipMonitorService.kt').FullName)
$receiptSources += @((Get-ChildItem -LiteralPath (Join-Path $receiptJavaRoot 'clipboard/action') -Filter '*.kt').FullName)
$receiptSources += @((Get-ChildItem -LiteralPath (Join-Path $receiptRepoRoot 'scripts/AndroidPrivacyChecks') -Filter 'Android*Stub.kt').FullName)
$receiptSources += @((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.kt').FullName)
$receiptSources += @('audio/AudioPairing.kt','audio/AudioProtocol.kt') | ForEach-Object { Join-Path $receiptJavaRoot $_ }
$receiptSources += @('core/LibraryBudget.kt','core/LibraryWorkspace.kt') | ForEach-Object { Join-Path $receiptJavaRoot $_ }
$receiptOutputJar = Join-Path $OutputDirectory 'receipt-checks.jar'
& $receiptJava -cp $receiptCompilerClasspath org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $receiptRuntimeClasspath -d $receiptOutputJar @receiptSources 2>&1 |
    Tee-Object -FilePath (Join-Path $OutputDirectory 'kotlin-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Production Android receipt compilation failed' }
$receiptPortFile = Join-Path $OutputDirectory ('port-' + [Guid]::NewGuid().ToString('N') + '.txt')
$receiptStopFile = $receiptPortFile + '.stop'
$receiptHost = Start-Process -FilePath 'F:/dotnet10/dotnet.exe' -ArgumentList @(
    ('"' + (Join-Path $PSScriptRoot 'bin/Debug/net10.0/AutoReceiptChecks.dll') + '"'), '--serve', $receiptPortFile, $receiptStopFile
) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory 'host.log') -RedirectStandardError (Join-Path $OutputDirectory 'host-error.log')
try {
    $receiptHostDeadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Path -LiteralPath $receiptPortFile)) {
        if ($receiptHost.HasExited -or (Get-Date) -gt $receiptHostDeadline) { throw 'Isolated WsHub did not become ready' }
        Start-Sleep -Milliseconds 50
    }
    $receiptPort = Get-Content -LiteralPath $receiptPortFile
    & $receiptJava -cp "$receiptOutputJar;$receiptRuntimeClasspath" ClipboardReceiptCheckKt $receiptPort 2>&1 |
        Tee-Object -FilePath (Join-Path $OutputDirectory 'android-checks.log')
    if ($LASTEXITCODE -ne 0) { throw 'Production Android automatic receipt checks failed' }
} finally {
    New-Item -ItemType File -Path $receiptStopFile -Force | Out-Null
    if (-not $receiptHost.WaitForExit(5000)) { $receiptHost.Kill() }
}
Write-Output "Evidence saved to $OutputDirectory"
