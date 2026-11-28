using System.Numerics;
using System.Security.Cryptography;
using el.Domain.Common;

namespace el.Domain.Clearing;

public sealed record CsvArtifact(ReadOnlyMemory<byte> Content, string Sha256, int Count, BigInteger TotalMinorUnits, CurrencyCode Currency);

public static class ArtifactHash
{
    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
