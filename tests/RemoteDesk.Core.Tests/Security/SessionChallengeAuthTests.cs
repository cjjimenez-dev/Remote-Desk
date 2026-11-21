using FluentAssertions;
using RemoteDesk.Core.Security;
using Xunit;

namespace RemoteDesk.Core.Tests.Security;

public class SessionChallengeAuthTests
{
    [Fact]
    public void SessionChallengeAuth_CorrectPassword_AuthenticatesSuccessfully()
    {
        var password = "SuperSecretPassword123!";
        var (salt, storedKey) = SessionChallengeAuth.HashPassword(password);
        var sessionId = "sess-challenge-test-1";

        // Host generates challenge nonce
        var nonce = SessionChallengeAuth.GenerateNonce();

        // Viewer computes response using password and received salt
        var derivedKey = SessionChallengeAuth.DeriveKey(password, salt);
        var response = SessionChallengeAuth.ComputeResponse(derivedKey, nonce, sessionId);

        // Host verifies response
        var verified = SessionChallengeAuth.VerifyResponse(storedKey, nonce, sessionId, response);
        verified.Should().BeTrue();
    }

    [Fact]
    public void SessionChallengeAuth_WrongPassword_RejectsAuthentication()
    {
        var correctPassword = "CorrectPassword123";
        var wrongPassword = "WrongPassword999";

        var (salt, storedKey) = SessionChallengeAuth.HashPassword(correctPassword);
        var sessionId = "sess-challenge-test-2";
        var nonce = SessionChallengeAuth.GenerateNonce();

        // Attacker derives key with wrong password
        var attackerDerivedKey = SessionChallengeAuth.DeriveKey(wrongPassword, salt);
        var attackerResponse = SessionChallengeAuth.ComputeResponse(attackerDerivedKey, nonce, sessionId);

        var verified = SessionChallengeAuth.VerifyResponse(storedKey, nonce, sessionId, attackerResponse);
        verified.Should().BeFalse();
    }

    [Fact]
    public void SessionChallengeAuth_ReplayAttackDifferentSession_RejectsAuthentication()
    {
        var password = "HostPasswordXYZ";
        var (salt, storedKey) = SessionChallengeAuth.HashPassword(password);
        var session1 = "sess-111";
        var session2 = "sess-222";
        var nonce = SessionChallengeAuth.GenerateNonce();

        var derivedKey = SessionChallengeAuth.DeriveKey(password, salt);
        var validResponseForSession1 = SessionChallengeAuth.ComputeResponse(derivedKey, nonce, session1);

        // Attacker tries to replay valid response on session 2
        var replayVerified = SessionChallengeAuth.VerifyResponse(storedKey, nonce, session2, validResponseForSession1);
        replayVerified.Should().BeFalse();
    }
}
