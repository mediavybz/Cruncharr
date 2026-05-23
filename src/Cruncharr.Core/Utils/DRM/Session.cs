using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using ProtoBuf;

namespace Cruncharr.Core.Utils.DRM;

public class ContentDecryptionModule{
    public byte[] privateKey{ get; set; } = Array.Empty<byte>();
    public byte[] identifierBlob{ get; set; } = Array.Empty<byte>();
}

public class DerivedKeys{
    public byte[] Auth1{ get; set; } = Array.Empty<byte>();
    public byte[] Auth2{ get; set; } = Array.Empty<byte>();
    public byte[] Enc{ get; set; } = Array.Empty<byte>();
}

public class Session{
    public byte[] WIDEVINE_SYSTEM_ID = new byte[]{ 237, 239, 139, 169, 121, 214, 74, 206, 163, 200, 39, 220, 213, 29, 33, 237 };

    private RSA _devicePrivateKey;
    private ClientIdentification _identifierBlob;
    private byte[] _identifier;
    private byte[] _pssh;
    private byte[] _rawLicenseRequest = Array.Empty<byte>();
    private byte[] _sessionKey = Array.Empty<byte>();
    private DerivedKeys _derivedKeys = new();
    private OaepEncoding _decryptEngine;
    public List<ContentKey> ContentKeys { get; set; } = new List<ContentKey>();
    public object? InitData{ get; set; }

    private AsymmetricCipherKeyPair DeviceKeys{ get; set; } = null!;

    public Session(ContentDecryptionModule contentDecryptionModule, byte[] pssh){
        _devicePrivateKey = CreatePrivateKeyFromPem(contentDecryptionModule.privateKey);

        using var reader = new StringReader(Encoding.UTF8.GetString(contentDecryptionModule.privateKey));
        DeviceKeys = (AsymmetricCipherKeyPair)new PemReader(reader).ReadObject()!;

        _identifierBlob = Serializer.Deserialize<ClientIdentification>(new MemoryStream(contentDecryptionModule.identifierBlob));
        _identifier = GenerateIdentifier();
        _pssh = pssh;
        InitData = ParseInitData(pssh);
        _decryptEngine = new OaepEncoding(new RsaEngine());
        _decryptEngine.Init(false, DeviceKeys.Private);
    }

    private RSA CreatePrivateKeyFromPem(byte[] pemKey){
        RSA rsa = RSA.Create();
        string s = Encoding.UTF8.GetString(pemKey);
        rsa.ImportFromPem(s);
        return rsa;
    }

    private byte[] GenerateIdentifier(){
        byte[] randomBytes = RandomNumberGenerator.GetBytes(8);
        string hex = BitConverter.ToString(randomBytes).Replace("-", "").ToLower();
        string identifier = hex + "01" + "00000000000000";
        return Encoding.UTF8.GetBytes(identifier);
    }

