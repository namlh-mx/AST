using AST.Infrastructure.Security;

// IT tool run ONCE before a Release build (spec §7). Do NOT distribute to regular users.
// Generates an ECDSA P-256 key pair: prints the public key as base64 (paste into RootPublicKey.Value) + writes a passphrase-encrypted private key file.

Console.Write("Passphrase cho khóa bí mật root admin: ");
var passphrase = ReadSecret();
if (string.IsNullOrWhiteSpace(passphrase)) { Console.Error.WriteLine("Passphrase trống — hủy."); return 1; }

var outFile = args.Length > 0 ? args[0] : "root-private.key";
var (pub, priv) = EcdsaKeys.Generate(passphrase);
File.WriteAllBytes(outFile, priv);

Console.WriteLine();
Console.WriteLine($"Đã ghi khóa bí mật (PKCS#8 mã hóa) -> {Path.GetFullPath(outFile)}");
Console.WriteLine("Giữ file này offline/USB, KHÔNG để lên share.");
Console.WriteLine();
Console.WriteLine("Dán chuỗi sau vào RootPublicKey.Value (AST.Infrastructure/Security/RootPublicKey.cs) trước khi build Release:");
Console.WriteLine(pub);
return 0;

static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? ""; // pipe/non-interactive: reads a single line directly
    var s = new System.Text.StringBuilder();
    ConsoleKeyInfo k;
    while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (k.Key == ConsoleKey.Backspace) { if (s.Length > 0) s.Length--; }
        else if (!char.IsControl(k.KeyChar)) s.Append(k.KeyChar);
    }
    Console.WriteLine();
    return s.ToString();
}
