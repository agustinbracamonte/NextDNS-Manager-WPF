using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NextDNSManager.Shared;

public static class BrokerProtocol
{
    public const int Version = 2;
    public const int MaxMessageBytes = 4096;
    public const string ServiceName = "NextDNSManagerBroker";
    public const string PipeName = "NextDNSManager.Control.v2";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[MaxMessageBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) throw new IOException("El servicio cerró la conexión antes de completar el mensaje.");
            int newline = Array.IndexOf(buffer, (byte)'\n', length, count);
            length += count;
            if (newline < 0) continue;
            if (newline != length - 1) throw new JsonException("Sólo se admite un mensaje por conexión.");
            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, newline), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Se esperaba un objeto JSON.");
            var properties = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (!properties.Add(property.Name)) throw new JsonException("Propiedad duplicada.");
            return document.RootElement.Deserialize<T>(JsonOptions) ?? throw new JsonException("Mensaje vacío.");
        }
        throw new JsonException("El mensaje excede el tamaño permitido.");
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (data.Length + 1 > MaxMessageBytes) throw new JsonException("El mensaje excede el tamaño permitido.");
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}

public sealed class BrokerRequest
{
    public int Version { get; set; } = BrokerProtocol.Version;
    public string Operation { get; set; } = "status";
    public string? ProfileId { get; set; }
    public bool ReportClientInfo { get; set; }
    public bool SetupRouter { get; set; }
    public int? PauseMinutes { get; set; }
}

public sealed class BrokerResponse
{
    public int Version { get; set; } = BrokerProtocol.Version;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool Active { get; set; }
    public string State { get; set; } = "inactive";
    public string? ProfileId { get; set; }
    public bool ReportClientInfo { get; set; }
    public bool SetupRouter { get; set; }
    public DateTimeOffset? PauseUntilUtc { get; set; }
}
