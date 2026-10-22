// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcIceCredentialsTests
{
    private const string ClientUfrag = "abcd";
    private const string ClientPassword = "0123456789abcdefghijKL";

    private static string Repeat(char c, int count) => new(c, count);

    [Test]
    public void TryParseStunUsername_AcceptsV1Username()
    {
        string serverUfrag = WebRtcIceCredentials.V1Prefix + "SomeRandomToken";

        bool parsed = WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:{ClientUfrag}", out WebRtcIceCredentials credentials);

        Assert.That(parsed, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(credentials.Flow, Is.EqualTo(WebRtcDirectIceFlow.V1));
            Assert.That(credentials.ServerUfrag, Is.EqualTo(serverUfrag));
            Assert.That(credentials.ClientUfrag, Is.EqualTo(ClientUfrag));
        });
    }

    [Test]
    public void TryParseStunUsername_AcceptsV2UsernameAndRecoversPassword()
    {
        string serverUfrag = WebRtcIceCredentials.BuildV2Ufrag(ClientPassword);

        bool parsed = WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:{ClientUfrag}", out WebRtcIceCredentials credentials);

        Assert.That(parsed, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(credentials.Flow, Is.EqualTo(WebRtcDirectIceFlow.V2));
            Assert.That(credentials.ServerUfrag, Is.EqualTo($"libp2p+webrtc+v2/{ClientPassword}"));
            Assert.That(credentials.ClientUfrag, Is.EqualTo(ClientUfrag));
        });

        Assert.That(credentials.TryGetClientPassword(out string? recovered), Is.True);
        Assert.That(recovered, Is.EqualTo(ClientPassword));
    }

    [Test]
    public void TrySplitStunUsername_SplitsOnFirstColonOnly()
    {
        string serverUfrag = WebRtcIceCredentials.BuildV2Ufrag(ClientPassword);

        bool split = WebRtcIceCredentials.TrySplitStunUsername($"{serverUfrag}:ab:cd:ef", out string? parsedServer, out string? parsedClient);

        Assert.That(split, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(parsedServer, Is.EqualTo(serverUfrag));
            Assert.That(parsedClient, Is.EqualTo("ab:cd:ef"));
        });
    }

    [Test]
    public void TryParseStunUsername_RejectsExtraColons()
    {
        string serverUfrag = WebRtcIceCredentials.BuildV2Ufrag(ClientPassword);

        Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:ab:cd:ef", out _), Is.False);
    }

    [Test]
    public void TryParseStunUsername_RejectsPrefixHiddenBehindAnotherColon()
    {
        string serverUfrag = WebRtcIceCredentials.BuildV2Ufrag(ClientPassword);

        Assert.That(WebRtcIceCredentials.TryParseStunUsername($"notaprefix:{serverUfrag}:{ClientUfrag}", out _), Is.False);
    }

    [TestCase("")]
    [TestCase(":")]
    [TestCase(":abcd")]
    [TestCase("libp2p+webrtc+v2/0123456789abcdefghijKL:")]
    [TestCase("libp2p+webrtc+v2/0123456789abcdefghijKL")]
    [TestCase(null)]
    public void TryParseStunUsername_RejectsMissingOrEmptyHalves(string? username)
    {
        Assert.That(WebRtcIceCredentials.TryParseStunUsername(username, out _), Is.False);
    }

    [TestCase("abcdefghijkl")]
    [TestCase("libp2p+webrtc+v3/0123456789abcdefghijKL")]
    [TestCase("libp2p+webrtc+/0123456789abcdefghijKL")]
    [TestCase("libp2p+webrtc+V2/0123456789abcdefghijKL")]
    public void TryParseStunUsername_RejectsAbsentOrUnrecognisedPrefix(string serverUfrag)
    {
        Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:{ClientUfrag}", out _), Is.False);
        Assert.That(WebRtcIceCredentials.GetFlow(serverUfrag), Is.EqualTo(WebRtcDirectIceFlow.Unknown));
    }

    [TestCase("xx")]
    [TestCase("0")]
    public void TryParseStunUsername_RejectsPrefixInTheMiddleOfTheUfrag(string padding)
    {
        string serverUfrag = padding + WebRtcIceCredentials.V2Prefix + ClientPassword;

        Assert.Multiple(() =>
        {
            Assert.That(WebRtcIceCredentials.GetFlow(serverUfrag), Is.EqualTo(WebRtcDirectIceFlow.Unknown));
            Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:{ClientUfrag}", out _), Is.False);
        });
    }

    [Test]
    public void GetFlow_AnchorsThePrefixAtTheStart()
    {
        string serverUfrag = WebRtcIceCredentials.V1Prefix + WebRtcIceCredentials.V2Prefix + ClientPassword;

        Assert.That(WebRtcIceCredentials.GetFlow(serverUfrag), Is.EqualTo(WebRtcDirectIceFlow.V1));
    }

    [TestCase(4, true)]
    [TestCase(256, true)]
    [TestCase(3, false)]
    [TestCase(257, false)]
    public void IsIceString_EnforcesUfragLengthBounds(int length, bool expected)
    {
        Assert.That(
            WebRtcIceCredentials.IsIceString(Repeat('a', length), WebRtcIceCredentials.MinUfragLength, WebRtcIceCredentials.MaxUfragLength),
            Is.EqualTo(expected));
    }

    [TestCase(4, true)]
    [TestCase(3, false)]
    public void TryParseStunUsername_EnforcesClientUfragLengthBounds(int length, bool expected)
    {
        string serverUfrag = WebRtcIceCredentials.BuildV2Ufrag(ClientPassword);

        Assert.That(
            WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:{Repeat('a', length)}", out _),
            Is.EqualTo(expected));
    }

    [TestCase(239, true)]
    [TestCase(240, false)]
    public void TryParseStunUsername_EnforcesServerUfragLengthBounds(int passwordLength, bool expected)
    {
        string serverUfrag = WebRtcIceCredentials.V2Prefix + Repeat('a', passwordLength);

        Assert.That(serverUfrag, Has.Length.EqualTo(WebRtcIceCredentials.V2Prefix.Length + passwordLength));
        Assert.That(
            WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:{ClientUfrag}", out _),
            Is.EqualTo(expected));
    }

    [TestCase("ab-d")]
    [TestCase("ab_d")]
    [TestCase("ab=d")]
    [TestCase("ab d")]
    [TestCase("abéd")]
    [TestCase("ab\rd")]
    [TestCase("ab\nd")]
    [TestCase("ab.d")]
    public void IsIceString_RejectsNonIceChars(string value)
    {
        Assert.That(WebRtcIceCredentials.IsIceString(value, WebRtcIceCredentials.MinUfragLength, WebRtcIceCredentials.MaxUfragLength), Is.False);
    }

    [Test]
    public void IsIceString_AcceptsEveryIceChar()
    {
        string allIceChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        Assert.That(WebRtcIceCredentials.IsIceString(allIceChars, WebRtcIceCredentials.MinUfragLength, WebRtcIceCredentials.MaxUfragLength), Is.True);
    }

    [TestCase("ab-d")]
    [TestCase("ab_d")]
    [TestCase("ab=d")]
    [TestCase("ab d")]
    [TestCase("abéd")]
    public void TryParseStunUsername_RejectsNonIceCharsInEitherHalf(string bad)
    {
        string serverUfrag = WebRtcIceCredentials.BuildV2Ufrag(ClientPassword);

        Assert.Multiple(() =>
        {
            Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:{bad}", out _), Is.False);
            Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{WebRtcIceCredentials.V2Prefix}{bad}{ClientPassword}:{ClientUfrag}", out _), Is.False);
        });
    }

    [Test]
    public void TryGetClientPassword_RejectsV1AndShortPasswords()
    {
        Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{WebRtcIceCredentials.V1Prefix}{ClientPassword}:{ClientUfrag}", out WebRtcIceCredentials v1), Is.True);
        Assert.That(v1.TryGetClientPassword(out _), Is.False);

        string shortPassword = Repeat('a', WebRtcIceCredentials.MinPasswordLength - 1);
        Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{WebRtcIceCredentials.V2Prefix}{shortPassword}:{ClientUfrag}", out WebRtcIceCredentials v2), Is.True);
        Assert.That(v2.TryGetClientPassword(out _), Is.False);
    }

    [Test]
    public void BuildV2Ufrag_RejectsPasswordsOutsideTheBounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => WebRtcIceCredentials.BuildV2Ufrag(Repeat('a', WebRtcIceCredentials.MinPasswordLength - 1)), Throws.ArgumentException);
            Assert.That(() => WebRtcIceCredentials.BuildV2Ufrag(Repeat('a', 240)), Throws.ArgumentException);
            Assert.That(() => WebRtcIceCredentials.BuildV2Ufrag("0123456789abcdefghij-L"), Throws.ArgumentException);
            Assert.That(WebRtcIceCredentials.BuildV2Ufrag(Repeat('a', 239)), Has.Length.EqualTo(WebRtcIceCredentials.MaxUfragLength));
        });
    }

    [Test]
    public void SpecConstants_MatchTheSpecLiterally()
    {
        // Every other test reads these from the constants, so a typo in one would be invisible.
        Assert.That(WebRtcIceCredentials.V1Prefix, Is.EqualTo("libp2p+webrtc+v1/"));
        Assert.That(WebRtcIceCredentials.V2Prefix, Is.EqualTo("libp2p+webrtc+v2/"));
        Assert.That(WebRtcIceCredentials.MinUfragLength, Is.EqualTo(4));
        Assert.That(WebRtcIceCredentials.MaxUfragLength, Is.EqualTo(256));
        Assert.That(WebRtcIceCredentials.MinPasswordLength, Is.EqualTo(22));

        // 256 ufrag chars minus the 17 char prefix.
        Assert.That(WebRtcIceCredentials.MaxPasswordLength, Is.EqualTo(239));
    }

    [TestCase(21, false)]
    [TestCase(22, true)]
    [TestCase(239, true)]
    [TestCase(240, false)]
    public void BuildV2Ufrag_EnforcesThePasswordBoundsAsLiterals(int passwordLength, bool expectedValid)
    {
        string password = new('a', passwordLength);

        if (expectedValid)
        {
            string ufrag = WebRtcIceCredentials.BuildV2Ufrag(password);
            Assert.That(ufrag, Is.EqualTo("libp2p+webrtc+v2/" + password));
            Assert.That(ufrag.Length, Is.LessThanOrEqualTo(256));
        }
        else
        {
            Assert.That(() => WebRtcIceCredentials.BuildV2Ufrag(password),
                Throws.InstanceOf<ArgumentException>());
        }
    }

    [Test]
    public void BuildV2Ufrag_RoundTripsThroughTheParserAtTheMaximum()
    {
        string password = new('z', 239);
        string serverUfrag = WebRtcIceCredentials.BuildV2Ufrag(password);

        Assert.That(WebRtcIceCredentials.TryParseStunUsername($"{serverUfrag}:abcd",
            out WebRtcIceCredentials credentials), Is.True);
        Assert.That(credentials.Flow, Is.EqualTo(WebRtcDirectIceFlow.V2));
        Assert.That(credentials.TryGetClientPassword(out string? recovered), Is.True);
        Assert.That(recovered, Is.EqualTo(password));
    }

    [TestCase(":abcd", TestName = "EmptyServerHalf")]
    [TestCase("abcd:", TestName = "EmptyClientHalf")]
    [TestCase("abcd", TestName = "NoSeparator")]
    public void TrySplitStunUsername_RejectsMalformedHalves(string username)
    {
        Assert.That(WebRtcIceCredentials.TrySplitStunUsername(username, out _, out _), Is.False);
    }
}
