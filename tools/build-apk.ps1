# 手工编译 Android APK —— 借用 Unity 附带的 SDK/JDK，不走 Gradle、不联网
# 已知坑与对策：
#   1) build-tools 的 d8.bat 用了 JDK 9 已删除的 -Djava.ext.dirs -> 直接调 d8.jar
#   2) D8 的输出目录必须预先存在，否则报 Invalid output
#   3) aapt2 不支持中文路径 -> 整个编译在 ASCII 临时目录里做，产物再拷回来
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$ErrorActionPreference = 'Stop'

$root   = Split-Path $PSScriptRoot -Parent
$appdir = Join-Path $root 'app'
$AP  = 'D:\Unity\2023.1.22f1\Editor\Data\PlaybackEngines\AndroidPlayer'
$BT  = Join-Path $AP 'SDK\build-tools\32.0.0'
$JDK = Join-Path $AP 'OpenJDK'
$AJAR= Join-Path $AP 'SDK\platforms\android-33\android.jar'
$JAVA= Join-Path $JDK 'bin\java.exe'
$env:JAVA_HOME = $JDK

$MIN_SDK = 23
$TARGET_SDK = 33

$stage = Join-Path $env:TEMP 'pcaudio_build'
$gen   = Join-Path $stage 'gen'
$cls   = Join-Path $stage 'classes'
$dex   = Join-Path $stage 'dexout'
$final = Join-Path $root 'PcAudio.apk'

foreach($p in @($BT,$JDK,$AJAR)){ if(-not (Test-Path $p)){ throw "工具链缺失: $p" } }

function Step($n,$m){ Write-Host "[$n] $m" -ForegroundColor Cyan }
function Chk($w){ if($LASTEXITCODE -ne 0){ throw "$w 失败 (exit=$LASTEXITCODE)" } }

Step 0 '准备 ASCII 编译目录（绕开 aapt2 的中文路径问题）'
if(Test-Path $stage){ Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage,$gen,$cls,$dex | Out-Null
Copy-Item (Join-Path $appdir 'AndroidManifest.xml') $stage -Force
Copy-Item (Join-Path $appdir 'res') (Join-Path $stage 'res') -Recurse -Force
Copy-Item (Join-Path $appdir 'src') (Join-Path $stage 'src') -Recurse -Force

Step 1 'aapt2 compile 资源'
& "$BT\aapt2.exe" compile --dir (Join-Path $stage 'res') -o (Join-Path $stage 'res.zip')
Chk 'aapt2 compile'

Step 2 'aapt2 link 生成基础 APK'
& "$BT\aapt2.exe" link -o (Join-Path $stage 'base.apk') -I $AJAR --manifest (Join-Path $stage 'AndroidManifest.xml') -R (Join-Path $stage 'res.zip') --java $gen --min-sdk-version $MIN_SDK --target-sdk-version $TARGET_SDK --auto-add-overlay
Chk 'aapt2 link'

Step 3 'javac 编译 Java'
$srcs = @(Get-ChildItem (Join-Path $stage 'src') -Recurse -Filter *.java | ForEach-Object { $_.FullName })
$srcs += @(Get-ChildItem $gen -Recurse -Filter *.java -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
& "$JDK\bin\javac.exe" --release 8 -encoding UTF-8 -classpath $AJAR -d $cls $srcs
Chk 'javac'
Write-Host ("    生成 {0} 个 .class" -f (Get-ChildItem $cls -Recurse -Filter *.class).Count)

Step 4 'd8: class -> dex（直接调 jar，绕过坏掉的 d8.bat）'
$classFiles = @(Get-ChildItem $cls -Recurse -Filter *.class | ForEach-Object { $_.FullName })
& $JAVA -cp "$BT\lib\d8.jar" com.android.tools.r8.D8 --min-api $MIN_SDK --lib $AJAR --output $dex $classFiles
Chk 'd8'
$dexFile = Join-Path $dex 'classes.dex'
if(-not (Test-Path $dexFile)){ throw 'classes.dex 未生成' }
Write-Host ("    classes.dex = {0} bytes" -f (Get-Item $dexFile).Length)

Step 5 '把 classes.dex 打进 APK'
$unsigned = Join-Path $stage 'unsigned.apk'
Copy-Item (Join-Path $stage 'base.apk') $unsigned -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($unsigned,'Update')
[void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$dexFile,'classes.dex',[System.IO.Compression.CompressionLevel]::Optimal)
$zip.Dispose()

Step 6 'zipalign'
$aligned = Join-Path $stage 'aligned.apk'
& "$BT\zipalign.exe" -p -f 4 $unsigned $aligned
Chk 'zipalign'

Step 7 'apksigner 签名'
$ks = Join-Path $PSScriptRoot 'pcaudio.keystore'
if(-not (Test-Path $ks)){
    Write-Host '    首次运行，生成签名密钥...'
    & "$JDK\bin\keytool.exe" -genkeypair -keystore $ks -storepass pcaudio -keypass pcaudio -alias pcaudio -keyalg RSA -keysize 2048 -validity 10950 -dname 'CN=PcAudio,O=DSH,C=CN' 2>&1 | Out-Null
    Chk 'keytool'
}
$signed = Join-Path $stage 'PcAudio.apk'
& "$BT\apksigner.bat" sign --ks $ks --ks-pass pass:pcaudio --key-pass pass:pcaudio --out $signed $aligned
Chk 'apksigner 签名'

Step 8 '验证签名'
$verifyOut = & "$BT\apksigner.bat" verify --verbose $signed
$verifyCode = $LASTEXITCODE
$verifyOut | Select-Object -First 5
if($verifyCode -ne 0){ throw "apksigner verify 失败 (exit=$verifyCode)" }

Step 9 '拷回项目目录'
Copy-Item $signed $final -Force
& "$BT\aapt2.exe" dump badging $signed | Select-Object -First 2

Write-Host ''
Write-Host ('构建成功: ' + $final) -ForegroundColor Green
Write-Host ('大小: {0:N0} bytes' -f (Get-Item $final).Length) -ForegroundColor Green