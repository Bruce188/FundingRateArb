using Google.Protobuf;

namespace FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;

// Hand-written protobuf message classes for dYdX v4 order placement.
// Uses Google.Protobuf.CodedOutputStream for correct varint / length-delimited encoding.
// This avoids pulling in the entire Cosmos proto dependency tree.

public enum DydxOrderSide { Buy = 1, Sell = 2 }

public enum DydxTimeInForce { Unspecified = 0, Ioc = 1, PostOnly = 2, FillOrKill = 3 }

/// <summary>dydxprotocol.subaccounts.SubaccountId</summary>
public sealed class DydxSubaccountId
{
    public string Owner { get; set; } = string.Empty;
    public uint SubaccountNumber { get; set; }

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: string owner
        if (Owner.Length > 0)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteString(Owner);
        }
        // field 2: uint32 subaccount_number
        if (SubaccountNumber != 0)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteUInt32(SubaccountNumber);
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }
}

/// <summary>dydxprotocol.clob.OrderId</summary>
public sealed class DydxOrderId
{
    public DydxSubaccountId SubaccountId { get; set; } = new();
    public uint ClientId { get; set; }
    public uint OrderFlags { get; set; }
    public uint ClobPairId { get; set; }

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: SubaccountId (nested message)
        var subBytes = SubaccountId.ToByteArray();
        if (subBytes.Length > 0)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(subBytes));
        }
        // field 2: uint32 client_id
        if (ClientId != 0)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteUInt32(ClientId);
        }
        // field 3: uint32 order_flags
        if (OrderFlags != 0)
        {
            output.WriteTag(3, WireFormat.WireType.Varint);
            output.WriteUInt32(OrderFlags);
        }
        // field 4: uint32 clob_pair_id
        if (ClobPairId != 0)
        {
            output.WriteTag(4, WireFormat.WireType.Varint);
            output.WriteUInt32(ClobPairId);
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }
}

/// <summary>dydxprotocol.clob.Order</summary>
public sealed class DydxOrder
{
    public DydxOrderId OrderId { get; set; } = new();
    public DydxOrderSide Side { get; set; }
    public ulong Quantums { get; set; }
    public ulong Subticks { get; set; }
    public uint GoodTilBlock { get; set; }
    public DydxTimeInForce TimeInForce { get; set; }
    public bool ReduceOnly { get; set; }

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: OrderId (nested message)
        var idBytes = OrderId.ToByteArray();
        if (idBytes.Length > 0)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(idBytes));
        }
        // field 2: enum Side (varint)
        if (Side != 0)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteEnum((int)Side);
        }
        // field 3: uint64 quantums
        if (Quantums != 0)
        {
            output.WriteTag(3, WireFormat.WireType.Varint);
            output.WriteUInt64(Quantums);
        }
        // field 4: uint64 subticks
        if (Subticks != 0)
        {
            output.WriteTag(4, WireFormat.WireType.Varint);
            output.WriteUInt64(Subticks);
        }
        // field 5: uint32 good_til_block (oneof good_til_oneof, field 5)
        if (GoodTilBlock != 0)
        {
            output.WriteTag(5, WireFormat.WireType.Varint);
            output.WriteUInt32(GoodTilBlock);
        }
        // field 7: enum TimeInForce
        if (TimeInForce != DydxTimeInForce.Unspecified)
        {
            output.WriteTag(7, WireFormat.WireType.Varint);
            output.WriteEnum((int)TimeInForce);
        }
        // field 8: bool reduce_only
        if (ReduceOnly)
        {
            output.WriteTag(8, WireFormat.WireType.Varint);
            output.WriteBool(true);
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }
}

/// <summary>dydxprotocol.clob.MsgPlaceOrder</summary>
public sealed class DydxMsgPlaceOrder
{
    public DydxOrder Order { get; set; } = new();

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: Order (nested message)
        var orderBytes = Order.ToByteArray();
        if (orderBytes.Length > 0)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(orderBytes));
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }
}

// ── Cosmos SDK transaction types ─────────────────────────────────────────────

/// <summary>cosmos.tx.v1beta1.TxBody</summary>
public sealed class CosmosTxBody
{
    public List<(string TypeUrl, byte[] Value)> Messages { get; set; } = [];
    public string Memo { get; set; } = string.Empty;

