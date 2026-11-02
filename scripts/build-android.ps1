param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$AndroidHome = $env:ANDROID_HOME,
    [switch]$DeviceTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'android'
if (-not $AndroidHome) { $AndroidHome = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
if (-not (Test-Path -LiteralPath $AndroidHome)) { throw '请安装 Android SDK 35 并设置 ANDROID_HOME。' }
$java = if ($JavaHome) { Join-Path $JavaHome 'bin\java.exe' } else { (Get-Command java -ErrorAction Stop).Source }
if (-not (Test-Path -LiteralPath $java)) { throw '请安装 JDK 17 并设置 JAVA_HOME。' }
$env:ANDROID_HOME = $AndroidHome
$tasks = @(':app:assembleDebug', ':app:testDebugUnitTest', ':app:lintDebug')
if ($DeviceTests) { $tasks += ':app:connectedDebugAndroidTest' }
# Invoke the wrapper directly so cmd.exe does not discard an UNC working directory.
& $java -classpath (Join-Path $project 'gradle\wrapper\gradle-wrapper.jar') org.gradle.wrapper.GradleWrapperMain -p $project @tasks --console=plain
if ($LASTEXITCODE -ne 0) { throw "Android 构建或验证失败：$LASTEXITCODE" }
$output = Join-Path $repo 'artifacts\android'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'app\build\outputs\apk\debug\app-debug.apk') -Destination (Join-Path $output 'DeskMonitor-android-debug.apk') -Force
Write-Host "APK: $(Join-Path $output 'DeskMonitor-android-debug.apk')"
