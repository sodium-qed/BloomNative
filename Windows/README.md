# Bloom Native for Windows

A Windows port of Bloom Native using WPF and the Windows media stack. The Windows app is a separate implementation; the SwiftUI/Metal app remains the macOS version.

## Requirements

- Windows 10 **version 2004 (build 19041) or later**, or Windows 11, on x64 or ARM64, with a working Windows media stack. Windows N editions may require Microsoft's Media Feature Pack.
- For building: the **.NET 8 SDK** or newer, including Windows desktop targeting support.
- Published bundles contain the .NET runtime. Users do not need to install the SDK or runtime to run a bundle.
- Internet access is needed only when you choose to download the original animation in the app. Builds and automated checks do not download it.
- The optional webcam experiment needs a working camera and Windows camera permission. It uses Windows' native camera APIs; no third-party native camera DLL is bundled.

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
5. Closing the main controls hides them while the wallpaper keeps running. Use the notification-area icon to reopen the controls or exit the app. The optional camera window is separate; closing the main controls does not stop an active camera.

The Windows version does not read a continuous laptop hinge angle. Manual position control and wake replay work without a camera. The optional webcam experiment below maps visual motion to the animation; it does not measure hinge degrees. The app does not overwrite your system wallpaper or create lock-screen snapshots. Exiting removes its desktop surfaces and exposes your existing wallpaper.

## Experimental webcam control (introduced in 0.1.3)

This experiment uses a selected background feature in the webcam image to estimate lid movement. Keep the laptop base fixed, move the lid slowly, and use a stationary, well-lit, textured target that stays visible and recognizable through a modest lid movement. A built-in camera must move with the lid for this to respond to lid movement; an external camera sitting on the desk will not do that. Moving the laptop, target, or camera independently can change the estimate. A person, changing screen image, blank wall, or reflective surface makes a poor target. Large or fast movements can lose the target.

The two calibration positions represent **0% and 100% of the Bloom animation**, not physical hinge angles. Choose two comfortable positions with the camera unobstructed; you do not need to close the laptop to capture the folded endpoint. The result is an experimental relative control, with no claim of angle accuracy. Physical-camera and laptop-hinge behavior has not yet been verified on hardware.

1. Choose **Webcam tracking (experimental)…**, select a camera, and press **Start camera**. Allow camera access in Windows if prompted; desktop-app camera access must also be enabled in Windows camera privacy settings. The camera never starts automatically. Use **Refresh cameras** after connecting a device if it is not listed.
2. Hold the lid at your chosen folded animation endpoint. In the live preview, drag a rectangle around a small stationary, textured feature.
3. Press **Capture folded (0%)**, then slowly move only the lid toward your chosen unfolded endpoint while keeping that feature visible.
4. Press **Capture unfolded (100%)**. Successful calibration starts tracking and maps the selected visual motion to Bloom's unfolding position. Calibrated tracking takes over the unfolding control: the manual slider and replay are disabled, and breathing pauses so a held position stays still.
5. If the target is lost or confidence becomes too low, the unfolding position holds. There is no automatic target reacquisition. Select the target again and capture both endpoints to resume tracking. Recalibrate after moving the laptop base, changing the target, or changing the camera setup.
6. Press **Stop camera**, or close the camera window, to release the camera before returning to manual unfolding or replay. **Reset calibration** lets you choose new endpoints while the camera remains running.

The preview is unmirrored. The camera may remain active while its window is minimized or the main controls are hidden; its camera window title and status indicate that it is on. Close the camera window or choose its stop control when you are done. Locking the session, sleep, display-off, lid-close notifications, and quitting the app also stop capture. These stops clear calibration, and unlocking or waking does **not** restart the camera. Start it and calibrate again explicitly. Hardware and Windows determine which lid and power notifications are available.

Camera processing is local and video-only: this feature does not use the microphone, save photos or recordings, or send camera frames over the network. Preview frames, the selected target, and calibration remain in memory and are cleared when the camera stops. Camera selection and calibration are not persisted between sessions. The app's separate, user-requested artwork download still uses the network.

