using CaseMesh.ObjectStoreProvision;

namespace CaseMesh.Storage.S3.Tests;

public sealed class BucketPolicyPrivacyTests
{
    [Fact]
    public void MissingPrincipalIsDeferredToTheAnonymousCanaryProbe()
    {
        const string policy = """{"Statement":[{"Effect":"Allow","Action":"s3:*","Resource":"*"}]}""";

        Assert.False(BucketPolicyPrivacy.HasPotentiallyPublicAllow(policy));
    }

    [Theory]
    [InlineData("""{"Statement":[{"Effect":"Allow","Principal":"*"}]}""")]
    [InlineData("""{"Statement":[{"Effect":"Allow","Principal":{"AWS":["arn:aws:iam::123:root","*"]}}]}""")]
    [InlineData("""{"Statement":[{"Effect":"Allow","NotPrincipal":{"AWS":"arn:aws:iam::123:root"}}]}""")]
    [InlineData("not-json")]
    public void PotentiallyPublicOrMalformedPoliciesFailClosed(string policy)
    {
        Assert.True(BucketPolicyPrivacy.HasPotentiallyPublicAllow(policy));
    }

    [Fact]
    public void ExplicitPrincipalIsAcceptedForRuntimePrivacyVerification()
    {
        const string policy = """{"Statement":[{"Effect":"Allow","Principal":{"AWS":"arn:aws:iam::123:root"}}]}""";

        Assert.False(BucketPolicyPrivacy.HasPotentiallyPublicAllow(policy));
    }
}
