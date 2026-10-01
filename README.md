# Bloom Native

A native SwiftUI + Metal wallpaper app for Apple Silicon Macs. Bloom unfolds as you open your MacBook, using the original Windows 11 Bloom animation displayed by its creator, Six N. Five.

## Features

- Lid-angle tracking with smooth interpolation between original animation frames.
- Matching system-wallpaper snapshots, refreshed when the pose or active Space changes.
- A blue Bloom screen saver with gentle breathing and drifting.
- Switch instantly between **English** and **简体中文** using the **Language / 语言** picker. Your choice is remembered; the initial language follows macOS.
- Close, minimize, or hide the controls while wallpaper keeps running. Reopen from the menu bar icon or Dock.
- Restore the recorded wallpaper when dynamic wallpaper is disabled or the app quits, provided the current wallpaper still belongs to Bloom Native.

## Build and install

Requires **Apple Silicon, macOS 14 or newer**, and Apple's Command Line Tools (or Xcode). No third-party runtime is required.

```sh
git clone https://github.com/taco-jpg/BloomNative.git
cd BloomNative
./build.sh
ditto "dist/Bloom Native.app" "/Applications/Bloom Native.app"
open "/Applications/Bloom Native.app"
```

The build fetches the pinned original clip directly from the creator's website and generates a screen-saver poster using Apple's AVFoundation. Artwork is downloaded for your local build and is excluded from this repository. If the source becomes unavailable, provide the matching `Resources/BloomOriginal.mp4` locally and run the build again.

For Xcode, run `./Scripts/prepare-artwork.sh` first, then open `BloomNative.xcodeproj` and build the **Bloom Native** scheme. The app embeds its screen saver, so it can be moved to Applications on its own.

This is an **ad-hoc signed local build**, not a notarized distribution. The repository publishes source code rather than an artwork-containing app download.

## Use

1. Open Bloom Native and turn on **Dynamic wallpaper**.
2. Keep **Follow lid angle** on for supported MacBooks. With it off, use the manual angle slider.
3. Use **Replay unfolding** to replay the original five-second unfold toward the current pose.
4. Choose **Install screen saver & open settings**, then select **Bloom Native** in macOS Screen Saver settings. On macOS 26 and newer, this lives under **Wallpaper → Screen Saver → Other → Show All**. The screen saver displays motion while it runs; it does not replace macOS's password or FileVault screen.

If an older screen saver is already installed, move it aside before using the installer. It will not overwrite an existing component.

## Performance and compatibility

The original clip contains 300 frames at 1500 × 1000, 60 fps, for five seconds. The app loads shared YUV GPU textures once per process; measured storage on the development Mac was approximately **698 MiB**. Loading takes some time. Rendering targets 60 fps, or 30 fps in Low Power Mode; actual performance depends on hardware and load. The screen saver draws one poster directly in the macOS host and uses much less memory.

Lid-angle reading uses a read-only Apple HID feature report and is hardware-dependent. It was tested on an M1 Max MacBook Pro. Unsupported machines can use manual preview and wake animation. Reduce Motion disables breathing; sleep and lock pause the desktop renderer.

System wallpaper snapshots match the current animation pose but are not a macOS-native video wallpaper format. Every Space's first transition frame and password-lock behavior have not been verified. The subtle screen-saver motion is whole-image zoom and drift; petal unfolding in the desktop comes from the original rendered clip, not a live 3D model.

## Verification

```sh
./build.sh
./verify.sh
```

The verification covers angle normalization and smoothing, source-frame decoding, GPU rendering at multiple poses, system-wallpaper JPEG export, screen-saver loading, blue output, and image changes over time. Language checks cover both translations and saving/reloading the selected language. Tests generate local output that is excluded from Git.

## Attribution and licenses

Application source code: **MIT**, see [LICENSE](LICENSE).

Windows 11 Bloom artwork and its original animation belong to **Microsoft / Six N. Five** and are **not covered by this repository's MIT license**. This project is independent and is not affiliated with Microsoft, Apple, or Six N. Five. No permission to redistribute that artwork is asserted; it is not committed to this repository or included in GitHub release assets.

- [Original creator's Windows 11 project](https://sixnfive.com/projects/windows-11/)
- [Original animation](https://sixnfive.com/wp-content/uploads/2021/07/curls_hero_05_anim_19_light2.mp4)
- [Source provenance and checksum](Resources/BloomOriginal.source.json)
- [LidAngleSensor protocol reference](https://github.com/samhenrigold/LidAngleSensor)

## 简体中文

原生 SwiftUI + Metal 动态壁纸，支持随 MacBook 屏幕开合展开、同步系统壁纸和轻微呼吸屏保。界面左侧的 **Language / 语言** 可以即时切换 **English / 简体中文**，并保存选择。

运行 `./build.sh` 后，将 `dist/Bloom Native.app` 拷贝到「应用程序」。屏保已内置在应用中，通过「安装屏保并打开设置」安装，然后在 macOS 系统设置中选择 Bloom Native。关闭、隐藏或最小化控制窗口后壁纸继续运行，通过菜单栏或 Dock 可以重新打开。

公开仓库仅包含应用源码。原动画从原作者网站下载到本机构建，素材版权归 Microsoft / Six N. Five，未随源码公开分发。
