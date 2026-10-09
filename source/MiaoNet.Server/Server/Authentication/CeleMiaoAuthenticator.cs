using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using MiaoNet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MiaoNet.Server;

// TODO simplify this
public sealed partial class CeleMiaoAuthenticator : IMiaoAuthenticator
{
    private readonly ILogger<CeleMiaoAuthenticator> logger;
    private readonly JsonSerializerOptions jsonSerializerOptions;
    private readonly HttpClient httpClient;
    private readonly string clientID, clientSecret;

    private readonly SymmetricAlgorithm alg;

    public const string BaseAddress = "https://bbs.celemiao.com";
    public const string EndPointCodeAuth = "oauth/token";
    public const string EndPointAuth = "api/celeste/user?access_token=";

    // OAuth requires this to match the redirect_uri the authorization code was
    // issued for, i.e. the client's ClientRC listener port. The Verðandi dual
    // package runs two MiaoNet modules at once, so this fork's client listens on
    // 38038 while the official alpha client keeps 21472 -- upstream hardcodes
    // 21472 here, which makes the code exchange fail with redirect_uri_mismatch.
    // This must stay in sync with ClientRC.cs in MiaoNet.Client.
    public const string RedirectUri = "http://localhost:38038/auth";

    public CeleMiaoAuthenticator(IOptions<AuthenticationOptions> options, ILogger<CeleMiaoAuthenticator> logger)
        : this(options, logger, new HttpClient())
    {
    }

    internal CeleMiaoAuthenticator(
        IOptions<AuthenticationOptions> options,
        ILogger<CeleMiaoAuthenticator> logger,
        HttpClient httpClient
    )
    {
        var authOptions = options.Value;
        clientID = authOptions.ClientID;
        clientSecret = authOptions.ClientSecret;

        alg = GetAes(Encoding.UTF8.GetBytes(authOptions.EncryptionPassword), "MiaoNetServer.TokenSalt"u8.ToArray());

        this.logger = logger;
        jsonSerializerOptions = new()
        {
            // blame bbs
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        this.httpClient = httpClient;
        string ua = $"MiaoNet.Server/{Connection.Version.ToString(3)}";
        logger.LogInformation(AppEvents.Auth, "Using User-Agent \"{ua}\".", ua);
        httpClient.BaseAddress = new Uri(BaseAddress);
        httpClient.DefaultRequestHeaders.Add("User-Agent", ua);
        // 每次都开新连接: 池里闲置的 keep-alive 连接可能已被 CDN 静默掐掉, 复用它会让请求卡住。
        // 鉴权一次登录只跑一两次, 多一次 TLS 握手(约 200ms)完全划算。
        httpClient.DefaultRequestHeaders.ConnectionClose = true;

        static Aes GetAes(byte[] password, byte[] salt)
        {
            var aes = Aes.Create();
            Span<byte> keyAndIV = stackalloc byte[32 + 16];
            Rfc2898DeriveBytes.Pbkdf2(password, salt, keyAndIV, 1000, HashAlgorithmName.SHA512);
            aes.Key = keyAndIV[0..32].ToArray();
            aes.IV = keyAndIV[32..(32 + 16)].ToArray();
            return aes;
        }
    }

    private static readonly TimeSpan AuthAttemptTimeout = TimeSpan.FromSeconds(8);
    private const int AuthAttempts = 3;

    // 实测到 BBS 的这次 POST 是双峰: 要么 ~1s 返回, 要么连接直接卡死(等多久都不回)——
    // 卡死的那次只能靠超时放弃, 再开一条新连接重试(CDN 会轮到别的边缘 IP)。
    // 没有这层重试时, 一次卡死就等于这次登录失败(表现: 服务端 Handshake timeouted,
    // 客户端 end of stream)。注意 HandshakeTimeout 必须 >= AuthAttempts * AuthAttemptTimeout + 余量。
    private async Task<HttpResponseMessage> PostAuthAsync(object content, CancellationToken token)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= AuthAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptCts.CancelAfter(AuthAttemptTimeout);
            try
            {
                return await httpClient.PostAsJsonAsync(EndPointCodeAuth, content, attemptCts.Token);
            }
            catch (Exception e) when (!token.IsCancellationRequested && IsTransient(e))
            {
                last = e;
                logger.LogWarning(
                    AppEvents.Auth,
                    "Auth request attempt {attempt}/{total} failed ({type}: {msg}), retrying.",
                    attempt, AuthAttempts, e.GetType().Name, e.Message
                );
            }
        }

