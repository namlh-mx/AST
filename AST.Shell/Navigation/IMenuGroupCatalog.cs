using AST.Core.Iam;

namespace AST.Shell.Navigation;

// Supplies the L1 group display metadata (name/icon/order) the Shell renders. Implemented by MenuGroupCatalog.
public interface IMenuGroupCatalog
{
    IReadOnlyList<MenuGroup> Groups { get; }
}
