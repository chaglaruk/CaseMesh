using System.Security.Cryptography;
using System.Text;
using CaseMesh.Core.Models;

namespace CaseMesh.Qa;

public static class MatterRetrievalIdentity
{
    public static Guid Create(
        TenantId tenantId,
        Guid matterId,
        RetrievalMaterialKind kind,
        Guid canonicalId,
        Guid sourceSpanId)
    {
        if (tenantId.Value == Guid.Empty || matterId == Guid.Empty || canonicalId == Guid.Empty || sourceSpanId == Guid.Empty)
            throw new ArgumentException("Stable retrieval identities require complete typed ownership.");
        var input = Encoding.UTF8.GetBytes(
            $"matter-evidence/v1|{tenantId.Value:D}|{matterId:D}|{(int)kind}|{canonicalId:D}|{sourceSpanId:D}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        // This is a custom SHA-256-derived identifier. Version 8 marks the RFC-layout UUID as application-defined.
        // Guid's byte-span constructor stores the rendered version nibble in byte 7 because Data3 is little-endian.
        digest[7] = (byte)((digest[7] & 0x0F) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest[..16]);
    }
}
