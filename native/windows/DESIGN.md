# Mousharing Windows native design

Product utility for an Indonesian-speaking owner sharing input between a Mac and Windows laptop. The single job is to connect safely, show which computer currently owns input, and recover local control without a terminal.

## Platform identity and runtime mapping

Native WinUI 3 / Fluent controls own rendering, spacing within controls, focus, contrast themes, typography and accessible semantics. Segoe UI Variable is the platform UI typeface. Application resources use system `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, `CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, and `SystemFillColorAttentionBackgroundBrush`. Accent comes from the user's Windows accent; never override high contrast. Brand-neutral surface #F3F3F3, text #1A1A1A, secondary #616161, information #005A9E, success #0F7B0F are reference light-mode roles only; actual runtime brushes remain system-owned.

## Layout and voice

A compact desktop utility window: status, two-screen arrangement, then setup/pairing and settings. Quiet native cards with 16–24 px section spacing, 8–12 px field spacing, rounded system controls. The memorable element is a literal two-computer arrangement whose left/right/top/bottom follows the configured layout. There is always a text-based non-drag layout selector. Indonesian copy uses direct actions: Mulai berbagi, Hubungkan, Jeda, Lanjutkan, Putuskan. A status uses both an icon and text; color is supplementary.

## Canonical behavior

MainWindow owns field validation and all inline feedback through a native InfoBar. Native ComboBox owns role and screen-side selection. Native PasswordBox masks invitation material until users reveal or copy it. The session controller owns busy/idle/connected/paused/failure states; unavailable controls are disabled while a transition is in progress. A failed connection leaves entered settings intact. Changing role or device name requires stopping the current session first. Pause always releases local capture and sends release/reset to the peer. Forget pairing requires a ContentDialog identifying the consequence. Secrets use Windows DPAPI CurrentUser; JSON settings contain no key material.

## Verification

XAML/C# build runs on a Windows runner. Native UI, keyboard navigation, Narrator, high contrast, 200% display scaling, clipboard, raw input capture and tray need Windows runtime QA. Source review or a successful build does not establish physical input readiness.
