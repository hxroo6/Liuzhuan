param(
    [string]$OutputDirectory = 'F:/appdata/Temp/liuzhuan-android-privacy-checks'
)
$ErrorActionPreference = 'Stop'
$privacyRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$privacyCache = 'F:/gradle-home/caches/modules-2/files-2.1'
$privacyJava = 'F:/jdk-21.0.12+8/bin/java.exe'
function Find-PrivacyJar([string]$Relative) {
    $privacyJar = Get-ChildItem -LiteralPath (Join-Path $privacyCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1
    if (-not $privacyJar) { throw "Required cached Kotlin dependency is missing: $Relative" }
    return $privacyJar.FullName
}
$privacyStdlib = Find-PrivacyJar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$privacyCoroutines = Find-PrivacyJar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$privacyAnnotations = Find-PrivacyJar 'org.jetbrains/annotations/23.0.0'
$privacyCompilerClasspath = @(
    (Find-PrivacyJar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),
    $privacyStdlib,
    (Find-PrivacyJar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),
    (Find-PrivacyJar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Find-PrivacyJar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),
    $privacyAnnotations,
    $privacyCoroutines
) -join ';'
$privacyRuntimeClasspath = @($privacyStdlib, $privacyCoroutines, $privacyAnnotations,'F:/gradle-8.11.1/lib/groovy-3.0.22.jar','F:/gradle-8.11.1/lib/groovy-json-3.0.22.jar') -join ';'
$privacySources = @(
    (Join-Path $privacyRepoRoot 'scripts/AutoReceiptChecks/JsonApiAdapter.kt'),
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/TransferTasks.kt'),
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/TransferTaskStates.kt'),
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/LanHub.kt'),
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/ClipReceiver.kt')
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/HookSenderIdentity.kt')
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/core/RecentFileEvents.kt')
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/core/Hex.kt')
) + @((Get-ChildItem -LiteralPath (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/clipboard') -Filter '*.kt' |
    Where-Object Name -ne 'ClipMonitorService.kt').FullName) +
    @((Get-ChildItem -LiteralPath (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/clipboard/action') -Filter '*.kt').FullName) +
    @((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.kt').FullName)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$privacyOutputJar = Join-Path $OutputDirectory 'privacy-checks.jar'
& $privacyJava -cp $privacyCompilerClasspath org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect `
    -classpath $privacyRuntimeClasspath -d $privacyOutputJar @privacySources 2>&1 |
    Tee-Object -FilePath (Join-Path $OutputDirectory 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Kotlin privacy compilation failed: $LASTEXITCODE" }
& $privacyJava -cp "$privacyOutputJar;$privacyRuntimeClasspath" ClipboardPrivacyCheckKt 2>&1 |
    Tee-Object -FilePath (Join-Path $OutputDirectory 'checks.log')
if ($LASTEXITCODE -ne 0) { throw "Android privacy checks failed: $LASTEXITCODE" }
foreach ($privacyFixture in @('physical-empty', 'backup-empty', 'missing-marker', 'missing-field', 'wrong-type')) {
    & $privacyJava -cp "$privacyOutputJar;$privacyRuntimeClasspath" ClipboardPrivacyCheckKt $privacyFixture 2>&1 |
        Tee-Object -FilePath (Join-Path $OutputDirectory 'checks.log') -Append
    if ($LASTEXITCODE -ne 0) { throw "Android privacy $privacyFixture checks failed: $LASTEXITCODE" }
}
# Compile the real hook against Android 36, not the JVM receiver stubs. This
# catches platform method/signature mistakes but is not an LSPosed device test.
$privacyHookClasspath = @($privacyStdlib, $privacyAnnotations,
    'F:/Android-SDK/platforms/android-36/android.jar',
    (Join-Path $privacyRepoRoot 'AndroidApp/app/libs/xposed-api-stub.jar')) -join ';'
$privacyHookSources = @('xposed/ClipHook.kt', 'core/BoundedRead.kt') | ForEach-Object {
    Join-Path $privacyRepoRoot ('AndroidApp/app/src/main/java/com/liuzhuan/app/' + $_)
}
& $privacyJava -cp $privacyCompilerClasspath org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect `
    -classpath $privacyHookClasspath -d (Join-Path $OutputDirectory 'hook-sdk36-check.jar') @privacyHookSources 2>&1 |
    Tee-Object -FilePath (Join-Path $OutputDirectory 'hook-compile.log')
if ($LASTEXITCODE -ne 0) { throw "Production hook SDK 36 compilation failed: $LASTEXITCODE" }
Write-Output "Evidence saved to $OutputDirectory"