    public void WriteTo(CodedOutputStream output)
    {
        foreach (var (typeUrl, value) in Messages)
        {
            // Each message is an Any: field 1 = type_url (string), field 2 = value (bytes)
            var anyBytes = EncodeAny(typeUrl, value);
            // TxBody.messages is field 1 (repeated, length-delimited)
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(anyBytes));
        }
        // field 2: string memo
        if (Memo.Length > 0)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString(Memo);
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodeAny(string typeUrl, byte[] value)
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        // field 1: string type_url
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(typeUrl);
        // field 2: bytes value
        if (value.Length > 0)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(value));
        }
        output.Flush();
        return ms.ToArray();
    }
}

/// <summary>cosmos.crypto.secp256k1.PubKey wrapper for AuthInfo encoding.</summary>
public sealed class CosmosSignerInfo
{
    public byte[] PublicKey { get; set; } = [];
    public ulong Sequence { get; set; }

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: Any public_key
        var pubKeyInner = EncodePubKeyBytes(PublicKey);
        var anyBytes = EncodeAny("/cosmos.crypto.secp256k1.PubKey", pubKeyInner);
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(anyBytes));

        // field 2: ModeInfo (single: DIRECT = 1)
        var modeInfoBytes = EncodeModeInfoDirect();
        output.WriteTag(2, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(modeInfoBytes));

        // field 3: uint64 sequence
        if (Sequence != 0)
        {
            output.WriteTag(3, WireFormat.WireType.Varint);
            output.WriteUInt64(Sequence);
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }

    /// <summary>Encodes cosmos.crypto.secp256k1.PubKey message: field 1 = bytes key.</summary>
    private static byte[] EncodePubKeyBytes(byte[] key)
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(key));
        output.Flush();
        return ms.ToArray();
    }

    /// <summary>Encodes ModeInfo { single { mode: SIGN_MODE_DIRECT (1) } }.</summary>
    private static byte[] EncodeModeInfoDirect()
    {
        // Inner: Single { mode = 1 }
        using var singleMs = new MemoryStream();
        var singleOut = new CodedOutputStream(singleMs);
        singleOut.WriteTag(1, WireFormat.WireType.Varint);
        singleOut.WriteEnum(1); // SIGN_MODE_DIRECT
        singleOut.Flush();
        var singleBytes = singleMs.ToArray();

        // Outer: ModeInfo { single = field 1 }
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(singleBytes));
        output.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodeAny(string typeUrl, byte[] value)
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(typeUrl);
        if (value.Length > 0)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(value));
        }
        output.Flush();
        return ms.ToArray();
    }
}

/// <summary>cosmos.tx.v1beta1.AuthInfo</summary>
public sealed class CosmosAuthInfo
{
    public CosmosSignerInfo SignerInfo { get; set; } = new();

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: repeated SignerInfo (length-delimited)
        var signerBytes = SignerInfo.ToByteArray();
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(signerBytes));
        // field 2: Fee — empty (gas_limit = 0, no amounts), omit entirely
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }
}

/// <summary>cosmos.tx.v1beta1.SignDoc</summary>
public sealed class CosmosSignDoc
{
    public byte[] BodyBytes { get; set; } = [];
    public byte[] AuthInfoBytes { get; set; } = [];
    public string ChainId { get; set; } = string.Empty;
    public ulong AccountNumber { get; set; }

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: bytes body_bytes
        if (BodyBytes.Length > 0)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(BodyBytes));
        }
        // field 2: bytes auth_info_bytes
        if (AuthInfoBytes.Length > 0)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(AuthInfoBytes));
        }
        // field 3: string chain_id
        if (ChainId.Length > 0)
        {
            output.WriteTag(3, WireFormat.WireType.LengthDelimited);
            output.WriteString(ChainId);
        }
        // field 4: uint64 account_number
        if (AccountNumber != 0)
        {
            output.WriteTag(4, WireFormat.WireType.Varint);
            output.WriteUInt64(AccountNumber);
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }
}

/// <summary>cosmos.tx.v1beta1.TxRaw</summary>
public sealed class CosmosTxRaw
{
    public byte[] BodyBytes { get; set; } = [];
    public byte[] AuthInfoBytes { get; set; } = [];
    public byte[] Signature { get; set; } = [];

    public void WriteTo(CodedOutputStream output)
    {
        // field 1: bytes body_bytes
        if (BodyBytes.Length > 0)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(BodyBytes));
        }
        // field 2: bytes auth_info_bytes
        if (AuthInfoBytes.Length > 0)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(AuthInfoBytes));
        }
        // field 3: bytes signature (repeated, we have one)
        if (Signature.Length > 0)
        {
            output.WriteTag(3, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(Signature));
        }
    }

    public byte[] ToByteArray()
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteTo(output);
        output.Flush();
        return ms.ToArray();
    }
}
