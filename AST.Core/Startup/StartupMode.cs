namespace AST.Core.Startup;

public enum StartupMode { Connected, NotConnected }

// Normalized File A read result (Shell maps from ErrorOr into this so the resolver stays pure Core).
public enum FileAOutcome { Ok, NotDeclared, Corrupt, IoError }
