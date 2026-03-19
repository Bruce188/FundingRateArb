using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// P/Invoke wrapper for the Lighter native signer library (lighter-signer-linux-amd64.so).
/// The library is compiled from Go and handles EIP-712 order signing.
/// </summary>
public sealed class LighterSigner : IDisposable
{
    // The shared library is installed by the lighter Python SDK.
    // On Linux it lives under the lighter package's signers/ directory.
    private const string LibraryName = "lighter-signer-linux-amd64";

    // Chain ID for Lighter mainnet = 304, testnet = 300
    private const int MainnetChainId = 304;

    // Order type constants (mirrored from Python SDK SignerClient)
    public const int OrderTypeMarket = 1;
    public const int TimeInForceIoc = 0;  // Immediate or Cancel
    public const int DefaultIocExpiry = 0;
    public const int NilTriggerPrice = 0;
    public const int CrossMarginMode = 0;

    private readonly ILogger _logger;
    private int _apiKeyIndex;
    private long _accountIndex;
    private bool _initialized;
    private bool _disposed;

    // Static flag to ensure DllImportResolver is only set once per process
    private static bool s_resolverSet;
    private static readonly object s_resolverLock = new();

    // ── Native struct matching the Go library's SignedTxResponse ──

    [StructLayout(LayoutKind.Sequential)]
    private struct SignedTxResponse
    {
        public byte TxType;
        public IntPtr TxInfo;    // char*
        public IntPtr TxHash;    // char*
        public IntPtr MessageToSign; // char*
        public IntPtr Err;       // char*
    }

