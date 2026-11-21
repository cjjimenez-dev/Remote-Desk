using FluentAssertions;
using RemoteDesk.Core.Security;
using Xunit;

namespace RemoteDesk.Core.Tests.Security;

public class DeviceIdentityTests
{
    [Fact]
    public void DeviceIdentity_GeneratesValidKeyAndNumericId()
    {
        using var identity = new DeviceIdentity();

        identity.DeviceId.Should().NotBeNullOrWhiteSpace();
        identity.DeviceId.Should().MatchRegex(@"^\d{3}-\d{3}$");
        identity.PublicKeyBytes.Should().NotBeEmpty();
        identity.PublicKeyBase64.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DeviceIdentity_SignsAndVerifiesDataCorrectly()
    {
        using var identity = new DeviceIdentity();
        var message = "RemoteDesk-Sign-Test-Payload-12345";

        var signature = identity.Sign(message);
        signature.Should().NotBeEmpty();

        var verified = identity.Verify(message, signature);
        verified.Should().BeTrue();

        var tampered = identity.Verify("Tampered-Payload", signature);
        tampered.Should().BeFalse();
    }

    [Fact]
    public void DeviceIdentity_VerifySignature_StaticMethod_RejectsWrongPublicKey()
    {
        using var alice = new DeviceIdentity();
        using var bob = new DeviceIdentity();

        var payload = "Test authentication message";
        var signature = alice.Sign(payload);

        // Bob's public key should reject Alice's signature
        var bobVerified = DeviceIdentity.VerifySignature(bob.PublicKeyBytes, payload, signature);
        bobVerified.Should().BeFalse();

        // Alice's public key should accept Alice's signature
        var aliceVerified = DeviceIdentity.VerifySignature(alice.PublicKeyBytes, payload, signature);
        aliceVerified.Should().BeTrue();
    }

    [Fact]
    public void DeviceIdentity_PersistsAndReloadsKeyViaStore()
    {
        var store = new InMemoryDeviceKeyStore();

        string initialDeviceId;
        byte[] initialPubKey;

        using (var identity1 = new DeviceIdentity(store))
        {
            initialDeviceId = identity1.DeviceId;
            initialPubKey = identity1.PublicKeyBytes;
        }

        // Create second instance using same key store
        using (var identity2 = new DeviceIdentity(store))
        {
            identity2.DeviceId.Should().Be(initialDeviceId);
            identity2.PublicKeyBytes.Should().Equal(initialPubKey);
        }
    }
}
