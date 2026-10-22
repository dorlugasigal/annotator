import AppKit

@MainActor
final class DecodedImageCache {
    private struct Entry {
        let data: Data
        let image: CGImage
        let cost: Int
        var used: UInt64
    }
    private struct Alias {
        let data: Data
        let key: UUID
    }
    private let byteLimit: Int
    private var entries: [UUID: Entry] = [:]
    private var contentKeys: [Data: UUID] = [:]
    private var aliases: [UUID: Alias] = [:]
    private var seen = Set<UUID>()
    private var clock: UInt64 = 0
    private(set) var bytes = 0
    private(set) var decodeCount = 0

    init(byteLimit: Int = 128 * 1_024 * 1_024) { self.byteLimit = max(0, byteLimit) }

    func beginFrame(validIDs: Set<UUID>) {
        aliases = aliases.filter { validIDs.contains($0.key) }
        seen.removeAll(keepingCapacity: true)
    }

    func image(for data: Data, elementID: UUID) throws -> CGImage {
        clock &+= 1
        if let alias = aliases[elementID], alias.data == data, var entry = entries[alias.key] {
            entry.used = clock; entries[alias.key] = entry; seen.insert(alias.key)
            return entry.image
        }
        aliases.removeValue(forKey: elementID)
        if let key = contentKeys[data], var entry = entries[key] {
            entry.used = clock; entries[key] = entry; seen.insert(key)
            aliases[elementID] = Alias(data: data, key: key)
            return entry.image
        }
        let image = try RasterAsset.decode(data)
        decodeCount += 1
        let (cost, overflow) = image.bytesPerRow.multipliedReportingOverflow(by: image.height)
        guard !overflow, cost <= byteLimit else { return image }
        while bytes > byteLimit - cost, let oldest = entries.min(by: { $0.value.used < $1.value.used })?.key {
            remove(oldest)
        }
        let key = UUID()
        entries[key] = Entry(data: data, image: image, cost: cost, used: clock)
        contentKeys[data] = key
        aliases[elementID] = Alias(data: data, key: key)
        bytes += cost
        seen.insert(key)
        return image
    }

    func endFrame() {
        for key in Array(entries.keys) where !seen.contains(key) { remove(key) }
        aliases = aliases.filter { entries[$0.value.key] != nil }
    }

    func reset() {
        entries.removeAll(); contentKeys.removeAll(); aliases.removeAll(); seen.removeAll()
        bytes = 0
    }

    private func remove(_ key: UUID) {
        guard let entry = entries.removeValue(forKey: key) else { return }
        contentKeys.removeValue(forKey: entry.data)
        bytes -= entry.cost
    }
}
