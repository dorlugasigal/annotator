import CryptoKit
import Foundation

do {
    guard CommandLine.arguments.count == 4,
          let publicKey = Data(base64Encoded: CommandLine.arguments[1]),
          let signature = Data(base64Encoded: CommandLine.arguments[2]) else {
        throw NSError(domain: "UpdateVerification", code: 1,
                      userInfo: [NSLocalizedDescriptionKey: "Expected public key, signature and archive path."])
    }
    let key = try Curve25519.Signing.PublicKey(rawRepresentation: publicKey)
    let archive = try Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[3]), options: .mappedIfSafe)
    guard key.isValidSignature(signature, for: archive) else {
        throw NSError(domain: "UpdateVerification", code: 2,
                      userInfo: [NSLocalizedDescriptionKey: "Archive signature does not match the bundled public key."])
    }
    print("Archive signature matches the update public key.")
} catch {
    FileHandle.standardError.write(Data("Update verification failed: \(error.localizedDescription)\n".utf8))
    exit(1)
}
