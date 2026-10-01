import AppKit
import MetalKit
import ScreenSaver

func check(_ predicate: Bool, _ message: String) { if !predicate { fatalError(message) } }
check(normalizedLidAngle(358) == 0 && normalizedLidAngle(99) == 99, "closed orientation wrap does not unfold")
check(bloomProgress(angle: 10, closed: 10, open: 120) == 0, "closed endpoint")
check(bloomProgress(angle: 120, closed: 10, open: 120) == 1, "open endpoint")
var previous = 0.0
for angle in stride(from: 0.0, through: 150.0, by: 0.25) {
    let value = bloomProgress(angle: angle, closed: 10, open: 120)
    check(value >= previous && value >= 0 && value <= 1, "continuous monotone mapping")
    previous = value
}
let a = smoothBloom(current: 0, target: 1, elapsed: 1.0/30, response: 0.22)
let b = smoothBloom(current: smoothBloom(current: 0, target: 1, elapsed: 1.0/60, response: 0.22), target: 1, elapsed: 1.0/60, response: 0.22)
check(abs(a-b) < 1e-12, "frame-rate independent smoothing")
let sensor = LidSensor()
let samples = (0..<30).compactMap { _ in sensor.read() }
print("Repeated HID reads: \(samples.count)/30, values: \(Set(samples))")
print("HID: \(sensor.status), actual angle: \(sensor.read().map { String($0) } ?? "unavailable")")
let root = URL(fileURLWithPath: CommandLine.arguments[1])
let appBundle = Bundle(url: root.appendingPathComponent("dist/Bloom Native.app"))!
let view = BloomMetalView(bundle: appBundle)
check(view.failure == nil, "Metal renderer initialization \(view.failure ?? "")")
let renderer = view.renderer!
check(renderer.frames.count == 300, "all 300 original animation frames decoded")
let device = view.device!
let width = 1280, height = 720
var imageData: [Data] = []
for progress in [Float(0), 0.5, 1] {
    let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
    descriptor.usage = [.renderTarget]; descriptor.storageMode = .shared
    let texture = device.makeTexture(descriptor: descriptor)!
    let pass = MTLRenderPassDescriptor()
    pass.colorAttachments[0].texture = texture
    pass.colorAttachments[0].loadAction = .clear
    pass.colorAttachments[0].storeAction = .store
    let command = renderer.queue.makeCommandBuffer()!
    let encoder = command.makeRenderCommandEncoder(descriptor: pass)!
    var uniforms = BloomUniforms(resolution: SIMD2(Float(width),Float(height)), time: 0, progress: progress, breath: 0, imageAspect: Float(renderer.texture.width)/Float(renderer.texture.height), frameCount: Float(renderer.frames.count), flags: renderer.frames.flags)
    encoder.setRenderPipelineState(renderer.pipeline)
    encoder.setFragmentTexture(renderer.frames.luma,index: 0)
    encoder.setFragmentTexture(renderer.frames.chroma,index: 1)
    encoder.setFragmentBytes(&uniforms,length: MemoryLayout<BloomUniforms>.stride,index: 0)
    encoder.drawPrimitives(type: .triangle,vertexStart: 0,vertexCount: 3)
    encoder.endEncoding(); command.commit(); command.waitUntilCompleted()
    check(command.status == .completed, "GPU command completed")
    var bytes = [UInt8](repeating: 0, count: width*height*4)
    texture.getBytes(&bytes, bytesPerRow: width*4, from: MTLRegionMake2D(0,0,width,height), mipmapLevel: 0)
    check(bytes.contains(where: {$0 > 30}), "render isn't blank")
    imageData.append(Data(bytes))
    for index in stride(from: 0, to: bytes.count, by: 4) { bytes.swapAt(index,index+2) }
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil,pixelsWide: width,pixelsHigh: height,bitsPerSample: 8,samplesPerPixel: 4,hasAlpha: true,isPlanar: false,colorSpaceName: .deviceRGB,bytesPerRow: width*4,bitsPerPixel: 32)!
    bytes.withUnsafeBytes { raw in bitmap.bitmapData!.update(from: raw.bindMemory(to: UInt8.self).baseAddress!, count: bytes.count) }
    try bitmap.representation(using: .png,properties: [:])!.write(to: root.appendingPathComponent("Tests/frame-\(progress).png"))
}
check(imageData[0] != imageData[1] && imageData[1] != imageData[2], "unfold frames differ")
let saverBundle = Bundle(url: root.appendingPathComponent("dist/Bloom Native.saver"))!
try saverBundle.loadAndReturnError()
check(saverBundle.principalClass != nil, "screen saver principal class load")
let cls = saverBundle.principalClass as! ScreenSaverView.Type
let saver = cls.init(frame: NSRect(x: 0, y: 0, width: 800, height: 600), isPreview: true)!
saver.startAnimation(); saver.stopAnimation()
check(saver.value(forKey: "sourceImageLoaded") as? Bool == true, "screen saver loaded original blue poster")
func saverBitmap(at elapsed: Double) -> NSBitmapImageRep {
    saver.setValue(elapsed,forKey: "elapsed")
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil,pixelsWide: 800,pixelsHigh: 600,bitsPerSample: 8,samplesPerPixel: 4,hasAlpha: true,isPlanar: false,colorSpaceName: .deviceRGB,bytesPerRow: 800*4,bitsPerPixel: 32)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    saver.draw(saver.bounds)
    NSGraphicsContext.restoreGraphicsState()
    return bitmap
}
let saverFirst = saverBitmap(at: 0), saverLater = saverBitmap(at: 4)
let firstData = saverFirst.representation(using: .png,properties: [:])!
let laterData = saverLater.representation(using: .png,properties: [:])!
check(firstData != laterData,"screen saver gentle movement changes actual drawing")
let center = saverFirst.colorAt(x: 400,y: 300)!.usingColorSpace(.deviceRGB)!
check(center.blueComponent > center.greenComponent,"screen saver renders blue rather than green")
try firstData.write(to: root.appendingPathComponent("Tests/saver-blue-0.png"))
try laterData.write(to: root.appendingPathComponent("Tests/saver-blue-4.png"))
let snapshot = try renderer.wallpaperJPEG(width: 1280,height: 720,progress: 1)
check(NSBitmapImageRep(data: snapshot)?.pixelsWide == 1280,"system wallpaper JPEG export")
try snapshot.write(to: root.appendingPathComponent("Tests/system-wallpaper.jpg"))
print("PASS: mapping, smoothing, GPU shader and 3 unfold frames, saver bundle load / start / stop")
