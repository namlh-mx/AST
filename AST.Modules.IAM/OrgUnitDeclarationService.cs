using System.Text.Json;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Repositories;
using ErrorOr;

namespace AST.Modules.IAM;

// Closes the gap described in AST.Core/Iam/IOrgUnitDeclarationService.cs's own doc comment: the org-unit
// declaration screen used to call IOrgUnitRepository.CancelPlanAsync/CloseVersionAsync DIRECTLY and picked
// the close-vs-cancel branch itself from a presentation status, so the date floor/date-equals-version-end/
// scope guards existed only in the VM and nothing recorded WHO retired the org unit. This service mirrors
// RoleDeclarationService.CloseRoleDeclarationAsync (AST.Modules.IAM/RoleDeclarationService.cs) step for
// step, minus the admin-flag machinery -- org units carry no such concept.
//
// Depends on the CONCRETE OrgUnitRepository class for its composite-write overloads (CancelPlanAsync /
// (new) CloseVersionAsync taking ICompositeWriteContext), which are deliberately NOT on the public
// IOrgUnitRepository interface (rule-module-boundary §3, same posture as the role side).
//
// Flow:
//   [1] P7 authorization (IAuthorizationService.AuthorizeAsync) -- BEFORE any lock/transaction opens.
//   [2] Scope handling: OrgUnit is a scoped entity (unlike Role, which is Global-only), so instead of
//       rejecting anything but Global, this guards ScopeLevel.Self explicitly (IsWithinScopeAsync/
//       GetHistoryInScopeAsync both THROW InvalidOperationException for Self -- org_unit_version has no
//       owner column) and then checks the target unit actually falls within the resolved scope via
//       IOrgUnitRepository.IsWithinScopeAsync.
//   [3] Resolve the target version SERVER-SIDE from GetHistoryInScopeAsync, restricted to IsActive --
//       stops the engine's own VersionedRepository.VersionNotFound (which names the physical table) from
//       reaching a screen, and rejects a re-submission of an already-closed/cancelled version id.
//   [4] VersionCloseRules.Validate derives the branch and validates the close/cancel date -- the single
//       shared home of these guards (docs/shared-components.md §⑥); never re-implemented or reordered here.
//   [5] ONE CompositeWrite: the version write (Cancel-plan or Close, per the derived branch) + exactly ONE
//       audit_log row on the SAME transaction -- an audit-write failure fails the whole composite.
//
// OrgUnitRepository declares no ExclusivelyOwnedDependents edge (verified: unlike RoleRepository, it does
// not override that base property), so unlike RoleDeclarationService.CloseRoleDeclarationAsync this service
// does NOT pre-probe and Enlist dependent identities up front -- there is nothing to auto-cut. A child org
// unit or a user that would lose coverage is instead BLOCKed by the engine's reverse-FK guard
// (TemporalFk.DependentsUncovered), which the composite already enforces without any extra Enlist here.
internal sealed class OrgUnitDeclarationService(
    OrgUnitRepository orgUnitRepository,
    IDbConnectionFactory connections,
    IAuditLogWriter auditLog,
    IAuthorizationService authorization,
    ICurrentWindowsUser currentUser,
    IBusinessDateProvider dates,
    IBreakGlassPolicy breakGlass,
    // The SAME singleton the write path runs (IamModule registers IPeriodEditor once). PreviewEditAsync
    // must answer with the algebra that will actually execute, so it takes the instance rather than
    // constructing a PeriodEditor of its own -- a second instance is a second configuration waiting to
    // diverge.
    IPeriodEditor periodEditor) : IOrgUnitDeclarationService
{
    // Same per-consumer-const convention as RoleDeclarationService.FunctionKey ("Iam.Role.Declare") --
    // no shared constant is extracted here (that is separate tracked debt, out of scope for this task).
    // This literal also lives as OrgUnitDeclarationViewModel's own private const FunctionKey. As of
    // 2026-08-21 (backlog 0.7) the VM no longer AUTHORIZES anything with it -- Edit's P7 and scope gate
    // moved here, joining Add and Close. What the VM still does with the key is a non-authoritative
    // pre-check that fails the UI early; the authoritative gate is in this file. Both literals must stay
    // in sync until the const has a single shared home.
    private const string FunctionKey = "Iam.OrgUnit.Declare";

    private static readonly JsonSerializerOptions DetailJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ErrorOr<UpsertResult>> CloseOrgUnitDeclarationAsync(CloseOrgUnitDeclarationRequest request)
    {
        var username = currentUser.Username ?? "unknown";

        // [1] P7 -- before any lock/transaction opens (no version row, no audit row on denial).
        var authz = await authorization.AuthorizeAsync(username, FunctionKey);
        if (authz.IsError)
        {
            return authz.Errors;
        }

        var scope = authz.Value;

        // [2a] Guard Self BEFORE any call that would throw InvalidOperationException
        // (IOrgUnitRepository.IsWithinScopeAsync / GetHistoryInScopeAsync both throw for Self --
        // org_unit_version has no owner column). Fails CLEARLY with the same code Role uses for an
        // insufficient scope, rather than let that exception escape through this shared service.
        if (scope.Level == ScopeLevel.Self)
        {
            return Error.Forbidden(
                "Authz.ScopeInsufficient",
                $"Org unit close/cancel is not applicable at {scope.Level} scope for actor '{username}'.");
        }

        // [2b] Scope-checked-write membership: the resolved scope alone only proves the actor holds SOME
        // scope for this function -- it does not prove the TARGET org unit falls within it (2026-08-05
        // security fix, mirrored from OrgUnitDeclarationViewModel.ExecuteSaveCloseAsync's own gate).
        if (!await orgUnitRepository.IsWithinScopeAsync(scope, request.OrgUnitId))
        {
            return Error.Forbidden(
                "OrgUnit.NotInScope",
                $"Org unit {request.OrgUnitId} is not within actor '{username}''s authorized scope.");
        }

        // [3] Read the target version server-side -- the caller's claim about which version this is is
        // never trusted. Restricted to a version that is CURRENTLY still in force (IsActive) so a
        // re-submission for an already-cancelled or already-superseded version fails with THIS service's
        // own OrgUnit.VersionNotFound, not a different code surfaced from deep inside the engine
        // (VersionedRepository.VersionNotFound, which also leaks the physical table name). Mirrors
        // RoleDeclarationService.CloseRoleDeclarationAsync's own rationale.
        //
        // Known, accepted race -- a VERSION-read TOCTOU, unrelated to the business-date race Task 0
        // removed (2026-08-11): if another
        // actor closes/cancels the SAME target version between this read and the composite below, the
        // engine's own VersionedRepository.VersionNotFound (naming the physical table) can still reach the
        // caller from inside the composite write. Fails clearly, no data corruption -- accepted, not fixed
        // here.
        var history = await orgUnitRepository.GetHistoryInScopeAsync(scope, request.OrgUnitId);
        var version = history.FirstOrDefault(v => v.Id == request.VersionId && v.IsActive);
        if (version is null)
        {
            return Error.NotFound(
                "OrgUnit.VersionNotFound",
                $"Org unit version {request.VersionId} was not found for org unit {request.OrgUnitId}.");
        }

        // [4] Server derives the branch and validates the close/cancel date -- delegated to the
        // entity-agnostic VersionCloseRules (AST.Core.EffectivePeriod), the single home of this
        // evaluation order (docs/shared-components.md §⑥). Never re-implemented or reordered here.
        // TASK 0 (2026-08-11): `today` is captured ONCE here and threaded unchanged
        // into the cancel branch below (OrgUnitRepository.CancelPlanAsync's `operationDate`) --
        // design-effective-period.md §3 requires a single business operation to capture "today" once and
        // use it consistently for every parameter; the engine no longer re-reads its own
        // IBusinessDateProvider for the cancel-eligibility guard.
        var today = dates.Today;
        var closeValidation = VersionCloseRules.Validate(
            today, new EffectivePeriod(version.EffectiveFrom, version.EffectiveTo), request.EffectiveThrough);
        if (closeValidation.IsError)
        {
            return closeValidation.Errors;
        }

        var isCancelPlanBranch = closeValidation.Value == VersionCloseBranch.CancelPlan;

        // [5] ONE CompositeWrite for the version write + the audit_log row -- an audit-write failure fails
        // the whole composite (both land or neither).
        var composite = new CompositeWrite(connections)
            .Enlist(orgUnitRepository, request.OrgUnitId);

        UpsertResult? upsertResult = null;

        var writeResult = await composite.ExecuteAsync(async context =>
        {
            // [5a] ROOT GATE (backlog 0.8, requester ruling 2026-08-21: a root org unit may not be closed,
            // absolutely -- there is no "unless it has no children" form).
            //
            // WHY IT IS HERE AND NOT BESIDE THE ROLE SIDE'S EQUIVALENT: RoleDeclarationService's
            // break-glass gate sits BEFORE its composite, and this one may not. Step [3]'s DTO above is
            // read before the org-unit lock is acquired, so its ParentId is reachable but NOT
            // authoritative -- an ordinary actor could observe non-root, lose a race to a concurrent
            // re-parent-to-root, and close a root. This read runs on the composite's own connection under
            // the lock. Deliberately independent of the parent-immutability guard: neither rule may rest
            // on the other having landed.
            var (found, parentId) = await orgUnitRepository.GetVersionParentIdAsync(
                context, request.OrgUnitId, request.VersionId);
            if (!found)
            {
                return Error.NotFound(
                    "OrgUnit.VersionNotFound",
                    $"Org unit version {request.VersionId} was not found for org unit {request.OrgUnitId}.");
            }

            var isRoot = parentId is null;

            // ONE authorization outcome governs BOTH the gate below and the break-glass audit row further
            // down. It used to be two independent IsBreakGlassAdmin calls, and
            // RealBreakGlassPolicy re-reads File B on EVERY call: if the store changed, was tampered with
            // or failed to read between them, the close committed on a `true` while the audit selection saw
            // `false` -- the operation succeeded and the ONE row that records "a normally-forbidden
            // operation was permitted" was silently omitted. Read once, use twice; a store that fails after
            // this line can no longer change what this operation records about itself.
            var isBreakGlassActor = breakGlass.IsBreakGlassAdmin(username);

            // The carve-out exists because parent is immutable and a root cannot be closed: without it a
            // MIS-DECLARED root would have no in-app remedy at all. It exempts the actor from THIS rule
            // only -- VersionCloseRules above already ran and is not re-entered or relaxed here.
            if (isRoot && !isBreakGlassActor)
            {
                return Error.Forbidden(
                    "OrgUnit.RootNotClosable",
                    $"The root org unit cannot be closed or cancelled by actor '{username}'.");
            }

            // CancelPlanAsync's `reason` parameter is non-nullable (unlike Role's Note-passthrough, which
            // has no such constraint downstream) -- Note is optional here (requester decision, matching the
            // role side), so a null Note becomes "" on the cancel branch rather than inventing placeholder
            // content. The close branch passes request.Note through as-is (nullable `reason` there).
            // KNOWN DIVERGENCE (accepted, not fixed here): the two branches therefore persist "no note"
            // differently -- cancel writes reason = '' while close leaves reason = NULL for the identical
            // caller input (Note: null). No data is lost (the audit_log row is the durable record of WHO
            // acted, independent of `reason` -- see the audit write below), but a later `reason IS NULL`
            // query would see two different shapes for the same "no note" input. Forced by CancelPlanAsync's
            // non-nullable `reason` parameter; not resolved here to avoid widening the repository's public
            // signature outside this task's scope.
            var write = isCancelPlanBranch
                ? await orgUnitRepository.CancelPlanAsync(
                    context, request.OrgUnitId, request.VersionId, today, username, request.Note ?? string.Empty)
                : await orgUnitRepository.CloseVersionAsync(
                    context, request.OrgUnitId, request.VersionId, request.EffectiveThrough!.Value,
                    new OperationDate(today), username, request.Note);
            if (write.IsError)
            {
                return write.Errors;
            }

            upsertResult = write.Value;

            var auditResult = await auditLog.WriteAsync(
                new AuditLogEntry(
                    "orgunit-close",
                    username,
                    $"org_unit_version:{request.VersionId}",
                    BuildDetailJson(
                        request.OrgUnitId, request.VersionId, isCancelPlanBranch ? "cancel" : "close",
                        isCancelPlanBranch ? null : request.EffectiveThrough, request.Note)),
                context.Transaction);
            if (auditResult.IsError)
            {
                return auditResult.Errors;
            }

            // A SECOND, security-specific row when the close only happened because the actor is a
            // break-glass rescuer. The ordinary "orgunit-close" row above records the retirement; it does
            // not record that a normally-forbidden operation was permitted, and that is the fact a security
            // review needs to find by querying for it rather than by inferring it from the unit's parent.
            // Same transaction, so it cannot survive a rollback of the write it describes.
            //
            // ⚠️ TWO rows is correct under the CURRENT sequencing. If the operation-history
            // slice lands (docs/design-operation-history.md), one row plus the operation row suffices --
            // re-check before assuming either shape is permanent.
            //
            // The break-glass conjunct is redundant TODAY -- the gate above returns for every non-break-glass
            // root close, so `isRoot` alone would select the same rows. It is written out anyway because the
            // condition must say what the row MEANS: if the root rule is ever relaxed, `isRoot` alone would
            // silently start labelling ordinary root closes as break-glass.
            if (isRoot && isBreakGlassActor)
            {
                var breakGlassAudit = await auditLog.WriteAsync(
                    new AuditLogEntry(
                        "orgunit-root-close-breakglass",
                        username,
                        $"org_unit_version:{request.VersionId}",
                        BuildDetailJson(
                            request.OrgUnitId, request.VersionId, isCancelPlanBranch ? "cancel" : "close",
                            isCancelPlanBranch ? null : request.EffectiveThrough, request.Note)),
                    context.Transaction);
                if (breakGlassAudit.IsError)
                {
                    return breakGlassAudit.Errors;
                }
            }

            return Result.Success;
        });

        return writeResult.IsError ? writeResult.Errors : upsertResult!;
    }

    // Second use-case: see IOrgUnitDeclarationService.AddOrgUnitDeclarationAsync's doc comment for the three
    // load-bearing properties. Step order deliberately mirrors CloseOrgUnitDeclarationAsync above.
    //
    // [1] P7 authorization -- BEFORE any lock/transaction opens.
    // [2] Global-scope gate.
    // [3] Enlist the temporal-FK parent (parent_id) up front when there is one. NOT the new identity: it does
    //     not exist when locks are taken, and the §7 carve-out is what authorises its write
    //     (VersionedRepository.EnsureCompositeEnlisted). Pre-minting it to obtain a lock key is exactly the
    //     defect 0.4b-A removed. No singleton root lock either -- EDGE E1 is RESOLVED with "no extra
    //     singleton named-lock for v1", and the root defect this slice fixes was sequential, not concurrent:
    //     the old probe simply could not SEE a future-dated root. The residual window is two admins declaring
    //     a root at the same instant, which E1 accepts for v1.
    // [4] ONE CompositeWrite: root-overlap probe -> mint -> first version -> audit_log row. Any failure rolls
    //     back all of them, so a zero-version header is not a state this path can produce and there is
    //     nothing to compensate.
    public async Task<ErrorOr<AddOrgUnitDeclarationResult>> AddOrgUnitDeclarationAsync(AddOrgUnitDeclarationRequest request)
    {
        var username = currentUser.Username ?? "unknown";

        // [1] P7 -- before any lock/transaction opens (no header row, no version row, no audit row on denial).
        var authz = await authorization.AuthorizeAsync(username, FunctionKey);
        if (authz.IsError)
        {
            return authz.Errors;
        }

        // [2] Add (root or non-root) requires system-wide scope. Moved here from
        // OrgUnitDeclarationViewModel.ExecuteSaveAddAsync (2026-08-05 decision-log, part 2) so it is not
        // bypassable by any caller that is not that screen: a narrower scope has no notion of "which unit to
        // attach under" that IsWithinScopeAsync could check the way Edit/Close do, so requiring Global
        // outright is the only sound gate.
        if (authz.Value.Level != ScopeLevel.Global)
        {
            return Error.Forbidden(
                "OrgUnit.AddRequiresGlobalScope",
                $"Creating an org unit requires {ScopeLevel.Global} scope; actor '{username}' holds {authz.Value.Level}.");
        }

        // [2b] ROOT GATE: the root org unit may be DECLARED and adjusted only
        // by a break-glass rescuer. Global scope is necessary and no longer sufficient for a root.
        //
        // Unlike Edit's and Close's root gates this one runs BEFORE the composite, and that is not an
        // inconsistency: those two read a STORED parent that a concurrent writer can change under them, so
        // they must decide under the identity lock. Here the parent is the caller's own request field for a
        // unit that does not exist yet, so there is nothing to race against.
        //
        // Read ONCE and reused by the audit row inside the transaction, for the same reason the other two
        // paths do it: a policy store re-read can disagree with itself and drop the row that records why
        // the operation was permitted.
        var isBreakGlassActor = breakGlass.IsBreakGlassAdmin(username);
        if (request.ParentId is null && !isBreakGlassActor)
        {
            return Error.Forbidden(
                "OrgUnit.RootNotDeclarable",
                $"A root org unit may only be declared by a break-glass administrator; actor '{username}' is not one.");
        }

        // [3] All locks up front. A root Add enlists nothing -- it has no temporal-FK parent.
        var composite = new CompositeWrite(connections);
        if (request.ParentId is { } parentId)
        {
            composite = composite.Enlist(orgUnitRepository, parentId);
        }

        long newOrgUnitId = 0;
        UpsertResult? upsertResult = null;

        var writeResult = await composite.ExecuteAsync(async context =>
        {
            // [4a] N1 root uniqueness, by OVERLAPPING PERIOD (requester ruling 2026-08-17): at most one root
            // on any given day, so a retired root MAY be succeeded by a new one. Runs INSIDE the transaction
            // on its connection, so it sees this composite's own in-flight writes.
            if (request.ParentId is null)
            {
                var rootPeriods = await orgUnitRepository.GetActiveRootPeriodsAsync(context);
                if (rootPeriods.Any(p => p.Overlaps(request.Period)))
                {
                    return Error.Validation(
                        "OrgUnit.RootPeriodOverlaps",
                        $"Another root org unit is already effective during [{request.Period.From:yyyy-MM-dd}, {request.Period.To:yyyy-MM-dd}].");
                }
            }

            // [4b] Minted only once every check above has passed, and immediately before its own first
            // version -- so this header cannot outlive a failure of that version write (§7).
            newOrgUnitId = await orgUnitRepository.CreateIdentityAsync(context);

            var write = await orgUnitRepository.UpsertAsync(
                context, newOrgUnitId, request.Period, request.OrgCode, request.OrgNameFullVn,
                request.OrgNameShortVn, request.ParentId, VersionOperationKind.Add, username, request.Reason,
                request.Supplemental);
            if (write.IsError)
            {
                return write.Errors;
            }

            upsertResult = write.Value;

            var auditResult = await auditLog.WriteAsync(
                new AuditLogEntry(
                    "orgunit-add",
                    username,
                    $"org_unit_version:{write.Value.NewVersionId}",
                    BuildAddDetailJson(newOrgUnitId, write.Value.NewVersionId, request.ParentId, request.Reason)),
                context.Transaction);
            if (auditResult.IsError)
            {
                return auditResult.Errors;
            }

            // A SECOND, security-specific row when a ROOT was declared, which [2b] permits only for a
            // break-glass rescuer. Same posture as the root edit and the root close: the "orgunit-add" row
            // records the declaration, this one records that a normally-forbidden operation was permitted.
            // Same transaction, so it cannot survive a rollback of the write it describes.
            if (request.ParentId is null && isBreakGlassActor)
            {
                var breakGlassAudit = await auditLog.WriteAsync(
                    new AuditLogEntry(
                        "orgunit-root-add-breakglass",
                        username,
                        $"org_unit_version:{write.Value.NewVersionId}",
                        BuildAddDetailJson(newOrgUnitId, write.Value.NewVersionId, request.ParentId, request.Reason)),
                    context.Transaction);
                if (breakGlassAudit.IsError)
                {
                    return breakGlassAudit.Errors;
                }
            }

            return Result.Success;
        });

        return writeResult.IsError
            ? writeResult.Errors
            : new AddOrgUnitDeclarationResult(newOrgUnitId, upsertResult!);
    }

    // Third use-case: see IOrgUnitDeclarationService.EditOrgUnitDeclarationAsync's doc comment for the
    // load-bearing property (a new parent is UNEXPRESSIBLE). Step order deliberately mirrors
    // CloseOrgUnitDeclarationAsync above.
    //
    // [1] P7 authorization -- BEFORE any lock/transaction opens.
    // [2] Self guard, then scope-checked-write membership of the TARGET unit.
    // [3] Resolve the target version server-side and match it against the caller's ExpectedVersionId.
    // [4] Enlist the identity AND the expected parent UP FRONT. The parent must be enlisted here and
    //     nowhere else: CompositeWrite.ExecuteAsync acquires every lock before the body runs
    //     (AST.Infrastructure/VersionedRepository.cs, EnsureCompositeEnlisted), so a parent first learned
    //     inside the transaction fails CompositeWrite.NotEnlisted. That is why the request carries an
    //     ExpectedParentId at all -- it is a lock key first and a verified value second, exactly like the
    //     role side's ExpectedRoleCode.
    // [5] ONE CompositeWrite: authoritative parent read -> verify the echo -> version write -> audit row.
    public async Task<ErrorOr<UpsertResult>> EditOrgUnitDeclarationAsync(EditOrgUnitDeclarationRequest request)
    {
        var username = currentUser.Username ?? "unknown";

        // [1] P7 -- before any lock/transaction opens (no version row, no audit row on denial).
        var authz = await authorization.AuthorizeAsync(username, FunctionKey);
        if (authz.IsError)
        {
            return authz.Errors;
        }

        var scope = authz.Value;

        // [2a] Guard Self BEFORE any call that would throw InvalidOperationException -- same rationale and
        // same code as the close path, so the two report an inapplicable scope identically.
        if (scope.Level == ScopeLevel.Self)
        {
            return Error.Forbidden(
                "Authz.ScopeInsufficient",
                $"Org unit edit is not applicable at {scope.Level} scope for actor '{username}'.");
        }

        // [2b] The resolved scope proves the actor holds SOME scope for this function, not that the TARGET
        // unit falls within it. This gate used to live in OrgUnitDeclarationViewModel.ExecuteSaveEditAsync,
        // where every caller that was not that screen got none of it (backlog 0.7).
        if (!await orgUnitRepository.IsWithinScopeAsync(scope, request.OrgUnitId))
        {
            return Error.Forbidden(
                "OrgUnit.NotInScope",
                $"Org unit {request.OrgUnitId} is not within actor '{username}''s authorized scope.");
        }

        // [3] The caller's claim about which version it edited is checked, never trusted. Restricted to a
        // version still in force, so a re-submission against an already-superseded or cancelled version
        // fails with THIS service's own code rather than one surfaced from inside the engine.
        var history = await orgUnitRepository.GetHistoryInScopeAsync(scope, request.OrgUnitId);
        if (!history.Any(v => v.Id == request.ExpectedVersionId && v.IsActive))
        {
            return Error.NotFound(
                "OrgUnit.VersionNotFound",
                $"Org unit version {request.ExpectedVersionId} was not found for org unit {request.OrgUnitId}.");
        }

        // The composite's ONE business date, captured once and threaded into everything below that needs a
        // "today" -- the close-date floor and the Close half's OperationDate. Re-reading the clock inside
        // the transaction is a live defect, and a gesture that ends coverage is exactly the
        // shape that creates it (docs/design-effective-period.md section 3).
        var today = dates.Today;

        // [4] All locks up front. A unit read as root enlists nothing extra -- it has no temporal-FK parent.
        var composite = new CompositeWrite(connections)
            .Enlist(orgUnitRepository, request.OrgUnitId);
        if (request.ExpectedParentId is { } expectedParentId)
        {
            composite = composite.Enlist(orgUnitRepository, expectedParentId);
        }

        UpsertResult? upsertResult = null;

        var writeResult = await composite.ExecuteAsync(async context =>
        {
            // [5a] THE authoritative read. Step [3]'s ran before the locks were taken, so its values are
            // reachable but not authoritative; this one runs on the composite's own connection under the
            // identity lock.
            var storedParents = await orgUnitRepository.GetActiveParentIdsAsync(context, request.OrgUnitId);

            // [5b] More than one distinct parent means this identity's history disagrees with itself -- the
            // pre-0.7 writer allowed that (parent_id went per version on every Edit). There is no single
            // "stored parent" to preserve, so the edit is refused rather than silently picking one row's
            // value and freezing it for good.
            // No active version left at all: the row was deactivated between step [3] and this lock. That
            // is a VERSION problem, not a parent problem -- reporting ParentMismatch here would send the
            // operator to re-check a parent that is not what changed.
            if (storedParents.Count == 0)
            {
                return Error.NotFound(
                    "OrgUnit.VersionNotFound",
                    $"Org unit {request.OrgUnitId} has no active version left to edit.");
            }

            // More than one distinct parent means this identity's history disagrees with itself.
            // Its own code, NOT the stale-echo code below: the two are indistinguishable to a caller that
            // only reads FirstError.Code, and a test asserting the shared code would stay green with this
            // guard deleted.
            if (storedParents.Count > 1)
            {
                return Error.Conflict(
                    "OrgUnit.ParentNotWellDefined",
                    $"Org unit {request.OrgUnitId} has {storedParents.Count} distinct parents across its " +
                    "active versions; its stored parent is not well-defined.");
            }

            var storedParent = storedParents[0];
            if (storedParent != request.ExpectedParentId)
            {
                return Error.Conflict(
                    "OrgUnit.ParentMismatch",
                    $"Org unit {request.OrgUnitId}'s stored parent is not the one the caller read.");
            }

            // [5b-2] ROOT GATE: the root org unit may be DECLARED and
            // ADJUSTED only by a break-glass rescuer; an ordinary admin, however wide their scope, may only
            // read it. Until this rule the gate covered close alone and lived in
            // CloseOrgUnitDeclarationAsync alone -- so Sửa could end a root that Đóng refuses, and could
            // rename or re-date one freely. Enforced HERE because `storedParent` is the value this
            // transaction read under the identity lock: an ordinary actor could otherwise observe non-root,
            // lose a race to a concurrent change, and edit a root.
            //
            // BEFORE the input guards below, deliberately. "You may not touch this record at all" is a
            // different answer from "your note is missing", and reporting the second to someone who cannot
            // perform the operation either way tells them to fix the wrong thing.
            //
            // Read ONCE and reused by the audit row at [5d-2]. A policy store re-read between the gate and
            // the row can disagree with itself, and then the operation succeeds while the one row that
            // records "a normally-forbidden operation was permitted" is silently omitted.
            var isBreakGlassActor = breakGlass.IsBreakGlassAdmin(username);
            if (storedParent is null && !isBreakGlassActor)
            {
                return Error.Forbidden(
                    "OrgUnit.RootNotEditable",
                    $"The root org unit may only be adjusted by a break-glass administrator; actor '{username}' is not one.");
            }

            // [5b-3] THE locked period read. Step [3]'s pre-lock history is reachable but not authoritative,
            // and a guard decided against a value that can change under the lock is not a guard -- the same
            // reasoning OrgUnit.ParentMismatch rests on. ONE read serves the note guard below and, when
            // EndsOn is set, the "ends here" guards: three separate reads would be three chances for the
            // answers to disagree with each other.
            var activePeriods = await orgUnitRepository.GetActivePeriodsAsync(context, request.OrgUnitId);
            var stored = activePeriods.FirstOrDefault(v => v.VersionId == request.ExpectedVersionId);
            if (stored is null)
            {
                return Error.NotFound(
                    "OrgUnit.VersionNotFound",
                    $"Org unit version {request.ExpectedVersionId} was not found for org unit {request.OrgUnitId}.");
            }

            // [5b-4] The note is the ONLY carrier of "why the period changed",
            // so it stops being optional exactly when the period changes. The trigger is "the requested
            // period differs from the STORED one" -- NOT "this is an Edit", which would quietly turn every
            // rename into a note-requiring save. The ViewModel keeps a pre-check as an affordance only; this
            // is the authoritative one, and it is here because any other caller of the service gets it too.
            if (request.Period != stored.Period && string.IsNullOrWhiteSpace(request.Reason))
            {
                return Error.Validation(
                    "OrgUnit.ReasonRequiredForPeriodChange",
                    $"Org unit {request.OrgUnitId}'s effective period changed from " +
                    $"[{stored.Period.From:yyyy-MM-dd}, {stored.Period.To:yyyy-MM-dd}] to " +
                    $"[{request.Period.From:yyyy-MM-dd}, {request.Period.To:yyyy-MM-dd}], so a reason is required.");
            }

            // [5c-0] "Ends here" guards. Every one of them decides against `stored`,
            // the period read at [5b-3] on THIS transaction under the identity lock -- never against a
            // value the caller sent.
            if (request.EndsOn is { } endsOn)
            {
                // (2) The screen fills Period.To and EndsOn from ONE date box, so a caller where the two
                // differ disagrees with itself. Verified and never used -- the same posture as
                // ExpectedParentId: the value written comes from `stored`, not from the echo.
                if (request.Period.To != endsOn)
                {
                    return Error.Validation(
                        "OrgUnit.EndsOnDisagreesWithPeriod",
                        $"The requested period ends on {request.Period.To:yyyy-MM-dd} but the end date is " +
                        $"{endsOn:yyyy-MM-dd}; one save cannot mean both.");
                }

                // (3) Nothing to remove means the ordinary Edit already does the job and the confirm should
                // never have offered this route. This guard exists so the engine's own InvalidShrink does
                // NOT surface here: InvalidShrink names a cut window [From, To) the
                // operator never typed, and `EndsOn == storedEnd` -- the everyday "I meant the end I already
                // have" mistake -- lands exactly on it.
                if (endsOn < request.Period.From || endsOn >= stored.Period.To)
                {
                    return Error.Validation(
                        "OrgUnit.EndsOnNotBeforeStoredEnd",
                        $"The end date {endsOn:yyyy-MM-dd} must fall inside the stored period " +
                        $"[{stored.Period.From:yyyy-MM-dd}, {stored.Period.To:yyyy-MM-dd}) for this version to " +
                        "have a tail to remove.");
                }

                // (4) EndsOn ends ONE VERSION'S stretch. That is only "the unit ends" if nothing else
                // survives after it -- reachable through the case-8 shape, where another active version
                // covers days beyond this one's end. The operator confirmed something false, so refuse and
                // name the surviving period rather than writing a half-ended unit.
                var later = activePeriods.FirstOrDefault(
                    v => v.VersionId != request.ExpectedVersionId && v.Period.To > endsOn);
                if (later is not null)
                {
                    return Error.Validation(
                        "OrgUnit.EndsOnLeavesLaterCoverage",
                        $"Org unit {request.OrgUnitId} still has an active version covering " +
                        $"[{later.Period.From:yyyy-MM-dd}, {later.Period.To:yyyy-MM-dd}], so it does not end " +
                        $"on {endsOn:yyyy-MM-dd}.");
                }

                // (5) The close-date FLOOR, applied to the row the upsert is about to write: the operator
                // has just stated a close intent, so the floor every other close obeys applies here too.
                // Delegated to the shared VersionCloseRules rather than re-derived, which is the whole
                // reason that component exists (docs/shared-components.md section 6).
                //
                // This does NOT arrive "for free" from CloseVersionAsync: the base only runs the
                // ValidateClose hook, which OrgUnitRepository does not override, plus its own InvalidShrink
                // window. So the rules are called here explicitly.
                //
                // The BRANCH rule is deliberately not applied. A
                // version whose coverage starts today or later is a CancelPlan target, and Validate would
                // refuse the whole gesture with CloseDateNotApplicableToCancelPlan -- leaving the remnant
                // tail unfixed for exactly the shape an operator most often mis-declares, and saying
                // so in words about cancelling a plan they never asked to cancel.
                //
                // Nothing is lost by scoping to the Retire branch: on the CancelPlan branch the floor is
                // already implied, because clause 3 above guarantees endsOn >= Period.From and that branch
                // is only reached when Period.From >= today. The other Retire-branch arms are unreachable
                // for the same reason -- clause 3 has already pinned endsOn inside [From, storedEnd).
                var upsertRowPeriod = new EffectivePeriod(request.Period.From, stored.Period.To);
                if (VersionCloseRules.BranchFor(today, upsertRowPeriod) == VersionCloseBranch.Retire)
                {
                    var closeRules = VersionCloseRules.Validate(today, upsertRowPeriod, endsOn);
                    if (closeRules.IsError)
                    {
                        return closeRules.Errors;
                    }
                }
            }

            // [5c] `storedParent` -- NOT request.ExpectedParentId. The echo is a check; the value written is
            // the one this transaction read. Passing the echo through would make the caller the source of a
            // field this method exists to make immutable.
            //
            // The upsert period's END is the STORED end when EndsOn is set -- b unchanged, so the 8-case
            // algebra produces NO tail of its own and the Close half below is what reduces coverage.
            // request.Period.From still applies: moving the start later is a legitimate
            // part of the same save and yields an ordinary head remnant.
            var upsertPeriod = request.EndsOn is null
                ? request.Period
                : new EffectivePeriod(request.Period.From, stored.Period.To);

            var write = await orgUnitRepository.UpsertAsync(
                context, request.OrgUnitId, upsertPeriod, request.OrgCode, request.OrgNameFullVn,
                request.OrgNameShortVn, storedParent, VersionOperationKind.Edit, username, request.Reason,
                request.Supplemental);
            if (write.IsError)
            {
                return write.Errors;
            }

            upsertResult = write.Value;

            // [5c-2] Closing the row the upsert just wrote leaves a remnant carrying those NEW values and no
            // tail. Running the EXISTING Close is what makes the coverage-REDUCING half inherit the reverse-FK
            // guard and the P11 auto-cut: UpsertAsync runs neither, and its own comment says why -- the 8-case
            // algebra never reduces coverage. This gesture does, so it may not skip them.
            //
            // `upsertResult` is REASSIGNED to the Close's result, because the row that survives is the Close
            // remnant and the transient [From, storedEnd] row is deactivated inside this same transaction.
            // Everything downstream -- the audit target, the caller's returned NewVersionId -- must name the
            // survivor.
            if (request.EndsOn is { } endsOnToApply)
            {
                var close = await orgUnitRepository.CloseVersionAsync(
                    context, request.OrgUnitId, write.Value.NewVersionId, endsOnToApply,
                    new OperationDate(today), username, request.Reason);
                if (close.IsError)
                {
                    return close.Errors;
                }

                upsertResult = close.Value;
            }

            // The row that SURVIVES this save. On an ordinary Edit it is the row the upsert wrote; on an
            // "ends here" it is the Close remnant, and pointing the audit trail at `write.Value` instead
            // would name a row this same transaction set isactive = 0.
            var survivingVersionId = upsertResult.NewVersionId;

            // [5d] Edit had NO audit row while it ran from the ViewModel. It gets one here for the same
            // reason Add and Close have theirs, and on the same transaction: either both land or neither.
            var auditResult = await auditLog.WriteAsync(
                new AuditLogEntry(
                    "orgunit-edit",
                    username,
                    $"org_unit_version:{survivingVersionId}",
                    BuildEditDetailJson(
                        request.OrgUnitId, survivingVersionId, storedParent, request.Reason)),
                context.Transaction);
            if (auditResult.IsError)
            {
                return auditResult.Errors;
            }

            // [5d-2] A SECOND, security-specific row whenever a ROOT edit only happened because the actor
            // is a break-glass rescuer -- the same fact, recorded the same way, as the root close at [5d]
            // of CloseOrgUnitDeclarationAsync. The "orgunit-edit" row above records the edit; it does not
            // record that a normally-forbidden operation was permitted, and that is the fact a security
            // review has to be able to find by querying rather than by inferring it from parent_id.
            //
            // ONE event type for the whole operation, named after the operation, matching how the ordinary
            // rows are named. Whether this particular edit also ENDED the unit is in the row's own detail,
            // not in a second event type -- under the 2026-09-03 ruling every root edit is equally
            // break-glass-only, so splitting the name by gesture would imply a distinction that no longer
            // exists.
            //
            // The break-glass conjunct is redundant TODAY -- the gate at [5b-4] returns for every
            // non-break-glass root edit -- but the condition must say what the row MEANS, or a later
            // relaxation of the root rule would silently start labelling ordinary saves break-glass.
            if (storedParent is null && isBreakGlassActor)
            {
                var breakGlassAudit = await auditLog.WriteAsync(
                    new AuditLogEntry(
                        "orgunit-root-edit-breakglass",
                        username,
                        $"org_unit_version:{survivingVersionId}",
                        BuildEditDetailJson(
                            request.OrgUnitId, survivingVersionId, storedParent, request.Reason)),
                    context.Transaction);
                if (breakGlassAudit.IsError)
                {
                    return breakGlassAudit.Errors;
                }
            }

            return Result.Success;
        });

        return writeResult.IsError ? writeResult.Errors : upsertResult!;
    }

    // The save-time confirm needs the OPERATIONS the write would
    // perform, and the only preview that existed -- OrgUnitRepository.PreviewUpsertAsync -- returns the
    // overlapping VERSIONS instead. A caller given only that has to re-derive the remnants itself, which
    // puts a second copy of the LOCKED 8-case algebra in the layer furthest from it.
    //
    // ADVISORY, and deliberately so: this runs OUTSIDE the identity lock, EditOrgUnitDeclarationAsync
    // re-plans under it, and nothing compares the two. A single admin
    // declares at a time -- so the confirm-to-save window is accepted rather than guarded.
    //
    // Same authorization, scope and version gates as the write it previews, in the same order: a preview
    // that answered for a unit the actor cannot edit would hand out that unit's periods for free.
    public async Task<ErrorOr<IReadOnlyList<PreviewedRemnant>>> PreviewEditAsync(
        long orgUnitId, long expectedVersionId, EffectivePeriod period, DateOnly? endsOn)
    {
        var username = currentUser.Username ?? "unknown";

        var authz = await authorization.AuthorizeAsync(username, FunctionKey);
        if (authz.IsError)
        {
            return authz.Errors;
        }

        var scope = authz.Value;
        if (scope.Level == ScopeLevel.Self)
        {
            return Error.Forbidden(
                "Authz.ScopeInsufficient",
                $"Org unit edit is not applicable at {scope.Level} scope for actor '{username}'.");
        }

        if (!await orgUnitRepository.IsWithinScopeAsync(scope, orgUnitId))
        {
            return Error.Forbidden(
                "OrgUnit.NotInScope",
                $"Org unit {orgUnitId} is not within actor '{username}''s authorized scope.");
        }

        var activeVersions = (await orgUnitRepository.GetHistoryInScopeAsync(scope, orgUnitId))
            .Where(v => v.IsActive)
            .ToList();

        var target = activeVersions.FirstOrDefault(v => v.Id == expectedVersionId);
        if (target is null)
        {
            return Error.NotFound(
                "OrgUnit.VersionNotFound",
                $"Org unit version {expectedVersionId} was not found for org unit {orgUnitId}.");
        }

        // The period the UPSERT would carry, which is NOT the period the operator typed once they have
        // chosen "the unit ends on that date": section 19.1 keeps the stored end so the algebra produces no
        // tail of its own, and the Close half cuts coverage back to `endsOn` afterwards. Previewing the
        // typed period instead would show the operator the very tail the gesture exists to remove.
        var upsertPeriod = endsOn is null
            ? period
            : new EffectivePeriod(period.From, target.EffectiveTo);

        var plan = periodEditor.PlanUpsert(
            activeVersions
                .Select(IVersionRow (v) => new PreviewVersionRow(v.Id, v.OrgUnitId, v.EffectiveFrom, v.EffectiveTo, v.IsActive))
                .ToList(),
            upsertPeriod);
        if (plan.IsError)
        {
            return plan.Errors;
        }

        // `CarriesOldBusinessData` is the write path's own discriminator for a remnant
        // (VersionedRepository ApplyUpsertPlan branches on exactly this), so the preview reads the plan the
        // same way rather than re-deciding which operations count.
        IReadOnlyList<PreviewedRemnant> remnants = plan.Value.Operations
            .Where(op => op.CarriesOldBusinessData)
            .Select(op => new PreviewedRemnant(op.Period))
            .ToList();

        return remnants.ToErrorOr();
    }

    // IVersionRow over the PUBLIC DTO. The write path plans over entities, which stay in the data layer
    // (rule-module-boundary item 2), so the service adapts what it is allowed to see. Only the four fields
    // the algebra reads are carried -- this is a view, never a row.
    private sealed record PreviewVersionRow(
        long Id, long IdentityId, DateOnly EffectiveFrom, DateOnly EffectiveTo, bool IsActive) : IVersionRow;

    // Target and action shape match this entity's own "orgunit-close" row above, so both are queried the
    // same way.
    //
    // TRIMMED ON PURPOSE (requester ruling 2026-08-17): `detail` carries the action's POINTERS -- identity,
    // version, parent, note -- and NOT a second copy of org_code / names / period / supplemental. Those live
    // on the org_unit_version row this points at, which is their authoritative home. The trim is safe HERE
    // specifically because an Add's own version row records recorded_by, reason and operation_kind='Add'
    // itself, and no later operation rewrites its business columns: a cut or split soft-deactivates it and
    // inserts new rows, and a cancel only flips isactive/status. Contrast the CANCEL branch, where
    // VersionedRepository.CancelVersionCoreAsync's no-predecessor path leaves the CREATOR's recorded_by
    // untouched -- there the audit row is the ONLY record of who acted, so nothing may be trimmed from it.
    //
    // Serialized once, at write time. A later history read renders this stored string as-is and must never
    // join back to current data to "refresh" it -- what was recorded stays what was recorded.
    private static string BuildAddDetailJson(long orgUnitId, long versionId, long? parentId, string? note) =>
        JsonSerializer.Serialize(
            new OrgUnitAddAuditDetail(orgUnitId, versionId, parentId, note),
            DetailJsonOptions);

    private sealed record OrgUnitAddAuditDetail(
        long OrgUnitId,
        long VersionId,
        long? ParentId,
        string? Note);

    // Same POINTERS-only shape and same trim rationale as BuildAddDetailJson above: identity, version,
    // parent, note -- never a second copy of the business columns, which live on the version row this
    // points at. `ParentId` is carried even though this operation cannot change it, because the row is
    // then self-describing without a join: a reader of the audit trail sees what the parent WAS at the
    // moment of the edit, which is the fact backlog 0.7 makes permanent.
    private static string BuildEditDetailJson(long orgUnitId, long versionId, long? parentId, string? note) =>
        JsonSerializer.Serialize(
            new OrgUnitEditAuditDetail(orgUnitId, versionId, parentId, note),
            DetailJsonOptions);

    private sealed record OrgUnitEditAuditDetail(
        long OrgUnitId,
        long VersionId,
        long? ParentId,
        string? Note);

    // Detail shape mirrors RoleDeclarationService.BuildRoleCloseDetailJson's own "role-close" shape
    // (docs/shared-components.md convention), scoped to org-unit fields.
    private static string BuildDetailJson(
        long orgUnitId, long versionId, string branch, DateOnly? effectiveThrough, string? note) =>
        JsonSerializer.Serialize(
            new OrgUnitCloseAuditDetail(orgUnitId, versionId, branch, effectiveThrough, note),
            DetailJsonOptions);

    private sealed record OrgUnitCloseAuditDetail(
        long OrgUnitId,
        long VersionId,
        string Branch,
        DateOnly? EffectiveThrough,
        string? Note);
}
