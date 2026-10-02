# Bloom Native for Windows

A Windows port of Bloom Native using WPF and the Windows media stack. The Windows app is a separate implementation; the SwiftUI/Metal app remains the macOS version.

## Requirements

- Windows 10 or Windows 11, x64 or ARM64, with a working Windows media stack. Windows N editions may require Microsoft's Media Feature Pack.
- For building: the **.NET 8 SDK** or newer, including Windows desktop targeting support.
- Published bundles contain the .NET runtime. Users do not need to install the SDK or runtime to run a bundle.
- Internet access is needed only when you choose to download the original animation in the app. Builds and automated checks do not download it.

## Build and run

From a checkout of this repository, run PowerShell on Windows:

```powershell
./Windows/build.ps1
./dist/windows/win-x64/BloomNative.Windows.exe
```

The default bundle targets x64. To build both supported architectures:

```powershell
./Windows/build.ps1 -Runtime @('win-x64', 'win-arm64')
```

Each bundle appears in `dist/windows/<runtime>/`, with an archive at `dist/windows/BloomNative-Windows-<runtime>.zip`. Choose `win-arm64` for native Windows on ARM. The archive includes the application, a `.scr` screen-saver entry point, this README, the source license, and artwork provenance. It contains **no animation or downloaded artwork**.

GitHub Actions also builds both bundles in the **Windows build and checks** workflow. After a successful run, download its **BloomNative-Windows** artifact and extract the archive for your architecture. These are unsigned builds; Windows may show an unknown-publisher prompt.

For development without packaging:

```powershell
dotnet run --project Windows/BloomNative.Windows.csproj
```

## Using the app

1. Start `BloomNative.Windows.exe`. Use the download control to fetch the original animation from its creator, or import an existing copy of that animation. The app validates it against the SHA-256 recorded in `BloomOriginal.source.json` before using it. Artwork and settings are stored under `%LOCALAPPDATA%\BloomNative`.
2. Enable the desktop wallpaper. The app places a separate animated surface behind the icons on each monitor.
3. Set the unfolding position with the manual slider, or replay the unfolding animation. Wake replay and gentle breathing can be controlled in settings.
4. Choose English or 简体中文 in the language setting. Settings are remembered between launches.
5. Closing the controls hides them while the wallpaper keeps running. Use the notification-area icon to reopen the controls or exit the app.

The Windows version does not read a continuous laptop hinge angle. Its manual position control and wake replay replace the Mac-specific HID sensor path. It does not overwrite your system wallpaper or create lock-screen snapshots. Exiting removes its desktop surfaces and exposes your existing wallpaper.

## Screen saver

Keep the extracted bundle in a permanent folder. Run the main app and download the artwork before using the screen saver. Right-click `BloomNative.scr` to test, configure, or install it through Windows. Installation here means selecting the screen saver in Windows settings; moving or deleting its file afterward can break that selection.

The screen saver supports the standard Windows command modes:

| Command | Behavior |
| --- | --- |
| `BloomNative.scr /s` | Full-screen screen saver; user input exits it. |
| `BloomNative.scr /c` | Configuration window. |
| `BloomNative.scr /p <HWND>` | Preview embedded in the window supplied by Windows. |

Windows controls password protection and sign-in policy. The app does not implement a replacement lock screen. The `.scr` file is a copy of the same executable, packaged separately so Windows recognizes it as a screen saver.

## Compatibility and limits

- Attaching wallpaper behind desktop icons uses Explorer's **undocumented WorkerW behavior**. Windows updates, alternative shells, Explorer restarts, and mixed-DPI monitor layouts may affect it. If attachment fails, use the controls to disable wallpaper and retry; report your Windows version and monitor setup.
- Rendering and seeking use Windows video decoding, rather than the Mac app's predecoded Metal textures. Exact frame timing and smoothness depend on the decoder and graphics hardware.
- Desktop animation pauses around sleep/lock transitions. Screen-saver behavior, wake replay, monitor changes, and Explorer recovery require interactive testing on actual Windows machines.
- WPF and Windows media playback are required. A build or command-line self-test is not evidence that wallpaper attachment or video playback works on every Windows configuration.

