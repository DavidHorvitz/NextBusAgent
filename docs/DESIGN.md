# BusPulse design system

Session 0 (2026-11-10). This is the spec for every later UI session. The implementation is [`BusPulse/Themes/Theme.xaml`](../BusPulse/Themes/Theme.xaml) (tokens and styles), [`Themes/ThemeTokens.cs`](../BusPulse/Themes/ThemeTokens.cs) (theme switching, contrast maths, line badge colours) and [`Views/Controls/DesignControls.cs`](../BusPulse/Views/Controls/DesignControls.cs) (`LineBadge`, `StatusChip`, `Banner`, `TickerText`, `Motion`). The reference screen is the Stops tab ([`Views/StopsView.xaml`](../BusPulse/Views/StopsView.xaml)). Tests: `DesignSystemTests` (tokens, contrast, font, line badges, no raw colours) and `DesignRenderTests` (renders the screen, the no-shift check, RTL mirroring).

## 1. UI library

**WPF-UI (lepoco/wpfui) 4.3.0, MIT.** Checked on NuGet on 2026-11-10: 4.3.0 is the latest release (published 2026-05-04) and ships a `net10.0-windows7.0` target, so it runs on our .NET 10 TFM without a compatibility shim. The project already referenced it, so no package changed.

Why WPF-UI:

- It provides the Fluent controls we need without writing them: `FluentWindow`, `TitleBar`, `NavigationView`, `Card`, `CardAction`, `CardControl`, `InfoBar`, `InfoBadge`, `Snackbar`, `ContentDialog`, `ToggleSwitch`, `NumberBox`, `AutoSuggestBox`, `ProgressRing`, plus `ApplicationThemeManager`, `SystemThemeWatcher`, `ApplicationAccentColorManager` and Mica/Acrylic backdrops.
- It ships Fluent System Icons (MIT) as `ui:SymbolIcon`, which `StatusChip`, `Banner` and the icon buttons use.
- It is already wired in (`FluentWindow` with Mica, `IThemeService`), so adopting it costs nothing.

Not the base:

- **The Fluent theme built into .NET (`Window.ThemeMode`, `Application.ThemeMode`)** is marked experimental (diagnostic `WPF0001`) and may change or be removed, so we do not build on it.
- **ModernWPF (kinnara, MIT)** is the fallback if WPF-UI blocks something we need. It has not been needed.

**RTL:** WPF-UI does not document right-to-left support. Every control we use has to be checked in Hebrew by hand (see the RTL checklist in section 7). What was checked in this session is listed there.

How our layer sits on top: WPF-UI provides the control templates and its own theme resources. Our tokens (`Bp.*`) colour everything we style ourselves, and `IThemeService` hands WPF-UI our accent (`Accent.9`) through `ApplicationAccentColorManager`, so WPF-UI controls such as the progress bar and focus states use the same blue.

## 2. Colour tokens

### Model

Five scales on the Radix Colors 12-step model, taken from **Radix Colors 3.0.0 (MIT, `@radix-ui/colors`)**. The hex values were generated from the package's CSS files (sRGB block), not typed by hand.

| Role | Radix scale | Used for |
| --- | --- | --- |
| Neutral | slate | Surfaces, borders, text |
| Accent | indigo | Primary actions, focus, selection, links |
| Success | green | "Live" / on time |
| Warning | amber | Delayed, stale data |
| Danger | red | Disruption, cancelled, "leave now", destructive actions |

**Accent: indigo,** because its hue is clearly apart from all three status colours (green, amber, red), so an accent-coloured control is never mistaken for a status, and it keeps the same solid step 9 in both themes. The previous accent was teal, which sat right next to "live" green.

Steps (the same for every scale):

| Step | Meaning |
| ---: | --- |
| 1 | App background |
| 2 | Subtle background (panels, the expanded departures area) |
| 3 | Component background, rest |
| 4 | Component background, hover |
| 5 | Component background, pressed or selected |
| 6 | Subtle border, non-interactive (dividers) |
| 7 | Subtle border, interactive (inputs, secondary buttons) |
| 8 | Strong border (hover border) |
| 9 | Solid fill, the colour itself |
| 10 | Solid fill, hover |
| 11 | Low-contrast text |
| 12 | High-contrast text |

### How tokens are used

