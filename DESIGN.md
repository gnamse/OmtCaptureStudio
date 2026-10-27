# DESIGN.md — OMT Capture Studio

Reference for building and changing UI in this app. Derived from the shipped
implementation (`App.axaml`, `StudioPalette.cs`, `MainWindow.axaml`, code-behind
and ViewModels), not aspirational — anything here that disagrees with the code is
a bug in this file.

**Surface:** desktop app shell + video monitoring viewport.
**Mode:** Operate. The user is completing a task (connect to a stream, broadcast,
record). Scanability, consistency and native expectations outrank expression;
brand lives in precise details, not decoration.
**Non-goal:** this is not a marketing surface. Do not add gradients, hero
imagery, or display type to functional controls.

---

## 1. Platform & hard constraints

- Windows x64, **Avalonia 11** with `FluentTheme`, `RequestedThemeVariant="Dark"`.
- **Dark-only by design** (broadcast dim-use scene). Do not introduce a light theme.
- **Native AOT** (`PublishAot=true`): no reflection-driven styling, no dynamic resource lookups at construction time.
- Window: `Height="800" Width="1240" MinHeight="650" MinWidth="950"`. **950px is the floor** — every layout must still read correctly there.

---

## 2. Colour tokens (single source of truth)

Defined in `App.axaml`. Values are exact — do not approximate or "tidy" them.

| Token | Value | Use |
|---|---|---|
| `StudioBg` | `#0F0F12` | Window background, viewport letterbox |
| `StudioSurface` | `#18181B` | Top bar, recording bar |
| `StudioSurfaceElevated` | `#27272A` | Raised panels, inactive toggles |
| `StudioBorder` | `#27272A` | Default 1px borders |
| `StudioBorderSubtle` | `#3F3F46` | Dividers, the `·` separators |
| `StudioTextPrimary` | `#F4F4F5` | Live values, headings |
| `StudioTextSecondary` | `#A1A1AA` | Field labels, secondary status |
| `StudioTextMuted` | `#80808A` | Version, dormant readouts |
| `StudioTextOnAccent` | `#FFFFFF` | Text on any accent fill |
| `StudioAccentSuccess` | `#10B981` | Connect action |
| `StudioAccentDanger` | `#DC2626` | Record / destructive action |
| `StudioAccentPrimary` | `#4F46E5` | Test Signal active, host Broadcast active |
| `StudioHudScrim` | `#CC18181B` | Viewport HUD chips |
| `StudioModalScrim` | `#CC09090B` | Recording-close overlay |
| `StudioShadowModal` | `0 12 32 0 #CC09090B` | Recording-close modal drop shadow — a `BoxShadows`, not a colour |
| `StudioVuMeterGradient` | 7-stop, see §7 | VU meter bar fill |

> `App.axaml` declares each colour **twice** — once as `<Color x:Key="…Color">` and once
> as a `<SolidColorBrush x:Key="…">` with the same literal. If you add or change a
> colour you must update **both** entries or the layers drift.

> **Value-type tokens are declared once, as their own type.** Spacing (`Thickness`),
> radius (`CornerRadius`) and elevation (`BoxShadows`) have no colour half, so the
> double-declaration rule does not apply to them:
> `<BoxShadows x:Key="StudioShadowModal">0 12 32 0 #CC09090B</BoxShadows>`, consumed as
> `BoxShadow="{DynamicResource StudioShadowModal}"`. The XAML parser converts that text
> to the value type at compile time.
>
> A shadow **cannot** be tokenised as a string. `<x:String x:Key="X">0 12 32 0 #CC…</x:String>`
> consumed via `BoxShadow="{StaticResource X}"` does not work: Avalonia's resource path only
> converts `Color` → `IBrush` (`StaticResourceExtension` → `ColorToBrushConverter`), so a
> string resource is never parsed into a value type. Declare the token as the type itself.

### 2.1 The two-layer palette rule (critical)

There are exactly **two** sanctioned places colour may live:

