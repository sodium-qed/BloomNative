import AppKit
import AVFoundation
let source = URL(fileURLWithPath: CommandLine.arguments[1])
let destination = URL(fileURLWithPath: CommandLine.arguments[2])
let asset = AVURLAsset(url: source)
let generator = AVAssetImageGenerator(asset: asset)
generator.appliesPreferredTrackTransform = true
generator.requestedTimeToleranceBefore = .zero
generator.requestedTimeToleranceAfter = .zero
// The pinned creator clip contains 300 frames at 60 fps.
let (image, _) = try await generator.image(at: CMTime(value: 299,timescale: 60))
let bitmap = NSBitmapImageRep(cgImage: image)
guard let data = bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.98]) else { fatalError("Cannot encode poster") }
try data.write(to: destination,options: .atomic)
