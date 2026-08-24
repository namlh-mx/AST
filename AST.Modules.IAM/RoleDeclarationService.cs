using System.Text.Json;
using AST.Core.Data;
using AST.Core.EffectivePeriod;
using AST.Core.Iam;
using AST.Core.Time;
using AST.Infrastructure;
using AST.Modules.IAM.Data.Repositories;
using ErrorOr;

namespace AST.Modules.IAM;

// Closes OPEN-B2: "Edit role + Revoke a grant + Add a grant in one Save" — see AST.Core/Iam/IRoleDeclarationService.cs
// for the use-case contract. Depends on the CONCRETE RoleRepository/RolePermissionRepository (not the public
// interfaces) because it needs their ICompositeWriteContext overloads, which are deliberately NOT on
// IRoleRepository/IRolePermissionRepository (rule-module-boundary §3 — composite overloads stay concrete-only).
//
// Flow:
//   [1] P7 re-authorization (IAuthorizationService.AuthorizeAsync) — BEFORE any lock/transaction opens.
//   [2] Read code ownership WITHOUT a lock to learn only this: is there a PRE-EXISTING role identity this
//       save will write (an edit target, or a dormant code owner to re-attach to)? If so it is Enlisted, so
//       its lock is taken up front like every other pre-existing identity. Nothing is minted here.
//   [3] ONE CompositeWrite: role version write + every grant Revoke (Close/Cancel) + every grant Add,
//       plus ONE `role-change` audit_log row for the role Add/Edit and ONE `permission-change` row per
//       grant Add/Revoke/Cancel — all at target `role:{roleId}`, all sharing one operation id in detail,
//       all on context.Transaction; an audit-write failure fails the whole composite (never swallowed).
//   [4] Identities that do NOT exist yet — a brand-new role, every grant-to-add — are minted INSIDE that
//       transaction (design-effective-period.md §7), each immediately before its own first version. So a
//       failure of any kind leaves no header and no version, and there is nothing to compensate: this path
//       has no compensation step, by construction rather than by best effort.
//
// AdminFlagLockKey is Enlisted UNCONDITIONALLY on every save (not only when the admin flag actually changes)
// — a deliberate accepted tradeoff avoiding a TOCTOU between "does this change the flag" and "did I lock it"
// (rare admin-only op, ~30 users; see RoleRepository.AdminFlagLockKey's own comment).
//
// FunctionRepository dependency: not written here, only Enlisted — CompositeWrite.ExecuteAsync's up-front
// §7 lock batch requires EVERY temporal-FK PARENT identity a context write will touch to already be
// Enlisted (VersionedRepository.EnsureCompositeEnlisted; role_permission_version's parents are role_id AND
// function_id). Each grant-to-add's function_id must therefore be Enlisted alongside the role and every
// grant identity, or the composite context Upsert fails clear with CompositeWrite.NotEnlisted.
internal sealed class RoleDeclarationService(
    RoleRepository roleRepository,
    RolePermissionRepository rolePermissionRepository,
    FunctionRepository functionRepository,
    IDbConnectionFactory connections,
    IAuditLogWriter auditLog,
    IAuthorizationService authorization,
    IBreakGlassPolicy breakGlass,
    ICurrentWindowsUser currentUser,
    IBusinessDateProvider dates) : IRoleDeclarationService
{
    // Registered into IFunctionRegistry by the composition root (AST/App.xaml.cs, FX002) so
    // FunctionCatalogSyncService syncs it into the `function` table and AuthorizeAsync can resolve it --
    // named consistently with the existing "Iam.OrgUnit.Declare" convention
    // (AST.Shell/ViewModels/Iam/OrgUnitDeclarationViewModel.cs).
    private const string FunctionKey = "Iam.Role.Declare";

    // The one exclusively-owned dependent table Role declares (RoleRepository.ExclusivelyOwnedDependents).
    // Named here so the close cascade can filter auto-cut outcomes by table rather than assuming every
    // outcome is a grant — two dependent tables have unrelated id spaces.
    private const string RolePermissionVersionTable = "role_permission_version";

    private static readonly JsonSerializerOptions DetailJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ErrorOr<SaveRoleDeclarationResult>> SaveRoleDeclarationAsync(SaveRoleDeclarationRequest request)
    {
        var username = currentUser.Username ?? "unknown";

        var operationDate = OperationDate.Capture(dates);
        var today = operationDate.Value;
        var period = new EffectivePeriod(today, EffectivePeriod.OpenEnd);
        var operationId = Guid.NewGuid().ToString("N");

        var authz = await authorization.AuthorizeAsync(username, FunctionKey);
        if (authz.IsError)
        {
            return authz.Errors;
        }

        if (authz.Value.Level != ScopeLevel.Global)
        {
            return Error.Forbidden(
                "Authz.ScopeInsufficient",
                $"Role declaration requires Global scope; actor '{username}' is authorized only at {authz.Value.Level}.");
        }

        foreach (var code in request.Target is RoleSaveTarget.ExistingRole e
                     ? new[] { request.RoleCode, e.ExpectedRoleCode }
                     : new[] { request.RoleCode })
        {
            if (code.Trim().Any(c => c > 127 || char.IsControl(c)))
            {
                return Error.Validation(
                    "Role.CodeNotAscii",
                    $"Mã vai trò '{code}' chứa ký tự không hợp lệ (ngoài bảng ASCII hoặc ký tự điều khiển). "
                    + "Mã vai trò chỉ dùng chữ và số không dấu.");
            }
        }

        // One normalisation, one value, used for the lock key, every ownership comparison and the persisted
        // column alike. Trimming in CodeLockKey while the reads and the write see the raw string means the lock
        // says "same code" where MySQL says "different" (leading whitespace is never ignored, and
        // utf8mb4_0900_ai_ci is NO PAD, so trailing whitespace is significant) — which mints a second identity
        // for a code that already has a live owner. Case is NOT folded here: the column's collation is already
        // case-insensitive, so the operator's own capitalisation is what gets stored and displayed.
        var roleCode = request.RoleCode.Trim();
        var expectedRoleCode = request.Target is RoleSaveTarget.ExistingRole existingForNorm
            ? existingForNorm.ExpectedRoleCode.Trim()
            : null;

        var adminFlagChangeAuthorized = breakGlass.IsBreakGlassAdmin(username);
        var revokeIds = request.GrantIdentityIdsToRevoke.Distinct().ToList();

        var guess = await GuessIdentityAsync(request.Target, roleCode, today);
        if (guess.IsError)
        {
            return guess.Errors;
        }

        var (preExistingRoleId, reattached) = guess.Value;

        var composite = new CompositeWrite(connections)
            .Enlist(RoleRepository.AdminFlagLockKey)
            .Enlist(RoleRepository.CodeLockKey(roleCode));

        // Only a PRE-EXISTING role identity is Enlisted. On the brand-new-role path there is nothing to
        // enlist yet and nothing to lock: the identity is minted inside the transaction (§7 carve-out), and
        // the code lock above — held for the whole composite — is what keeps two clients from creating two
        // identities for the same code.
        if (preExistingRoleId is { } enlistableRoleId)
        {
            composite = composite.Enlist(roleRepository, enlistableRoleId);
        }

        if (expectedRoleCode is not null)
        {
            composite = composite.Enlist(RoleRepository.CodeLockKey(expectedRoleCode));
        }

        foreach (var revokeId in revokeIds)
        {
            composite = composite.Enlist(rolePermissionRepository, revokeId);
        }

        foreach (var add in request.GrantsToAdd)
        {
            composite = composite.Enlist(functionRepository, add.FunctionId);
        }

        // Assigned by the in-transaction re-decision below (re-attached, edited, or freshly minted) and read
        // back after the composite commits.
        long resolvedRoleId = 0;
        long newRoleVersionId = 0;
        var addedIds = new List<long>();
        var revokedIds = new List<long>();
        var derivedOperationKind = request.Target is RoleSaveTarget.NewRole
            ? VersionOperationKind.Add
            : VersionOperationKind.Edit;

        var result = await composite.ExecuteAsync(async context =>
        {
            var reDecision = await ReDecideIdentityAsync(
                context, request.Target, roleCode, expectedRoleCode, today, preExistingRoleId, reattached);
            if (reDecision.IsError)
            {
                return reDecision.Errors;
            }

            resolvedRoleId = reDecision.Value;

            var roleWrite = await roleRepository.UpsertAsync(
                context, resolvedRoleId, period, roleCode, request.RoleName,
                request.IsAdminRole, adminFlagChangeAuthorized, derivedOperationKind, operationDate,
                username, request.Reason);
            if (roleWrite.IsError)
            {
                return roleWrite.Errors;
            }

            newRoleVersionId = roleWrite.Value.NewVersionId;

            var roleAudit = await auditLog.WriteAsync(
                new AuditLogEntry(
                    "role-change",
                    username,
                    $"role:{resolvedRoleId}",
                    BuildRoleSaveDetailJson(
                        derivedOperationKind == VersionOperationKind.Add ? "add" : "edit",
                        operationId, resolvedRoleId, newRoleVersionId, roleCode, request.RoleName,
                        request.IsAdminRole, request.Reason)),
                context.Transaction);
            if (roleAudit.IsError)
            {
                return roleAudit.Errors;
            }

            foreach (var grantId in revokeIds)
            {
                var active = await rolePermissionRepository.GetByIdentityAsync(context, grantId, today);
                if (active.IsError)
                {
                    return active.Errors;
                }

                if (active.Value.RoleId != resolvedRoleId)
                {
                    return Error.NotFound(
                        "RolePermission.NotOwnedByRole",
                        $"Role permission {grantId} does not belong to role {resolvedRoleId} (belongs to role {active.Value.RoleId}).");
                }

                var branch = VersionCloseRules.BranchFor(
                    today, new EffectivePeriod(active.Value.EffectiveFrom, active.Value.EffectiveTo));

                string action;
                DateOnly reportedTo;

                if (branch == VersionCloseBranch.Retire)
                {
                    action = "revoke";
                    var retireThrough = today.AddDays(-1);

                    var revoke = await rolePermissionRepository.RevokeAsync(
                        context, grantId, active.Value.Id, retireThrough, operationDate, username,
                        request.Reason);
                    if (revoke.IsError)
                    {
                        return revoke.Errors;
                    }

                    reportedTo = retireThrough;
                }
                else
                {
                    action = "cancel";

                    var cancel = await rolePermissionRepository.CancelPlanAsync(
                        context, grantId, active.Value.Id, today, username, request.Reason ?? string.Empty);
                    if (cancel.IsError)
                    {
                        return cancel.Errors;
                    }

                    reportedTo = active.Value.EffectiveTo;
                }

                var auditResult = await auditLog.WriteAsync(
                    new AuditLogEntry(
                        "permission-change",
                        username,
                        $"role:{resolvedRoleId}",
                        BuildDetailJson(
                            action, operationId, resolvedRoleId, grantId, active.Value.FunctionId,
                            active.Value.ScopeLevel, active.Value.EffectiveFrom, reportedTo, request.Reason)),
                    context.Transaction);
                if (auditResult.IsError)
                {
                    return auditResult.Errors;
                }

                revokedIds.Add(grantId);
            }

            foreach (var add in request.GrantsToAdd)
            {
                var grantPeriod = period;

                var overlapping = await rolePermissionRepository.GetActiveGrantsForPeriodAsync(
                    context, resolvedRoleId, grantPeriod);
                var conflict = overlapping.FirstOrDefault(g => g.FunctionId == add.FunctionId);
                if (conflict is not null)
                {
                    return Error.Conflict(
                        "RolePermission.OverlappingGrant",
                        $"Role {resolvedRoleId} already has an active grant (role_permission {conflict.RolePermissionId}) for function {add.FunctionId} over [{conflict.EffectiveFrom:yyyy-MM-dd}, {conflict.EffectiveTo:yyyy-MM-dd}], overlapping the requested [{grantPeriod.From:yyyy-MM-dd}, {grantPeriod.To:yyyy-MM-dd}].");
                }

                // Minted only once every check above has passed, and immediately before its own first
                // version — so this header cannot outlive a failure of that version write (§7).
                var grantId = await rolePermissionRepository.CreateIdentityAsync(context);

                var grantWrite = await rolePermissionRepository.UpsertAsync(
                    context, grantId, grantPeriod, resolvedRoleId, add.FunctionId, add.ScopeLevel,
                    VersionOperationKind.Add, operationDate, username, add.Note);
                if (grantWrite.IsError)
                {
                    return grantWrite.Errors;
                }

                var auditResult = await auditLog.WriteAsync(
                    new AuditLogEntry(
                        "permission-change",
                        username,
                        $"role:{resolvedRoleId}",
                        BuildDetailJson(
                            "grant", operationId, resolvedRoleId, grantId, add.FunctionId, add.ScopeLevel,
                            grantPeriod.From, grantPeriod.To, add.Note)),
                    context.Transaction);
                if (auditResult.IsError)
                {
                    return auditResult.Errors;
                }

                addedIds.Add(grantId);
            }

            return Result.Success;
        });

        if (result.IsError)
        {
            return result.Errors;
        }

        return new SaveRoleDeclarationResult(resolvedRoleId, newRoleVersionId, reattached, addedIds, revokedIds);
    }

    // Second use-case (brief 060): see IRoleDeclarationService.CloseRoleDeclarationAsync's doc comment
    // for the branch boundary and the server-derived-branch design (the caller never picks "close" vs
    // "cancel" itself).
    //
    // Corrected (was: "No audit_log row here ... the journal stays complete without a new audit
    // row" — FALSE for the no-adjacent-predecessor cancel case): RoleRepository.CancelVersionCoreAsync's
    // no-predecessor path only flips isactive=0/cancelled=1 on the EXISTING row, never touching
    // recorded_by — so cancelling a brand-new role's sole future plan left ZERO record of WHO cancelled
    // it (the cancelled row still shows the CREATOR). Both branches below route through ONE
    // CompositeWrite (same shape as SaveRoleDeclarationAsync above) so the version write and every
    // audit_log row commit or roll back together — an audit-write failure fails the whole composite,
    // never swallowed.
    //
    // B3b (slice 2, Task 3, 2026-08-15): a role stop is ONE gesture, not one row. It mints ONE operation
    // id (same convention as SaveRoleDeclarationAsync) and writes ONE parent "role-close" event plus ONE
    // "permission-change" child event per P11 auto-cut outcome the engine reported on `UpsertResult`,
    // all at target `role:{roleId}` (never `role_version:{id}` — that was the pre-B3b target and is now
    // read only on historical rows), all sharing the one operation id in `detail`. The service reads the
    // engine's report (Shrunk/Cancelled) instead of re-deriving each grant's fate from dates — the
    // coverage arithmetic stays in the one place that already owns it (VersionedRepository).
    public async Task<ErrorOr<UpsertResult>> CloseRoleDeclarationAsync(CloseRoleDeclarationRequest request)
    {
        var username = currentUser.Username ?? "unknown";
        var operationDate = OperationDate.Capture(dates);
        var today = operationDate.Value;
        var operationId = Guid.NewGuid().ToString("N");

        // [1] P7 — before any lock/transaction opens (mirrors SaveRoleDeclarationAsync's own opening
        // sequence above).
        var authz = await authorization.AuthorizeAsync(username, FunctionKey);
        if (authz.IsError)
        {
            return authz.Errors;
        }

        // Role is a system-wide (Global-only) entity — same rejection as Save's own scope check.
        if (authz.Value.Level != ScopeLevel.Global)
        {
            return Error.Forbidden(
                "Authz.ScopeInsufficient",
                $"Role close requires Global scope; actor '{username}' is authorized only at {authz.Value.Level}.");
        }

        // [2] Read the target version server-side — the caller's claim about which version this is is
        // never trusted. GetHistoryAsync returns every version ever recorded for the identity (active,
        // inactive, cancelled alike). Restrict the resolve to a version that is CURRENTLY still in force
        // as a version row — re-submitting an id for an already-cancelled or already-superseded version
        // must fail with THIS service's own Role.VersionNotFound, not fall through to the branch logic
        // below and surface a different code from deep inside the engine (VersionedRepository.VersionNotFound,
        // which also leaks the physical table name).
        // `IsActive` alone is the "still in force" definition here — `cancelled = 1` is
        // only ever set together with `isactive = 0` (RoleRepository never cancels an active row), so a
        // separate `!v.Cancelled` conjunct would be redundant, not an independent condition.
        var history = await roleRepository.GetHistoryAsync(request.RoleId);
        var version = history.FirstOrDefault(v => v.Id == request.VersionId && v.IsActive);
        if (version is null)
        {
            return Error.NotFound(
                "Role.VersionNotFound",
                $"Role version {request.VersionId} was not found for role {request.RoleId}.");
        }

        // [3] Admin-flag break-glass gate (root-caused during design review, not present on
        // Close before this feature): closing/retiring an admin-flagged role removes admin coverage from
        // the system, the same effect as clearing the flag via Edit — Close must not be a side-door
        // around that gate. Verbatim error/code, matching RoleRepository.cs's own UpsertAsync gate.
        if (version.IsAdminRole && !breakGlass.IsBreakGlassAdmin(username))
        {
            return Error.Forbidden(
                "Role.AdminFlagChangeNotAuthorized",
                "Changing the admin-flag on a role requires an explicit authority check by the caller.");
        }

        // [4] Server derives the branch and validates the close/cancel date — delegated to the
        // entity-agnostic VersionCloseRules (AST.Core.EffectivePeriod), which is the single home of
        // this evaluation order (docs/shared-components.md §⑥). Do not re-implement or reorder these
        // guards here.
        // TASK 0 (2026-08-11): `today` is captured ONCE here and threaded
        // unchanged into the cancel branch below (RoleRepository.CancelPlanAsync's `operationDate`) —
        // design-effective-period.md §3 requires a single business operation to capture "today" once and
        // use it consistently for every parameter; the engine no longer re-reads its own
        // IBusinessDateProvider for the cancel-eligibility guard, so the two can no longer disagree
        // across a midnight rollover the way they used to.
        var targetPeriod = new EffectivePeriod(version.EffectiveFrom, version.EffectiveTo);
        DateOnly? derivedCloseDate = VersionCloseRules.BranchFor(today, targetPeriod) == VersionCloseBranch.CancelPlan
            ? null
            : today.AddDays(-1);
        var closeValidation = VersionCloseRules.Validate(today, targetPeriod, derivedCloseDate);
        if (closeValidation.IsError)
        {
            return closeValidation.Errors;
        }

        var isCancelPlanBranch = closeValidation.Value == VersionCloseBranch.CancelPlan;

        // [5] ONE CompositeWrite for the version write + the audit_log row — same shape as
        // SaveRoleDeclarationAsync above. AdminFlagLockKey is Enlisted UNCONDITIONALLY,
        // mirroring SaveRoleDeclarationAsync: the cancel branch's predecessor-restore path
        // (RoleRepository.CancelPlanAsync -> InsertRemnantAsync) can insert a NEW active version with
        // is_admin_role=1 copied verbatim from the restored predecessor, so it must hold the same
        // singleton-admin-flag lock as any other path that can create an active admin-flag version —
        // unconditional to avoid a TOCTOU between "does this restore an admin predecessor" and "did I
        // lock it" (rare admin-only op, ~30 users; see RoleRepository.AdminFlagLockKey's own comment).
        // Pre-probe currently-active grants owned by this role and Enlist every one up front
        // (VersionedRepository.EnsureCompositeDependentsEnlistedAsync requires every exclusively-owned
        // dependent P11 might auto-cut to already be Enlisted before ExecuteAsync — a grant opened AFTER
        // this probe is a grow-only TOCTOU, caught fail-clear by that same guard as
        // CompositeWrite.DependentNotEnlisted, never silently missed).
        var activeGrants = await rolePermissionRepository.GetActiveGrantsForPeriodAsync(
            request.RoleId, new EffectivePeriod(DateOnly.MinValue, EffectivePeriod.OpenEnd));

        var composite = new CompositeWrite(connections)
            .Enlist(roleRepository, request.RoleId)
            .Enlist(RoleRepository.AdminFlagLockKey);
        foreach (var grant in activeGrants)
        {
            composite = composite.Enlist(rolePermissionRepository, grant.RolePermissionId);
        }

        UpsertResult? upsertResult = null;

        var writeResult = await composite.ExecuteAsync(async context =>
        {
            var write = isCancelPlanBranch
                ? await roleRepository.CancelPlanAsync(
                    context, request.RoleId, request.VersionId, today, breakGlass.IsBreakGlassAdmin(username),
                    username, request.Note)
                : await roleRepository.CloseVersionAsync(
                    context, request.RoleId, request.VersionId, derivedCloseDate!.Value, operationDate,
                    username, request.Note);
            if (write.IsError)
            {
                return write.Errors;
            }

            upsertResult = write.Value;

            var auditResult = await auditLog.WriteAsync(
                new AuditLogEntry(
                    "role-close",
                    username,
                    // B3b: the target is the ROLE, not the version — the journal is opened per role,
                    // and the precise object already lives in `detail`. An audit row is never
                    // rewritten, but no reader carries a branch for the older `role_version:{id}`
                    // shape: AST had not been released when this shape shipped, so no database can
                    // hold one (amended 2026-08-15).
                    $"role:{request.RoleId}",
                    BuildRoleCloseDetailJson(
                        operationId, request.RoleId, request.VersionId,
                        isCancelPlanBranch ? "cancel" : "close",
                        isCancelPlanBranch ? null : derivedCloseDate, request.Note)),
                context.Transaction);
            if (auditResult.IsError)
            {
                return auditResult.Errors;
            }

            // B3b closes F11 from the other side: the remnant row now says WHAT it was (Task 2's
            // operation_kind), and this says WHY — which role stop caused it, in the same gesture. The
            // engine reported the outcomes; this method never re-derives coverage.
            // Filter by the dependent TABLE, not just by identity id. Role declares
            // exactly one exclusively-owned edge today, so every outcome is a role-permission outcome —
            // but the id spaces of two different dependent tables are unrelated, so a second edge would
            // make an unfiltered loop match a foreign identity against `activeGrants` and journal a
            // grant that was never touched. Filtering here costs nothing and removes the assumption.
            foreach (var outcome in write.Value.AutoCutOutcomes.Where(
                o => o.DependentVersionTable == RolePermissionVersionTable))
            {
                // activeGrants is the pre-probe already taken BEFORE the composite, so the child event
                // names the function and scope with no extra read inside the transaction.
                // EnsureCompositeDependentsEnlistedAsync already fails CompositeWrite.DependentNotEnlisted
                // for a grant the pre-probe never saw, so this should never be null — but a throw inside
                // the composite is the wrong failure shape, so this is a clear failure, not an assumption.
                var grant = activeGrants.FirstOrDefault(g => g.RolePermissionId == outcome.DependentIdentityId);
                if (grant is null)
                {
                    return Error.Failure(
                        "Role.CascadeGrantNotProbed",
                        $"Auto-cut reported an outcome for role_permission {outcome.DependentIdentityId}, which the pre-write probe never saw.");
                }

                // The two Action values sit in the same bare-verb vocabulary slice 1 established
                // ("add"/"edit" on role-change; "grant"/"revoke"/"cancel" on permission-change):
                //   "cut"    — the grant was in force and the role's stop shortened it. `To` is the new
                //              end, exactly as the save path reports a partial revoke.
                //   "cancel" — the grant never had an effective day and went with the role. `To` is its
                //              original end, because no period survives.
                var childResult = await auditLog.WriteAsync(
                    new AuditLogEntry(
                        "permission-change",
                        username,
                        $"role:{request.RoleId}",
                        BuildDetailJson(
                            outcome.Action == AutoCutAction.Cancelled ? "cancel" : "cut",
                            operationId, request.RoleId, outcome.DependentIdentityId,
                            grant.FunctionId, grant.ScopeLevel,
                            outcome.EffectiveFrom, outcome.CutTo ?? outcome.EffectiveTo,
                            request.Note)),
                    context.Transaction);
                if (childResult.IsError)
                {
                    return childResult.Errors;
                }
            }

            return Result.Success;
        });

        return writeResult.IsError ? writeResult.Errors : upsertResult!;
    }

    // Answers ONE question, without holding any lock: is there a PRE-EXISTING role identity this save will
    // write? `PreExistingRoleId` is null exactly when a brand-new identity will be needed — and that one is
    // minted inside the transaction (§7), never here, so this method has nothing to undo and the caller has
    // nothing to compensate. A lock-free read can go stale between here and the transaction; every way it
    // can is re-checked by ReDecideIdentityAsync under the code lock.
    private async Task<ErrorOr<(long? PreExistingRoleId, bool Reattached)>> GuessIdentityAsync(
        RoleSaveTarget target, string roleCode, DateOnly today)
    {
        return target switch
        {
            RoleSaveTarget.NewRole => await GuessNewRoleIdentityAsync(roleCode, today),
            RoleSaveTarget.ExistingRole existing => await GuessExistingRoleIdentityAsync(existing, roleCode, today),
            _ => Error.Unexpected("Role.SaveTargetInvalid", "Unknown role save target."),
        };
    }

    private async Task<ErrorOr<(long? PreExistingRoleId, bool Reattached)>> GuessNewRoleIdentityAsync(
        string roleCode, DateOnly today)
    {
        var owners = await roleRepository.GetCodeOwnersAsync(roleCode, today);
        if (owners.Count > 1)
        {
            return Error.Validation(
                "Role.CodeIdentityAmbiguous",
                $"Mã vai trò '{roleCode}' thuộc về nhiều hơn một vai trò lịch sử; không thể xác định vai trò nào được dùng lại.");
        }

        if (owners.Count == 1)
        {
            var owner = owners[0];
            if (owner.HasVersionInForceToday)
            {
                return Error.Validation(
                    "Role.CodeInUse",
                    $"Mã vai trò '{roleCode}' đang được một vai trò hiệu lực sử dụng.");
            }

            if (owner.HasFutureVersion)
            {
                return Error.Validation(
                    "Role.CodeOwnerNotDormant",
                    $"Mã vai trò '{roleCode}' thuộc về một vai trò có phiên bản tương lai — dữ liệu không hợp lệ.");
            }

            return (owner.RoleId, true);
        }

        // No owner: a brand-new identity is needed. It is NOT minted here — see this method's comment.
        return (null, false);
    }

    private Task<ErrorOr<(long? PreExistingRoleId, bool Reattached)>> GuessExistingRoleIdentityAsync(
        RoleSaveTarget.ExistingRole target, string roleCode, DateOnly today) =>
        GuessExistingRoleFromOwnersAsync(target, roleCode, today);

    private async Task<ErrorOr<(long? PreExistingRoleId, bool Reattached)>> GuessExistingRoleFromOwnersAsync(
        RoleSaveTarget.ExistingRole target, string roleCode, DateOnly today)
    {
        var owners = await roleRepository.GetCodeOwnersAsync(roleCode, today);
        if (owners.Any(o => o.RoleId != target.RoleId))
        {
            return Error.Validation(
                "Role.CodeOwnedByAnotherIdentity",
                $"Mã vai trò '{roleCode}' đã thuộc về một vai trò khác.");
        }

        return (target.RoleId, false);
    }

    // Runs INSIDE the composite transaction, with the code lock already held, and RETURNS the identity the
    // write will use: the pre-existing one the caller Enlisted, or a brand-new one minted here.
    //
    // Every disagreement with the lock-free guess is an error, never a silent correction — in BOTH
    // directions. The guess is what decided which identity got a lock: if the truth under the lock names a
    // different identity, honouring it would mean writing an identity nobody locked, and re-attaching one
    // would need a lock that can no longer be taken (§7: never after the transaction opened). Minting is the
    // one case that needs no lock, but it is still refused when the guess expected to re-attach — the
    // operator's screen was built on ownership that has since changed, so they must reload.
    private async Task<ErrorOr<long>> ReDecideIdentityAsync(
        ICompositeWriteContext context,
        RoleSaveTarget target,
        string roleCode,
        string? normalizedExpectedRoleCode,
        DateOnly today,
        long? preExistingRoleId,
        bool guessedReattached)
    {
        var owners = await roleRepository.GetCodeOwnersAsync(context, roleCode, today);

        return target switch
        {
            RoleSaveTarget.NewRole => await ResolveNewRoleIdentityAsync(
                context, owners, preExistingRoleId, guessedReattached),
            RoleSaveTarget.ExistingRole existing => await ValidateExistingRoleReDecisionAsync(
                context, existing, roleCode, normalizedExpectedRoleCode!, today, owners),
            _ => Error.Unexpected("Role.SaveTargetInvalid", "Unknown role save target."),
        };
    }

    private async Task<ErrorOr<long>> ResolveNewRoleIdentityAsync(
        ICompositeWriteContext context,
        IReadOnlyList<RoleRepository.RoleCodeOwner> owners,
        long? preExistingRoleId,
        bool guessedReattached)
    {
        if (owners.Count > 1)
        {
            return OwnershipChangedError();
        }

        if (owners.Count == 1)
        {
            var owner = owners[0];
            if (owner.HasVersionInForceToday || owner.HasFutureVersion)
            {
                return OwnershipChangedError();
            }

            if (!guessedReattached || owner.RoleId != preExistingRoleId)
            {
                return OwnershipChangedError();
            }

            return owner.RoleId;
        }

        // The guess re-attached to a dormant owner that no longer owns the code. Its lock is the only one
        // held, so minting a different identity here would write outside everything the composite locked.
        if (guessedReattached)
        {
            return OwnershipChangedError();
        }

        return await roleRepository.CreateIdentityAsync(context);
    }

    private async Task<ErrorOr<long>> ValidateExistingRoleReDecisionAsync(
        ICompositeWriteContext context,
        RoleSaveTarget.ExistingRole existing,
        string roleCode,
        string normalizedExpectedRoleCode,
        DateOnly today,
        IReadOnlyList<RoleRepository.RoleCodeOwner> owners)
    {
        if (owners.Any(o => o.RoleId != existing.RoleId))
        {
            return Error.Validation(
                "Role.CodeOwnedByAnotherIdentity",
                $"Mã vai trò '{roleCode}' đã thuộc về một vai trò khác.");
        }

        var currentVersion = await roleRepository.GetByIdentityAsync(context, existing.RoleId, today);
        if (currentVersion.IsError)
        {
            if (currentVersion.Errors.Any(e => e.Code == "EffectivePeriod.NoCoverage"))
            {
                return Error.Conflict(
                    "Role.VersionOutOfDate",
                    $"Vai trò {existing.RoleId} đã thay đổi kể từ lúc mở (không còn hiệu lực hôm nay). "
                    + "Hãy tải lại và thao tác lại.");
            }

            return currentVersion.Errors;
        }

        if (!string.Equals(
                currentVersion.Value.RoleCode.Trim(),
                normalizedExpectedRoleCode,
                StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "Role.ExpectedCodeMismatch",
                $"Mã vai trò đã tải '{normalizedExpectedRoleCode}' không khớp với mã hiện tại '{currentVersion.Value.RoleCode}'. Hãy tải lại.");
        }

        if (currentVersion.Value.Id != existing.ExpectedRoleVersionId)
        {
            return Error.Conflict(
                "Role.VersionOutOfDate",
                $"Vai trò {existing.RoleId} đã được người khác thay đổi (bản đang mở {existing.ExpectedRoleVersionId}, "
                + $"bản hiện tại {currentVersion.Value.Id}). Hãy tải lại và thao tác lại.");
        }

        return existing.RoleId;
    }

    private static Error OwnershipChangedError() =>
        Error.Conflict(
            "Role.CodeOwnershipChanged",
            "Một khai báo khác cho cùng mã vai trò đã được lưu trong lúc bạn chuẩn bị; hãy tải lại và thử lại.");

    private static string BuildDetailJson(
        string action, string operationId, long roleId, long rolePermissionId, long functionId, ScopeLevel scopeLevel,
        DateOnly from, DateOnly to, string? note) =>
        JsonSerializer.Serialize(
            new AuditDetail(action, operationId, roleId, rolePermissionId, functionId, scopeLevel.ToString(), from, to, note),
            DetailJsonOptions);

    // O2 as amended 2026-08-14 (spec §5 B3a): an ordinary role Add/Edit writes ONE audit row.
    private static string BuildRoleSaveDetailJson(
        string action, string operationId, long roleId, long roleVersionId, string roleCode, string roleName,
        bool isAdminRole, string? note) =>
        JsonSerializer.Serialize(
            new RoleSaveAuditDetail(action, operationId, roleId, roleVersionId, roleCode, roleName, isAdminRole, note),
            DetailJsonOptions);

    private sealed record RoleSaveAuditDetail(
        string Action, string OperationId, long RoleId, long RoleVersionId, string RoleCode, string RoleName,
        bool IsAdminRole, string? Note);

    private sealed record AuditDetail(
        string Action,
        string OperationId,
        long RoleId,
        long RolePermissionId,
        long FunctionId,
        string ScopeLevel,
        DateOnly From,
        DateOnly To,
        string? Note);

    // Extended B3b (2026-08-15): detail shape for CloseRoleDeclarationAsync's "role-close" audit
    // row — deliberately separate from AuditDetail above (Save's per-grant shape), since Close/Cancel
    // carries a different field set (no FunctionId/ScopeLevel; EffectiveThrough only applies to the
    // close branch). `Action` is a bare verb ("close" | "cancel"), matching the vocabulary
    // RoleSaveAuditDetail/AuditDetail already use ("add"/"edit"; "grant"/"revoke"/"cancel") — the noun
    // lives in `event_type` ("role-close"). Field order mirrors RoleSaveAuditDetail: Action, OperationId,
    // then the payload.
    private static string BuildRoleCloseDetailJson(
        string operationId, long roleId, long versionId, string branch, DateOnly? effectiveThrough, string? note) =>
        JsonSerializer.Serialize(
            new RoleCloseAuditDetail(branch, operationId, roleId, versionId, effectiveThrough, note),
            DetailJsonOptions);

    private sealed record RoleCloseAuditDetail(
        string Action,
        string OperationId,
        long RoleId,
        long RoleVersionId,
        DateOnly? EffectiveThrough,
        string? Note);
}
