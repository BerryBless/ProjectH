using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Net;

// Review fix B1: the server's RSA-2048 private key, which decrypts each connection's session key (connect request blob,
// RSA-OAEP-SHA1). Keys are RSA.ToXmlString text (the Unity client's Mono has no PEM import; the option names say Pem for
// the deployment convention). Where the key comes from, first found wins:
//   1. Server:PrivateKeyPem (the environment variable Server__PrivateKeyPem): the XML itself.
//   2. Server:PrivateKeyPath: a file holding it.
//   3. keys/dev-server-key.xml next to the server (the committed development key, like the development database password).
// The development key (also any key whose public half is DevServerPublicKey.Xml) is refused in Production.
// Lifetime: one per GameLoop, owned by it (NetworkListener uses Rsa on LiteNetLib's receive thread only), disposed with it.
public sealed class ServerIdentity : IDisposable
{
    public const string DevKeyRelativePath = "keys/dev-server-key.xml";

    // 기능: 키를 감싼다.
    // 입력: rsa - 개인키, isDevKey - 개발용 키인지, fingerprint - 공개키 지문, source - 어디서 읽었는지(로그).
    // 출력: ServerIdentity.
    private ServerIdentity(RSA rsa, bool isDevKey, string fingerprint, string source)
    {
        Rsa = rsa;
        IsDevKey = isDevKey;
        Fingerprint = fingerprint;
        Source = source;
    }

    public RSA Rsa { get; }
    public bool IsDevKey { get; }
    // SHA-256 of the public key XML, first 8 bytes as hex: logged at startup so a deployment can tell which key it runs.
    public string Fingerprint { get; }
    public string Source { get; }

    // 기능: 설정에서 서버 키를 읽는다(PrivateKeyPem → PrivateKeyPath → 개발용 키 파일). Production에서 개발용 키면 거부한다.
    // 입력: options - 서버 설정, isProduction - Production 환경인지, baseDirectory - 개발용 키를 찾을 폴더(서버 실행 폴더).
    // 출력: 읽은 ServerIdentity. 키가 없거나 잘못됐거나 2048비트가 아니거나 Production + 개발용 키면 InvalidOperationException.
    public static ServerIdentity Load(ServerOptions options, bool isProduction, string baseDirectory)
    {
        string xml;
        string source;
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            xml = options.PrivateKeyPem;
            source = "Server:PrivateKeyPem";
        }
        else if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            if (!File.Exists(options.PrivateKeyPath)) throw new InvalidOperationException($"Server:PrivateKeyPath {options.PrivateKeyPath} does not exist.");
            xml = File.ReadAllText(options.PrivateKeyPath);
            source = options.PrivateKeyPath;
        }
        else
        {
            string path = Path.Combine(baseDirectory, DevKeyRelativePath);
            if (!File.Exists(path)) throw new InvalidOperationException($"No server key: set Server:PrivateKeyPem or Server:PrivateKeyPath ({path} is missing).");
            xml = File.ReadAllText(path);
            source = DevKeyRelativePath;
        }

        RSA rsa = RSA.Create();
        try
        {
            rsa.FromXmlString(xml);
        }
        catch (Exception ex)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"The server key from {source} is not an RSA XML private key.", ex);
        }
        if (rsa.KeySize != 2048)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"The server key from {source} must be RSA-2048 (the client's blob is {ProtocolLimits.RsaBlobBytes} bytes).");
        }
        string publicXml = rsa.ToXmlString(false);
        bool isDev = publicXml == DevServerPublicKey.Xml;
        if (isDev && isProduction)
        {
            rsa.Dispose();
            throw new InvalidOperationException("The development server key is refused in Production (the host's default environment): " +
                "set Server:PrivateKeyPem or Server:PrivateKeyPath, or start a development server with --environment Development " +
                "(DOTNET_ENVIRONMENT=Development; dotnet run sets it through Properties/launchSettings.json).");
        }
        return new ServerIdentity(rsa, isDev, FingerprintOf(publicXml), source);
    }

    // 기능: 공개키 XML의 지문(SHA-256 앞 8 B hex)을 구한다.
    // 입력: publicXml - RSA.ToXmlString(false).
    // 출력: 16자 hex 문자열.
    public static string FingerprintOf(string publicXml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicXml)), 0, 8).ToLowerInvariant();

    // 기능: 연결 요청의 blob을 복호해 세션 키를 꺼낸다(RSA-OAEP-SHA1). 수신 스레드 전용(연결 요청마다, 쿠키·빈도 검사 뒤).
    // 입력: blob - RsaBlobBytes 바이트.
    // 출력: 32 B 세션 키를 얻으면 true와 키, 복호 실패·길이가 다르면 false.
    public bool TryDecryptSessionKey(byte[] blob, out byte[] sessionKey)
    {
        sessionKey = Array.Empty<byte>();
        try
        {
            byte[] key = Rsa.Decrypt(blob, RSAEncryptionPadding.OaepSHA1);
            if (key.Length != ProtocolLimits.SessionKeyBytes) return false;
            sessionKey = key;
            return true;
        }
        catch (CryptographicException)
        {
            // A blob for another key or garbage: an expected refusal (BadRequest), not a server fault.
            return false;
        }
    }

    // 기능: 키를 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Dispose() => Rsa.Dispose();
}
