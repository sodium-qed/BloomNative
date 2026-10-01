import Foundation

enum AppLanguage: String, CaseIterable, Identifiable {
    case english = "en"
    case simplifiedChinese = "zh-Hans"
    var id: String { rawValue }
    var title: String { self == .english ? "English" : "简体中文" }
    static var selected: AppLanguage {
        if let value = UserDefaults.standard.string(forKey: "appLanguage"), let language = AppLanguage(rawValue: value) { return language }
        return Locale.preferredLanguages.first?.hasPrefix("zh") == true ? .simplifiedChinese : .english
    }
}

// App-selected language applies immediately, independently of the system language.
enum BloomStrings {
    static func text(_ key: String, language: AppLanguage = .selected) -> String {
        language == .simplifiedChinese ? key : (english[key] ?? key)
    }
    private static let english: [String: String] = [
        "随开合，慢慢绽放。": "Bloom as you open your Mac.",
        "启用动态桌面": "Dynamic wallpaper",
        "跟随屏幕开合": "Follow lid angle",
        "当前角度": "Lid angle",
        "此机型无法读取时，使用唤醒展开或手动预览。": "No supported sensor? Use the wake animation or manual preview.",
        "预览角度": "Preview angle",
        "预览开合角度": "Preview lid angle",
        "展开与动效": "Unfolding & motion",
        "完全展开角度": "Fully open angle",
        "平滑跟随": "Smoothing",
        "秒": "s",
        "平滑跟随时间": "Smoothing time",
        "轻微呼吸": "Gentle breathing",
        "重播展开": "Replay unfolding",
        "隐藏控制面板": "Hide controls",
        "关闭或隐藏窗口后，壁纸继续运行。菜单栏 Bloom 可重新打开。": "Wallpaper keeps running when controls are closed or hidden. Reopen from the Bloom menu bar icon.",
        "屏保 · 安静地流动": "Screen saver · Gentle motion",
        "安装后在系统屏保设置中选择 Bloom Native。锁屏的密码界面由 macOS 管理，动画在屏保运行时显示。": "After installation, select Bloom Native in Screen Saver settings. Animation plays in the screen saver; macOS manages the password screen.",
        "安装屏保并打开设置": "Install screen saver & open settings",
        "原动画：Microsoft / Six N. Five": "Original animation: Microsoft / Six N. Five",
        "桌面运行中": "Wallpaper running",
        "实时预览": "Live preview",
        "一朵花，一次新的开始。": "A bloom. A fresh beginning.",
        "Windows 11 Bloom · 原作者展开动画": "Windows 11 Bloom · Original creator animation",
        "原始动画逐帧跟随 · 300 帧 / 5 秒 · GPU 平滑播放": "Original animation · 300 frames / 5 seconds · Smooth GPU playback",
        "好": "OK",
        "正在检测铰链传感器…": "Looking for a lid sensor…",
        "未检测到可读取的铰链传感器": "No supported lid sensor detected",
        "无法打开传感器接口": "Unable to open the sensor interface",
        "铰链传感器已连接": "Lid sensor connected",
        "暂时无法读取角度": "Lid angle temporarily unavailable",
        "传感器报告格式不受支持": "Unsupported sensor report format",
        "系统壁纸同步失败：": "System wallpaper update failed: ",
        "屏保已存在。请在系统设置中选择 Bloom Native；若要更新，请先移走旧版。": "Screen saver already installed. Select Bloom Native in System Settings. Move the previous version aside before updating.",
        "这台 Mac 无法启用 Metal": "Metal is unavailable on this Mac",
        "应用资源不完整，请重新构建或恢复完整应用包": "App resources are missing. Rebuild or restore the complete app bundle.",
        "无法生成系统壁纸快照": "Unable to create the system wallpaper image",
        "无法解码原始动画": "Unable to decode the original animation",
        "动画解码未完成": "Animation decoding did not finish",
        "无法为原始动画分配 GPU 资源": "Unable to allocate GPU resources for the animation",
        "动画帧缺少像素数据": "Animation frame has no pixel data",
        "原始动画缺少视频轨道": "Original animation has no video track",
        "语言": "Language",
        "显示控制面板": "Show Controls",
        "隐藏 Bloom Native": "Hide Bloom Native",
        "退出 Bloom Native": "Quit Bloom Native",
        "窗口": "Window",
        "最小化": "Minimize",
        "关闭控制面板": "Close Controls",
        "动态壁纸": "Dynamic Wallpaper",
        "重播 Bloom": "Replay Bloom",
    ]
}
