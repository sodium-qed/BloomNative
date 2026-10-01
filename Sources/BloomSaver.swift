import AppKit
import ScreenSaver

// Draw directly in the screen saver's host-provided graphics context. Nested Metal
// views/display links can produce a green surface or never tick in the legacy host.
@objc(BloomNativeSaverView) final class BloomNativeSaverView: ScreenSaverView {
    private var poster: NSImage?
    private var began = ProcessInfo.processInfo.systemUptime
    @objc dynamic private var elapsed = 0.0
    @objc var sourceImageLoaded: Bool { poster != nil }
    override init?(frame: NSRect,isPreview: Bool) {
        super.init(frame: frame,isPreview: isPreview)
        loadPoster()
        animationTimeInterval = 1.0 / (isPreview || ProcessInfo.processInfo.isLowPowerModeEnabled ? 30.0 : 60.0)
    }
    required init?(coder: NSCoder) { super.init(coder: coder); loadPoster(); animationTimeInterval = 1.0/60 }
    private func loadPoster() {
        if let url = Bundle(for: BloomNativeSaverView.self).url(forResource: "BloomPoster",withExtension: "jpg") {
            poster = NSImage(contentsOf: url)
        }
    }
    override var isOpaque: Bool { true }
    override func startAnimation() {
        began = ProcessInfo.processInfo.systemUptime; elapsed = 0
        super.startAnimation(); needsDisplay = true
    }
    override func animateOneFrame() {
        elapsed = ProcessInfo.processInfo.systemUptime - began
        needsDisplay = true
    }
    override func draw(_ dirtyRect: NSRect) {
        NSColor(calibratedRed: 0.58,green: 0.73,blue: 0.83,alpha: 1).setFill()
        bounds.fill()
        guard let poster else {
            ("Bloom Native: image resource unavailable" as NSString).draw(at: NSPoint(x: 24,y: 24),withAttributes: [.foregroundColor: NSColor.white,.font: NSFont.systemFont(ofSize: 18)])
            return
        }
        let motion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? 0.0 : 1.0
        let zoom = 1.015 + 0.008 * sin(elapsed*0.42) * motion
        let scale = max(bounds.width/poster.size.width,bounds.height/poster.size.height)*zoom
        let width = poster.size.width*scale, height = poster.size.height*scale
        let x = (bounds.width-width)/2 + sin(elapsed*0.25)*bounds.width*0.003*motion
        let y = (bounds.height-height)/2 + cos(elapsed*0.19)*bounds.height*0.003*motion
        NSGraphicsContext.current?.imageInterpolation = .high
        poster.draw(in: NSRect(x: x,y: y,width: width,height: height),from: .zero,operation: .copy,fraction: 1,respectFlipped: true,hints: nil)
    }
    override func stopAnimation() { super.stopAnimation() }
}