1. **Declarative XAML** → the `Studio*` tokens above, consumed with `{DynamicResource …}`.
2. **Imperative code** (ViewModel / control code-behind) → `StudioPalette.cs` (`MainWindowViewModel` uses `ConnectBrush`, `TestSignalActiveBrush`, `RecordStartBrush`, …).

Both layers mirror the same hex values and cover different lookup contexts.

**`StudioPalette.cs` must stay `static`.** Never resolve brushes via
`Application.Current.Resources` at construction: the headless automated test suite
constructs `MainWindowViewModel` and controls from a plain console with **no Avalonia
`Application`**, and a runtime resource lookup throws there. This is a hard
constraint, not a preference.

**Banned:** inline hex literals in any `.axaml` other than `App.axaml`;
`Color.FromRgb` / `Color.Parse` anywhere outside `StudioPalette.cs`. Audit verified
0 violations — keep it that way.

### 2.2 Contrast floor (measured)

| Pair | Ratio | Result |
|---|---|---|
| `StudioTextOnAccent` on `StudioAccentPrimary` | **6.3:1** | AA text — pass |
| `StudioAccentPrimary` on `StudioBg` | **3.05:1** | Passes the 3:1 non-text floor — **only marginally** |
| `StudioTextMuted` on `StudioBg` | **4.90:1** | AA text — pass |
| `StudioTextMuted` on `StudioSurface` | **4.53:1** | AA text — pass, only **0.03 above** the 4.5:1 floor |

`StudioAccentPrimary` sits right at the edge for non-text use. If it is ever
lightened, it falls below the 3:1 floor — recheck rather than eyeballing.

`StudioTextMuted` on `StudioSurface` clears AA by 0.03 — the tightest pair in the app
(status bar, top bar, the `REC` chip). Any change to either token, or moving muted text
onto a different surface, must be re-measured, not eyeballed: `StudioTextMuted` on
`StudioSurfaceElevated` measures **3.81:1** and fails AA — do not put muted text there.

---

## 3. Spacing, radius, density

4px base scale — use the tokens, not raw numbers.

- **Spacing:** `SpaceXs` 4, `SpaceSm` 8, `SpaceMd` 12, `SpaceLg` 16, `SpaceXl` 24, `Space2xl` 32.
- **Bar padding:** `BarPadding` `16,8` for the top and recording bars; `StatusBarPadding` `16,0` for the status bar.
- **Radius:** `RadiusSm` 4 (chips, tags, brand badge), `RadiusMd` 8 (modals), `RadiusLg` 12.
- **Control height:** every toolbar control carries `MinHeight="30"`. Do not introduce a second height.
- Keep this dense. Broadcast operators read this at a glance across many hours; adding vertical air is a regression, not an improvement.

---

## 4. Typography

Two families only. No display fonts, no new weights beyond these roles.

- **UI / labels:** system sans. Field labels are `FontSize="12" FontWeight="SemiBold"` in `StudioTextSecondary` (e.g. `Source:`, `Target:`, `Audio:`, `Network:`). Section labels (`CONTAINER`, `ENCODER`, `QUALITY`, `SAVE FOLDER`) are `FontSize="10" FontWeight="Bold"` in `StudioTextSecondary`.
- **Numeric / telemetry:** `Consolas, monospace` — recording duration (`FontSize="15"`, bold), video specs (`FontSize="11"`), bitrate and drop counters. This makes live numbers scan as data, not prose.
- **Status / telemetry readouts:** `FontSize="11"` in `StudioTextSecondary`.
- **Empty-state:** `NO SIGNAL` is `FontSize="28" FontWeight="Black"` in `StudioTextMuted`, with a `FontSize="12"` instruction beneath it in `StudioTextSecondary`.

---

## 5. Layout rules

### 5.1 Cap every elastic control — and its column

This is the rule most easily broken and the one that made the header look
unprofessional when maximized. Star/proportional columns with
`HorizontalAlignment="Stretch"` and **no upper bound** absorb all leftover width:
at ~2560px the Source combobox stretched to roughly 1800px and Save Folder to
roughly 2200px, with the value stranded at the left edge.

