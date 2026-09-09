# Implementation notes

Things about the Remote Desktop ActiveX control (`mstscax.dll`) that are not obvious from the IDL and cost real
debugging time. Kept out of the README because nobody installing the app needs them.

## The design that everything else follows from

COM goes through **IDispatch late binding** (`src/Com.cs`), DPAPI through a `crypt32` P/Invoke, and JSON
through a hand-written parser (`src/MiniJson.cs`). That is what lets the .NET Framework 4.8 build (`build.cmd`,
no SDK required) and the .NET 8 build (`RdpTabs.csproj`) share one source tree with no NuGet packages and no
generated interop assembly.

The price is that pure vtable interfaces are unreachable — see the last two entries.

## `Connected` is `0` disconnected, `1` connected, `2` connecting

Polled every 250 ms, because late binding cannot sink `IMsTscAxEvents`. There are no events at all.

1 and 2 are easy to transpose, and transposing them means the UI says "connecting" with the overlay covering a
session that is already live. Pinned down empirically with `UpdateSessionDisplaySettings`, which only succeeds
on an established session, and now encoded as named constants in `src/RdpSessionControl.cs`.

## Cancelling the credential prompt wedges the control

If the user cancels or closes Windows' own credential dialog, the control stays in "connecting" **forever** and
never reports an error. `overallConnectionTimeout` does not fire either. Hence the app's own 30-second timeout,
paused while the dialog is up, plus code to detect that modal dialog and bring it to the front — it can end up
behind the main window, which looks exactly like a hang.

## SmartSizing only scales down, never up

Verified twice: with `SmartSizing=1`, a 1800×1200 remote in a 2534×1799 control area stayed at its original
size, centred, with an `#ABABAB` border. So the only way to fill a window larger than the remote resolution is
to change that resolution — which is why "fit window" calls `UpdateSessionDisplaySettings` (450 ms debounce)
and why maximizing has to follow the window.

Minimizing deliberately does not follow: a minimized window degenerates to 0×0, and following that would
shrink the remote desktop to a patch.

## The DPI scale can only be applied after connecting

No late-bindable property sets it beforehand: `DesktopScaleFactor` / `DeviceScaleFactor` return
`DISP_E_UNKNOWNNAME` through IDispatch because they live only on `IMsRdpExtendedSettings`, a pure vtable
interface. QueryInterface succeeds, but calling members in the order guessed from the IDL **segfaults the
process** — do not go there. What works: the moment the state becomes connected, push size and scale with
`UpdateSessionDisplaySettings`, retried up to four times via the debounce timer.

## Other vtable-only casualties

`IMsRdpClientNonScriptable` holds `SendKeys`, so there is no "send Ctrl+Alt+Del". Only the extended disconnect
reason is available, not the primary one. Both would need `[ComImport]` interfaces declared with the exact
member order — or the .NET 8 path with `COMReference`-generated interop.

## The CLSID has to be probed, not looked up

On Windows 11 both the ProgID and the CLSID of `MsTscAx.MsTscAx.13` are in the registry, but the class factory
in `mstscax.dll` does not implement it and `CoCreateInstance` returns `CLASS_E_CLASSNOTAVAILABLE`. So
`RdpAxHost` walks from `.13` downwards and actually creates each candidate, keeping the first that works
(`.12` on the machine this was built on).

## Small ones

- **`PerformanceFlags` is only sent during the handshake**, so changing the experience tier needs a reconnect.
- **A TCP pre-flight runs first** (3 s). Since `OnDisconnected` is unreachable, the common failures
  (unresolvable name, closed port, unreachable host) are diagnosed locally so the message is accurate.
- **Every layout measures its text** with `TextRenderer.MeasureText`. Glyph widths vary by font, DPI and
  script; a profile name in a script the UI font lacks goes through GDI font-linking and comes out far wider
  than expected.
- **Scrollbars and message boxes are system-drawn**, so dark mode needs uxtheme's `SetPreferredAppMode`
  (undocumented ordinal 135) plus `SetWindowTheme(hwnd, "DarkMode_Explorer")` on each scrolling panel. Both
  calls are guarded; if they fail the scrollbars just stay light.
- **The frameless window keeps native behaviour** by removing only the top caption in `WM_NCCALCSIZE` (and
  still insetting the top by the frame thickness when maximized, or content lands off-screen), with the tab
  strip returning `HTTRANSPARENT` over blank areas so hit-testing falls through to the parent's `HTCAPTION`.
- **The app icon is copied byte-for-byte** from `mstsc.exe`'s `RT_GROUP_ICON`/`RT_ICON` resources.
  `PrivateExtractIcons` plus `Icon.ToBitmap()` produces noise at the large sizes.
