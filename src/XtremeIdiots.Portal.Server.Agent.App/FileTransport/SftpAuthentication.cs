using System.Security.Cryptography;
using System.Text;

using Renci.SshNet;

using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.FileTransport;

namespace XtremeIdiots.Portal.Server.Agent.App.FileTransport;

internal sealed class SftpAuthentication : IDisposable
{
    private readonly AuthenticationMethod _authenticationMethod;

    private SftpAuthentication(ConnectionInfo connectionInfo, AuthenticationMethod authenticationMethod)
    {
        ConnectionInfo = connectionInfo;
        _authenticationMethod = authenticationMethod;
    }

    public ConnectionInfo ConnectionInfo { get; }

    public static SftpAuthentication Create(
        string hostname,
        int port,
        string username,
        SftpAuthenticationType authenticationType,
        string password,
        string? privateKey,
        string? privateKeyPassphrase)
    {
        AuthenticationMethod authenticationMethod = authenticationType switch
        {
            SftpAuthenticationType.Password => new PasswordAuthenticationMethod(username, password),
            SftpAuthenticationType.PrivateKey => CreatePrivateKeyAuthenticationMethod(username, privateKey, privateKeyPassphrase),
            _ => throw new InvalidOperationException($"Unsupported SFTP authentication type '{authenticationType}'."),
        };

        try
        {
            var connectionInfo = new ConnectionInfo(hostname, port, username, authenticationMethod);
            return new SftpAuthentication(connectionInfo, authenticationMethod);
        }
        catch
        {
            authenticationMethod.Dispose();
            throw;
        }
    }

    public void Dispose() => _authenticationMethod.Dispose();

    private static PrivateKeyAuthenticationMethod CreatePrivateKeyAuthenticationMethod(
        string username,
        string? privateKey,
        string? privateKeyPassphrase)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("The SFTP private key is required for private-key authentication.");
        }

        var privateKeyBytes = Encoding.UTF8.GetBytes(privateKey);
        try
        {
            using var privateKeyStream = new MemoryStream(privateKeyBytes, writable: false);
            var privateKeyFile = string.IsNullOrEmpty(privateKeyPassphrase)
                ? new PrivateKeyFile(privateKeyStream)
                : new PrivateKeyFile(privateKeyStream, privateKeyPassphrase);

            try
            {
                return new PrivateKeyAuthenticationMethod(username, privateKeyFile);
            }
            catch
            {
                privateKeyFile.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
    }
}
