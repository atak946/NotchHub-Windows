// Guncelleme dosyasini imzalar: dotnet run --file tools/sign.cs -- <Notch.exe> [anahtar.pem]
// Anahtar yoksa olusturulur. Ozel anahtar depoya KOYULMAZ.
#:property PublishAot=false
using System.Security.Cryptography;

if (args.Length < 1) { Console.Error.WriteLine("Kullanim: sign.cs <dosya> [anahtar.pem]"); return 1; }
var file = Path.GetFullPath(args[0]);
var keyPath = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".notch", "update-signing-key.pem");

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
if (File.Exists(keyPath)) key.ImportFromPem(File.ReadAllText(keyPath));
else
{
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine($"Yeni imzalama anahtari olusturuldu: {keyPath}");
}

var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
Console.WriteLine($"PUBLIC {publicKey}");

var installer = Path.Combine("Services", "Installer.cs");
if (File.Exists(installer) && !File.ReadAllText(installer).Contains(publicKey))
{
    Console.Error.WriteLine("Bu anahtar Services/Installer.cs icindeki UpdatePublicKey ile eslesmiyor; uygulama imzayi kabul etmez.");
    return 2;
}

using (var stream = File.OpenRead(file))
    File.WriteAllBytes(file + ".sig", key.SignData(stream, HashAlgorithmName.SHA256));
Console.WriteLine($"Imzalandi: {file}.sig");
return 0;