## Verification

Run the core checks on any system with the .NET 8 SDK:

```powershell
dotnet run --project Windows/Tests/BloomNative.Checks.csproj --configuration Release
```

On Windows, build first and run the published application self-test:

```powershell
$resultPath = Join-Path $env:TEMP 'bloom-windows-self-test.json'
$process = Start-Process -FilePath './dist/windows/win-x64/BloomNative.Windows.exe' -ArgumentList @('--self-test', "`"$resultPath`"") -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Application self-test failed.' }
Get-Content -LiteralPath $resultPath
```

This initial port was developed in a Linux environment, where its Windows GUI cannot run. CI is configured to run the core checks, publish both architectures, and execute an x64 WPF startup self-test without fetching artwork. ARM64 is built but is not executed on the x64 CI runner. The self-test does not verify video playback or desktop integration. Interactive desktop and screen-saver validation is separate from these checks; run the following matrix on Windows before treating a configuration as verified:

| Manual check | Expected result |
| --- | --- |
| Fresh launch, then user-initiated artwork download | No automatic artwork download; successful download is checksum-validated. |
| Manual position and replay | Position updates; replay unfolds toward the selected position. |
| Desktop icons and ordinary application windows | Icons remain clickable and application windows stay above the wallpaper. |
| Two monitors, negative monitor coordinates, mixed DPI | Each display is covered correctly without shifting or covering controls. |
| Add/remove a monitor, then restart Explorer | Wallpaper recovers or reports that desktop attachment is unavailable. |
| Lock/unlock and sleep/resume | Animation pauses and resumes appropriately; configured wake replay occurs. |
| Breathing off and Windows animation effects off | No breathing when either setting disables it. |
| Close controls, reopen from tray, then exit | Closing hides controls; exit removes desktop surfaces and the tray icon. |
| Switch English/简体中文 and restart | Controls change language and the preference persists. |
| Windows screen-saver Configure, Preview, and full-screen Test | Correct settings window, embedded preview, and full-screen behavior. |
| Full-screen screen saver and keyboard/mouse input | Screen saver exits; Windows retains sign-in protection. |
| Windows x64 and native Windows ARM64 | Correct architecture bundle starts and plays the animation. |

## Artwork and source licenses

The application source is MIT-licensed; see `LICENSE`. The original Bloom animation belongs to **Microsoft / Six N. Five** and is excluded from that license. No permission to redistribute it is asserted. The app downloads the animation to the user's machine only when requested, using the source URL and SHA-256 in `BloomOriginal.source.json`.

- Creator: <https://sixnfive.com/projects/windows-11/>
- Original animation: <https://sixnfive.com/wp-content/uploads/2021/07/curls_hero_05_anim_19_light2.mp4>
- SHA-256: `01e08e7efd67574db59352a3cb8be79aeb8e65120bb8aba2f27047e501d5bb75`

## 简体中文

Windows 版本使用 WPF，支持手动控制展开位置、唤醒时重新播放、轻微呼吸动画、托盘控制及屏幕保护程序。不读取连续的笔记本屏幕开合角度，也不覆盖系统壁纸或锁屏图片。

在 Windows PowerShell 中运行 `./Windows/build.ps1`，然后启动 `dist/windows/win-x64/BloomNative.Windows.exe`。Windows ARM64 可使用 `./Windows/build.ps1 -Runtime win-arm64`。首次使用时，请在应用中点击下载原动画；构建和发布包均不包含该素材。界面可切换 **English / 简体中文**。

桌面背景使用 Explorer 的非公开接口，需在实际 Windows 设备上检查多显示器、缩放、唤醒及屏保行为。关闭控制窗口后可通过托盘图标重新打开，或退出应用。
