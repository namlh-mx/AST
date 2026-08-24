namespace AST.Core.Startup;

// Reason = English constant code for code comparison; Message = Vietnamese sentence for the user (spec §4).
public sealed record StartupStatus(StartupMode Mode, string Reason, string Message);