- `Theme.xaml` holds every colour twice: `Bp.Light.<Name>` and `Bp.Dark.<Name>` (for example `Bp.Light.Neutral.3`).
- **Views never use those keys.** They use the alias brush `Bp.<Name>` through `DynamicResource`: `{DynamicResource Bp.Neutral.3}`, `{DynamicResource Bp.Text.Primary}`.
- `ThemeTokens.Apply` writes one frozen brush per alias into the application resources whenever the theme changes (`IThemeService`, also on a Windows theme switch while "system" is selected). `DesignSystemTests` fail if the light and dark sets do not have exactly the same keys.
- Role aliases are entries like `<sys:String x:Key="Bp.Light.Text.Primary">Neutral.12</sys:String>`: they name a step of the same mode, so a role can point at a different step in dark. (`<StaticResource x:Key>` entries were tried first; they lost keys when the dictionary loaded.)

Role aliases:

| Alias | Light | Dark | Note |
| --- | --- | --- | --- |
| `Bg.App` | Neutral 1 | Neutral 1 | Under Mica the window shows the backdrop instead |
| `Bg.Subtle` | Neutral 2 | Neutral 2 | |
| `Bg.Control` | Neutral 3 | Neutral 3 | |
| `Bg.ControlHover` | Neutral 4 | Neutral 4 | |
| `Bg.ControlPressed` | Neutral 5 | Neutral 5 | |
| `Border.Subtle` | Neutral 6 | Neutral 6 | Dividers |
| `Border.Control` | Neutral 7 | Neutral 7 | |
| `Border.Strong` | Neutral 8 | Neutral 8 | |
| `Text.Primary` | Neutral 12 | Neutral 12 | |
| `Text.Secondary` | Neutral 11 | Neutral 11 | |
| `Text.Disabled` | Neutral 8 | Neutral 8 | Disabled text is exempt from contrast minimums |
| `Text.Link` | Accent 11 | Accent 11 | Quiet buttons |
| `Text.OnSolid` | `#FFFFFF` | `#FFFFFF` | Only on accent 9 / primary states |
| `Focus` | Accent 9 | Accent 9 | **Not step 8:** accent 8 on neutral 2 is 2.30:1 in light, below 3:1 |
| `Primary.Rest` | Accent 9 | Accent 9 | |
| `Primary.Hover` | Accent 10 | **Accent 8** | White on dark accent 10 is 4.28:1, below 4.5 |
| `Primary.Pressed` | Accent 11 | Accent 7 | |

### Token table

Light:

| Step | Neutral | Accent | Success | Warning | Danger |
| ---: | --- | --- | --- | --- | --- |
| 1 | `#FCFCFD` | `#FDFDFE` | `#FBFEFC` | `#FEFDFB` | `#FFFCFC` |
| 2 | `#F9F9FB` | `#F7F9FF` | `#F4FBF6` | `#FEFBE9` | `#FFF7F7` |
| 3 | `#F0F0F3` | `#EDF2FE` | `#E6F6EB` | `#FFF7C2` | `#FEEBEC` |
| 4 | `#E8E8EC` | `#E1E9FF` | `#D6F1DF` | `#FFEE9C` | `#FFDBDC` |
| 5 | `#E0E1E6` | `#D2DEFF` | `#C4E8D1` | `#FBE577` | `#FFCDCE` |
| 6 | `#D9D9E0` | `#C1D0FF` | `#ADDDC0` | `#F3D673` | `#FDBDBE` |
| 7 | `#CDCED6` | `#ABBDF9` | `#8ECEAA` | `#E9C162` | `#F4A9AA` |
| 8 | `#B9BBC6` | `#8DA4EF` | `#5BB98B` | `#E2A336` | `#EB8E90` |
| 9 | `#8B8D98` | `#3E63DD` | `#30A46C` | `#FFC53D` | `#E5484D` |
| 10 | `#80838D` | `#3358D4` | `#2B9A66` | `#FFBA18` | `#DC3E42` |
| 11 | `#60646C` | `#3A5BC7` | `#218358` | `#AB6400` | `#CE2C31` |
| 12 | `#1C2024` | `#1F2D5C` | `#193B2D` | `#4F3422` | `#641723` |

Dark:

