namespace AST.Core.Security;

// "Obfuscating" encryption of File A's content (spec §2). NOT real protection (real protection is on the DB side, §⑤).
[SharedComponent]
public interface IConfigProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext); // throws if the ciphertext is corrupt
}