    // ── P/Invoke declarations ──

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateClient(
        byte[] url,          // c_char_p
        byte[] privateKey,   // c_char_p
        int chainId,         // c_int
        int apiKeyIndex,     // c_int
        long accountIndex    // c_longlong
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CheckClient(
        int apiKeyIndex,     // c_int
        long accountIndex    // c_longlong
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern SignedTxResponse SignCreateOrder(
        int marketIndex,          // c_int
        long clientOrderIndex,    // c_longlong
        long baseAmount,          // c_longlong
        int price,                // c_int
        int isAsk,                // c_int (0=buy, 1=sell)
        int orderType,            // c_int
        int timeInForce,          // c_int
        int reduceOnly,           // c_int (0=false, 1=true)
        int triggerPrice,         // c_int
        long orderExpiry,         // c_longlong
        long nonce,               // c_longlong
        int apiKeyIndex,          // c_int
        long accountIndex         // c_longlong
    );

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern SignedTxResponse SignUpdateLeverage(
        int marketIndex,     // c_int
        int fraction,        // c_int (IMF = 10000 / leverage)
        int marginMode,      // c_int
        long nonce,          // c_longlong
        int apiKeyIndex,     // c_int
        long accountIndex    // c_longlong
    );

    public LighterSigner(ILogger logger)
    {
        _logger = logger;

        // Set the native library search path to include the Python package location.
        // SetDllImportResolver can only be called once per assembly, so guard with a static flag.
        lock (s_resolverLock)
        {
            if (!s_resolverSet)
            {
                var signerDir = FindSignerLibraryDirectory();
                if (signerDir is not null)
                {
                    NativeLibrary.SetDllImportResolver(
                        typeof(LighterSigner).Assembly,
                        (name, assembly, searchPath) =>
                        {
                            if (name == LibraryName)
                            {
                                var fullPath = Path.Combine(signerDir, $"{LibraryName}.so");
                                if (NativeLibrary.TryLoad(fullPath, out var handle))
                                    return handle;
                            }
                            return IntPtr.Zero;
                        });
                }
                s_resolverSet = true;
            }
        }
    }

    /// <summary>
    /// Initialize the signer with credentials. Must be called before any signing operations.
    /// </summary>
    public void Initialize(string baseUrl, string signerPrivateKey, int apiKeyIndex, long accountIndex)
    {
        _logger.LogInformation(
            "Initializing Lighter signer for account {AccountIndex}, API key index {ApiKeyIndex}",
            accountIndex, apiKeyIndex);

        var urlBytes = System.Text.Encoding.UTF8.GetBytes(baseUrl + "\0");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(signerPrivateKey + "\0");

        var err = CreateClient(urlBytes, keyBytes, MainnetChainId, apiKeyIndex, accountIndex);

        // Zero out the private key bytes immediately after use
        Array.Clear(keyBytes, 0, keyBytes.Length);

        if (err != IntPtr.Zero)
        {
            string errMsg;
            try
            {
                errMsg = Marshal.PtrToStringAnsi(err) ?? "Unknown error";
            }
            finally
            {
                Marshal.FreeHGlobal(err);
            }
            throw new InvalidOperationException($"Lighter signer CreateClient failed: {errMsg}");
        }

        // Verify the client
        var checkErr = CheckClient(apiKeyIndex, accountIndex);
        if (checkErr != IntPtr.Zero)
        {
            string checkErrMsg;
            try
            {
                checkErrMsg = Marshal.PtrToStringAnsi(checkErr) ?? "Unknown error";
            }
            finally
            {
                Marshal.FreeHGlobal(checkErr);
            }
            throw new InvalidOperationException($"Lighter signer CheckClient failed: {checkErrMsg}");
        }

        // Store for later use (can't change after init due to native library state)
        // These are captured in the closure for the instance
        _apiKeyIndex = apiKeyIndex;
        _accountIndex = accountIndex;
        _initialized = true;

        _logger.LogInformation("Lighter signer initialized successfully");
    }

    /// <summary>
    /// Sign a market order. Returns (txType, txInfoJson, txHash) or throws on error.
    /// </summary>
    public (byte txType, string txInfo, string txHash) SignMarketOrder(
        int marketIndex,
        long clientOrderIndex,
        long baseAmount,
        int price,
        bool isAsk,
        bool reduceOnly,
        long nonce)
    {
        EnsureInitialized();

        var result = SignCreateOrder(
            marketIndex,
            clientOrderIndex,
            baseAmount,
            price,
            isAsk ? 1 : 0,
            OrderTypeMarket,
            TimeInForceIoc,
            reduceOnly ? 1 : 0,
            NilTriggerPrice,
            DefaultIocExpiry,
            nonce,
            _apiKeyIndex,
            _accountIndex);

        return DecodeResult(result);
    }

    /// <summary>
    /// Sign a leverage update. Returns (txType, txInfoJson, txHash) or throws on error.
    /// </summary>
    public (byte txType, string txInfo, string txHash) SignLeverageUpdate(
        int marketIndex,
        int leverage,
        long nonce)
    {
        EnsureInitialized();

        // Convert leverage to initial margin fraction: IMF = 10000 / leverage
        // e.g., 5x leverage -> IMF = 2000 (20%)
        var imf = 10_000 / leverage;

        var result = SignUpdateLeverage(
            marketIndex,
            imf,
            CrossMarginMode,
            nonce,
            _apiKeyIndex,
            _accountIndex);

        return DecodeResult(result);
    }

    private (byte txType, string txInfo, string txHash) DecodeResult(SignedTxResponse result)
    {
        // Marshal all IntPtr strings and free native memory in a finally block.
        // The Go library allocates strings with C.CString (malloc); Marshal.FreeHGlobal
        // calls free() on Linux, which is the correct counterpart.
        string? errMsg = null;
        string txInfo = "";
        string txHash = "";

        try
        {
            if (result.Err != IntPtr.Zero)
                errMsg = Marshal.PtrToStringAnsi(result.Err) ?? "Unknown signing error";

            if (result.TxInfo != IntPtr.Zero)
                txInfo = Marshal.PtrToStringAnsi(result.TxInfo) ?? "";

            if (result.TxHash != IntPtr.Zero)
                txHash = Marshal.PtrToStringAnsi(result.TxHash) ?? "";

            if (result.MessageToSign != IntPtr.Zero)
            {
                // MessageToSign is not used by the caller; marshal and discard to trigger cleanup
                _ = Marshal.PtrToStringAnsi(result.MessageToSign);
            }
        }
        finally
        {
            // Free native memory allocated by Go's C.CString (malloc) for each non-zero pointer
            if (result.Err != IntPtr.Zero)
                Marshal.FreeHGlobal(result.Err);
            if (result.TxInfo != IntPtr.Zero)
                Marshal.FreeHGlobal(result.TxInfo);
            if (result.TxHash != IntPtr.Zero)
                Marshal.FreeHGlobal(result.TxHash);
            if (result.MessageToSign != IntPtr.Zero)
                Marshal.FreeHGlobal(result.MessageToSign);
        }

        if (errMsg is not null)
            throw new InvalidOperationException($"Lighter signing failed: {errMsg}");

        if (string.IsNullOrEmpty(txInfo))
            throw new InvalidOperationException("Lighter signing returned empty txInfo");

        return (result.TxType, txInfo, txHash);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("LighterSigner not initialized. Call Initialize() first.");
    }

    /// <summary>
    /// Locate the lighter-signer .so file from the Python package installation.
    /// </summary>
    private string? FindSignerLibraryDirectory()
    {
        // Prefer the application's own directory first (trusted, non-user-writable in production)
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var appSoPath = Path.Combine(appDir, $"{LibraryName}.so");
        if (File.Exists(appSoPath))
            return appDir;

        // Fallback: common locations for the lighter Python package signer.
        // WARNING: These are user-writable paths. In production, copy the .so to the app directory.
        var candidates = new[]
        {
            // pip install --user
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/lib/python3.12/site-packages/lighter/signers"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/lib/python3.11/site-packages/lighter/signers"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/lib/python3.10/site-packages/lighter/signers"),
            // venv / system
            "/usr/lib/python3/dist-packages/lighter/signers",
            "/usr/local/lib/python3.12/dist-packages/lighter/signers",
        };

        foreach (var dir in candidates)
        {
            var soPath = Path.Combine(dir, $"{LibraryName}.so");
            if (File.Exists(soPath))
            {
                _logger.LogWarning(
                    "Loading lighter-signer native library from user-writable path '{Path}'. " +
                    "For production, copy the library to the application directory to mitigate supply-chain risk.",
                    dir);
                return dir;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The native library is a shared singleton managed by the Go runtime;
        // no per-instance cleanup is needed.
    }
}
