using AST.Core.Data;
using AST.Infrastructure;
using AST.Core.EffectivePeriod;
using AST.Core.Iam.Repositories;
using AST.Core.Startup;
using AST.Core.Time;
using AST.Modules.IAM.Data;
using AST.Modules.IAM.Data.Repositories;
using Prism.Ioc;
using Prism.Modularity;

namespace AST.Modules.IAM;

// Registers DI for the IAM module (rule-module-boundary §1b: the module registers itself, the Shell does not
// know the specific list ahead of time). Discovered + loaded by the Shell's DirectoryModuleCatalog scanning
// Modules/ at runtime (Task X, `AST/App.xaml.cs`) -- the exe compiles WITHOUT a reference to this assembly
// (AST.csproj's ProjectReference has ReferenceOutputAssembly="false", used only to build this project + stage
// its output into Modules/, see AST.csproj's CopyIamModule* targets).
//
// [B2 note] Does NOT register IConnectionStringProvider / IClock / IBusinessDateProvider here --
// that is the composition root's (Shell's) responsibility, since the connection-string source/system clock is a
// deployment decision, not something the business module owns (rule-module-boundary). Missing registration of
// these interfaces at the Shell will make DryIoc fail to resolve at runtime -- this is INTENTIONAL behavior.
//
// No [Module(ModuleName=...)] attribute: that type lives in Prism.Wpf (windows-only), and this project is
// deliberately headless (net10.0, only references Prism.Core) so it stays unit-testable without a WPF/STA
// runtime. DirectoryModuleCatalog discovers IModule-implementing types by reflection and defaults ModuleName
// to the type name when the attribute is absent -- no wiring is lost.
public sealed class IamModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<IDbConnectionFactory, MySqlConnectionFactory>();
        containerRegistry.RegisterSingleton<IStandardScopeFilterBuilder, StandardScopeFilterBuilder>();
        containerRegistry.RegisterSingleton<IEffectivePeriodResolver, EffectivePeriodResolver>();
        containerRegistry.RegisterSingleton<IPeriodEditor, PeriodEditor>();
        // IAM owns its temporal-FK edges and hands them to the SharedKernel registry (rule-module-boundary §1).
        // A factory, NOT RegisterSingleton<ITemporalFkRegistry, TemporalFkRegistry>(): auto-wiring would resolve
        // IEnumerable<TemporalFkEdge> to an EMPTY collection, i.e. a registry that validates nothing. The
        // TemporalFkRegistry ctor now rejects that outright, so such a slip fails loudly instead of silently.
        containerRegistry.RegisterSingleton<ITemporalFkRegistry>(() => IamTemporalFkEdges.CreateRegistry());
        containerRegistry.RegisterSingleton<IParentCoverageProvider, DbParentCoverageProvider>();
        containerRegistry.RegisterSingleton<IDependentCoverageProvider, DbDependentCoverageProvider>();
        containerRegistry.RegisterSingleton<ITemporalFkValidator, TemporalFkValidator>();
        containerRegistry.RegisterSingleton<AST.Core.Iam.IIntegrityCheckService, Data.IntegrityCheckService>();

        containerRegistry.RegisterSingleton<ISchemaVersionChecker, Data.SchemaVersionChecker>();

        containerRegistry.Register<IOrgUnitRepository, OrgUnitRepository>();
        containerRegistry.Register<IRoleRepository, RoleRepository>();
        containerRegistry.Register<IUserRepository, UserRepository>();
        containerRegistry.Register<IFunctionRepository, FunctionRepository>();
        containerRegistry.Register<IRolePermissionRepository, RolePermissionRepository>();

        // Concrete registrations (add-only, alongside the interface registrations above): RoleDeclarationService
        // (Seam-2 orchestration, OPEN-B2) needs the CONCRETE RoleRepository/RolePermissionRepository classes for
        // their ICompositeWriteContext overloads (deliberately not on the public interfaces), and the concrete
        // FunctionRepository to Enlist each grant-to-add's function_id (a role_permission temporal-FK parent).
        containerRegistry.Register<RoleRepository>();
        containerRegistry.Register<RolePermissionRepository>();
        containerRegistry.Register<FunctionRepository>();

        // OrgUnitDeclarationService (Close/Cancel orchestration) needs the CONCRETE OrgUnitRepository class
        // for its composite-write overloads (CancelPlanAsync / (new) CloseVersionAsync taking
        // ICompositeWriteContext), deliberately not on the public IOrgUnitRepository interface -- same
        // posture as RoleRepository above.
        containerRegistry.Register<OrgUnitRepository>();

        // [C2] Syncs the function catalog from code. IFunctionRegistry/IBusinessDateProvider are registered by
        // the composition root (Shell) (cross-cutting) -- see the B2 note above.
        containerRegistry.Register<AST.Core.Iam.IFunctionCatalogSync, FunctionCatalogSyncService>();

        // [C3] Permission resolution (pure read). The §⑤ break-glass implementation reads File B + verifies the
        // digital signature (RealBreakGlassPolicy); there is no default — resolving an unregistered IBreakGlassPolicy
        // fails clearly (DI error) rather than silently allowing or denying. See IBreakGlassPolicy.cs.
        // [§⑤ #2] IBreakGlassPolicy is NO LONGER registered here: Prism loads modules AFTER the app, so a registration
        // at the module level would override the composition root. The composition root (Shell) is the ONLY place that registers
        // IBreakGlassPolicy (RealBreakGlassPolicy).
        containerRegistry.Register<AST.Core.Iam.IAuthorizationService, AuthorizationService>();

        // [Seam-2 orchestration] OPEN-B2: composes role-edit + grant revoke + grant add in one Save. Depends
        // on IAuditLogWriter/IBreakGlassPolicy/ICurrentWindowsUser, all registered by the composition root
        // (Shell) BEFORE modules load (see the B2 note above) — never registered here.
        containerRegistry.Register<AST.Core.Iam.IRoleDeclarationService, RoleDeclarationService>();

        // Closes the org-unit close/cancel gap (AST.Core/Iam/IOrgUnitDeclarationService.cs) -- same
        // cross-cutting dependency posture as IRoleDeclarationService above (IAuditLogWriter/
        // ICurrentWindowsUser/IBusinessDateProvider are registered by the composition root, never here).
        containerRegistry.Register<AST.Core.Iam.IOrgUnitDeclarationService, OrgUnitDeclarationService>();
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
