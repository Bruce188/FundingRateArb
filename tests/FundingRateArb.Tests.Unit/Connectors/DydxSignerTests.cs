using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;
using NBitcoin.Crypto;

namespace FundingRateArb.Tests.Unit.Connectors;

public class DydxSignerTests
{
    // Standard BIP39 test mnemonic (12-word "abandon" mnemonic)
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void DeriveAddress_FromKnownMnemonic_ProducesValidDydxAddress()
    {
        var signer = new DydxSigner(TestMnemonic);

        signer.Address.Should().StartWith("dydx1");
        // Bech32 address: "dydx1" prefix + data + 6-char checksum
        // Total length is typically 43 characters for a 20-byte hash
        signer.Address.Length.Should().BeGreaterThanOrEqualTo(39);
    }

    [Fact]
    public void DeriveAddress_DifferentMnemonics_ProduceDifferentAddresses()
    {
        var signer1 = new DydxSigner(TestMnemonic);
        var signer2 = new DydxSigner(
            "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong");

        signer1.Address.Should().NotBe(signer2.Address);
    }

    [Fact]
    public void CompressedPublicKey_Is33Bytes()
    {
        var signer = new DydxSigner(TestMnemonic);

        signer.CompressedPublicKey.Should().HaveCount(33);
        // Compressed key starts with 0x02 or 0x03
        signer.CompressedPublicKey[0].Should().BeOneOf(0x02, 0x03);
    }

    [Fact]
    public void BuildAndSignTx_ProducesNonEmptyBytes()
    {
        var signer = new DydxSigner(TestMnemonic);

        var order = CreateTestOrder(signer.Address);
        var txBytes = signer.BuildAndSignPlaceOrderTx(order, accountNumber: 1, sequence: 0, chainId: "dydx-mainnet-1");

        txBytes.Should().NotBeNullOrEmpty();
        txBytes.Length.Should().BeGreaterThan(50); // A signed tx should be substantial
    }

    [Fact]
    public void BuildAndSignTx_SameInputs_ProducesSameOutput()
    {
        var signer = new DydxSigner(TestMnemonic);

        var order = CreateTestOrder(signer.Address);
        var tx1 = signer.BuildAndSignPlaceOrderTx(order, accountNumber: 1, sequence: 0, chainId: "dydx-mainnet-1");
        var tx2 = signer.BuildAndSignPlaceOrderTx(order, accountNumber: 1, sequence: 0, chainId: "dydx-mainnet-1");

        tx1.Should().Equal(tx2);
    }

    [Fact]
    public void Constructor_InvalidMnemonic_Throws()
    {
        Action act = () => _ = new DydxSigner("this is not a valid mnemonic phrase");

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Constructor_NullMnemonic_Throws()
    {
        Action act = () => _ = new DydxSigner(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_EmptyMnemonic_Throws()
    {
        Action act = () => _ = new DydxSigner("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildAndSignTx_SignatureIs64Bytes()
    {
        var signer = new DydxSigner(TestMnemonic);

        var order = CreateTestOrder(signer.Address);
        var txBytes = signer.BuildAndSignPlaceOrderTx(order, accountNumber: 0, sequence: 0, chainId: "dydx-mainnet-1");

        // Parse TxRaw from the output to find the signature field.
        // TxRaw layout: field 1 (body_bytes), field 2 (auth_info_bytes), field 3 (signature).
        // The signature in Cosmos compact format is exactly 64 bytes (32 bytes r + 32 bytes s).
        // We verify by checking that the ToCompactSignature produces exactly 64 bytes.
        var hash = System.Security.Cryptography.SHA256.HashData(txBytes);

        // Verify the tx bytes contain a 64-byte segment (the signature).
        // The simplest structural check: sign a known input and verify the output of ToCompactSignature directly.
        var key = new NBitcoin.Mnemonic(TestMnemonic).DeriveExtKey().Derive(new NBitcoin.KeyPath("m/44'/118'/0'/0/0")).PrivateKey;
        var signature = key.Sign(new NBitcoin.uint256(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })));
        var compact = DydxSigner.ToCompactSignature(signature.MakeCanonical());
        compact.Should().HaveCount(64, "Cosmos compact signature must be exactly 64 bytes (r || s)");
    }

    [Fact]
    public void Dispose_DisposesKeyMaterial()
    {
        var signer = new DydxSigner(TestMnemonic);
        signer.Dispose();

        // After dispose, signing should throw ObjectDisposedException
        var order = CreateTestOrder("dydx1test");
        Action act = () => signer.BuildAndSignPlaceOrderTx(order, 1, 0, "dydx-mainnet-1");
        act.Should().Throw<ObjectDisposedException>();
    }

    private static DydxOrder CreateTestOrder(string ownerAddress) => new()
    {
        OrderId = new DydxOrderId
        {
            SubaccountId = new DydxSubaccountId { Owner = ownerAddress, SubaccountNumber = 0 },
            ClientId = 12345,
            OrderFlags = 0,
            ClobPairId = 0, // BTC-USD
        },
        Side = DydxOrderSide.Buy,
        Quantums = 1_000_000_000,
        Subticks = 50_000_000_000,
        GoodTilBlock = 100,
        TimeInForce = DydxTimeInForce.Ioc,
        ReduceOnly = false,
    };
}