## If the artwork download returns HTTP 403

HTTP 403 means the creator's server refused the app's download request. Choose **Open video in browser** to open the [original animation](https://sixnfive.com/wp-content/uploads/2021/07/curls_hero_05_anim_19_light2.mp4) in your normal browser. If the browser can access it, save the original video, then return to Bloom Native and choose **Use a local copy…** to import that file. Import checks the same SHA-256 as the app download; a different animation, converted video, or saved error page will be rejected.

Browser access is not guaranteed. If the source also refuses your browser, you can use an existing matching copy of the original animation.

## If the preview works but the desktop still shows the old wallpaper

Version **0.1.5** includes the desktop composition fix introduced in 0.1.4, plus display-geometry checks and recovery fixes. For modern Explorer layouts, it puts a normal opaque WPF rendering window inside a native layered host with constant alpha 255 (fully opaque). This avoids relying on WPF's per-pixel transparent-window rendering under Explorer's non-redirection window hierarchy. It still needs visual confirmation on affected Windows devices. Experimental webcam control and the artwork browser/local-copy fallback remain included.

This update also preserves the requested wallpaper state across temporary Explorer failures, retries with a bounded delay, detects surfaces that have moved or become clipped, and safely handles native windows destroyed during a shell restart. Turning the wallpaper off cancels recovery. Downloads now have a deadline covering the whole transfer, and imports verify a staged copy before replacing existing artwork. Initial artwork verification finishes before download/import controls become available.

To check the new build:

1. **Quit** the old build from its notification-area menu. Extract the new bundle into a separate folder, start `BloomNative.Windows.exe`, and confirm the title shows **Windows 0.1.5**. Previously downloaded artwork is reused.
2. Enable **Dynamic desktop wallpaper** and choose **Replay unfolding**. Minimize the controls or press **Win+D** to view the desktop. Check that Bloom is visible on every monitor and that the replay changes the desktop image, as well as the preview.
3. Click a desktop icon and open an ordinary application window. Icons should remain usable and application windows should appear above Bloom. Disable the wallpaper and confirm that your original Windows wallpaper becomes visible again.
4. If Bloom is still hidden, keep **Dynamic desktop wallpaper** enabled and click **Test desktop layer**, then press **Win+D**. Look for the magenta/cyan **BLOOM DESKTOP TEST** pattern behind your icons on each desktop. This affects the desktop surfaces only. Click **Test desktop layer** again to restore the video. A visible pattern with missing video points toward media rendering; an invisible pattern also leaves desktop-host visibility unresolved.
5. Open **Diagnostics** from the notification-area menu, or choose **Copy diagnostics** in the controls. If the clipboard is unavailable, use the selectable report in the diagnostics window. Include the report with your Windows version, monitor count, display scaling, whether the preview was working, and whether the test pattern appeared. The report includes the test-pattern state and last desktop error, along with desktop-window hierarchy and monitor geometry.

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
- Webcam control is experimental image tracking, not a replacement for a hardware hinge sensor. Camera permissions, camera sharing, lighting, motion, occlusion, and Windows power notifications require physical-device testing. Manual controls remain available without camera access.

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

This initial port was developed in a Linux environment, where its Windows GUI cannot run. CI is configured to run the core checks, publish both architectures, and execute an x64 WPF self-test without fetching artwork. ARM64 is built but is not executed on the x64 CI runner.

The desktop self-test briefly displays windows owned by the test, including a non-redirection parent modeled on modern Explorer and an ordinary parent for the classic path. It exercises the production native host and WPF child, reads actual on-screen pixels at fixed points inside its own fixture, and checks visible color changes, a marker above the wallpaper, intentional covering by a simulated stock wallpaper, restoration after removal, placement, and cleanup. The screen-pixel checks require an interactive Windows desktop with DWM composition. The self-test fails if composition is unavailable.

A separate media check writes a tiny generated red/cyan H.264 test clip to a temporary file. It first checks an ordinary MediaElement, then verifies decoded frames and seeking through the production BloomView under both desktop host layouts. If the ordinary decoder is unavailable, the JSON report explicitly records `mediaComposition.verified=false` and the reason; that is not a media-validation pass. Once the baseline succeeds, incorrect production video pixels fail the self-test. The clip contains no creator artwork and its temporary file is removed afterward.

These fixtures do not attach to the real Explorer desktop or validate Explorer's discovery behavior, actual desktop-icon transparency, the original animation, or physical-device rendering. Separate checks verify that the required Windows camera APIs are present and that in-memory camera pixel conversion works, without opening a camera. Passing these tests does not establish camera compatibility or confirm the wallpaper is visible on an affected user's device. Interactive desktop, screen-saver, and camera validation remain necessary; run the following matrix on Windows before treating a configuration as verified:

| Manual check | Expected result |
| --- | --- |
| Fresh launch, then user-initiated artwork download | No automatic artwork download; successful download is checksum-validated. |
| Artwork server returns HTTP 403 | Clear refusal message with browser/local-import guidance; no artwork is installed from the failed response. |
| Import a matching original video, a different file, and a missing file | Matching original is accepted; incorrect or missing files report an error without replacing installed artwork. |
| Manual position and replay | Position updates; replay unfolds toward the selected position. |
| Working preview with desktop wallpaper enabled; controls minimized or Win+D pressed | Bloom is visibly animated on the desktop, above the existing Windows wallpaper and below desktop icons. Attachment status alone is insufficient. |
| Wallpaper enabled; toggle Test desktop layer and press Win+D | Magenta/cyan BLOOM DESKTOP TEST appears behind icons on every desktop while the controls preview is unchanged. Toggling the test off restores the video. |
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

The webcam experiment additionally needs the following device checks. Passing synthetic motion checks or compiling the capture code does not establish camera compatibility or hinge-tracking accuracy.

| Webcam device check | Expected result |
| --- | --- |
| Fresh launch, open camera window, and select a device | Camera remains off until explicitly started. |
| Camera permission denied; camera already in use; no camera present | Clear status or error; no crash or unexpected capture; manual wallpaper controls remain usable. |
| Start the selected camera | The selected device supplies an unmirrored preview and the camera window indicates active capture. |
| Fixed laptop base, stationary textured target, two modest lid positions | Accepted endpoints map to folded/unfolded animation positions without claiming physical degrees. |
| Successful calibration, then target loss | Manual unfolding and replay remain disabled while tracking has control; breathing stays paused and the last accepted position holds. Stop the camera to return to manual control. |
| Blank target or endpoints too similar to calibrate | Calibration is rejected with guidance instead of starting unreliable control. |
| Cover the lens, move the target out of view, or change lighting sharply | Low-confidence or lost tracking holds the current animation position; reselecting and recalibrating is required after loss. |
| Minimize the camera window; hide the main controls | Capture may continue, and the camera window's active title remains identifiable on the taskbar. |
| Reset calibration | Camera preview continues; unfolding control awaits a new calibration. |
| Stop camera or close its window, then reopen it | Camera is released, prior frames/calibration are cleared, and capture does not restart automatically. |
| Lock/unlock, sleep/resume, display off/on, supported lid close/open | Capture stops and remains off after return; explicitly starting it again requires new calibration. |
| Disconnect a USB camera while running | Capture ends or reports failure and releases resources; no automatic restart. |
| Quit the app during capture or camera startup | Capture stops and the process exits without leaving the camera active. |
| Select a different camera after stopping | Only the newly selected device starts; previous calibration is not reused. |

## Artwork and source licenses

The application source is MIT-licensed; see `LICENSE`. The original Bloom animation belongs to **Microsoft / Six N. Five** and is excluded from that license. No permission to redistribute it is asserted. The app downloads the animation to the user's machine only when requested, using the source URL and SHA-256 in `BloomOriginal.source.json`.

- Creator: <https://sixnfive.com/projects/windows-11/>
- Original animation: <https://sixnfive.com/wp-content/uploads/2021/07/curls_hero_05_anim_19_light2.mp4>
- SHA-256: `01e08e7efd67574db59352a3cb8be79aeb8e65120bb8aba2f27047e501d5bb75`

## 简体中文

Windows 版本使用 WPF，要求 Windows 10 2004（19041）及以上或 Windows 11，支持手动控制展开位置、唤醒时重新播放、轻微呼吸动画、托盘控制及屏幕保护程序。不读取连续的笔记本屏幕开合角度，也不覆盖系统壁纸或锁屏图片。

0.1.3 新增实验性摄像头控制：明确启动选中的摄像头后，在预览中框选静止、有纹理的背景目标，保持笔记本底座不动，在适度的屏幕开合范围内分别记录动画的折叠和展开端点。它估计图像运动，不测量铰链角度，尚未通过真实摄像头和笔记本硬件验证。目标丢失时展开位置保持不变，需要重新选择目标并校准。

摄像头窗口最小化或主控制窗口隐藏后，拍摄可能继续，摄像头窗口标题和状态会显示正在运行。停止摄像头、关闭其窗口、锁定、休眠、关闭显示器、收到合盖通知或退出程序都会停止拍摄；恢复后不会自动重启，需要手动启动并重新校准。摄像头只在本机处理视频，不使用麦克风，不保存照片或录像，不上传画面；帧、目标和校准仅存在内存中，停止时清除。下载动画素材是独立的联网操作。

在 Windows PowerShell 中运行 `./Windows/build.ps1`，然后启动 `dist/windows/win-x64/BloomNative.Windows.exe`。Windows ARM64 可使用 `./Windows/build.ps1 -Runtime win-arm64`。首次使用时，请在应用中点击下载原动画；构建和发布包均不包含该素材。界面可切换 **English / 简体中文**。

若下载返回 HTTP 403，可选择在浏览器中打开原动画；如果浏览器可以访问，请保存原视频，再通过“选择本地副本…”导入。导入仍会校验同一 SHA-256。浏览器也可能被拒绝；此时只能使用已有的匹配原视频副本，应用无法保证源文件可访问。

0.1.5 包含 0.1.4 的桌面渲染修复，并增加窗口位置检查、Explorer 故障后的有限重试和原生窗口清理保护。下载超时现在覆盖整个传输过程；导入时先复制并校验，再替换原文件。桌面渲染方式：针对现代 Explorer 布局，用完全不透明的原生分层窗口承载普通 WPF 渲染子窗口，处理“已连接、视频正常播放，但桌面仍不可见”的情况。此改动仍需在受影响的 Windows 设备上确认。

若预览正常但桌面仍显示旧壁纸，请先从托盘退出旧版本，再启动标题显示 **Windows 0.1.5** 的新版本。启用动态壁纸并重新播放，最小化控制窗口或按 **Win+D**，确认桌面上的 Bloom 确实可见、图标仍可点击。若依然不可见，保持动态壁纸启用，点击“测试桌面图层”，再按 **Win+D**，检查图标下方是否出现洋红色/青色 **BLOOM DESKTOP TEST** 图案；再次点击该按钮恢复视频。图案可见但视频不可见时，更可能是视频渲染问题。请复制诊断信息，并说明图案是否出现、显示器数量和缩放设置。

桌面背景使用 Explorer 的非公开接口，需在实际 Windows 设备上检查多显示器、缩放、唤醒及屏保行为。关闭控制窗口后可通过托盘图标重新打开，或退出应用。

托盘菜单现在提供“诊断信息…”窗口。自动复制失败时，仍可在窗口中选择诊断文本。关闭动态壁纸会取消自动恢复；连续恢复失败后，请关闭再重新启用以手动重试。
