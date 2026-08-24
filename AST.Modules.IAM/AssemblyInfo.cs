using System.Runtime.CompilerServices;

// Allows the integration test project (B2) to directly instantiate `internal` Repository/Entity types to
// connect to a real DB -- does NOT widen access for any other module/assembly (rule-module-boundary still
// holds: Entity/impl stays `internal`, only the Interface+DTO in AST.Core.Iam.Repositories are public).
[assembly: InternalsVisibleTo("AST.Modules.IAM.Tests")]