| Step | Neutral | Accent | Success | Warning | Danger |
| ---: | --- | --- | --- | --- | --- |
| 1 | `#111113` | `#11131F` | `#0E1512` | `#16120C` | `#191111` |
| 2 | `#18191B` | `#141726` | `#121B17` | `#1D180F` | `#201314` |
| 3 | `#212225` | `#182449` | `#132D21` | `#302008` | `#3B1219` |
| 4 | `#272A2D` | `#1D2E62` | `#113B29` | `#3F2700` | `#500F1C` |
| 5 | `#2E3135` | `#253974` | `#174933` | `#4D3000` | `#611623` |
| 6 | `#363A3F` | `#304384` | `#20573E` | `#5C3D05` | `#72232D` |
| 7 | `#43484E` | `#3A4F97` | `#28684A` | `#714F19` | `#8C333A` |
| 8 | `#5A6169` | `#435DB1` | `#2F7C57` | `#8F6424` | `#B54548` |
| 9 | `#696E77` | `#3E63DD` | `#30A46C` | `#FFC53D` | `#E5484D` |
| 10 | `#777B84` | `#5472E4` | `#33B074` | `#FFD60A` | `#EC5D5E` |
| 11 | `#B0B4BA` | `#9EB1FF` | `#3DD68C` | `#FFCA16` | `#FF9592` |
| 12 | `#EDEEF0` | `#D6E1FF` | `#B1F1CB` | `#FFE7B3` | `#FFD1D9` |

### Measured contrast

