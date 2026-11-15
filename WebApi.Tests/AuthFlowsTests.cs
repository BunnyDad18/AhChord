using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>
/// ST-82: the /api/auth flows end-to-end — register → login → me → change-password.
/// Duplicate registration is a 409 (pre-check and Identity race mapping), login answers a
/// plain 401 on bad credentials, and change-password rotates the credentials while the
/// issued JWT stays valid (stateless by design — logout is client-side token discarding).
/// Runs in the shared ST-82 collection store: usernames are unique per test.
/// </summary>
[Collection("ST-82 endpoint app")]
public class AuthFlowsTests
{
    private const string Password = "Str0ngPass!";
    private readonly ApiTestFactory _app;

    public AuthFlowsTests(ApiTestFactory app) => _app = app;

    private HttpClient Anonymous => _app.CreateClient();

    // ---------- register ----------

    [Fact]
    public async Task Register_Returns201_TokenAndUser_NoSelfGrantedAdmin()
    {
        var username = ApiTestFactory.UniqueName("james");

        var response = await Anonymous.PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest(username, $"{username}@studio.test", Password, "James")));

        Assert.Equal(201, (int)response.StatusCode);
        var auth = await ApiTestFactory.ReadJsonAsync<AuthResponse>(response);
        Assert.False(string.IsNullOrEmpty(auth.Token));
        Assert.True(auth.ExpiresIn > 0);
        Assert.Equal(username, auth.User.Username);
        Assert.Equal($"{username}@studio.test", auth.User.Email);
        Assert.Equal("James", auth.User.DisplayName);
        Assert.False(auth.User.IsAdmin); // ST-61: registration never self-grants
        Assert.False(string.IsNullOrEmpty(auth.User.Id));
    }

    [Fact]
    public async Task Register_DuplicateUsername_Conflict()
    {
        var username = ApiTestFactory.UniqueName("dupu");
        await _app.RegisterUserAsync(username);

        var response = await Anonymous.PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest(username, "other@studio.test", Password, null)));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(409, status);
        Assert.Equal("username-taken", code);
        Assert.True(fields.ContainsKey("username"));
    }

    [Fact]
    public async Task Register_DuplicateEmail_Conflict_MatchesCaseInsensitively()
    {
        var user = await _app.RegisterUserAsync();

        var response = await Anonymous.PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest(ApiTestFactory.UniqueName("jimmy"),
                user.Email.ToUpperInvariant(), Password, null)));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(409, status);
        Assert.Equal("email-taken", code);
        Assert.True(fields.ContainsKey("email"));
    }

    [Theory]
    [InlineData("ab", "username")]            // too short
    [InlineData("has spaces", "username")]    // illegal characters
    [InlineData("", "username")]              // missing
    [InlineData("not-an-email", "email")]     // bad email
    [InlineData("", "email")]                 // missing email
    public async Task Register_InvalidField_400_WithFieldError(string value, string badField)
    {
        var username = badField == "username" ? value : ApiTestFactory.UniqueName("validuser");
        var email = badField == "email" ? value : $"{username}@studio.test";

        var response = await Anonymous.PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest(username, email, Password, null)));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey(badField));
    }

    [Fact]
    public async Task Register_ShortPassword_400()
    {
        var username = ApiTestFactory.UniqueName("james");

        var response = await Anonymous.PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest(username, $"{username}@studio.test", "short", null)));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("password"));
    }

    [Fact]
    public async Task Register_LongDisplayName_IsClippedTo100()
    {
        var username = ApiTestFactory.UniqueName("james");

        var response = await Anonymous.PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest(username, $"{username}@studio.test",
                Password, new string('x', 150))));

        var auth = await ApiTestFactory.ReadJsonAsync<AuthResponse>(response);
        Assert.Equal(100, auth.User.DisplayName!.Length); // ST-63 bounded display name
    }

    // ---------- login ----------

    [Fact]
    public async Task Login_ByUsername_Returns200_Token()
    {
        var user = await _app.RegisterUserAsync();

        var response = await Anonymous.PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(user.Username, null, Password)));

        Assert.Equal(200, (int)response.StatusCode);
        var auth = await ApiTestFactory.ReadJsonAsync<AuthResponse>(response);
        Assert.Equal(user.Id, auth.User.Id);
        Assert.False(string.IsNullOrEmpty(auth.Token));
    }

    [Fact]
    public async Task Login_ByEmail_Returns200_Token()
    {
        var user = await _app.RegisterUserAsync();

        var response = await Anonymous.PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(null, user.Email, Password)));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(user.Id, (await ApiTestFactory.ReadJsonAsync<AuthResponse>(response)).User.Id);
    }

    [Fact]
    public async Task Login_WrongPassword_Unauthorized()
    {
        var user = await _app.RegisterUserAsync();

        var response = await Anonymous.PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(user.Username, null, "WrongPass1!")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("bad-credentials", code);
    }

    [Fact]
    public async Task Login_UnknownUser_Unauthorized()
    {
        var response = await Anonymous.PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(ApiTestFactory.UniqueName("ghost"), null, Password)));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("bad-credentials", code);
    }

    [Fact]
    public async Task Login_NoIdentity_400()
    {
        var response = await Anonymous.PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(null, null, Password)));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("username"));
    }

    // ---------- me ----------

    [Fact]
    public async Task Me_ReturnsTheCallerProfile()
    {
        var user = await _app.RegisterUserAsync(displayName: "James");

        var response = await user.Client.GetAsync("/api/auth/me");

        Assert.Equal(200, (int)response.StatusCode);
        var dto = await ApiTestFactory.ReadJsonAsync<AuthUserDto>(response);
        Assert.Equal(user.Id, dto.Id);
        Assert.Equal(user.Username, dto.Username);
        Assert.Equal("James", dto.DisplayName);
        Assert.False(dto.IsAdmin);
    }

    [Fact]
    public async Task Me_Anonymous_UnauthorizedChallenge()
    {
        var response = await Anonymous.GetAsync("/api/auth/me");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    [Fact]
    public async Task PatchMe_SetsTrimmedDisplayName()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PatchAsync("/api/auth/me",
            ApiTestFactory.Json(new UpdateMeRequest("  James Taylor  ")));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("James Taylor", (await ApiTestFactory.ReadJsonAsync<AuthUserDto>(response)).DisplayName);
    }

    [Fact]
    public async Task PatchMe_BlankDisplayName_ClearsToNull()
    {
        var user = await _app.RegisterUserAsync(displayName: "James");

        var response = await user.Client.PatchAsync("/api/auth/me",
            ApiTestFactory.Json(new UpdateMeRequest("   ")));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Null((await ApiTestFactory.ReadJsonAsync<AuthUserDto>(response)).DisplayName);
    }

    [Fact]
    public async Task PatchMe_Anonymous_Unauthorized()
    {
        var response = await Anonymous.PatchAsync("/api/auth/me",
            ApiTestFactory.Json(new UpdateMeRequest("Nobody")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- change-password ----------

    [Fact]
    public async Task ChangePassword_RotatesCredentials_OldLoginRejected_NewLoginAccepted()
    {
        var user = await _app.RegisterUserAsync();
        const string newPassword = "N3wSecret!";

        var change = await user.Client.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest(Password, newPassword)));
        Assert.Equal(200, (int)change.StatusCode);

        var old = await Anonymous.PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(user.Username, null, Password)));
        Assert.Equal(401, (int)old.StatusCode);

        var fresh = await Anonymous.PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(user.Username, null, newPassword)));
        Assert.Equal(200, (int)fresh.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_OldTokenStaysValid_StampCheckOffByDefault()
    {
        // The LAN product's default posture (JWT_STAMP_CHECK=false): tokens are not revoked
        // server-side, sign-out is client-side deletion, and a leaked token lives to its expiry
        // (30 days). The security-stamp gate is opt-in because turning it on changes that
        // behaviour on devices people use daily — PublicDemoGateTests covers it ON (a password
        // change revokes the old token, the response carries a replacement for this device) and
        // the forced re-login it implies. SECURITY.md is the place that decides.
        var user = await _app.RegisterUserAsync();

        await user.Client.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest(Password, "N3wSecret!")));

        var response = await user.Client.GetAsync("/api/auth/me");
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_SameAsCurrent_400()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest(Password, Password)));

        var (status, _, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.True(fields.ContainsKey("newPassword"));
    }

    [Fact]
    public async Task ChangePassword_NewTooShort_400()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest(Password, "short")));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("newPassword"));
    }

    [Fact]
    public async Task ChangePassword_WrongCurrent_400_MapsToCurrentPasswordField()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest("WrongPass1!", "N3wSecret!")));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("password-change-failed", code);
        Assert.True(fields.ContainsKey("currentPassword")); // ST-63 PasswordMismatch field mapping
    }

    [Fact]
    public async Task ChangePassword_Anonymous_Unauthorized()
    {
        var response = await Anonymous.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest(Password, "N3wSecret!")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }
}
