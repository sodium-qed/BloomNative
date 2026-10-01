import AppKit
import MetalKit

struct BloomUniforms {
    var resolution: SIMD2<Float>
    var time: Float
    var progress: Float
    var breath: Float
    var imageAspect: Float
    var frameCount: Float = 1
    var flags: Float = 0
}
final class BloomRenderer: NSObject, MTKViewDelegate {
    let queue: MTLCommandQueue
    let pipeline: MTLRenderPipelineState
    let frames: BloomFrames
    var texture: MTLTexture { frames.luma }
    var target: Float = 1
    var breath: Float = 1
    var response: Double = 0.22
    private(set) var progress: Double = 1
    private var lastTime = CACurrentMediaTime()
    private let started = CACurrentMediaTime()
    private var replayStarted: TimeInterval?
    private var replayTarget: Float?
    init(view: MTKView, bundle: Bundle) throws {
        guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue() else {
            throw NSError(domain: "Bloom", code: 1, userInfo: [NSLocalizedDescriptionKey: BloomStrings.text("这台 Mac 无法启用 Metal")])
        }
        view.device = device; self.queue = queue
        guard let url = bundle.url(forResource: "Bloom", withExtension: "metal"),
              let imageURL = bundle.url(forResource: "BloomOriginal", withExtension: "mp4") else {
            throw NSError(domain: "Bloom", code: 2, userInfo: [NSLocalizedDescriptionKey: BloomStrings.text("应用资源不完整，请重新构建或恢复完整应用包")])
        }
        let library = try device.makeLibrary(source: String(contentsOf: url, encoding: .utf8), options: nil)
        let descriptor = MTLRenderPipelineDescriptor()
        descriptor.vertexFunction = library.makeFunction(name: "bloomVertex")
        descriptor.fragmentFunction = library.makeFunction(name: "bloomFragment")
        descriptor.colorAttachments[0].pixelFormat = view.colorPixelFormat
        pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
        frames = try BloomFrames.load(device: device, url: imageURL)
        super.init()
        view.delegate = self
        view.preferredFramesPerSecond = 60
        view.enableSetNeedsDisplay = false
    }
    func reset(progress: Double = 0, playOriginal: Bool = false) {
        self.progress = progress; lastTime = CACurrentMediaTime()
        replayStarted = playOriginal ? lastTime : nil; replayTarget = nil
    }
    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}
    func draw(in view: MTKView) {
        let now = CACurrentMediaTime()
        if let replayStarted {
            if let replayTarget, replayTarget != target { self.replayStarted = nil }
            else {
                replayTarget = target
                let fraction = min(1, (now - replayStarted) / frames.duration)
                progress = Double(target) * fraction
                if fraction >= 1 { self.replayStarted = nil }
            }
        }
        if replayStarted == nil {
            progress = smoothBloom(current: progress, target: Double(target), elapsed: now - lastTime, response: response)
        }
        lastTime = now
        guard let pass = view.currentRenderPassDescriptor, let drawable = view.currentDrawable,
              let command = queue.makeCommandBuffer(), let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return }
        var uniforms = BloomUniforms(resolution: SIMD2(Float(view.drawableSize.width),Float(view.drawableSize.height)), time: Float(now-started), progress: Float(progress), breath: breath, imageAspect: Float(texture.width)/Float(texture.height), frameCount: Float(frames.count), flags: frames.flags)
        encoder.setRenderPipelineState(pipeline)
        encoder.setFragmentTexture(frames.luma, index: 0)
        encoder.setFragmentTexture(frames.chroma, index: 1)
        encoder.setFragmentBytes(&uniforms, length: MemoryLayout<BloomUniforms>.stride, index: 0)
        encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
        encoder.endEncoding(); command.present(drawable); command.commit()
    }
}
final class BloomMetalView: MTKView {
    var renderer: BloomRenderer?
    var failure: String?
    init(frame: NSRect = .zero, bundle: Bundle = .main) {
        super.init(frame: frame, device: MTLCreateSystemDefaultDevice())
        do { renderer = try BloomRenderer(view: self, bundle: bundle) }
        catch { failure = error.localizedDescription; NSLog("Bloom rendering error: %@", failure!) }
    }
    required init(coder: NSCoder) { fatalError("init(coder:) is not supported") }
}

extension BloomRenderer {
    func wallpaperJPEG(width: Int, height: Int, progress: Double) throws -> Data {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm,width: width,height: height,mipmapped: false)
        descriptor.storageMode = .shared; descriptor.usage = .renderTarget
        guard let texture = queue.device.makeTexture(descriptor: descriptor),let command = queue.makeCommandBuffer() else { throw snapshotError() }
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = texture
        pass.colorAttachments[0].loadAction = .clear; pass.colorAttachments[0].storeAction = .store
        guard let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { throw snapshotError() }
        var uniforms = BloomUniforms(resolution: SIMD2(Float(width),Float(height)),time: 0,progress: Float(progress),breath: 0,imageAspect: Float(frames.luma.width)/Float(frames.luma.height),frameCount: Float(frames.count),flags: frames.flags)
        encoder.setRenderPipelineState(pipeline)
        encoder.setFragmentTexture(frames.luma,index: 0); encoder.setFragmentTexture(frames.chroma,index: 1)
        encoder.setFragmentBytes(&uniforms,length: MemoryLayout<BloomUniforms>.stride,index: 0)
        encoder.drawPrimitives(type: .triangle,vertexStart: 0,vertexCount: 3)
        encoder.endEncoding(); command.commit(); command.waitUntilCompleted()
        guard command.status == .completed else { throw command.error ?? snapshotError() }
        var bytes = [UInt8](repeating: 0,count: width*height*4)
        texture.getBytes(&bytes,bytesPerRow: width*4,from: MTLRegionMake2D(0,0,width,height),mipmapLevel: 0)
        for index in stride(from: 0,to: bytes.count,by: 4) { bytes.swapAt(index,index+2) }
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil,pixelsWide: width,pixelsHigh: height,bitsPerSample: 8,samplesPerPixel: 4,hasAlpha: true,isPlanar: false,colorSpaceName: .deviceRGB,bytesPerRow: width*4,bitsPerPixel: 32) else { throw snapshotError() }
        bytes.withUnsafeBytes { raw in bitmap.bitmapData!.update(from: raw.bindMemory(to: UInt8.self).baseAddress!,count: bytes.count) }
        guard let data = bitmap.representation(using: .jpeg,properties: [.compressionFactor: 0.95]) else { throw snapshotError() }
        return data
    }
    private func snapshotError() -> NSError { NSError(domain: "BloomWallpaper",code: 1,userInfo: [NSLocalizedDescriptionKey: BloomStrings.text("无法生成系统壁纸快照")]) }
}