        throw last!;
    }

    private static bool IsTransient(Exception e)
        => e is TaskCanceledException or HttpRequestException or IOException;

    public async Task<AuthenticationResult> AuthenticateAsync(byte[] data, bool isAuthorize, CancellationToken token)
    {
        try
        {
            if (isAuthorize)
            {
                // data is a utf8 string of code here
                string authCode = Encoding.UTF8.GetString(data);
                var content = new
                {
                    client_id = clientID,
                    client_secret = clientSecret,
                    grant_type = "authorization_code",
                    code = authCode,
                    redirect_uri = RedirectUri
                };
                var res = await PostAuthAsync(content, token);
                res.EnsureSuccessStatusCode();
                DateTime resTime = (res.Headers.Date ?? DateTimeOffset.UtcNow).DateTime;
                var json = await res.Content.ReadAsStringAsync(token);
                (BbsOAuth2TokenResult? tokenResult, BbsAuthErrorResult? errorResult) =
                    DeserializeOrError<BbsOAuth2TokenResult>(json, jsonSerializerOptions);

                if (tokenResult is not null)
                {
                    AuthenticationResult result = await AuthenticateByTokenAsync(tokenResult.AccessToken, token);

                    if (result.Type is AuthenticationResultType.Success)
                    {
                        TokenObject tokenObject = new(
                            tokenResult.AccessToken,
                            tokenResult.RefreshToken,
                            resTime + TimeSpan.FromSeconds(tokenResult.ExpiresIn)
                        );
                        var authData = RefBinarySerialization.Serialize(tokenObject, 80);
                        var encryptedData = alg.EncryptCbc(authData, alg.IV);
                        return new(result.Type, result.PlayerInfo, encryptedData);
                    }
                    else
                    {
                        return result;
                    }
                }
                else if (errorResult is not null)
                {
                    logger.LogWarning(AppEvents.Auth, "BBS-side auth failed with error {err}: {msg}.", errorResult.Error, errorResult.ErrorDescription);
                    return new(AuthenticationResultType.InvalidTokenData, null, null);
                }
                else
                {
                    logger.LogWarning(AppEvents.Auth, "BBS-side returned an empty response.");
                    return new(AuthenticationResultType.InternalServerError, null, null);
                }
            }
            else
            {
                byte[] decryptedData;
                try
                {
                    decryptedData = alg.DecryptCbc(data, alg.IV);
                }
                catch (CryptographicException e)
                {
                    logger.LogWarning(AppEvents.Auth, e, "Failed to decrypt authentication data.");
                    return new AuthenticationResult(AuthenticationResultType.InvalidTokenData);
                }
                TokenObject tokenObject = RefBinarySerialization.Deserialize<TokenObject>(decryptedData);
                // the access token is expired, refresh it
                if (DateTime.UtcNow > tokenObject.ExpiredDateTime)
                {
                    var content = new
                    {
                        client_id = clientID,
                        client_secret = clientSecret,
                        grant_type = "refresh_token",
                        refresh_token = tokenObject.RefreshToken,
                        redirect_uri = RedirectUri
                    };
                    var res = await PostAuthAsync(content, token);
                    res.EnsureSuccessStatusCode();
                    DateTime resTime = (res.Headers.Date ?? DateTimeOffset.UtcNow).DateTime;
                    var json = await res.Content.ReadAsStringAsync(token);
                    (BbsOAuth2RefreshedTokenResult? tokenResult, BbsAuthErrorResult? errorResult) =
                        DeserializeOrError<BbsOAuth2RefreshedTokenResult>(json, jsonSerializerOptions);

                    if (tokenResult is not null)
                    {
                        AuthenticationResult result = await AuthenticateByTokenAsync(tokenResult.AccessToken, token);

                        if (result.Type is AuthenticationResultType.Success)
                        {
                            TokenObject newTokenObject = new(
                                tokenResult.AccessToken,
                                tokenObject.RefreshToken,
                                resTime + TimeSpan.FromSeconds(tokenResult.ExpiresIn)
                            );
                            var authData = RefBinarySerialization.Serialize(newTokenObject);
                            var encryptedData = alg.EncryptCbc(authData, alg.IV);
                            return new(result.Type, result.PlayerInfo, encryptedData);
                        }
                        else
                        {
                            return result;
                        }
                    }
                    else if (errorResult is not null)
                    {
                        logger.LogWarning(AppEvents.Auth, "BBS-side auth failed with error {err}: {msg}.", errorResult.Error, errorResult.ErrorDescription);
                        return new(AuthenticationResultType.InvalidTokenData, null, null);
                    }
                    else
                    {
                        logger.LogWarning(AppEvents.Auth, "BBS-side returned an empty response.");
                        return new(AuthenticationResultType.InternalServerError, null, null);
                    }
                }
                return await AuthenticateByTokenAsync(tokenObject.AccessToken, token);
            }
        }
        catch (Exception e)
        {
            logger.LogError(AppEvents.Auth, e, "Exception occurred while authenticating.");
            return new AuthenticationResult(AuthenticationResultType.InternalServerError);
        }
    }

    private async Task<AuthenticationResult> AuthenticateByTokenAsync(string accessToken, CancellationToken token)
    {
        var res = await httpClient.GetAsync($"{EndPointAuth}{Uri.EscapeDataString(accessToken)}", token);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(token);
        (BbsAuthResult? result, BbsAuthErrorResult? errorResult)
            = DeserializeOrError<BbsAuthResult>(json, jsonSerializerOptions);

        if (result is not null)
        {
            // if they are banned
            if (result.SuspendedUntil is not null && result.SuspendedUntil.Value > DateTime.UtcNow)
            {
                logger.LogInformation(
                    AppEvents.Auth,
                    "{pn}:{id} is suspended due to {reason}, message: {msg}, until {until}.",
                    result.UserName, result.ID,
                    result.SuspendReason, result.SuspendMessage,
                    result.SuspendedUntil
                );
                return new AuthenticationResult(AuthenticationResultType.Suspended, result.SuspendMessage);
            }

            if (result.Color is not { Length: 7 } || !TryParseHexColor(result.Color.AsSpan(1), out Color color))
            {
                color = Color.White;
                logger.LogWarning(AppEvents.Auth, "Failed to parse color \"{hex}\" for player {name}, falling back to white.", result.Color, result.UserName);
            }
            return new AuthenticationResult(
                AuthenticationResultType.Success,
                new PlayerInfo(result.ID, result.UserName, result.Prefix ?? string.Empty, result.AvatarUrl ?? string.Empty, color),
                null
            );
        }
        else if (errorResult is not null)
        {
            logger.LogWarning(AppEvents.Auth, "BBS-side auth failed with error {err}: {msg}.", errorResult.Error, errorResult.ErrorDescription);
            return new(AuthenticationResultType.InvalidTokenData);
        }
        else
        {
            logger.LogWarning(AppEvents.Auth, "BBS-side returned an empty response.");
            return new(AuthenticationResultType.InternalServerError);
        }
    }

    private static (T?, BbsAuthErrorResult?) DeserializeOrError<T>(string json, JsonSerializerOptions options)
        where T : class
    {
        var jsonObject = JsonSerializer.Deserialize<JsonObject>(json, options);
        if (jsonObject is null)
            return (null, null);
        if (!jsonObject.ContainsKey("error"))
        {
            T result = JsonSerializer.Deserialize<T>(jsonObject, options)!;
            return (result, null);
        }
        else
        {
            BbsAuthErrorResult errorResult = JsonSerializer.Deserialize<BbsAuthErrorResult>(jsonObject, options)!;
            return (null, errorResult);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseHexColor(ReadOnlySpan<char> hexSpan, out Color color)
    {
        int r1 = GetHexVal(hexSpan[0]);
        int r2 = GetHexVal(hexSpan[1]);
        int g1 = GetHexVal(hexSpan[2]);
        int g2 = GetHexVal(hexSpan[3]);
        int b1 = GetHexVal(hexSpan[4]);
        int b2 = GetHexVal(hexSpan[5]);

        if ((r1 | r2 | g1 | g2 | b1 | b2) == -1)
        {
            color = default;
            return false;
        }

        byte r = (byte)((r1 << 4) | r2);
        byte g = (byte)((g1 << 4) | g2);
        byte b = (byte)((b1 << 4) | b2);

        color = new Color(r, g, b, 255);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetHexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1
    };
}
