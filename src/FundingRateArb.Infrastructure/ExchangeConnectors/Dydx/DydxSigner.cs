using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;

namespace FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;

/// <summary>
/// Handles all cryptographic operations for dYdX v4: key derivation from mnemonic,
/// dYdX address generation, Cosmos transaction building, and secp256k1 signing.
/// </summary>
public sealed class DydxSigner : IDisposable
{
    private Key? _privateKey;
    private readonly PubKey _publicKey;
    private readonly ILogger? _logger;

    /// <summary>Bech32-encoded dydx address (e.g. "dydx1abc...").</summary>
    public string Address { get; }

    /// <summary>33-byte compressed secp256k1 public key.</summary>
    public byte[] CompressedPublicKey { get; }

    /// <summary>
    /// Derives a secp256k1 key pair and dYdX address from a BIP39 mnemonic phrase.
    /// Uses Cosmos BIP44 path m/44'/118'/0'/0/0.
    /// </summary>
    public DydxSigner(string mnemonic, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            throw new ArgumentException("Mnemonic cannot be null or empty.", nameof(mnemonic));
        }

        _logger = logger;

        var mnemonicObj = new Mnemonic(mnemonic);
        var masterKey = mnemonicObj.DeriveExtKey();
        // Cosmos coin type 118, standard path
        var derived = masterKey.Derive(new KeyPath("m/44'/118'/0'/0/0"));
        _privateKey = derived.PrivateKey;
        _publicKey = _privateKey.PubKey;
        CompressedPublicKey = _publicKey.ToBytes(); // 33 bytes compressed

        // Null out intermediate key material
        masterKey = null;
        derived = null;