**Cap the `ColumnDefinition` itself with `MaxWidth`** — capping only the control
is not enough, because the star column keeps growing and the capped control then
leaves dead space *inside* its column (measured: a 922px hole in the bottom bar).

Current caps (do not remove; raise only with a reason):

| Column | MaxWidth |
|---|---|
| Row 0 — Source | 340 |
| Row 1 — Target | 320 |
| Row 1 — Audio | 340 |
| Row 1 — Network | 280 |
| Recording bar — Save Folder | 460 |

Surplus space then falls at the **right edge** of the bar instead of inside or
between fields (measured maximized: Source row 53% right margin, broadcast row
29%). The recording bar's remaining gap is intentional — left settings, right
record actions.

> Controls may also carry their own `MaxWidth`; both are fine, but the column cap
> is what actually prevents the balloon.

### 5.2 Header structure

The top bar is a **two-row responsive grid** inside one `Border`:

- **Row 0** — brand, `Source:` picker, refresh, Connect/Disconnect, divider, Test Signal,
  **compact live-setting summary**, **Broadcast toggle**.
- **Row 1** — broadcast configuration: `Target:`, `Audio:`, `Network:`, refresh.
- Rows below: viewport + VU meter (star), recording config bar, status bar.

Do not collapse these back into one row; that is what caused the original crowding
at 950px. Preserve every `Grid.Column` assignment when editing — add to the row, do
not restructure it.

**NET-012 amendment — row 1 collapses until Broadcast is ON.** The broadcast configuration
row is `IsVisible`-bound to the **broadcast state** (`IsBroadcastRowVisible`), and the
Broadcast toggle moved up to row 0 because it is the entry point that reveals the row.

- Collapsing-when-off and locking-when-on are **different axes**. Collapsing is visibility;
  the selector locks (`IsMainContentEnabled` / `IsSourceSelectorEnabled` /
  `IsCaptureSelectorEnabled`) still apply while a broadcast is live.
- The row's visibility is keyed to **broadcast** state and never to **recording** state, so a
  take is never disturbed by the collapse. A broadcast switched off mid-recording collapses
  the row and the recording keeps running.
- While collapsed, row 0 carries a **compact live-setting summary** (`LiveSettingSummary`,
  e.g. `Desktop · SystemAudio`) so hiding the pickers never hides *which* target and audio are
  selected. It is `Polite` — descriptive, not an alarm.
- **NET-013 P1-01 — the summary's column is ELASTIC (`Width="*" MaxWidth="240"`).** At the 950px
  floor the summary must SHRINK (and `TextTrimming="CharacterEllipsis"`) rather than push the
  Broadcast toggle past the window edge; at wide sizes `MaxWidth` caps it at 240 so the surplus
  still falls at the right edge (§5.1) and the toggle does not drift. Measured before the fix:
  the summary occupied `716..956` and pushed `BtnDesktopCapture` to `966..1056` — 16px past a
  950px window, i.e. **the toggle was unreachable at the minimum size.** Any control placed to the
  right of an elastic summary must be an `Auto` column that the summary can never displace.
- Every row-1 `Grid.Column` assignment is preserved (including the now-unused column 8) so the
  row can be restored or extended without re-deriving the layout.

### 5.4 The recording bar is two tiers (NET-012)

The Operate-mode primary path is **pick source → connect → record**, so the bar shows only what
that path needs:

- **Defaults strip (always visible)** — PRESET, SAVE FOLDER (+ Browse/Open), duration, stats,
  Record, and the `Advanced ▾` disclosure.
- **Advanced tier (collapsed by default)** — CONTAINER, ENCODER, QUALITY, BACKUP FOLDER (+ Browse),
  ROLL OVER, OR SIZE, MAX LENGTH, START IN.

The defaults are already the right ones (MP4 / Auto Hardware / High / no rollover / no schedule),
so nothing is hidden that the primary path needs. Collapsing is **visibility only**: every control
still exists, still works, and still sits inside the `IsRecordingConfigEnabled` lock, so the
live-changing-selector rule is unchanged. Editing any control still switches the preset picker to
Custom from inside the tier.

