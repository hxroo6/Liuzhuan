param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-receive-directory-checks')
$ErrorActionPreference='Stop'
$directoryRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$directoryJava='F:/jdk-21.0.12+8/bin/java.exe'
$directoryCache='F:/gradle-home/caches/modules-2/files-2.1'
function Find-DirectoryJar([string]$Relative) {
    $directoryDependency=Get-ChildItem -LiteralPath (Join-Path $directoryCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1
    if(-not $directoryDependency) { throw "Cached dependency missing: $Relative" }
    return $directoryDependency.FullName
}
$directoryStdlib=Find-DirectoryJar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$directoryAnnotations=Find-DirectoryJar 'org.jetbrains/annotations/23.0.0'
$directoryCompiler=@((Find-DirectoryJar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$directoryStdlib,
    (Find-DirectoryJar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),
    (Find-DirectoryJar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Find-DirectoryJar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$directoryAnnotations,
    (Find-DirectoryJar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0')) -join ';'
$directoryRuntime=@($directoryStdlib,$directoryAnnotations) -join ';'
$directorySources=@((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.kt').FullName)
$directorySources+=(Join-Path $directoryRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app/ReceivedFiles.kt')
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$directoryJar=Join-Path $OutputDirectory 'directory-checks.jar'
& $directoryJava -cp $directoryCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $directoryRuntime -d $directoryJar @directorySources
if($LASTEXITCODE -ne 0) { throw 'Directory check compilation failed' }
& $directoryJava -cp "$directoryJar;$directoryRuntime" com.liuzhuan.app.DirectoryCheckKt $directoryRepo
if($LASTEXITCODE -ne 0) { throw 'Directory checks failed' }