    public byte[] GetLicenseRequest(){
        var random = new Random();
        uint keyControlNonceId = (uint)(random.NextDouble() * Math.Pow(2, 31));
        
        object licenseRequest;
        
        if (InitData is WidevineCencHeader){
            licenseRequest = new SignedLicenseRequest{
                Type = SignedLicenseRequest.MessageType.LicenseRequest,
                Msg = new LicenseRequest{
                    Type = LicenseRequest.RequestType.New,
                    KeyControlNonce = keyControlNonceId,
                    ProtocolVersion = ProtocolVersion.Current,
                    RequestTime = uint.Parse((DateTime.Now - DateTime.UnixEpoch).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture).Split('.')[0]),
                    ContentId = new LicenseRequest.ContentIdentification{
                        CencId = new LicenseRequest.ContentIdentification.Cenc{
                            LicenseType = LicenseType.Default,
                            RequestId = _identifier,
                            Pssh = (WidevineCencHeader)InitData
                        }
                    }
                }
            };
        } else{
            licenseRequest = new SignedLicenseRequestRaw{
                Type = SignedLicenseRequestRaw.MessageType.LicenseRequest,
                Msg = new LicenseRequestRaw{
                    Type = LicenseRequestRaw.RequestType.New,
                    KeyControlNonce = keyControlNonceId,
                    ProtocolVersion = ProtocolVersion.Current,
                    RequestTime = uint.Parse((DateTime.Now - DateTime.UnixEpoch).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture).Split('.')[0]),
                    ContentId = new LicenseRequestRaw.ContentIdentification{
                        CencId = new LicenseRequestRaw.ContentIdentification.Cenc{
                            LicenseType = LicenseType.Default,
                            RequestId = _identifier,
                            Pssh = _pssh
                        }
                    }
                }
            };
        }

        dynamic lr = licenseRequest;
        lr.Msg.ClientId = _identifierBlob;

        using (var memoryStream = new MemoryStream()){
            Serializer.Serialize(memoryStream, lr.Msg);
            byte[] data = memoryStream.ToArray();
            _rawLicenseRequest = data;
            lr.Signature = Sign(data);
        }

        byte[] requestBytes;
        using (var memoryStream = new MemoryStream()){
            Serializer.Serialize(memoryStream, licenseRequest);
            requestBytes = memoryStream.ToArray();
        }

        return requestBytes;
    }

    static WidevineCencHeader? ParseInitData(byte[] initData){
        WidevineCencHeader? cencHeader;

        try{
            cencHeader = Serializer.Deserialize<WidevineCencHeader>(new MemoryStream(initData[32..]));
        } catch{
            try{
                PSSHBox psshBox = PSSHBox.FromByteArray(initData);
                cencHeader = Serializer.Deserialize<WidevineCencHeader>(new MemoryStream(psshBox.Data ?? Array.Empty<byte>()));
            } catch{
                return null;
            }
        }

        return cencHeader;
    }

    public byte[] Sign(byte[] data){
        var eng = new PssSigner(new RsaEngine(), new Sha1Digest());
        eng.Init(true, DeviceKeys.Private);
        eng.BlockUpdate(data, 0, data.Length);
        return eng.GenerateSignature();
    }

    public byte[] Decrypt(byte[] data){
        int blockSize = _decryptEngine.GetInputBlockSize();
        List<byte> plainText = new List<byte>();

        for (int chunkPosition = 0; chunkPosition < data.Length; chunkPosition += blockSize){
            int chunkSize = Math.Min(blockSize, data.Length - chunkPosition);
            byte[] decryptedChunk = _decryptEngine.ProcessBlock(data, chunkPosition, chunkSize);
            plainText.AddRange(decryptedChunk);
        }

        return plainText.ToArray();
    }

    public void ProvideLicense(byte[] license){
        SignedLicense signedLicense;
        try{
            signedLicense = Serializer.Deserialize<SignedLicense>(new MemoryStream(license));
        } catch{
            throw new Exception("Unable to parse license");
        }

        try{
            var sessionKey = Decrypt(signedLicense.SessionKey);

            if (sessionKey.Length != 16){
                throw new Exception("Unable to decrypt session key");
            }

            _sessionKey = sessionKey;
        } catch{
            throw new Exception("Unable to decrypt session key");
        }

        _derivedKeys = DeriveKeys(_rawLicenseRequest, _sessionKey);

        byte[] licenseBytes;
        using (var memoryStream = new MemoryStream()){
            Serializer.Serialize(memoryStream, signedLicense.Msg);
            licenseBytes = memoryStream.ToArray();
        }

        byte[] hmacHash = CryptoUtils.GetHMACSHA256Digest(licenseBytes, _derivedKeys.Auth1);

        if (!hmacHash.SequenceEqual(signedLicense.Signature)){
            throw new Exception("License signature mismatch");
        }

        foreach (License.KeyContainer key in signedLicense.Msg.Keys){
            string type = key.Type.ToString();

            if (type == "Signing")
                continue;

            byte[] keyId;
            byte[] encryptedKey = key.Key;
            byte[] iv = key.Iv;
            keyId = key.Id;
            if (keyId == null){
                keyId = Encoding.ASCII.GetBytes(key.Type.ToString());
            }

            byte[] decryptedKey;

            using MemoryStream mstream = new MemoryStream();
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using CryptoStream cryptoStream = new CryptoStream(mstream, aes.CreateDecryptor(_derivedKeys.Enc, iv), CryptoStreamMode.Write);
            cryptoStream.Write(encryptedKey, 0, encryptedKey.Length);
            decryptedKey = mstream.ToArray();

            List<string> permissions = new List<string>();
            if (type == "OperatorSession"){
                foreach (PropertyInfo perm in key._OperatorSessionKeyPermissions.GetType().GetProperties()){
                    if ((uint)perm.GetValue(key._OperatorSessionKeyPermissions)! == 1){
                        permissions.Add(perm.Name);
                    }
                }
            }

            ContentKeys.Add(new ContentKey{
                KeyID = keyId,
                Type = type,
                Bytes = decryptedKey,
                Permissions = permissions
            });
        }
    }

    public static DerivedKeys DeriveKeys(byte[] message, byte[] key){
        byte[] encKeyBase = Encoding.UTF8.GetBytes("ENCRYPTION").Concat(new byte[]{ 0x0, }).Concat(message).Concat(new byte[]{ 0x0, 0x0, 0x0, 0x80 }).ToArray();
        byte[] authKeyBase = Encoding.UTF8.GetBytes("AUTHENTICATION").Concat(new byte[]{ 0x0, }).Concat(message).Concat(new byte[]{ 0x0, 0x0, 0x2, 0x0 }).ToArray();

        byte[] encKey = new byte[]{ 0x01 }.Concat(encKeyBase).ToArray();
        byte[] authKey1 = new byte[]{ 0x01 }.Concat(authKeyBase).ToArray();
        byte[] authKey2 = new byte[]{ 0x02 }.Concat(authKeyBase).ToArray();
        byte[] authKey3 = new byte[]{ 0x03 }.Concat(authKeyBase).ToArray();
        byte[] authKey4 = new byte[]{ 0x04 }.Concat(authKeyBase).ToArray();

        byte[] encCmacKey = CryptoUtils.GetCMACDigest(encKey, key);
        byte[] authCmacKey1 = CryptoUtils.GetCMACDigest(authKey1, key);
        byte[] authCmacKey2 = CryptoUtils.GetCMACDigest(authKey2, key);
        byte[] authCmacKey3 = CryptoUtils.GetCMACDigest(authKey3, key);
        byte[] authCmacKey4 = CryptoUtils.GetCMACDigest(authKey4, key);

        byte[] authCmacCombined1 = authCmacKey1.Concat(authCmacKey2).ToArray();
        byte[] authCmacCombined2 = authCmacKey3.Concat(authCmacKey4).ToArray();

        return new DerivedKeys{
            Auth1 = authCmacCombined1,
            Auth2 = authCmacCombined2,
            Enc = encCmacKey
        };
    }
}
