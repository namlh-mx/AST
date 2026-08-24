namespace AST.Shell.Services;

// A file the user picked: its display path + its raw bytes. The picker reads the bytes so ViewModels never
// touch the filesystem (keeps them headless-testable).
public sealed record PickedFile(string Path, byte[] Content);
