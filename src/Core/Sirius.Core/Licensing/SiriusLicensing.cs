namespace Sirius.Core.Licensing;

/// <summary>
/// Sirius' public licence key: it tells a genuine code from any other. Its
/// private half signs codes, with <c>tools/LicenceMaker</c>, and is kept
/// off the repository and out of every package (decision 97).
/// </summary>
public static class SiriusLicensing
{
    /// <summary>ECDSA P-256, as SubjectPublicKeyInfo.</summary>
    public static byte[] PublicKey { get; } = Convert.FromBase64String(PublicKeyText);

    private const string PublicKeyText =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJde4xfsB0BxNFOxl72+aYH1Qx+AicV1/+EVSGFZyvK1HGpDQL3IRWUyL8MaEpuao6VB/lZSsekIeucT3fyB/mw==";
}
