import Foundation
import AVFoundation
import Metal

// Shared across preview and desktop displays: decode the original clip once.
final class BloomFrames {
    let luma: MTLTexture
    let chroma: MTLTexture
    let count: Int
    let duration: Double
    let flags: Float
    static private var cache: [String: BloomFrames] = [:]
    static private let lock = NSLock()
    static func load(device: MTLDevice, url: URL) throws -> BloomFrames {
        lock.lock(); defer { lock.unlock() }
        if let frames = cache[url.path] { return frames }
        let frames = try BloomFrames(device: device, url: url)
        cache[url.path] = frames
        return frames
    }
    private init(device: MTLDevice, url: URL) throws {
        let asset = AVURLAsset(url: url)
        let (track, seconds) = try Self.loadMetadata(asset)
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        ])
        output.alwaysCopiesSampleData = false
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? Self.error(BloomStrings.text("无法解码原始动画")) }
        var frames: [CVPixelBuffer] = []
        while let sample = output.copyNextSampleBuffer() {
            if let buffer = CMSampleBufferGetImageBuffer(sample) { frames.append(buffer) }
        }
        guard reader.status == .completed, let first = frames.first else { throw reader.error ?? Self.error(BloomStrings.text("动画解码未完成")) }
        count = frames.count
        duration = seconds
        let width = CVPixelBufferGetWidthOfPlane(first, 0), height = CVPixelBufferGetHeightOfPlane(first, 0)
        func makeTexture(format: MTLPixelFormat, width: Int, height: Int) throws -> MTLTexture {
            let descriptor = MTLTextureDescriptor()
            descriptor.textureType = .type2DArray; descriptor.pixelFormat = format
            descriptor.width = width; descriptor.height = height; descriptor.arrayLength = frames.count
            descriptor.storageMode = .shared; descriptor.usage = .shaderRead
            guard let texture = device.makeTexture(descriptor: descriptor) else { throw Self.error(BloomStrings.text("无法为原始动画分配 GPU 资源")) }
            return texture
        }
        luma = try makeTexture(format: .r8Unorm, width: width, height: height)
        chroma = try makeTexture(format: .rg8Unorm, width: CVPixelBufferGetWidthOfPlane(first,1), height: CVPixelBufferGetHeightOfPlane(first,1))
        let pixelType = CVPixelBufferGetPixelFormatType(first)
        let matrix = CVBufferCopyAttachment(first, kCVImageBufferYCbCrMatrixKey, nil) as? String
        flags = Float((pixelType == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange ? 1 : 0) + (matrix == kCVImageBufferYCbCrMatrix_ITU_R_709_2 as String ? 2 : 0))
        for (index, buffer) in frames.enumerated() {
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            for (plane, texture) in [(0,luma),(1,chroma)] {
                guard let address = CVPixelBufferGetBaseAddressOfPlane(buffer, plane) else { throw Self.error(BloomStrings.text("动画帧缺少像素数据")) }
                let stride = CVPixelBufferGetBytesPerRowOfPlane(buffer, plane)
                texture.replace(region: MTLRegionMake2D(0,0,texture.width,texture.height), mipmapLevel: 0, slice: index, withBytes: address, bytesPerRow: stride, bytesPerImage: stride * texture.height)
            }
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
        }
        let bytes = luma.allocatedSize + chroma.allocatedSize
        NSLog("Bloom original animation: %d frames, %dx%d, %.2fs, %.1f MiB shared GPU storage", count, width,height,duration,Double(bytes)/1048576)
    }
    // The MTKView initializer is synchronous; metadata work runs on a detached task,
    // so AVFoundation's modern async loading does not depend on the main run loop.
    private final class MetadataResult: @unchecked Sendable {
        var value: Result<(AVAssetTrack, Double), Error>?
    }
    static private func loadMetadata(_ asset: AVURLAsset) throws -> (AVAssetTrack, Double) {
        let box = MetadataResult(), ready = DispatchSemaphore(value: 0)
        Task.detached {
            do {
                guard let track = try await asset.loadTracks(withMediaType: .video).first else { throw Self.error(BloomStrings.text("原始动画缺少视频轨道")) }
                let duration = try await asset.load(.duration)
                box.value = .success((track, CMTimeGetSeconds(duration)))
            } catch { box.value = .failure(error) }
            ready.signal()
        }
        ready.wait()
        return try box.value!.get()
    }
    static private func error(_ message: String) -> NSError { NSError(domain: "BloomOriginal", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
}