WCAG 2.x contrast ratio, computed from the token values by `ThemeTokens.Contrast`. Every row is asserted in `DesignSystemTests.TokenPairs_MeetTheirContrastMinimum_InBothThemes`, so changing a token that breaks a pair fails the build. Minimums: 4.5 for text, 7 for primary text on the backgrounds (the brief's "steps 11 and 12 readable on step 2"), 3 for icons and focus rings.

| Foreground | Background | Use | Min | Light | Dark |
| --- | --- | --- | ---: | ---: | ---: |
| Neutral 11 | Neutral 2 | Secondary text on subtle background (the brief's requirement) | 4.5 | **5.65** | **8.45** |
| Neutral 12 | Neutral 2 | Primary text on subtle background (the brief's requirement) | 7.0 | **15.58** | **15.15** |
| Neutral 11 | Neutral 1 | Secondary text on app background | 4.5 | 5.79 | 9.06 |
| Neutral 12 | Neutral 1 | Primary text on app background | 7.0 | 15.98 | 16.25 |
| Neutral 11 | Neutral 3 | Caption on a hovered row | 4.5 | 5.22 | 7.64 |
| Neutral 12 | Neutral 4 | Neutral line badge | 4.5 | 13.41 | 12.43 |
| White | Primary.Rest | Primary button | 4.5 | 5.21 | 5.21 |
| White | Primary.Hover | Primary button, hover | 4.5 | 6.02 | 6.11 |
| White | Primary.Pressed | Primary button, pressed | 4.5 | 6.00 | 7.65 |
| Neutral 12 | Neutral 5 | Secondary button, pressed | 4.5 | 12.55 | 11.26 |
| Accent 11 | Neutral 3 | Quiet button, hover | 4.5 | 5.27 | 7.72 |
| Accent 11 | Neutral 4 | Quiet button, pressed | 4.5 | 4.91 | 7.00 |
| Danger 11 | Neutral 3 | Destructive button, rest | 4.5 | 4.58 | 7.55 |
| Danger 11 | Danger 3 | Destructive button, hover | 4.5 | 4.54 | 7.75 |
| Danger 12 | Danger 4 | Destructive button, pressed | 4.5 | 9.72 | 10.78 |
| Accent 12 | Accent 4 | Selected suggestion row, checked chip | 4.5 | 10.89 | 9.93 |
| Accent 12 | Accent 5 | Checked chip, pressed | 4.5 | 9.84 | 8.39 |
| Success 12 | Success 3 | Chip word "live" | 4.5 | 11.00 | 11.45 |
| Warning 12 | Warning 3 | Chip word "delayed", "not updated" | 4.5 | 10.47 | 12.98 |
| Danger 12 | Danger 3 | Chip word "cancelled" | 4.5 | 10.84 | 11.95 |
| Neutral 12 | Neutral 3 | Chip word "scheduled" | 4.5 | 14.41 | 13.70 |
| Success 11 | Success 3 | Chip icon | 3.0 | 4.21 | 7.86 |
| Warning 11 | Warning 3 | Chip icon | 3.0 | 4.25 | 10.26 |
| Danger 11 | Danger 3 | Chip icon | 3.0 | 4.54 | 7.75 |
| Accent 12 | Accent 3 | Info banner | 4.5 | 11.80 | 11.59 |
| Accent 11 | Neutral 2 | Legacy `AccentBrush` text | 4.5 | 5.70 | 8.53 |
| Warning 12 | Neutral 2 | Legacy `WarningTextBrush` | 4.5 | 10.81 | 14.52 |
| Danger 11 | Neutral 2 | Legacy `ErrorTextBrush` | 4.5 | 4.96 | 8.35 |
| Focus (Accent 9) | Neutral 1 | Focus ring | 3.0 | 5.08 | 3.62 |
| Focus (Accent 9) | Neutral 2 | Focus ring | 3.0 | 4.95 | 3.38 |

Pairs measured and **rejected**, and what replaced them:

| Pair | Ratio | Instead |
| --- | ---: | --- |
| Accent 8 focus ring on neutral 2 (light) | 2.30 | Focus ring is accent 9 |
| Success 11 text on success 3 (light) | 4.21 | Chip text is step 12; step 11 only for the icon |
| Warning 11 text on warning 3 (light) | 4.25 | Same |
| Warning 11 as text on neutral 2 (light) | 4.38 | Warning text is step 12 |
| White on dark accent 10 (primary hover) | 4.28 | Dark hover is accent 8 |
| Danger 11 on danger 4 / danger 5 (light) | 4.07 / 3.69 | Destructive rests on neutral 3, turns red on hover, text goes to step 12 when pressed |
| White on danger 9 (light) | 3.91 | No text on solid danger at all |
| White on success 9 (light) | 3.16 | No text on solid success at all |

The header gradient (still in `MainWindow`, not yet rebuilt) runs accent 12 → 9 in light and accent 3 → 6 in dark; white text measures at least 5.2:1 on all four ends (`HeaderGradient_KeepsWhiteTextReadable`).

## 3. Typography

- **Family: Heebo,** embedded from Google Fonts (static cuts Regular, Medium, SemiBold and Bold, v28, downloaded 2026-11-10). Licence checked in the download: **SIL Open Font License 1.1**, "Copyright 2014 The Heebo Project Authors (https://github.com/OdedEzer/heebo)", with no Reserved Font Name. The licence file is [`BusPulse/Assets/Fonts/OFL.txt`](../BusPulse/Assets/Fonts/OFL.txt) and is copied next to the executable.
- Heebo over Rubik: Heebo's Latin is Roboto-based with **tabular digits by default** (all ten digits have the same advance width, asserted in `Heebo_IsEmbedded_WithTabularDigits_AndHebrew` for all four weights), which the countdowns need; Rubik is rounder and less compact at the sizes we use.
- **Static cuts, not the variable font:** WPF renders only a variable font's default instance and would synthesize the bold. The test checks each weight resolves to a real cut.
- **Fallback: Segoe UI** (`Bp.Font` is `…/Assets/Fonts/#Heebo, Segoe UI`), used per character for any glyph Heebo lacks. Heebo covers the Hebrew letters and the geresh (U+05F3) in "דק׳" (checked).
- Applied app-wide through `FontFamily` on `MainWindow`. Do not add another family.

Type scale (`Bp.Type.*` TextBlock styles, sizes in `Bp.FontSize.*`):

| Style | Size | Weight | Colour | Use |
| --- | ---: | --- | --- | --- |
| `Display` | 26 | Bold | Text.Primary | The ETA number, nothing else |
| `Title` | 16 | SemiBold | Text.Primary | Stop names, section headings |
| `Body` / `BodyStrong` | 14 | Regular / SemiBold | Text.Primary | Destinations, content |
| `Caption` | 12 | Regular | Text.Secondary | Codes, distances, operator, units |
| `Hint` | 12 | Regular, wraps | Text.Secondary | The one hint line per section |

**Countdowns never move the layout.** Three things together:

1. Tabular digits (Heebo default).
2. The number sits in a box with `MinWidth` 32 (`Bp.EtaNumberMinWidth`), wide enough for two digits at Display size, aligned towards the unit.
3. The ETA column is a fixed 96 px (`Bp.EtaColumnWidth`), which also fits "עכשיו" / "Now".

Verified by `Countdown_FromNineToTen_DoesNotMoveTheRow` (Hebrew and English): it renders the Stops screen, changes one row from 10 to 9 to 99 minutes and compares the position and width of every badge, chip and text (the unit included) — they are identical. It also checks that every departure row keeps the 96 px column with "Now" in it. A mutation check (ETA column set to `Auto`) makes the test fail.

## 4. Components

All in `Theme.xaml`; light and dark come from the tokens, so every component below has both.

### LineBadge (`controls:LineBadge`)

A 28 px pill with the line number (14 px bold).

- **Fill from GTFS `routes.txt` `route_color`, text from `route_text_color`.** No invented colours, operator or otherwise.
- **No colour in the feed:** a neutral pill (neutral 4 fill, neutral 7 border, text primary) and the **operator name** next to it.
- **Text contrast:** the feed's text colour is kept if it reaches 4.5:1 on the fill; otherwise black or white, whichever contrasts more (`LineBadgeColors.Resolve`). A missing `route_text_color` means black (GTFS default).

What the Ministry of Transport feed actually contains (measured on the cached feed of 2026-11-10): 7,855 routes, **1,380 with a `route_color`**, only four values: `FF9933` (1,112 routes), `33CC33` (133), `9933FF` (126) and `3399FF` (9). They appear across operators (Egged, Dan, Kavim, Metropoline, Superbus and others), so they mark a service type, not an operator. The feed has **no `route_text_color` column.** Black on `9933FF` is 4.27:1, so that badge falls back to white (4.92:1); the other three keep black. Covered by `LineBadge_UsesFeedColours_AndFallsBackByLuminance`.

**Today `Arrival` carries no route colour,** so the Stops screen shows neutral badges with the operator. Passing `route_color` through from the feed to `Arrival` is a later session's data change (it is feature work, not part of this session).

### StatusChip (`controls:StatusChip`)

A 24 px pill, always **icon + word**, never colour alone. Fluent System Icons from WPF-UI.

| Status | Word (he / en) | Icon | Colours |
| --- | --- | --- | --- |
| `Live` | חי / live | `Live24` | success 3 fill, success 11 icon, success 12 word |
| `Scheduled` | מתוכנן / scheduled | `CalendarClock24` | neutral 3 / 11 / 12 |
| `Delayed` | מתעכב | `Clock24` | warning 3 / 11 / 12 |
| `Cancelled` | מבוטל | `DismissCircle24` | danger 3 / 11 / 12 |
| `Stale` | לא עדכני / not updated | `Warning24` | warning 3 / 11 / 12 |

`Stale` is live data older than 120 s (it was a 50 % opacity row before). No provider reports delays or cancellations yet, so `Delayed` and `Cancelled` are styled but have no strings or data source; add the strings with the source.

### DepartureRow

One row per departure, never a card. Grid: **line badge | destination | status chip | ETA**.

- Badge and status columns use `SharedSizeGroup`, so destinations line up across rows of one stop.
- The ETA is the hero: `Bp.Eta.Number` (Display, Bold, text primary, in a `TickerText` that cross-fades) plus the unit in Caption. Fixed 96 px column.
- Destination: Body, one line, ellipsis.
- `Bp.DepartureRow`: minimum height 48, padding 8/4, a neutral 6 divider below, no background at rest. The source and age go in the tooltip.

### Buttons (`Bp.Button.*`)

Radius 8, minimum height 32, padding 16/6, Medium weight. Focus is a 2 px ring in `Focus` drawn only on **keyboard** focus (`FocusVisualStyle`, i.e. focus-visible).

| Variant | Rest | Hover | Pressed | Disabled |
| --- | --- | --- | --- | --- |
| `Primary` | accent 9 fill, white | Primary.Hover | Primary.Pressed | neutral 3 fill, neutral 8 text |
| `Secondary` | neutral 3 fill, neutral 7 border, text primary | neutral 4, border neutral 8 | neutral 5 | neutral 2, border neutral 6, neutral 8 text |
| `Quiet` | transparent, accent 11 text | neutral 3 | neutral 4 | neutral 8 text |
| `Destructive` | neutral 3 fill, danger 7 border, danger 11 text | danger 3, border danger 8 | danger 4, danger 12 text | as secondary |
| `Icon` | Quiet, 32×32, text secondary, one `ui:SymbolIcon` | | | |
| `ListRow` | Full-width row button, transparent, min 48 | neutral 3 | neutral 4 | |

Icon buttons must set `AutomationProperties.Name`.

### Inputs

- `Bp.TextBox`: neutral 1 fill, neutral 7 border, radius 8, min height 32; border neutral 8 on hover; 2 px `Focus` border while focused (mouse or keyboard, because the caret is there). For a placeholder or clear button use `ui:TextBox` with `Bp.TextBox` brushes; check its RTL by hand.
- `Bp.SuggestionRow` (`ListBoxItem`): min 40, radius 8; hover neutral 3; selected accent 4 with accent 12 text.
- `Bp.ToggleChip` (`ToggleButton`): pill, neutral 3 / border neutral 7; checked accent 4, border accent 8, accent 12 text. For mutually exclusive time modes (now / depart at / arrive by).

### Banner (`controls:Banner`)

For offline, no-service and location messages. Radius 8, 1 px border, icon + wrapped text. `Tone`: `Info` (accent 3 fill, accent 6 border, `Info24`), `Offline` (neutral, `WifiOff24`), `Warning` (`Warning24`), `Danger` (`ErrorCircle24`). Text is step 12 of the tone. At most one per section.

### Skeleton (`Bp.Skeleton`)

Neutral 4 blocks, radius 4, shaped like the content they replace, pulsing opacity 1 → 0.55 (900 ms, reversing). **First load only:** the Stops list shows three skeleton rows while it loads with nothing to show; an expanded stop shows two while its first arrivals load. A reload of the stop list keeps the old list and shows a 2 px progress bar; the 25 s refresh of expanded stops updates the rows in place.

## 5. Layout and density rules

These are rules for every later session.

1. **One 8 px grid.** Margins and paddings are multiples of 8 (`Bp.Space.1..4` = 8, 16, 24, 32). 4 is allowed only inside a component (chip and badge padding, icon-to-word gaps).
2. **Corner radius 8 for controls, 12 for cards** (`Bp.Radius.Control`, `Bp.Radius.Card`). Pills (badges, chips, toggle chips) are fully rounded.
3. **Cards group related content only.** A list is rows with dividers, never a stack of cards. (The Stops screen uses one subtle card for the departures of an expanded stop, because they belong together.)
4. **At most one short hint line per section.** No repeated labels: if the column says it, the row does not.
5. **Advanced options live behind a "מתקדם" expander, closed by default.**
6. **Settings is a gear icon in the top corner that opens a side panel.** Nothing from settings shows in the main view (done in session 1, section 11).

## 6. Motion rules

- Durations **150–250 ms** (`Motion.Standard` = 200 ms), ease-out.
- **Content that changes fades and slides** (`Motion.FadeInOnShow`: opacity 0 → 1 and 8 px down to 0, used on the expanded departures and the banner). **Items that stay put do not move.**
- **Numbers cross-fade** (`TickerText`: the old value fades out while the new one fades in, in the same place).
- **Hover is a background change,** never a bounce, a scale or a shadow.
- **Reduced motion:** `Motion.IsReduced` reads the Windows "Animation effects" setting (Settings → Accessibility → Visual effects; `SystemParameters.ClientAreaAnimation`, i.e. `SPI_GETCLIENTAREAANIMATION`) at the start of every animation. When it is off, nothing animates: text swaps instantly, panels appear without a fade, skeletons do not pulse. Tests and screenshots set `Motion.ForceReduced`.
- Not verified on a real screen yet: the animations with the Windows setting on and off.

## 7. RTL

`FlowDirection` is set at the window root (`MainWindow`, bound to `ShellViewModel.FlowDirection`). Layout mirrors from there: `Margin`, `Padding`, grid columns, `HorizontalAlignment` and `TextAlignment` are all **logical** (Left = start), so write them for LTR and let RTL mirror them.

Must **not** mirror, and how:

| Element | Why | How |
| --- | --- | --- |
| Logos and illustrations (the bus in the header) | Physical drawing | `FlowDirection="LeftToRight"` on the element (already done for the bus `Viewbox`) |
| Media-style icons (play, forward) | Convention | Font icons are safe (below); a `Path` needs `FlowDirection="LeftToRight"` |
| Arrows whose meaning is physical (compass bearing, `DirectionArrow`) | Points at a real direction | `FlowDirection="LeftToRight"` (`DirectionArrow` already never mirrors) |
| Maps, charts with a time axis | Physical axes | `FlowDirection="LeftToRight"` |

Must mirror: arrows that follow the reading direction, such as "בני ברק ◄ תל אביב" (origin → destination). Write them as text in the localized string, or choose the glyph by direction, so the arrow points the way the text reads.

Measured in `Rtl_SymbolIconsAreNotMirrored_ButPathsAre`:

- **`ui:SymbolIcon` (Fluent font icons) does not mirror** in RTL; it is drawn as text. A directional font icon such as `ArrowRight24` must therefore be swapped for its counterpart in RTL when its meaning follows the reading direction.
- **`Path` / `Geometry` does mirror** with the layout. A path arrow follows the reading direction for free; a physical one needs `FlowDirection="LeftToRight"`.

### Checklist for every screen (later sessions)

- [ ] Render the screen in Hebrew and English, light and dark (`DesignRenderTests` pattern, or the app with `--theme=` and the language switch).
- [ ] Start edge: the first column (badge, star, icon) is on the right in Hebrew.
- [ ] Numbers, times and countdowns read correctly in Hebrew: "9 דק׳" shows the number to the right of the unit. Times like "17:42" are not reversed.
- [ ] **Mixed Hebrew + digits in one string changes order with the flow.** Line "54א" renders as **"א54" in the Hebrew UI and "54א" in the English UI** (seen in the screenshots; this is the Unicode bidi algorithm). Decide per field whether that is acceptable. For line badges it currently follows the UI direction like any Hebrew page would; **open question for the maintainer:** should line names render in one fixed order? If so, set `FlowDirection="LeftToRight"` on the badge text.
- [ ] Hebrew names in the English UI (stop names come from the feed in Hebrew) render right-to-left inside an LTR row and are trimmed at the correct end. Seen correct on the Stops screen.
- [ ] Punctuation at the end of a mixed string (".", ")", "׳") lands on the correct side. Check every banner and hint in both languages.
- [ ] **Overlay scrollbars sit on the left in RTL.** WPF-UI's scrollbar overlaps the end edge of the content: give scrollable lists a logical end margin of 16 (`Margin="0,0,16,0"`, which mirrors). Seen in the running app before the fix.
- [ ] `PrintWindow` captures of an RTL window come out mirrored (caption buttons on the wrong side): take screenshots from screen pixels or offscreen rendering, not `PrintWindow`.
- [ ] Physical arrows, logos and maps carry `FlowDirection="LeftToRight"`.
- [ ] Font icons that imply a direction are swapped in RTL; path icons are checked for mirroring.
- [ ] WPF-UI controls used for the first time (`AutoSuggestBox`, `NumberBox`, `ContentDialog`, `NavigationView`, `Snackbar`) are checked in Hebrew by hand: text alignment, caret and clear-button side, dropdown position.

Checked by hand in this session (screenshots in `docs/screens/session-0/` and two launches of the real app): the Stops tab in Hebrew and English, light and dark (the running app was seen in Hebrew only); `TabControl` headers; the WPF-UI `ListBox` and `ScrollViewer`; our buttons, chips, badges, toggle chips, text box, suggestion rows, banners and skeleton in Hebrew.

## 8. Do not

- **No raw colours in views.** No hex values, no `White`/`Black`/named colours, no `SystemColors`. Use `{DynamicResource Bp.*}`. `RebuiltViews_UseNoRawColours` enforces it for rebuilt views; add each view to it when it is rebuilt.
- **No colour-only status.** Every status has an icon and a word.
- **No new fonts.** Heebo, with Segoe UI as fallback.
- **No card per row.** Lists are rows.
- **No invented line or operator colours.** Only the feed's `route_color`.
- **No text on solid success or danger (step 9),** and no step-11 text on step-3 status fills (measured below 4.5 in light).
- **No hover bounce, scale or shadow.** Background change only.
- **No animation that ignores reduced motion.** Go through `Motion`.
- **Nothing scheduled presented as live** (an app rule; the chip makes it visible).
- **Do not put the theme dictionary first in `App.xaml`:** `ResourceLocalizer` swaps `MergedDictionaries[0]` (guarded by `Theme_IsMergedAfterTheStringsDictionary`).

## 9. Migration state

- **Rebuilt on the design system:** the Stops tab, the shell (section 11) and the settings panel.
- **Legacy brushes, mapped onto tokens** by `IThemeService` so the other screens follow the palette without a rebuild: `AccentBrush` → accent 11 (it is used as text), `LiveBadgeBrush` → success 3, `ScheduledBadgeBrush` → neutral 3, `WarningTextBrush` → warning 12, `ErrorTextBrush` → danger 11, `HeaderGradientBrush` → accent 12 → 9 (light) / accent 3 → 6 (dark). Remove each key when the last view using it is rebuilt.
- **Still raw colours, for later sessions:** (the `MainWindow` header with the bus illustration was removed in session 1) `TripPlannerView`, `LinesView`, `TimeWheelPicker`. `OperatorBrushConverter` (trip planner) **invents operator colours,** which breaks the LineBadge rule; replace it with `LineBadge` when the planner is rebuilt.
- The app icons (`Assets/Icons`, generated by `tools/generate_icons.py`) are still teal.

## 10. Screenshots

`docs/screens/session-0/`, rendered offscreen with sample data (fixed clock 2026-11-10 17:42, made-up stops around Bnei Brak, no real location):

- `stops-he-light.png`, `stops-he-dark.png`, `stops-en-light.png`, `stops-en-dark.png`: the reference screen with one pinned stop and two expanded stops (live, scheduled, stale and "now" rows).
- `stops-he-light-loading.png`: first load (skeleton).
- `components-he-light.png`, `components-he-dark.png`: every component in Hebrew.

Regenerate (PowerShell):

```powershell
$env:BUSPULSE_SCREENSHOT_DIR = "$PWD\docs\screens\session-0"; dotnet test .\BusPulse.sln --filter "FullyQualifiedName~DesignRenderTests"; Remove-Item Env:BUSPULSE_SCREENSHOT_DIR
```

Offscreen rendering has no Mica: the background is token step 1. In the app the window shows Mica behind the content.

## 11. Shell (session 1)

Implemented in [`MainWindow.xaml`](../BusPulse/MainWindow.xaml) (window, title bar, gear), [`Views/ShellView.xaml`](../BusPulse/Views/ShellView.xaml) (navigation and settings panel) and [`Views/SettingsView.xaml`](../BusPulse/Views/SettingsView.xaml). Offscreen tests: `ShellRenderTests`; screenshots in `docs/screens/session-1/`.

- **Window:** WPF-UI `FluentWindow`, `WindowBackdropType="Mica"`, `ExtendsContentIntoTitleBar`, `ui:TitleBar`. Mica needs Windows 11; where it is missing (this machine is Windows 10, build 19045) WPF-UI leaves the plain window background, which the design tokens already cover, so no code branch is needed. Mica itself has not been seen on screen.
- **Navigation:** `ui:NavigationView`, `PaneDisplayMode="Left"`, 168 px, no toggle, no back button, three items: Stops, Trip planner, Lines (Fluent icons `Location24`, `Directions24`, `TextBulletListLtr24`). The pane sits at the **start edge**: left in English, right in Hebrew (asserted by `Navigation_SitsAtTheStartEdge_AndSettingsPanelAtTheEnd`, seen in the running app). Pages are created once per tab by `ShellView` (it is the `INavigationViewPageProvider`) and keep their state; `ShellViewModel.SelectedTabIndex` and the navigation stay in sync both ways.
- **Settings:** a gear icon button in the title bar's trailing content (`Bp.Button.Icon`, tooltip and automation name "Settings") opens a 400 px panel at the **end edge** (right in English, left in Hebrew) over a 60 % scrim. Closed at startup and never saved. Closes with Esc, the close button, the scrim or the gear. Opening moves keyboard focus into the panel. Contents: General (appearance, language, notifications, lead time), Home and work (with the address search for adding places), Calendar, then the closed **Advanced** expander (refresh interval, test notification, address-search switch and privacy line, add by stop code, clear favourites and history, provider status, secrets source, the user-session notice). The brand header, the mute and hide buttons and the footer notice are gone from the main view: the window's X hides to the tray, mute is the Notifications checkbox.
- **Keyboard:** Ctrl+1..3 select a tab, **Ctrl+4 and Ctrl+comma open the settings panel** (there are three tabs now; 4 used to be the Settings tab), Esc closes the panel, F5 refreshes, Ctrl+F opens the planner search. The launch flag `--tab=3` therefore opens the panel.
- **Density:** pages get a 16 px start/end margin from the shell; the settings panel uses flat sections with 1 px dividers, 16 px between sections, 4 px label-to-field gaps, no cards.
- **Theme:** `IThemeService` (`ApplicationThemeManager`, `SystemThemeWatcher` while "system") is unchanged; the choice is saved in the config (`SelectedTheme` → `AppConfig.Theme`) and applied before the first frame.
- **Still open for later sessions:** the Trip planner and Lines views are legacy (default WPF-UI controls and cards); the planner shows one summary line in black text in dark mode; the legacy `HeaderGradientBrush` mapping in `ThemeService` has no user any more and can be removed together with its test; the nav pane plus a 400 px panel is tight at the 440 px minimum width.
