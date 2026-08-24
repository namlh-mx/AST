namespace AST.Core.Iam;

// [C3] Break-glass "plug point" (first bootstrap admin / rescue when the DB permission grid is empty) — §⑤.
// Separated as a seam so C3 can build the DB-based resolution core FIRST without waiting for the §⑤ config
// security design (2 files A/B + digital signature). The §⑤ implementation (RealBreakGlassPolicy) reads File B
// + verifies the signature; it is registered ONLY at the composition root (Shell). There is no default
// registration — resolving without a registered policy fails clearly (DI error) rather than silently allowing
// or denying, which is the intended fail-safe.
[SharedComponent]
public interface IBreakGlassPolicy
{
    // true -> the user is granted full (Global) access, BYPASSING the DB permission grid. Verifying the
    // signature/list is the job of the §⑤ implementation; C3 only consumes the bool result.
    bool IsBreakGlassAdmin(string username);
}
