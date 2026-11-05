# Mousharing native design contract

## Product and audience

A private, local-network utility for a person with Mac and Windows laptops and one primary mouse/keyboard. Setup must explain which laptop supplies input, pair without Terminal or certificate files, arrange two screens, and make the current owner of input obvious. Indonesian is the initial product language.

## Native ownership

The user explicitly selected native Mac and Windows applications. SwiftUI/AppKit owns Mac controls, SF system typography, menu bar, system colors, focus, sheets, and scrollbars. WinUI 3/Win32 owns Windows controls, Segoe/system typography, Fluent spacing, theme resources, dialogs, and tray integration. There is no web renderer, CSS theme, Electron, or cross-platform imitation of system widgets. OS semantic foreground/background/accent/error colors are the runtime token owners, including light/dark/accessibility contrast variants. Preserve each platform's navigation and keyboard conventions.

## Layout and hierarchy

The signature element is the two-laptop arrangement with an explicit master indicator. Keep the surrounding settings quiet. Use a native sidebar or native sectioned settings surface; visible sections cover connection, role, screens, clipboard, and permission/status recovery. Give the primary action a stable position and label. Advanced IP fallback stays behind a disclosure until necessary. Do not display cipher names, keys, ports, or exception stack traces as normal product copy.

## Behavior

One master supplies physical mouse/keyboard; one client receives. Either OS can hold either role. Role changes, pause, disconnect, quit, timeout, permission failure, and lost capture release held input and restore local cursor. Clipboard text can flow both ways and can be disabled. Pairing uses a copy/paste invitation, public discovery, and a manual address fallback. Pairing material is masked in fields and never appears in logs. Store secrets in Keychain/DPAPI. Forget pairing is a separate confirmed action that rotates trust.

## States and accessibility

Use text and a symbol for connection state; never color alone. Represent disconnected, discovering, pairing, connected, paused, and actionable failure. Disable duplicate connection actions while pending. Preserve entered invitation and address after validation errors. Native pickers provide keyboard alternatives to drag-based screen placement. Controls need accessible labels; normal system focus and keyboard behavior remain active. Keep status visible from menu bar/tray while the settings window is closed.

## Validation and limits

Source/build checks and encrypted protocol fixtures do not prove cross-device input behavior or rendered accessibility. Test live pairing, both master directions, clipboard, pause, role changes, reconnect, and cursor/focus recovery on the actual laptops before a production release. Initial scope is one primary display per laptop and text clipboard. Native build outputs are development artifacts until signing and installation are verified.
