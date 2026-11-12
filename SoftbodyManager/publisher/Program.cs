using System.Security.Cryptography;
using System.Text.Json;
using SoftbodyManager;

// 1.1.0: Lenki's side of the manager's updates. Makes the signing key once, and signs each release's manifest
// (make_friend_pack.py runs "sign"; publish_release.py runs "verify").
//
//   dotnet run --project publisher -- keygen
//       the key pair: the private key in %APPDATA%\SoftbodyPublisher\update-signing-key.pem (it never leaves this PC: back it
//       up somewhere safe), the public key in SoftbodyManager\update-key.pub.txt (built into the manager)
//   dotnet run --project publisher -- sign <manifest.json> <update.json>
//   dotnet run --project publisher -- verify <update.json>
//
// --key <file> and --public <file> use other files (the tests do).

var rest = new List<string>();
string keyFile = null, pubFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--key" && i + 1 < args.Length) keyFile = args[++i];
    else if (args[i] == "--public" && i + 1 < args.Length) pubFile = args[++i];
    else rest.Add(args[i]);
}
keyFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftbodyPublisher", "update-signing-key.pem");
pubFile ??= Path.Combine(ManagerDir(), "update-key.pub.txt");

switch (rest.FirstOrDefault())
{
    case "keygen" when rest.Count == 1: return KeyGen();
    case "sign" when rest.Count == 3: return Sign(rest[1], rest[2]);
    case "verify" when rest.Count == 2: return Verify(rest[1]);
    default:
        Console.Error.WriteLine("usage: keygen | sign <manifest.json> <update.json> | verify <update.json>   [--key <private.pem>] [--public <update-key.pub.txt>]");
        return 2;
}

int KeyGen()
{
    if (File.Exists(keyFile))
    {
        Console.Error.WriteLine($"There is a signing key already: {keyFile}. Keeping it (a new key would make every manager out there refuse your updates).");
        return 1;
    }
    if (File.Exists(pubFile))
    {
        Console.Error.WriteLine($"{pubFile} exists, but its private key is not at {keyFile}. If you moved the key, pass --key <file>. " +
                                "If it is lost, friends need a new pack once: move update-key.pub.txt away and run keygen again.");
        return 1;
    }
    var (privatePem, publicKey) = SignedUpdate.NewKeyPair();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFile)));
    File.WriteAllText(keyFile, privatePem);
    File.WriteAllText(pubFile, publicKey + Environment.NewLine);
    Console.WriteLine("Signing key made.");
    Console.WriteLine($"  private (secret; back it up somewhere safe, it never goes on GitHub): {keyFile}");
    Console.WriteLine($"  public (built into the manager): {pubFile}");
    return 0;
}

int Sign(string manifestFile, string outFile)
{
    if (!File.Exists(keyFile)) { Console.Error.WriteLine($"No signing key at {keyFile}: run keygen once."); return 1; }
    if (!File.Exists(pubFile)) { Console.Error.WriteLine($"No public key at {pubFile}: the manager would not know it."); return 1; }
    var pem = File.ReadAllText(keyFile);
    var pub = File.ReadAllText(pubFile).Trim();
    if (SignedUpdate.PublicKeyOf(pem) != pub)
    {
        Console.Error.WriteLine($"The signing key ({keyFile}) does not belong to {pubFile}: managers built with that public key would refuse this release.");
        return 1;
    }
    var text = File.ReadAllText(manifestFile);
    try { using var _ = JsonDocument.Parse(text); }
    catch (JsonException ex) { Console.Error.WriteLine($"{manifestFile} is not JSON: {ex.Message}"); return 1; }
    using var key = ECDsa.Create();
    key.ImportFromPem(pem);
    var signed = SignedUpdate.Sign(text, key);
    if (!signed.IsSignedBy(pub)) { Console.Error.WriteLine("The signature does not check out against the public key."); return 1; }
    File.WriteAllText(outFile, signed.ToJson());
    Console.WriteLine($"signed: {outFile}");
    return 0;
}

int Verify(string file)
{
    if (!File.Exists(pubFile)) { Console.Error.WriteLine($"No public key at {pubFile}."); return 1; }
    SignedUpdate signed;
    try { signed = SignedUpdate.FromJson(File.ReadAllText(file)); }
    catch (Exception ex) { Console.Error.WriteLine($"{file} is not readable: {ex.Message}"); return 1; }
    bool ok = signed != null && signed.IsSignedBy(File.ReadAllText(pubFile));
    Console.WriteLine(ok ? "signature OK" : "signature WRONG: managers would refuse this release");
    return ok ? 0 : 1;
}

// the folder with SoftbodyManager.csproj (this tool sits in its publisher\ folder)
static string ManagerDir()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SoftbodyManager.csproj"))) return d.FullName;
    return Environment.CurrentDirectory;
}
