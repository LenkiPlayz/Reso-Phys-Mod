using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoftbodyManager;

/// <summary>
/// 1.1.0: the signed file every GitHub release carries (update.json): the release's manifest, as text, and Lenki's signature of
/// that text (ECDSA P-256 with SHA-256). The manager has Lenki's public key built in and takes nothing from a release whose
/// signature does not check out, so someone who got into the GitHub account still could not send code to friends. The
/// private key stays on Lenki's PC (publisher\ makes it and signs with it).
/// </summary>
sealed class SignedUpdate
{
    /// <summary>The release's manifest.json, exactly the text that was signed.</summary>
    public string Manifest { get; set; }
    /// <summary>Base64 of the signature (IEEE P1363: r then s).</summary>
    public string Signature { get; set; }

    public const string FileName = "update.json";

    public static SignedUpdate Sign(string manifestText, ECDsa privateKey) => new()
    {
        Manifest = manifestText,
        Signature = Convert.ToBase64String(privateKey.SignData(Encoding.UTF8.GetBytes(manifestText), HashAlgorithmName.SHA256)),
    };

    /// <summary>Whether the manifest was signed with the private half of <paramref name="publicKey"/> (base64 of a P-256
    /// SubjectPublicKeyInfo, as update-key.pub.txt holds it).</summary>
    public bool IsSignedBy(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || Manifest == null || string.IsNullOrEmpty(Signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyData(Encoding.UTF8.GetBytes(Manifest), Convert.FromBase64String(Signature), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static SignedUpdate FromJson(string json) => JsonSerializer.Deserialize<SignedUpdate>(json);

    /// <summary>A new P-256 key pair: the private key as PKCS#8 PEM, the public key as base64 SubjectPublicKeyInfo.</summary>
    public static (string privatePem, string publicKey) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>The public key of a private key (PEM).</summary>
    public static string PublicKeyOf(string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}

/// <summary>1.1.0: what the manager was built with: where updates come from and the key their signature must match (both
/// embedded from update-repo.txt and update-key.pub.txt when the manager is published; without them it has no updates).</summary>
static class BuiltIn
{
    public static string UpdateRepo => Resource("update-repo.txt");
    public static string UpdateKey => Resource("update-key.pub.txt");

    static string Resource(string name)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) return null;
            var text = new StreamReader(s).ReadToEnd().Trim();
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }
}
