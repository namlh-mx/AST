# WPF conventions (AST project)

Project-specific structure only. For generic WPF rules, follow the skills this file
points to — do NOT restate them here. The stack is **WPF-UI (Fluent) + Prism** (NOT
CommunityToolkit.Mvvm — do not introduce `[ObservableProperty]`/`RelayCommand`).

## Where things live
- Views: `AST/Views/<Domain>/<Name>View.xaml` (+ `.xaml.cs`). A View is a `UserControl`.
  `<Domain>` is one of three buckets — pick by what the screen's ViewModel actually depends
  on, not by where it happens to sit today:
  1. **A business module's domain** (e.g. `Iam/OrgUnit` for a screen serving
     `AST.Modules.IAM`) — matches the module's own name, PascalCase, no `AST.Modules.`
     prefix — for a screen whose VM depends on that module's repositories/services.
  2. **`Platform`** — for a screen whose VM depends only on platform/infrastructure
     services (config-security §⑤, connection/secrets provider, admin session, audit log —
     the platform layer's charter) and NOT on any `AST.Modules.*` business module.
     Example: `Platform/AdminAuthView`, `Platform/ConnectionDeclarationView`,
     `Platform/ConfigurationStationView` (the hub screen that only routes to the two
     preceding ones — still `Platform`, it is not generic shell chrome, it IS that domain's
     landing screen).
  3. **No domain folder** — genuinely generic shell-level chrome with no screen-specific
     backing service at all: `Dashboard`, `ComingSoon`, `Help`. Stays flat under `AST/Views/`.
- ViewModels: same domain subfolder under `AST.Shell/ViewModels/<Domain>/<Name>ViewModel.cs`.
  BCL-only presentation VMs the shell owns live here, never in a business module project
  (rule-module-boundary — Presentation is deliberately centralized, see below).
  **Exception:** a VM that needs `Prism.Wpf` directly (e.g. `IRegionManager` for its own
  navigation calls, which `AST.Shell` cannot reference — plain `net10.0`, no WPF) lives in
  the exe's own `AST/ViewModels/<Domain>/<Name>ViewModel.cs` instead — today's one instance
  is `ConfigurationStationViewModel`.
- **Decided 2026-07-25** (retrofitted onto every existing screen as of 2026-07-31 — the `Iam`
  and `Platform` buckets are both fully populated now; a NEW screen still follows this convention
  from the start): this subfolder convention exists because `AST/Views/` and
  `AST.Shell/ViewModels/` are NOT split into per-module assemblies (see the note in
  `rule-module-boundary` §1d) — without a domain subfolder, screens from every business area
  AND every platform-infrastructure area land in the same two flat folders with no
  visual/discoverability boundary as the app grows. `AdminAuthView`/`AdminAuthViewModel`,
  `ConnectionDeclarationView`/`ConnectionDeclarationViewModel`,
  `ConfigurationStationView`/`ConfigurationStationViewModel`, `BreakGlassAdminViewModel`,
  `ConfigAuditHistoryViewModel` moved into `Platform/` 2026-07-31.
- Converters: `AST.UI/Converters/`. Behaviors: `AST/Behaviors/`.
- Design-system brushes / typography: `AST.UI/Resources/DesignSystem/WpfUiOverrides.xaml` —
  reuse these keys via `{DynamicResource ...}`, do NOT hard-code colors. Common keys:
  `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, `ApplicationBackgroundBrush`,
  `SystemFillColorCriticalBrush` (error), `SystemFillColorSuccessBrush` (success).

## Design-system resource wiring (LOCKED) — do not restructure without an F5
The shared design system lives in `AST.UI` (`Resources/DesignSystem/*.xaml`), but the exe `AST/App.xaml`
merges it **FLAT and app-global** — never wrap it in a nested aggregate "hub" `ResourceDictionary`. Merge
order (LOCKED, WpfUiOverrides LAST so its retints win):
`ui:ThemesDictionary → ui:ControlsDictionary → Palette → Typography → Spacing → Controls → WpfUiOverrides`.
Two hard WPF-UI constraints are WHY (a nested hub silently breaks both, and only F5 shows it):
- **`ui:ThemesDictionary` must be a top-level member of `Application.Resources.MergedDictionaries`** — the
  runtime brand accent (`ApplicationAccentColorManager.Apply` in `App.xaml.cs`) finds it only there; nesting
  it makes the accent silently no-op (white primary buttons, blue DataGrid selection).
- A dictionary consumed **nested** cannot resolve a cross-file `{StaticResource}` / `BasedOn="{x:Type ui:Button}"`
  against a sibling outside its nesting; the duplicate `ui:ControlsDictionary` used to force it regressed the
  `FluentWindow` maximize (covered the taskbar).
**Module UIs do NOT re-merge these** — they render inside the shell window and inherit the app-global resources.
Full rationale: the design record (2026-07-18) +.

## Screen layout standard (no-scroll, star-sizing) — approved 2026-07-13
A screen shows all its parts without a window scrollbar. Default shape:
- Root is a `Grid` that FILLS the content area — NOT an outer `ScrollViewer`
  (`*` sizing is a no-op inside a vertical ScrollViewer, so the outer ScrollViewer
  must be removed for the Grid to fill a content area with a determined height).
- `Auto` rows for fixed chrome (title, status band, form fields, button rows).
- `*` rows for the data regions / tables, so they take the remaining height and
  grow/shrink with the window. Multiple tables split the `*` space (e.g. `1*:1*`).
- Each table (`DataGrid`/list) keeps default virtualization and carries a `MinHeight`
  (e.g. 120) so it scrolls INTERNALLY when its rows overflow instead of growing the page.
- The window (`MainWindow`) carries `MinWidth`/`MinHeight` so nothing is squeezed away
  on a small display.
Reference implementation: `AST/Views/Platform/AdminAuthView.xaml`.

## Screen anatomy standard (screen-anatomy v2, `AST.UI`)
Shell chrome (title bar / sidebar / startup banner / Prism `ContentRegion`) is drawn by the shell, never a
screen. A screen's own body assembles from a small, fixed set of shared blocks — do not hand-build a
substitute per screen:
1. **Screen header** (icon + title + optional Back affordance) — on every screen.
2. **Status band** (bound to an `IStatusBanner` VM) — on every screen; reserved height so show/hide never
   reflows.
3. **Content** — a stock `TabControl` (WPF-UI-themed) or a grid of `AstCard`s, chosen per screen; soft cap
   **≤ 5 tabs / ≤ 4 cards** visible (a design guideline, not code-enforced).
4. Inside each tab/card: **section header** (`AstSectionHeader` — icon + text, `AST.UI/Controls/`), **field
   row** (`AstField` — label above a `Content`-slot input with optional helper/error, `AST.UI/Controls/`),
   **data table** (`AstDataGrid`), **action bar** (button group — see "Action button groups" below).

**`AstScreen`** (`AST.UI/Controls/AstScreen.cs`, keyed style `AstScreen` in `Controls.xaml`) packages blocks
1–2 as the standard top frame plus a body `Content` slot — see the header/status-band alignment section
right below. **`Spacing.Between`** (`AST.UI/Controls/Spacing.cs`) is the token-driven uniform gap for a
`StackPanel` whose children share one spacing (e.g. a vertical field stack) — set
`controls:Spacing.Between="{StaticResource AstFieldGap}"`; do NOT reach for it where gaps are intentionally
uneven (hand-set each child's `Margin` there instead, as the Connection card stacks do today).

Retrofit onto these components is incremental, not a one-shot migration — new/rebuilt screens use them; each
existing screen's retrofit is its own task with its own F5 (tracked in).

### Screen header + status band alignment — owned by `AstScreen`
Every screen's header and status band occupy the top band and line up with the shell sidebar's user-area, so
the header sits on the sidebar-toggle centre line and the first data region begins on the user-area↔menu
divider line. This frame is now a single owned component — a screen wraps its body in **`controls:AstScreen`**
instead of hand-building the recipe:
```xml
<controls:AstScreen Title="…" Icon="…"
                     BackCommand="{Binding BackCommand, RelativeSource={RelativeSource AncestorType=views:MyView}}"
                     Message="{Binding StatusMessage}" Severity="{Binding Severity}"
                     Style="{StaticResource AstScreen}">
  <!-- screen body: the content slot -->
</controls:AstScreen>
```
`AstScreen`'s template owns the values that used to be hand-tuned per screen: root `Margin="24,0,24,24"`
(0 top so the header's vertical centre lands on the sidebar toggle centre), the header row, the status-band
row, then the body row at `Margin="0,6,0,0"` (content top sits just below the user-area↔menu divider line, not
overlapping it). It composes `AstScreenHeader` (see below) and `AstStatusBand` (see "Status band" below).
`BackCommand` is a dumb passthrough — it carries NO navigation authority; the view still owns Prism
`RequestNavigate` (§1c intact).
Reference: `ConnectionDeclarationView` (adopted). `AdminAuthView` still hand-builds an equivalent frame
(`AstScreenHeader` + `AstStatusBand` composed directly, not wrapped in `AstScreen`) — retrofit deferred, same
visual result either way; tracked in.

### Shell title-bar band + "AST" as the Home affordance (`MainWindow` only, chrome §1c)
`MainWindow` Row0 is an explicit ~64px band; `ui:TitleBar VerticalAlignment="Top"` keeps the OS caption
buttons at the natural ~32px top strip. The **"AST" title text** (in `TitleBar.Header`, left) IS the Home
affordance — there is **no separate Home button**: clicking it navigates the content region to Dashboard,
and it turns **bold + brand red** (`#89002a`) while Dashboard is the shown screen. This is chrome only
(`rule-module-boundary §1c`): the click drives content navigation through the shell's own `NavigateCommand`
(the same one the sidebar leaves use) and the shell owns the active-highlight state (exactly one of
{AST, a sidebar path} reads as active) — it is never shell navigation authority. `SetAstActive` in
code-behind toggles the bold+red. `FontSize` / `Margin` / `VerticalAlignment` on the AST `TextBlock` are
F5-tunable. Reference: `AST/MainWindow.xaml`.

### Multi-workstation layout stability — size follows the WINDOW, not the CONTENT (approved 2026-07-14)
A region's size/position is a function of the WINDOW, never of its content. A content change
(load / add / remove / edit) must never move or resize any other region. Two axes:
- Content change → must NOT move anything; overflow is absorbed INSIDE the region (internal scroll
  or pre-reserved space).
- Window/machine change → regions may scale with the window (multi-workstation goal).

Design floor = ~1280×720 effective WPF units (covers 1366×768 @100% and 1920×1080 @150%).
`MainWindow` `MinWidth=1280`/`MinHeight=720` and always launches maximized (`WindowState=Maximized`)
so it fills the actual screen on any machine. WPF `PerMonitorV2` (`AST/app.manifest`) handles DPI
already — the axis that varies between machines is effective width in WPF units, not DPI.

Five techniques (see `AdminAuthView` for the reference implementation):
1. Outer frame = Grid with determined rows (`Auto` chrome, `*` data regions).
2. Status band = the shared `controls:AstStatusBand` in an `Auto` cell; it reserves `MinHeight` so
   show/hide never reflows.
3. A region with an action bar = internal 3-row Grid: header `Auto` + data `*` (scrolls internally)
   + action bar `Auto` pinned.
4. Tables: fixed column widths + always-on vertical scrollbar + horizontal scroll `Auto`; the table
   sits in a `*` column with `MaxWidth = Σ(columns) + 16`, and overrides the `AstDataGrid`
   `HorizontalAlignment` to `Stretch` so it fills/shrinks to the column instead of clipping.
   Do **not** pin a hard `Width` on a grid placed next to another region — a hard `Width` squeezes
   the neighbour on a narrow window (that is exactly technique 5). Size each column to its **max
   possible content** (e.g. a Windows-username column = 200 for the 25-char max; a `yyyy-MM-dd HH:mm`
   date column = 140); the `AstDataGrid` style already forces `ScrollViewer.VerticalScrollBarVisibility="Visible"`
   (that fixed dorsal scrollbar is the `+16` in the `MaxWidth` budget). Keep `MaxWidth = Σ(columns) + 16`
   when tuning. Reference impl: `AdminAuthView` (history + rescuer grids).
5. Never place an `Auto` column hard-holding a fixed `Width` next to another region (it squeezes the
   neighbour) — use `*`+`MaxWidth` + internal scroll.
6. A single-value field that can hold a long string (e.g. a file path) is a fixed-height, no-wrap
   element so it never grows and pushes its neighbours, yet stays selectable and reads as a label:
   a read-only `TextBox` styled with **no `BasedOn`** (so it drops WPF-UI's Fluent input chrome and
   falls back to the framework-default template) + borderless/transparent + `TextWrapping="NoWrap"`
   + `HorizontalScrollBarVisibility="Hidden"`. Overflow is hidden; the user selects/drags to reveal
   it (tooltips are removed app-wide). NOT a wrapping `TextBlock` (not selectable) and NOT a plain
   `ui:TextBox` (looks like an input). If such a field sits inside a per-region ScrollViewer, give
   it a right margin (~16) so the overlay scrollbar does not sit over its right edge. This is the shared
   **`AstSelectableValueText`** style in `Controls.xaml` (deliberate no `BasedOn`); set `Foreground` on the
   element itself — the style leaves value colour to the call site.
7. Per-region overflow fallback (no whole-page scroll): a **form** region wraps its content in an
   internal `ScrollViewer` (`VerticalScrollBarVisibility="Auto"`, horizontal `Disabled`) so it
   scrolls INSIDE the card only when the card is shorter than its content (small/scaled screens);
   the page itself never scrolls (`*` is a no-op inside a vertical ScrollViewer, so the ROOT keeps
   its no-scroll star Grid — only the region's inner content, which has a natural height, is
   wrapped). A data table already scrolls internally via its `MinHeight` + virtualization.

## Styling a WPF-UI control — always `BasedOn` the default style
WPF-UI (`Wpf.Ui`) styles its controls through the IMPLICIT styles merged by
`ui:ControlsDictionary` (keyed `{x:Type ui:Button}` etc.), which carry the ControlTemplate,
the Fluent icon font, and the foreground brushes. A keyed style with
`TargetType="ui:Button"` but **no** `BasedOn` REPLACES that implicit style, so the control
loses its template — e.g. a `ui:Button`'s `Icon` then draws as a notdef box. Any keyed style
for a WPF-UI control MUST derive from the default:
```xml
<Style x:Key="AstInlineIconButton"
       TargetType="ui:Button"
       BasedOn="{StaticResource {x:Type ui:Button}}">
  <!-- only override the chrome (Appearance, Border, hover/pressed brushes, the mouse-pointer property) -->
</Style>
```

## Wiring (Prism, View-First)
- Register in `AST/App.xaml.cs` → `RegisterTypes`:
  `containerRegistry.RegisterForNavigation<Views.<Name>View, AST.ViewModels.<Name>ViewModel>();`
  This pairs the View with its ViewModel and sets the DataContext on navigation — the
  View needs NO explicit `DataContext` and NO `ViewModelLocator` attribute.
  If the VM has constructor DI that DryIoc cannot auto-wire (e.g. a primitive arg), add an
  explicit factory registration — `containerRegistry.Register(typeof(...), () => new ...)`, or
  `RegisterSingleton(typeof(...), () => new ...)` when the VM must be a singleton (e.g. it
  subscribes to an app-lifetime singleton service's event, so subscriber lifetime must equal
  publisher lifetime — see the existing `AdminAuthViewModel` registration for the singleton-factory pattern).
- Navigate by view name: `regionManager.RequestNavigate("<region>", "<Name>View");`
  Region names come from `MainWindowViewModel` (e.g. the content region constant).
- WPF-UI xmlns on a View root: `xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`.

## ViewModel pattern (Prism)
- Base class: `Prism.Mvvm.BindableBase`. Observable property = backing field +
  `SetProperty(ref _field, value)`. Command = `Prism.Commands.DelegateCommand`
  (lazy-initialized, see the skeleton). Keep VMs BCL-only — no `System.Windows` types.
- Icons: Fluent System Icons (bundled) via WPF-UI `SymbolIcon` / `SymbolRegular`.

## Generic rules (pointers — read the skill, do NOT copy here)
- View↔VM wiring (Prism): skill `wpf-rule-view-viewmodel-wiring-prism`.
- MVVM layer separation (no `System.Windows` in VMs): skill `wpf-rule-mvvm-constraints`.
- Converters: skill `wpf-rule-converter-patterns`.
- ResourceDictionary / style order: skill `wpf-rule-resourcedictionary-patterns`.
- Virtualization (large lists): skill `wpf-rule-virtualization-patterns`.

## AutomationId for FlaUI-readiness (new screens only — decided 2026-07-29)

Every **newly-built** View/CustomControl sets `AutomationProperties.AutomationId` on its
interactable elements, so a future FlaUI automated-UI-test gate can find them. This is
a forward convention only — existing, already-stable screens are **not** retrofitted.

- For an `ItemsControl`-based element (`ListView`, `ListBox`, `TreeView`, …), set the
  `AutomationId` on the **`ItemContainerStyle`**, never on `DataTemplate` content — FlaUI's UIA
  tree exposes the item container, not the template inside it.
- A new control that renders via `Shape` (`Path`, `Ellipse`, a hand-drawn connection/line) has no
  `AutomationPeer` by default and is invisible to FlaUI; if it will need UIA verification later,
  override `OnCreateAutomationPeer` when authoring it.
- No FlaUI test project exists yet — this is about placing IDs now so one can be stood up later
  once enough new screens carry them; it does not gate anything today.

## Reference skeleton
`templates/skeletons/` holds one minimal View+ViewModel pair showing the structure above.
Copy the shape, not the content — it is not a working feature.

## Form & control chrome standards (UI design — locked growing list)

Single home for reusable chrome. A UI standard has **one home** — a keyed `Ast*` style in
`AST.UI/Resources/DesignSystem/Controls.xaml`, or a shared control in `AST.UI/Controls/` (e.g. `AstPasswordBox`,
`AstStatusBand`) — and applies to **every** screen; never a per-view copy. The standard set grows as
screens are built. Tokens live in `Palette.xaml` / `Typography.xaml` / `Spacing.xaml` — do not hard-code
hex in views.

### Text input placeholders
- Do **not** put example/suggestion hints in `PlaceholderText` (e.g. `db.local`, `ast_db`, `●●●●●●●●`) — an
  input carries a clear field label above it, not an in-box suggestion. The one allowed use is an
  empty-state label on a **read-only** value field (e.g. the key path shows `"Chưa chọn khóa"` until a file
  is picked) — that is a state, not a typing hint.

### Password fields
- Control: **`controls:AstPasswordBox`** (`AST.UI/Controls/AstPasswordBox.cs`) only — never stock `PasswordBox`,
  and no longer raw `ui:PasswordBox`. Apply the keyed style **`AstPasswordBox`** to it as before.
  **Why the subclass is mandatory (do not "simplify" it away):** WPF-UI 4.3's `PasswordBox` syncs
  *Text -> Password* only while the password is revealed, so a programmatic clear (a ViewModel wiping the
  field on reuse/clear/navigate-away) is reverted from the visible text AND the stale secret is pushed back
  into the ViewModel through the TwoWay binding. The subclass restores the *Password -> Text* direction and
  disables the inherited `TextBox` undo stack (Ctrl+Z would otherwise resurrect a wiped plaintext).
- **Wipe the secret when it stops being needed.** Prism reuses a view + its ViewModel across navigation
  (`IsNavigationTarget => true`), so a typed password outlives the screen unless the view clears it in
  `OnNavigatedFrom` (see `ConnectionDeclarationView`). Clearing only the ViewModel property is enough —
  the control follows through the binding, given `AstPasswordBox`.
- Mask character: `PasswordChar` = **●** (U+25CF); color = **default** control foreground (do not paint
  mask with brand/error red).
- **Red (`AstErrorBrush` / primary-danger cues) is reserved for problem states** (validation failure,
  connect error, destructive caution) — never for normal password masking.
- Reveal: `RevealButtonEnabled=True` via the style (project default). Do not invent custom eye overlays.
- Do not set ad-hoc `PasswordChar`/`Foreground` on individual screens unless intentionally overriding.

### Declaration / data-entry form screens
- A screen with a form **derives from `AST.Views.DeclarationFormView`** (XAML root = `<views:DeclarationFormView …>`,
  not `<UserControl>`) and its ViewModel **implements `AST.Shell.Presentation.IDeclarationForm`**. That is all —
  the base then supplies, identically on every such screen:
  - **leave-confirmation** when `HasUnsavedInput`, using the standard warning dialog;
  - **wiping the form** in `OnNavigatedFrom`.
- **Do not hand-roll either behaviour per screen.** Prism reuses a view *and* its ViewModel
  (`IsNavigationTarget => true`), so anything typed survives navigation unless the screen clears it — and a
  screen that forgets leaks a secret. That is a real defect this project has already shipped once.
- `HasUnsavedInput` means **touched AND non-empty** — never just non-empty. A just-opened screen, a cleared
  form and a saved form must all leave silently, or the confirmation trains operators to dismiss it.
- `Clear()` resets **every** field the operator can type into, including file paths and pending list edits —
  not only the obvious secret.
- Screens open **blank**; do not prefill a declaration form from the saved configuration. What is in force
  belongs in a history/status surface the operator reads, and is brought back deliberately (e.g. *Dùng lại*).

### Dialogs (confirm / notice)
- **Build every dialog through `AST.Controls.AstDialog`** (`ConfirmAsync` / `NoticeAsync`) — never construct a
  `ContentDialog` inline in a screen, and never use the classic Win32 `MessageBox`. `AstDialog` is the single
  home of the shape below; changing the shape means changing it there, once.
- Shape: title row = **kind icon + `AST` in bold at BODY font size** (the stock template forces FontSize 20 on
  the title — pin it back); body = **one short sentence**; **fixed standard width** so every dialog is the same
  size (WPF-UI otherwise sizes each dialog to its content).
- Buttons carry a **`SymbolRegular` icon + a short label**, are **equal width and content-sized**, right-aligned
  — the action-group convention below. The stock footer stretches each button across an equal star column, so
  `AstDialog` switches it off (`IsFooterVisible=False`) and lays the buttons out itself; that is the library's
  own extension point and is cheaper + safer than copying the whole ContentDialog ControlTemplate.
- The cancelling choice is the `DefaultButton`, so Enter/Esc never commits the risky action.
- Kind = the existing `StatusSeverity` (no second enum). **Dialog colours are per kind** (warning = amber
  `AstWarningBrush`, error = red, info = brand, success = green) — deliberately NOT the in-screen status band's
  convention (success = green, anything else = red), which stays as it is. A band annotates a screen; a dialog
  shows one message alone, whose kind must read at a glance.
- Host: `ui:ContentDialogHost x:Name="RootDialogHost"` in `MainWindow.xaml`, wired once via
  `IContentDialogService.SetDialogHost`. It is **visual chrome only** (`rule-module-boundary` §1c) — no
  `RegionName`, never a content host.
- Dialogs are shown from **view code-behind**, not from a ViewModel (keeps the VM BCL-only, no dialog port).
  A dialog call at the Prism navigation edge is `async void`: wrap it so the continuation callback is always
  answered and an exception cannot kill the process (see `DeclarationFormView.ConfirmNavigationRequest`).

### Action button groups (inside a card)
- Same **content-based** width for every button in the group = width of the **longest** label
  (`Grid.IsSharedSizeScope` + `SharedSizeGroup`), **not** star-stretch across the card.
- Group `HorizontalAlignment="Center"` in the card.
- Icons: WPF-UI `SymbolRegular` via `Button.Icon` / `SymbolIcon`; styles that target `ui:Button`
  **must** `BasedOn="{StaticResource {x:Type ui:Button}}"` (otherwise icons become notdef boxes).

### Status band (per-screen message line) — bound to `IStatusBanner`
- The status band is the shared UserControl **`controls:AstStatusBand`** (`AST.UI/Controls/AstStatusBand.xaml`)
  — the single home. Bind `Message`/`Severity` to a VM that implements **`AST.Core.Presentation.IStatusBanner`**
  (`StatusMessage`/`Severity`) — the one status-band VM contract; the control reserves its height so show/hide
  never reflows (technique 2).
- A VM with a single status source just exposes `StatusMessage`/`Severity` directly (e.g.
  `ConnectionDeclarationViewModel`). A VM that aggregates several sources onto one band (e.g.
  `AdminAuthViewModel`, funnelling its own auth-result status plus two child VMs' status) reconciles them
  onto `StatusMessage`/`Severity` itself, last-writer-wins — the child VMs are funnel SOURCES, not
  `IStatusBanner` implementers themselves. See `AdminAuthViewModel`'s `OnBreakGlassStatusChanged`/
  `OnHistoryStatusChanged` for the pattern.
- Do **not** re-inline the icon+text band per screen — it drifted before this was consolidated.

### Selectable read-only values (paths, etc.)
- Apply the shared **`AstSelectableValueText`** style (`AST.UI/Resources/DesignSystem/Controls.xaml`) — the
  label-look selectable `TextBox` recipe (deliberate no `BasedOn`, drops Fluent input chrome). Set
  `Foreground` on the element itself; the style leaves value colour to the call site. See "Multi-workstation layout stability" in this file.
- A **file-path value** is shown as `[folder icon] path`: a plain **decorative** `ui:SymbolIcon`
  (`FolderOpen24`, no click/command) in an `Auto` column, then the `AstSelectableValueText` path in the
  `*` column with `Margin="8,0,0,0"`. The field label lines up with the icon's left edge. Same shape on
  every screen (connection save-path, AdminAuth history path).

### Screen header = Back affordance (drill-in screens) — `AstScreenHeader`
- A screen reached by navigating **from another screen** (a drill-in — NOT one reached by a sidebar leaf)
  sets its `AstScreen`/`AstScreenHeader` **`BackCommand`** DP; the whole header then becomes the Back
  affordance (hand cursor; a left-click executes the command) — there is **no arrow button**, and no
  code-behind click handler to hand-wire. The view exposes a `DelegateCommand` set **before**
  `InitializeComponent` (so the header's binding reads it on first layout) that `RequestNavigate`s to the
  parent view; the header never navigates itself (`AST.UI/Controls/AstScreenHeader.xaml.cs`) — the consuming
  view keeps navigation authority (§1c). The Prism `ConfirmNavigationRequest` leave-confirm still fires (it
  is triggered by navigating away, not by the header click). **Unlike** the shell "AST" home text, the
  screen header is **NOT** highlighted — it just carries the hand cursor. Sidebar-leaf screens have no
  parent, so their header has no `BackCommand` and stays a plain (non-clickable) label. Reference:
  `AdminAuthView` / `ConnectionDeclarationView`.

### Header / status / content
- Screen anatomy standard + header/status-band alignment + no-scroll star layout: sections above in this
  file. Reference: `MainWindow` + `AdminAuthView` + `ConnectionDeclarationView` on **main**.
