# Mousharing native Mac preview

SwiftUI/AppKit application for macOS 14+. It has no Python runtime dependency and uses the native v1 protocol documented in `../shared/PROTOCOL.md`, rather than the Python CLI protocol.

## Build

```sh
swift build --package-path native/macos
native/macos/build-app.sh
```

The build script creates `native/macos/dist/Mousharing.app` and applies an ad-hoc development signature. It does not install or launch the app, change input permissions, or start sharing. Distribution signing/notarization is not configured.

## Configuration and pairing

- Select **Bagikan mouse & keyboard saya** for a Mac master, or **Gunakan perangkat lain** for a Mac client.
- Copy the master invitation into the client and choose **Gunakan undangan**. A matching discovered master is preferred; an editable address field is available when discovery is blocked.
- Pairing secrets are stored in Keychain. Public settings are stored in `~/Library/Application Support/Mousharing/settings.json`.
- The first authenticated client is pinned to the master. Creating a new invitation rotates its key and clears that pin, after a confirmation dialog.
- Start sharing only after granting the applicable macOS Accessibility/Input Monitoring permissions. Client reception requires Accessibility; Input Monitoring is requested only for the master.
- Choose the client screen side relative to the master. F8 switches manually and Escape returns to the master. Clipboard text is negotiated by both endpoints; either endpoint may disable it before connecting.
- Pause stops input, clipboard, connections, listeners, and reconnect attempts. A client reconnects at bounded 1/2/4/8/15-second intervals while sharing remains started. Master waits for the pinned client after a disconnection.

## Verification

A command-line self-test exits before creating GUI state, opening Keychain, accessing OS clipboard, or installing input hooks:

```sh
native/macos/.build/debug/MousharingMac --self-test native/shared/fixtures.json
```

The self-test checks shared HKDF/AES-GCM vectors, exact raw fixture encryption, invitation decoding, and strict sequence types. XCTest tests add screen geometry and malformed base64 cases:

```sh
MOUSHARING_FIXTURES="$PWD/native/shared/fixtures.json" swift test --package-path native/macos
```

Full Xcode is required for XCTest. The current local Command Line Tools can build the app and execute the standalone cryptographic self-test, but do not provide the XCTest module.

For the shared Python interoperability harness only, `--protocol-test-client HOST PORT PUBLIC_FIXTURE_INVITE` exercises the actual `NativeSession` TCP handshake and clipboard payload in memory. It never activates GUI, Keychain, OS clipboard or input hooks. Do not use live pairing invitations in this mode or command-line arguments.

## Remaining acceptance work

Native input/clipboard sharing and role reversal must be exercised on the actual Mac and Windows pair. The earlier successful Python prototype tests are not acceptance evidence for these native engines. Check screen boundaries, held-key release, sleep/wake, pause/quit, foreground capture, pair rotation, denied permissions, blocked discovery, reconnect, and master role changes. This preview currently handles one primary screen per device and one active client.
