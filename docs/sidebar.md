# Shell sidebar (settled model)

The shell sidebar is the app's main navigation menu. This documents the **as-built** model
(structure settled 2026-07-11; behaviour/highlight refined by F5 on 2026-07-12). Earlier requirement drafts
proposed 3 levels, auto-hover/pin behaviour, and custom chevron/dot indicators — those are **superseded** by
the decisions below.

## 1. Structure — 2 levels, data-driven

- **2 levels only**: L1 = business group, L2 = leaf (a screen). A 3rd grouping level, when needed, is
  handled later by a shared screen — not by a deeper menu.
- The tree is **built from the existing function registry**, not a hand-maintained menu list:
  `ISidebarMenuBuilder` (in `AST.Shell/Navigation/`) composes, per L1 group:
  - real functions from `IFunctionRegistry` (grouped by `MenuGroupCode`), plus
  - transitional **placeholder** leaves (`Menu N.M`) from `PlaceholderMenuSeed`, ordered together.
  - Group display metadata (name/icon/order) comes from `MenuGroupCatalog` (`MenuGroup` records).
- **Placeholders are UI-only** and are NEVER registered into `IFunctionRegistry` (that would sync them
  to the DB function catalog and subject them to authorization). When a real module registers its
  `FunctionDescriptor`, the corresponding placeholder is dropped.
- L1 groups (Vietnamese labels): Kế toán giao dịch, Kế toán nội bộ, Tiền tệ - kho quỹ,
  Báo cáo quản trị, Kiểm tra - giám sát.
- **Built once, lazily, at first read** (`MainWindowViewModel.MainMenu`/`FooterMenu`) — not eagerly in the
  ViewModel's constructor. The ctor runs during Prism `CreateShell()`, before `InitializeModules()` loads
  business modules; building there would freeze whatever functions were registered at that early moment and
  permanently hide anything a module registers afterward (the 2026-08-07 bug this note documents). The first
  read happens in `MainWindow.xaml.cs`'s `OnLoaded`, which fires after modules have loaded and the app's own
  startup registration has run. Never rebuilt after that first read — a later `IFunctionRegistry.Register`
  call is a logged no-op for the sidebar this session (see `IFunctionRegistry`'s `docs/shared-components.md`
  entry for the registry-side half of this invariant).
- **Sidebar filtering by permission was considered and explicitly rejected** — every read/write on every
  declared screen already fails closed via `IAuthorizationService` at the data layer, so hiding a menu item
  buys no additional security; worse, it would make the "Cấu hình" footer entry (the rescuer's only path to
  fix a broken DB connection) depend on the DB being reachable to even render, which is backwards. Do not
  reopen this.
