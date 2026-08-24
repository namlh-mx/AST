using System.Collections.ObjectModel;

namespace AST.Core.Presentation;

// One node of Screen A's org-unit tree. Lives in AST.Core (not AST.Shell) because BOTH the View layer (the
// `AST` exe project's OrgUnitDeclarationView/OrgUnitTreeView) and the VM layer (AST.Shell's
// OrgUnitDeclarationViewModel) must reference it, and AST.Shell cannot depend on the exe project `AST` --
// same shape/reachability reason as OrgUnitPickerItem's home (relocated 2026-07-31, Phase 4d Task 3a: was a
// View-local placeholder type in OrgUnitDeclarationView.xaml.cs before the VM gained a real tree-load path).
[SharedComponent]
public sealed class OrgUnitTreeNode
{
    public OrgUnitTreeNode(long id, string label)
    {
        Id = id;
        Label = label;
    }

    public long Id { get; }
    public string Label { get; }
    public ObservableCollection<OrgUnitTreeNode> Children { get; } = [];

    // View state, not data. Set to `true` unconditionally by LoadTreeCoreAsync at construction time
    // (expand-all, not carried across reloads — see docs/shared-components.md). The style setter is
    // OneWay (VM/node -> container only); a user's manual collapse lives solely as a local value on
    // the generated TreeViewItem and is intentionally discarded on the next reload, since nothing
    // reads it back. Do NOT enable virtualization/recycling on the hosting TreeView without
    // revisiting this -- container recycling would carry a stale local collapse state onto a
    // DIFFERENT node's container, silently breaking expand-all.
    public bool IsExpanded { get; set; }
}
