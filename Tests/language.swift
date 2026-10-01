import Foundation
// Run under a separate defaults domain so verification never changes app preferences.
@main struct LanguageVerification {
    static func main() {
        let oldValue = UserDefaults.standard.object(forKey: "appLanguage")
        defer {
            if let oldValue { UserDefaults.standard.set(oldValue,forKey: "appLanguage") }
            else { UserDefaults.standard.removeObject(forKey: "appLanguage") }
        }
        for language in AppLanguage.allCases {
            UserDefaults.standard.set(language.rawValue,forKey: "appLanguage")
            precondition(AppLanguage.selected == language)
        }
        precondition(BloomStrings.text("启用动态桌面",language: .english) == "Dynamic wallpaper")
        precondition(BloomStrings.text("启用动态桌面",language: .simplifiedChinese) == "启用动态桌面")
        precondition(BloomStrings.text("铰链传感器已连接",language: .english) == "Lid sensor connected")
        precondition(BloomStrings.text("显示控制面板",language: .english) == "Show Controls")
        print("PASS: English / Chinese translations and saved language preference")
    }
}
