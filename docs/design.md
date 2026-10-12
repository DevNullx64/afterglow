# Design notes

How Afterglow's interface is put together, and the critique the 1.5 redesign started from.
The rules live in code in [Theme/Palette.xaml](../src/Afterglow.App/Theme/Palette.xaml) and
[Theme/Controls.xaml](../src/Afterglow.App/Theme/Controls.xaml); this page explains them.

## Sources

- Windows 11 typography — type ramp, minimum sizes, sentence case:
  <https://learn.microsoft.com/windows/apps/design/signature-experiences/typography>
- Windows 11 geometry — 8 px containers, 4 px in-page controls:
  <https://learn.microsoft.com/windows/apps/design/signature-experiences/geometry>
- Spacing in 4-epx steps, 24 epx page gutters:
  <https://learn.microsoft.com/windows/apps/develop/ui/alignment-margin-padding>
- WCAG 2.2 SC 1.4.3 (text contrast 4.5:1) and SC 1.4.11 (control boundaries 3:1):
  <https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum>,
  <https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast>

## The system

**Colour.** One dark palette with an ember accent. Contrast against a card (`Bg2` #191F28),
computed with the WCAG relative-luminance formula:

| Token | Hex | On a card | Use |
|---|---|---:|---|
| Text | #E8EDF4 | 14.1:1 | everything you read first |
| TextMuted | #97A1AE | 6.3:1 | labels, status lines, secondary values |
| TextFaint | #808B99 | 4.8:1 | explanations, hints, footnotes (was #5D6875, 2.9:1) |
| TextDisabled | #5D6875 | 2.9:1 | disabled controls only (exempt from SC 1.4.3) |
| ControlStrong | #6B7684 | 3.6:1 | switch, checkbox and radio outlines (SC 1.4.11) |
| Accent | #FF8A3C | 7.1:1 | primary actions, selection, the live marker |
| OnAccent | #141414 | 7.9:1 on Accent | text on accent fills |

Series colours (clock blue, temperature red, power amber, load green, VRAM violet, fans
cyan, FPS orange, voltage gold) are shared by tiles, graphs and the overlay.

**Type.** The Windows 11 ramp: Caption 12, Body 14, Body Strong 14 semibold, Title 28.
Nothing is smaller than 12 px. UI copy uses Segoe UI Variable *Text*, titles and big
numbers its *Display* cut (WPF doesn't drive the font's optical-size axis, so the two
named instances are picked explicitly). Sentence case everywhere, including labels.

**Space and shape.** A 4-epx grid: cards pad 16 and sit 12 apart, page gutters are 24,
groups are 24 apart. Cards are 8 px round, controls 4 px; controls are 32 px tall. Form
pages are one width (1000 px max), so the right edge doesn't jump between pages.

**Components.** Primary button (one per card), secondary, subtle (row actions), toggle
switch (on/off settings), checkbox (pick several), segmented control (pick one of a few
modes), slider rows (label + hint | slider | value), warning banner, inset panel (logs,
reports), status text that takes no room while empty.

## The 1.5 critique

Screenshots of all eight pages at 1280 × 1900 and at the minimum window size (1024 × 660),
reviewed against the rules above. The findings, worst first:

1. **Primary buttons with plain-text labels drew near-white text on orange (2.0:1).** The
   app-wide text style set a foreground, which outranks inheritance, so the button's dark
   label colour never reached its text. The same bug meant disabled buttons kept bright
   text and looked clickable. Fixed by giving each button template its own text style.
2. **Explanations were 2.9:1 against their card** — the copy that explains what each
   feature does was the least readable text on the page.
3. **No disabled look** for sliders and switches (e.g. Tuning without admin rights).
4. **Tuning's Apply button was off-screen** at the minimum window size, below six cards.
5. **Empty containers**: 120 px log boxes with nothing in them, empty progress bars,
   reserved status lines, a session history with no explanation, buttons enabled on an
   empty list, text boxes with no hint of what to type.
6. **A missing tile**: with no game running the dashboard's eighth tile vanished,
   leaving a hole in the grid.
7. **Inconsistency**: five different page widths, fourteen font sizes (seven of them under
   12 px), four label-column widths, unstyled system radio buttons and checkboxes, combo
   boxes whose arrow disappeared under the text.
8. **Graph scales** showed the padded raw extremes (3,005 / 1,108, 593.8 / 2.4) drawn over
   the line.

What worked and was kept: the series colours, the honest explanatory copy, keyboard focus
rings, the Windows 11 window chrome, and the fan-curve and V/F charts.

## Verification

- [tools/ui-check.ps1](../tools/ui-check.ps1) (UI Automation, demo mode): opening and closing a tile's
  expanded graph, the FPS tile leading to the FPS page, the "Unapplied changes" marker
  appearing on a slider move and clearing on Apply, the fan-mode switch revealing the duty
  slider, saving a profile and the list actions enabling on selection, unlabelled switches
  carrying accessible names — 18 checks, all passing.
- Screenshots of every page at both sizes, before and after.
- The axis rounding is unit-tested (`AxisScaleTests`, expected values worked by hand).
