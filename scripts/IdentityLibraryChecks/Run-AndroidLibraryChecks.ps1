param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-m49-android-checks',[Parameter(Mandatory=$true)][string]$ProofFixture)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../JvmCheckEnvironment.ps1')
$jvm=Get-JvmCheckEnvironment
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cache=$jvm.Cache
function Jar([string]$path){$file=Get-ChildItem (Join-Path $cache $path) -Recurse -Filter '*.jar' | Select-Object -First 1;if(-not $file){throw "Missing cached dependency $path"};$file.FullName}
$stdlib=Jar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21';$annotations=Jar 'org.jetbrains/annotations/23.0.0';$coroutines=Jar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$compiler=@((Jar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$stdlib,(Jar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),(Jar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),(Jar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$annotations,$coroutines)-join $jvm.Separator
$runtime=@($stdlib,$annotations,$coroutines,$jvm.Groovy,$jvm.GroovyJson)-join $jvm.Separator
$sources=@((Get-ChildItem $PSScriptRoot -Filter '*.kt').FullName)
$sources+=Join-Path $repo 'scripts/AutoReceiptChecks/JsonApiAdapter.kt'
$sources+=@('MaterialLibrary.kt','core/LibraryBudget.kt','core/LibraryWorkspace.kt','core/LibrarySavedSearches.kt','core/OfflineOriginalCatalog.kt','data/MaterialItem.kt','data/MaterialRepository.kt','net/Proto.kt','net/IdentityProof.kt','net/LanDiscovery.kt') | ForEach-Object {Join-Path $repo "AndroidApp/app/src/main/java/com/liuzhuan/app/$_"}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$jar=Join-Path $OutputDirectory 'android-library-checks.jar'
& $jvm.Java -cp $compiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $runtime -d $jar @sources
if($LASTEXITCODE -ne 0){throw 'Kotlin compilation failed'}
& $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.AndroidLibraryCheckKt $ProofFixture
if($LASTEXITCODE -ne 0){throw 'Android library checks failed'}
foreach($phase in @('seed','verify')) {
 & $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.LibraryWorkspaceChecksKt $OutputDirectory $phase
 if($LASTEXITCODE -ne 0){throw "Offline workspace $phase failed"}
}
foreach($phase in @('seed','verify')) {
 & $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.LibraryControllerChecksKt $OutputDirectory $phase
 if($LASTEXITCODE -ne 0){throw "Offline coordinator $phase failed"}
}
& $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.LibraryManagerChecksKt $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Offline library management checks failed'}
& $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.LibraryAsyncChecksKt $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Asynchronous durable library checks failed'}
& $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.LibraryTimeoutChecksKt $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Library actor timeout recovery checks failed'}
foreach($phase in @('seed','verify')) {
 & $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.LibrarySavedSearchChecksKt $OutputDirectory $phase
 if($LASTEXITCODE -ne 0){throw "Saved search $phase checks failed"}
}
& $jvm.Java -cp "$jar$($jvm.Separator)$runtime" com.liuzhuan.checks.LibraryReadPreemptionChecksKt $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Read-only query preemption checks failed'}
foreach($check in @('LibraryActivationRaceChecksKt','OfflineOriginalChecksKt','LibraryIncrementalChecksKt')) {
 & $jvm.Java -cp "$jar$($jvm.Separator)$runtime" "com.liuzhuan.checks.$check" $OutputDirectory
 if($LASTEXITCODE -ne 0){throw "$check failed"}
}
