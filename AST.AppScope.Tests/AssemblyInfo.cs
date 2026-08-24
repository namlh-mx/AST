// The process Application is a per-process, thread-affine singleton and this project exists to observe
// it, so two test classes running concurrently are two readers of one global. The snapshot is an
// immutable record -- narrowed on ScopeSnapshot: the INTENT and THIS VERY PROPERTY, not a mechanism,
// since every collection is IReadOnlyList<T> over a List<T> that a same-assembly caller could cast
// back -- and Lazy(ExecutionAndPublication) builds it exactly once, so no race is known TODAY
// -- this removes the QUESTION rather than a measured defect, and it is written down that way so a
// later reader does not cite it as evidence of one. Verified 2026-08-20: the repository has no
// xunit.runner.json and no other CollectionBehavior attribute, so nothing else already decided this.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
