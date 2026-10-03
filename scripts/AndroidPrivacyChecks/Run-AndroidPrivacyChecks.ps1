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
$privacyRuntimeClasspath = @($privacyStdlib, $privacyCoroutines, $privacyAnnotations) -join ';'
$privacySources = @(
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/LanHub.kt'),
    (Join-Path $privacyRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app/ClipReceiver.kt')
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
Write-Output "Evidence saved to $OutputDirectory"
