import Foundation
import IOKit.hid

// Read only; no exclusive device access or input-monitoring permission needed.
final class LidSensor {
    private var device: IOHIDDevice?
    private var manager: IOHIDManager?
    private(set) var status = "未检测到可读取的铰链传感器"
    init() { reconnect() }
    func reconnect() {
        if let device { IOHIDDeviceClose(device, 0) }
        device = nil
        if let manager { IOHIDManagerClose(manager, 0) }
        manager = nil
        status = "未检测到可读取的铰链传感器"
        let manager = IOHIDManagerCreate(kCFAllocatorDefault, 0)
        IOHIDManagerSetDeviceMatching(manager, [kIOHIDVendorIDKey: 0x05ac,
            kIOHIDPrimaryUsagePageKey: 0x20, kIOHIDPrimaryUsageKey: 0x8a] as CFDictionary)
        guard IOHIDManagerOpen(manager, 0) == kIOReturnSuccess else {
            status = "无法打开传感器接口"; return
        }
        self.manager = manager
        guard let devices = IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice> else { return }
        for candidate in devices {
            guard IOHIDDeviceOpen(candidate, 0) == kIOReturnSuccess else { continue }
            device = candidate
            if read() != nil { status = "铰链传感器已连接"; return }
            IOHIDDeviceClose(candidate, 0); device = nil
        }
    }
    func read() -> Double? {
        guard let device else { return nil }
        var bytes = [UInt8](repeating: 0, count: 8)
        var count = bytes.count
        let result = IOHIDDeviceGetReport(device, kIOHIDReportTypeFeature, 1, &bytes, &count)
        guard result == kIOReturnSuccess, count >= 3 else { status = "暂时无法读取角度"; return nil }
        let angle = Double(UInt16(bytes[1]) | UInt16(bytes[2]) << 8)
        // Physical angle range, rather than an arbitrary validation quota.
        guard angle <= 360 else { status = "传感器报告格式不受支持"; return nil }
        status = "铰链传感器已连接"
        return normalizedLidAngle(angle)
    }
    deinit {
        if let device { IOHIDDeviceClose(device, 0) }
        if let manager { IOHIDManagerClose(manager, 0) }
    }
}

func bloomProgress(angle: Double, closed: Double, open: Double) -> Double {
    let x = min(1, max(0, (angle - closed) / max(1, open - closed)))
    return x * x * (3 - 2 * x)
}
func smoothBloom(current: Double, target: Double, elapsed: Double, response: Double) -> Double {
    current + (target - current) * (1 - exp(-max(0, elapsed) / max(0.01, response)))
}

// Orientation reports may wrap to 358–360 degrees just below the closed zero.
// MacBook hinges open through the positive half-circle; negative closure offsets map to closed.
func normalizedLidAngle(_ raw: Double) -> Double {
    let signed = raw > 180 ? raw - 360 : raw
    return max(0, signed)
}
