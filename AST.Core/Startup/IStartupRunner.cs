namespace AST.Core.Startup;

// Re-runs the startup chain (read File A -> test DB -> check schema) and updates IStartupState.
// Impl lives at the composition root (Shell) because it needs the RESOLVED ISchemaVersionChecker
// IMPLEMENTATION (only available after Prism loads modules) + DI; reuses StartupOrchestrator
// (spec §9, rule-prefer-existing — do not duplicate the resolve logic).
[SharedComponent]
public interface IStartupRunner
{
    StartupStatus Rerun();
}
