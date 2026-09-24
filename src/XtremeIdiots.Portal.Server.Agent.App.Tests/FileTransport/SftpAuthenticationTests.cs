using System.Security.Cryptography;

using Renci.SshNet;

using XtremeIdiots.Portal.Server.Agent.App.FileTransport;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.FileTransport;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.FileTransport;

public class SftpAuthenticationTests
{
    [Fact]
    public void Create_WithPasswordAuthentication_UsesPasswordMethod()
    {
        using var authentication = SftpAuthentication.Create(
            "sftp.example.local",
            22,
            "demo",
            SftpAuthenticationType.Password,
            "secret",
            null,
            null);

        Assert.IsType<PasswordAuthenticationMethod>(
            Assert.Single(authentication.ConnectionInfo.AuthenticationMethods));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_WithPrivateKey_UsesPrivateKeyMethod(bool encrypted)
    {
        const string passphrase = "test-passphrase";
        using var rsa = RSA.Create(2048);
        var privateKey = encrypted
            ? rsa.ExportEncryptedPkcs8PrivateKeyPem(
                passphrase,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1_000))
            : rsa.ExportPkcs8PrivateKeyPem();

        using var authentication = SftpAuthentication.Create(
            "sftp.example.local",
            22,
            "demo",
            SftpAuthenticationType.PrivateKey,
            string.Empty,
            privateKey,
            encrypted ? passphrase : null);

        Assert.IsType<PrivateKeyAuthenticationMethod>(
            Assert.Single(authentication.ConnectionInfo.AuthenticationMethods));
    }

    [Fact]
    public void Create_WithMissingPrivateKey_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SftpAuthentication.Create(
                "sftp.example.local",
                22,
                "demo",
                SftpAuthenticationType.PrivateKey,
                string.Empty,
                null,
                null));

        Assert.Equal("The SFTP private key is required for private-key authentication.", exception.Message);
    }
}