The disclosure is a `ToggleButton` with a **drawn chevron** (`IconChevronDown` / `IconChevronUp`
`StreamGeometry`, §6 — no glyphs, no emoji), `MinHeight="30"` so it introduces no second control
height, named and tooltipped, and keyboard reachable. Expanded state is an observable
(`IsAdvancedRecordingOptionsExpanded`) so it is assertable headlessly.

**NET-013 P2-01 — the Advanced tier is ONE `StackPanel` at `Grid.Row="1" Grid.Column="0"
Grid.ColumnSpan="6`."** It holds all seven advanced pickers (CONTAINER, ENCODER, QUALITY,
ROLL OVER, OR SIZE, MAX LENGTH, START IN) in that order. Previously the codec pickers sat in
column 0, which the bar shares with the PRESET picker, so expanding Advanced widened that shared
`Auto` column by ~156px and starved Save Folder — measured: `TxtOutputDir` crushed to 64px and
overlapping `BtnBrowseDir` by 9px. Spanning six columns decouples row 1's width from column 0
entirely, so column 0 stays at the preset's width and Save Folder keeps its width in **both**
states. Do not put a row-1 control back into a single column.

Gaps between adjacent toolbar controls are **not** implicit: `BtnAdvancedRecording` carries
`Margin="8,0,0,0"` and `BtnTestSignal` carries `Margin="0,0,10,0"`, because two controls that abut
at 0px read as one control (NET-013 P2-02).

### 5.5 The broadcast row is three states (NET-014)

The broadcast row is not a toggle, it is a **reveal → select → start** sequence. The user's report:
*"when I click Broadcast, it disabled the controls. What I want is: when I click Broadcast, it should
show the controls and after my selection, there should be an extra button that says Start
Broadcasting."* Revealing used to start the send, so the pickers the operator still had to touch
appeared already locked.

| State | Row | Pickers | Row-0 button | In-row action |
|---|---|---|---|---|
| **1 Hidden** (default) | invisible | — | reveal | — |
| **2 Selecting** | visible | **enabled** | hide | `Start Broadcasting` |
| **3 Live** | visible | **locked** | stop + hide | `Stop Broadcasting` |

The live-lock key is the **live state**, not the reveal: pickers lock when a send is running, because
that is when a mid-flight change drops receivers. `BroadcastSessionState.IsCaptureSelectorEnabled`
(`!IsBroadcasting`) is already correct for this and did not change.

**A live broadcast is never orphaned.** Row visibility is
`IsBroadcastRevealed || IsDesktopCaptureActive` — OR'd with the live state deliberately, so clearing
the reveal flag cannot hide a running send. The row-0 button's role depends on the state for the same
reason: while OFF it reveals/hides, and while LIVE it **stops the send and returns to Hidden**
(stopped, never merely hidden). The in-row action stops but **keeps the panel revealed**, so the
operator can adjust and restart without the controls vanishing under the cursor.

The start still routes through `SetDesktopCaptureActiveAsync` and therefore through
`OmtDesktopCaptureGenerator` — the new button is a **UI-state change only**; the send lifecycle and
its single gate are untouched (the ADR-0005 analog).

The in-row action is `MinHeight="30"` (no second control height), named with the action it will
perform (`BroadcastActionAutomationName`), tooltipped, and keyboard reachable. It is **always
enabled** — binding `IsCaptureSelectorEnabled` here would disable *Stop* exactly when it is needed.
The selecting-state hint carries `AutomationProperties.LiveSetting="Polite"`: it is guidance, not an
alarm. The hint sits in the freed column 8 as an elastic `*` column (`MinWidth="0"`,
`TextTrimming="CharacterEllipsis"`, `MaxWidth="260"`), so it shrinks before it can push the action
button out of the window — the P1-01 lesson applied to row 1. At 950px the three pickers hold their
`MinWidth`s (130 + 140 + 130 = 400), the action needs ~150, labels and refresh ~216 of the 922
available, leaving ~156 for the hint, which trims.