- **Footer** (pinned to the pane bottom): two leaves — **Trợ giúp** (→ `HelpView`) above **Cấu hình**
  (→ `ConfigurationStationView`). Cấu hình is no longer an expandable group: it opens the Configuration
  Station screen, whose *Cấu hình hệ thống* tab hosts a set of Execute-gated cards (§4 "Related screens"
  enumerates them and each one's specific gate) rather than a fixed count named here, so a future card
  doesn't require updating two spots.

## 2. Behaviour — built-in WPF-UI only

- Rendered by the WPF-UI `NavigationView` as **chrome only**. Content navigation is 100% Prism
  `IRegionManager.RequestNavigate` on `ContentRegion` (rule-module-boundary §1c); no
  `TargetPageType`/`INavigationService`.
- **Default collapsed** (`IsPaneOpen=false`, `CompactPaneLength=48`, `OpenPaneLength=260`). The built-in
  toggle is hidden (`IsPaneToggleVisible=False`); a **custom borderless pane-toggle** in the pane header
  drives `IsPaneOpen` from code-behind. Its glyph is left-aligned onto the menu-icon line (~19px, mirroring
  the nav items' 40px icon column) so the collapsed toggle lines up with the icons below it.
- **No native flyout (verified against WPF-UI 4.3 source)** — `NavigationView` has no popup/flyout in any
  mode. Collapsed-rail group access is **Approach A**: clicking an L1 group icon **auto-opens the pane and
  expands that group**; the pane then **auto-collapses** after the user opens a screen (leaf click) or clicks
  outside the sidebar (a `PreviewMouseDown` on the window). A **manual** toggle-open is sticky (does not
  auto-collapse). (Locked 2026-07-11; the old "built-in flyout" note was a wrong assumption, corrected here.)
- **Accordion (single-expand)**: expanding one L1 group collapses the others; collapsing the pane collapses
  all groups. Driven from `MainWindow` code-behind by observing the built-in `NavigationViewItem.IsExpanded`
  / `NavigationView.IsPaneOpen` DPs (via `DependencyPropertyDescriptor`) — no template changes.
- **Built-in visual cues only** — the parent chevron and child indent are WPF-UI's own; no custom
  chevron/dot templates, no auto-hover/pin. ("Built-in only, no hand-rolled template", locked 2026-07-10.)
- **Interactive-state colours** map to the brand palette (`Palette.xaml`) by overriding WPF-UI's `NavigationViewItem*`
  theme brushes in `Resources/DesignSystem/WpfUiOverrides.xaml`: hover bg `#f6e9eb`, pressed bg `#f0d9dc`,
  selected bg `#ffe9ea` (the active-leaf pink — painted by the stock template's `IsActive` trigger, see §4),
  resting foreground `#1a1c1c`, hover/pressed foreground `#89002a`. **Leaf (child-item) hover foreground** is
  driven from code-behind (`MouseEnter`/`MouseLeave` brand-red the label + icon): WPF-UI 4.3's child-item
  template only changes Background on hover, unlike the L1 template which also reddens the text.

## 3. Rendering seam (View layer)

- The builder returns a **BCL `SidebarNode` tree** (headless-testable); the exe-side
  `NavigationMenuBuilder` maps each node to a WPF-UI `NavigationViewItem` in `MainWindow` code-behind, so
  `MainWindowViewModel` stays free of `System.Windows` types.
- Each leaf's `Command` = `NavigateCommand`, `CommandParameter` = the `SidebarNode` (carries the Prism
  target + the title shown by `ComingSoonView`). `IconKey` (a Fluent `SymbolRegular` name string) maps to
  a `SymbolIcon`; unknown keys degrade to no icon.

## 4. Related screens

- **Landing screen**: `DashboardView` (placeholder cards "Chức năng chờ triển khai").
- **Leaf target (placeholder)**: `ComingSoonView`, showing the clicked leaf's title.
- **Configuration Station**: `ConfigurationStationView` (footer *Cấu hình* leaf) — a 3-tab shell screen (WPF-UI
  themed `TabControl`: *Cấu hình hệ thống* / *Tham số nghiệp vụ* / *Quản lý phiên bản*, latter two placeholder).
  Its *Cấu hình hệ thống* Execute buttons Prism-navigate the ContentRegion to `AdminAuthView` /
  `ConnectionDeclarationView`; the DB button is gated by admin authentication. A third card now routes to
  `OrgUnitDeclarationView`, and its gate is `role_permission` on `Iam.OrgUnit.Declare` (not `IAdminSession`).
- **User area**: pane-header placeholder — a `PersonCircle24` icon + "Người dùng" label, laid out on the
  **same 40px-icon-column geometry as the menu items** (icon centred at ~19px, label starting at 40px) so it
  lines up with the list; the label is shown only while the pane is open (a real account panel is plugged in
  later).
- **Home affordance**: Home is **not** a sidebar item and there is **no separate Home button**. The **"AST"
  title text** in the title-bar band IS the Home affordance: clicking it navigates to `DashboardView` via the
  same `NavigateCommand`, and it turns **bold + brand red** (`#89002a`) while Dashboard is the shown screen
  (startup + on click), reverting when a sidebar item takes over.
- **Active highlight (one target tracks the SHOWN screen)**: the active leaf renders its icon **Filled** +
  brand foreground `#89002a` + a pink background — the pink is painted by the stock template's `IsActive`
  trigger (`NavigationViewItem.IsActive=true`, **not** a local `Background`, so the hover wash still wins);
  its **parent L1 group icon co-highlights** (Filled + brand). A **group's own `IsActive` is never set** —
  a group is marked by its icon alone, so WPF-UI's chevron-collapse write is erased on sight. Fill follows
  the shown screen only: **browsing a group header does NOT fill it** (browse ≠ active).
  - **The highlight is driven by the navigation RESULT, never by the click.** The ViewModel subscribes to the
    content region's `Navigated` + `NavigationFailed` and resolves which leaf corresponds to the shown view;
    the View then repaints **every** item absolutely. There is deliberately **no revert path**: a cancelled or
    failed navigation re-derives the shown view from the region's `ActiveViews`, so re-applying the truth IS
    the revert. This is what keeps the chrome honest when a leave-confirm is answered "Ở lại", and what makes
    navigations that never touch the sidebar (startup, a Trạm cấu hình card) update it correctly.
  - **Resolution never guesses.** Every placeholder leaf targets the same view, so a view name alone does not
    identify a leaf; the leaf title (a navigation parameter) breaks the tie. If it still cannot decide, **no
    leaf is lit** — a blank sidebar is honest, a wrong leaf is a lie.
  - **A leaf may own views that are not leaves.** Screens reachable only through Trạm cấu hình stay resolved
    to the `Cấu hình` hub, which therefore stays lit while one of them is shown. Ownership is a fallback and
    can never override a real leaf.
  - Painting is `MainWindow` code-behind (WPF-UI has no reliable selected-foreground in this chrome-only
    setup); which leaf to paint is decided in `MainWindowViewModel`, which holds no WPF types.
- **Connection status**: a single dot at the bottom-left of the status bar (no clock); per-screen status
  lives on each screen, not a global banner.
