# Native Mousharing interaction contract

Source of authority: user's current request for native Mac/Windows applications, configurable master, easy pairing; native/shared/PROTOCOL.md owns wire invariants. DESIGN.md owns visual intent.

## Canonical UI Map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Form | Native SwiftUI and WinUI controls + platform view model | DESIGN.md + this contract | role / pairing / device settings | platform builds and live UI review |
| Select/Listbox | Native Picker and ComboBox | DESIGN.md | role / client screen side | platform UI keyboard review |
| Scrollbar | AppKit and Windows App SDK system theme | DESIGN.md native ownership | platform system settings | platform UI review |
| Toast | Native app observable connection state | this contract recovery states | persistent error/status / copy acknowledgement | view-model and live review |
| Connection | Native platform network service | native/shared/PROTOCOL.md | master listener / authenticated client | shared fixtures and tests |
| Input ownership | Native capture and receiver state machine | this contract + native/shared/PROTOCOL.md | local / remote / paused | native tests and two-device testing |
| Pairing lifecycle | Keychain/DPAPI config + native confirmation | this contract + native/shared/PROTOCOL.md | invite / remember / forget | source review and live pairing |

## State transitions

Connection and role forms preserve entered values after failure and block duplicate connects. Status remains available from menu bar/tray. Pairing credentials never enter broadcast metadata or logs. Forget stops the current connection and invalidates the previous trust key after confirmation. Master/client changes stop sharing before saving the new role. Pause stops input and clipboard, closes the session, and cancels reconnection; Resume uses saved configuration. Auto-reconnect authenticates the saved peer again before enabling capture. No automatic role takeover.

## Cross-platform vocabulary

Use equivalent Indonesian labels and concepts across platforms. Master means this laptop supplies the mouse and keyboard; client means it accepts input from its paired master. Do not imply screen-video streaming or dragging files across operating systems. Clipboard support is plain text and explains the supported size in advanced/help copy.

## Recovery

Errors identify the action that failed and the next corrective action: grant the named OS input permission, paste a complete invitation, select a reachable laptop, check private LAN connectivity, or forget/re-pair after identity mismatch. Stop must always remain available. No automatic role takeover and no permission bypass. Reconnection must preserve the authenticated peer and release old held input first.