### 5.3 The VU meter is a fixed column

`Grid.Column="1" Width="130"` beside the viewport. It must not stretch with the
window; its scale is calibrated.

---

## 6. Components

- **ComboBox** — used for every selection (sources, targets, audio devices, network, container/encoder/quality). Item templates are a single `TextBlock` bound to `DisplayName` with `TextTrimming="CharacterEllipsis"`.
- **ToggleButton** — only for state that is *live*: Test Signal and Broadcast. Background binds a `StudioPalette` brush; they are never plain buttons.
- **Button** — actions (Connect, Record, Refresh, Browse). Command-bound, never click-handlers.
- **Icon set** — drawn `StreamGeometry` in `App.axaml` (`IconRefresh`, `IconFolder`, `IconWarning`), rendered as `Path` at 14–15px with `StrokeThickness="1.8"`, `Stretch="Uniform"`. Add new icons the same way — no image files, no emoji, no font icons.

---

## 7. Feedback & state (Operate-mode essentials)

- **Status bar** is the primary channel for what just happened and why.
- **Live regions are mandatory.** `AutomationProperties.LiveSetting="Assertive"` on status / LIVE / record-stats text; `"Polite"` on high-frequency telemetry (bitrate, dropped frames, audio). Screen readers announce nothing without this.
- **Lock what cannot change mid-flight — and not a moment before.** Target, Audio and Network selectors are disabled while a broadcast is **live**; revealing the row for selection leaves them **enabled** (NET-014 §5.5). Locking on reveal made the pickers unusable exactly when the operator needed them. Source is disabled while connected. All of them require restarting the sender, so allowing edits mid-stream would silently drop receivers. Reuse the existing enabled-flag pattern rather than inventing a new one.
- **VU meter** uses `StudioVuMeterGradient` (`#10B981` → `#22C55E` → `#84CC16` → `#EAB308` → `#F59E0B` → `#EF4444` → `#DC2626`), calibrated −60 dBFS to 0 dBFS. Do not restyle it freely; it communicates level by colour.
- **Modals must trap focus.** The recording-close overlay cycles Tab/Shift+Tab between its two buttons so focus cannot reach the controls behind it.

---

## 8. Accessibility baseline

Non-negotiable for every interactive control:

1. `AutomationProperties.Name` — all 20 current controls have one. Keep it at 100%.
2. `ToolTip.Tip` — every control also carries one.
3. Keyboard reachability. Touch-target sizing (44pt) is a *mobile* rule and does not apply here.
4. New modals: focus trap + an initial focus target + `Name`/`HelpText` on the overlay.
5. New status text: choose `Assertive` vs `Polite` deliberately.

---

## 9. Pitfalls (learned the hard way)

- **Never let `AsioHostAudioSource`'s category drive layout.** Pro-audio complexity must not leak into the shell.
- **Do not structure the header by pixel-polishing at one width.** Verify at both `MinWidth` 950 and maximized.
- **Nothing may probe hardware in a ViewModel constructor.** The headless suite constructs ViewModels; ASIO driver probing is lazy and guarded for exactly this reason.
- **Static brushes only** in ViewModels (see §2.1).

---

## 10. Checklist before calling UI work done

- [ ] No new hex literals outside `App.axaml`; no `Color.FromRgb` outside `StudioPalette.cs`
- [ ] Every new interactive control has `AutomationProperties.Name` **and** a tooltip
- [ ] New status text has a deliberate `LiveSetting`
- [ ] Every new elastic control has a `MaxWidth`
- [ ] Verified at `MinWidth="950"` **and** maximized, not just the default 1240px
- [ ] `MinHeight="30"` on any new toolbar control
- [ ] Headless test suite still passes (ViewModel constructs with no Avalonia `Application`)
- [ ] Progressive disclosure: anything hidden behind a disclosure is **visibility only** — the
      control still exists, still works, and is still inside its enable/disable lock
- [ ] Any collapsed row still surfaces the live/selected information it hid (a compact summary),
      and its visibility is keyed to the axis that owns it (broadcast, not recording)
