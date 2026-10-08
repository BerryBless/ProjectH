# Server keys

`dev-server-key.xml` is the **development** RSA-2048 private key (`RSA.ToXmlString(true)`), committed on purpose like the development database password. Its public half is `Shared/Runtime/Protocol/DevServerPublicKey.cs` (`DevServerPublicKey.Xml`); the Unity client ships the same text as `Client/Assets/Resources/ServerPublicKey.txt`, and the bots and the test client use it by default. A test pins the pair.

Review fix B1 (`Docs/Networking.md` "접속 순서", `Docs/Server.md` "설정"):

- The server decrypts each connection's session key with this key (RSA-OAEP-SHA1).
- A server without `Server:PrivateKeyPem` / `Server:PrivateKeyPath` uses this file and logs `Server identity: DEV KEY`.
- **A Production server refuses to start with it.** Give a production key through the environment (`Server__PrivateKeyPem`, the XML text) or a file outside the repository (`Server:PrivateKeyPath`), and ship its public half to the clients (`ServerPublicKey.txt`, bots `--server-public-key <path>`).

Making a key pair (once, outside the repository):

```csharp
using var rsa = System.Security.Cryptography.RSA.Create(2048);
File.WriteAllText("server-key.xml", rsa.ToXmlString(true));     // the server's secret
File.WriteAllText("server-public.xml", rsa.ToXmlString(false)); // to the clients
```
