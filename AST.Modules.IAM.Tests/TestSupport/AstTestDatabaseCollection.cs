namespace AST.Modules.IAM.Tests.TestSupport;

// Every test class DROPs+reapplies the `ast_test` schema in InitializeAsync (isolated per class, following
// the B1 SchemaBootstrapSmokeTests pattern). xUnit by default runs DIFFERENT test classes in parallel
// (each class is an implicit collection) -> 2 classes DROPping/migrating at the same time on the SAME real DB would
// collide. Group every test class touching the real DB into 1 collection so xUnit runs them SEQUENTIALLY.
public sealed class AstTestDatabaseFixture
{
}

[CollectionDefinition(Name)]
public sealed class AstTestDatabaseCollection : ICollectionFixture<AstTestDatabaseFixture>
{
    public const string Name = "ast_test database (serialized)";
}
