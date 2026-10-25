param([string]$OutputDirectory = 'F:/appdata/Temp/liuzhuan-audio-checks')
$ErrorActionPreference = 'Stop'
$audioRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$audioCache = 'F:/gradle-home/caches/modules-2/files-2.1'
function Find-AudioJar([string]$Relative) {
    $audioDependency = Get-ChildItem -LiteralPath (Join-Path $audioCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1
    if (-not $audioDependency) { throw "Cached dependency missing: $Relative" }
    return $audioDependency.FullName
}
$audioStdlib = Find-AudioJar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$audioAnnotations = Find-AudioJar 'org.jetbrains/annotations/23.0.0'
$audioCompiler = @((Find-AudioJar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'), $audioStdlib,
    (Find-AudioJar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'), (Find-AudioJar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Find-AudioJar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'), $audioAnnotations,
    (Find-AudioJar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0')) -join ';'
$audioRuntime = "$audioStdlib;$audioAnnotations"
$audioRecoverySource = Join-Path $audioRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app/audio/MicrophoneRecovery.kt'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$audioJar = Join-Path $OutputDirectory 'audio-checks.jar'
$audioFixture = Join-Path $OutputDirectory 'android-frame.bin'
& F:/jdk-21.0.12+8/bin/java.exe -cp $audioCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $audioRuntime -d $audioJar (Join-Path $PSScriptRoot 'AndroidProtocolCheck.kt') (Join-Path $audioRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app/audio/AudioProtocol.kt') (Join-Path $audioRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app/audio/PcmLevelMeter.kt') (Join-Path $audioRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app/audio/SilenceGate.kt') (Join-Path $audioRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app/audio/MicrophoneLifetime.kt') $audioRecoverySource
if ($LASTEXITCODE -ne 0) { throw 'Android audio encoder compilation failed' }
& F:/jdk-21.0.12+8/bin/java.exe -cp "$audioJar;$audioRuntime" AndroidProtocolCheckKt $audioFixture
if ($LASTEXITCODE -ne 0) { throw 'Android audio encoder checks failed' }
& F:/dotnet10/dotnet.exe run --project (Join-Path $PSScriptRoot 'AudioChecks.csproj') -c Release -- --android-frame $audioFixture
if ($LASTEXITCODE -ne 0) { throw 'Cross-platform audio checks failed' }
