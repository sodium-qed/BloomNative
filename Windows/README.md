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

## If the artwork download returns HTTP 403

HTTP 403 means the creator's server refused the app's download request. Choose **Open video in browser** to open the [original animation](https://sixnfive.com/wp-content/uploads/2021/07/curls_hero_05_anim_19_light2.mp4) in your normal browser. If the browser can access it, save the original video, then return to Bloom Native and choose **Use a local copy…** to import that file. Import checks the same SHA-256 as the app download; a different animation, converted video, or saved error page will be rejected.

Browser access is not guaranteed. If the source also refuses your browser, you can use an existing matching copy of the original animation.

## If the preview works but the desktop still shows the old wallpaper

Version **0.1.2** changes desktop-layer placement for Explorer layouts where Bloom could be attached successfully but remain covered by Windows' existing wallpaper. This change still needs visual confirmation on affected Windows devices. The download handling and browser/local-copy fallback from 0.1.1 are also included.

To check the new build:

1. **Quit** the old build from its notification-area menu. Extract the new bundle into a separate folder, start `BloomNative.Windows.exe`, and confirm the title shows **Windows 0.1.2**. Previously downloaded artwork is reused.
2. Enable **Dynamic desktop wallpaper** and choose **Replay unfolding**. Minimize the controls or press **Win+D** to view the desktop. Check that Bloom is visible on every monitor and that the replay changes the desktop image, as well as the preview.
3. Click a desktop icon and open an ordinary application window. Icons should remain usable and application windows should appear above Bloom. Disable the wallpaper and confirm that your original Windows wallpaper becomes visible again.
4. If Bloom is still hidden, return to the controls and choose **Copy diagnostics**. Include that report with your Windows version, monitor count, display scaling, and whether the preview was working. The report summarizes the desktop window hierarchy and monitor geometry to help identify the layer being used.

An enabled checkbox or successful attachment status alone does not confirm that the animation is visible. The desktop check above is required to confirm this issue is resolved on your machine.

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

- Attaching wallpaper behind desktop icons uses Explorer's **undocumented desktop-window hierarchy (Progman/WorkerW)**. Windows updates, alternative shells, Explorer restarts, and mixed-DPI monitor layouts may affect it. If attachment fails or the wallpaper remains hidden, use **Copy diagnostics** and include the report with your Windows version and monitor setup.
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

This initial port was developed in a Linux environment, where its Windows GUI cannot run. CI is configured to run the core checks, publish both architectures, and execute an x64 WPF self-test without fetching artwork. The self-test includes an owned HWND fixture that exercises the production window-creation and attachment methods: layered rendering configuration after WPF initialization, parent and stacking order, position, detection of a covered surface, and cleanup. It does not attach to the real Explorer desktop or verify video playback. ARM64 is built but is not executed on the x64 CI runner. Interactive desktop and screen-saver validation is separate from these checks; run the following matrix on Windows before treating a configuration as verified:

| Manual check | Expected result |
| --- | --- |
| Fresh launch, then user-initiated artwork download | No automatic artwork download; successful download is checksum-validated. |
| Artwork server returns HTTP 403 | Clear refusal message with browser/local-import guidance; no artwork is installed from the failed response. |
| Import a matching original video, a different file, and a missing file | Matching original is accepted; incorrect or missing files report an error without replacing installed artwork. |
| Manual position and replay | Position updates; replay unfolds toward the selected position. |
| Working preview with desktop wallpaper enabled; controls minimized or Win+D pressed | Bloom is visibly animated on the desktop, above the existing Windows wallpaper and below desktop icons. Attachment status alone is insufficient. |
| Desktop icons and ordinary application windows | Icons remain clickable and application windows stay above the wallpaper. |
| Disable wallpaper after a successful desktop replay | The existing Windows wallpaper reappears without changing its system setting. |
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

若下载返回 HTTP 403，可选择在浏览器中打开原动画；如果浏览器可以访问，请保存原视频，再通过“选择本地副本…”导入。导入仍会校验同一 SHA-256。浏览器也可能被拒绝；此时只能使用已有的匹配原视频副本，应用无法保证源文件可访问。

若预览正常但桌面仍显示旧壁纸，请先从托盘退出旧版本，再启动标题显示 **Windows 0.1.2** 的新版本。启用动态壁纸并重新播放，最小化控制窗口或按 **Win+D**，确认桌面上的 Bloom 确实可见、图标仍可点击。此改动仍需在受影响的 Windows 设备上确认；若依然不可见，请复制诊断信息，并提供显示器数量和缩放设置。

桌面背景使用 Explorer 的非公开接口，需在实际 Windows 设备上检查多显示器、缩放、唤醒及屏保行为。关闭控制窗口后可通过托盘图标重新打开，或退出应用。
