# 打包发布：把独立 exe、APK、adb、源码、说明收进一个文件夹，拷到别的机器就能用
#
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1
#
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$ErrorActionPreference = 'Stop'

$root  = Split-Path $PSScriptRoot -Parent
$dist  = Join-Path $root '发布包'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Step($m){ Write-Host $m -ForegroundColor Cyan }

# ---------- 0. 准备干净的目录 ----------
Step '[1/6] 准备目录'
# 先放开占用：桌面端进程和 adb server 会锁住发布包里的文件，直接删会 Access denied
try {
    Get-Process | Where-Object { $_.ProcessName -match '电脑声音|PcAudioServer' } |
        Stop-Process -Force -ErrorAction SilentlyContinue
} catch {}
try {
    $oldAdb = Join-Path $dist 'adb\adb.exe'
    if(Test-Path $oldAdb){ & $oldAdb kill-server 2>$null | Out-Null }
} catch {}
Start-Sleep -Milliseconds 900
if(Test-Path $dist){ Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# ---------- 1. 编译 Android APK ----------
Step '[2/6] 编译 Android APK'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-apk.ps1')
if(-not (Test-Path (Join-Path $root 'PcAudio.apk'))){ throw 'APK 未生成' }
Copy-Item (Join-Path $root 'PcAudio.apk') $dist -Force

# ---------- 2. 发布独立单文件 exe ----------
Step '[3/6] 发布独立单文件 exe（自带 .NET 运行时，目标机不用装任何东西）'
$pub = Join-Path $root '_pub'
if(Test-Path $pub){ Remove-Item $pub -Recurse -Force }
& dotnet publish (Join-Path $root 'pc\PcAudioServer.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $pub
if($LASTEXITCODE -ne 0){ throw 'PC 端发布失败' }
# exe 直接放根目录，双击就跑 —— 不再需要点 .bat
Copy-Item (Join-Path $pub 'PcAudioServer.exe') (Join-Path $dist '电脑声音.exe') -Force

# ---------- 3. 带一份 adb（手机端要靠它拉起 App 和建 USB 隧道）----------
Step '[4/6] 收集 adb'
$adbSrc = @(
    'D:\scrcpy-win64-v3.3.3',
    'D:\Unity\2023.1.22f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools'
) | Where-Object { Test-Path (Join-Path $_ 'adb.exe') } | Select-Object -First 1

if($adbSrc){
    $adbDir = Join-Path $dist 'adb'
    New-Item -ItemType Directory -Force -Path $adbDir | Out-Null
    foreach($f in @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll')){
        $src = Join-Path $adbSrc $f
        if(Test-Path $src){ Copy-Item $src $adbDir -Force }
    }
    Write-Host ('     已收录: ' + $adbSrc)
} else {
    Write-Host '     没找到 adb.exe，包内将不含 adb（启动器会自动去找系统里的）' -ForegroundColor Yellow
}

# ---------- 4. 拷贝源码 ----------
Step '[5/6] 拷贝源码'
New-Item -ItemType Directory -Force -Path (Join-Path $dist '源码') | Out-Null
robocopy (Join-Path $root 'pc')    (Join-Path $dist '源码\pc')    /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
robocopy (Join-Path $root 'app')   (Join-Path $dist '源码\app')   /E /NFL /NDL /NJH /NJS /NP | Out-Null
robocopy (Join-Path $root 'tools') (Join-Path $dist '源码\tools') /E /NFL /NDL /NJH /NJS /NP | Out-Null

# ---------- 5. 启动器 + 说明 ----------
Step '[6/6] 写说明文件'


$readme = @'
把电脑声音发送到手机
====================

一、包内有什么
  电脑声音.exe            桌面端（独立单文件，自带 .NET 运行时）。**双击它就行**，装 App、
  PcAudio.apk             手机端安装包
                          建 USB 通道、拉起手机端、开服务全都自动做了
  adb\                    手机调试工具（启动器会优先用包内这一份）
  源码\                   两端完整源码，可自行改再编译
  settings2.txt           启动器记住的选择

二、最快用法
  1. 手机开「开发者选项」和「USB 调试」
  2. 插数据线，双击「电脑声音.exe」
  3. 手机弹出「允许 USB 调试」时点允许
  4. 手机端 App 显示「已连接 · 收到 100 帧/秒」就有声音了
  首次 USB 启动会让 adb 开一下 TCP/IP（USB 会掉几秒），属正常。

三、不插线也能用（Wi-Fi）
  先用 USB 跑一次，启动器会把手机和电脑的局域网地址各记一份；
  之后拔线也照样能用：程序会自己发现 adb 里的 Wi-Fi 设备并切过去。

四、关心的几个参数
  编码    Opus 压缩  —— 省带宽、延迟低，默认
          无损 PCM   —— 不压缩直传，音质无损、延迟最低，但 1536 kbps
                        USB 完全没压力，Wi-Fi 要看信号
  码率    只对 Opus 有效。实测：
            96 kbps 及以上 = 高频完整（听感透明）
            64 kbps        = 14kHz 掉 16dB，明显发闷
            32 kbps 以下   = 只够听语音
          所以除非特意省流量，别低于 96 kbps。
  缓冲    25 / 40 / 80 / 150 ms，越小延迟越低、越大越抗卡。
          注意：手机输出若是蓝牙，App 会自动把缓冲放宽到 250ms ——
          蓝牙链路固有延迟就有 100~300ms，用 25ms 会一直饿死、听感发糊。

五、两端设置谁说了算
  规则：**电脑端是权威，手机端是遥控器。**
  · 手机连上后一律以电脑端实际在发的为准，手机界面跟着刷新；
  · 手机上点按钮才会发命令去改电脑端，改完电脑端界面也会同步显示；
  · 在电脑端改任何参数，都会清掉手机那边的临时覆盖，以电脑端为准。
  所以两端永远显示同一个状态，不会出现"两边设置对不上"。

六、后台运行
  桌面端可以缩到系统托盘后台跑：
  · 点右上角 X = 缩到托盘，服务继续运行（不是退出）
  · 双击托盘图标 = 重新打开界面
  · 托盘右键 = 显示主界面 / 启动服务 / 停止服务 / 退出
  · 启动器默认带 --minimized，跑起来就直接在托盘里

七、声音从哪出来
  跟随手机系统：接了蓝牙耳机/音箱就从蓝牙出，否则走手机扬声器。
  蓝牙 A2DP 会再压一次（SBC/AAC），若觉得"蒙"，那是蓝牙那一段，
  可断开蓝牙做对比。

八、常见问题
  · 提示"adb reverse 隧道没建立成功，本次会走 Wi-Fi"
      adb reverse 被 adbd 重启冲掉了，重新插拔数据线再跑一次。
  · 手机一直"正在搜索电脑"
      确认手机电脑在同一局域网；VPN/代理软件可能拦截 UDP 广播。
  · 提示"USB隧道连不上，已改走 Wi-Fi"
      同上。App 也会明确告诉你它到底在用哪条路。
  · 服务端重装后仍报旧版本
      启动器按 APK 的 MD5 判重，换了 APK 会自动重装。

九、自己改代码
  电脑端：源码\pc\    (.NET 9 + WinForms，用 tools\build-server.ps1 编译)
  手机端：源码\app\   (不用 Gradle，tools\build-apk.ps1 借 Unity 的 SDK 手编)
  重新打包：源码\tools\make-release.ps1
'@
[System.IO.File]::WriteAllText((Join-Path $dist '使用说明.txt'), $readme, (New-Object System.Text.UTF8Encoding($false)))

# settings2.txt: 默认「自动 / 96kbps / 40ms」—— 96k 是实测不会发闷的下限
'1 4 2' | Set-Content (Join-Path $dist 'settings2.txt') -Encoding ASCII

# ---------- 汇总 ----------
Write-Host ''
Write-Host '================ 打包完成 ================' -ForegroundColor Green
$total = 0
Get-ChildItem $dist -Recurse -File | ForEach-Object { $total += $_.Length }
Get-ChildItem $dist | Select-Object Mode, @{n='大小';e={ if($_.PSIsContainer){''}else{'{0:N0}' -f $_.Length} }}, Name | Format-Table -AutoSize
Write-Host ('总大小: {0:N1} MB   位置: {1}' -f ($total/1MB), $dist) -ForegroundColor Green