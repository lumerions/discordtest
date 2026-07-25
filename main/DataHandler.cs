using System;
using System.Text;
using System.Security.Cryptography;
using Isopoh.Cryptography.Argon2;

namespace Internal.Data;

public class DataHandler
{
    public (byte[] nonce, byte[] ciphertext, byte[] tag) Encrypt (string PlainText, byte[] EncryptKey)
    {
        byte [] tag = new byte[16];
        byte [] nonce = RandomNumberGenerator.GetBytes(12);
        var plainTextBytes = Encoding.UTF8.GetBytes(PlainText);
        byte [] ciphertext = new byte[plainTextBytes.Length];

        using (var aes = new AesGcm(EncryptKey))
        {
            aes.Encrypt(nonce, plainTextBytes, ciphertext, tag);
        };

        return (nonce, ciphertext, tag);
    } 

    public string Decrypt (byte[] ciphertext, byte[] nonce, byte[] tag, byte[] EncryptKey) 
    {
        var decrypted = new byte[ciphertext.Length];

        using (var aes = new AesGcm(EncryptKey))
        {
            aes.Decrypt(nonce, ciphertext, tag, decrypted);
        }

        return Encoding.UTF8.GetString(decrypted);
    }

    public string ArgonHash (string InputText)
    {
        return Argon2.Hash(InputText);
    }

    public bool VerifyArgonHash (string InputText, string Argon2Hash)
    {
        return Argon2.Verify(Argon2Hash, InputText);
    }

    public byte[] HmacSha256 (string Email, byte[] SecretKey)
    {
       using var hmac = new HMACSHA256(SecretKey);
       return hmac.ComputeHash(Encoding.UTF8.GetBytes(Email.Trim().ToLowerInvariant()));
    }
}