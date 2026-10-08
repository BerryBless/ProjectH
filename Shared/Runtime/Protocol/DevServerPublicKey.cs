namespace ProjectH.Shared.Protocol
{
    // Review fix B1: the public half of the development server key (Server/src/ProjectH.Server/keys/dev-server-key.xml), in
    // RSA.ToXmlString form (Unity's Mono has no PEM import). The bots and the test client use it when no other public key is
    // given; the Unity client ships the same text as Resources/ServerPublicKey.txt. A production server refuses to start
    // with the development key, so this key only ever reaches development servers. A test pins it to the private file.
    public static class DevServerPublicKey
    {
        public const string Xml = "<RSAKeyValue><Modulus>0q7ATWD+R7eU9l0LH9DO/7DrxtWBVRZLbRoNxtAofWBwyPlQCZ4f4hF4SE1duGKPM5GqXzw7QccjX0pSdMme68vPdTsumtDnzpmAnNzAIQh9qfK6H/QhlBAPdq6o/MQPUFFkv38aUvgsumWSKxNr06JOXhQwHcjdFM9y08gYZ4GfjBdJcFP2F45e0z1c/1aYdY4I+VMH3hBRZvlWqVMsJ+FGoVJyyUf4zD5b/7WbbKADRm68lNbHjggzPf6PDvHQz5UxaL7LapmPnXSbe2ivOnrVodCiHla9/SPFQAiwNQsAJiytYuAc/a0xJ/YxHd98cwX7xtbmEIIwavzahbpihQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
    }
}
