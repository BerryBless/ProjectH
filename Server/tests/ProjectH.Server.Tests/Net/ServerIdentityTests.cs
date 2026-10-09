using System;
using System.IO;
using System.Security.Cryptography;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Net;

// Review fix B1: where the server key comes from, and the development key refused in Production.
public class ServerIdentityTests
{
    // 기능: 새 RSA 키 쌍을 만들어 개인 키까지 담은 XML로 돌려준다.
    // 입력: bits - 키 길이.
    // 출력: 개인 키 XML 문자열.
    private static string NewKeyXml(int bits = 2048)
    {
        using var rsa = RSA.Create(bits);
        return rsa.ToXmlString(true);
    }

    [Fact]
    public void TheDevKey_LoadsByDefault_AndIsMarkedDev()
    {
        using var identity = ServerIdentity.Load(new ServerOptions(), isProduction: false, AppContext.BaseDirectory);
        Assert.True(identity.IsDevKey);
        Assert.Equal(ServerIdentity.DevKeyRelativePath, identity.Source);
        Assert.Equal(ServerIdentity.FingerprintOf(DevServerPublicKey.Xml), identity.Fingerprint);
        Assert.Equal(16, identity.Fingerprint.Length);
    }

    [Fact]
    public void AProductionHost_WithTheDevKey_RefusesToStart()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServerIdentity.Load(new ServerOptions(), isProduction: true, AppContext.BaseDirectory));
        Assert.Contains("development server key", error.Message);

        // The same key given as text is still the development key.
        string devXml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ServerIdentity.DevKeyRelativePath));
        Assert.Throws<InvalidOperationException>(() =>
            ServerIdentity.Load(new ServerOptions { PrivateKeyPem = devXml }, isProduction: true, AppContext.BaseDirectory));
    }

    [Fact]
    public void AKeyFromTheOptions_Wins_AndWorksInProduction()
    {
        string xml = NewKeyXml();
        using var identity = ServerIdentity.Load(new ServerOptions { PrivateKeyPem = xml }, isProduction: true, AppContext.BaseDirectory);
        Assert.False(identity.IsDevKey);
        Assert.Equal("Server:PrivateKeyPem", identity.Source);

        using var pub = RSA.Create();
        pub.FromXmlString(xml);
        var sessionKey = new byte[ProtocolLimits.SessionKeyBytes];
        sessionKey[0] = 42;
        byte[] blob = pub.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA1);
        Assert.True(identity.TryDecryptSessionKey(blob, out byte[] decrypted));
        Assert.Equal(sessionKey, decrypted);
    }

    [Fact]
    public void AKeyFile_IsRead_WhenNoTextIsGiven()
    {
        string path = Path.Combine(Path.GetTempPath(), $"projecth-key-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, NewKeyXml());
        try
        {
            using var identity = ServerIdentity.Load(new ServerOptions { PrivateKeyPath = path }, isProduction: true, AppContext.BaseDirectory);
            Assert.False(identity.IsDevKey);
            Assert.Equal(path, identity.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFile_NotAKey_OrAnotherSize_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServerIdentity.Load(new ServerOptions { PrivateKeyPath = "no-such-key.xml" }, false, AppContext.BaseDirectory));
        Assert.Throws<InvalidOperationException>(() =>
            ServerIdentity.Load(new ServerOptions { PrivateKeyPem = "not a key" }, false, AppContext.BaseDirectory));
        Assert.Throws<InvalidOperationException>(() =>
            ServerIdentity.Load(new ServerOptions { PrivateKeyPem = NewKeyXml(1024) }, false, AppContext.BaseDirectory));
        Assert.Throws<InvalidOperationException>(() =>
            ServerIdentity.Load(new ServerOptions(), false, Path.GetTempPath()));   // no dev key there
    }

    // A blob for another key, a garbage blob or a key of another length is a refusal (false), never an exception.
    [Fact]
    public void ABadBlob_IsRefused_WithoutThrowing()
    {
        using var identity = ServerIdentity.Load(new ServerOptions(), false, AppContext.BaseDirectory);
        Assert.False(identity.TryDecryptSessionKey(new byte[ProtocolLimits.RsaBlobBytes], out _));
        using var other = RSA.Create(2048);
        Assert.False(identity.TryDecryptSessionKey(other.Encrypt(new byte[32], RSAEncryptionPadding.OaepSHA1), out _));
        using var dev = RSA.Create();
        dev.FromXmlString(DevServerPublicKey.Xml);
        Assert.False(identity.TryDecryptSessionKey(dev.Encrypt(new byte[16], RSAEncryptionPadding.OaepSHA1), out _));
    }
}