        // Address derivation: SHA256(pubkey) -> RIPEMD160 -> Bech32 with "dydx" HRP
        var sha256Hash = SHA256.HashData(CompressedPublicKey);
        var ripemd160Hash = NBitcoin.Crypto.Hashes.RIPEMD160(sha256Hash, sha256Hash.Length);
        Address = Bech32Encode("dydx", ripemd160Hash);
    }

    /// <summary>
    /// Builds a signed Cosmos transaction containing a MsgPlaceOrder.
    /// Returns the serialized TxRaw bytes ready for broadcast.
    /// </summary>
    public byte[] BuildAndSignPlaceOrderTx(
        DydxOrder order, ulong accountNumber, ulong sequence, string chainId)
    {
        // 1. Serialize MsgPlaceOrder
        var msgPlaceOrder = new DydxMsgPlaceOrder { Order = order };
        var msgBytes = msgPlaceOrder.ToByteArray();

        // 2. Build TxBody
        var txBody = new CosmosTxBody
        {
            Messages = [("/dydxprotocol.clob.MsgPlaceOrder", msgBytes)],
            Memo = ""
        };
        var bodyBytes = txBody.ToByteArray();

        // 3. Build AuthInfo
        var authInfo = new CosmosAuthInfo
        {
            SignerInfo = new CosmosSignerInfo
            {
                PublicKey = CompressedPublicKey,
                Sequence = sequence
            }
        };
        var authInfoBytes = authInfo.ToByteArray();

        // 4. Build SignDoc
        var signDoc = new CosmosSignDoc
        {
            BodyBytes = bodyBytes,
            AuthInfoBytes = authInfoBytes,
            ChainId = chainId,
            AccountNumber = accountNumber
        };
        var signDocBytes = signDoc.ToByteArray();

        // 5. Sign: SHA256 hash of SignDoc, then secp256k1 compact signature (r || s, 64 bytes)
        var hash = SHA256.HashData(signDocBytes);
        ObjectDisposedException.ThrowIf(_privateKey is null, this);
        var signature = _privateKey.Sign(new uint256(hash)).MakeCanonical();
        var sigBytes = ToCompactSignature(signature);

        // 6. Build TxRaw
        var txRaw = new CosmosTxRaw
        {
            BodyBytes = bodyBytes,
            AuthInfoBytes = authInfoBytes,
            Signature = sigBytes
        };
        return txRaw.ToByteArray();
    }

    /// <summary>
    /// Broadcasts a signed transaction to the Cosmos validator RPC.
    /// Returns the transaction hash on success, throws on failure.
    /// </summary>
    public async Task<string> BroadcastTxAsync(
        HttpClient validatorClient, byte[] txRawBytes, CancellationToken ct = default)
    {
        var base64Tx = Convert.ToBase64String(txRawBytes);
        var payload = new { tx_bytes = base64Tx, mode = "BROADCAST_MODE_SYNC" };

        var response = await validatorClient.PostAsJsonAsync(
            "cosmos/tx/v1beta1/txs", payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DydxBroadcastResponse>(
            cancellationToken: ct);

        if (result?.TxResponse is null)
        {
            throw new InvalidOperationException("Empty broadcast response from validator");
        }

        if (result.TxResponse.Code != 0)
        {
            _logger?.LogWarning("dYdX broadcast failed — code {Code}, raw_log: {RawLog}",
                result.TxResponse.Code, result.TxResponse.RawLog);
            throw new InvalidOperationException(
                $"Broadcast failed with code {result.TxResponse.Code}");
        }

        return result.TxResponse.TxHash;
    }

    /// <summary>
    /// Fetches the account number and sequence from the Cosmos auth module.
    /// </summary>
    public async Task<(ulong AccountNumber, ulong Sequence)> GetAccountInfoAsync(
        HttpClient validatorClient, CancellationToken ct = default)
    {
        var response = await validatorClient.GetAsync(
            $"cosmos/auth/v1beta1/accounts/{Address}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);

        // The response wraps the account in a polymorphic "account" field.
        // Parse the inner @type-discriminated account to extract account_number and sequence.
        using var doc = JsonDocument.Parse(json);
        var accountElement = doc.RootElement.GetProperty("account");

        // dYdX uses BaseAccount or may nest it — handle both forms
        var target = accountElement.TryGetProperty("base_account", out var baseAccount)
            ? baseAccount
            : accountElement;

        var accountNumberStr = target.GetProperty("account_number").GetString();
        if (!ulong.TryParse(accountNumberStr, out var accountNumber))
        {
            throw new InvalidOperationException(
                $"Failed to parse account_number from validator response: '{accountNumberStr}'");
        }

        var sequenceStr = target.GetProperty("sequence").GetString();
        if (!ulong.TryParse(sequenceStr, out var sequence))
        {
            throw new InvalidOperationException(
                $"Failed to parse sequence from validator response: '{sequenceStr}'");
        }

        return (accountNumber, sequence);
    }

    /// <summary>
    /// Converts an ECDSA DER signature to the compact 64-byte (r || s) format used by Cosmos.
    /// </summary>
    internal static byte[] ToCompactSignature(ECDSASignature signature)
    {
        var der = signature.ToDER();
        var (r, s) = ParseDerSignature(der);

        var result = new byte[64];
        // Right-align r and s into 32-byte slots
        Buffer.BlockCopy(r, 0, result, 32 - r.Length, r.Length);
        Buffer.BlockCopy(s, 0, result, 64 - s.Length, s.Length);
        return result;
    }

    /// <summary>Parses r and s from a DER-encoded ECDSA signature.</summary>
    private static (byte[] R, byte[] S) ParseDerSignature(byte[] der)
    {
        // DER format: 0x30 [total-len] 0x02 [r-len] [r] 0x02 [s-len] [s]
        int offset = 2; // skip 0x30 and total length
        if (der[offset] != 0x02)
        {
            throw new FormatException("Expected INTEGER tag for r");
        }

        offset++;
        int rLen = der[offset++];
        var r = new byte[rLen];
        Array.Copy(der, offset, r, 0, rLen);
        // Strip leading zero padding (DER adds 0x00 prefix for positive numbers with high bit set)
        if (r.Length > 32 && r[0] == 0)
        {
            r = r[1..];
        }

        offset += rLen;

        if (der[offset] != 0x02)
        {
            throw new FormatException("Expected INTEGER tag for s");
        }

        offset++;
        int sLen = der[offset++];
        var s = new byte[sLen];
        Array.Copy(der, offset, s, 0, sLen);
        if (s.Length > 32 && s[0] == 0)
        {
            s = s[1..];
        }

        return (r, s);
    }

    public void Dispose()
    {
        _privateKey?.Dispose();
        _privateKey = null;
    }

    /// <summary>
    /// Bech32-encodes raw bytes with the given human-readable part (BIP-173).
    /// </summary>
    private static string Bech32Encode(string hrp, byte[] data)
    {
        // Convert 8-bit data to 5-bit groups for Bech32
        var converted = ConvertBits(data, 8, 5, true);
        return Bech32EncodeRaw(hrp, converted);
    }

    private static readonly string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    private static int[] Bech32HrpExpand(string hrp)
    {
        var ret = new int[hrp.Length * 2 + 1];
        for (int i = 0; i < hrp.Length; i++)
        {
            ret[i] = hrp[i] >> 5;
        }
        // separator
        ret[hrp.Length] = 0;
        for (int i = 0; i < hrp.Length; i++)
        {
            ret[hrp.Length + 1 + i] = hrp[i] & 31;
        }

        return ret;
    }

    private static int Bech32Polymod(int[] values)
    {
        uint[] gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ (uint)v;
            for (int i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) == 1)
                {
                    chk ^= gen[i];
                }
            }
        }
        return (int)chk;
    }

    private static int[] Bech32CreateChecksum(string hrp, byte[] data)
    {
        var hrpExpanded = Bech32HrpExpand(hrp);
        var values = new int[hrpExpanded.Length + data.Length + 6];
        Array.Copy(hrpExpanded, values, hrpExpanded.Length);
        for (int i = 0; i < data.Length; i++)
        {
            values[hrpExpanded.Length + i] = data[i];
        }

        var polymod = Bech32Polymod(values) ^ 1;
        var ret = new int[6];
        for (int i = 0; i < 6; i++)
        {
            ret[i] = (polymod >> (5 * (5 - i))) & 31;
        }

        return ret;
    }

    private static string Bech32EncodeRaw(string hrp, byte[] data)
    {
        var checksum = Bech32CreateChecksum(hrp, data);
        var sb = new System.Text.StringBuilder(hrp.Length + 1 + data.Length + 6);
        sb.Append(hrp);
        sb.Append('1'); // separator
        foreach (var b in data)
        {
            sb.Append(Bech32Charset[b]);
        }

        foreach (var c in checksum)
        {
            sb.Append(Bech32Charset[c]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// General-purpose bit converter (BIP-173 specification).
    /// Converts between bit groups (e.g., 8-bit to 5-bit for Bech32).
    /// </summary>
    private static byte[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxV = (1 << toBits) - 1;
        var result = new List<byte>();

        foreach (var b in data)
        {
            acc = (acc << fromBits) | b;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxV));
            }
        }

        if (pad && bits > 0)
        {
            result.Add((byte)((acc << (toBits - bits)) & maxV));
        }

        return result.ToArray();
    }
}
